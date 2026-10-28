using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 迈从（MCHOSE）Realtek 方案鼠标：VID 0x5253，2.4G PID 0x1020 / 有线 0x00B0。
///
/// 协议事实来自 mouse_tray 的 realtek.py（clean-room 重写，不复制代码）：
/// - 厂商集合 Usage Page 0xFF01 / Usage 0x0001；
/// - 鼠标约每 2 秒主动推送 Report ID 0x13、subtype 0x1D；
/// - 电量 = 报文 byte[5] XOR 0xFF；byte[4] XOR 0xFF 为有线标志；
/// - 可用 20 字节 Feature Report [0x11,0xF9,0xFF...] 催一次，但不是必需；
/// - 休眠/不推送时返回读取失败，不能把 0 当电量。
///
/// 目前只对 MCHOSE L7 Pro 有公开逆向依据，其他型号必须逐台验证。
/// </summary>
public sealed class MchoseRealtekBatteryReader : IBatteryReader
{
    private const ushort RealtekVendorId = 0x5253;
    private const ushort WirelessPid = 0x1020;
    private const ushort WiredPid = 0x00B0;
    private const ushort VendorUsagePage = 0xFF01;
    private const ushort VendorUsage = 0x0001;
    private const byte ReportId = 0x13;
    private const byte Subtype = 0x1D;
    private const int BatteryOffset = 5;
    private const int WiredOffset = 4;
    private const int ReadTimeoutMs = 3200;

    private readonly IAppLogger _logger;

    public MchoseRealtekBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "mchose-realtek-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        return device.VendorId == RealtekVendorId &&
               device.ProductId is WirelessPid or WiredPid &&
               device.Kind != DeviceKind.Receiver &&
               device.InterfacePaths.Count > 0;
    }

    public Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
        => Task.Run(() => Read(device, cancellationToken), cancellationToken);

    private BatteryReading Read(PeripheralDevice device, CancellationToken cancellationToken)
    {
        var matched = false;
        var lastError = string.Empty;

        foreach (var path in device.InterfacePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = HidRequestResponse.OpenReadWrite(path);
            if (handle.IsInvalid)
            {
                lastError = $"打开失败（{HidRequestResponse.LastErrorText()}）";
                continue;
            }

            if (!HidNativeMethods.TryGetCaps(handle, out var caps))
            {
                lastError = "读取 Caps 失败";
                continue;
            }

            if (caps.UsagePage != VendorUsagePage || caps.Usage != VendorUsage || caps.InputReportByteLength < 6)
            {
                continue;
            }

            matched = true;
            TryNudge(handle, caps, cancellationToken);

            var deadline = Environment.TickCount64 + ReadTimeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = HidRequestResponse.TryRead(handle, caps.InputReportByteLength, 1100);
                if (frame is null)
                {
                    continue;
                }

                _logger.Info($"MCHOSE Realtek：rx={Convert.ToHexString(frame.AsSpan(0, Math.Min(16, frame.Length)))}…");
                if (!TryParseFrame(frame, out var percent, out var wired, out var reason))
                {
                    lastError = reason;
                    continue;
                }

                var charging = wired && percent < 100;
                _logger.Info($"MCHOSE Realtek：percent={percent}, wired={wired}");
                return BatteryReading.FromPercent(
                    percent,
                    BatterySource.MchoseRealtekHid,
                    BatteryConfidence.Low,
                    wired
                        ? "MCHOSE Realtek 推送帧（有线/充电，仅 L7 Pro 有公开依据）"
                        : "MCHOSE Realtek 推送帧（仅 L7 Pro 有公开依据）",
                    null,
                    charging);
            }

            lastError = "3.2 秒内没有收到 0x1D 推送帧";
        }

        return matched
            ? BatteryReading.ReadFailed($"MCHOSE Realtek 未取得电量：{lastError}")
            : BatteryReading.Unsupported("MCHOSE Realtek：没有 FF01/0001 的厂商输入接口。");
    }

    /// <summary>可选催帧：失败不影响后续等待，不能因此判定设备不支持。</summary>
    private void TryNudge(Microsoft.Win32.SafeHandles.SafeFileHandle handle, HidNativeMethods.HidpCaps caps, CancellationToken cancellationToken)
    {
        if (caps.FeatureReportByteLength < 20)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var tx = new byte[20];
        Array.Fill(tx, (byte)0xFF);
        tx[0] = 0x11;
        tx[1] = 0xF9;
        if (!HidRequestResponse.TrySetFeature(handle, tx))
        {
            _logger.Info($"MCHOSE Realtek：催帧失败（{HidRequestResponse.LastErrorText()}），继续等待推送。");
        }
    }

    internal static bool TryParseFrame(ReadOnlySpan<byte> frame, out int percent, out bool wired, out string reason)
    {
        percent = 0;
        wired = false;
        reason = string.Empty;

        if (frame.Length <= BatteryOffset)
        {
            reason = $"帧长度不足（{frame.Length}）";
            return false;
        }

        if (frame[0] != ReportId || frame[1] != Subtype)
        {
            reason = $"帧头不匹配（0x{frame[0]:X2}/0x{frame[1]:X2}）";
            return false;
        }

        percent = frame[BatteryOffset] ^ 0xFF;
        if (percent is < 0 or > 100)
        {
            reason = $"电量越界（{percent}）";
            return false;
        }

        wired = (frame[WiredOffset] ^ 0xFF) != 0;
        return true;
    }

    public Task<string> ProbeAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var lines = new List<string>();
            foreach (var path in device.InterfacePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var handle = HidRequestResponse.OpenReadWrite(path);
                if (handle.IsInvalid)
                {
                    lines.Add($"{path} 打开失败");
                    continue;
                }

                if (!HidNativeMethods.TryGetCaps(handle, out var caps))
                {
                    lines.Add($"{path} 读取 Caps 失败");
                    continue;
                }

                lines.Add($"{path} usage={caps.UsagePage:X4}/{caps.Usage:X4} in={caps.InputReportByteLength} feat={caps.FeatureReportByteLength}");
                if (caps.UsagePage != VendorUsagePage || caps.Usage != VendorUsage || caps.InputReportByteLength < 6)
                {
                    continue;
                }

                TryNudge(handle, caps, cancellationToken);
                var frame = HidRequestResponse.TryRead(handle, caps.InputReportByteLength, ReadTimeoutMs);
                lines.Add(frame is null
                    ? "  3.2 秒内没有推送帧"
                    : $"  rx={Convert.ToHexString(frame.AsSpan(0, Math.Min(16, frame.Length)))}… valid={TryParseFrame(frame, out var p, out var w, out _)} pct={p} wired={w}");
            }

            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);
    }
}
