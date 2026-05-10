using JobDispatcherNET;

namespace ExampleMmorpgServer;

public sealed record ZoneSnapshot(string Name, IReadOnlyList<PlayerSnapshot> Players);
public sealed record PlayerSnapshot(string PlayerId, string Name, float X, float Y, int Hp, int MaxHp, bool IsAlive);

/// <summary>
/// 게임 존. 단일 존 내에서 플레이어 Actor 기반 병렬 처리를 한다.
///
/// 이전 버전은 ConcurrentDictionary로 _actors race를 회피했지만,
/// 본 버전은 GameZone 자체를 AsyncExecutable(actor)로 만들어 ChatServer와 동일한 패턴을 사용.
///   - _actors는 일반 Dictionary, lock 0개
///   - 패킷 라우팅은 GameZone 큐 안에서 직렬화 → 서로 다른 PlayerActor에는 병렬 dispatch
///   - 자기복제 heartbeat (StartHeartbeat → DoAsyncAfter)
///   - 차단 read API (GetSnapshot, ManualResetEventSlim 패턴)
///
/// 코딩 컨벤션:
///   public Handle*(args)   → DoAsync(() => Process*(args))   (큐에 푸시만)
///   private Process*(args) → 실제 본문 (디버거/스택트레이스/grep 친화적)
/// </summary>
public class GameZone : AsyncExecutable
{
    private readonly string _name;
    private readonly float _width;
    private readonly float _height;
    private readonly SpatialIndex _spatialIndex;
    private readonly Dictionary<string, PlayerActor> _actors = [];
    private volatile bool _stopped;

    private const float MeleeRange = 3.0f;
    private const float MaxCastRange = 15.0f;

    public string Name => _name;
    public SpatialIndex Spatial => _spatialIndex;

    public GameZone(string name, float width, float height, float cellSize = 50f)
    {
        _name = name;
        _width = width;
        _height = height;
        _spatialIndex = new SpatialIndex(cellSize);
    }

    // ── 외부 진입점 (큐에 푸시만) ──────────────────────────────────────

    public void EnterZone(Player player, float spawnX, float spawnY)
        => DoAsync(() => ProcessEnterZone(player, spawnX, spawnY));

    public void LeaveZone(string playerId)
        => DoAsync(() => ProcessLeaveZone(playerId));

    public void HandleMove(string playerId, float newX, float newY)
        => DoAsync(() => ProcessMove(playerId, newX, newY));

    public void HandleMeleeAttack(string attackerId, string targetId)
        => DoAsync(() => ProcessMeleeAttack(attackerId, targetId));

    public void HandleAreaAttack(string attackerId, float centerX, float centerY, float radius)
        => DoAsync(() => ProcessAreaAttack(attackerId, centerX, centerY, radius));

    /// <summary>
    /// PlayerActor → GameZone 큐로 위임되는 melee 라우팅.
    /// 대상 lookup이 GameZone 큐 안에서 일어나므로 _actors race 없음.
    /// </summary>
    internal void SendMeleeDamage(string targetId, AttackerSnapshot attacker, float meleeRange)
        => DoAsync(() =>
        {
            if (_actors.TryGetValue(targetId, out var target))
                target.ReceiveMeleeDamage(attacker, meleeRange);
        });

    /// <summary>
    /// PlayerActor → GameZone 큐로 위임되는 AoE fan-out.
    /// SpatialIndex 조회와 _actors lookup 모두 여기서 처리.
    /// </summary>
    internal void DispatchAreaDamage(AttackerSnapshot attacker, float centerX, float centerY, float radius)
        => DoAsync(() =>
        {
            var nearby = _spatialIndex.QueryRadius(centerX, centerY, radius);
            Console.WriteLine($"  [{_name}] AoE 대상탐색 → {nearby.Count}명 fan-out " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");

            foreach (var p in nearby)
            {
                if (p.PlayerId == attacker.PlayerId) continue;
                if (_actors.TryGetValue(p.PlayerId, out var ta))
                    ta.ReceiveAreaDamage(attacker, centerX, centerY, radius);
            }
        });

    // ── 실제 본문 (private, GameZone 큐에서 직렬 실행) ─────────────────

    private void ProcessEnterZone(Player player, float spawnX, float spawnY)
    {
        if (_actors.ContainsKey(player.PlayerId))
        {
            Console.WriteLine($"[{_name}] 이미 입장 중: {player.PlayerId}");
            return;
        }

        player.X = Math.Clamp(spawnX, 0, _width);
        player.Y = Math.Clamp(spawnY, 0, _height);

        var actor = new PlayerActor(player, this);
        _actors[player.PlayerId] = actor;
        _spatialIndex.Add(player);
        actor.StartRegen();

        Console.WriteLine($"[{_name}] {player.Name} 입장 ({player.X:F1},{player.Y:F1}) " +
                          $"HP:{player.Hp} ATK:{player.Attack} DEF:{player.Defense}");
        player.SendPacket?.Invoke($"ENTER_OK|{_name}|{player.X:F1}|{player.Y:F1}");
    }

    private void ProcessLeaveZone(string playerId)
    {
        if (_actors.Remove(playerId, out var actor))
        {
            // spatial 제거는 actor 자기 큐에서 — 잔여 Move/Damage 작업과 직렬화된다.
            actor.Despawn();
            Console.WriteLine($"[{_name}] {actor.Player.Name} 퇴장");
        }
    }

    private void ProcessMove(string playerId, float newX, float newY)
    {
        if (!_actors.TryGetValue(playerId, out var actor)) return;

        float clampedX = Math.Clamp(newX, 0, _width);
        float clampedY = Math.Clamp(newY, 0, _height);
        actor.Move(clampedX, clampedY);
    }

    private void ProcessMeleeAttack(string attackerId, string targetId)
    {
        if (_actors.TryGetValue(attackerId, out var attacker) &&
            _actors.ContainsKey(targetId))
        {
            attacker.MeleeAttack(targetId, MeleeRange);
        }
    }

    private void ProcessAreaAttack(string attackerId, float centerX, float centerY, float radius)
    {
        if (_actors.TryGetValue(attackerId, out var attacker))
            attacker.AreaAttack(centerX, centerY, radius, MaxCastRange);
    }

    // ── 자기복제 heartbeat ─────────────────────────────────────────────

    public void StartHeartbeat(TimeSpan period)
        => DoAsync(() => Heartbeat(period));

    private void Heartbeat(TimeSpan period)
    {
        if (_stopped) return;

        if (_actors.Count > 0)
        {
            int alive = 0, dead = 0;
            foreach (var a in _actors.Values)
            {
                if (a.Player.IsAlive) alive++; else dead++;
            }
            Console.WriteLine($"  [{_name}] heartbeat 인원={_actors.Count} (생존{alive}/사망{dead}) " +
                              $"[스레드:{Environment.CurrentManagedThreadId}]");
        }

        DoAsyncAfter(period, () => Heartbeat(period));
    }

    // ── 동기 read API (외부에서 안전한 상태 조회) ─────────────────────

    /// <summary>
    /// 차단(blocking) 스냅샷. actor 큐 밖(메인 스레드)에서 호출해야 한다.
    /// _actors / Player.X,Y,Hp 같은 여러 필드를 일관성 있게 묶어 읽는 표준 패턴.
    /// </summary>
    public ZoneSnapshot GetSnapshot()
    {
        using var ev = new ManualResetEventSlim(false);
        PlayerSnapshot[]? players = null;
        DoAsync(() =>
        {
            players = _actors.Values.Select(a =>
            {
                var p = a.Player;
                return new PlayerSnapshot(p.PlayerId, p.Name, p.X, p.Y, p.Hp, p.MaxHp, p.IsAlive);
            }).ToArray();
            ev.Set();
        });
        ev.Wait();
        return new ZoneSnapshot(_name, players!);
    }

    /// <summary>
    /// 종료 — heartbeat 중지 신호 + 모든 PlayerActor에 Despawn 전송 + 각 큐 drain.
    /// async/await 없이 ValueTask 를 차단 대기로 변환해서 동기적으로 실행한다.
    /// </summary>
    public override ValueTask DisposeAsync()
    {
        _stopped = true;

        // GameZone 큐에서 모든 actor에게 Despawn 신호 전송 (자기복제 RegenTick 정지 + spatial 제거)
        PlayerActor[] snapshot = [];
        var captureTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DoAsync(() =>
        {
            snapshot = _actors.Values.ToArray();
            foreach (var a in snapshot)
                a.Despawn();
            _actors.Clear();
            captureTcs.SetResult();
        });
        captureTcs.Task.Wait();

        // 각 PlayerActor 큐 drain — Despawn이 큐 마지막 작업이 되도록 보장
        foreach (var a in snapshot)
            a.DisposeAsync().AsTask().Wait();

        // GameZone 자체 큐 drain — 마지막 한 번만 ValueTask 그대로 반환
        return base.DisposeAsync();
    }
}
