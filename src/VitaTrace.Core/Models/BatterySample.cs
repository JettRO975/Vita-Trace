namespace VitaTrace.Core.Models;

/// <summary>
/// 历史曲线上的一个采样点。Percent 允许为 null：设备读取失败时保留“断点”，
/// 曲线会断开，而不是把失败画成 0%。
/// </summary>
public readonly record struct BatterySample(DateTimeOffset Timestamp, int? Percent);
