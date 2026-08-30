using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace JobDispatcherNET.Samples.Pipelines;

/// <summary>Server knobs, all settable from the command line.</summary>
public sealed record ServerOptions
{
    /// <summary>Listening port. Repo convention for dev TCP is 25001-25199.</summary>
    public int Port { get; init; } = 25120;

    /// <summary>Job-system worker threads.</summary>
    public int WorkerThreads { get; init; } = Math.Max(2, Environment.ProcessorCount);

    /// <summary>World snapshot period.</summary>
    public TimeSpan TickPeriod { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Pending connections the listener will hold.</summary>
    public int Backlog { get; init; } = 512;
}

/// <summary>
/// Ties the pieces together: one <see cref="JobSystem"/>, one <see cref="JobDispatcher"/> worker
/// pool, one <see cref="WorldActor"/>, and an accept loop that turns each socket into a
/// <see cref="SessionConnection"/>.
///
/// <para>The split mirrors <c>AdvancedMmorpgServer</c>: IO threads parse and enqueue, workers run
/// all game logic. What changed is the IO side — <see cref="System.IO.Pipelines"/> and async
/// sockets instead of two dedicated OS threads per session.</para>
/// </summary>
public sealed class PipelinesGameServer
{
    private readonly ServerOptions _options;
    private readonly ConcurrentDictionary<long, SessionConnection> _sessions = [];
    private readonly CancellationTokenSource _cts = new();

    private JobDispatcher? _dispatcher;
    private Socket? _listener;
    private Task? _acceptTask;
    private long _nextConnectionId;
    private long _acceptedTotal;
    private int _stopped;

    /// <summary>Create the server. Nothing is started until <see cref="Start"/>.</summary>
    public PipelinesGameServer(ServerOptions options)
    {
        _options = options;

        System = new JobSystem(new JobSystemOptions
        {
            Name = "pipelines",
            TimerPrecision = TimerPrecision.Coarse,
            EnableDetailedMetrics = true,
            MaxJobDuration = TimeSpan.FromMilliseconds(50),
        });

        World = new WorldActor(System, options.TickPeriod);
    }

    /// <summary>The system every actor on this server belongs to.</summary>
    public JobSystem System { get; }

    /// <summary>The registry owner.</summary>
    public WorldActor World { get; }

    /// <summary>Live sessions.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>Connections accepted since start.</summary>
    public long AcceptedTotal => Interlocked.Read(ref _acceptedTotal);

    /// <summary>Live sessions, for the <c>status</c> command.</summary>
    public IReadOnlyCollection<SessionConnection> Sessions => _sessions.Values.ToArray();

    /// <summary>Start the worker pool, the world tick and the accept loop.</summary>
    public void Start()
    {
        // Workers first: an actor reached before a dispatcher exists would be flushed inline on
        // whatever thread posted to it.
        _dispatcher = new JobDispatcher(_options.WorkerThreads, new JobDispatcherOptions
        {
            System = System,
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 5,
        });
        _ = _dispatcher.RunWorkerThreadsAsync();

        World.StartTick();

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(new IPEndPoint(IPAddress.Any, _options.Port));
        _listener.Listen(_options.Backlog);

        _acceptTask = Task.Run(AcceptLoopAsync);

        JobLog.Info($"[server] listening on 0.0.0.0:{_options.Port} " +
                    $"(workers={_options.WorkerThreads}, tick={_options.TickPeriod.TotalMilliseconds:F0}ms)");
    }

    /// <summary>
    /// Async accept loop. One task, not one thread — accepting is pure IO.
    /// </summary>
    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener!.AcceptAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    JobLog.Warn($"[server] accept failed: {ex.SocketErrorCode}");
                    continue;
                }

                var id = Interlocked.Increment(ref _nextConnectionId);
                Interlocked.Increment(ref _acceptedTotal);

                var session = new SessionConnection(id, socket, this);
                _sessions[id] = session;
                session.Start();
            }
        }
        catch (Exception ex)
        {
            JobLog.Error("[server] accept loop failed", ex);
        }

        JobLog.Info("[server] accept loop stopped");
    }

    internal void OnSessionClosed(SessionConnection session) => _sessions.TryRemove(session.ConnectionId, out _);

    /// <summary>
    /// Graceful shutdown, in the order the library expects:
    /// stop taking input, let the world quiesce, then drain and stop the system.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        JobLog.Info("[server] shutdown start");

        // 1. Stop external input. Internal work still needs the actors.
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        if (_acceptTask is not null)
            await Task.WhenAny(_acceptTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        foreach (var session in _sessions.Values)
            session.Close("server shutting down");

        // 2. Cancel the repeating tick and empty the registry, so nothing keeps re-arming a timer
        //    while StopAsync is trying to reach quiescence.
        try
        {
            var removed = await World.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            JobLog.Info($"[server] world stopped, {removed} entities removed");
        }
        catch (Exception ex)
        {
            JobLog.Warn($"[server] world stop did not complete: {ex.Message}");
        }

        // 3. Drain in-flight work (the queued disconnect markers included), then stop the timer
        //    thread and the worker pool. StopAsync disposes every dispatcher attached to the system.
        var workers = _dispatcher?.WorkerCount ?? 0;
        var drained = await System.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!drained)
            JobLog.Warn("[server] some work was still in flight at shutdown");

        System.Dispose();
        _cts.Dispose();

        JobLog.Info($"[server] shutdown complete (drained={drained}, accepted={AcceptedTotal}, workers={workers})");
    }
}
