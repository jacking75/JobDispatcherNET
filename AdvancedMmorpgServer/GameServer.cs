using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// 게임 서버의 최상위 컨테이너. Dispatcher + World + NetworkServer를 묶는다.
/// </summary>
public sealed class GameServer : IAsyncDisposable
{
    private readonly ServerConfig _config;
    private readonly GameWorld _world;
    private readonly NetworkServer _network;
    private JobDispatcher<GameWorker>? _dispatcher;
    private Task? _workersTask;

    public GameWorld World => _world;
    public ServerConfig Config => _config;

    public GameServer(ServerConfig config)
    {
        _config = config;
        _world = new GameWorld(config);
        _network = new NetworkServer(this, config.Server.Port);
    }

    public Task StartAsync()
    {
        Console.WriteLine("===========================================");
        Console.WriteLine("  AdvancedMmorpgServer 시작");
        Console.WriteLine($"  월드: {_config.World.Name} ({_config.World.Width}x{_config.World.Height})");
        Console.WriteLine($"  워커 스레드: {_config.Server.WorkerThreads}");
        Console.WriteLine($"  NPC: {_config.Npc.TotalCount}마리, tick {_config.Npc.TickIntervalMs}ms");
        Console.WriteLine($"  포트: {_config.Server.Port}");
        Console.WriteLine("===========================================\n");

        _dispatcher = new JobDispatcher<GameWorker>(_config.Server.WorkerThreads);
        _workersTask = _dispatcher.RunWorkerThreadsAsync();

        _world.SpawnInitialNpcs();
        _world.StartBroadcaster();
        _network.Start();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("\n[서버] 종료 중...");
        _network.Stop();
        await _world.StopAsync();
        if (_dispatcher is not null)
            await _dispatcher.DisposeAsync();
        Console.WriteLine("[서버] 종료 완료");
    }
}
