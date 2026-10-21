using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VitaTrace.App.Services;

/// <summary>
/// 窗口外观相关的 Win32 调用。
/// 这些调用全部是“可选增强”：失败时只影响圆角/闪烁，不影响功能。
/// </summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Win11 圆角 + 深色边框；Win10 上调用失败会被忽略。</summary>
    public static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var darkValue = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));

        var corner = DwmwcpRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    /// <summary>窗口可见时闪任务栏；托盘模式下没有任务栏按钮，调用无效果但不报错。</summary>
    public static void FlashTaskbar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        const uint flashAll = 0x00000003;
        const uint flashTimerNoForeground = 0x0000000C;
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Hwnd = handle,
            Flags = flashAll | flashTimerNoForeground,
            Count = 3,
            Timeout = 0
        };

        _ = FlashWindowEx(ref info);
    }
}
