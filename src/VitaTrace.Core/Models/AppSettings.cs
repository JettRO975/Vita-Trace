namespace VitaTrace.Core.Models;

/// <summary>
/// 应用设置。字段默认值按“普通用户第一次打开就能用、不打扰”的原则确定：
/// 跟随系统主题/语言、60 秒刷新、关闭窗口进托盘、开机启动默认关闭。
/// </summary>
public sealed class AppSettings
{
    /// <summary>system / zh-CN / en-US。</summary>
    public string Language { get; set; } = "system";

    /// <summary>system / light / dark / sea / dusk。默认海蓝。</summary>
    public string Theme { get; set; } = "sea";

    /// <summary>刷新间隔（秒）。默认 60；过小会增加 HID/BLE 压力，下限 15。</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>低电量阈值（百分比），默认 20。</summary>
    public int LowBatteryThreshold { get; set; } = 20;

    /// <summary>历史数据保留天数，默认 90。</summary>
    public int HistoryRetentionDays { get; set; } = 90;

    public bool StartWithWindows { get; set; }

    /// <summary>关闭主窗口时是否隐藏到托盘。默认开启，符合“常驻监控工具”的使用习惯。</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool LowBatteryNotifications { get; set; } = true;

    public string? LastSelectedDeviceId { get; set; }

    /// <summary>设备 Id → 用户别名。设备重连后 Id 稳定时别名仍有效。</summary>
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AppSettings Clone()
    {
        return new AppSettings
        {
            Language = Language,
            Theme = Theme,
            RefreshSeconds = RefreshSeconds,
            LowBatteryThreshold = LowBatteryThreshold,
            HistoryRetentionDays = HistoryRetentionDays,
            StartWithWindows = StartWithWindows,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            LowBatteryNotifications = LowBatteryNotifications,
            LastSelectedDeviceId = LastSelectedDeviceId,
            Aliases = new Dictionary<string, string>(Aliases, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// 兜底修正：手改配置文件、旧版本升级、异常值都在这里收敛，避免 UI 出现负数或 0 秒轮询。
    /// </summary>
    public void Normalize()
    {
        Language = NormalizeChoice(Language, "system", "zh-CN", "en-US");
        Theme = NormalizeChoice(Theme, "sea", "dusk", "system", "light", "dark");
        RefreshSeconds = Math.Clamp(RefreshSeconds, 15, 3600);
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 5, 50);
        HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 7, 365);
        Aliases = Aliases is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(Aliases, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeChoice(string? value, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return allowed[0];
        }

        var match = allowed.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        return match ?? allowed[0];
    }
}
