using VitaTrace.Core.Models;

namespace VitaTrace.Core.Abstractions;

/// <summary>
/// 电量历史存储。只保存“已经成功读到的采样点”，不保存推测值。
/// 具体实现可以是 JSON 文件、SQLite 或云同步；界面层只依赖这个接口。
/// </summary>
public interface IHistoryStore
{
    /// <summary>读取指定设备在 since 之后的采样点，按时间升序返回副本。</summary>
    IReadOnlyList<BatterySample> GetSamples(string deviceId, DateTimeOffset since);

    /// <summary>追加一个采样点。实现应保证线程安全与原子落盘。</summary>
    void Append(string deviceId, BatterySample sample);

    /// <summary>删除早于 olderThan 的采样点。</summary>
    void Prune(DateTimeOffset olderThan);
}

/// <summary>演示模式/测试使用的空实现，不会写任何文件。</summary>
public sealed class NullHistoryStore : IHistoryStore
{
    public static NullHistoryStore Instance { get; } = new();

    private NullHistoryStore()
    {
    }

    public IReadOnlyList<BatterySample> GetSamples(string deviceId, DateTimeOffset since)
        => Array.Empty<BatterySample>();

    public void Append(string deviceId, BatterySample sample)
    {
    }

    public void Prune(DateTimeOffset olderThan)
    {
    }
}
