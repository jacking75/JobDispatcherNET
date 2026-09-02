using JobDispatcherNET;

namespace AdvancedMmorpgServer;

public sealed class GameServer : IDisposable
{
    private readonly ServerConfig _config;
    private readonly JobSystem _system;
    private readonly GameWorld _world;
    private readonly NetworkServer _network;
    private JobDispatcher? _dispatcher;
    private int _disposed;

    public GameWorld World => _world;
    public ServerConfig Config => _config;
    public JobSystem System => _system;

    public GameServer(ServerConfig config)
    {
        _config = config;

        // One system owns the workers, the timer thread and the metrics for this server.
        // Everything else attaches to it, so shutdown is a single call.
        _system = new JobSystem(new JobSystemOptions
        {
            Name = "game",
            TimerPrecision = TimerPrecision.Coarse,
            MaxJobDuration = TimeSpan.FromMilliseconds(50),
        });

        _world = new GameWorld(config, _system);
        _network = new NetworkServer(this, config.Server.Port);
    }

    public void Start()
    {
        JobLog.Info("===========================================");
        JobLog.Info("  AdvancedMmorpgServer start");
        JobLog.Info($"  World: {_config.World.Name} ({_config.World.Width}x{_config.World.Height})");
        JobLog.Info($"  Worker threads: {_config.Server.WorkerThreads}");
        JobLog.Info($"  NPC: {_config.Npc.TotalCount}, tick {_config.Npc.TickIntervalMs}ms");
        JobLog.Info($"  Port: {_config.Server.Port}");
        JobLog.Info("===========================================");

        // The non-generic dispatcher has no polling loop: workers block until the system has
        // something to do. The IRunnable + Thread.Sleep(1) worker this sample used to need is gone.
        _dispatcher = new JobDispatcher(_config.Server.WorkerThreads, new JobDispatcherOptions
        {
            System = _system,
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 5,
            RestartBackoff = TimeSpan.FromSeconds(1),
        });
        _ = _dispatcher.RunWorkerThreadsAsync();

        _world.SpawnInitialNpcs();
        _network.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        JobLog.Info("[Server] shutdown start");

        // 1. Stop external input. Internal shutdown work still needs the actors.
        _network.Stop();

        // 2. Despawn everything and cancel the timer chains, so the system can reach quiescence.
        _world.Stop();

        // 3. Drain what is left, then stop the timer thread and the workers.
        var drained = _system.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (!drained)
            JobLog.Warn("[Server] some work was still in flight at shutdown");

        _system.Dispose();
        JobLog.Info("[Server] shutdown complete");
    }
}
