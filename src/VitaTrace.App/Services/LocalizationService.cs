using System.Globalization;
using System.Windows;

namespace VitaTrace.App.Services;

/// <summary>
/// 运行时语言切换。做法是替换 Application.Resources 里的字符串字典，
/// XAML 用 DynamicResource 绑定，切换后无需重启窗口。
/// </summary>
public sealed class LocalizationService
{
    private const string DictionaryPrefix = "Resources/Strings.";

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = "zh-CN";

    public bool IsChinese => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public void Apply(string preference)
    {
        var language = Resolve(preference);
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        var oldDictionary = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true);

        var newDictionary = new ResourceDictionary
        {
            Source = new Uri($"{DictionaryPrefix}{language}.xaml", UriKind.Relative)
        };

        if (oldDictionary is not null)
        {
            var index = dictionaries.IndexOf(oldDictionary);
            dictionaries[index] = newDictionary;
        }
        else
        {
            dictionaries.Add(newDictionary);
        }

        CurrentLanguage = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string this[string key]
    {
        get
        {
            var value = Application.Current?.TryFindResource(key);
            return value as string ?? key;
        }
    }

    public string Format(string key, params object[] args)
    {
        var template = this[key];
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static string Resolve(string preference)
    {
        return preference switch
        {
            "zh-CN" => "zh-CN",
            "en-US" => "en-US",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh-CN"
                : "en-US"
        };
    }
}
