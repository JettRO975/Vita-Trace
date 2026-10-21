namespace VitaTrace.Core.Models;

/// <summary>
/// 一个可展示的外设或接收器实体。
///
/// 设计要点：
/// 1. Receiver 与 Peripheral 是两种实体，接收器没有电池，不能把它标成“电量未知”的外设；
/// 2. ReceiverId 只在“能确认接收器与外设的对应关系”时填写（目前只有罗技 HID++ 能可靠做到）；
/// 3. 任何无法确认的字段保持 null，由 UI 显示“未知”，不编造。
/// </summary>
public sealed class PeripheralDevice
{
    /// <summary>稳定标识：优先使用设备路径/容器 ID 的哈希；演示数据用固定字符串。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>系统能提供的名称；可能为空，UI 需要兜底。</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string? Model { get; set; }

    public string? Manufacturer { get; set; }

    public string? SerialNumber { get; set; }

    public ushort? VendorId { get; set; }

    public ushort? ProductId { get; set; }

    public DeviceKind Kind { get; set; } = DeviceKind.Unknown;

    public ConnectionKind Connection { get; set; } = ConnectionKind.Unknown;

    public bool IsConnected { get; set; } = true;

    /// <summary>Windows 设备路径 / 容器 ID / BLE 实例 ID，用于排障。</summary>
    public string? DevicePath { get; set; }

    /// <summary>
    /// 物理父设备的 PnP 实例 ID（例如 BTHLE\DEV_... 或 USB\VID_...）。
    /// 蓝牙电量属性挂在 BLE 设备节点上，而不是 HID 接口上，读取时要用它定位。
    /// </summary>
    public string? PhysicalInstanceId { get; set; }

    /// <summary>
    /// 同一物理设备下的全部 HID 接口路径。厂商电量协议往往只在某一个
    /// vendor collection 上可用，不能只拿第一个接口去试。
    /// </summary>
    public IReadOnlyList<string> InterfacePaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 厂商自定义状态接口路径（例如雷柏 FF00:0002 的主动上报集合）。
    /// 有它时读取器直接读这个接口，避免在十几个 HID 集合上逐个超时。
    /// </summary>
    public string? VendorStatusPath { get; init; }

    /// <summary>厂商无线控制接口（Output/Input Report），用于需要主动查询的键盘协议。</summary>
    public string? WirelessControlPath { get; init; }

    /// <summary>若该外设通过接收器连接，这里填接收器的 Id；无法确认时为 null。</summary>
    public string? ReceiverId { get; set; }

    /// <summary>接收器显示名，便于 UI 在详情里说明“通过哪个接收器连接”。</summary>
    public string? ReceiverName { get; set; }

    public BatteryStatus BatteryStatus { get; set; } = BatteryStatus.Unknown;

    public BatteryReading? Battery { get; set; }

    /// <summary>用户自定义名称；只影响显示，不改变设备标识。</summary>
    public string? UserAlias { get; set; }

    /// <summary>用户备注，后续版本使用。</summary>
    public string? Notes { get; set; }

    /// <summary>演示数据标记：UI 必须显示“演示”横幅，避免与真实读数混淆。</summary>
    public bool IsDemo { get; init; }

    /// <summary>显示名优先级：用户别名 → 系统名称 → 明确占位。</summary>
    public string EffectiveName =>
        !string.IsNullOrWhiteSpace(UserAlias) ? UserAlias! :
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName :
        "未知设备";
}
