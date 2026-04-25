using System.Collections.Concurrent;
using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// 단일 월드. 모든 플레이어/NPC Actor를 보유하고 패킷 라우팅과 브로드캐스트를 담당한다.
///
/// 스레딩 요약:
///   - 각 Player/Npc Actor가 자기 큐를 가짐 → 워커 풀에서 병렬 실행
///   - SpatialIndex는 ConcurrentDictionary 기반 → 외부 lock 없음
///   - 세션 송신은 Channel(이벤트) + 별도 SendLoop 태스크로 분리
///   - 브로드캐스트는 BroadcastActor가 DoAsyncAfter로 자가 스케줄링
/// </summary>
public sealed class GameWorld
{
    public ServerConfig Config { get; }
    public float Width => Config.World.Width;
    public float Height => Config.World.Height;
    public SpatialIndex Spatial { get; }

    private volatile bool _isStopping;
    public bool IsStopping => _isStopping;

    private readonly ConcurrentDictionary<int, PlayerActor> _players = [];
    private readonly ConcurrentDictionary<int, NpcActor> _npcs = [];
    private readonly ConcurrentDictionary<int, ClientSession> _sessions = [];

    private int _nextEntityId;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _broadcastInterval;
    private BroadcastActor? _broadcaster;

    public IReadOnlyCollection<ClientSession> Sessions => (IReadOnlyCollection<ClientSession>)_sessions.Values;
    public IReadOnlyCollection<NpcActor> Npcs => (IReadOnlyCollection<NpcActor>)_npcs.Values;
    public IReadOnlyCollection<PlayerActor> Players => (IReadOnlyCollection<PlayerActor>)_players.Values;

    public GameWorld(ServerConfig cfg)
    {
        Config = cfg;
        Spatial = new SpatialIndex(cfg.World.SpatialCellSize);
        _tickInterval = TimeSpan.FromMilliseconds(cfg.Npc.TickIntervalMs);
        _broadcastInterval = TimeSpan.FromMilliseconds(cfg.Server.BroadcastIntervalMs);
    }

    public int AllocateEntityId() => Interlocked.Increment(ref _nextEntityId);

    // ─────────────────────────────────────────────────────
    //  엔티티 등록
    // ─────────────────────────────────────────────────────

    public void SpawnInitialNpcs()
    {
        var types = Config.Npc.Types;
        if (types.Count == 0)
        {
            Console.WriteLine("[월드] NPC 타입이 정의되어 있지 않음 — 스폰 스킵");
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
            Spatial.Add(npc);
            actor.Start();
        }
        Console.WriteLine($"[월드] NPC {Config.Npc.TotalCount}마리 스폰 완료");
    }

    private static ServerConfig.NpcTypeConfig PickByWeight(List<ServerConfig.NpcTypeConfig> types, int totalWeight)
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

    public PlayerActor AddPlayer(string name, ClientSession session)
    {
        int id = AllocateEntityId();
        var p = new Player(id, name);
        p.X = Random.Shared.NextSingle() * Width;
        p.Y = Random.Shared.NextSingle() * Height;
        var actor = new PlayerActor(p, this);
        _players[id] = actor;
        _sessions[id] = session;
        Spatial.Add(p);

        // 세션의 PlayerId를 먼저 설정해야 SendInitialSnapshot의 self-skip 로직이 동작한다
        session.OnLoggedIn(id);

        session.SendPacket(Packets.Welcome(p.Id, p.X, p.Y, Width, Height));

        // 신규 입장자에게 기존 월드 스냅샷 전송 (자신은 제외)
        SendInitialSnapshot(session);

        // 모두에게 자신의 입장을 알림 (자신도 포함되어 자기 SPAWN 1번 받음)
        BroadcastSpawn(p);
        return actor;
    }

    public void RemovePlayer(int playerId)
    {
        if (_players.TryRemove(playerId, out var actor))
        {
            actor.Despawn();
            BroadcastDespawn(playerId);
        }
        _sessions.TryRemove(playerId, out _);
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
    //  패킷 라우팅
    // ─────────────────────────────────────────────────────

    public Entity? GetEntity(int id)
    {
        if (_players.TryGetValue(id, out var pa)) return pa.Player;
        if (_npcs.TryGetValue(id, out var na)) return na.Npc;
        return null;
    }

    public void SendDamage(int targetId, AttackerSnapshot atk, float meleeRange)
    {
        if (_players.TryGetValue(targetId, out var pa))
            pa.ReceiveDamage(atk, meleeRange);
        else if (_npcs.TryGetValue(targetId, out var na))
            na.ReceiveDamage(atk, meleeRange);
    }

    public void HandleClientMove(int playerId, float x, float y)
    {
        if (_players.TryGetValue(playerId, out var pa))
            pa.Move(x, y);
    }

    public void HandleClientAttack(int playerId, int targetId)
    {
        if (_players.TryGetValue(playerId, out var pa))
            pa.MeleeAttack(targetId);
    }

    // ─────────────────────────────────────────────────────
    //  브로드캐스트 알림
    // ─────────────────────────────────────────────────────

    public void BroadcastSpawn(Entity e)   => Broadcast(Packets.Spawn(e));
    public void BroadcastDespawn(int id)   => Broadcast(Packets.Despawn(id));
    public void NotifyAttack(int aId, int tId, int dmg) => Broadcast(Packets.Attack(aId, tId, dmg));
    public void NotifyDeath(int id, int killerId)       => Broadcast(Packets.Death(id, killerId));
    public void NotifyRespawn(int id, float x, float y, int hp) => Broadcast(Packets.Respawn(id, x, y, hp));

    private void Broadcast(string packet)
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

    internal void BroadcastSnapshot()
    {
        // 전체 엔티티 위치/HP 스냅샷 — 단순화를 위해 항상 풀 스냅샷
        // (다수의 엔티티 + 다수의 클라이언트 스트레스 테스트가 목적)
        var sb = new System.Text.StringBuilder(8192);
        sb.Append("STATE");
        foreach (var pa in _players.Values)
        {
            if (pa.Despawned) continue;
            var p = pa.Player;
            sb.Append('|').Append(p.Id).Append(',').Append(p.X.ToString("F1"))
              .Append(',').Append(p.Y.ToString("F1")).Append(',').Append(Volatile.Read(ref p.Hp));
        }
        foreach (var na in _npcs.Values)
        {
            if (na.Despawned) continue;
            var n = na.Npc;
            sb.Append('|').Append(n.Id).Append(',').Append(n.X.ToString("F1"))
              .Append(',').Append(n.Y.ToString("F1")).Append(',').Append(Volatile.Read(ref n.Hp));
        }
        var msg = sb.ToString();
        Broadcast(msg);
    }

    // ─────────────────────────────────────────────────────
    //  종료
    // ─────────────────────────────────────────────────────

    public async Task StopAsync()
    {
        _isStopping = true;

        foreach (var s in _sessions.Values) s.Close();
        _sessions.Clear();

        foreach (var na in _npcs.Values) na.Despawn();
        foreach (var pa in _players.Values) pa.Despawn();

        if (_broadcaster is not null)
            await _broadcaster.DisposeAsync();

        // Actor들이 잔여 작업을 비울 시간을 잠깐 준다
        await Task.Delay(200);

        foreach (var na in _npcs.Values) await na.DisposeAsync();
        foreach (var pa in _players.Values) await pa.DisposeAsync();
    }
}

/// <summary>
/// 주기적 스냅샷 브로드캐스트를 자가 스케줄링하는 Actor.
/// 워커 풀에서 실행되며 다른 Actor와 동일한 처리 모델을 따른다.
/// </summary>
internal sealed class BroadcastActor : AsyncExecutable
{
    private readonly GameWorld _world;
    private readonly TimeSpan _interval;

    public BroadcastActor(GameWorld world, TimeSpan interval)
    {
        _world = world;
        _interval = interval;
    }

    public void Start() => DoAsync(Tick);

    private void Tick()
    {
        if (_world.IsStopping) return;
        try { _world.BroadcastSnapshot(); }
        catch (Exception ex) { Console.Error.WriteLine($"[브로드캐스트 오류] {ex.Message}"); }
        DoAsyncAfter(_interval, Tick);
    }
}
