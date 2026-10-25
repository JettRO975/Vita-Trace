using System.Reflection;
using System.Windows.Media;
using VitaTrace.App.Mvvm;
using VitaTrace.App.Services;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;
using VitaTrace.Infrastructure.Storage;

namespace VitaTrace.App.ViewModels;

/// <summary>设置页里的一个选项（主题/语言胶囊）。</summary>
public sealed record OptionItem(string Value, string Label);

/// <summary>
/// 外观选项：除了名称，还带三枚预览色点。
/// 颜色用 Brush 而不是字符串，避免绑定到 Fill 时依赖运行时类型转换。
/// </summary>
public sealed record AppearanceOption(string Value, string Label, Brush Color1, Brush Color2, Brush Color3);

/// <summary>
/// 设置窗口的状态。保存时把草稿整体交给 App.ApplySettings，
/// 由 App 统一处理“持久化 + 主题/语言/开机启动生效”，避免设置页直接改全局状态。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _draft;
    private readonly Func<AppSettings, SettingsSaveResult> _apply;
    private readonly LocalizationService _l10n;

    private string _theme;
    private string _language;
    private int _refreshSeconds;
    private int _lowBatteryThreshold;
    private int _historyRetentionDays;
    private bool _startWithWindows;
    private bool _minimizeToTrayOnClose;
    private bool _lowBatteryNotifications;
    private string? _errorMessage;

    public SettingsViewModel(
        AppSettings draft,
        Func<AppSettings, SettingsSaveResult> apply,
        LocalizationService localization)
    {
        _draft = draft;
        _apply = apply;
        _l10n = localization;

        _theme = draft.Theme;
        _language = draft.Language;
        _refreshSeconds = draft.RefreshSeconds;
        _lowBatteryThreshold = draft.LowBatteryThreshold;
        _historyRetentionDays = draft.HistoryRetentionDays;
        _startWithWindows = draft.StartWithWindows;
        _minimizeToTrayOnClose = draft.MinimizeToTrayOnClose;
        _lowBatteryNotifications = draft.LowBatteryNotifications;

        ThemeOptions = new[]
        {
            new AppearanceOption("sea", _l10n["ThemeSea"], Brush("#598BFD"), Brush("#9AD4CB"), Brush("#35B3C9")),
            new AppearanceOption("dusk", _l10n["ThemeDusk"], Brush("#945FFA"), Brush("#F698B8"), Brush("#F7DFD9")),
            new AppearanceOption("system", _l10n["ThemeSystem"], Brush("#D7DEEA"), Brush("#AEB8C8"), Brush("#8A96AD")),
            new AppearanceOption("light", _l10n["ThemeLight"], Brush("#FFFFFF"), Brush("#2F6BFF"), Brush("#E2E6EE")),
            new AppearanceOption("dark", _l10n["ThemeDark"], Brush("#1E1F24"), Brush("#7C9CFF"), Brush("#50545E"))
        };

        LanguageOptions = new[]
        {
            new OptionItem("system", _l10n["LanguageSystem"]),
            new OptionItem("zh-CN", _l10n["LanguageZh"]),
            new OptionItem("en-US", _l10n["LanguageEn"])
        };

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        OpenLogCommand = new RelayCommand(() => AppPaths.OpenInExplorer(AppPaths.LogDirectory));
        OpenDataCommand = new RelayCommand(() => AppPaths.OpenInExplorer(AppPaths.Root));
    }

    public event EventHandler? CloseRequested;

    /// <summary>保存成功后触发；设置窗口据此跳过关闭时的主题回滚。</summary>
    public event EventHandler? Saved;

    public IReadOnlyList<AppearanceOption> ThemeOptions { get; }

    public IReadOnlyList<OptionItem> LanguageOptions { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenLogCommand { get; }

    public RelayCommand OpenDataCommand { get; }

    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value);
    }

    public int RefreshSeconds
    {
        get => _refreshSeconds;
        set => SetProperty(ref _refreshSeconds, value);
    }

    public int LowBatteryThreshold
    {
        get => _lowBatteryThreshold;
        set => SetProperty(ref _lowBatteryThreshold, value);
    }

    public int HistoryRetentionDays
    {
        get => _historyRetentionDays;
        set => SetProperty(ref _historyRetentionDays, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set => SetProperty(ref _minimizeToTrayOnClose, value);
    }

    public bool LowBatteryNotifications
    {
        get => _lowBatteryNotifications;
        set => SetProperty(ref _lowBatteryNotifications, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string LogPath => AppPaths.LogFile;

    public string DataPath => AppPaths.Root;

    public string VersionText => $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}";

    private static SolidColorBrush Brush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Save()
    {
        ErrorMessage = null;

        if (RefreshSeconds is < 15 or > 3600)
        {
            ErrorMessage = _l10n["ValidationRefresh"];
            return;
        }

        if (LowBatteryThreshold is < 5 or > 50)
        {
            ErrorMessage = _l10n["ValidationThreshold"];
            return;
        }

        if (HistoryRetentionDays is < 7 or > 365)
        {
            ErrorMessage = _l10n["ValidationHistory"];
            return;
        }

        _draft.Theme = Theme;
        _draft.Language = Language;
        _draft.RefreshSeconds = RefreshSeconds;
        _draft.LowBatteryThreshold = LowBatteryThreshold;
        _draft.HistoryRetentionDays = HistoryRetentionDays;
        _draft.StartWithWindows = StartWithWindows;
        _draft.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        _draft.LowBatteryNotifications = LowBatteryNotifications;
        _draft.Normalize();

        var result = _apply(_draft);
        if (!result.Success)
        {
            ErrorMessage = _l10n.Format("SaveFailed", result.Error ?? string.Empty);
            return;
        }

        Saved?.Invoke(this, EventArgs.Empty);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
