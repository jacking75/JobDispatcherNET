namespace JobDispatcherNET;

public enum JobLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 라이브러리 내부 로깅 추상화. 기본 구현은 <see cref="ConsoleJobLogger"/>.
/// 상용 환경에서는 Serilog/Microsoft.Extensions.Logging 어댑터로 교체.
/// </summary>
public interface IJobLogger
{
    bool IsEnabled(JobLogLevel level);
    void Log(JobLogLevel level, string message, Exception? exception = null);
}

/// <summary>
/// 라이브러리 전역 로거. 사용자가 Set 하지 않으면 <see cref="ConsoleJobLogger"/> 사용.
/// </summary>
public static class JobLog
{
    private static IJobLogger _instance = new ConsoleJobLogger();

    public static IJobLogger Current
    {
        get => _instance;
        set => _instance = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static void Debug(string message) { if (_instance.IsEnabled(JobLogLevel.Debug)) _instance.Log(JobLogLevel.Debug, message); }
    public static void Info(string message)  { if (_instance.IsEnabled(JobLogLevel.Info))  _instance.Log(JobLogLevel.Info,  message); }
    public static void Warn(string message)  { if (_instance.IsEnabled(JobLogLevel.Warn))  _instance.Log(JobLogLevel.Warn,  message); }
    public static void Error(string message, Exception? ex = null)
    {
        if (_instance.IsEnabled(JobLogLevel.Error)) _instance.Log(JobLogLevel.Error, message, ex);
    }
}

/// <summary>
/// 콘솔 출력 기본 로거. Warn/Error 만 출력하여 hot path Console.WriteLine 폭주를 막는다.
/// </summary>
public sealed class ConsoleJobLogger : IJobLogger
{
    public JobLogLevel MinLevel { get; init; } = JobLogLevel.Warn;

    public bool IsEnabled(JobLogLevel level) => level >= MinLevel;

    public void Log(JobLogLevel level, string message, Exception? exception = null)
    {
        var writer = level >= JobLogLevel.Warn ? Console.Error : Console.Out;
        writer.WriteLine($"[JobDispatcherNET][{level}] {message}{(exception is null ? string.Empty : $"\n{exception}")}");
    }
}

/// <summary>로그를 완전히 끄고 싶을 때.</summary>
public sealed class NullJobLogger : IJobLogger
{
    public bool IsEnabled(JobLogLevel level) => false;
    public void Log(JobLogLevel level, string message, Exception? exception = null) { }
}
