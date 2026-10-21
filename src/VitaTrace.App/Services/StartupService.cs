using Microsoft.Win32;

namespace VitaTrace.App.Services;

/// <summary>
/// 开机启动：只写 HKCU\...\Run，不需要管理员权限，也不安装服务。
/// 绿色版被移动/删除后，这里留下的失效项由用户在设置里关闭即可。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VitaTrace";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var command = BuildCommand();
            if (!string.IsNullOrWhiteSpace(command))
            {
                key.SetValue(ValueName, command);
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// 开发时通过 dotnet run 启动，ProcessPath 是 dotnet.exe，需要额外带上 DLL 路径；
    /// 发布后的 apphost 则直接指向 exe。
    /// </summary>
    private static string BuildCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return string.Empty;
        }

        if (processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            // 开发/框架依赖模式：dotnet.exe + VitaTrace.dll。
            // 单文件发布时 ProcessPath 是 VitaTrace.exe，不会走到这里；
            // 因此不能用 Assembly.Location（单文件下恒为空）。
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "VitaTrace.dll");
            return File.Exists(assemblyPath)
                ? $"\"{processPath}\" \"{assemblyPath}\" --autostart"
                : string.Empty;
        }

        return $"\"{processPath}\" --autostart";
    }
}
