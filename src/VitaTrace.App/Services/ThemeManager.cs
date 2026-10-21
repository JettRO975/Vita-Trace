using System.Windows;
using Microsoft.Win32;

namespace VitaTrace.App.Services;

/// <summary>
/// 浅色/深色主题切换。只替换主题字典，样式字典与语言字典保持不动。
/// 跟随系统时读取 HKCU 的 AppsUseLightTheme（不需要管理员权限）。
/// </summary>
public sealed class ThemeManager
{
    private const string ThemePrefix = "Themes/";

    public bool IsDark { get; private set; }

    public event EventHandler? Changed;

    public void Apply(string preference)
    {
        var isDark = preference switch
        {
            "dark" => true,
            "light" => false,
            _ => IsSystemDark()
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;

        // 只匹配浅色/深色主题字典。不能只判断是否包含 "Themes/"，
        // 因为 Shared.xaml 也在 Themes 目录下，误替换会把全部样式一起换掉。
        var oldDictionary = dictionaries.FirstOrDefault(d =>
        {
            var source = d.Source?.OriginalString;
            return source is not null &&
                   (source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                    source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase));
        });

        var newDictionary = new ResourceDictionary
        {
            Source = new Uri($"{ThemePrefix}{(isDark ? "Dark" : "Light")}.xaml", UriKind.Relative)
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

        IsDark = isDark;
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
