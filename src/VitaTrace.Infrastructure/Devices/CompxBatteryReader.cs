using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// CompX / PixArt 系 2.4G 设备的 Feature Report 电量查询（只读查询，不写配置）。
///
/// 协议依据：设备在厂商 Feature Report 上接受 0x83 查询命令，返回
/// A1 00 02 02 00 83 [charging] [percent] 帧。不同固件可能要求 65 字节
/// （含 Report ID）或 64 字节，所以两种长度都尝试。
///
/// 重要边界：
/// 1. 这是厂商私有协议，不是标准 HID Battery Strength；只对 VID 白名单尝试；
/// 2. 响应必须同时满足帧头、chg∈{0,1}、percent∈1..100 才接受；
/// 3. 失败返回 ReadFailed/Unsupported，绝不拿缓存或默认值冒充电量；
/// 4. 协议事实来自公开社区实现，代码为独立实现，未复制 GPL 源码。
/// </summary>
public sealed class CompxBatteryReader : IBatteryReader
{
    // 已知使用 CompX / PixArt 64 字节 Feature 方案的厂商。
    // 只用于“是否值得尝试”，不代表这些 VID 的每一款设备都一定支持。
    private static readonly HashSet<ushort> KnownVendorIds = new()
    {
        0x3837, // MCHOSE 迈从
        0x3151, // MicLink / YICHIP OEM（部分迈从、Attack Shark）
        0x3554, // VGN / VXE / Darmoshark 等
        0x373B, // ATK / VXE
        0x37B0, // LAMZU
        0x373E, // CompX / Lamzu
        0x3710, // Pulsar
        0x36A7, // WLmouse
        0x3367, // Endgame Gear
        0x25A7, // Attack Shark
        0x1D57, // Attack Shark OEM
        0x1A2C, // Ajazz
        0x258A, // Motospeed / SinoWealth
        0x260D, // Dareu
        0x33E4, // G-Wolves
        0x3404, // Zaunkoenig
        0x04D9, // Holtek / Fater
        0x045E  // Microsoft Pro IntelliMouse
    };

    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, string> _workingPaths = new(StringComparer.OrdinalIgnoreCase);

    public CompxBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "compx-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        // 接收器本身没有电池；即使接收器支持查询，也不能把配对设备的电量
        // 直接标成“接收器电量”。这里只处理已确认是外设的实体。
        if (device.Kind == DeviceKind.Receiver || device.Connection == ConnectionKind.Wired)
        {
            return false;
        }

        if (device.VendorId is not { } vid || !KnownVendorIds.Contains(vid))
        {
            return false;
        }

        return device.InterfacePaths.Count > 0 || !string.IsNullOrWhiteSpace(device.DevicePath);
    }

    public async Task<BatteryReading> ReadAsync(
        PeripheralDevice device,
        CancellationToken cancellationToken = default)
    {
        var paths = device.InterfacePaths.Count > 0
            ? device.InterfacePaths
            : string.IsNullOrWhiteSpace(device.DevicePath)
                ? Array.Empty<string>()
                : new[] { device.DevicePath! };

        if (paths.Count == 0)
        {
            return BatteryReading.Unsupported("没有可用的 HID 接口路径。");
        }

        var triedFeatureReport = false;
        IEnumerable<string> candidatePaths = paths;

        // 上次成功的接口优先，避免每次刷新都从键盘/鼠标集合开始盲试。
        if (_workingPaths.TryGetValue(device.Id, out var knownPath) && paths.Contains(knownPath))
        {
            candidatePaths = new[] { knownPath }
                .Concat(paths.Where(p => !string.Equals(p, knownPath, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var handle = WindowsHidDeviceEnumerator.OpenHid(path);
            if (handle.IsInvalid)
            {
                continue;
            }

            if (!HidNativeMethods.TryGetCaps(handle, out var caps) ||
                caps.FeatureReportByteLength < 64)
            {
                continue;
            }

            triedFeatureReport = true;
            var length = caps.FeatureReportByteLength >= 65 ? 65 : 64;

            try
            {
                if (TryQuery(handle, length, out var percent, out var charging))
                {
                    _workingPaths[device.Id] = path;
                    _logger.Info($"CompX 电量读取成功：{device.DisplayName} = {percent}% (charging={charging})");
                    return BatteryReading.FromPercent(
                        percent,
                        BatterySource.CompxHid,
                        BatteryConfidence.Medium,
                        "CompX 厂商 Feature 0x83 查询返回的百分比。",
                        isCharging: charging);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"CompX 查询失败：{device.DisplayName} / {path} / {ex.Message}");
            }
        }

        return triedFeatureReport
            ? BatteryReading.ReadFailed("CompX 查询已发送，但设备没有返回有效的 0x83 电量帧。")
            : BatteryReading.Unsupported("设备的 HID Feature 报告长度不足 64 字节，无法承载 CompX 查询。");
    }

    /// <summary>
    /// 诊断入口：绕过 CanHandle/UI，直接把每个接口的 Feature 长度与查询结果写进日志。
    /// 只做只读查询，不修改设备配置；用于确认真实硬件到底走哪条协议。
    /// </summary>
    public Task<string> ProbeAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"设备：{device.DisplayName} / VID_{device.VendorId:X4} PID_{device.ProductId:X4}");

        var paths = device.InterfacePaths.Count > 0
            ? device.InterfacePaths
            : string.IsNullOrWhiteSpace(device.DevicePath)
                ? Array.Empty<string>()
                : new[] { device.DevicePath! };

        foreach (var path in paths)
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
                sb.AppendLine("  读取 HID Caps 失败");
                continue;
            }

            sb.AppendLine($"  feature={caps.FeatureReportByteLength} input={caps.InputReportByteLength} output={caps.OutputReportByteLength}");

            if (caps.FeatureReportByteLength < 64)
            {
                continue;
            }

            var length = caps.FeatureReportByteLength >= 65 ? 65 : 64;
            var ok = TryQuery(handle, length, out var percent, out var charging, line => sb.AppendLine(line));
            sb.AppendLine(ok ? $"  结果：{percent}% charging={charging}" : "  结果：没有有效电量帧");
        }

        return Task.FromResult(sb.ToString());
    }

    private static bool TryQuery(
        SafeFileHandle handle,
        int reportLength,
        out int percent,
        out bool isCharging,
        Action<string>? trace = null)
    {
        percent = 0;
        isCharging = false;

        var tx = new byte[reportLength];
        tx[0] = 0x00;
        if (reportLength >= 7)
        {
            tx[1] = 0x00;
            tx[2] = 0x00;
            tx[3] = 0x02;
            tx[4] = 0x02;
            tx[5] = 0x00;
            tx[6] = 0x83;
        }

        trace?.Invoke($"  tx[{tx.Length}]={Convert.ToHexString(tx)}");

        if (!HidNativeMethods.HidD_SetFeature(handle, tx, tx.Length))
        {
            // 64 字节设备没有前导 0x00，命令整体左移一位。
            if (reportLength == 65)
            {
                var tx64 = new byte[64];
                Array.Copy(tx, 1, tx64, 0, tx64.Length);
                if (!HidNativeMethods.HidD_SetFeature(handle, tx64, tx64.Length))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        // 设备需要几毫秒准备响应；最多轮询 12 次，总等待约 100ms。
        for (var attempt = 0; attempt < 12; attempt++)
        {
            Thread.Sleep(8);

            var rx = new byte[reportLength];
            rx[0] = 0x00;
            if (!HidNativeMethods.HidD_GetFeature(handle, rx, rx.Length))
            {
                trace?.Invoke($"  rx 失败，Win32={Marshal.GetLastWin32Error()}");
                continue;
            }

            trace?.Invoke($"  rx[{rx.Length}]={Convert.ToHexString(rx)}");

            var data = rx;
            if (rx.Length >= 2 && rx[0] == 0x00)
            {
                data = new byte[rx.Length - 1];
                Array.Copy(rx, 1, data, 0, data.Length);
            }

            // 0xA0 是“命令已收到，稍后取结果”的确认帧，不是电量帧。
            if (data.Length > 0 && data[0] == 0xA0)
            {
                continue;
            }

            for (var i = 0; i + 7 < data.Length; i++)
            {
                if (data[i] != 0xA1 ||
                    data[i + 1] != 0x00 ||
                    data[i + 2] != 0x02 ||
                    data[i + 3] != 0x02 ||
                    data[i + 4] != 0x00 ||
                    data[i + 5] != 0x83)
                {
                    continue;
                }

                var charging = data[i + 6];
                var pct = data[i + 7];
                if (charging is 0 or 1 && pct is > 0 and <= 100)
                {
                    percent = pct;
                    isCharging = charging == 1;
                    return true;
                }
            }
        }

        return false;
    }
}
