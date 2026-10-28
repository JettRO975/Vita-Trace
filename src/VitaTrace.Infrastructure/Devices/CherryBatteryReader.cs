using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// Cherry 2.4G 键盘：VID 0x046A，PID 0x01AC / 0x00EA，col04 的 64 字节 Output Report。
///
/// 协议来自 2.4g-device-center 的 PROTOCOLS.md（clean-room 重写）：
///   TX: 04 20 00 1A 06 ...
///   RX: 04 20 00 1A 06 00 00 00 &lt;percent&gt; &lt;charging&gt; ...
/// 只有响应前缀完全匹配、电量在 0–100 时才接受；不重试写命令，
/// 避免对键盘固件做未知操作。
/// </summary>
public sealed class CherryBatteryReader : IBatteryReader
{
    private const ushort CherryVendorId = 0x046A;
    private const ushort CherryPid1 = 0x01AC;
    private const ushort CherryPid2 = 0x00EA;
    private const int ReportLength = 64;
    private const int PercentOffset = 8;
    private const int ChargingOffset = 9;

    private readonly IAppLogger _logger;

    public CherryBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "cherry-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        return device.VendorId == CherryVendorId &&
               device.ProductId is CherryPid1 or CherryPid2 &&
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

            if (caps.OutputReportByteLength < ReportLength || caps.InputReportByteLength < 10)
            {
                continue;
            }

            matched = true;
            var request = BuildRequest();
            if (!HidRequestResponse.TrySend(handle, request))
            {
                lastError = $"发送 64 字节查询失败（{HidRequestResponse.LastErrorText()}）";
                continue;
            }

            var response = HidRequestResponse.TryRead(handle, caps.InputReportByteLength, 1200);
            if (response is null)
            {
                lastError = "等待响应超时";
                continue;
            }

            _logger.Info($"Cherry：rx={Convert.ToHexString(response.AsSpan(0, Math.Min(16, response.Length)))}…");
            if (!TryParseResponse(response, out var percent, out var charging, out var reason))
            {
                lastError = reason;
                continue;
            }

            _logger.Info($"Cherry：percent={percent}, charging={charging}");
            return BatteryReading.FromPercent(
                percent,
                BatterySource.CherryHid,
                BatteryConfidence.Low,
                "Cherry 64 字节 Output 协议（实验性；仅公开资料）",
                null,
                charging);
        }

        return matched
            ? BatteryReading.ReadFailed($"Cherry 未取得电量：{lastError}")
            : BatteryReading.Unsupported("Cherry：没有 64 字节 Output / 足够长度的 Input 接口。");
    }

    private static byte[] BuildRequest()
    {
        var request = new byte[ReportLength];
        request[0] = 0x04;
        request[1] = 0x20;
        request[2] = 0x00;
        request[3] = 0x1A;
        request[4] = 0x06;
        return request;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> response, out int percent, out bool charging, out string reason)
    {
        percent = 0;
        charging = false;
        reason = string.Empty;

        if (response.Length <= ChargingOffset)
        {
            reason = $"响应长度不足（{response.Length}）";
            return false;
        }

        ReadOnlySpan<byte> prefix = stackalloc byte[] { 0x04, 0x20, 0x00, 0x1A, 0x06 };
        if (!response.StartsWith(prefix))
        {
            reason = $"响应前缀不匹配（{Convert.ToHexString(response[..5])}）";
            return false;
        }

        var value = response[PercentOffset];
        if (value > 100)
        {
            reason = $"电量越界（{value}）";
            return false;
        }

        percent = value;
        charging = response[ChargingOffset] != 0;
        return true;
    }

    public Task<string> ProbeAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
        => Task.FromResult("Cherry 探针需要真实键盘；按 PID/64 字节 Output 匹配，不主动扫描其他接口。");
}
