using JobDispatcherNET.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDispatcherNET.Extensions.Hosting;

/// <summary>
/// Everything <see cref="ServiceCollectionExtensions.AddJobDispatcher(IServiceCollection, Action{JobDispatcherBuilderOptions})"/>
/// needs to build the job system and its worker pool.
/// </summary>
public sealed class JobDispatcherBuilderOptions
{
    /// <summary>
    /// Worker threads to start. Defaults to <see cref="Environment.ProcessorCount"/>.
    /// These are dedicated OS threads, not thread-pool threads, so size them against cores rather
    /// than against expected concurrency.
    /// </summary>
    public int WorkerCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Options for the <see cref="JobSystem"/> singleton. <see cref="JobSystemOptions.Logger"/> is
    /// filled in from the application's <see cref="ILoggerFactory"/> unless you set it yourself.
    /// </summary>
    public JobSystemOptions SystemOptions { get; set; } = new();

    /// <summary>
    /// Options for the worker pool. <see cref="JobDispatcherOptions.System"/> is overwritten with
    /// the registered <see cref="JobSystem"/>.
    /// </summary>
    public JobDispatcherOptions DispatcherOptions { get; set; } = new();

    /// <summary>
    /// How long shutdown waits for in-flight jobs, ready-queue items and pending timers to finish
    /// before stopping the workers anyway. Default 30 seconds.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Generic Host wiring for JobDispatcherNET.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register a <see cref="JobSystem"/> and its <see cref="JobDispatcher"/> worker pool as
    /// singletons, route the library's logging into the application's
    /// <see cref="ILoggerFactory"/>, and start and drain the workers with the host.
    ///
    /// <para>The worker threads are started by an <see cref="IHostedService"/>, so they come up in
    /// registration order with the rest of the application: register services that post jobs
    /// <em>after</em> this call and their <c>StartAsync</c> runs with workers already live.</para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to change worker count, options and drain timeout.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
    public static IServiceCollection AddJobDispatcher(
        this IServiceCollection services,
        Action<JobDispatcherBuilderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<JobDispatcherBuilderOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);

        services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<JobDispatcherBuilderOptions>>().Value;
            var systemOptions = options.SystemOptions;

            if (systemOptions.Logger is null
                && provider.GetService<ILoggerFactory>() is { } loggerFactory)
            {
                systemOptions = systemOptions with { Logger = MicrosoftLoggerAdapter.Create(loggerFactory) };
            }

            return new JobSystem(systemOptions);
        });

        services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<JobDispatcherBuilderOptions>>().Value;
            var system = provider.GetRequiredService<JobSystem>();
            var workerCount = Math.Max(1, options.WorkerCount);
            return new JobDispatcher(workerCount, options.DispatcherOptions with { System = system });
        });

        services.AddHostedService<JobSystemHostedService>();
        return services;
    }

    /// <summary>
    /// Add a health check that reports on the job system's worker threads and shutdown gate.
    /// Requires <see cref="AddJobDispatcher(IServiceCollection, Action{JobDispatcherBuilderOptions})"/>.
    /// </summary>
    /// <param name="builder">The health-checks builder.</param>
    /// <param name="name">Name the check is reported under. Defaults to <c>jobdispatcher</c>.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
    public static IHealthChecksBuilder AddJobDispatcher(
        this IHealthChecksBuilder builder,
        string name = "jobdispatcher")
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(
            name,
            static provider => new JobSystemHealthCheck(
                provider.GetRequiredService<JobSystem>(),
                provider.GetRequiredService<IOptions<JobDispatcherBuilderOptions>>()),
            failureStatus: null,
            tags: null));
    }
}

/// <summary>
/// Starts the worker pool when the host starts and drains the job system when it stops.
/// Registered for you by
/// <see cref="ServiceCollectionExtensions.AddJobDispatcher(IServiceCollection, Action{JobDispatcherBuilderOptions})"/>.
/// </summary>
public sealed class JobSystemHostedService : IHostedService
{
    private readonly JobSystem _system;
    private readonly JobDispatcher _dispatcher;
    private readonly TimeSpan _drainTimeout;
    private Task? _workers;

    /// <summary>Create the hosted service.</summary>
    /// <param name="system">The job system to drain on shutdown.</param>
    /// <param name="dispatcher">The worker pool to start.</param>
    /// <param name="options">Supplies <see cref="JobDispatcherBuilderOptions.ShutdownDrainTimeout"/>.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public JobSystemHostedService(JobSystem system, JobDispatcher dispatcher, IOptions<JobDispatcherBuilderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);

        _system = system;
        _dispatcher = dispatcher;
        _drainTimeout = options.Value.ShutdownDrainTimeout;
    }

    /// <summary>
    /// Start the worker threads. Returns as soon as they are running — the threads themselves are
    /// dedicated OS threads and do not occupy the startup path.
    /// </summary>
    /// <param name="cancellationToken">Host startup cancellation. Not used: startup does not block.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workers = _dispatcher.RunWorkerThreadsAsync();
        _system.Logger.Info(
            $"JobSystem '{_system.Name}' started {_dispatcher.WorkerCount} worker thread(s).");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drain in-flight work, then stop the timer thread and the workers.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown cancellation, used only for the final worker join.</param>
    /// <returns>A task that completes once the workers have stopped or the drain timeout expired.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var drained = await _system.StopAsync(_drainTimeout, refuseNewWork: true).ConfigureAwait(false);

        if (drained)
        {
            _system.Logger.Info($"JobSystem '{_system.Name}' drained cleanly and stopped.");
        }
        else
        {
            _system.Logger.Warn(
                $"JobSystem '{_system.Name}' did not drain within {_drainTimeout.TotalSeconds:F0}s " +
                "and was stopped with work still in flight.");
        }

        if (_workers is not { } workers)
            return;

        try
        {
            await workers.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _system.Logger.Warn($"JobSystem '{_system.Name}' still has worker threads running after shutdown.");
        }
        catch (OperationCanceledException)
        {
            // Host shutdown timed out; the workers are background threads and will not block exit.
        }
        finally
        {
            _workers = null;
        }
    }
}

/// <summary>
/// Reports the job system as healthy while every configured worker thread is alive and the system
/// is still accepting work, degraded while some workers are down or the shutdown gate is closed,
/// and unhealthy once no worker is left.
/// </summary>
public sealed class JobSystemHealthCheck : IHealthCheck
{
    private readonly JobSystem _system;
    private readonly int _expectedWorkers;

    /// <summary>Create the health check.</summary>
    /// <param name="system">The job system to inspect.</param>
    /// <param name="options">Supplies the configured <see cref="JobDispatcherBuilderOptions.WorkerCount"/>.</param>
    /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
    public JobSystemHealthCheck(JobSystem system, IOptions<JobDispatcherBuilderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(options);

        _system = system;
        _expectedWorkers = Math.Max(1, options.Value.WorkerCount);
    }

    /// <summary>
    /// Compare live workers against the configured count and check the shutdown gate.
    /// </summary>
    /// <param name="context">Registration context supplied by the health-check service.</param>
    /// <param name="cancellationToken">Unused: the check reads counters and never blocks.</param>
    /// <returns>The health of the job system, with worker and queue counters attached as data.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var live = _system.LiveWorkerCount;
        var accepting = _system.AcceptingWork;

        var data = new Dictionary<string, object>(7, StringComparer.Ordinal)
        {
            ["system"] = _system.Name,
            ["liveWorkers"] = live,
            ["configuredWorkers"] = _expectedWorkers,
            ["acceptingWork"] = accepting,
            ["readyQueueDepth"] = _system.ReadyQueueDepth,
            ["inFlightJobs"] = _system.InFlightJobs,
            ["pendingAsyncJobs"] = _system.PendingAsyncJobs,
        };

        if (live == 0)
        {
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                $"JobSystem '{_system.Name}' has no live worker threads (configured {_expectedWorkers}).",
                exception: null,
                data));
        }

        if (live >= _expectedWorkers && accepting)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                $"JobSystem '{_system.Name}': {live}/{_expectedWorkers} workers live.",
                data));
        }

        var reason = accepting
            ? $"JobSystem '{_system.Name}': only {live}/{_expectedWorkers} worker threads are live."
            : $"JobSystem '{_system.Name}' is no longer accepting work ({live}/{_expectedWorkers} workers live).";

        return Task.FromResult(HealthCheckResult.Degraded(reason, exception: null, data));
    }
}
