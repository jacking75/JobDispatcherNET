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
/// Process-wide default logger, used by any <see cref="JobSystem"/> that was not given its own
/// through <see cref="JobSystemOptions.Logger"/>.
/// </summary>
public static class JobLog
{
    private static IJobLogger _instance = new ConsoleJobLogger();

    /// <summary>The current default logger. Never null.</summary>
    public static IJobLogger Current
    {
        get => _instance;
        set => _instance = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Log at <see cref="JobLogLevel.Debug"/>.</summary>
    public static void Debug(string message) => _instance.Debug(message);

    /// <summary>Log at <see cref="JobLogLevel.Info"/>.</summary>
    public static void Info(string message) => _instance.Info(message);

    /// <summary>Log at <see cref="JobLogLevel.Warn"/>.</summary>
    public static void Warn(string message) => _instance.Warn(message);

    /// <summary>Log at <see cref="JobLogLevel.Error"/>.</summary>
    public static void Error(string message, Exception? exception = null) => _instance.Error(message, exception);
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
