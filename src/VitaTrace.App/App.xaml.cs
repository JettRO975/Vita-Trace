using System.Runtime.InteropServices;
using System.Text;
using VitaTrace.App.Demo;
using VitaTrace.App.Services;
using VitaTrace.App.ViewModels;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;
using VitaTrace.Infrastructure.Devices;
using System.Windows.Interop;
using VitaTrace.Infrastructure.Logging;
using VitaTrace.Infrastructure.Storage;

namespace VitaTrace.App;

/// <summary>
/// 应用入口与“组合根”。职责：
/// 1. 解析 --demo / --autostart；
/// 2. 加载设置、应用主题与语言；
/// 3. 创建托盘、主窗口与 MainViewModel；
/// 4. 统一处理设置保存、低电量提醒和退出。
///
/// 设计原则：业务状态在 ViewModel，窗口/托盘/注册表等平台细节留在 App 与 Services，
/// 这样后续替换设备枚举或电量读取器时不需要改界面。
/// </summary>
public partial class App : Application
{
    private IAppLogger _logger = NullLogger.Instance;
    private JsonSettingsStore? _settingsStore;
    private IHistoryStore _historyStore = NullHistoryStore.Instance;
    private TrayService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private bool _isDemo;
    private bool _isExiting;
    private bool _minimizedBalloonShown;

    public AppSettings Settings { get; private set; } = new();

    public ThemeManager Theme { get; } = new();

    public LocalizationService Localization { get; } = new();

    public TrayService? Tray => _tray;

    public IAppLogger Logger => _logger;

    public bool IsExiting => _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            InitializeCore(e.Args);
        }
        catch (Exception ex)
        {
            Logger.Error("启动失败", ex);
            MessageBox.Show(
                Localization.Format("FatalError", ex.Message),
                "Vita Trace",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>设置窗口保存时调用。持久化成功后才切换主题/语言，避免“界面变了但没存上”。</summary>
    public SettingsSaveResult ApplySettings(AppSettings draft)
    {
        if (_settingsStore is null)
        {
            return SettingsSaveResult.Fail("Settings store is not initialized.");
        }

        draft.Normalize();
        var saveResult = _settingsStore.Save(draft);
        if (!saveResult.Success)
        {
            Logger.Error($"保存设置失败：{saveResult.Error}");
            return saveResult;
        }

        Settings = draft;

        try
        {
            Theme.Apply(draft.Theme);

            // 原生标题栏不会自动跟随 WPF 资源切换，需要把 DWM 深色属性重新应用到所有窗口。
            foreach (Window window in Windows)
            {
                WindowEffects.Apply(window, Theme.IsDark);
            }

            Localization.Apply(draft.Language);

            // 开机启动只写 HKCU；失败时把原因带回设置窗口，而不是静默忽略。
            if (!_isDemo)
            {
                StartupService.SetEnabled(draft.StartWithWindows);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("应用设置失败", ex);
            return SettingsSaveResult.Fail(ex.Message);
        }

        _viewModel?.OnSettingsChanged();
        UpdateTrayText();
        UpdateTrayStatus();
        Logger.Info("设置已保存");
        return SettingsSaveResult.Ok();
    }

    /// <summary>关闭窗口但保留托盘时的轻提示，只在本次运行第一次关闭时显示。</summary>
    public void NotifyMinimizedToTray()
    {
        if (_minimizedBalloonShown)
        {
            return;
        }

        _minimizedBalloonShown = true;
        _tray?.ShowBalloon(Localization["TrayMinimized"], Localization["TrayMinimizedBody"]);
    }

    /// <summary>真正退出。窗口的 Closing 会检查 IsExiting，避免又被“关闭进托盘”拦住。</summary>
    public void RequestExit()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        Logger.Info("用户请求退出");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _tray?.Dispose();

        if (_ownsMutex && _singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 已不是互斥体所有者（例如异常路径），忽略即可。
            }
        }

        _singleInstanceMutex?.Dispose();
        Logger.Info("Vita Trace 已退出");
        base.OnExit(e);
    }

    private void InitializeCore(string[] args)
    {
        _isDemo = args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        AppPaths.EnsureCreated();
        _logger = new FileLogger(_isDemo ? AppPaths.DemoLogFile : AppPaths.LogFile);
        Logger.Info($"启动：demo={_isDemo}, args=[{string.Join(' ', args)}]");

        _settingsStore = new JsonSettingsStore(_isDemo ? AppPaths.DemoSettingsFile : AppPaths.SettingsFile);
        Settings = _settingsStore.Load();

        // 演示数据只用于 UI 验收，不能污染真实历史文件。
        _historyStore = _isDemo
            ? NullHistoryStore.Instance
            : new JsonHistoryStore(AppPaths.HistoryFile, Logger);
        if (!string.IsNullOrWhiteSpace(_settingsStore.LastLoadError))
        {
            Logger.Warn($"读取设置失败，使用默认值：{_settingsStore.LastLoadError}");
        }

        // 先应用主题/语言，后面的错误提示与托盘菜单才能用正确语言。
        Theme.Apply(Settings.Theme);
        Localization.Apply(Settings.Language);

        // 开发用只读探针：不建窗口、不写设置，只把 HID 接口和厂商查询结果写日志。
        // 正式用户不会带 --probe，因此不会看到这个模式。
        if (args.Any(a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase)))
        {
            RunProbe();
            Shutdown();
            return;
        }

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        // 绿色版被移动后，旧的启动项可能指向不存在的路径；启动时顺手修复。
        if (!_isDemo && Settings.StartWithWindows)
        {
            try
            {
                if (!StartupService.IsEnabled())
                {
                    StartupService.SetEnabled(true);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("修复开机启动项失败", ex);
            }
        }

        CreateMainWindow(args);

        // 先确保主窗口有 HWND，再用它注册托盘。独立 message-only 窗口在部分
        // Windows 11 桌面上会被 Shell 以 Access Denied 拒绝。
        var hwnd = new WindowInteropHelper(_mainWindow!).EnsureHandle();
        CreateTray(hwnd);
        Logger.Info($"托盘诊断：visible={_tray?.IsVisible}, iconHandle={_tray?.IconHandle}, error={_tray?.LastError}");
        UpdateTrayText();
        UpdateTrayStatus();

        // 只有首次注册失败时才重新声明。不要无条件 Reassert：
        // 先 NIM_DELETE 再 NIM_ADD 会让 Explorer 重建图标，部分 Win11 版本会把应用图标变成透明占位。
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_tray is { IsVisible: false })
                {
                    _tray.Reassert();
                }
            }),
            DispatcherPriority.ApplicationIdle);

        // 首次枚举放到窗口显示之后，避免启动阶段被设备 I/O 阻塞。
        _ = _viewModel!.RefreshAsync();
        Logger.Info("主窗口与托盘已就绪");
    }

    private void RunProbe()
    {
        try
        {
            var enumerator = new WindowsHidDeviceEnumerator(Logger);
            var devices = enumerator.EnumerateAsync().GetAwaiter().GetResult();
            var readers = new (string Name, Func<PeripheralDevice, Task<string>> Probe)[]
            {
                ("Rapoo", device => ProbeReaderAsync(new RapooBatteryReader(Logger), device)),
                ("MCHOSE-Keyboard", device => ProbeReaderAsync(new MchoseKeyboardBatteryReader(Logger), device)),
                ("CompX", device => new CompxBatteryReader(Logger).ProbeAsync(device)),
                ("MCHOSE", device => new MchoseBatteryReader(Logger).ProbeAsync(device))
            };

            foreach (var device in devices)
            {
                Logger.Info($"探针开始：{device.DisplayName}");
                foreach (var reader in readers)
                {
                    Logger.Info($"探针协议：{reader.Name}");
                    var report = reader.Probe(device).GetAwaiter().GetResult();
                    foreach (var line in report.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Logger.Info($"  {line.TrimEnd()}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("HID 探针失败", ex);
        }
    }

    private static async Task<string> ProbeReaderAsync(IBatteryReader reader, PeripheralDevice device)
    {
        if (!reader.CanHandle(device))
        {
            return "SKIP | CanHandle=false";
        }

        try
        {
            var reading = await reader.ReadAsync(device).ConfigureAwait(false);
            return $"{reading.Status} | {reading.Percent?.ToString() ?? "-"}% | {reading.Source} | {reading.Detail}";
        }
        catch (Exception ex)
        {
            return $"ReadFailed | - | {reader.Id} | {ex.Message}";
        }
    }

    private bool AcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            @"Local\VitaTrace.SingleInstance",
            out var createdNew);

        _ownsMutex = createdNew;
        if (createdNew)
        {
            return true;
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;

        // 已有实例可能正隐藏在托盘里。再次启动时把旧窗口叫出来，
        // 而不是只弹一句“已在运行”；这样即使托盘图标被 Explorer 吞掉也能恢复。
        var existingWindow = FindVitaWindow();
        if (existingWindow != IntPtr.Zero)
        {
            // 不能直接 ShowWindow：那只会把 Win32 窗口显示出来，
            // WPF 仍认为 Visibility=Hidden，内容区会变成黑屏。
            // 这里发私有消息，让第一个实例在它的 Dispatcher 里执行 Show()。
            _ = PostMessage(existingWindow, VitaTrace.App.MainWindow.ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
            SetForegroundWindow(existingWindow);
        }
        else
        {
            MessageBox.Show(
                Localization["AlreadyRunning"],
                "Vita Trace",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        return false;
    }

    private void CreateTray(IntPtr hwnd)
    {
        _tray = new TrayService(
            hwnd,
            "Vita Trace",
            Localization["TrayRefresh"],
            Localization["TrayExit"]);

        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.RefreshRequested += (_, _) => _ = _viewModel?.RefreshAsync();
        _tray.ExitRequested += (_, _) => RequestExit();
        _tray.DeviceSelected += (_, deviceId) =>
        {
            _viewModel?.SelectDevice(deviceId);
            ShowMainWindow();
        };

        _viewModel!.TrayStateChanged += (_, _) => SyncTray();
        SyncTray();
    }

    /// <summary>只把“本次确实读到 Ok 电量”的设备投影到托盘。</summary>
    private void SyncTray()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        var devices = _viewModel.Devices
            .Where(d => d.IsTrayVisible)
            .Select(d => new TrayDeviceState(
                d.Device.Id,
                d.Index,
                d.Name,
                d.TrayPercent))
            .ToList();

        _tray.Sync(devices);
        Logger.Info($"托盘同步：{devices.Count} 个设备，entries={_tray.EntryCount}, added={_tray.AddedCount}, visible={_tray.IsVisible}, error={_tray.LastError}");
    }

    /// <summary>别名写入设置并立即持久化；不改主题/语言，避免重命名触发整套设置重载。</summary>
    private void PersistAlias(string deviceId, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            Settings.Aliases.Remove(deviceId);
        }
        else
        {
            Settings.Aliases[deviceId] = alias.Trim();
        }

        var result = _settingsStore?.Save(Settings);
        if (result is { Success: false })
        {
            Logger.Warn($"保存设备别名失败：{result.Value.Error}");
        }
    }

    private void CreateMainWindow(string[] args)
    {
        // 真实模式从第三阶段开始接 Windows HID 枚举；演示模式仍使用固定示例数据。
        var enumerator = _isDemo
            ? new DemoDeviceEnumerator()
            : (IDeviceEnumerator)new WindowsHidDeviceEnumerator(Logger);

        _viewModel = new MainViewModel(
            enumerator,
            new IBatteryReader[]
            {
                // 通用兜底放最前：BLE 设备的系统电量属性比厂商私有协议更可靠。
                new WindowsPnpBatteryReader(),
                new RapooBatteryReader(Logger),
                new MchoseKeyboardBatteryReader(Logger),
                new CompxBatteryReader(Logger),
                new MchoseBatteryReader(Logger)
            },
            _historyStore,
            () => Settings,
            Logger,
            Localization,
            _isDemo,
            persistAlias: PersistAlias);

        _viewModel.LowBatteryDetected += OnLowBatteryDetected;

        _mainWindow = new MainWindow
        {
            DataContext = _viewModel
        };
        MainWindow = _mainWindow;

        // 开机自启时不弹窗，避免每次登录都打扰；用户点托盘图标再显示。
        var startMinimized = !_isDemo &&
                             args.Any(a =>
                                 string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        if (!startMinimized)
        {
            _mainWindow.Show();
        }
    }

    private void ShowMainWindow() => _mainWindow?.ShowFromTray();

    private void OnLowBatteryDetected(object? sender, DeviceItemViewModel device)
    {
        if (!Settings.LowBatteryNotifications)
        {
            return;
        }

        var body = Localization.Format("TrayLowBatteryBody", device.Name, device.PercentValue);
        _tray?.ShowBalloon(device.Device.Id, Localization["TrayLowBattery"], body, warning: true);

        // 托盘模式下没有任务栏按钮，FlashWindowEx 不会报错但也没有视觉效果；
        // 窗口可见（包括最小化）时才闪任务栏。
        if (_mainWindow is { IsVisible: true })
        {
            WindowEffects.FlashTaskbar(_mainWindow);
        }

        Logger.Info($"低电量提醒：{device.Name} {device.PercentValue:0}%");
    }

    private void UpdateTrayText()
    {
        _tray?.UpdateMenuText(
            Localization["TrayRefresh"],
            Localization["TrayExit"]);
    }

    private void UpdateTrayStatus() => SyncTray();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("未处理的 UI 异常", e.Exception);
        MessageBox.Show(
            Localization.Format("FatalError", e.Exception.Message),
            "Vita Trace",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Logger.Error("未处理的进程异常", e.ExceptionObject as Exception);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    /// <summary>
    /// 按标题查找已有主窗口。这里不用 FindWindow：在部分跨桌面/隐藏窗口场景中
    /// FindWindow 会返回 ERROR_INVALID_NAME，EnumWindows 更稳定。
    /// </summary>
    private static IntPtr FindVitaWindow()
    {
        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            var title = new StringBuilder(256);
            _ = GetWindowText(hWnd, title, title.Capacity);
            if (title.ToString() == "Vita Trace")
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
