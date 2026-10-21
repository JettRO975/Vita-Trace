using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
    private SettingsWindow? _settingsWindow;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        _app = (App)Application.Current;
        InitializeComponent();

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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // 设置窗口是单例：连续点击（或输入设备重复触发 Click）时不叠出多个窗口。
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(
            _app.Settings.Clone(),
            _app.ApplySettings,
            _app.Localization);

        var window = new SettingsWindow
        {
            Owner = this,
            DataContext = viewModel
        };

        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        viewModel.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();
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
            Settings_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateTexts();

    private void UpdateTexts() => SettingsButton.ToolTip = _app.Localization["Settings"];
}
