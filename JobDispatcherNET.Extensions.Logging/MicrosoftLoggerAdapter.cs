using Microsoft.Extensions.Logging;

namespace JobDispatcherNET.Extensions.Logging;

/// <summary>
/// Adapts an <see cref="ILogger"/> to <see cref="IJobLogger"/>, so everything the job system
/// writes flows into the host application's logging pipeline (Serilog, NLog, the console
/// provider, ...) instead of straight to <see cref="Console"/>.
///
/// <para>Assign it through <see cref="JobSystemOptions.Logger"/> for a single system, or through
/// <see cref="JobLog.Current"/> for the whole process:</para>
/// <code>
/// JobLog.Current = MicrosoftLoggerAdapter.Create(loggerFactory);
/// </code>
/// </summary>
public sealed class MicrosoftLoggerAdapter : IJobLogger
{
    /// <summary>
    /// Category name used by <see cref="Create(ILoggerFactory)"/>. Filter on it to turn the
    /// library's own output up or down independently of the rest of the application.
    /// </summary>
    public const string DefaultCategoryName = "JobDispatcherNET";

    private static readonly Func<string, Exception?, string> MessageFormatter = static (state, _) => state;

    private readonly ILogger _logger;

    /// <summary>Wrap an existing <see cref="ILogger"/>.</summary>
    /// <param name="logger">The logger every job-system line is written to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <c>null</c>.</exception>
    public MicrosoftLoggerAdapter(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>The wrapped logger.</summary>
    public ILogger Logger => _logger;

    /// <summary>
    /// Create an adapter over a logger from <paramref name="factory"/> in the
    /// <see cref="DefaultCategoryName"/> category.
    /// </summary>
    /// <param name="factory">Factory the logger is taken from.</param>
    /// <returns>An <see cref="IJobLogger"/> ready to hand to <see cref="JobSystemOptions.Logger"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <c>null</c>.</exception>
    public static IJobLogger Create(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new MicrosoftLoggerAdapter(factory.CreateLogger(DefaultCategoryName));
    }

    /// <summary>
    /// Map a <see cref="JobLogLevel"/> onto the matching <see cref="LogLevel"/>:
    /// <see cref="JobLogLevel.Debug"/> to <see cref="LogLevel.Debug"/>,
    /// <see cref="JobLogLevel.Info"/> to <see cref="LogLevel.Information"/>,
    /// <see cref="JobLogLevel.Warn"/> to <see cref="LogLevel.Warning"/> and
    /// <see cref="JobLogLevel.Error"/> to <see cref="LogLevel.Error"/>.
    /// </summary>
    /// <param name="level">The job-system level to translate.</param>
    /// <returns>The equivalent <see cref="LogLevel"/>. Unknown values map to <see cref="LogLevel.Information"/>.</returns>
    public static LogLevel ToLogLevel(JobLogLevel level) => level switch
    {
        JobLogLevel.Debug => LogLevel.Debug,
        JobLogLevel.Info => LogLevel.Information,
        JobLogLevel.Warn => LogLevel.Warning,
        JobLogLevel.Error => LogLevel.Error,
        _ => LogLevel.Information,
    };

    /// <inheritdoc />
    public bool IsEnabled(JobLogLevel level) => _logger.IsEnabled(ToLogLevel(level));

    /// <inheritdoc />
    public void Log(JobLogLevel level, string message, Exception? exception = null) =>
        _logger.Log(ToLogLevel(level), default, message, exception, MessageFormatter);
}
