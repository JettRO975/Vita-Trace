using System.Text.Json;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.Infrastructure.Storage;

/// <summary>
/// 基于 JSON 的设置存储。
/// 设计取舍：Phase 2 不引入数据库，设置量很小；历史曲线后续单独用 SQLite。
/// 写入采用“临时文件 + 原子替换”，避免写到一半断电导致配置损坏。
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public JsonSettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>最近一次 Load 的错误；由 App 读取并写入日志。</summary>
    public string? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                var defaults = new AppSettings();
                defaults.Normalize();
                return defaults;
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            // 配置损坏不应阻止软件启动：回退默认值，同时把原因交给上层记录。
            LastLoadError = ex.Message;
            var fallback = new AppSettings();
            fallback.Normalize();
            return fallback;
        }
    }

    public SettingsSaveResult Save(AppSettings settings)
    {
        try
        {
            settings.Normalize();
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FilePath, overwrite: true);
            return SettingsSaveResult.Ok();
        }
        catch (Exception ex)
        {
            return SettingsSaveResult.Fail(ex.Message);
        }
    }
}
