namespace VitaTrace.Core.Abstractions;

/// <summary>
/// 极简日志抽象。Phase 2 只做文件日志；后续可以替换成 Serilog 等实现而不改业务代码。
/// </summary>
public interface IAppLogger
{
    string LogFilePath { get; }

    void Info(string message);

    void Warn(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}

/// <summary>测试/空实现，避免业务层因为日志失败而中断。</summary>
public sealed class NullLogger : IAppLogger
{
    public static NullLogger Instance { get; } = new();

    public string LogFilePath => string.Empty;

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? exception = null)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
