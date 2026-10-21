using VitaTrace.Core.Models;

namespace VitaTrace.Core.Abstractions;

/// <summary>保存结果：失败时把原因带回 UI，而不是静默吞掉。</summary>
public readonly record struct SettingsSaveResult(bool Success, string? Error)
{
    public static SettingsSaveResult Ok() => new(true, null);

    public static SettingsSaveResult Fail(string error) => new(false, error);
}

public interface ISettingsStore
{
    /// <summary>读取设置；文件不存在或损坏时返回默认值，不抛异常。</summary>
    AppSettings Load();

    /// <summary>原子写入：先写临时文件，再替换，避免断电留下半个 JSON。</summary>
    SettingsSaveResult Save(AppSettings settings);
}
