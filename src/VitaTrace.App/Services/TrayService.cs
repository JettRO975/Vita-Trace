using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace VitaTrace.App.Services;

/// <summary>
/// 多设备系统托盘。每个“能读到电量”的设备注册一个独立 Shell_NotifyIcon，
/// 圆圈显示列表编号，颜色表示电量：>=20% 绿色，<20% 红色。
///
/// 这里直接调用 Shell API，而不是 WinForms NotifyIcon：
/// 1. WinForms 的 Visible=true 不代表 Shell 真的接受了图标；
/// 2. 多图标需要自己管理 uID、图标句柄和右键菜单；
/// 3. 需要拿到明确的 Win32 错误，便于排查托盘未显示的问题。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const string AppEntryId = "__app";
    private const uint WM_TRAY = 0x8000 + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_BALLOONUSERCLICK = 0x0405;

    private readonly MenuItem _deviceHeader;
    private readonly MenuItem _refreshItem;
    private readonly MenuItem _exitItem;
    private readonly TextBlock _refreshText;
    private readonly TextBlock _exitText;
    private readonly ContextMenu _menu;
    private readonly HwndSource _source;
    private readonly bool _ownsSource;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<string, TrayEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _idsByUid = new();
    private readonly uint _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private string? _menuDeviceId;
    private uint _nextUid = 1;
    private bool _disposed;

    /// <summary>
    /// 使用主窗口的 HWND 注册托盘。不要用独立的 message-only HwndSource：
    /// 在部分 Windows 11/受限桌面环境下，Shell 会以 Access Denied 拒绝这种窗口。
    /// </summary>
    public TrayService(IntPtr hwnd, string toolTip, string refreshText, string exitText)
    {
        _deviceHeader = new MenuItem { IsEnabled = false };
        _refreshItem = new MenuItem
        {
            Header = CreateMenuHeader(
                "M17.65,6.35 C16.2,4.9 14.21,4 12,4 C7.58,4 4,7.58 4,12 C4,16.42 7.58,20 12,20 C15.73,20 18.84,17.45 19.73,14 L17.65,14 C16.83,16.33 14.61,18 12,18 C8.69,18 6,15.31 6,12 C6,8.69 8.69,6 12,6 C13.66,6 15.14,6.69 16.22,7.78 L13,11 L20,11 L20,4 Z",
                refreshText,
                out _refreshText,
                filled: true)
        };
        _exitItem = new MenuItem
        {
            Header = CreateMenuHeader(
                "M8,1.5 L8,7.5 M4.2,3.5 A6,6 0 1 0 11.8,3.5",
                exitText,
                out _exitText)
        };

        _refreshItem.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        // 左键单击托盘图标已经能打开主窗口，菜单里不再重复放“打开”。
        // 设备图标右键时第一行显示设备名，下面只保留刷新/退出两个操作。
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu.Style = FindStyle("TrayContextMenuStyle") ?? _menu.Style;
        _deviceHeader.Style = FindStyle("TrayMenuHeaderStyle") ?? _deviceHeader.Style;
        _refreshItem.Style = FindStyle("TrayMenuItemStyle") ?? _refreshItem.Style;
        _exitItem.Style = FindStyle("TrayMenuItemStyle") ?? _exitItem.Style;

        _menu.Items.Add(_deviceHeader);
        _menu.Items.Add(CreateSeparator());
        _menu.Items.Add(_refreshItem);
        _menu.Items.Add(CreateSeparator());
        _menu.Items.Add(_exitItem);

        _hwnd = hwnd;
        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
        {
            var parameters = new HwndSourceParameters("VitaTraceTray")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0
            };
            source = new HwndSource(parameters);
            _hwnd = source.Handle;
            _ownsSource = true;
        }

        _source = source;
        _source.AddHook(WndProc);

        // ^ 溢出区只保留一个应用小图标（V 的缩小版），不参与设备电量同步。
        AddAppEntry();
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? ExitRequested;

    /// <summary>点击某个设备图标时触发，参数是设备 Id。</summary>
    public event EventHandler<string>? DeviceSelected;

    public bool IsVisible => _entries.Values.Any(e => e.Added);

    public int EntryCount => _entries.Count;

    public int AddedCount => _entries.Values.Count(e => e.Added);

    public long IconHandle => _entries.Values.FirstOrDefault(e => e.Added)?.Icon.Handle.ToInt64() ?? 0;

    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// 同步托盘图标集合。只传入“读得到电量”的设备；
    /// 读不到、接收器、有线设备由调用方过滤掉。
    /// </summary>
    public void Sync(IReadOnlyList<TrayDeviceState> devices)
    {
        if (_disposed)
        {
            return;
        }

        var wanted = devices
            .Where(d => d.Percent is >= 0 and <= 100)
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var wantedIds = new HashSet<string>(wanted.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var id in _entries.Keys
                     .Where(id => id != AppEntryId && !wantedIds.Contains(id))
                     .ToList())
        {
            Remove(id);
        }

        foreach (var device in wanted)
        {
            if (_entries.TryGetValue(device.Id, out var existing))
            {
                var iconChanged = existing.Number != device.Number || existing.Percent != device.Percent;
                existing.DeviceId = device.Id;
                existing.Promoted = true;
                existing.Number = device.Number;
                existing.Percent = device.Percent;
                existing.Name = device.Name;
                if (iconChanged)
                {
                    var oldIcon = existing.Icon;
                    existing.Icon = TrayIconFactory.CreateDevice(device.Number, device.Percent);
                    existing.Data.hIcon = existing.Icon.Handle;
                    Modify(existing, NIF_ICON);
                    oldIcon.Dispose();
                }

                UpdateTip(existing, device);
                continue;
            }

            Add(device, device.Id, promoted: true);
        }
    }

    public void UpdateMenuText(string refreshText, string exitText)
    {
        _refreshText.Text = refreshText;
        _exitText.Text = exitText;
    }

    /// <summary>没有指定设备时（例如“已最小化到托盘”提示）挂到当前第一个可见图标上。</summary>
    public void ShowBalloon(string title, string body, bool warning = false)
    {
        var first = _entries.Values.FirstOrDefault(e => e.Added && e.Promoted)
                    ?? _entries.Values.FirstOrDefault(e => e.Added && e.IsApp);
        if (first is not null)
        {
            ShowBalloon(first.DeviceId, title, body, warning);
        }
    }

    public void ShowBalloon(string deviceId, string title, string body, bool warning = false)
    {
        var entry = _entries.Values.FirstOrDefault(
                        e => e.Added && e.Promoted &&
                             string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    ?? _entries.Values.FirstOrDefault(e => e.Added && e.IsApp);
        if (_disposed || entry is null)
        {
            return;
        }

        entry.Data.szInfoTitle = Trim64(title);
        entry.Data.szInfo = Trim256(body);
        entry.Data.dwInfoFlags = warning ? 0x00000003u : 0x00000001u;
        entry.Data.uTimeoutOrVersion = 5000;
        Modify(entry, NIF_INFO);
    }

    /// <summary>窗口显示/Explorer 重启后重新注册一次所有设备图标。</summary>
    public void Reassert()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var entry in _entries.Values.ToList())
        {
            Delete(entry);
        }

        foreach (var entry in _entries.Values.ToList())
        {
            AddExisting(entry);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _entries.Values.ToList())
        {
            Delete(entry);
            entry.Icon.Dispose();
        }

        _entries.Clear();
        _idsByUid.Clear();
        _source.RemoveHook(WndProc);
        if (_ownsSource)
        {
            _source.Dispose();
        }
    }

    private void AddAppEntry()
    {
        var entry = new TrayEntry
        {
            Id = AppEntryId,
            DeviceId = string.Empty,
            Promoted = false,
            IsApp = true,
            Uid = _nextUid++,
            Icon = TrayIconFactory.CreateApp()
        };

        entry.Data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = entry.Uid,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = entry.Icon.Handle,
            szTip = "Vita Trace",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        _entries[AppEntryId] = entry;
        _idsByUid[entry.Uid] = AppEntryId;
        AddExisting(entry);
    }

    private void Add(TrayDeviceState device, string key, bool promoted)
    {
        var entry = new TrayEntry
        {
            Id = key,
            DeviceId = device.Id,
            Promoted = promoted,
            Uid = _nextUid++,
            Number = device.Number,
            Percent = device.Percent,
            Name = device.Name,
            Icon = TrayIconFactory.CreateDevice(device.Number, device.Percent)
        };

        entry.Data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = entry.Uid,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = entry.Icon.Handle,
            szTip = Trim(TipFor(device, promoted)),
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        _entries[entry.Id] = entry;
        _idsByUid[entry.Uid] = entry.Id;
        AddExisting(entry);
        UpdateTip(entry, device);
    }

    private void AddExisting(TrayEntry entry)
    {
        entry.Added = Shell_NotifyIcon(NIM_ADD, ref entry.Data);
        if (!entry.Added)
        {
            LastError = $"err={Marshal.GetLastWin32Error()} hwnd=0x{_hwnd.ToInt64():X} cbSize={entry.Data.cbSize}";
            return;
        }

        // 不调用 NIM_SETVERSION：版本 4 会把回调参数改成 NIN_* 语义，
        // 而本类的 WndProc 按经典 WM_LBUTTONUP/WM_RBUTTONUP 处理。
        // 之前在版本 4 下右键没有反应，正是这个原因；托盘图标显示也保持最兼容的经典模式。
        LastError = string.Empty;

        // 应用图标（V）完全不碰 Windows 11 的通知区域注册表。
        // 实测在 NIM_ADD 之后改 IsPromoted，Explorer 会重新加载该条目，
        // 应用图标会变成“占位但透明”；设备圆环需要固定显示，才走 TryPromote。
        if (!entry.IsApp)
        {
            TryPromote(entry.Data.szTip, entry.Promoted);
        }
    }

    private static void TryPromote(string tooltip, bool promoted)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings",
                writable: true);

            if (root is null)
            {
                return;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                using var item = root.OpenSubKey(name, writable: true);
                if (item?.GetValue("InitialTooltip") is not string current ||
                    !string.Equals(current, tooltip, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!promoted)
                {
                    // 应用图标留在 ^：删除“固定显示”值，让 Windows 使用默认的溢出区分类。
                    // 显式写 0 在部分 Win11 版本会被当成“完全隐藏”，反而连 ^ 都不显示。
                    if (item.GetValue("IsPromoted") is not null)
                    {
                        item.DeleteValue("IsPromoted", throwOnMissingValue: false);
                    }

                    continue;
                }

                if (item.GetValue("IsPromoted") is not int currentValue || currentValue != 1)
                {
                    item.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                }
            }
        }
        catch
        {
            // 托盘图标已经注册成功；固定显示是增强项，注册表被策略锁住时不影响主功能。
        }
    }

    private void Remove(string id)
    {
        if (id == AppEntryId)
        {
            return;
        }

        if (!_entries.Remove(id, out var entry))
        {
            return;
        }

        Delete(entry);
        _idsByUid.Remove(entry.Uid);
        entry.Icon.Dispose();
    }

    private void Delete(TrayEntry entry)
    {
        if (!entry.Added)
        {
            return;
        }

        entry.Data.uFlags = 0;
        Shell_NotifyIcon(NIM_DELETE, ref entry.Data);
        entry.Added = false;
    }

    private void Modify(TrayEntry entry, uint flags)
    {
        if (!entry.Added)
        {
            return;
        }

        entry.Data.uFlags = flags;
        Shell_NotifyIcon(NIM_MODIFY, ref entry.Data);
    }

    private static void UpdateTip(TrayEntry entry, TrayDeviceState device)
    {
        entry.Data.szTip = Trim(TipFor(device, entry.Promoted));
        if (entry.Added)
        {
            entry.Data.uFlags = NIF_TIP;
            Shell_NotifyIcon(NIM_MODIFY, ref entry.Data);
        }

        TryPromote(entry.Data.szTip, entry.Promoted);
    }

    private static string TipFor(TrayDeviceState device, bool promoted)
        => $"{device.Number}. {device.Name} — {device.Percent}%";

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Explorer 重启后会广播 TaskbarCreated，之前注册的图标全部失效，
        // 必须重新 NIM_ADD；否则任务栏看起来“应用还在，但托盘图标消失”。
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            Reassert();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_TRAY)
        {
            return IntPtr.Zero;
        }

        var uid = (uint)wParam.ToInt64();
        if (uid == 0)
        {
            uid = (uint)((lParam.ToInt64() >> 16) & 0xFFFF);
        }

        if (!_idsByUid.TryGetValue(uid, out var entryId) ||
            !_entries.TryGetValue(entryId, out var entry))
        {
            return IntPtr.Zero;
        }

        var code = (int)(lParam.ToInt64() & 0xFFFF);
        switch (code)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
            case NIN_SELECT:
            case NIN_BALLOONUSERCLICK:
                if (!entry.IsApp)
                {
                    DeviceSelected?.Invoke(this, entry.DeviceId);
                }

                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;

            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                _menuDeviceId = entry.IsApp ? null : entry.DeviceId;
                _deviceHeader.Header = BuildHeader(entry);
                // 托盘回调发生时窗口通常不在前台；不抢前台，WPF ContextMenu 可能一打开就立刻被关闭。
                SetForegroundWindow(_hwnd);
                _menu.Placement = PlacementMode.MousePoint;
                _menu.IsOpen = true;
                _menu.Focus();
                break;
        }

        handled = true;
        return IntPtr.Zero;
    }

    private static object CreateMenuHeader(
        string geometry,
        string text,
        out TextBlock label,
        bool filled = false)
    {
        var icon = new Path
        {
            Data = Geometry.Parse(geometry),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (filled)
        {
            // Material 的 refresh 路径是填充图形，直接填充比描边更清晰。
            icon.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
        }
        else
        {
            icon.StrokeThickness = 1.6;
            icon.StrokeStartLineCap = PenLineCap.Round;
            icon.StrokeEndLineCap = PenLineCap.Round;
            icon.StrokeLineJoin = PenLineJoin.Round;
            icon.SetResourceReference(Shape.StrokeProperty, "TextPrimaryBrush");
        }

        label = new TextBlock
        {
            Text = text,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(icon);
        panel.Children.Add(label);
        return panel;
    }

    private static object BuildHeader(TrayEntry entry)
    {
        if (entry.IsApp)
        {
            return new TextBlock
            {
                Text = "Vita Trace",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
        }

        // 设备图标：先显示应用名，再显示设备名和电量，避免只有“Vita Trace”看不出是哪台设备。
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Vita Trace",
            FontSize = 9,
            Opacity = 0.65
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{entry.Number}. {entry.Name} · {entry.Percent}%",
            Margin = new Thickness(0, 1, 0, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        return panel;
    }

    private static Style? FindStyle(string key)
        => Application.Current?.TryFindResource(key) as Style;

    private static Separator CreateSeparator()
        => new() { Style = FindStyle("TrayMenuSeparatorStyle") ?? new Style(typeof(Separator)) };

    private static string Trim(string text)
        => text.Length <= 63 ? text : text[..63];

    private static string Trim64(string text)
        => text.Length <= 63 ? text : text[..63];

    private static string Trim256(string text)
        => text.Length <= 255 ? text : text[..255];

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private sealed class TrayEntry
    {
        public required string Id { get; init; }
        public required string DeviceId { get; set; }
        public bool Promoted { get; set; }
        public bool IsApp { get; init; }
        public required uint Uid { get; init; }
        public required Icon Icon { get; set; }
        // 必须是字段：ref entry.Data 才能把修改写回结构体；属性会返回副本。
        public NOTIFYICONDATA Data;
        public int Number { get; set; }
        public int Percent { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Added { get; set; }
    }
}

/// <summary>托盘需要的最小设备信息；由 App 从 MainViewModel 投影。</summary>
public sealed record TrayDeviceState(string Id, int Number, string Name, int Percent);
