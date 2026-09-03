namespace JobDispatcherNET;

/// <summary>Severity of a library log line.</summary>
public enum JobLogLevel
{
    /// <summary>Verbose detail, off in production.</summary>
    Debug,

    /// <summary>Lifecycle events: workers starting, systems stopping.</summary>
    Info,

    /// <summary>Something recoverable that an operator should see: drops, restarts, slow jobs.</summary>
    Warn,

    /// <summary>A job or worker failed.</summary>
    Error,
}

/// <summary>
/// Logging seam. The library never writes to the console directly through anything else, so
/// swapping this out is enough to route everything into Serilog, NLog or
/// <c>Microsoft.Extensions.Logging</c> — see the <c>JobDispatcherNET.Extensions.Logging</c> package.
/// </summary>
public interface IJobLogger
{
    /// <summary>Cheap check callers use before building a message string.</summary>
    bool IsEnabled(JobLogLevel level);

    /// <summary>Write one line.</summary>
    void Log(JobLogLevel level, string message, Exception? exception = null);
}

/// <summary>Level helpers so callers do not repeat the <see cref="IJobLogger.IsEnabled"/> dance.</summary>
public static class JobLoggerExtensions
{
    /// <summary>Log at <see cref="JobLogLevel.Debug"/>.</summary>
    public static void Debug(this IJobLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(JobLogLevel.Debug)) logger.Log(JobLogLevel.Debug, message);
    }

    /// <summary>Log at <see cref="JobLogLevel.Info"/>.</summary>
    public static void Info(this IJobLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(JobLogLevel.Info)) logger.Log(JobLogLevel.Info, message);
    }

    /// <summary>Log at <see cref="JobLogLevel.Warn"/>.</summary>
    public static void Warn(this IJobLogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(JobLogLevel.Warn)) logger.Log(JobLogLevel.Warn, message);
    }

    /// <summary>Log at <see cref="JobLogLevel.Error"/>.</summary>
    public static void Error(this IJobLogger logger, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(JobLogLevel.Error)) logger.Log(JobLogLevel.Error, message, exception);
    }
}

/// <summary>
/// Wraps a user <see cref="IJobLogger"/> so a failing one cannot take a thread down with it.
///
/// The library logs from the timer thread and from worker threads, and neither has a supervisor
/// that can recover from an escaping exception: a Serilog sink that throws on a full disk used to
/// stop every timer on the system for the life of the process. Every library-internal log call goes
/// through this, so the only thing a broken logger costs is the log line.
/// </summary>
internal sealed class SafeJobLogger(IJobLogger? inner) : IJobLogger
{
    /// <summary>The wrapped logger. <c>null</c> follows <see cref="JobLog.Current"/>, which is mutable.</summary>
    private IJobLogger Inner => inner ?? JobLog.Current;

    /// <inheritdoc />
    public bool IsEnabled(JobLogLevel level)
    {
        try { return Inner.IsEnabled(level); }
        catch { return false; }
    }

    /// <inheritdoc />
    public void Log(JobLogLevel level, string message, Exception? exception = null)
    {
        try { Inner.Log(level, message, exception); }
        catch { /* a broken logger must not kill a worker or the timer thread */ }
    }
}

/// <summary>
/// Process-wide default logger, used by any <see cref="JobSystem"/> that was not given its own
/// through <see cref="JobSystemOptions.Logger"/>.
/// </summary>
public static class JobLog
{
    private static IJobLogger _instance = new ConsoleJobLogger();

    /// <summary>Follows <see cref="Current"/> and swallows whatever it throws.</summary>
    internal static readonly IJobLogger Safe = new SafeJobLogger(null);

    /// <summary>The current default logger. Never null.</summary>
    public static IJobLogger Current
    {
        get => _instance;
        set => _instance = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Log at <see cref="JobLogLevel.Debug"/>. Never throws, whatever the logger does.</summary>
    public static void Debug(string message) => Safe.Debug(message);

    /// <summary>Log at <see cref="JobLogLevel.Info"/>. Never throws, whatever the logger does.</summary>
    public static void Info(string message) => Safe.Info(message);

    /// <summary>Log at <see cref="JobLogLevel.Warn"/>. Never throws, whatever the logger does.</summary>
    public static void Warn(string message) => Safe.Warn(message);

    /// <summary>Log at <see cref="JobLogLevel.Error"/>. Never throws, whatever the logger does.</summary>
    public static void Error(string message, Exception? exception = null) => Safe.Error(message, exception);
}

/// <summary>
/// Console logger. Defaults to <see cref="JobLogLevel.Warn"/> so a hot path cannot flood stdout.
/// </summary>
public sealed class ConsoleJobLogger : IJobLogger
{
    /// <summary>Lowest level that is written. Default <see cref="JobLogLevel.Warn"/>.</summary>
    public JobLogLevel MinLevel { get; init; } = JobLogLevel.Warn;

    /// <inheritdoc />
    public bool IsEnabled(JobLogLevel level) => level >= MinLevel;

    /// <inheritdoc />
    public void Log(JobLogLevel level, string message, Exception? exception = null)
    {
        var writer = level >= JobLogLevel.Warn ? Console.Error : Console.Out;
        writer.WriteLine($"[JobDispatcherNET][{level}] {message}{(exception is null ? string.Empty : $"{Environment.NewLine}{exception}")}");
    }
}

/// <summary>Discards everything. Use in benchmarks and tests.</summary>
public sealed class NullJobLogger : IJobLogger
{
    /// <summary>A shared instance.</summary>
    public static readonly NullJobLogger Instance = new();

    /// <inheritdoc />
    public bool IsEnabled(JobLogLevel level) => false;

    /// <inheritdoc />
    public void Log(JobLogLevel level, string message, Exception? exception = null) { }
}
