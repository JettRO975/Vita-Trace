using VitaTrace.Core.Models;

namespace VitaTrace.Core.Abstractions;

/// <summary>
/// 单条电量读取通路。每个品牌/协议一个实现，由上层按顺序探测。
/// 关键约定：读不到时返回 Percent=null 的读数，并给出可展示的原因；绝不编造百分比。
/// </summary>
public interface IBatteryReader
{
    /// <summary>用于日志和 UI 的来源标识，例如 "logitech-hidpp"。</summary>
    string Id { get; }

    /// <summary>快速能力判断，不发起 I/O；返回 false 表示该设备不可能走这条通路。</summary>
    bool CanHandle(PeripheralDevice device);

    /// <summary>执行读取。实现方负责超时、协议错误、设备忙等异常。</summary>
    Task<BatteryReading> ReadAsync(PeripheralDevice device, CancellationToken cancellationToken = default);
}
