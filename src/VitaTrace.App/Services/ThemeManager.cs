using System.Windows;
using Microsoft.Win32;

namespace VitaTrace.App.Services;

/// <summary>
/// 外观切换：跟随系统 / 浅色 / 深色 / 海蓝 / 暮紫。
/// 只替换主题字典，样式字典与语言字典保持不动。
/// 跟随系统时读取 HKCU 的 AppsUseLightTheme（不需要管理员权限）。
/// </summary>
public sealed class ThemeManager
{
    private const string ThemePrefix = "Themes/";

    private static readonly string[] ThemeFiles = { "Light", "Dark", "Sea", "Dusk" };

    public bool IsDark { get; private set; }

    public event EventHandler? Changed;

    public void Apply(string preference)
    {
        var theme = preference switch
        {
            "dark" => "Dark",
            "light" => "Light",
            "sea" => "Sea",
            "dusk" => "Dusk",
            _ => IsSystemDark() ? "Dark" : "Light"
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;

        // 只匹配主题字典。Shared.xaml 也在 Themes 目录下，不能按目录前缀判断。
        var oldDictionary = dictionaries.FirstOrDefault(d =>
        {
            var source = d.Source?.OriginalString;
            return source is not null &&
                   ThemeFiles.Any(file => source.EndsWith($"Themes/{file}.xaml", StringComparison.OrdinalIgnoreCase));
        });

        var newDictionary = new ResourceDictionary
        {
            Source = new Uri($"{ThemePrefix}{theme}.xaml", UriKind.Relative)
        };

        if (oldDictionary is not null)
        {
            var index = dictionaries.IndexOf(oldDictionary);
            dictionaries[index] = newDictionary;
        }
        else
        {
            dictionaries.Insert(1, newDictionary);
        }

        IsDark = theme == "Dark";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            // 读取注册表失败时回退浅色，不影响启动。
            return false;
        }
    }
}
