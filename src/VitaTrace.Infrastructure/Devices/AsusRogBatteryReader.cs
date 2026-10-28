using System.Linq;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// ASUS ROG 私有 HID 电量读取器（鼠标/键盘）。
///
/// 协议事实来自 2.4g-device-center 的 PROTOCOLS.md 与 libratbag 的公开说明，
/// 这里做 clean-room 重写：
/// - VID 0x0B05，厂商 Usage Page 0xFF01/0xFF00；
/// - 普通设备 64/65 字节 report ID 0；OMNI 接收器 report ID 3，命令字段后移 1 字节；
/// - 鼠标 0x0712：payload[0]=percent（旧型号 raw×25），payload[5] 为充电；
/// - 键盘 0x1201：payload[1]=percent，payload[4]==1 为充电；
/// - 响应回显命令；FF AA 表示接收器在但设备休眠。
///
/// 没有真机时只做只读查询；未知型号/旧型号用产品名判断，无法确认时返回失败/不支持。
/// </summary>
public sealed class AsusRogBatteryReader : IBatteryReader
{
    private const ushort AsusVendorId = 0x0B05;
    private const ushort OmniPid = 0x1ACE;
    private const int ReportLength = 64;
    private const byte MouseCommandLow = 0x12;
    private const byte MouseCommandHigh = 0x07;
    private const byte KeyboardCommandLow = 0x01;
    private const byte KeyboardCommandHigh = 0x12;

    private readonly IAppLogger _logger;

    public AsusRogBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "asus-rog-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        return device.VendorId == AsusVendorId &&
               device.Kind != DeviceKind.Receiver &&
               device.InterfacePaths.Count > 0;
    }

    public Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
        => Task.Run(() => Read(device, cancellationToken), cancellationToken);

    private BatteryReading Read(PeripheralDevice device, CancellationToken cancellationToken)
    {
        var legacy = IsLegacyModel(device.DisplayName);
        var keyboardFirst = device.Kind == DeviceKind.Keyboard;
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

            if (!IsVendorCollection(caps) || caps.OutputReportByteLength < ReportLength || caps.InputReportByteLength < ReportLength)
            {
                continue;
            }

            matched = true;
            var reportIds = device.ProductId == OmniPid ? new byte[] { 3 } : new byte[] { 0, 3 };
            foreach (var reportId in reportIds)
            {
                foreach (var keyboard in new[] { keyboardFirst, !keyboardFirst })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var request = BuildRequest(reportId, keyboard);
                    if (!HidRequestResponse.TrySend(handle, request))
                    {
                        lastError = $"发送 0x{(keyboard ? KeyboardCommandHigh : MouseCommandHigh):X2}" +
                                    $"0x{(keyboard ? KeyboardCommandLow : MouseCommandLow):X2} 失败（{HidRequestResponse.LastErrorText()}）";
                        continue;
                    }

                    var response = HidRequestResponse.TryRead(handle, caps.InputReportByteLength, 1200);
                    if (response is null)
                    {
                        lastError = "等待响应超时";
                        continue;
                    }

                    _logger.Info($"ROG：rx={Convert.ToHexString(response.AsSpan(0, Math.Min(16, response.Length)))}…");
                    if (!TryParseResponse(response, reportId, keyboard, legacy, out var percent, out var charging, out var reason))
                    {
                        lastError = reason;
                        continue;
                    }

                    _logger.Info($"ROG：percent={percent}, charging={charging}, reportId={reportId}, keyboard={keyboard}");
                    return BatteryReading.FromPercent(
                        percent,
                        BatterySource.AsusRogHid,
                        BatteryConfidence.Low,
                        $"ASUS ROG 0x{(keyboard ? "1201" : "0712")}（实验协议，reportId={reportId}）",
                        null,
                        charging);
                }
            }
        }

        return matched
            ? BatteryReading.ReadFailed($"ASUS ROG 未取得电量：{lastError}")
            : BatteryReading.Unsupported("ASUS ROG：没有 0xFF01/0xFF00 的 64 字节厂商接口。");
    }

    private static bool IsVendorCollection(HidNativeMethods.HidpCaps caps)
        => caps.UsagePage is 0xFF01 or 0xFF00;

    private static bool IsLegacyModel(string name)
    {
        string[] keywords =
        {
            "Chakram", "Pugio", "Keris", "Impact II", "Gladius II", "Strix Carry"
        };
        return keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    internal static byte[] BuildRequest(byte reportId, bool keyboard)
    {
        var buffer = new byte[ReportLength];
        buffer[0] = reportId;
        var offset = reportId == 3 ? 2 : 1;
        buffer[offset] = keyboard ? KeyboardCommandLow : MouseCommandLow;
        buffer[offset + 1] = keyboard ? KeyboardCommandHigh : MouseCommandHigh;
        return buffer;
    }

    internal static bool TryParseResponse(
        ReadOnlySpan<byte> response,
        byte reportId,
        bool keyboard,
        bool legacy,
        out int percent,
        out bool charging,
        out string reason)
    {
        percent = 0;
        charging = false;
        reason = string.Empty;

        if (response.Length < ReportLength)
        {
            reason = $"响应长度不足（{response.Length}）";
            return false;
        }

        if (response[0] != reportId)
        {
            reason = $"reportId 不匹配（0x{response[0]:X2}）";
            return false;
        }

        var offset = reportId == 3 ? 2 : 1;
        var low = keyboard ? KeyboardCommandLow : MouseCommandLow;
        var high = keyboard ? KeyboardCommandHigh : MouseCommandHigh;
        if (response[offset] == 0xFF && response[offset + 1] == 0xAA)
        {
            reason = "接收器在但设备休眠（FF AA）";
            return false;
        }

        if (response[offset] != low || response[offset + 1] != high)
        {
            reason = $"命令回显不匹配（0x{response[offset]:X2} 0x{response[offset + 1]:X2}）";
            return false;
        }

        // 去掉 report ID 后 payload 从 [4] 开始；OMNI 整体后移 1 字节。
        var payloadStart = reportId == 3 ? 5 : 4;
        var percentIndex = keyboard ? payloadStart + 1 : payloadStart;
        var chargingIndex = keyboard ? payloadStart + 4 : payloadStart + 5;
        var raw = response[percentIndex];

        if (legacy && raw <= 4)
        {
            raw = (byte)(raw * 25);
        }

        if (raw > 100)
        {
            reason = $"电量越界（{raw}）";
            return false;
        }

        percent = raw;
        charging = keyboard ? response[chargingIndex] == 1 : response[chargingIndex] != 0;
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

                lines.Add($"{path} usage={caps.UsagePage:X4}/{caps.Usage:X4} in={caps.InputReportByteLength} out={caps.OutputReportByteLength}");
                if (!IsVendorCollection(caps) || caps.OutputReportByteLength < ReportLength || caps.InputReportByteLength < ReportLength)
                {
                    continue;
                }

                foreach (var reportId in device.ProductId == OmniPid ? new byte[] { 3 } : new byte[] { 0, 3 })
                {
                    foreach (var keyboard in new[] { true, false })
                    {
                        var request = BuildRequest(reportId, keyboard);
                        if (!HidRequestResponse.TrySend(handle, request))
                        {
                            lines.Add($"  reportId={reportId} keyboard={keyboard} 发送失败");
                            continue;
                        }

                        var response = HidRequestResponse.TryRead(handle, caps.InputReportByteLength, 1000);
                        lines.Add(response is null
                            ? $"  reportId={reportId} keyboard={keyboard} 无响应"
                            : $"  reportId={reportId} keyboard={keyboard} rx={Convert.ToHexString(response.AsSpan(0, Math.Min(16, response.Length)))}…");
                    }
                }
            }

            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);
    }
}
