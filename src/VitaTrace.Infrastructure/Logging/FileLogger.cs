using VitaTrace.Core.Abstractions;

namespace VitaTrace.Infrastructure.Logging;

/// <summary>
/// 轻量文件日志。Phase 2 先满足“出问题能查”的最低要求：
/// 时间、级别、消息、异常堆栈；单文件超过 2MB 时滚动为 .1。
/// 不引入第三方日志库，减少依赖与打包体积；后续需要结构化日志再替换。
/// </summary>
public sealed class FileLogger : IAppLogger
{
    private const long MaxBytes = 2 * 1024 * 1024;

    // 带 BOM 的 UTF-8：Windows 10 记事本和部分日志查看器不会自动识别无 BOM 的 UTF-8，
    // 中文会显示成乱码。只在新建日志文件时写入 BOM，追加时不会重复写。
    private static readonly System.Text.UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly object _gate = new();

    public FileLogger(string logFilePath)
    {
        if (!TrySwitchTo(logFilePath, out var error))
        {
            throw new IOException($"日志文件不可写：{error}");
        }
    }

    /// <summary>当前日志文件完整路径。切换目录时原地更新，已有读取器引用不需要重建。</summary>
    public string LogFilePath { get; private set; } = string.Empty;

    /// <summary>
    /// 切换日志文件。只在目录可写、文件可创建时生效；失败时保留旧路径。
    /// 用“先创建空文件再切换”的顺序，避免设置已保存但日志实际写不进去。
    /// </summary>
    public bool TrySwitchTo(string logFilePath, out string error)
    {
        try
        {
            var fullPath = Path.GetFullPath(logFilePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "路径中没有有效的目录";
                return false;
            }

            Directory.CreateDirectory(directory);
            if (!File.Exists(fullPath))
            {
                File.WriteAllText(fullPath, string.Empty, Utf8WithBom);
            }

            lock (_gate)
            {
                LogFilePath = fullPath;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        // 日志失败绝不能影响主流程，因此这里吞掉 I/O 异常。
        lock (_gate)
        {
            try
            {
                RollIfNeeded();

                // 先创建带 BOM 的空文件，再追加；AppendAllText 对已存在文件不会补 BOM。
                if (!File.Exists(LogFilePath))
                {
                    File.WriteAllText(LogFilePath, string.Empty, Utf8WithBom);
                }

                File.AppendAllText(LogFilePath, line + Environment.NewLine, Utf8WithBom);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(LogFilePath);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var backup = LogFilePath + ".1";
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        File.Move(LogFilePath, backup);
    }
}
