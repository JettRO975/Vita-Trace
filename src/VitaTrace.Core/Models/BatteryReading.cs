namespace VitaTrace.Core.Models;

/// <summary>
/// 一次电量读数的不可变快照。
/// 关键约束：读不到就是读不到，Percent 为 null，绝不用 0 或随机数代替。
/// </summary>
public sealed record BatteryReading
{
    /// <summary>0–100；null 表示本次没有可用百分比。</summary>
    public int? Percent { get; init; }

    /// <summary>部分协议能提供毫伏值（例如罗技 HID++ 0x1001），没有则为 null。</summary>
    public int? Millivolts { get; init; }

    /// <summary>是否正在充电；null 表示协议未提供该信息。</summary>
    public bool? IsCharging { get; init; }

    /// <summary>本次读取结果对应的设备状态，由读取器判定，避免 UI 层猜测。</summary>
    public BatteryStatus Status { get; init; } = BatteryStatus.Unknown;

    public BatterySource Source { get; init; } = BatterySource.None;

    public BatteryConfidence Confidence { get; init; } = BatteryConfidence.Unknown;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>给用户看的补充说明，例如“接收器已连接，但配对外设未知”。</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// 构造一个有效的百分比读数。越界值直接抛异常，避免把错误数据带进 UI。
    /// </summary>
    public static BatteryReading FromPercent(
        int percent,
        BatterySource source,
        BatteryConfidence confidence,
        string? detail = null,
        int? millivolts = null,
        bool? isCharging = null)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "电量百分比必须在 0–100 之间。");
        }

        return new BatteryReading
        {
            Percent = percent,
            Status = BatteryStatus.Ok,
            Source = source,
            Confidence = confidence,
            Detail = detail,
            Millivolts = millivolts,
            IsCharging = isCharging,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    /// <summary>设备没有电量通道时使用；Percent 必须保持 null。</summary>
    public static BatteryReading Unsupported(string detail) => new()
    {
        Percent = null,
        Status = BatteryStatus.Unsupported,
        Source = BatterySource.None,
        Confidence = BatteryConfidence.Unknown,
        Detail = detail,
        UpdatedAt = DateTimeOffset.Now
    };

    /// <summary>
    /// 本轮没读到、但本地仍有有效旧读数时使用：保留百分比，标记为 Stale。
    /// 这样界面能区分“上次读数”和“本次失败”，不会把旧值伪装成实时值。
    /// </summary>
    public BatteryReading AsStale(string detail) => this with
    {
        Status = BatteryStatus.Stale,
        Detail = detail,
        Confidence = BatteryConfidence.Medium
    };

    /// <summary>读取失败：可能是超时、设备忙、厂商软件占用等，稍后可以重试。</summary>
    public static BatteryReading ReadFailed(string detail) => new()
    {
        Percent = null,
        Status = BatteryStatus.ReadFailed,
        Source = BatterySource.None,
        Confidence = BatteryConfidence.Unknown,
        Detail = detail,
        UpdatedAt = DateTimeOffset.Now
    };
}
