using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// 플레이어 단위 Actor. 클라이언트 패킷이 여기에서 직렬 처리된다.
/// 다른 Actor 가 보낸 데미지(ReceiveDamage)도 여기에서 직렬화 → lock 불필요.
///
/// v2 라이브러리 활용:
///   - hot path 진입점 (Move/MeleeAttack/ReceiveDamage) 은 <c>DoAsync&lt;TState&gt;</c> 로 closure 회피
///   - <see cref="JobOptions"/> 로 큐 한도 지정 — 봇/악성 클라이언트의 패킷 폭주 방어
///   - low-frequency 진입점 (Despawn) 은 closure 사용 OK
/// </summary>
public sealed class PlayerActor : AsyncExecutable
{
    /// <summary>한 플레이어의 in-flight 작업 상한. 1초 60FPS 기준 충분히 큼.</summary>
    private const int PlayerQueueCapacity = 256;

    private readonly Player _player;
    private readonly GameWorld _world;
    private volatile bool _despawned;

    public Player Player => _player;
    public int Id => _player.Id;
    public bool Despawned => _despawned;

    public PlayerActor(Player p, GameWorld world)
        : base(new JobOptions
        {
            MaxQueueSize = PlayerQueueCapacity,
            DropPolicy = DropPolicy.Reject,
            OnDropped = (actor, _) =>
            {
                if (actor is PlayerActor pa)
                    JobLog.Warn($"[플레이어 #{pa.Id}] 큐 만원 — 작업 드롭");
            },
        })
    {
        _player = p;
        _world = world;
    }

    // ── hot path: DoAsync<TState> 로 closure 회피 ──

    public void Move(float newX, float newY)
        => DoAsync<(PlayerActor A, float X, float Y)>(
            static t => t.A.ProcessMove(t.X, t.Y),
            (this, newX, newY));

    public void MeleeAttack(int targetId)
        => DoAsync<(PlayerActor A, int T)>(
            static t => t.A.ProcessMeleeAttack(t.T),
            (this, targetId));

    public void ReceiveDamage(AttackerSnapshot atk, float meleeRange)
        => DoAsync<(PlayerActor A, AttackerSnapshot Atk, float R)>(
            static t => t.A.ProcessReceiveDamage(t.Atk, t.R),
            (this, atk, meleeRange));

    public void Despawn()
        => DoAsync<PlayerActor>(static a => a.ProcessDespawn(), this);

    // ── 실제 본문 (private — actor 큐에서 직렬 실행) ──

    private void ProcessMove(float newX, float newY)
    {
        if (_despawned || !_player.IsAlive) return;

        float oldX = _player.X, oldY = _player.Y;

        // 이동 속도 제한 — 속이는 클라이언트로부터 보호.
        // 한 번 MOVE 에서 0.5초 분량까지 허용 (봇 tick 250ms + 약간의 jitter 흡수).
        float dx = newX - oldX, dy = newY - oldY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float maxStep = _player.MoveSpeed * 0.5f;
        if (dist > maxStep && dist > 0.0001f)
        {
            float k = maxStep / dist;
            newX = oldX + dx * k;
            newY = oldY + dy * k;
        }

        _player.X = Math.Clamp(newX, 0, _world.Width);
        _player.Y = Math.Clamp(newY, 0, _world.Height);
        _world.Spatial.UpdatePosition(_player, oldX, oldY);
    }

    private void ProcessMeleeAttack(int targetId)
    {
        if (_despawned || !_player.IsAlive) return;

        var snap = new AttackerSnapshot(_player.Id, _player.Name, _player.Kind,
            _player.X, _player.Y, _player.Attack);

        // 타겟 Actor 로 전달 (World 큐 → 타겟 Actor 큐)
        _world.SendDamage(targetId, snap, meleeRange: 3.5f);
    }

    private void ProcessReceiveDamage(AttackerSnapshot atk, float meleeRange)
    {
        if (_despawned || !_player.IsAlive) return;

        float d = _player.DistanceTo(atk.X, atk.Y);
        if (d > meleeRange) return;

        int dealt = _player.TakeDamage(atk.Attack);
        _world.NotifyAttack(atk.AttackerId, _player.Id, dealt);

        if (!_player.IsAlive)
        {
            _world.NotifyDeath(_player.Id, atk.AttackerId);
            // 5초 후 부활 — method group 으로 closure 회피
            DoAsyncAfter(TimeSpan.FromSeconds(5), TryRespawn);
        }
    }

    private void TryRespawn()
    {
        if (_despawned) return;
        Respawn();
    }

    private void Respawn()
    {
        float oldX = _player.X, oldY = _player.Y;
        _player.Hp = _player.MaxHp;
        _player.X = Random.Shared.NextSingle() * _world.Width;
        _player.Y = Random.Shared.NextSingle() * _world.Height;
        _world.Spatial.UpdatePosition(_player, oldX, oldY);
        _world.NotifyRespawn(_player.Id, _player.X, _player.Y, _player.Hp);
    }

    private void ProcessDespawn()
    {
        if (_despawned) return;
        _despawned = true;
        _world.Spatial.Remove(_player);
    }
}
