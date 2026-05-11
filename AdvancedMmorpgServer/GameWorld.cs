using System.Collections.Concurrent;
using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>외부에서 안전하게 읽을 수 있는 월드 상태 스냅샷.</summary>
public sealed record WorldSnapshot(
    int SessionCount,
    int LivePlayerCount,
    int TotalPlayerCount,
    int LiveNpcCount,
    int TotalNpcCount,
    int WorldQueueDepth);

/// <summary>
/// 단일 월드. 모든 플레이어/NPC Actor 를 보유하고 패킷 라우팅과 브로드캐스트를 담당한다.
///
/// v2 라이브러리 활용 패턴:
///   - GameWorld 자체가 <see cref="AsyncExecutable"/> — 라우팅/등록/해제가 큐 안에서 직렬화
///   - hot path 진입점은 <c>DoAsync&lt;TState&gt;</c> 로 closure 알로케이션 회피
///   - low-frequency 진입점(AddPlayer 등) 은 closure 사용 OK
///   - 외부 read 는 차단 스냅샷 (<see cref="GetSnapshot"/>)
///   - <see cref="JobOptions"/> 로 라우터 큐 한도 명시 + drop 알림
///
/// 엔티티 lookup 분리:
///   _players / _npcs Dictionary 는 World 큐 전용 (등록/해제 직렬화)
///   _entityLookup ConcurrentDictionary 는 NPC AI 가 lock-free 로 조회 (X/Y/Hp 는 actor 안에서만 변경)
/// </summary>
public sealed class GameWorld : AsyncExecutable
{
    private const int WorldQueueCapacity = 10_000;

    public ServerConfig Config { get; }
    public float Width => Config.World.Width;
    public float Height => Config.World.Height;
    public SpatialIndex Spatial { get; }

    private volatile bool _isStopping;
    public bool IsStopping => _isStopping;

    // ── World 큐 안에서만 접근 (lock 0개) ──
    private readonly Dictionary<int, PlayerActor> _players = [];
    private readonly Dictionary<int, NpcActor> _npcs = [];
    private readonly Dictionary<int, ClientSession> _sessions = [];

    // ── lock-free entity lookup (NPC AI 용) ──
    // 기록은 World 큐에서만, 읽기는 어디서든 안전.
    private readonly ConcurrentDictionary<int, Entity> _entityLookup = new();

    private int _nextEntityId;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _broadcastInterval;
    private BroadcastActor? _broadcaster;

    public GameWorld(ServerConfig cfg)
        : base(new JobOptions
        {
            MaxQueueSize = WorldQueueCapacity,
            DropPolicy = DropPolicy.Reject,
            OnDropped = (actor, _) => JobLog.Warn(
                $"[월드] 큐 만원 — 작업 드롭 (queue={actor.RemainingTaskCount})"),
        })
    {
        Config = cfg;
        Spatial = new SpatialIndex(cfg.World.SpatialCellSize);
        _tickInterval = TimeSpan.FromMilliseconds(cfg.Npc.TickIntervalMs);
        _broadcastInterval = TimeSpan.FromMilliseconds(cfg.Server.BroadcastIntervalMs);
    }

    public int AllocateEntityId() => Interlocked.Increment(ref _nextEntityId);

    // ─────────────────────────────────────────────────────
    //  외부 진입점 — 모두 World 큐로 위임
    // ─────────────────────────────────────────────────────

    public void SpawnInitialNpcs() => DoAsync(ProcessSpawnInitialNpcs);

    public void AddPlayer(string name, ClientSession session)
        => DoAsync(() => ProcessAddPlayer(name, session));

    public void RemovePlayer(int playerId)
        => DoAsync<(GameWorld W, int Id)>(
            static t => t.W.ProcessRemovePlayer(t.Id),
            (this, playerId));

    /// <summary>hot path — closure 회피.</summary>
    public void HandleClientMove(int playerId, float x, float y)
        => DoAsync<(GameWorld W, int Id, float X, float Y)>(
            static t => t.W.ProcessHandleMove(t.Id, t.X, t.Y),
            (this, playerId, x, y));

    /// <summary>hot path — closure 회피.</summary>
    public void HandleClientAttack(int playerId, int targetId)
        => DoAsync<(GameWorld W, int A, int T)>(
            static t => t.W.ProcessHandleAttack(t.A, t.T),
            (this, playerId, targetId));

    /// <summary>hot path — Actor 가 데미지 라우팅 시 호출. closure 회피.</summary>
    public void SendDamage(int targetId, AttackerSnapshot atk, float meleeRange)
        => DoAsync<(GameWorld W, int Id, AttackerSnapshot Atk, float R)>(
            static t => t.W.ProcessRouteDamage(t.Id, t.Atk, t.R),
            (this, targetId, atk, meleeRange));

    /// <summary>
    /// NPC AI 의 lock-free 엔티티 조회. _entityLookup 은 ConcurrentDictionary.
    /// X/Y/Hp 는 owning actor 안에서만 mutate 되므로 stale read 가능 (Tick 단위 OK).
    /// </summary>
    public Entity? GetEntity(int id) =>
        _entityLookup.TryGetValue(id, out var e) ? e : null;

    // ─────────────────────────────────────────────────────
    //  실제 본문 (private — World 큐에서 직렬 실행)
    // ─────────────────────────────────────────────────────

    private void ProcessSpawnInitialNpcs()
    {
        var types = Config.Npc.Types;
        if (types.Count == 0)
        {
            JobLog.Warn("[월드] NPC 타입이 정의되어 있지 않음 — 스폰 스킵");
            return;
        }

        int totalWeight = types.Sum(t => Math.Max(1, t.Weight));
        for (int i = 0; i < Config.Npc.TotalCount; i++)
        {
            var picked = PickByWeight(types, totalWeight);
            int id = AllocateEntityId();
            float x = Random.Shared.NextSingle() * Width;
            float y = Random.Shared.NextSingle() * Height;
            var npc = new Npc(id, $"{picked.Kind}#{id}", picked, x, y);
            var actor = new NpcActor(npc, this, _tickInterval);
            _npcs[id] = actor;
            _entityLookup[id] = npc;
            Spatial.Add(npc);
            actor.Start();
        }
        JobLog.Info($"[월드] NPC {Config.Npc.TotalCount}마리 스폰 완료");
    }

    private static ServerConfig.NpcTypeConfig PickByWeight(
        List<ServerConfig.NpcTypeConfig> types, int totalWeight)
    {
        int r = Random.Shared.Next(totalWeight);
        foreach (var t in types)
        {
            int w = Math.Max(1, t.Weight);
            if (r < w) return t;
            r -= w;
        }
        return types[^1];
    }

    private void ProcessAddPlayer(string name, ClientSession session)
    {
        int id = AllocateEntityId();
        var p = new Player(id, name);
        p.X = Random.Shared.NextSingle() * Width;
        p.Y = Random.Shared.NextSingle() * Height;
        var actor = new PlayerActor(p, this);
        _players[id] = actor;
        _sessions[id] = session;
        _entityLookup[id] = p;
        Spatial.Add(p);

        session.OnLoggedIn(id);
        session.SendPacket(Packets.Welcome(p.Id, p.X, p.Y, Width, Height));

        SendInitialSnapshot(session);
        BroadcastSpawnDirect(p);
    }

    private void ProcessRemovePlayer(int playerId)
    {
        if (_players.Remove(playerId, out var actor))
        {
            _entityLookup.TryRemove(playerId, out _);
            actor.Despawn();
            BroadcastDespawnDirect(playerId);
        }
        _sessions.Remove(playerId);
    }

    private void ProcessHandleMove(int playerId, float x, float y)
    {
        if (_players.TryGetValue(playerId, out var pa))
            pa.Move(x, y);
    }

    private void ProcessHandleAttack(int playerId, int targetId)
    {
        if (_players.TryGetValue(playerId, out var pa))
            pa.MeleeAttack(targetId);
    }

    private void ProcessRouteDamage(int targetId, AttackerSnapshot atk, float meleeRange)
    {
        if (_players.TryGetValue(targetId, out var pa))
            pa.ReceiveDamage(atk, meleeRange);
        else if (_npcs.TryGetValue(targetId, out var na))
            na.ReceiveDamage(atk, meleeRange);
    }

    private void SendInitialSnapshot(ClientSession dest)
    {
        foreach (var actor in _players.Values)
        {
            var p = actor.Player;
            if (p.Id == dest.PlayerId) continue;
            dest.SendPacket(Packets.Spawn(p));
        }
        foreach (var actor in _npcs.Values)
        {
            dest.SendPacket(Packets.Spawn(actor.Npc));
        }
    }

    // ─────────────────────────────────────────────────────
    //  브로드캐스트 — actor 들이 호출. World 큐로 위임.
    // ─────────────────────────────────────────────────────

    public void BroadcastSpawn(Entity e)
        => DoAsync<(GameWorld W, Entity E)>(static t => t.W.BroadcastSpawnDirect(t.E), (this, e));

    public void BroadcastDespawn(int id)
        => DoAsync<(GameWorld W, int Id)>(static t => t.W.BroadcastDespawnDirect(t.Id), (this, id));

    public void NotifyAttack(int aId, int tId, int dmg)
        => DoAsync<(GameWorld W, int A, int T, int D)>(
            static t => t.W.BroadcastDirect(Packets.Attack(t.A, t.T, t.D)),
            (this, aId, tId, dmg));

    public void NotifyDeath(int id, int killerId)
        => DoAsync<(GameWorld W, int Id, int Killer)>(
            static t => t.W.BroadcastDirect(Packets.Death(t.Id, t.Killer)),
            (this, id, killerId));

    public void NotifyRespawn(int id, float x, float y, int hp)
        => DoAsync<(GameWorld W, int Id, float X, float Y, int Hp)>(
            static t => t.W.BroadcastDirect(Packets.Respawn(t.Id, t.X, t.Y, t.Hp)),
            (this, id, x, y, hp));

    private void BroadcastSpawnDirect(Entity e) => BroadcastDirect(Packets.Spawn(e));
    private void BroadcastDespawnDirect(int id) => BroadcastDirect(Packets.Despawn(id));

    private void BroadcastDirect(string packet)
    {
        foreach (var s in _sessions.Values)
            s.SendPacket(packet);
    }

    // ─────────────────────────────────────────────────────
    //  주기적 상태 스냅샷
    // ─────────────────────────────────────────────────────

    public void StartBroadcaster()
    {
        _broadcaster = new BroadcastActor(this, _broadcastInterval);
        _broadcaster.Start();
    }

    /// <summary>BroadcastActor → World 큐 위임 호출.</summary>
    internal void RouteBroadcastSnapshot()
        => DoAsync<GameWorld>(static w => w.BroadcastSnapshotDirect(), this);

    private void BroadcastSnapshotDirect()
    {
        var sb = new System.Text.StringBuilder(8192);
        sb.Append("STATE");
        foreach (var pa in _players.Values)
        {
            if (pa.Despawned) continue;
            var p = pa.Player;
            sb.Append('|').Append(p.Id).Append(',').Append(p.X.ToString("F1"))
              .Append(',').Append(p.Y.ToString("F1")).Append(',').Append(p.Hp);
        }
        foreach (var na in _npcs.Values)
        {
            if (na.Despawned) continue;
            var n = na.Npc;
            sb.Append('|').Append(n.Id).Append(',').Append(n.X.ToString("F1"))
              .Append(',').Append(n.Y.ToString("F1")).Append(',').Append(n.Hp);
        }
        BroadcastDirect(sb.ToString());
    }

    // ─────────────────────────────────────────────────────
    //  외부 차단 read API
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 외부(메인/모니터링)에서 호출 — World 큐가 처리할 때까지 대기 후 일관된 스냅샷.
    /// Closure 캡처 OK (low-frequency).
    /// </summary>
    public WorldSnapshot GetSnapshot()
    {
        using var ev = new ManualResetEventSlim(false);
        WorldSnapshot? result = null;
        DoAsync(() =>
        {
            int alivePlayers = 0;
            foreach (var pa in _players.Values) if (!pa.Despawned) alivePlayers++;
            int aliveNpcs = 0;
            foreach (var na in _npcs.Values) if (!na.Despawned && na.Npc.IsAlive) aliveNpcs++;
            result = new WorldSnapshot(
                SessionCount: _sessions.Count,
                LivePlayerCount: alivePlayers,
                TotalPlayerCount: _players.Count,
                LiveNpcCount: aliveNpcs,
                TotalNpcCount: _npcs.Count,
                WorldQueueDepth: RemainingTaskCount);
            ev.Set();
        });
        ev.Wait(TimeSpan.FromSeconds(2));
        return result ?? new WorldSnapshot(0, 0, 0, 0, 0, RemainingTaskCount);
    }

    // ─────────────────────────────────────────────────────
    //  종료
    // ─────────────────────────────────────────────────────

    public void Stop()
    {
        _isStopping = true;

        // 1) World 큐 안에서 모든 세션/Actor 일괄 close + despawn
        var captured = new TaskCompletionSource<(PlayerActor[] P, NpcActor[] N)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DoAsync(() =>
        {
            foreach (var s in _sessions.Values) s.Close();
            _sessions.Clear();
            foreach (var na in _npcs.Values) na.Despawn();
            foreach (var pa in _players.Values) pa.Despawn();
            captured.TrySetResult((_players.Values.ToArray(), _npcs.Values.ToArray()));
        });
        captured.Task.Wait(TimeSpan.FromSeconds(2));
        var (playersSnap, npcsSnap) = captured.Task.IsCompletedSuccessfully
            ? captured.Task.Result
            : ([], []);

        // 2) Broadcaster drain
        if (_broadcaster is not null)
            _broadcaster.DisposeAsync().AsTask().Wait();

        // 3) 잔여 자가복제 tick 들이 _isStopping 보고 자연 종료될 시간
        Thread.Sleep(200);

        // 4) 각 Actor 큐 drain
        foreach (var na in npcsSnap) na.DisposeAsync().AsTask().Wait();
        foreach (var pa in playersSnap) pa.DisposeAsync().AsTask().Wait();

        // 5) World 자체 큐 drain
        DisposeAsync().AsTask().Wait();
    }
}

/// <summary>
/// 주기적 STATE 브로드캐스트를 자가 스케줄링.
/// 자기 큐는 매우 작게 잡음 (자기복제 1개 + 안전 여유).
/// </summary>
internal sealed class BroadcastActor : AsyncExecutable
{
    private readonly GameWorld _world;
    private readonly TimeSpan _interval;

    public BroadcastActor(GameWorld world, TimeSpan interval)
        : base(new JobOptions
        {
            MaxQueueSize = 16,
            DropPolicy = DropPolicy.Reject,
        })
    {
        _world = world;
        _interval = interval;
    }

    public void Start() => DoAsync(Tick);

    private void Tick()
    {
        if (_world.IsStopping) return;
        try { _world.RouteBroadcastSnapshot(); }
        catch (Exception ex) { JobLog.Error("[브로드캐스트 오류]", ex); }
        DoAsyncAfter(_interval, Tick);
    }
}
