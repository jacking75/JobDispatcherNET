using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// 게임 서버의 최상위 컨테이너. Dispatcher + World + NetworkServer 를 묶는다.
///
/// 동기 API (async/await 미사용):
///   Start / Dispose 모두 차단(blocking) 호출.
///
/// 라이브러리 활용:
///   - <see cref="JobDispatcherOptions"/> 로 워커 supervisor / 재기동 백오프 명시
///   - 셧다운 시 <see cref="AsyncExecutable.AcceptingWork"/> = false 로 신규 입력 차단
///   - <see cref="TimerRegistry.DisposeAll"/> 로 비-워커 timer 정리
/// </summary>
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
        JobLog.Info("  AdvancedMmorpgServer 시작");
        JobLog.Info($"  월드: {_config.World.Name} ({_config.World.Width}x{_config.World.Height})");
        JobLog.Info($"  워커 스레드: {_config.Server.WorkerThreads}");
        JobLog.Info($"  NPC: {_config.Npc.TotalCount}마리, tick {_config.Npc.TickIntervalMs}ms");
        JobLog.Info($"  포트: {_config.Server.Port}");
        JobLog.Info("===========================================");

        // 라이브러리 v2 옵션 — 워커 supervisor 명시
        var dispatcherOpts = new JobDispatcherOptions
        {
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 5,
            RestartBackoff = TimeSpan.FromSeconds(1),
        };

        _dispatcher = new JobDispatcher<GameWorker>(_config.Server.WorkerThreads, dispatcherOpts);
        _workersTask = _dispatcher.RunWorkerThreadsAsync();

        _world.SpawnInitialNpcs();
        _world.StartBroadcaster();
        _network.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        JobLog.Info("[서버] 종료 시작");

        // 1) 라이브러리 셧다운 게이트 — 신규 actor 작업 거부
        AsyncExecutable.AcceptingWork = false;

        // 2) 네트워크 정지 — IO 스레드들 join (잔여 패킷은 sequencer 가 마저 drain)
        _network.Stop();

        // 3) World drain — 모든 actor despawn + 큐 비우기
        _world.Stop();

        // 4) 워커 풀 정지 + Join
        _dispatcher?.Dispose();

        // 5) 비-워커 스레드(IO 등)에서 만들어졌을 수 있는 TimerQueue 정리
        TimerRegistry.DisposeAll();

        // 다음 인스턴스를 위해 게이트 복구 (테스트 친화)
        AsyncExecutable.AcceptingWork = true;

        JobLog.Info("[서버] 종료 완료");
    }
}
