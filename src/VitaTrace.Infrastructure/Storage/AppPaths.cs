namespace VitaTrace.Infrastructure.Storage;

/// <summary>
/// 应用数据路径。统一放在 %LOCALAPPDATA%\VitaTrace，避免写 Program Files 触发权限问题，
/// 也方便用户备份/清理。
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    /// <summary>
    /// 默认使用 %LOCALAPPDATA%\VitaTrace。VITATRACE_DATA_DIR 仅用于开发调试、
    /// 绿色版便携运行或自动化验收，避免把测试数据写进真实用户目录。
    /// </summary>
    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("VITATRACE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VitaTrace");
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>演示模式使用独立设置，避免覆盖真实配置。</summary>
    public static string DemoSettingsFile => Path.Combine(Root, "settings.demo.json");

    public static string DefaultLogDirectory => Path.Combine(Root, "logs");

    public static string LogDirectory => DefaultLogDirectory;

    public static string LogFile => Path.Combine(LogDirectory, "app.log");

    public static string DemoLogFile => Path.Combine(LogDirectory, "demo.log");

    /// <summary>
    /// 解析用户自定义日志目录。空值回退默认目录；相对路径按当前目录展开为绝对路径。
    /// 这里不创建目录，真正的可写性由 FileLogger.TrySwitchTo 验证。
    /// </summary>
    public static string ResolveLogDirectory(string? customDirectory)
    {
        if (string.IsNullOrWhiteSpace(customDirectory))
        {
            return DefaultLogDirectory;
        }

        return Path.GetFullPath(customDirectory.Trim());
    }

    /// <summary>日志统一写 app.log，目录由设置决定。</summary>
    public static string ResolveLogFile(string? customDirectory)
    {
        return Path.Combine(ResolveLogDirectory(customDirectory), "app.log");
    }

    /// <summary>历史使用 JSON 文件（当前无第三方 SQLite 依赖），仍放在应用数据目录。</summary>
    public static string HistoryFile => Path.Combine(Root, "history.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }

    public static void OpenInExplorer(string path)
    {
        // 目录不存在时 Explorer 会打开“此电脑”，先兜底创建。
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.GetDirectoryName(path) ?? Root;
        }

        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }
}
