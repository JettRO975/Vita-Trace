using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 标准 HID Battery Strength（Usage Page 0x06 / Usage 0x20）兜底读取器。
///
/// 标准只规定“有电量字段”，没有统一规定报告 ID 与字段偏移；实现保守策略：
/// 只接受长度为 1 或 2 的 Feature Report，并只接受唯一的 0–100 数值。
/// 多字段/多报告无法确认时返回 Unsupported，绝不猜一个百分比。
/// 大多数标准 HID 电量走 Input Report，本读取器不主动阻塞读取输入报告；
/// 这类设备交给 Windows PnP / 厂商协议或后续的异步被动读取。
/// </summary>
public sealed class StandardHidBatteryReader : IBatteryReader
{
    private readonly IAppLogger _logger;

    public StandardHidBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "standard-hid-battery";

    public bool CanHandle(PeripheralDevice device)
    {
        return device.InterfacePaths.Count > 0 &&
               device.Kind != DeviceKind.Receiver &&
               device.Connection is ConnectionKind.UsbReceiver or ConnectionKind.Bluetooth;
    }

    public Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
        => Task.Run(() => Read(device, cancellationToken), cancellationToken);

    private BatteryReading Read(PeripheralDevice device, CancellationToken cancellationToken)
    {
        if (!CanHandle(device))
        {
            return BatteryReading.Unsupported("标准 HID：当前设备不适用。");
        }

        var matched = false;
        var ambiguous = false;
        var lastError = string.Empty;

        foreach (var path in device.InterfacePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = HidRequestResponse.OpenReadOnly(path);
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

            if (caps.UsagePage != 0x06 || caps.Usage != 0x20)
            {
                continue;
            }

            matched = true;
            var length = caps.FeatureReportByteLength;
            if (length is < 1 or > 2)
            {
                ambiguous = true;
                lastError = $"FeatureReportByteLength={length}，字段不唯一";
                continue;
            }

            for (var reportId = 0; reportId <= 0x0F; reportId++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buffer = new byte[length];
                buffer[0] = (byte)reportId;
                if (!HidNativeMethods.HidD_GetFeature(handle, buffer, buffer.Length))
                {
                    continue;
                }

                var value = length == 1 ? buffer[0] : buffer[1];
                if (value is < 0 or > 100)
                {
                    lastError = $"reportId=0x{reportId:X2} 数值越界（{value}）";
                    continue;
                }

                _logger.Info($"标准 HID：reportId=0x{reportId:X2}, battery={value}");
                return BatteryReading.FromPercent(
                    value,
                    BatterySource.StandardHid,
                    BatteryConfidence.Low,
                    "标准 HID Battery Strength（实验兜底）",
                    null,
                    null);
            }
        }

        return matched
            ? BatteryReading.Unsupported(
                ambiguous
                    ? "标准 HID 电量通道存在，但字段不唯一，暂不猜测。"
                    : $"标准 HID 电量通道存在，但读取失败：{lastError}")
            : BatteryReading.Unsupported("标准 HID：没有 0x06/0x20 Battery Strength 接口。");
    }

    public Task<string> ProbeAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var lines = new List<string>();
            foreach (var path in device.InterfacePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var handle = HidRequestResponse.OpenReadOnly(path);
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
                if (caps.UsagePage != 0x06 || caps.Usage != 0x20)
                {
                    continue;
                }

                var length = caps.FeatureReportByteLength;
                if (length is < 1 or > 2)
                {
                    lines.Add($"  Feature 长度={length}，跳过（无法确认字段）");
                    continue;
                }

                for (var reportId = 0; reportId <= 0x0F; reportId++)
                {
                    var buffer = new byte[length];
                    buffer[0] = (byte)reportId;
                    if (HidNativeMethods.HidD_GetFeature(handle, buffer, buffer.Length))
                    {
                        lines.Add($"  reportId=0x{reportId:X2} → {Convert.ToHexString(buffer)}");
                    }
                }
            }

            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);
    }
}
