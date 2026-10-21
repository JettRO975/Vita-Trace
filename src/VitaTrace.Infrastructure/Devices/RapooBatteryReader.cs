using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 雷柏 2.4G 接收器的只读电量读取器。
///
/// 依据（只提取协议事实，代码为独立实现）：
/// - 雷柏接收器在 Vendor Usage Page 0xFF00 / Usage 0x0002 的输入集合上，
///   每隔几秒主动推送一个 Report ID = 7 的 19 字节状态帧；
/// - 帧内 byte 8 是电量百分比（0–100）；
/// - byte 1 的低 4 位表示连接方式：0 = 无线，2 = 插着数据线（充电）。
///
/// 这里**不发送任何 Feature/Output 命令**，只打开只读句柄接收设备主动上报，
/// 因此不会改 DPI、灯光或设备配置。设备不推送时明确返回 ReadFailed，
/// 绝不使用缓存值或固定 100%。
/// </summary>
public sealed class RapooBatteryReader : IBatteryReader
{
    private const ushort RapooVendorId = 0x24AE;
    private const byte StatusReportId = 7;
    private const int BatteryIndex = 8;

    private readonly IAppLogger _logger;

    public RapooBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "rapoo-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        // 只看 VID + 接口路径，不依赖设备名；VT7 Max 的接收器可能叫
        // “Rapoo Gaming Device”，靠名字会漏掉。
        return device.VendorId == RapooVendorId && device.InterfacePaths.Count > 0;
    }

    public Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Read(device, cancellationToken), cancellationToken);
    }

    private BatteryReading Read(PeripheralDevice device, CancellationToken cancellationToken)
    {
        if (!CanHandle(device))
        {
            return BatteryReading.Unsupported("不是雷柏设备");
        }

        // 只读已知的 FF00:0002 状态接口。不要在其余 HID 集合上逐个 ReadFile：
        // 某些集合会阻塞，导致探针和刷新线程卡死。
        var statusPath = device.VendorStatusPath;
        if (string.IsNullOrWhiteSpace(statusPath))
        {
            return BatteryReading.ReadFailed("未找到雷柏 2.4G 状态接口");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var reading = TryReadPath(statusPath, 5000);
            return reading ?? BatteryReading.ReadFailed("未收到雷柏 2.4G 状态帧");
        }
        catch (Exception ex)
        {
            _logger.Warn($"雷柏状态帧读取失败：{statusPath}", ex);
            return BatteryReading.ReadFailed("雷柏状态帧读取异常");
        }
    }

    private BatteryReading? TryReadPath(string path, int timeoutMs)
    {
        var handle = HidNativeMethods.CreateFileW(
            path,
            HidNativeMethods.GenericRead,
            HidNativeMethods.FileShareReadWrite,
            IntPtr.Zero,
            HidNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        byte[]? received = null;
        var reader = new Thread(() =>
        {
            try
            {
                // 状态帧固定 19 字节；缓冲区按 HID 输入报告长度给，避免驱动等待更多数据。
                var buffer = new byte[19];
                if (HidNativeMethods.ReadFile(handle, buffer, buffer.Length, out var bytesRead, IntPtr.Zero) &&
                    bytesRead > 0)
                {
                    received = buffer[..bytesRead];
                }
            }
            catch
            {
                // 设备拔出/句柄关闭时忽略，由超时路径收尾。
            }
        })
        {
            IsBackground = true,
            Name = "rapoo-battery-read"
        };

        reader.Start();
        if (!reader.Join(timeoutMs))
        {
            // 同步 ReadFile 卡住时，关闭句柄可让内核取消挂起 I/O；
            // 线程是后台线程，不会阻止进程退出。
            handle.Dispose();
            return null;
        }

        handle.Dispose();
        return received is not null && TryParse(received, out var reading) ? reading : null;
    }

    private static bool TryParse(byte[] data, out BatteryReading reading)
    {
        reading = null!;

        // 19 字节状态帧：byte 0 = Report ID 7，byte 1 低 4 位 = 连接方式。
        if (data.Length < BatteryIndex + 1 || data[0] != StatusReportId)
        {
            return false;
        }

        var mode = data[1] & 0x0F;
        if (mode is not 0 and not 2)
        {
            return false;
        }

        var percent = data[BatteryIndex];
        if (percent is < 0 or > 100)
        {
            return false;
        }

        var wired = mode == 2;
        reading = BatteryReading.FromPercent(
            percent,
            BatterySource.RapooHid,
            BatteryConfidence.High,
            wired ? "雷柏状态帧：USB 供电" : "雷柏状态帧：2.4G 无线",
            isCharging: wired);
        return true;
    }
}
