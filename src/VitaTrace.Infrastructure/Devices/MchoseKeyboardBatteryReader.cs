using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 迈从（MCHOSE）2.4G 键盘的只读电量读取器。
///
/// 协议事实来自 M HUB 网页驱动的键盘分支（G98 Pro 使用 report ID 0x13）：
/// - 发送 20 字节 Output Report：0x13、命令 0x4A、子命令 0x01，其余为 0；
/// - 设备返回 20 字节 Input Report：0x13、0x4A、0x01、…、电量、状态、…、校验和；
/// - 最后 1 字节是前 19 字节的算术和（mod 256）。
///
/// 这是**只读查询**：命令只读取电量，不修改 DPI、灯效或键盘配置。
/// 校验和/长度不符时返回 ReadFailed，不使用缓存值或固定 100%。
/// </summary>
public sealed class MchoseKeyboardBatteryReader : IBatteryReader
{
    private const byte ReportId = 0x13;
    private const byte BatteryCommand = 0x4A;
    private const byte BatterySubCommand = 0x01;
    private const int FrameLength = 20;
    private const int BatteryIndex = 5;
    private const int ChecksumIndex = 19;

    private readonly IAppLogger _logger;

    public MchoseKeyboardBatteryReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public string Id => "mchose-keyboard-hid";

    public bool CanHandle(PeripheralDevice device)
    {
        // G98 Pro 的接收器 VID 是 0x258A（SINOWEALTH）；部分迈从设备用 0x3837。
        // 必须拿到 FF02:0002 的 Output 集合，不能只凭品牌猜。
        return device.WirelessControlPath is not null &&
               (device.VendorId == 0x258A || device.VendorId == 0x3837);
    }

    public Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Read(device, cancellationToken), cancellationToken);
    }

    private BatteryReading Read(PeripheralDevice device, CancellationToken cancellationToken)
    {
        if (!CanHandle(device))
        {
            return BatteryReading.Unsupported("没有可用的迈从键盘控制接口");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var path = device.WirelessControlPath!;
        using var handle = HidNativeMethods.CreateFileW(
            path,
            HidNativeMethods.GenericRead | HidNativeMethods.GenericWrite,
            HidNativeMethods.FileShareReadWrite,
            IntPtr.Zero,
            HidNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return BatteryReading.ReadFailed("无法打开迈从键盘控制接口");
        }

        var request = new byte[FrameLength];
        request[0] = ReportId;
        request[1] = BatteryCommand;
        request[2] = BatterySubCommand;

        if (!HidNativeMethods.HidD_SetOutputReport(handle, request, request.Length))
        {
            // 少数驱动只接受 WriteFile 的 Output Report 写法，作为兼容回退。
            if (!HidNativeMethods.WriteFile(handle, request, request.Length, out _, IntPtr.Zero))
            {
                _logger.Warn($"迈从键盘电量查询发送失败：{path}");
                return BatteryReading.ReadFailed("迈从键盘查询命令被拒绝");
            }
        }

        var response = ReadResponse(handle, 2500);
        if (response is null)
        {
            return BatteryReading.ReadFailed("未收到迈从键盘电量状态帧");
        }

        if (!TryParse(response, out var percent))
        {
            _logger.Warn("迈从键盘状态帧校验失败");
            return BatteryReading.ReadFailed("迈从键盘状态帧无效");
        }

        // 状态帧里还有一个字节疑似充电/满电标志，但官方网页驱动没有公开语义；
        // 在确认前不要把“正在充电”当作事实，只报告电量。
        return BatteryReading.FromPercent(
            percent,
            BatterySource.MchoseHid,
            BatteryConfidence.Medium,
            "迈从键盘状态帧：2.4G 无线");
    }

    private static byte[]? ReadResponse(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int timeoutMs)
    {
        byte[]? received = null;
        var reader = new Thread(() =>
        {
            try
            {
                var buffer = new byte[FrameLength];
                if (HidNativeMethods.ReadFile(
                        handle,
                        buffer,
                        buffer.Length,
                        out var bytesRead,
                        IntPtr.Zero) &&
                    bytesRead >= FrameLength)
                {
                    received = buffer;
                }
            }
            catch
            {
                // 设备休眠/拔出时由超时路径收尾。
            }
        })
        {
            IsBackground = true,
            Name = "mchose-keyboard-battery-read"
        };

        reader.Start();
        if (!reader.Join(timeoutMs))
        {
            return null;
        }

        return received;
    }

    private static bool TryParse(byte[] data, out int percent)
    {
        percent = 0;

        if (data.Length < FrameLength ||
            data[0] != ReportId ||
            data[1] != BatteryCommand)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < ChecksumIndex; i++)
        {
            sum = (sum + data[i]) & 0xFF;
        }

        if (sum != data[ChecksumIndex])
        {
            return false;
        }

        if (data[BatteryIndex] > 100)
        {
            return false;
        }

        percent = data[BatteryIndex];
        return true;
    }
}
