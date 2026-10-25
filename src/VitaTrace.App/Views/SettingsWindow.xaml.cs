using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VitaTrace.App.Services;
using VitaTrace.App.ViewModels;

namespace VitaTrace.App.Views;

/// <summary>
/// 设置窗口。窗口本身只处理 Escape 关闭和标题栏外观；
/// 所有设置项的读写与校验都在 SettingsViewModel 中完成。
/// 外观选项是“预览式”切换：点一下立即预览，保存才落盘，取消/关闭会回滚。
/// </summary>
public partial class SettingsWindow : Window
{
    private SettingsViewModel? _boundViewModel;
    private bool _ready;
    private bool _saved;
    private string _originalTheme = "sea";
    private string _currentTheme = "sea";
    private string _previousTheme = "sea";

    public SettingsWindow()
    {
        InitializeComponent();

        // 设置窗口和主窗口共用主题；这里只需要补一次 DWM 深色边框/圆角。
        SourceInitialized += (_, _) =>
            WindowEffects.Apply(this, ((App)Application.Current).Theme.IsDark);

        DataContextChanged += (_, _) => HookViewModel();
        Loaded += OnLoaded;
    }

    private void HookViewModel()
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.Saved -= OnSaved;
        }

        _boundViewModel = DataContext as SettingsViewModel;
        if (_boundViewModel is not null)
        {
            _boundViewModel.Saved += OnSaved;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null)
        {
            HookViewModel();
        }

        if (_boundViewModel is null)
        {
            return;
        }

        _originalTheme = _boundViewModel.Theme;
        _currentTheme = _originalTheme;
        _previousTheme = _originalTheme;
        _ready = true;
    }

    private void OnSaved(object? sender, EventArgs e) => _saved = true;

    /// <summary>
    /// 点击外观胶囊时立即预览。反向切换（点回上一次选中的外观）时圆形遮罩收缩，
    /// 其余情况从点击位置向外扩散。
    /// </summary>
    private void AppearanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready ||
            AppearanceList.SelectedItem is not AppearanceOption option ||
            option.Value == _currentTheme)
        {
            return;
        }

        if (AppearanceList.ItemContainerGenerator.ContainerFromItem(option) is not FrameworkElement container)
        {
            return;
        }

        var center = container.TranslatePoint(
            new Point(container.ActualWidth / 2, container.ActualHeight / 2),
            this);
        var screenPoint = PointToScreen(center);
        var reverse = option.Value == _previousTheme;

        if (Application.Current is App app)
        {
            app.PreviewTheme(option.Value, screenPoint, reverse);
        }

        _previousTheme = _currentTheme;
        _currentTheme = option.Value;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.Saved -= OnSaved;
        }

        // 预览过但没保存（取消 / X / Esc）时恢复原外观，避免设置窗口留下未持久化的状态。
        if (_ready && !_saved && _currentTheme != _originalTheme && Application.Current is App app)
        {
            app.ApplyThemeNow(_originalTheme);
        }

        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
