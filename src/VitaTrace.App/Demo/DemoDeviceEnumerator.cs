using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.App.Demo;

/// <summary>
/// 第二阶段没有真实设备枚举，演示数据只用于验证界面与交互。
/// 硬约束：
/// 1. 所有实体都带 IsDemo=true，界面必须显示演示横幅；
/// 2. 演示读数不会被写入真实历史文件；
/// 3. 真实模式使用 EmptyDeviceEnumerator，绝不退化为演示数据。
/// </summary>
public sealed class DemoDeviceEnumerator : IDeviceEnumerator
{
    public Task<IReadOnlyList<PeripheralDevice>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PeripheralDevice> devices = new List<PeripheralDevice>
        {
            new()
            {
                Id = "demo-bolt-receiver",
                DisplayName = "Logitech Bolt Receiver",
                Manufacturer = "Logitech",
                Model = "Bolt",
                VendorId = 0x046D,
                ProductId = 0xC548,
                Kind = DeviceKind.Receiver,
                Connection = ConnectionKind.UsbReceiver,
                DevicePath = @"USB\VID_046D&PID_C548\DEMO",
                BatteryStatus = BatteryStatus.Wired,
                IsDemo = true
            },
            new()
            {
                Id = "demo-mx-master-3s",
                DisplayName = "MX Master 3S",
                Manufacturer = "Logitech",
                Model = "MX Master 3S",
                VendorId = 0x046D,
                ProductId = 0xB034,
                Kind = DeviceKind.Mouse,
                Connection = ConnectionKind.UsbReceiver,
                DevicePath = @"HID\VID_046D&PID_B034\DEMO",
                ReceiverId = "demo-bolt-receiver",
                ReceiverName = "Logitech Bolt Receiver",
                BatteryStatus = BatteryStatus.Ok,
                Battery = BatteryReading.FromPercent(82, BatterySource.LogitechHidpp, BatteryConfidence.High),
                IsDemo = true
            },
            new()
            {
                Id = "demo-mx-keys-s",
                DisplayName = "MX Keys S",
                Manufacturer = "Logitech",
                Model = "MX Keys S",
                VendorId = 0x046D,
                ProductId = 0xB378,
                Kind = DeviceKind.Keyboard,
                Connection = ConnectionKind.UsbReceiver,
                DevicePath = @"HID\VID_046D&PID_B378\DEMO",
                ReceiverId = "demo-bolt-receiver",
                ReceiverName = "Logitech Bolt Receiver",
                BatteryStatus = BatteryStatus.Ok,
                Battery = BatteryReading.FromPercent(64, BatterySource.LogitechHidpp, BatteryConfidence.High),
                IsDemo = true
            },
            new()
            {
                Id = "demo-g304",
                DisplayName = "G304 Lightspeed",
                Manufacturer = "Logitech",
                Model = "G304",
                VendorId = 0x046D,
                ProductId = 0xC53F,
                Kind = DeviceKind.Mouse,
                Connection = ConnectionKind.UsbReceiver,
                DevicePath = @"HID\VID_046D&PID_C53F\DEMO",
                ReceiverId = "demo-bolt-receiver",
                ReceiverName = "Logitech Bolt Receiver",
                BatteryStatus = BatteryStatus.Ok,
                Battery = BatteryReading.FromPercent(19, BatterySource.LogitechHidpp, BatteryConfidence.High),
                IsDemo = true
            },
            new()
            {
                Id = "demo-razer-viper",
                DisplayName = "炼狱蝰蛇 V2X",
                Manufacturer = "Razer",
                Model = "Viper V2X",
                VendorId = 0x1532,
                ProductId = 0x0091,
                Kind = DeviceKind.Mouse,
                Connection = ConnectionKind.UsbReceiver,
                DevicePath = @"HID\VID_1532&PID_0091\DEMO",
                ReceiverName = "2.4G Receiver",
                BatteryStatus = BatteryStatus.Unsupported,
                IsDemo = true
            },
            new()
            {
                Id = "demo-sony-xm5",
                DisplayName = "WH-1000XM5",
                Manufacturer = "Sony",
                Model = "WH-1000XM5",
                Kind = DeviceKind.Headset,
                Connection = ConnectionKind.Bluetooth,
                DevicePath = @"BTHLE\DEV_001122334455\DEMO",
                BatteryStatus = BatteryStatus.Ok,
                Battery = BatteryReading.FromPercent(45, BatterySource.BleGatt, BatteryConfidence.Medium),
                IsDemo = true
            },
            new()
            {
                Id = "demo-keychron-k8",
                DisplayName = "Keychron K8",
                Manufacturer = "Keychron",
                Model = "K8",
                Kind = DeviceKind.Keyboard,
                Connection = ConnectionKind.Wired,
                DevicePath = @"HID\VID_3434&PID_0280\DEMO",
                BatteryStatus = BatteryStatus.Wired,
                IsDemo = true
            },
            new()
            {
                Id = "demo-mx-anywhere",
                DisplayName = "MX Anywhere 3",
                Manufacturer = "Logitech",
                Model = "MX Anywhere 3",
                VendorId = 0x046D,
                ProductId = 0xB025,
                Kind = DeviceKind.Mouse,
                Connection = ConnectionKind.Bluetooth,
                DevicePath = @"BTHLE\DEV_8899AABBCCDD\DEMO",
                IsConnected = false,
                BatteryStatus = BatteryStatus.Disconnected,
                Battery = BatteryReading.FromPercent(55, BatterySource.WindowsPnp, BatteryConfidence.Medium),
                IsDemo = true
            }
        };

        return Task.FromResult(devices);
    }
}

/// <summary>真实模式下的占位枚举器；第三阶段替换为 USB/HID/BLE 实现。</summary>
public sealed class EmptyDeviceEnumerator : IDeviceEnumerator
{
    public Task<IReadOnlyList<PeripheralDevice>> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PeripheralDevice>>(Array.Empty<PeripheralDevice>());
}
