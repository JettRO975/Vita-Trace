using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 基于 SetupAPI + HID API 的真实设备枚举器（第三阶段）。
///
/// 枚举思路：
/// 1. 用 GUID_DEVINTERFACE_HID 枚举系统中所有 HID 接口；
/// 2. 读取接口的 VID/PID、产品名、厂商名、Usage Page/Usage；
/// 3. 沿 CfgMgr32 的父设备链向上找到 USB/蓝牙物理设备，
///    同一个物理设备下的多个 HID 集合只生成一个实体；
/// 4. 用“已知接收器 VID/PID + 名称关键词 + 多 HID 集合”做接收器启发式判断；
/// 5. 接收器只生成一个“接收器”实体，不把它的 HID 集合伪装成已配对键鼠。
///
/// 本阶段只做“识别”，不读取电量；所以真实设备的 BatteryStatus 只会是
/// Wired（USB/接收器）或 Unknown（蓝牙），不会出现任何数字。
/// </summary>
public sealed class WindowsHidDeviceEnumerator : IDeviceEnumerator
{
    // 已知 2.4G 接收器 PID。列表只用于“提高接收器判断置信度”，
    // 不在列表里的设备仍会通过名称/接口形态判断，不会因此被丢弃。
    private static readonly HashSet<ushort> KnownReceiverPids = new()
    {
        0xC52B, // Unifying
        0xC532,
        0xC534,
        0xC539, // Lightspeed
        0xC53F, // G304 等 Lightspeed
        0xC547,
        0xC548, // Bolt
        0xC54D
    };

    private static readonly string[] ReceiverKeywords =
    {
        "receiver", "接收器", "unifying", "lightspeed", "bolt", "2.4g", "2.4 g"
    };

    private static readonly string[] HeadsetKeywords =
    {
        "headset", "headphone", "earphone", "耳机", "耳麦"
    };

    private readonly IAppLogger _logger;

    public WindowsHidDeviceEnumerator(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public Task<IReadOnlyList<PeripheralDevice>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<PeripheralDevice>>(() => Enumerate(cancellationToken), cancellationToken);
    }

    private IReadOnlyList<PeripheralDevice> Enumerate(CancellationToken cancellationToken)
    {
        // 该实现只适用于 Windows；在非 Windows 上返回空列表而不是抛异常，
        // 方便未来把 Core/Infrastructure 放到别的平台做单元测试。
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<PeripheralDevice>();
        }

        HidNativeMethods.HidD_GetHidGuid(out var hidGuid);
        var deviceInfoSet = HidNativeMethods.SetupDiGetClassDevsW(
            ref hidGuid,
            null,
            IntPtr.Zero,
            HidNativeMethods.DigcfPresent | HidNativeMethods.DigcfDeviceInterface);

        if (deviceInfoSet == new IntPtr(-1))
        {
            _logger.Warn($"SetupDiGetClassDevs 失败，Win32={Marshal.GetLastWin32Error()}");
            return Array.Empty<PeripheralDevice>();
        }

        var interfaces = new List<HidInterfaceInfo>();
        try
        {
            for (var index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var interfaceData = new HidNativeMethods.SpDeviceInterfaceData
                {
                    Size = Marshal.SizeOf<HidNativeMethods.SpDeviceInterfaceData>()
                };

                if (!HidNativeMethods.SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref hidGuid,
                        index,
                        ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != HidNativeMethods.ErrorNoMoreItems)
                    {
                        _logger.Warn($"SetupDiEnumDeviceInterfaces 中断，Win32={error}，已枚举 {index} 个接口");
                    }

                    break;
                }

                var info = ReadInterface(deviceInfoSet, ref interfaceData);
                if (info is not null)
                {
                    interfaces.Add(info);
                }
            }
        }
        finally
        {
            HidNativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        foreach (var info in interfaces)
        {
            _logger.Info(
                $"  HID 接口 {info.DisplayName} | usage=0x{info.UsagePage:X4}/0x{info.Usage:X4} | " +
                $"feature={info.FeatureReportLength} | {info.Path}");
        }

        var devices = BuildDevices(interfaces);
        _logger.Info($"HID 枚举完成：{interfaces.Count} 个接口，合并为 {devices.Count} 个设备");

        foreach (var device in devices)
        {
            _logger.Info(
                $"  {device.EffectiveName} | {device.Kind} | {device.Connection} | " +
                $"VID_{device.VendorId:X4} PID_{device.ProductId:X4} | {device.DevicePath}");
        }

        return devices;
    }

    private HidInterfaceInfo? ReadInterface(
        IntPtr deviceInfoSet,
        ref HidNativeMethods.SpDeviceInterfaceData interfaceData)
    {
        try
        {
            var deviceInfo = new HidNativeMethods.SpDevInfoData
            {
                Size = Marshal.SizeOf<HidNativeMethods.SpDevInfoData>()
            };

            // 第一次调用只取所需缓冲区大小；cbSize 在 x64 上必须是 8（含对齐），
            // 不是 Marshal.SizeOf 的结果，这是 SetupAPI 的经典坑。
            _ = HidNativeMethods.SetupDiGetDeviceInterfaceDetailW(
                deviceInfoSet,
                ref interfaceData,
                IntPtr.Zero,
                0,
                out var requiredSize,
                ref deviceInfo);

            if (requiredSize == 0)
            {
                return null;
            }

            var detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                if (!HidNativeMethods.SetupDiGetDeviceInterfaceDetailW(
                        deviceInfoSet,
                        ref interfaceData,
                        detailBuffer,
                        requiredSize,
                        out _,
                        ref deviceInfo))
                {
                    return null;
                }

                var path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                var instanceId = GetInstanceId(deviceInfoSet, ref deviceInfo) ?? path;
                var physicalId = GetPhysicalParentId(deviceInfo.DevInst) ?? NormalizeInstanceId(instanceId);

                // 只保留真正通过 USB / 蓝牙连接的 HID 设备。
                // 笔记本的 HIDI2C 触摸板、ACPI 电源键、虚拟多点触控等内部设备
                // 也会出现在 HID 接口枚举里，但它们不是用户要监控的外设。
                if (!IsExternalTransport(physicalId))
                {
                    return null;
                }

                var product = string.Empty;
                var manufacturer = string.Empty;
                var serial = string.Empty;
                ushort vendorId = 0;
                ushort productId = 0;
                ushort usagePage = 0;
                ushort usage = 0;
                ushort featureReportLength = 0;
                ushort inputReportLength = 0;
                ushort outputReportLength = 0;

                using var handle = OpenHid(path);
                if (!handle.IsInvalid)
                {
                    var attributes = new HidNativeMethods.HiddAttributes
                    {
                        Size = Marshal.SizeOf<HidNativeMethods.HiddAttributes>()
                    };

                    if (HidNativeMethods.HidD_GetAttributes(handle, ref attributes))
                    {
                        vendorId = attributes.VendorId;
                        productId = attributes.ProductId;
                    }

                    product = ReadHidString(handle, isProduct: true);
                    manufacturer = ReadHidString(handle, isProduct: false);
                    serial = ReadSerial(handle);
                    (usagePage, usage) = ReadUsage(handle);

                    if (HidNativeMethods.TryGetCaps(handle, out var caps))
                    {
                        featureReportLength = caps.FeatureReportByteLength;
                        inputReportLength = caps.InputReportByteLength;
                        outputReportLength = caps.OutputReportByteLength;
                    }
                }

                // 打不开设备时，从实例 ID 里解析 VID/PID；这比直接用 0 更可靠。
                if (vendorId == 0 || productId == 0)
                {
                    ParseVidPid(instanceId, out var parsedVid, out var parsedPid);
                    vendorId = vendorId == 0 ? parsedVid : vendorId;
                    productId = productId == 0 ? parsedPid : productId;
                }

                var friendlyName = GetRegistryString(
                    deviceInfoSet, ref deviceInfo, HidNativeMethods.SpdrpFriendlyName);
                var deviceDesc = GetRegistryString(
                    deviceInfoSet, ref deviceInfo, HidNativeMethods.SpdrpDeviceDesc);
                var registryManufacturer = GetRegistryString(
                    deviceInfoSet, ref deviceInfo, HidNativeMethods.SpdrpMfg);

                var displayName = FirstNonEmpty(product, friendlyName, deviceDesc, "未知设备");
                var manufacturerName = FirstNonEmpty(manufacturer, registryManufacturer);
                var connection = physicalId.Contains("BTH", StringComparison.OrdinalIgnoreCase) ||
                                 instanceId.Contains("BTH", StringComparison.OrdinalIgnoreCase)
                    ? ConnectionKind.Bluetooth
                    : ConnectionKind.Wired;

                return new HidInterfaceInfo
                {
                    Path = path,
                    InstanceId = instanceId,
                    PhysicalId = physicalId,
                    DisplayName = displayName,
                    Product = product,
                    Manufacturer = manufacturerName,
                    SerialNumber = serial,
                    VendorId = vendorId,
                    ProductId = productId,
                    UsagePage = usagePage,
                    Usage = usage,
                    FeatureReportLength = featureReportLength,
                    InputReportLength = inputReportLength,
                    OutputReportLength = outputReportLength,
                    Connection = connection
                };
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"读取 HID 接口失败：{ex.Message}", ex);
            return null;
        }
    }

    private static IReadOnlyList<PeripheralDevice> BuildDevices(IReadOnlyList<HidInterfaceInfo> interfaces)
    {
        // 同一个物理设备可能有多个 HID 集合（键盘 + 鼠标 + 厂商自定义），
        // 必须先按 PhysicalId 合并，否则列表里会出现一堆重复设备。
        var groups = interfaces
            .GroupBy(i => i.PhysicalId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .ToList();

        var devices = new List<PeripheralDevice>(groups.Count);
        foreach (var group in groups)
        {
            var primary = group.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Product)) ?? group[0];
            var isReceiver = LooksLikeReceiver(group);

            // 雷柏的 2.4G 接收器产品名可能只是 “Rapoo Gaming Device”，
            // 但它在 FF00:0002 上暴露了鼠标电量状态帧。把它当作
            // “通过接收器连接的鼠标”，而不是有线设备，否则电量读取会被跳过。
            var isRapooBatteryDongle = primary.VendorId == 0x24AE &&
                                       group.Any(i => i.UsagePage == 0xFF00 && i.Usage == 0x0002);

            // 迈从 G98 Pro 的 SINOWEALTH 接收器在 FF02:0002 上有一个 20 字节
            // Output/Input 集合，可用 0x13/0x4A 查询键盘电量。它产品名仍叫
            // “接收器”，但这里按“通过接收器连接的键盘”展示，避免把键盘电量
            // 挂在接收器卡片上。
            var wirelessControl = group.FirstOrDefault(i =>
                i.UsagePage == 0xFF02 && i.Usage == 0x0002 && i.OutputReportLength >= 20);
            var isMchoseKeyboardDongle = primary.VendorId == 0x258A && wirelessControl is not null;

            // ATK/VXE 的 2.4G 接收器常暴露名称为 “USB Receiver” 的 HID 集合，
            // 本身没有电池，但在 FF02:0002（17 字节）或 FF05:0002（64 字节）
            // 上能查询鼠标电量。这里不把它作为“未知接收器”，而是作为
            // “通过接收器连接的鼠标”展示；协议读取器会再按接口指纹确认。
            var compxWireless = group.FirstOrDefault(i =>
                i.UsagePage == 0xFF02 && i.Usage == 0x0002 &&
                i.InputReportLength >= 17 && i.OutputReportLength >= 17);
            var nordicWireless = group.FirstOrDefault(i =>
                i.UsagePage == 0xFF05 && i.Usage == 0x0002 &&
                i.InputReportLength >= 64 && i.OutputReportLength >= 64);
            var isAtkMouseDongle = (isReceiver || primary.Connection == ConnectionKind.UsbReceiver) &&
                                   (primary.VendorId is 0x373B or 0x3554) &&
                                   (compxWireless is not null || nordicWireless is not null) &&
                                   ClassifyKind(group) != DeviceKind.Keyboard;

            // 罗技 Unifying/Bolt/Lightspeed 接收器本身没有电池，电池在配对设备通道 1..6。
            // 这里把接收器投影成“无线鼠标/键盘”卡片，避免把通道 1 的电量挂在接收器上。
            // 只建一个逻辑设备，读取器取第一个有效通道（按用户确认的范围）。
            var logitechHidpp = group.FirstOrDefault(i =>
                i.VendorId == 0x046D &&
                i.UsagePage is 0xFF00 or 0xFF43 &&
                i.InputReportLength >= 20 &&
                i.OutputReportLength >= 20);
            var isLogitechWireless = primary.VendorId == 0x046D &&
                                     isReceiver &&
                                     logitechHidpp is not null &&
                                     ClassifyKind(group) is DeviceKind.Mouse or DeviceKind.Keyboard;
            var logitechKind = isLogitechWireless ? ClassifyKind(group) : DeviceKind.Other;

            // ROG 的 2.4G 接收器本身没有电池；把 64/65 字节厂商集合投影成
            // “ROG 无线设备”，由 AsusRogBatteryReader 依次试鼠标/键盘命令。
            var asusRogHid = group.FirstOrDefault(i =>
                i.VendorId == 0x0B05 &&
                i.UsagePage is 0xFF01 or 0xFF00 &&
                i.InputReportLength >= 64 &&
                i.OutputReportLength >= 64);
            var isAsusRogWireless = primary.VendorId == 0x0B05 &&
                                    isReceiver &&
                                    asusRogHid is not null;
            var asusRogKind = isAsusRogWireless ? ClassifyKind(group) : DeviceKind.Other;

            var kind = isMchoseKeyboardDongle
                ? DeviceKind.Keyboard
                : isAtkMouseDongle
                    ? DeviceKind.Mouse
                    : isLogitechWireless
                        ? logitechKind
                        : isAsusRogWireless
                            ? asusRogKind
                            : isReceiver
                                ? DeviceKind.Receiver
                                : ClassifyKind(group);

            var connection = isReceiver || isRapooBatteryDongle || isMchoseKeyboardDongle || isAtkMouseDongle || isLogitechWireless || isAsusRogWireless
                ? ConnectionKind.UsbReceiver
                : primary.Connection;

            var batteryStatus = isMchoseKeyboardDongle || isAtkMouseDongle || isLogitechWireless || isAsusRogWireless
                ? BatteryStatus.Unknown
                : isReceiver || (!isRapooBatteryDongle && connection == ConnectionKind.Wired)
                    ? BatteryStatus.Wired
                    : BatteryStatus.Unknown;

            // 接收器的 HID 集合里可能混着已配对键鼠的产品名，不能直接把第一个
            // 产品名当成接收器名称；优先找名称里明确带 Receiver 的集合。
            var receiverInterface = isReceiver
                ? group.FirstOrDefault(i => ReceiverKeywords.Any(i.DisplayName.Contains))
                : null;
            var displayName = isMchoseKeyboardDongle
                ? "SINOWEALTH 2.4G 键盘"
                : isAtkMouseDongle
                    ? $"{FirstNonEmpty(primary.Manufacturer, "ATK/VXE")} 2.4G 鼠标"
                    : isLogitechWireless
                        ? logitechKind == DeviceKind.Mouse ? "Logitech 无线鼠标" : "Logitech 无线键盘"
                        : isAsusRogWireless
                            ? asusRogKind == DeviceKind.Keyboard ? "ASUS ROG 无线键盘" :
                              asusRogKind == DeviceKind.Mouse ? "ASUS ROG 无线鼠标" : "ASUS ROG 无线设备"
                            : isReceiver
                                ? FirstNonEmpty(
                                    receiverInterface?.DisplayName,
                                    string.IsNullOrWhiteSpace(primary.Manufacturer)
                                        ? null
                                        : $"{primary.Manufacturer} USB 接收器",
                                    "USB 接收器")
                                : primary.DisplayName;

            devices.Add(new PeripheralDevice
            {
                Id = (isLogitechWireless ? "logi:" : isAsusRogWireless ? "rog:" : isReceiver && !isAtkMouseDongle ? "receiver:" : "hid:") + StableHash(primary.PhysicalId),
                DisplayName = displayName,
                Model = isReceiver || isAtkMouseDongle || isLogitechWireless || isAsusRogWireless || string.IsNullOrWhiteSpace(primary.Product) ? null : primary.Product,
                Manufacturer = string.IsNullOrWhiteSpace(primary.Manufacturer) ? null : primary.Manufacturer,
                SerialNumber = isReceiver || isAtkMouseDongle || isLogitechWireless || isAsusRogWireless || string.IsNullOrWhiteSpace(primary.SerialNumber)
                    ? null
                    : primary.SerialNumber,
                VendorId = primary.VendorId == 0 ? null : primary.VendorId,
                ProductId = primary.ProductId == 0 ? null : primary.ProductId,
                Kind = kind,
                Connection = connection,
                IsConnected = true,
                DevicePath = primary.Path,
                PhysicalInstanceId = primary.PhysicalId,
                InterfacePaths = group
                    .Select(i => i.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                VendorStatusPath = group
                    .FirstOrDefault(i => i.UsagePage == 0xFF00 && i.Usage == 0x0002)
                    ?.Path,
                WirelessControlPath = compxWireless?.Path ?? nordicWireless?.Path ?? wirelessControl?.Path,
                BatteryStatus = batteryStatus,
                Battery = null,
                IsDemo = false
            });
        }

        // 接收器排后面，键盘/鼠标排前面；同类型按名称排序，刷新时顺序稳定。
        return devices
            .OrderBy(d => d.Kind == DeviceKind.Receiver ? 1 : 0)
            .ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static DeviceKind ClassifyKind(IReadOnlyList<HidInterfaceInfo> interfaces)
    {
        // 游戏鼠标经常同时暴露键盘集合（宏键、板载配置），
        // 所以先判断鼠标；真正的键盘通常没有 0x01/0x02 集合。
        if (interfaces.Any(i => i.UsagePage == 0x01 && i.Usage == 0x02))
        {
            return DeviceKind.Mouse;
        }

        if (interfaces.Any(i => i.UsagePage == 0x01 && i.Usage == 0x06))
        {
            return DeviceKind.Keyboard;
        }

        if (interfaces.Any(i => i.UsagePage == 0x01 && i.Usage is 0x04 or 0x05))
        {
            return DeviceKind.Gamepad;
        }

        var name = string.Join(' ', interfaces.Select(i => i.DisplayName)).ToLowerInvariant();
        if (HeadsetKeywords.Any(name.Contains))
        {
            return DeviceKind.Headset;
        }

        return DeviceKind.Other;
    }

    private static bool LooksLikeReceiver(IReadOnlyList<HidInterfaceInfo> interfaces)
    {
        var primary = interfaces.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Product)) ?? interfaces[0];
        var name = string.Join(' ', interfaces.Select(i => i.DisplayName)).ToLowerInvariant();

        if (ReceiverKeywords.Any(name.Contains))
        {
            return true;
        }

        if (primary.VendorId == 0x046D && KnownReceiverPids.Contains(primary.ProductId))
        {
            return true;
        }

        // 不使用“多 HID 集合 + 厂商 Usage Page”作为接收器判据：
        // 很多游戏键盘也有厂商自定义集合，这样会把有线键盘误判成接收器。
        // 名称和已知 VID/PID 之外的情况宁可保留为未知设备，也不猜测。
        return false;
    }

    private static string ReadHidString(SafeFileHandle handle, bool isProduct)
    {
        var buffer = new StringBuilder(256);
        var ok = isProduct
            ? HidNativeMethods.HidD_GetProductString(handle, buffer, (uint)buffer.Capacity)
            : HidNativeMethods.HidD_GetManufacturerString(handle, buffer, (uint)buffer.Capacity);
        return ok ? buffer.ToString().Trim() : string.Empty;
    }

    private static string ReadSerial(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(256);
        return HidNativeMethods.HidD_GetSerialNumberString(handle, buffer, (uint)buffer.Capacity)
            ? buffer.ToString().Trim()
            : string.Empty;
    }

    private static (ushort UsagePage, ushort Usage) ReadUsage(SafeFileHandle handle)
    {
        if (!HidNativeMethods.HidD_GetPreparsedData(handle, out var preparsedData) ||
            preparsedData == IntPtr.Zero)
        {
            return ((ushort)0, (ushort)0);
        }

        try
        {
            var caps = new HidNativeMethods.HidpCaps { Reserved = new ushort[17] };
            return HidNativeMethods.HidP_GetCaps(preparsedData, ref caps) == HidNativeMethods.HidpStatusSuccess
                ? (caps.UsagePage, caps.Usage)
                : ((ushort)0, (ushort)0);
        }
        finally
        {
            HidNativeMethods.HidD_FreePreparsedData(preparsedData);
        }
    }

    internal static SafeFileHandle OpenHid(string path)
    {
        // 先请求读写共享；某些厂商软件会独占设备，失败后退到 0 访问，
        // 只读取属性，不发送任何报告。
        var handle = HidNativeMethods.CreateFileW(
            path,
            HidNativeMethods.GenericRead | HidNativeMethods.GenericWrite,
            HidNativeMethods.FileShareRead | HidNativeMethods.FileShareWrite,
            IntPtr.Zero,
            HidNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();

        // 键盘/鼠标的 HID 集合通常允许读、拒绝写。只请求 GENERIC_READ
        // 就能拿到 Preparsed Data（Usage / Report 长度）；最后才退到 0 访问。
        handle = HidNativeMethods.CreateFileW(
            path,
            HidNativeMethods.GenericRead,
            HidNativeMethods.FileShareRead | HidNativeMethods.FileShareWrite,
            IntPtr.Zero,
            HidNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (!handle.IsInvalid)
        {
            return handle;
        }

        handle.Dispose();
        return HidNativeMethods.CreateFileW(
            path,
            0,
            HidNativeMethods.FileShareRead | HidNativeMethods.FileShareWrite,
            IntPtr.Zero,
            HidNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
    }

    private static string? GetInstanceId(
        IntPtr deviceInfoSet,
        ref HidNativeMethods.SpDevInfoData deviceInfo)
    {
        var buffer = new StringBuilder(512);
        return HidNativeMethods.SetupDiGetDeviceInstanceIdW(
            deviceInfoSet,
            ref deviceInfo,
            buffer,
            buffer.Capacity,
            out _)
            ? buffer.ToString()
            : null;
    }

    private static string? GetPhysicalParentId(uint devInst)
    {
        var current = devInst;
        for (var depth = 0; depth < 5; depth++)
        {
            if (HidNativeMethods.CM_Get_Parent(out var parent, current, 0) != 0)
            {
                return null;
            }

            var id = GetDeviceId(parent);
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (IsPhysicalDeviceId(id))
            {
                return id;
            }

            current = parent;
        }

        return null;
    }

    private static string? GetDeviceId(uint devInst)
    {
        var buffer = new StringBuilder(512);
        return HidNativeMethods.CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Capacity, 0) == 0
            ? buffer.ToString()
            : null;
    }

    private static bool IsPhysicalDeviceId(string id)
    {
        // USB 复合设备的中间层是 ...&MI_00；继续向上才是整个 USB 设备。
        if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
            !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExternalTransport(string physicalId)
        => physicalId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
           physicalId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
           physicalId.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeInstanceId(string instanceId)
    {
        // 没有父设备时，至少把同一个 HID 设备的多个 COLxx 合并掉。
        var col = instanceId.IndexOf("&COL", StringComparison.OrdinalIgnoreCase);
        return col > 0 ? instanceId[..col] : instanceId;
    }

    private static string? GetRegistryString(
        IntPtr deviceInfoSet,
        ref HidNativeMethods.SpDevInfoData deviceInfo,
        uint property)
    {
        _ = HidNativeMethods.SetupDiGetDeviceRegistryPropertyW(
            deviceInfoSet,
            ref deviceInfo,
            property,
            out _,
            null,
            0,
            out var requiredSize);

        if (requiredSize == 0)
        {
            return null;
        }

        var buffer = new byte[requiredSize];
        if (!HidNativeMethods.SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref deviceInfo,
                property,
                out _,
                buffer,
                requiredSize,
                out _))
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
    }

    private static void ParseVidPid(string instanceId, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;

        var vidIndex = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vidIndex >= 0 && vidIndex + 8 <= instanceId.Length)
        {
            _ = ushort.TryParse(instanceId.AsSpan(vidIndex + 4, 4), out vendorId);
        }

        var pidIndex = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (pidIndex >= 0 && pidIndex + 8 <= instanceId.Length)
        {
            _ = ushort.TryParse(instanceId.AsSpan(pidIndex + 4, 4), out productId);
        }
    }

    private static string StableHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private sealed class HidInterfaceInfo
    {
        public required string Path { get; init; }

        public required string InstanceId { get; init; }

        public required string PhysicalId { get; init; }

        public required string DisplayName { get; init; }

        public string Product { get; init; } = string.Empty;

        public string Manufacturer { get; init; } = string.Empty;

        public string SerialNumber { get; init; } = string.Empty;

        public ushort VendorId { get; init; }

        public ushort ProductId { get; init; }

        public ushort UsagePage { get; init; }

        public ushort Usage { get; init; }

        public ushort FeatureReportLength { get; init; }

        public ushort InputReportLength { get; init; }

        public ushort OutputReportLength { get; init; }

        public ConnectionKind Connection { get; init; }
    }
}
