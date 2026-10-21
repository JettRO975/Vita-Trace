namespace VitaTrace.Core.Models;

/// <summary>
/// 设备类型。只表达“能确认的部分”，无法确认时用 Unknown，不用猜测值填充。
/// </summary>
public enum DeviceKind
{
    Unknown = 0,
    Keyboard,
    Mouse,
    Headset,
    Gamepad,
    /// <summary>USB 无线接收器本身（不是键鼠外设）。</summary>
    Receiver,
    Other
}

/// <summary>
/// 连接方式。UsbReceiver 表示“通过 2.4G USB 接收器连接”，不是 USB 有线。
/// </summary>
public enum ConnectionKind
{
    Unknown = 0,
    UsbReceiver,
    Bluetooth,
    Wired
}

/// <summary>
/// 电量状态。必须能区分“没读到”“不支持”“读取失败”“断开”“有线不需要电池”，
/// 这些状态在 UI 上不能合并成一个“--”。
/// </summary>
public enum BatteryStatus
{
    Unknown = 0,
    Ok,
    /// <summary>读数存在，但超过新鲜度阈值，可能已过期。</summary>
    Stale,
    /// <summary>设备支持电量，但本次读取失败（超时、权限、协议错误等）。</summary>
    ReadFailed,
    /// <summary>设备/接收器没有可用的电量通道。</summary>
    Unsupported,
    /// <summary>外设已断开，保留最后一次已知读数但必须标灰。</summary>
    Disconnected,
    /// <summary>接收器已断开，配对的外设无法读取。</summary>
    ReceiverOffline,
    /// <summary>有线设备由总线供电，不存在需要监控的电池。</summary>
    Wired
}

/// <summary>
/// 电量数据来源。来源必须随读数一起展示，用户才能判断可信度。
/// </summary>
public enum BatterySource
{
    None = 0,
    /// <summary>Windows PnP 属性（蓝牙 BLE 设备）。</summary>
    WindowsPnp,
    /// <summary>BLE GATT Battery Level 特征（0x2A19）。</summary>
    BleGatt,
    /// <summary>罗技 HID++ 2.0（Unified Battery / Battery Status）。</summary>
    LogitechHidpp,
    /// <summary>CompX / PixArt 系的 64 字节厂商 Feature 查询（部分迈从、ATK、VXE 等）。</summary>
    CompxHid,
    /// <summary>迈从 0x11/0x13 报告协议（目前只确认部分鼠标型号）。</summary>
    MchoseHid,
    /// <summary>雷柏 2.4G 接收器主动推送的 FF00:0002 状态帧（只读，不发送命令）。</summary>
    RapooHid,
    /// <summary>标准 HID Battery Strength（Usage Page 0x06 / Usage 0x20）。</summary>
    StandardHid,
    /// <summary>用户手动输入（未来的兜底功能，默认关闭）。</summary>
    Manual
}

/// <summary>
/// 读数可信度。用于 UI 标注，不能替代真实读数。
/// </summary>
public enum BatteryConfidence
{
    Unknown = 0,
    Low,
    Medium,
    High
}
