using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// SetupAPI / HID / CfgMgr32 的最小 P/Invoke 集合。
///
/// 只声明枚举阶段真正用到的函数，避免复制一整套 Win32 头文件：
/// 1. SetupDi* 用来枚举 HID 设备接口、读取设备实例 ID 和注册表属性；
/// 2. HidD_* / HidP_* 用来读取 VID/PID、产品名、厂商名和 Usage；
/// 3. CM_Get_Parent / CM_Get_Device_ID 用来向上找到 USB 物理设备，
///    从而把同一个接收器下的多个 HID 集合合并成一个实体。
///
/// 这些 API 都不需要管理员权限；打开设备时只请求只读/共享访问。
/// </summary>
internal static class HidNativeMethods
{
    internal const int DigcfPresent = 0x02;
    internal const int DigcfDeviceInterface = 0x10;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;

    internal const int ErrorNoMoreItems = 259;

    // HidP_* 的“成功”不是 0，而是 HIDP_STATUS_SUCCESS (0x00110000)。
    // 这条曾经把 Usage 与 Feature 长度全部读成 0，导致厂商电量协议无法探测。
    internal const int HidpStatusSuccess = 0x00110000;

    internal const uint SpdrpDeviceDesc = 0x00000000;
    internal const uint SpdrpHardwareId = 0x00000001;
    internal const uint SpdrpClass = 0x00000005;
    internal const uint SpdrpMfg = 0x00000009;
    internal const uint SpdrpFriendlyName = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDevInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    internal static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        int memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    internal static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int CM_Get_Device_IDW(
        uint devInst,
        StringBuilder buffer,
        uint bufferLen,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool HidD_GetProductString(
        SafeFileHandle device,
        StringBuilder buffer,
        uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool HidD_GetManufacturerString(
        SafeFileHandle device,
        StringBuilder buffer,
        uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool HidD_GetSerialNumberString(
        SafeFileHandle device,
        StringBuilder buffer,
        uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetFeature(
        SafeFileHandle device,
        byte[] buffer,
        int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_SetFeature(
        SafeFileHandle device,
        byte[] buffer,
        int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetInputReport(
        SafeFileHandle device,
        byte[] buffer,
        int bufferLength);

    // ---- 只读输入报告（雷柏等设备会主动推送状态帧） -------------------------
    // 使用 overlapped I/O，避免 ReadFile 在设备休眠时永久阻塞 UI 线程。
    internal const uint FileShareReadWrite = 0x00000003;
    internal const uint FileFlagOverlapped = 0x40000000;
    internal const int ErrorIoPending = 997;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(
        SafeFileHandle file,
        byte[] buffer,
        int numberOfBytesToRead,
        out int numberOfBytesRead,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(
        SafeFileHandle file,
        byte[] buffer,
        int numberOfBytesToRead,
        out int numberOfBytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetOverlappedResult(
        SafeFileHandle file,
        ref NativeOverlapped overlapped,
        out int numberOfBytesTransferred,
        bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateEventW(
        IntPtr eventAttributes,
        bool manualReset,
        bool initialState,
        IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_SetOutputReport(
        SafeFileHandle device,
        byte[] buffer,
        int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(
        SafeFileHandle file,
        byte[] buffer,
        int numberOfBytesToWrite,
        out int numberOfBytesWritten,
        IntPtr overlapped);

    internal const int CrSuccess = 0;
    internal const int CrNoSuchValue = 0x1D;
    internal const uint DevPropTypeByte = 0x00000003;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Locate_DevNodeW(
        out uint deviceInstance,
        string deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Get_DevNode_PropertyW(
        uint deviceInstance,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[] propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);

    /// <summary>读取 HID 顶层 Caps；失败时返回 false，不抛异常。</summary>
    internal static bool TryGetCaps(SafeFileHandle device, out HidpCaps capabilities)
    {
        capabilities = new HidpCaps { Reserved = new ushort[17] };

        if (!HidD_GetPreparsedData(device, out var preparsedData) ||
            preparsedData == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return HidP_GetCaps(preparsedData, ref capabilities) == HidpStatusSuccess;
        }
        finally
        {
            HidD_FreePreparsedData(preparsedData);
        }
    }
}
