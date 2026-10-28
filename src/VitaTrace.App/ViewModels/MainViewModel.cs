using VitaTrace.App.Mvvm;
using VitaTrace.App.Services;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;

namespace VitaTrace.App.ViewModels;

/// <summary>
/// 主界面状态。职责：
/// 1. 定时/手动刷新设备列表；
/// 2. 合并新旧设备，保持选中项与历史；
/// 3. 把电量读取结果映射成展示状态；
/// 4. 维护低电量聚合状态（托盘与横幅共用）。
///
/// 它不直接操作窗口、托盘或注册表，这些由 App/MainWindow 负责，便于测试。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceEnumerator _enumerator;
    private readonly IReadOnlyList<IBatteryReader> _readers;
    private readonly IHistoryStore _history;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly IAppLogger _logger;
    private readonly LocalizationService _l10n;
    private readonly Action<string, string?>? _persistAlias;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _lowBatteryNotified = new(StringComparer.OrdinalIgnoreCase);

    private DeviceItemViewModel? _selectedDevice;
    private DateTimeOffset _nextRefresh;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastUpdated;
    private bool _isRefreshing;
    private string? _statusMessage;
    private bool _isRenaming;
    private string _editingName = string.Empty;
    private bool _disposed;

    public MainViewModel(
        IDeviceEnumerator enumerator,
        IReadOnlyList<IBatteryReader> readers,
        IHistoryStore history,
        Func<AppSettings> settingsProvider,
        IAppLogger logger,
        LocalizationService localization,
        bool isDemo,
        Action<string, string?>? persistAlias = null)
    {
        _enumerator = enumerator;
        _readers = readers;
        _history = history;
        _settingsProvider = settingsProvider;
        _logger = logger;
        _l10n = localization;
        _persistAlias = persistAlias;
        IsDemo = isDemo;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing);
        RefreshCommand.Failed += exception =>
        {
            _logger.Error("刷新失败", exception);
            StatusMessage = exception.Message;
        };

        _l10n.LanguageChanged += (_, _) => OnLanguageChanged();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _nextRefresh = DateTimeOffset.Now.AddSeconds(2);
        _timer.Start();
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsDemo { get; }

    public event EventHandler<DeviceItemViewModel>? LowBatteryDetected;

    /// <summary>设备列表或任一设备的电量/名称变化，App 据此重画托盘图标。</summary>
    public event EventHandler? TrayStateChanged;

    public DeviceItemViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                if (_isRenaming && value is null)
                {
                    CancelRename();
                }

                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(EmptyTitle));
                OnPropertyChanged(nameof(EmptyHint));
                TrayStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(RefreshButtonText));
            }
        }
    }

    public string RefreshButtonText => IsRefreshing ? _l10n["Refreshing"] : _l10n["Refresh"];

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasDevices => Devices.Count > 0;

    public bool HasSelection => SelectedDevice is not null;

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    public string EditingName
    {
        get => _editingName;
        set => SetProperty(ref _editingName, value);
    }

    public string DeviceCountText => _l10n.Format("DeviceCount", Devices.Count);

    /// <summary>左侧导航上的精简设备数徽标。</summary>
    public string DeviceBadgeText => Devices.Count.ToString();

    public string LastUpdatedText => _lastUpdated is { } time
        ? _l10n.Format("LastUpdated", time.LocalDateTime.ToString("HH:mm:ss"))
        : _l10n["NotAvailable"];

    public DeviceItemViewModel? LowBatteryDevice => Devices.FirstOrDefault(d => d.IsLowBattery);

    public bool HasLowBattery => LowBatteryDevice is not null;

    public string LowBatteryText
    {
        get
        {
            var device = LowBatteryDevice;
            return device is null
                ? string.Empty
                : _l10n.Format("LowBatteryMessage", device.Name, device.PercentValue);
        }
    }

    public string EmptyTitle => HasDevices ? _l10n["SelectDevice"] : _l10n["NoDevices"];

    public string EmptyHint => HasDevices ? _l10n["EmptySelectionHint"] : _l10n["NoDevicesHint"];

    public async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            PruneHistoryIfNeeded();
            var devices = await _enumerator.EnumerateAsync();
            MergeDevices(devices);

            if (!IsDemo)
            {
                await ReadBatteriesAsync(devices);
            }

            // 只有明显变慢时才写日志，避免每分钟刷新把日志刷满。
            if (stopwatch.ElapsedMilliseconds > 1500)
            {
                _logger.Info($"刷新耗时 {stopwatch.ElapsedMilliseconds} ms，设备 {devices.Count} 个");
            }

            _lastUpdated = DateTimeOffset.Now;
            OnPropertyChanged(nameof(LastUpdatedText));
            StatusMessage = null;
            UpdateLowBatteryState();
            TrayStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error("枚举设备失败", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            _nextRefresh = DateTimeOffset.Now.AddSeconds(Math.Max(15, _settingsProvider().RefreshSeconds));
        }
    }

    /// <summary>设置变更后刷新阈值、间隔与文案；不重置历史。</summary>
    public void OnSettingsChanged()
    {
        var settings = _settingsProvider();
        foreach (var device in Devices)
        {
            device.Update(device.Device, settings);
        }

        _nextRefresh = DateTimeOffset.Now.AddSeconds(Math.Max(15, settings.RefreshSeconds));
        UpdateLowBatteryState();
        OnPropertyChanged(nameof(RefreshButtonText));
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>托盘图标点击后按设备 Id 选中；设备可能已被拔出，找不到时忽略。</summary>
    public void SelectDevice(string deviceId)
    {
        var device = Devices.FirstOrDefault(d =>
            string.Equals(d.Device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is not null)
        {
            SelectedDevice = device;
        }
    }

    public void BeginRename()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        EditingName = SelectedDevice.Name;
        IsRenaming = true;
    }

    public void CommitRename()
    {
        if (!IsRenaming || SelectedDevice is null)
        {
            return;
        }

        RenameDevice(SelectedDevice, EditingName);
        IsRenaming = false;
    }

    public void CancelRename()
    {
        IsRenaming = false;
        EditingName = string.Empty;
    }

    /// <summary>别名保存后立即刷新列表/托盘 tooltip；空值表示恢复系统名称。</summary>
    public void RenameDevice(DeviceItemViewModel device, string? alias)
    {
        device.SetAlias(alias);
        _persistAlias?.Invoke(device.Device.Id, string.IsNullOrWhiteSpace(alias) ? null : alias.Trim());
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnLanguageChanged()
    {
        foreach (var device in Devices)
        {
            device.RefreshLocalized();
        }

        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(DeviceBadgeText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(RefreshButtonText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(LowBatteryText));
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.Now < _nextRefresh)
        {
            return;
        }

        _nextRefresh = DateTimeOffset.Now.AddSeconds(Math.Max(15, _settingsProvider().RefreshSeconds));
        _ = RefreshAsync();
    }

    private void MergeDevices(IReadOnlyList<PeripheralDevice> devices)
    {
        var selectedId = SelectedDevice?.Device.Id;
        var existing = Devices.ToDictionary(d => d.Device.Id, StringComparer.OrdinalIgnoreCase);
        var merged = new List<DeviceItemViewModel>(devices.Count);

        foreach (var device in devices)
        {
            if (existing.TryGetValue(device.Id, out var item))
            {
                item.Update(device, _settingsProvider());
            }
            else
            {
                var settings = _settingsProvider();
                item = new DeviceItemViewModel(
                    device,
                    settings,
                    _l10n,
                    persistSample: (id, sample) => _history.Append(id, sample));

                if (IsDemo)
                {
                    item.SeedDemoHistory();
                }
                else
                {
                    // 设备 ID 在重启后保持稳定，历史才能在下次启动时接上。
                    item.LoadHistory(_history.GetSamples(
                        device.Id,
                        DateTimeOffset.Now.AddDays(-settings.HistoryRetentionDays)));
                }
            }

            merged.Add(item);
        }

        // 接收器是“通道”而不是用户最关心的外设，统一排到列表末尾；
        // 首次进入时优先选中真正有电量的设备，避免一打开就是接收器的空圆环。
        var ordered = merged
            .OrderBy(d => d.Device.Kind == DeviceKind.Receiver ? 1 : 0)
            .ToList();

        Devices.Clear();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            item.Index = i + 1;
            Devices.Add(item);
        }

        var selection = selectedId is null
            ? null
            : ordered.FirstOrDefault(d => string.Equals(d.Device.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        SelectedDevice = selection
            ?? ordered.FirstOrDefault(d => d.HasPercent)
            ?? ordered.FirstOrDefault();

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(DeviceBadgeText));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 真实模式：按顺序尝试所有能处理该设备的读取器。
    /// Phase 2 还没有任何读取器，所以设备状态保持“未知”，不会显示假电量。
    /// </summary>
    private async Task ReadBatteriesAsync(IReadOnlyList<PeripheralDevice> devices)
    {
        _logger.Info($"电量读取开始：{devices.Count} 个设备");

        foreach (var device in devices)
        {
            if (!device.IsConnected ||
                device.Kind == DeviceKind.Receiver ||
                device.Connection == ConnectionKind.Wired)
            {
                continue;
            }

            var item = Devices.FirstOrDefault(d => string.Equals(d.Device.Id, device.Id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                continue;
            }

            // 这组日志就是用户反馈兼容性问题时要附上的“协议现场”：
            // VID/PID、接口路径、每个 reader 的尝试与结果，都在同一份 app.log 里。
            _logger.Info(
                $"  设备：{device.EffectiveName} | VID_{device.VendorId:X4} PID_{device.ProductId:X4} | " +
                $"{device.Kind}/{device.Connection} | status={device.BatteryStatus} | " +
                $"source={device.Battery?.Source.ToString() ?? "none"} | path={device.DevicePath}");

            foreach (var reader in _readers)
            {
                if (!reader.CanHandle(device))
                {
                    continue;
                }

                _logger.Info($"    协议尝试：{reader.Id}");

                try
                {
                    var reading = await ReadWithTimeoutAsync(reader, device, TimeSpan.FromSeconds(6));
                    _logger.Info(
                        $"    协议结果：{reader.Id} -> {reading.Status} " +
                        $"{(reading.Percent?.ToString() ?? "-")}% | source={reading.Source} | " +
                        $"detail={reading.Detail ?? "-"} | {reading.UpdatedAt:O}");

                    // 本轮没读到、但 10 分钟内有过有效读数：保留旧值并标记 Stale。
                    // 不把旧值伪装成实时值，也不因为一次休眠就显示“读取失败”。
                    var previous = item.Device.Battery;
                    if (reading.Percent is null &&
                        previous is { Percent: not null } &&
                        previous.UpdatedAt > DateTimeOffset.Now.AddMinutes(-10) &&
                        previous.Status is BatteryStatus.Ok or BatteryStatus.Stale)
                    {
                        item.ApplyReading(previous.AsStale("本轮未读到新值，显示上次成功读数"));
                        continue;
                    }

                    item.ApplyReading(reading);
                    if (reading.Percent is not null)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"读取电量失败：{device.DisplayName} / {reader.Id}", ex);
                    item.ApplyReading(BatteryReading.ReadFailed(ex.Message));
                }
            }
        }

        _logger.Info("电量读取结束");
    }

    /// <summary>
    /// 单个读取器最多等 6 秒。底层 HID 读取有些实现无法取消，
    /// 但至少不会让一次刷新被休眠设备拖到几十秒。
    /// </summary>
    private static async Task<BatteryReading> ReadWithTimeoutAsync(
        IBatteryReader reader,
        PeripheralDevice device,
        TimeSpan timeout)
    {
        var readTask = reader.ReadAsync(device);
        var completed = await Task.WhenAny(readTask, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != readTask)
        {
            return BatteryReading.ReadFailed($"读取超时（{timeout.TotalSeconds:0} 秒）");
        }

        return await readTask.ConfigureAwait(false);
    }

    private void UpdateLowBatteryState()
    {
        var lowDevices = Devices.Where(d => d.IsLowBattery).ToList();
        OnPropertyChanged(nameof(LowBatteryDevice));
        OnPropertyChanged(nameof(HasLowBattery));
        OnPropertyChanged(nameof(LowBatteryText));

        var currentIds = new HashSet<string>(
            lowDevices.Select(d => d.Device.Id),
            StringComparer.OrdinalIgnoreCase);

        // 设备恢复电量后要移除提醒标记，否则下次再掉到阈值以下就不会提醒。
        _lowBatteryNotified.IntersectWith(currentIds);

        foreach (var device in lowDevices)
        {
            if (_lowBatteryNotified.Add(device.Device.Id))
            {
                LowBatteryDetected?.Invoke(this, device);
            }
        }
    }

    /// <summary>每小时最多清理一次旧历史，避免每次刷新都重写 JSON 文件。</summary>
    private void PruneHistoryIfNeeded()
    {
        if (IsDemo)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (now - _lastPrune < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastPrune = now;
        _history.Prune(now.AddDays(-_settingsProvider().HistoryRetentionDays));
    }
}
