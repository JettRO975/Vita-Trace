using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VitaTrace.App.Services;
using VitaTrace.App.ViewModels;
using VitaTrace.Infrastructure.Storage;

namespace VitaTrace.App.Views;

/// <summary>
/// 嵌在主窗口里的设置页。设置窗口不再单独弹出，这样主题切换的圆形遮罩
/// 只覆盖一个窗口，过渡更自然；取消/离开页面时由这里负责把预览主题回滚。
/// </summary>
public partial class SettingsView : UserControl
{
    private static readonly HashSet<string> AutoSaveProperties = new()
    {
        nameof(SettingsViewModel.Theme),
        nameof(SettingsViewModel.Language),
        nameof(SettingsViewModel.RefreshSeconds),
        nameof(SettingsViewModel.LowBatteryThreshold),
        nameof(SettingsViewModel.HistoryRetentionDays)
    };

    private SettingsViewModel? _viewModel;
    private bool _ready;
    private bool _inThemeChange;
    private string _currentTheme = "sea";
    private readonly DispatcherTimer _themeSaveTimer;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();

        // 主题动画播放完再落盘，避免保存时重新 Apply 主题打断遮罩动画。
        _themeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _themeSaveTimer.Tick += (_, _) =>
        {
            _themeSaveTimer.Stop();
            if (_ready)
            {
                _viewModel?.AutoSave();
            }
        };
    }

    /// <summary>进入设置页：绑定新草稿，并记录进入时的主题。</summary>
    public void Activate(SettingsViewModel viewModel)
    {
        _ready = false;
        _themeSaveTimer.Stop();
        DataContext = viewModel;
        HookViewModel();

        _currentTheme = viewModel.Theme;
        _ready = true;
    }

    /// <summary>离开设置页：每次修改都已自动保存，不需要回滚预览主题。</summary>
    public void Deactivate()
    {
        _ready = false;
        if (_themeSaveTimer.IsEnabled)
        {
            _themeSaveTimer.Stop();
            _viewModel?.AutoSave();
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = null;
        DataContext = null;
    }

    private void HookViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// 设置项变化后自动保存；数字框在 LostFocus 时更新，因此不会每敲一个字符就保存一次。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_ready || _inThemeChange || e.PropertyName is null ||
            !AutoSaveProperties.Contains(e.PropertyName))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_ready)
            {
                _viewModel?.AutoSave();
            }
        }));
    }

    /// <summary>
    /// 点击外观胶囊时立即预览：无论切到哪种外观，都从点击位置向外扩散。
    /// </summary>
    private void AppearanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready ||
            AppearanceList.SelectedItem is not AppearanceOption option ||
            option.Value == _currentTheme)
        {
            return;
        }

        var window = Window.GetWindow(this);
        Point screenPoint;

        // 鼠标点击时以真实光标位置为圆心，这样扩散一定从用户按下的色块开始；
        // 键盘切换或鼠标不在窗口上时，回退到胶囊中心。
        if (window is not null && IsMouseOver)
        {
            screenPoint = window.PointToScreen(Mouse.GetPosition(window));
        }
        else if (AppearanceList.ItemContainerGenerator.ContainerFromItem(option) is FrameworkElement container)
        {
            var center = container.TranslatePoint(
                new Point(container.ActualWidth / 2, container.ActualHeight / 2),
                this);
            screenPoint = PointToScreen(center);
        }
        else
        {
            return;
        }

        _inThemeChange = true;
        try
        {
            _viewModel!.Theme = option.Value;

            if (Application.Current is App app)
            {
                app.PreviewTheme(option.Value, screenPoint);
            }

            // 先让圆形动画播完，再保存；保存会重新 Apply 主题，太早会打断遮罩。
            _themeSaveTimer.Stop();
            _themeSaveTimer.Start();
        }
        finally
        {
            _inThemeChange = false;
        }

        _currentTheme = option.Value;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel is not null)
        {
            _viewModel.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// 开关的保存不依赖 PropertyChanged：某些模板/键盘操作下事件时序不稳定，
    /// 直接以 Click 作为“用户已经完成一次切换”的可靠落点。
    /// </summary>
    private void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        // Click 触发时 IsChecked 已完成更新；用 Background 优先级让绑定先写回 VM。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _viewModel?.AutoSave();
        }));
    }

    /// <summary>
    /// 修改日志目录。只移动日志文件，不移动设置/历史；选择后立即自动保存并切换 FileLogger。
    /// </summary>
    private void ChangeLogPath_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = TryFindResource("ChangeLogPathTitle") as string ?? "选择日志保存目录",
            InitialDirectory = Path.GetDirectoryName(_viewModel.LogPath) ?? AppPaths.LogDirectory,
            Multiselect = false
        };

        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted == true)
        {
            _viewModel.ChangeLogDirectory(dialog.FolderName);
        }
    }
}
