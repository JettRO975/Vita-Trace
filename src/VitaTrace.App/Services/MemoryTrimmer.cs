using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VitaTrace.App.Services;

/// <summary>
/// 长驻托盘应用的轻量内存维护：把当前进程的工作集交还给系统。
/// 只降低“工作集”占用，不改变托管堆里的对象；因此不会影响功能，
/// 代价是下次访问被换出的页面时可能有一次很短的缺页。
/// </summary>
internal static class MemoryTrimmer
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// 在窗口隐藏、启动稳定后调用。吞掉异常：内存整理失败不应该影响主流程。
    /// </summary>
    public static void Trim()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // 诊断性操作，失败静默忽略。
        }
    }
}
