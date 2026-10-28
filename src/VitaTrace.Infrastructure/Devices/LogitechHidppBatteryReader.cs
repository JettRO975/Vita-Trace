using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 罗技 HID++ 2.0 只读电量读取器。
///
/// 协议事实来自 Solaar/LogiOps 的公开说明与 2.4g-device-center PROTOCOLS.md：
/// - 长报告 report ID 0x11（20 字节），短报告 0x10（7 字节）；
/// - request: 11 deviceIndex featureIndex (function&lt;&lt;4 | 0x0A) params...
/// - receiver 的配对设备索引 1..6，直连/接收器自身 0xFF；
/// - 先通过 feature 0x0000/function 0 查 feature index，再读 0x1004/0x1000。
///
/// 这里只发查询，不做配对、改设置或写持久化配置。
/// 没有真机时按协议解析自检；若接收器通道没有响应，返回 Unsupported/ReadFailed。
/// </summary>
public sealed class LogitechHidppBatteryReader : IBatteryReader
{
    private const ushort LogitechVendorId = 0x046D;
    private const byte LongReportId = 0x11;
    private const byte ShortReportId = 0x10;
    private const ushort FeatureRoot = 0x0000;
    private const ushort FeatureUnifiedBattery = 0x1004;
    private const ushort FeatureBatteryStatus = 0x1000;
    private const ushort FeatureBatteryVoltage = 0x1001;
    private const int LongReportLength = 20;
    private const int DeviceIndexReceiver = 0xFF;

    private readonly IAppLogger _logger;

    public LogitechHidppBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "logitech-hidpp";

    public bool CanHandle(PeripheralDevice device)
    {
        return device.VendorId == LogitechVendorId &&
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

            if (!IsHidppCollection(caps))
            {
                continue;
            }

            matched = true;
            foreach (var deviceIndex in EnumerateDeviceIndexes(device))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var unifiedIndex = QueryFeatureIndex(handle, deviceIndex, FeatureUnifiedBattery);
                if (unifiedIndex is > 0)
                {
                    if (TryReadBattery(handle, deviceIndex, (byte)unifiedIndex.Value, FeatureUnifiedBattery, out var percent, out var charging, out var reason))
                    {
                        _logger.Info($"罗技：0x1004 deviceIndex={deviceIndex} feature={unifiedIndex.Value} percent={percent}");
                        return BatteryReading.FromPercent(
                            percent,
                            BatterySource.LogitechHidpp,
                            BatteryConfidence.Medium,
                            $"Logitech HID++ 0x1004（通道 {FormatIndex(deviceIndex)}）",
                            null,
                            charging);
                    }

                    lastError = reason;
                }

                var statusIndex = QueryFeatureIndex(handle, deviceIndex, FeatureBatteryStatus);
                if (statusIndex is > 0)
                {
                    if (TryReadBattery(handle, deviceIndex, (byte)statusIndex.Value, FeatureBatteryStatus, out var percent, out var charging, out var reason))
                    {
                        _logger.Info($"罗技：0x1000 deviceIndex={deviceIndex} feature={statusIndex.Value} percent={percent}");
                        return BatteryReading.FromPercent(
                            percent,
                            BatterySource.LogitechHidpp,
                            BatteryConfidence.Medium,
                            $"Logitech HID++ 0x1000（通道 {FormatIndex(deviceIndex)}）",
                            null,
                            charging);
                    }

                    lastError = reason;
                }

                // 0x1001 只返回电压，不生成百分比；这里只记录能力，不猜测电量。
                var voltageIndex = QueryFeatureIndex(handle, deviceIndex, FeatureBatteryVoltage);
                if (voltageIndex is > 0 && string.IsNullOrEmpty(lastError))
                {
                    lastError = $"通道 {FormatIndex(deviceIndex)} 只支持电压（0x1001）";
                }
            }
        }

        return matched
            ? BatteryReading.ReadFailed($"Logitech HID++ 未取得电量：{lastError}")
            : BatteryReading.Unsupported("Logitech：没有可用的 0x11 长报告厂商接口。");
    }

    private static IEnumerable<byte> EnumerateDeviceIndexes(PeripheralDevice device)
    {
        // 直连设备通常是 0xFF；接收器需要 1..6。接收器本身不先试 0xFF，
        // 避免把接收器的状态误当成鼠标电量。
        if (device.Kind != DeviceKind.Receiver)
        {
            yield return DeviceIndexReceiver;
        }

        for (var i = 1; i <= 6; i++)
        {
            yield return (byte)i;
        }

        if (device.Kind == DeviceKind.Receiver)
        {
            yield return DeviceIndexReceiver;
        }
    }

    private static bool IsHidppCollection(HidNativeMethods.HidpCaps caps)
    {
        return (caps.UsagePage == 0xFF00 || caps.UsagePage == 0xFF43) &&
               caps.InputReportByteLength >= LongReportLength &&
               caps.OutputReportByteLength >= LongReportLength;
    }

    private static int? QueryFeatureIndex(SafeFileHandle handle, byte deviceIndex, ushort featureId)
    {
        var request = new byte[LongReportLength];
        request[0] = LongReportId;
        request[1] = deviceIndex;
        request[2] = 0x00; // feature index 0 = IRoot
        request[3] = 0x00; // function 0 = getFeature
        request[4] = (byte)(featureId >> 8);
        request[5] = (byte)(featureId & 0xFF);

        if (!HidRequestResponse.TrySend(handle, request))
        {
            return null;
        }

        var response = ReadLong(handle, TimeSpan.FromMilliseconds(900));
        if (response is null || response[0] != LongReportId || response[1] != deviceIndex || response[2] != 0x00 || response[3] != 0x00)
        {
            return null;
        }

        var index = response[4];
        return index == 0 ? null : index;
    }

    private static bool TryReadBattery(
        SafeFileHandle handle,
        byte deviceIndex,
        byte featureIndex,
        ushort featureId,
        out int percent,
        out bool charging,
        out string reason)
    {
        percent = 0;
        charging = false;
        reason = string.Empty;

        var request = BuildBatteryRequest(deviceIndex, featureIndex, featureId);
        if (!HidRequestResponse.TrySend(handle, request))
        {
            reason = "发送查询失败";
            return false;
        }

        var response = ReadLong(handle, TimeSpan.FromMilliseconds(900));
        if (response is null)
        {
            reason = "响应超时";
            return false;
        }

        if (response[3] != request[3])
        {
            reason = $"function 回显不匹配（0x{response[3]:X2}）";
            return false;
        }

        return TryParseBatteryResponse(response, deviceIndex, featureIndex, out percent, out charging, out reason);
    }

    internal static byte[] BuildBatteryRequest(byte deviceIndex, byte featureIndex, ushort featureId)
    {
        var request = new byte[LongReportLength];
        request[0] = LongReportId;
        request[1] = deviceIndex;
        request[2] = featureIndex;
        request[3] = featureId == FeatureBatteryStatus ? (byte)0x0A : (byte)0x1A;
        return request;
    }

    internal static bool TryParseBatteryResponse(
        ReadOnlySpan<byte> response,
        byte deviceIndex,
        byte featureIndex,
        out int percent,
        out bool charging,
        out string reason)
    {
        percent = 0;
        charging = false;
        reason = string.Empty;

        if (response.Length < LongReportLength ||
            response[0] != LongReportId ||
            response[1] != deviceIndex ||
            response[2] != featureIndex)
        {
            reason = "响应头/回显不匹配";
            return false;
        }

        var value = response[4];
        if (value > 100)
        {
            reason = $"电量越界（{value}）";
            return false;
        }

        percent = value;
        charging = response[6] == 1;
        return true;
    }

    private static byte[]? ReadLong(SafeFileHandle handle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = HidRequestResponse.TryRead(handle, LongReportLength, 250);
            if (response is null || response.Length == 0)
            {
                continue;
            }

            if (response[0] == LongReportId)
            {
                return response;
            }

            // 短报告可能是电量推送/设备事件，继续等长报告，不把它当查询响应。
            if (response[0] == ShortReportId)
            {
                continue;
            }
        }

        return null;
    }

    private static string FormatIndex(byte index) => index == DeviceIndexReceiver ? "0xFF" : index.ToString();

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

                lines.Add($"{path} usage={caps.UsagePage:X4}/{caps.Usage:X4} in={caps.InputReportByteLength} out={caps.OutputReportByteLength}");
                if (!IsHidppCollection(caps))
                {
                    continue;
                }

                foreach (var deviceIndex in EnumerateDeviceIndexes(device))
                {
                    var f1004 = QueryFeatureIndex(handle, deviceIndex, FeatureUnifiedBattery);
                    var f1000 = QueryFeatureIndex(handle, deviceIndex, FeatureBatteryStatus);
                    var f1001 = QueryFeatureIndex(handle, deviceIndex, FeatureBatteryVoltage);
                    lines.Add($"  index={FormatIndex(deviceIndex)} 1004={f1004?.ToString() ?? "-"} 1000={f1000?.ToString() ?? "-"} 1001={f1001?.ToString() ?? "-"}");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);
    }
}

