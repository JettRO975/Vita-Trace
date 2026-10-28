using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VitaTrace.App.Services;
using VitaTrace.App.ViewModels;
using VitaTrace.App.Views;

namespace VitaTrace.App;

/// <summary>
/// 主窗口。这里只做“窗口级”的事情：品牌行、托盘关闭语义、设置对话框入口。
/// 设备与电量的业务状态全部在 MainViewModel 中，避免窗口代码越写越厚。
/// </summary>
public partial class MainWindow : Window
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;

    /// <summary>
    /// 第二个实例用来请求“把已有窗口显示出来”的私有窗口消息。
    /// 必须由 WPF 自己调用 Show()，不能从另一个进程用 ShowWindow 强行显示：
    /// WPF 的 Visibility 仍是 Hidden 时，原生显示只会留下一个黑框。
    /// </summary>
    public const int ShowWindowMessage = 0x8001;

    private readonly App _app;
    private readonly DispatcherTimer _deviceChangeTimer;
    private SettingsViewModel? _settingsViewModel;
    private HwndSource? _hwndSource;
    private bool _entrancePlayed;
    private bool _navigating;
    private string _currentPage = "Devices";

    public MainWindow()
    {
        _app = (App)Application.Current;
        InitializeComponent();

        Loaded += OnLoaded;

        // 设备变更消息可能连续到达（一个 USB 设备会带来多个节点变化），
        // 用 800ms 防抖合并成一次刷新，避免反复枚举造成界面卡顿。
        _deviceChangeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _deviceChangeTimer.Tick += OnDeviceChangeTimerTick;

        _app.Localization.LanguageChanged += OnLanguageChanged;
        UpdateTexts();
    }

    /// <summary>
    /// 首次显示时的错落进场：品牌行、横幅、设备列表、详情依次淡入并上移。
    /// 只播一次；从托盘恢复时不再重复，避免每次打开都像重新启动。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_entrancePlayed)
        {
            return;
        }

        _entrancePlayed = true;

        // 首次显示时确保设备页和左侧导航状态一致（XAML 里 IsChecked 可能早于页面创建）。
        ShowPage(_currentPage);

        var items = new (FrameworkElement Element, int DelayMs)[]
        {
            (BrandRow, 0),
            (BannerHost, 55),
            (DevicePanel, 120),
            (DetailPanel, 185)
        };

        if (!SystemParameters.ClientAreaAnimation)
        {
            foreach (var (element, _) in items)
            {
                element.Opacity = 1;
                element.RenderTransform = Transform.Identity;
            }

            return;
        }

        foreach (var (element, delayMs) in items)
        {
            element.Opacity = 0;
            var translate = new TranslateTransform(0, 12);
            element.RenderTransform = translate;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var rise = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(360))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            element.BeginAnimation(OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    /// <summary>
    /// 切换设备时把详情滚动条复位到顶部。
    /// 否则从设置窗口返回、或查看过长的设备详情后，标题和电量会被上一次的滚动位置顶出视野。
    /// </summary>
    private void Devices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailScroll?.ScrollToTop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 使用 Windows 原生标题栏（最小化/最大化/关闭由系统绘制），
        // 避免自绘按钮在不同 DPI、主题和输入法下出现视觉与命中问题。
        WindowEffects.Apply(this, _app.Theme.IsDark);

        // WM_DEVICECHANGE + DBT_DEVNODES_CHANGED 是系统广播，不需要注册设备通知，
        // 也不需要管理员权限；收到后延迟刷新，覆盖插入、拔出和重新连接。
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
    }

    /// <summary>从托盘恢复窗口。最小化状态要先还原，否则 Show 出来仍是任务栏里的一条。</summary>
    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_app.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        if (_app.Settings.MinimizeToTrayOnClose && _app.Tray is not null)
        {
            // 关闭 = 收进托盘，后台继续刷新；这是桌面监控工具的常见习惯。
            e.Cancel = true;
            Hide();
            _app.NotifyMinimizedToTray();
            return;
        }

        // 没有托盘时关闭窗口就退出。用 BeginInvoke 延后 Shutdown，
        // 避免在 Closing 事件里同步关闭窗口造成重入。
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(_app.RequestExit));
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _deviceChangeTimer.Stop();
        _deviceChangeTimer.Tick -= OnDeviceChangeTimerTick;

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        _app.Localization.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange && wParam.ToInt64() == DbtDevNodesChanged)
        {
            // 重新计时，而不是立刻枚举：等系统把设备节点树更新完再读。
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        }
        else if (msg == ShowWindowMessage)
        {
            // 必须回到 WPF 的 Dispatcher 里 Show()，否则窗口虽然被 Win32 显示，
            // 内容仍停留在 Hidden 状态，用户会看到黑屏。
            Dispatcher.BeginInvoke(new Action(ShowFromTray), DispatcherPriority.Input);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnDeviceChangeTimerTick(object? sender, EventArgs e)
    {
        _deviceChangeTimer.Stop();

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsRefreshing)
        {
            // 正在刷新时不要重入，等当前刷新结束后再补一次。
            _deviceChangeTimer.Start();
            return;
        }

        _ = viewModel.RefreshAsync();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsRenaming)
        {
            viewModel.CommitRename();
            return;
        }

        viewModel.BeginRename();
        RenameTextBox.Focus();
        RenameTextBox.SelectAll();
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            viewModel.CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.CancelRename();
            e.Handled = true;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// 关闭按钮和原生标题栏的 X 行为一致：按设置最小化到托盘，或在退出时真正关闭。
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>左侧导航切换。设置页是同一窗口里的页面，不再弹模态窗口。</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_navigating || !IsLoaded || sender is not RadioButton { Tag: string page })
        {
            return;
        }

        ShowPage(page);
    }

    private void ShowPage(string page)
    {
        if (page != "Settings")
        {
            LeaveSettings();
        }

        DevicesPage.Visibility = page == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;

        PageTitle.Text = page switch
        {
            "Settings" => _app.Localization["Settings"],
            "About" => _app.Localization["About"],
            _ => _app.Localization["DevicesPageTitle"]
        };
        PageSubtitle.Text = page switch
        {
            "Settings" => _app.Localization["SettingsPageSubtitle"],
            "About" => _app.Localization["AboutPageSubtitle"],
            _ => _app.Localization["DevicesPageSubtitle"]
        };

        _navigating = true;
        NavDevices.IsChecked = page == "Devices";
        NavSettings.IsChecked = page == "Settings";
        NavAbout.IsChecked = page == "About";
        _navigating = false;
        _currentPage = page;

        if (page == "Settings")
        {
            EnterSettings();
        }
    }

    private void EnterSettings()
    {
        if (_settingsViewModel is not null)
        {
            return;
        }

        _settingsViewModel = new SettingsViewModel(
            _app.Settings.Clone(),
            _app.ApplySettings,
            _app.Localization,
            () => _app.LogFilePath);
        _settingsViewModel.CloseRequested += OnSettingsCloseRequested;
        SettingsPage.Activate(_settingsViewModel);
    }

    private void LeaveSettings()
    {
        if (_settingsViewModel is null)
        {
            return;
        }

        _settingsViewModel.CloseRequested -= OnSettingsCloseRequested;
        SettingsPage.Deactivate();
        _settingsViewModel = null;
    }

    private void OnSettingsCloseRequested(object? sender, EventArgs e)
    {
        ShowPage("Devices");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5 && DataContext is MainViewModel viewModel)
        {
            if (viewModel.RefreshCommand.CanExecute(null))
            {
                viewModel.RefreshCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowPage("Settings");
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateTexts();

    private void UpdateTexts()
    {
        if (!IsLoaded)
        {
            return;
        }

        ShowPage(_currentPage);
    }
}
