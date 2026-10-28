using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// Nordic 54 64 字节查询协议（ATK / VXE 新一代接收器）。
///
/// 帧结构由 2.4g-device-center 的 PROTOCOLS 文档与 Nordic54BatteryReader
/// 源码注释交叉确认：请求 `08 7D 72 02 00 link 07 01`，响应 byte7 为
/// 电量、byte8/9 为毫伏，byte2 非 0 表示外设休眠。
///
/// 没有真机时只能算实验支持；读取失败必须返回 Unsupported/ReadFailed。
/// </summary>
public sealed class Nordic54BatteryReader : IBatteryReader
{
    private static readonly HashSet<ushort> KnownVendorIds = new()
    {
        0x373B,
        0x3554
    };

    private readonly IAppLogger _logger;

    public Nordic54BatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "nordic54-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        return device.VendorId is { } vid &&
               KnownVendorIds.Contains(vid) &&
               device.Connection == ConnectionKind.UsbReceiver &&
               device.Kind is DeviceKind.Mouse or DeviceKind.Unknown &&
               device.InterfacePaths.Count > 0;
    }

    public Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Read(device, cancellationToken), cancellationToken);
    }

    private BatteryReading Read(PeripheralDevice device, CancellationToken cancellationToken)
    {
        if (!CanHandle(device))
        {
            return BatteryReading.Unsupported("Nordic54：当前设备不是 ATK/VXE 2.4G 鼠标。");
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

            if (!HidNativeMethods.TryGetCaps(handle, out var caps) ||
                caps.UsagePage != 0xFF05 ||
                caps.Usage != 0x0002 ||
                caps.InputReportByteLength < ProtocolFrameParser.Nordic54FrameLength ||
                caps.OutputReportByteLength < ProtocolFrameParser.Nordic54FrameLength)
            {
                continue;
            }

            matched = true;
            var request = ProtocolFrameParser.BuildNordic54Request();
            if (!HidRequestResponse.TrySend(handle, request))
            {
                lastError = $"发送失败（{HidRequestResponse.LastErrorText()}）";
                continue;
            }

            var response = HidRequestResponse.TryRead(handle, ProtocolFrameParser.Nordic54FrameLength, 1800);
            if (response is null)
            {
                lastError = "读取超时";
                continue;
            }

            var frame = NormalizeReport(response);
            _logger.Info($"Nordic54 响应：{Convert.ToHexString(frame.AsSpan(0, Math.Min(frame.Length, 16)))}...");
            if (ProtocolFrameParser.TryParseNordic54(frame, out var percent, out var millivolts, out var reason))
            {
                return BatteryReading.FromPercent(
                    percent,
                    BatterySource.CompxHid,
                    BatteryConfidence.Low,
                    "Nordic54：2.4G 状态（实验协议）",
                    millivolts > 0 ? millivolts : null,
                    null);
            }

            lastError = $"解析失败：{reason}";
        }

        return matched
            ? BatteryReading.ReadFailed($"Nordic54 未取得有效帧：{lastError}")
            : BatteryReading.Unsupported("Nordic54：接口指纹不匹配（需要 FF05:0002 / 64 字节）。");
    }

    public Task<string> ProbeAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var lines = new List<string>();
            foreach (var path in device.InterfacePaths)
            {
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
                if (caps.UsagePage != 0xFF05 || caps.Usage != 0x0002 ||
                    caps.InputReportByteLength < ProtocolFrameParser.Nordic54FrameLength ||
                    caps.OutputReportByteLength < ProtocolFrameParser.Nordic54FrameLength)
                {
                    continue;
                }

                using var handle = HidRequestResponse.OpenReadWrite(path);
                if (handle.IsInvalid)
                {
                    lines.Add("  读写打开失败");
                    continue;
                }

                var request = ProtocolFrameParser.BuildNordic54Request();
                var sent = HidRequestResponse.TrySend(handle, request);
                lines.Add($"  TX: {Convert.ToHexString(request.AsSpan(0, 8))} sent={sent}");
                if (!sent)
                {
                    continue;
                }

                var response = HidRequestResponse.TryRead(handle, ProtocolFrameParser.Nordic54FrameLength, 1800);
                if (response is null)
                {
                    lines.Add("  RX 超时");
                    continue;
                }

                var frame = NormalizeReport(response);
                lines.Add($"  RX: {Convert.ToHexString(frame.AsSpan(0, Math.Min(frame.Length, 16)))}...");
                var ok = ProtocolFrameParser.TryParseNordic54(frame, out var pct, out var mv, out var reason);
                lines.Add($"  parse={ok} pct={pct} mV={mv} reason={reason}");
            }

            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);
    }

    private static byte[] NormalizeReport(byte[] data)
    {
        if (data.Length == 0 || data[0] == ProtocolFrameParser.Nordic54ReportId)
        {
            return data;
        }

        var frame = new byte[Math.Max(ProtocolFrameParser.Nordic54FrameLength, data.Length + 1)];
        frame[0] = ProtocolFrameParser.Nordic54ReportId;
        Array.Copy(data, 0, frame, 1, data.Length);
        return frame;
    }
}
