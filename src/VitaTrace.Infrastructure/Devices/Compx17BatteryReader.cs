using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// ATK / VXE 等使用 CompX 17 字节查询的 2.4G 鼠标。
///
/// 协议事实来源（只读、无复制代码）：
/// - 2.4g-device-center 的 CompX 文档：17 字节 `08 04` 查询，byte6 电量；
/// - mouse_tray 的 `nordic52.py` 注释：byte6 电量、byte7 有线标记、byte8/9 mV；
/// - `0x17` 变体的 byte5 需要 ×2。
///
/// 这是“实验协议”：没有真机时只验证帧解析；设备端不支持时必须返回
/// Unsupported，不能因为 VID 匹配就编一个百分比。
/// </summary>
public sealed class Compx17BatteryReader : IBatteryReader
{
    private static readonly HashSet<ushort> KnownVendorIds = new()
    {
        0x373B, // ATK
        0x3554  // VXE
    };

    private readonly IAppLogger _logger;

    public Compx17BatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "compx17-hid";

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
            return BatteryReading.Unsupported("CompX17：当前设备不是 ATK/VXE 2.4G 鼠标。");
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
                caps.UsagePage != 0xFF02 ||
                caps.Usage != 0x0002 ||
                caps.InputReportByteLength < ProtocolFrameParser.CompxFrameLength ||
                caps.OutputReportByteLength < ProtocolFrameParser.CompxFrameLength)
            {
                continue;
            }

            matched = true;
            foreach (var command in new[]
                     {
                         ProtocolFrameParser.CompxQueryCommand,
                         ProtocolFrameParser.Compx17QueryCommand
                     })
            {
                var request = ProtocolFrameParser.BuildCompx17Request(command);
                if (!HidRequestResponse.TrySend(handle, request))
                {
                    lastError = $"0x{command:X2} 发送失败（{HidRequestResponse.LastErrorText()}）";
                    continue;
                }

                var response = HidRequestResponse.TryRead(
                    handle,
                    Math.Max((int)caps.InputReportByteLength, ProtocolFrameParser.CompxFrameLength),
                    1500);
                if (response is null)
                {
                    lastError = $"0x{command:X2} 读取超时";
                    continue;
                }

                var frame = NormalizeReport(response);
                _logger.Info($"CompX17 0x{command:X2} 响应：{Convert.ToHexString(frame)}");
                if (ProtocolFrameParser.TryParseCompx17(
                        frame,
                        command,
                        out var percent,
                        out var wired,
                        out var millivolts,
                        out var reason))
                {
                    var detail = wired
                        ? "CompX17：当前为有线模式，数值来自鼠标"
                        : "CompX17：2.4G 状态（实验协议）";
                    return BatteryReading.FromPercent(
                        percent,
                        BatterySource.CompxHid,
                        BatteryConfidence.Low,
                        detail,
                        millivolts > 0 ? millivolts : null,
                        null);
                }

                lastError = $"0x{command:X2} 解析失败：{reason}";
            }
        }

        return matched
            ? BatteryReading.ReadFailed($"CompX17 未取得有效帧：{lastError}")
            : BatteryReading.Unsupported("CompX17：接口指纹不匹配（需要 FF02:0002 / 17 字节）。");
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
                if (caps.UsagePage != 0xFF02 || caps.Usage != 0x0002 ||
                    caps.InputReportByteLength < ProtocolFrameParser.CompxFrameLength ||
                    caps.OutputReportByteLength < ProtocolFrameParser.CompxFrameLength)
                {
                    continue;
                }

                using var handle = HidRequestResponse.OpenReadWrite(path);
                if (handle.IsInvalid)
                {
                    lines.Add("  读写打开失败");
                    continue;
                }

                foreach (var command in new[]
                         {
                             ProtocolFrameParser.CompxQueryCommand,
                             ProtocolFrameParser.Compx17QueryCommand
                         })
                {
                    var request = ProtocolFrameParser.BuildCompx17Request(command);
                    var sent = HidRequestResponse.TrySend(handle, request);
                    lines.Add($"  TX 0x{command:X2}: {Convert.ToHexString(request)} sent={sent}");
                    if (!sent)
                    {
                        continue;
                    }

                    var response = HidRequestResponse.TryRead(handle, 17, 1500);
                    if (response is null)
                    {
                        lines.Add("  RX 超时");
                        continue;
                    }

                    var frame = NormalizeReport(response);
                    lines.Add($"  RX: {Convert.ToHexString(frame)}");
                    var ok = ProtocolFrameParser.TryParseCompx17(
                        frame, command, out var pct, out var wired, out var mv, out var reason);
                    lines.Add($"  parse={ok} pct={pct} wired={wired} mV={mv} reason={reason}");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);
    }

    private static byte[] NormalizeReport(byte[] data)
    {
        if (data.Length == 0 || data[0] == ProtocolFrameParser.CompxReportId)
        {
            return data;
        }

        // 某些驱动栈会把 Report ID 去掉；这里补回，解析器只认完整帧。
        var frame = new byte[Math.Max(ProtocolFrameParser.CompxFrameLength, data.Length + 1)];
        frame[0] = ProtocolFrameParser.CompxReportId;
        Array.Copy(data, 0, frame, 1, data.Length);
        return frame;
    }
}
