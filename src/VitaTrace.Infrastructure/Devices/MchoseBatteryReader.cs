using System.Text;
using Microsoft.Win32.SafeHandles;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 迈从（MCHOSE）2.4G 鼠标的 0x11/0x13 报告协议。
///
/// 公开实现（dsh-mchose-battery，WebHID）给出的流程：
/// 1. 向 Report ID 0x11 发送 64 字节命令：前两字节 0xF4、0x55，其余 0xFF；
/// 2. 设备通过 Report ID 0x13 回报；
/// 3. 回报数据逐字节 XOR 0xFF 后，首字节为 0xE2、长度 >= 17；
/// 4. data[4] 是电量百分比，data[3] 非 0 表示充电，data[9..] 是设备名。
///
/// 这是鼠标协议，不假设键盘也使用同一套；键盘/其他型号仍会走能力探测，
/// 读不到就明确标记 Unsupported/ReadFailed。
/// </summary>
public sealed class MchoseBatteryReader : IBatteryReader
{
    private const ushort MchoseVendorId = 0x3837;
    private const byte CommandReportId = 0x11;
    private const byte InputReportId = 0x13;

    private readonly IAppLogger _logger;

    public MchoseBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "mchose-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        return device.VendorId == MchoseVendorId &&
               device.Kind != DeviceKind.Receiver &&
               (device.InterfacePaths.Count > 0 || !string.IsNullOrWhiteSpace(device.DevicePath));
    }

    public Task<BatteryReading> ReadAsync(
        PeripheralDevice device,
        CancellationToken cancellationToken = default)
    {
        var paths = GetPaths(device);
        if (paths.Count == 0)
        {
            return Task.FromResult(BatteryReading.Unsupported("没有可用的 HID 接口路径。"));
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var handle = WindowsHidDeviceEnumerator.OpenHid(path);
            if (handle.IsInvalid || !HidNativeMethods.TryGetCaps(handle, out var caps))
            {
                continue;
            }

            if (TryRead(handle, caps, out var percent, out var charging, out var name, null))
            {
                _logger.Info($"MCHOSE 电量读取成功：{device.DisplayName} = {percent}%");
                return Task.FromResult(BatteryReading.FromPercent(
                    percent,
                    BatterySource.MchoseHid,
                    BatteryConfidence.Medium,
                    string.IsNullOrWhiteSpace(name)
                        ? "MCHOSE 0x11/0x13 报告返回的百分比。"
                        : $"MCHOSE 0x11/0x13 报告返回的百分比（设备名：{name}）。",
                    isCharging: charging));
            }
        }

        return Task.FromResult(
            BatteryReading.ReadFailed("已尝试 MCHOSE 0x11 查询，但没有收到有效的 0x13 电量报告。"));
    }

    /// <summary>诊断入口：记录每个接口的能力与实际收发字节，便于确认真实硬件协议。</summary>
    public Task<string> ProbeAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"设备：{device.DisplayName} / VID_{device.VendorId:X4} PID_{device.ProductId:X4}");

        foreach (var path in GetPaths(device))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sb.AppendLine($"接口：{path}");

            using var handle = WindowsHidDeviceEnumerator.OpenHid(path);
            if (handle.IsInvalid)
            {
                sb.AppendLine("  打开失败");
                continue;
            }

            if (!HidNativeMethods.TryGetCaps(handle, out var caps))
            {
                sb.AppendLine("  读取 Caps 失败");
                continue;
            }

            sb.AppendLine($"  feature={caps.FeatureReportByteLength} input={caps.InputReportByteLength} output={caps.OutputReportByteLength}");

            var ok = TryRead(handle, caps, out var percent, out var charging, out var name, line => sb.AppendLine(line));
            sb.AppendLine(ok
                ? $"  结果：{percent}% charging={charging} name={name}"
                : "  结果：没有有效 MCHOSE 报告");
        }

        return Task.FromResult(sb.ToString());
    }

    private static IReadOnlyList<string> GetPaths(PeripheralDevice device)
    {
        if (device.InterfacePaths.Count > 0)
        {
            return device.InterfacePaths;
        }

        return string.IsNullOrWhiteSpace(device.DevicePath)
            ? Array.Empty<string>()
            : new[] { device.DevicePath! };
    }

    private static bool TryRead(
        SafeFileHandle handle,
        HidNativeMethods.HidpCaps caps,
        out int percent,
        out bool charging,
        out string name,
        Action<string>? trace)
    {
        percent = 0;
        charging = false;
        name = string.Empty;

        var sent = false;

        // 优先 Feature Report（公开的鼠标实现使用这条通路）。
        if (caps.FeatureReportByteLength >= 65)
        {
            var tx = new byte[65];
            Array.Fill(tx, (byte)0xFF);
            tx[0] = CommandReportId;
            tx[1] = 0xF4;
            tx[2] = 0x55;
            trace?.Invoke($"  feature tx={Convert.ToHexString(tx)}");
            sent |= HidNativeMethods.HidD_SetFeature(handle, tx, tx.Length);
        }

        // 某些键盘/接收器把同一命令放在 Output Report 上（32 字节）。
        if (!sent && caps.OutputReportByteLength >= 17)
        {
            var length = Math.Max(17, (int)caps.OutputReportByteLength);
            var tx = new byte[length];
            Array.Fill(tx, (byte)0xFF);
            tx[0] = CommandReportId;
            tx[1] = 0xF4;
            tx[2] = 0x55;
            trace?.Invoke($"  output tx={Convert.ToHexString(tx)}");
            sent = HidNativeMethods.HidD_SetOutputReport(handle, tx, tx.Length);
            trace?.Invoke($"  HidD_SetOutputReport={sent} win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");

            if (!sent)
            {
                sent = HidNativeMethods.WriteFile(handle, tx, tx.Length, out var written, IntPtr.Zero);
                trace?.Invoke($"  WriteFile={sent} written={written} win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
        }

        if (!sent)
        {
            return false;
        }

        var inputLength = Math.Max(17, (int)caps.InputReportByteLength);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            Thread.Sleep(20);

            var rx = new byte[inputLength];
            rx[0] = InputReportId;
            if (!HidNativeMethods.HidD_GetInputReport(handle, rx, rx.Length))
            {
                trace?.Invoke($"  input rx 失败，Win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                continue;
            }

            trace?.Invoke($"  input rx={Convert.ToHexString(rx)}");

            var data = rx[0] == InputReportId ? rx.AsSpan(1) : rx.AsSpan();
            if (data.Length < 17)
            {
                continue;
            }

            // 设备返回的数据整体 XOR 0xFF；先复制再解码，避免修改原始日志。
            var decoded = new byte[data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                decoded[i] = (byte)(data[i] ^ 0xFF);
            }

            if (decoded[0] != 0xE2)
            {
                continue;
            }

            var pct = decoded[4];
            if (pct > 100)
            {
                continue;
            }

            percent = pct;
            charging = decoded[3] != 0;

            var nameBytes = new List<byte>();
            for (var i = 9; i < decoded.Length && decoded[i] != 0; i++)
            {
                nameBytes.Add(decoded[i]);
            }

            name = Encoding.ASCII.GetString(nameBytes.ToArray()).Trim();
            return true;
        }

        return false;
    }
}
