using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 雷蛇 90 字节 Feature Report 电量读取器。
///
/// 协议事实来自 mouse_tray / 2.4g-device-center 的公开文档，属于 clean-room 重写：
/// - feature report ID 0，总长 91（首字节为 report id，后面 90 字节 payload）；
/// - payload[1] 为 transaction id（已验证型号使用 0x1F）；
/// - payload[5]=0x02（command class），payload[6]=0x07（command id）；
/// - 响应中电量在 buffer[10]（0–255），换算为百分比；
/// - 原始值 0 视为休眠/不可用，不能显示为 0%。
///
/// 没有真机时只能标实验；如果设备休眠、接口不匹配或响应校验失败，
/// 必须返回 ReadFailed/Unsupported，不能生成百分比。
/// </summary>
public sealed class RazerBatteryReader : IBatteryReader
{
    private const ushort RazerVendorId = 0x1532;
    private const int ReportLength = 91;
    private const byte TransactionId = 0x1F;
    private const byte CommandClass = 0x02;
    private const byte CommandId = 0x07;
    private const byte DataSize = 0x80;

    private readonly IAppLogger _logger;

    public RazerBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "razer-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        return device.VendorId == RazerVendorId &&
               device.Connection == ConnectionKind.UsbReceiver &&
               device.InterfacePaths.Count > 0;
    }

    public Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
        => Task.Run(() => Read(device, cancellationToken), cancellationToken);

    private BatteryReading Read(PeripheralDevice device, CancellationToken cancellationToken)
    {
        if (!CanHandle(device))
        {
            return BatteryReading.Unsupported("Razer：设备不是雷蛇 2.4G 外设。");
        }

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

            if (caps.FeatureReportByteLength < ReportLength)
            {
                continue;
            }

            matched = true;
            var request = BuildRequest();
            _logger.Info($"Razer：发送 Feature 查询 {Convert.ToHexString(request.AsSpan(0, 16))}…");

            if (!HidRequestResponse.TrySetFeature(handle, request))
            {
                lastError = $"HidD_SetFeature 失败（{HidRequestResponse.LastErrorText()}）";
                continue;
            }

            // 无线鼠标需要一段稳定时间才准备响应；这是协议事实，不是重试。
            Thread.Sleep(340);
            cancellationToken.ThrowIfCancellationRequested();

            var response = HidRequestResponse.TryGetFeature(handle, request);
            if (response is null || response.Length < ReportLength)
            {
                lastError = "HidD_GetFeature 无响应";
                continue;
            }

            _logger.Info($"Razer：响应 {Convert.ToHexString(response.AsSpan(0, 16))}…");

            if (!TryParseResponse(response, out var percent, out var reason))
            {
                lastError = reason;
                continue;
            }

            _logger.Info($"Razer：percent={percent}");
            return BatteryReading.FromPercent(
                percent,
                BatterySource.RazerHid,
                BatteryConfidence.Low,
                "Razer 90B Feature（实验协议；休眠时 raw=0）",
                null,
                null);
        }

        return matched
            ? BatteryReading.ReadFailed($"Razer 未取得有效电量：{lastError}")
            : BatteryReading.Unsupported("Razer：接口特征不匹配（需要 FeatureReportByteLength≥91）。");
    }

    /// <summary>
    /// 纯解析：便于无硬件自检。输入是包含 report id 的 91 字节响应。
    /// 只接受 status=0x02、class/id 回显正确且 raw 非 0 的帧。
    /// </summary>
    internal static bool TryParseResponse(byte[] response, out int percent, out string reason)
    {
        percent = 0;
        reason = string.Empty;

        if (response.Length < ReportLength)
        {
            reason = $"响应长度不足（{response.Length}<{ReportLength}）";
            return false;
        }

        if (response[1] is 0x00 or 0x01)
        {
            reason = $"设备忙/未就绪（status=0x{response[1]:X2}）";
            return false;
        }

        if (response[6] != CommandClass || response[7] != CommandId)
        {
            reason = $"响应回显不匹配（class=0x{response[6]:X2}, id=0x{response[7]:X2}）";
            return false;
        }

        var raw = response[10];
        if (raw == 0)
        {
            reason = "原始值为 0，设备可能休眠";
            return false;
        }

        percent = (int)Math.Round(raw * 100.0 / 255.0);
        if (percent is < 0 or > 100)
        {
            reason = $"百分比越界（raw={raw}）";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 构造 91 字节 Feature 查询。首字节 0x00 是 report id；payload 90 字节，
    /// 其中 header 8 字节 + 80 字节数据 + XOR + 填充。
    /// </summary>
    internal static byte[] BuildRequest()
    {
        var buffer = new byte[ReportLength];
        buffer[0] = 0x00; // feature report id
        buffer[1] = 0x00; // status
        buffer[2] = TransactionId;
        buffer[3] = 0x00; // remaining
        buffer[4] = 0x00; // remaining
        buffer[5] = 0x00; // protocol type
        buffer[6] = CommandClass;
        buffer[7] = CommandId;
        buffer[8] = DataSize;

        // CRC 覆盖 payload[2..87]，即含 report id 的 buffer[3..88]。
        byte crc = 0;
        for (var i = 3; i <= 88; i++)
        {
            crc ^= buffer[i];
        }

        buffer[89] = crc;
        buffer[90] = 0x00;
        return buffer;
    }

    public Task<string> ProbeAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var lines = new List<string>();
            foreach (var path in device.InterfacePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var readHandle = HidRequestResponse.OpenReadOnly(path);
                if (readHandle.IsInvalid)
                {
                    lines.Add($"{path} 打开失败");
                    continue;
                }

                if (!HidNativeMethods.TryGetCaps(readHandle, out var caps))
                {
                    lines.Add($"{path} 读取 Caps 失败");
                    continue;
                }

                lines.Add($"{path} usage={caps.UsagePage:X4}/{caps.Usage:X4} in={caps.InputReportByteLength} out={caps.OutputReportByteLength} feat={caps.FeatureReportByteLength}");
                if (caps.FeatureReportByteLength < ReportLength)
                {
                    continue;
                }

                using var handle = HidRequestResponse.OpenReadWrite(path);
                if (handle.IsInvalid)
                {
                    lines.Add("  Feature 打开失败");
                    continue;
                }

                var request = BuildRequest();
                if (!HidRequestResponse.TrySetFeature(handle, request))
                {
                    lines.Add($"  HidD_SetFeature 失败 {HidRequestResponse.LastErrorText()}");
                    continue;
                }

                Thread.Sleep(340);
                var response = HidRequestResponse.TryGetFeature(handle, request);
                lines.Add(response is null
                    ? "  Feature 无响应"
                    : $"  Feature 响应 {Convert.ToHexString(response.AsSpan(0, Math.Min(16, response.Length)))}…");
            }

            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);
    }
}
