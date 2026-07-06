using JobDispatcherNET;

namespace AdvancedMmorpgServer;

public sealed class GameServer : IDisposable
{
    private readonly ServerConfig _config;
    private readonly GameWorld _world;
    private readonly NetworkServer _network;
    private JobDispatcher<GameWorker>? _dispatcher;
    private Task? _workersTask;
    private int _disposed;

    public GameWorld World => _world;
    public ServerConfig Config => _config;

    public GameServer(ServerConfig config)
    {
        _config = config;
        _world = new GameWorld(config);
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

        var dispatcherOpts = new JobDispatcherOptions
        {
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 5,
            RestartBackoff = TimeSpan.FromSeconds(1),
        };

        _dispatcher = new JobDispatcher<GameWorker>(_config.Server.WorkerThreads, dispatcherOpts);
        _workersTask = _dispatcher.RunWorkerThreadsAsync();

        _world.SpawnInitialNpcs();
        _network.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        JobLog.Info("[Server] shutdown start");

        // Stop external inputs first. Internal shutdown still needs actor jobs.
        _network.Stop();
        _world.Stop();

        AsyncExecutable.AcceptingWork = false;
        _dispatcher?.Dispose();
        TimerRegistry.DisposeAll();
        AsyncExecutable.AcceptingWork = true;

        JobLog.Info("[Server] shutdown complete");
    }
}
