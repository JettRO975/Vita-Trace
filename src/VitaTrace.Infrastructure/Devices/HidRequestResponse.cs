using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// HID 请求/响应传输的公共部分。
///
/// 这里只做“发一帧、等一帧”，不解释协议。所有读取都有超时：
/// HID 设备休眠时同步 ReadFile 可能永久阻塞，不能让刷新线程被拖死。
/// </summary>
internal static class HidRequestResponse
{
    /// <summary>以只读共享方式打开接口，用于读取 Caps；失败返回无效句柄。</summary>
    internal static SafeFileHandle OpenReadOnly(string path)
    {
        return HidNativeMethods.CreateFileW(
            path,
            HidNativeMethods.GenericRead,
            HidNativeMethods.FileShareReadWrite,
            IntPtr.Zero,
            HidNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
    }

    /// <summary>以读写共享方式打开厂商集合；失败返回无效句柄。</summary>
    internal static SafeFileHandle OpenReadWrite(string path)
    {
        return HidNativeMethods.CreateFileW(
            path,
            HidNativeMethods.GenericRead | HidNativeMethods.GenericWrite,
            HidNativeMethods.FileShareReadWrite,
            IntPtr.Zero,
            HidNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
    }

    /// <summary>
    /// 发送 Output Report。优先 HidD_SetOutputReport；部分固件只接受 WriteFile，
    /// 因此保留回退。返回 false 时不代表设备一定不支持，调用方应记录 Win32 错误。
    /// </summary>
    internal static bool TrySend(SafeFileHandle handle, byte[] report)
    {
        if (HidNativeMethods.HidD_SetOutputReport(handle, report, report.Length))
        {
            return true;
        }

        return HidNativeMethods.WriteFile(handle, report, report.Length, out _, IntPtr.Zero);
    }

    /// <summary>
    /// 读取一帧输入报告。独立后台线程 + Join 超时；超时后调用方关闭句柄，
    /// 内核会取消挂起的 I/O。
    /// </summary>
    internal static byte[]? TryRead(SafeFileHandle handle, int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return null;
        }

        byte[]? received = null;
        var reader = new Thread(() =>
        {
            try
            {
                var buffer = new byte[length];
                if (HidNativeMethods.ReadFile(
                        handle,
                        buffer,
                        buffer.Length,
                        out var bytesRead,
                        IntPtr.Zero) &&
                    bytesRead > 0)
                {
                    received = bytesRead == buffer.Length ? buffer : buffer[..bytesRead];
                }
            }
            catch
            {
                // 设备拔出/句柄关闭时由超时路径收尾。
            }
        })
        {
            IsBackground = true,
            Name = "vitatrace-hid-read"
        };

        reader.Start();
        if (!reader.Join(timeoutMs))
        {
            return null;
        }

        return received;
    }

    /// <summary>从输入帧中去掉可选的 Report ID；只在长度足够时使用。</summary>
    internal static ReadOnlySpan<byte> StripReportId(ReadOnlySpan<byte> data, byte reportId)
        => data.Length > 0 && data[0] == reportId ? data[1..] : data;

    /// <summary>读取失败时附带 Win32 错误，便于日志定位权限/共享冲突。</summary>
    internal static string LastErrorText()
        => $"Win32={Marshal.GetLastWin32Error()}";
}
