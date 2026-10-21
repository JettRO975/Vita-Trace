using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 读取蓝牙 BLE 设备的系统电量属性。
///
/// Windows 把 BLE Battery Service 的结果缓存成 PnP 设备属性：
/// GUID {104EA319-6EE2-4701-BD47-8DDBF425BBE5}，PID 2，类型 BYTE。
/// 这条通路不需要配对后自己跑 GATT，也不要求管理员权限。
///
/// 注意：
/// 1. 只对 BLE 设备可靠；经典蓝牙键鼠通常没有这个属性；
/// 2. 属性可能滞后，所以可信度标 Medium，不标 High；
/// 3. 属性不存在时返回 Unsupported，不把“没读到”写成 0%。
/// </summary>
public sealed class WindowsPnpBatteryReader : IBatteryReader
{
    private static readonly HidNativeMethods.DevPropKey BatteryPropertyKey = new()
    {
        FormatId = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        PropertyId = 2
    };

    public string Id => "windows-pnp-battery";

    public bool CanHandle(PeripheralDevice device)
        => device.Connection == ConnectionKind.Bluetooth &&
           !string.IsNullOrWhiteSpace(device.PhysicalInstanceId);

    public Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.PhysicalInstanceId))
        {
            return Task.FromResult(BatteryReading.Unsupported("没有蓝牙设备实例 ID。"));
        }

        if (HidNativeMethods.CM_Locate_DevNodeW(
                out var deviceInstance,
                device.PhysicalInstanceId,
                0) != HidNativeMethods.CrSuccess)
        {
            return Task.FromResult(BatteryReading.ReadFailed("无法定位蓝牙设备节点。"));
        }

        var buffer = new byte[1];
        var size = (uint)buffer.Length;
        var propertyKey = BatteryPropertyKey;
        var result = HidNativeMethods.CM_Get_DevNode_PropertyW(
            deviceInstance,
            ref propertyKey,
            out var propertyType,
            buffer,
            ref size,
            0);

        if (result == HidNativeMethods.CrNoSuchValue || size == 0)
        {
            // 设备没有暴露 PnP 电量属性：这是能力问题，不是临时失败。
            return Task.FromResult(BatteryReading.Unsupported("系统没有提供该蓝牙设备的电量属性。"));
        }

        if (result != HidNativeMethods.CrSuccess ||
            propertyType != HidNativeMethods.DevPropTypeByte ||
            buffer[0] > 100)
        {
            return Task.FromResult(BatteryReading.ReadFailed($"蓝牙电量属性读取失败（{result}）。"));
        }

        return Task.FromResult(BatteryReading.FromPercent(
            buffer[0],
            BatterySource.WindowsPnp,
            BatteryConfidence.Medium,
            "Windows PnP Battery"));
    }
}
