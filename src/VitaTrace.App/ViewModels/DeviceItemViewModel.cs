using VitaTrace.App.Mvvm;
using VitaTrace.App.Services;
using VitaTrace.Core.Models;

namespace VitaTrace.App.ViewModels;

/// <summary>
/// 设备卡片的展示模型。所有“给人看的文字”都在这里根据当前语言生成，
/// XAML 只做绑定，避免在界面里散落 if/switch。
/// </summary>
public sealed class DeviceItemViewModel : ObservableObject
{
    private readonly LocalizationService _l10n;
    private readonly Action<string, BatterySample>? _persistSample;
    private PeripheralDevice _device;
    private AppSettings _settings;
    private int _index;

    public DeviceItemViewModel(
        PeripheralDevice device,
        AppSettings settings,
        LocalizationService localization,
        Action<string, BatterySample>? persistSample = null)
    {
        _device = device;
        _settings = settings;
        _l10n = localization;
        _persistSample = persistSample;
        ApplyAlias(settings);
    }

    /// <summary>设备在左侧列表中的 1-based 序号，同时用于托盘图标里的数字。</summary>
    public int Index
    {
        get => _index;
        set
        {
            if (SetProperty(ref _index, value))
            {
                OnPropertyChanged(nameof(NumberText));
            }
        }
    }

    public string NumberText => _index > 0 ? _index.ToString() : "—";

    public PeripheralDevice Device => _device;

    public ObservableCollection<BatterySample> History { get; } = new();

    public string Name => _device.EffectiveName;

    public string KindText => _device.Kind switch
    {
        DeviceKind.Keyboard => _l10n["KindKeyboard"],
        DeviceKind.Mouse => _l10n["KindMouse"],
        DeviceKind.Headset => _l10n["KindHeadset"],
        DeviceKind.Gamepad => _l10n["KindGamepad"],
        DeviceKind.Receiver => _l10n["KindReceiver"],
        DeviceKind.Other => _l10n["KindOther"],
        _ => _l10n["KindUnknown"]
    };

    public string ConnectionText => _device.Connection switch
    {
        ConnectionKind.UsbReceiver => _l10n["ConnectionUsbReceiver"],
        ConnectionKind.Bluetooth => _l10n["ConnectionBluetooth"],
        ConnectionKind.Wired => _l10n["ConnectionWired"],
        _ => _l10n["ConnectionUnknown"]
    };

    /// <summary>接收器只显示“接收器”，不再重复“2.4G 接收器”。</summary>
    public string Subtitle => _device.Kind == DeviceKind.Receiver
        ? KindText
        : $"{KindText} · {ConnectionText}";

    public bool HasPercent => _device.Battery?.Percent is int percent
                              && percent is >= 0 and <= 100
                              && _device.BatteryStatus is BatteryStatus.Ok
                                  or BatteryStatus.Stale
                                  or BatteryStatus.Disconnected;

    public double PercentValue => HasPercent ? _device.Battery!.Percent!.Value : 0;

    public string BatteryText => HasPercent ? $"{PercentValue:0}%" : StatusText;

    public string RingText => HasPercent ? $"{PercentValue:0}%" : "—";

    public string RingCaption => HasPercent ? _l10n["Battery"] : StatusText;

    public Brush RingBrush => HasPercent ? BatteryPalette.For(PercentValue) : BatteryPalette.Unknown;

    public string StatusText => _device.BatteryStatus switch
    {
        BatteryStatus.Ok => _l10n["StatusOk"],
        BatteryStatus.Stale => _l10n["StatusStale"],
        BatteryStatus.ReadFailed => _l10n["StatusReadFailed"],
        BatteryStatus.Unsupported => _l10n["StatusUnsupported"],
        BatteryStatus.Disconnected => _l10n["StatusDisconnected"],
        BatteryStatus.ReceiverOffline => _l10n["StatusReceiverOffline"],
        BatteryStatus.Wired => _l10n["StatusWired"],
        _ => _l10n["StatusUnknown"]
    };

    public Brush StatusBrush => _device.BatteryStatus switch
    {
        BatteryStatus.Ok => BatteryPalette.Good,
        BatteryStatus.Stale => BatteryPalette.Medium,
        BatteryStatus.ReadFailed => BatteryPalette.Low,
        BatteryStatus.ReceiverOffline => BatteryPalette.Low,
        BatteryStatus.Wired => BatteryPalette.Wired,
        _ => BatteryPalette.Unknown
    };

    public string SourceText => _device.Battery?.Source switch
    {
        BatterySource.WindowsPnp => _l10n["SourcePnp"],
        BatterySource.BleGatt => _l10n["SourceBleGatt"],
        BatterySource.LogitechHidpp => _l10n["SourceHidpp"],
        BatterySource.CompxHid => _l10n["SourceCompxHid"],
        BatterySource.MchoseHid => _l10n["SourceMchoseHid"],
        BatterySource.RapooHid => _l10n["SourceRapooHid"],
        BatterySource.StandardHid => _l10n["SourceStandardHid"],
        BatterySource.Manual => _l10n["SourceManual"],
        _ => _l10n["SourceNone"]
    };

    public string ConfidenceText => _device.Battery?.Confidence switch
    {
        BatteryConfidence.High => _l10n["ConfidenceHigh"],
        BatteryConfidence.Medium => _l10n["ConfidenceMedium"],
        BatteryConfidence.Low => _l10n["ConfidenceLow"],
        _ => _l10n["ConfidenceUnknown"]
    };

    public string UpdatedText => _device.Battery is { } battery
        ? _l10n.Format("LastUpdated", battery.UpdatedAt.LocalDateTime.ToString("HH:mm:ss"))
        : _l10n["NotAvailable"];

    /// <summary>
    /// 状态说明。优先使用读取器给出的具体原因；没有时按状态给通用说明。
    /// 这里不生成任何“猜测性”描述。
    /// </summary>
    public string HintText
    {
        get
        {
            if (_device.BatteryStatus == BatteryStatus.Ok &&
                !string.IsNullOrWhiteSpace(_device.Battery?.Detail))
            {
                return _device.Battery!.Detail!;
            }

            return _device.BatteryStatus switch
            {
                BatteryStatus.Ok => _l10n["StatusOk"],
                BatteryStatus.Stale => _l10n["StaleHint"],
                BatteryStatus.ReadFailed => _l10n["ReadFailedHint"],
                BatteryStatus.Unsupported => _l10n["UnsupportedHint"],
                BatteryStatus.Disconnected => _l10n["DisconnectedHint"],
                BatteryStatus.ReceiverOffline => _l10n["ReceiverOfflineHint"],
                BatteryStatus.Wired => _device.Kind == DeviceKind.Receiver
                    ? _l10n["ReceiverNoBattery"]
                    : _l10n["WiredHint"],
                _ => _l10n["UnknownHint"]
            };
        }
    }

    public string ModelText => string.IsNullOrWhiteSpace(_device.Model)
        ? _l10n["NotAvailable"]
        : _device.Model!;

    public string VendorText => string.IsNullOrWhiteSpace(_device.Manufacturer)
        ? _l10n["NotAvailable"]
        : _device.Manufacturer!;

    public string VidPidText => _device.VendorId is { } vid && _device.ProductId is { } pid
        ? $"VID_{vid:X4} / PID_{pid:X4}"
        : _l10n["NotAvailable"];

    public string ReceiverText => _device.Kind == DeviceKind.Receiver
        ? _l10n["NotAvailable"]
        : !string.IsNullOrWhiteSpace(_device.ReceiverName)
            ? _device.ReceiverName!
            : _l10n["NotAvailable"];

    public string NotesText => string.IsNullOrWhiteSpace(_device.Notes)
        ? _l10n["NotAvailable"]
        : _device.Notes!;

    /// <summary>
    /// 是否出现在托盘：只有本次真正读到 Ok 电量、且已连接的设备。
    /// 接收器、有线、读取失败、Stale、断开都不显示，避免出现无意义的灰点。
    /// </summary>
    public bool IsTrayVisible =>
        _device.IsConnected &&
        _device.Kind != DeviceKind.Receiver &&
        _device.BatteryStatus == BatteryStatus.Ok &&
        _device.Battery?.Percent is int percent &&
        percent is >= 0 and <= 100;

    public int TrayPercent => IsTrayVisible ? (int)PercentValue : 0;

    public bool IsLowBattery =>
        HasPercent &&
        _device.IsConnected &&
        _device.Kind != DeviceKind.Receiver &&
        PercentValue < _settings.LowBatteryThreshold;

    public bool HasHistory => History.Count > 0;

    public string HistoryRangeText => _l10n.Format("ChartRange", History.Count);

    public void Update(PeripheralDevice device, AppSettings settings)
    {
        _device = device;
        _settings = settings;
        ApplyAlias(settings);
        // 设备对象整体替换，所有派生属性都可能变化。
        OnPropertyChanged(null);
    }

    /// <summary>保存/清除用户别名。空字符串表示恢复系统名称。</summary>
    public void SetAlias(string? alias)
    {
        _device.UserAlias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        OnPropertyChanged(null);
    }

    private void ApplyAlias(AppSettings settings)
    {
        _device.UserAlias = settings.Aliases.TryGetValue(_device.Id, out var alias) &&
                            !string.IsNullOrWhiteSpace(alias)
            ? alias
            : null;
    }

    public void ApplyReading(BatteryReading reading)
    {
        _device.Battery = reading;
        _device.BatteryStatus = reading.Status;

        // 只有本次真正读到的 Ok 读数才写入历史；Stale 是旧时间戳，
        // 再写一次会造成曲线时间倒序或重复点。
        if (reading.Percent is int percent && reading.Status == BatteryStatus.Ok)
        {
            AddHistory(percent, reading.UpdatedAt, force: false);
        }

        OnPropertyChanged(null);
    }

    public void RefreshLocalized() => OnPropertyChanged(null);

    public void AddHistory(int? percent, DateTimeOffset timestamp, bool force)
    {
        if (!force && History.Count > 0)
        {
            var last = History[^1];
            // 电量没变且间隔小于 10 分钟时不再堆点，避免 90 天历史被噪音撑爆。
            if (last.Percent == percent && timestamp - last.Timestamp < TimeSpan.FromMinutes(10))
            {
                return;
            }
        }

        var sample = new BatterySample(timestamp, percent);
        History.Add(sample);
        while (History.Count > 60)
        {
            History.RemoveAt(0);
        }

        // 只有运行期新产生的采样点才落盘；加载旧历史和演示种子不会触发写入。
        _persistSample?.Invoke(_device.Id, sample);
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryRangeText));
    }

    /// <summary>
    /// 启动时加载持久化历史。只替换显示集合，不写回存储，避免“加载又保存”的循环。
    /// </summary>
    public void LoadHistory(IEnumerable<BatterySample> samples)
    {
        History.Clear();
        foreach (var sample in samples.OrderBy(s => s.Timestamp).TakeLast(60))
        {
            History.Add(sample);
        }

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryRangeText));
    }

    /// <summary>
    /// 仅用于演示：预置一段平滑曲线，让“历史曲线”在第二阶段就能被看到。
    /// 这些点不会写入真实历史文件。
    /// </summary>
    public void SeedDemoHistory()
    {
        if (_device.Battery?.Percent is not int percent)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var seed = Math.Abs(_device.Id.Length % 17);
        for (var i = 0; i < 16; i++)
        {
            var timestamp = now.AddMinutes(-(15 - i) * 6);
            var wave = Math.Sin((i + seed) * 0.65) * 3.2;
            var value = i == 15 ? percent : Math.Clamp((int)Math.Round(percent + wave), 1, 100);
            History.Add(new BatterySample(timestamp, value));
        }

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryRangeText));
    }
}
