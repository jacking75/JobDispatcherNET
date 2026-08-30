using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// NPC actor. Its AI tick is a repeating timer on its own queue, so every NPC ticks in parallel
/// across the worker pool while each NPC's own work stays serial.
///
/// Library features on show:
///   - hot path entry point (ReceiveDamage) uses <c>DoAsync&lt;TState&gt;</c>, so no closure is allocated
///   - the AI tick is a cancellable <see cref="AsyncExecutable.DoAsyncEvery"/> handle rather than a
///     job that re-schedules itself — one failed tick no longer ends the chain, and despawn just cancels
///   - <see cref="JobOptions.MaxQueueSize"/> caps the queue so a mobbed NPC cannot balloon
/// </summary>
public sealed class NpcActor : AsyncExecutable
{
    /// <summary>NPC 큐 한도. tick 1개 + 다수 공격자 피격 흡수.</summary>
    private const int NpcQueueCapacity = 128;

    public enum AiState { Idle, Chase, Attack, Flee }

    private readonly Npc _npc;
    private readonly GameWorld _world;
    private readonly TimeSpan _tickInterval;

    private AiState _state = AiState.Idle;
    private int _targetId = -1;
    private long _lastAttackTickMs;
    private long _lastTickMs;
    private float _wanderDirX;
    private float _wanderDirY;
    private long _wanderUntilMs;
    private long _fleeUntilMs;

    private const float ChaseGiveUpRangeFactor = 1.6f;
    private const long AttackCooldownMs = 1500;
    private const long FleeDurationMs = 4000;
    private const long WanderRetargetMs = 1500;
    private const float WanderRadius = 12f;

    private volatile bool _despawned;
    private ITimerHandle? _tickTimer;
    private ITimerHandle? _respawnTimer;

    public Npc Npc => _npc;
    public int Id => _npc.Id;
    public bool Despawned => _despawned;

    public NpcActor(Npc npc, GameWorld world, TimeSpan tickInterval)
        : base(new JobOptions
        {
            Name = $"Npc#{npc.Id}",
            System = world.System,
            MaxQueueSize = NpcQueueCapacity,
            DropPolicy = DropPolicy.Reject,
        })
    {
        _npc = npc;
        _world = world;
        _tickInterval = tickInterval;
    }

    /// <summary>Arm the AI tick. Called once at spawn.</summary>
    public void Start()
        => DoAsync<NpcActor>(static a => a.ProcessStart(), this);

    private void ProcessStart()
    {
        if (_despawned) return;

        // Spread the first tick across the interval so 50 NPCs do not all fire on the same
        // millisecond and stampede the workers.
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, Math.Max(1, (int)_tickInterval.TotalMilliseconds)));
        _tickTimer = DoAsyncEvery(_tickInterval, Tick, jitter);
    }

    public void Despawn()
        => DoAsync<NpcActor>(static a => a.ProcessDespawn(), this);

    private void ProcessDespawn()
    {
        if (_despawned) return;
        _despawned = true;

        // Cancelling releases the timers so shutdown can actually reach quiescence.
        _tickTimer?.Cancel();
        _respawnTimer?.Cancel();

        Aoi.LeaveWorldNpc(_world.Spatial, _npc);
    }

    /// <summary>hot path — closure 회피.</summary>
    public void ReceiveDamage(AttackerSnapshot atk, float meleeRange)
        => DoAsync<(NpcActor A, AttackerSnapshot Atk, float R)>(
            static t => t.A.ProcessReceiveDamage(t.Atk, t.R),
            (this, atk, meleeRange));

    private void ProcessReceiveDamage(AttackerSnapshot atk, float meleeRange)
    {
        if (_despawned || !_npc.IsAlive) return;

        float d = _npc.DistanceTo(atk.X, atk.Y);
        if (d > meleeRange) return;

        int dealt = _npc.TakeDamage(atk.Attack);
        var s = _world.Spatial.SectorAt(_npc.X, _npc.Y);
        Aoi.Publish(s, Packets.Attack(atk.AttackerId, _npc.Id, dealt));
        Aoi.Publish(s, Packets.StateOne(_npc));

        if (!_npc.IsAlive)
        {
            _state = AiState.Idle;
            _targetId = -1;
            Aoi.Publish(s, Packets.Death(_npc.Id, atk.AttackerId));
            _respawnTimer = DoAsyncAfter(
                TimeSpan.FromSeconds(_world.Config.Npc.RespawnSeconds),
                static a => a.Respawn(), this);
            return;
        }

        // 어그로
        if (_targetId == -1 || _state == AiState.Idle)
        {
            _targetId = atk.AttackerId;
            _state = AiState.Chase;
        }

        // HP 낮으면 도망
        if (_npc.FleeHpRatio > 0 && _npc.Hp < _npc.MaxHp * _npc.FleeHpRatio)
        {
            _state = AiState.Flee;
            _fleeUntilMs = NowMs() + FleeDurationMs;
        }
    }

    private void Respawn()
    {
        if (_despawned) return;
        float oldX = _npc.X, oldY = _npc.Y;
        _npc.Hp = _npc.MaxHp;
        _npc.X = _npc.SpawnX + (Random.Shared.NextSingle() - 0.5f) * 10f;
        _npc.Y = _npc.SpawnY + (Random.Shared.NextSingle() - 0.5f) * 10f;
        _npc.X = Math.Clamp(_npc.X, 0, _world.Width);
        _npc.Y = Math.Clamp(_npc.Y, 0, _world.Height);
        Aoi.EntityMoved(_world.Spatial, _npc, oldX, oldY);
        _state = AiState.Idle;
        _targetId = -1;
        _lastTickMs = 0;
        Aoi.PublishAt(_world.Spatial, _npc.X, _npc.Y,
            Packets.Respawn(_npc.Id, _npc.X, _npc.Y, _npc.Hp));

        // No need to re-arm anything: the repeating tick kept running and simply did nothing
        // while the NPC was dead.
    }

    /// <summary>AI tick, driven by the repeating timer armed in ProcessStart.</summary>
    private void Tick()
    {
        if (_despawned) return;
        if (_world.IsStopping) return;

        // Dead NPCs tick idly until the respawn timer brings them back.
        if (!_npc.IsAlive) return;

        long now = NowMs();
        float dt = _lastTickMs == 0 ? (float)_tickInterval.TotalSeconds : (now - _lastTickMs) / 1000f;
        if (dt > 1f) dt = 1f;
        _lastTickMs = now;

        switch (_state)
        {
            case AiState.Idle:    TickIdle(now, dt); break;
            case AiState.Chase:   TickChase(now, dt); break;
            case AiState.Attack:  TickAttack(now, dt); break;
            case AiState.Flee:    TickFlee(now, dt); break;
        }
    }

    private void TickIdle(long now, float dt)
    {
        var target = _world.Spatial.FindNearestPlayer(_npc.X, _npc.Y, _npc.AggroRange);
        if (target is not null)
        {
            _targetId = target.Id;
            _state = AiState.Chase;
            return;
        }

        if (now >= _wanderUntilMs)
        {
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            _wanderDirX = MathF.Cos(angle);
            _wanderDirY = MathF.Sin(angle);
            _wanderUntilMs = now + Random.Shared.Next(800, (int)WanderRetargetMs + 800);
        }

        float step = _npc.MoveSpeed * 0.4f * dt;
        float nx = _npc.X + _wanderDirX * step;
        float ny = _npc.Y + _wanderDirY * step;

        float dx = nx - _npc.SpawnX, dy = ny - _npc.SpawnY;
        if (dx * dx + dy * dy > WanderRadius * WanderRadius)
        {
            _wanderDirX = -_wanderDirX;
            _wanderDirY = -_wanderDirY;
            return;
        }

        MoveTo(nx, ny);
    }

    private void TickChase(long now, float dt)
    {
        var target = _world.GetEntity(_targetId);
        if (target is null || !target.IsAlive)
        {
            _state = AiState.Idle;
            _targetId = -1;
            return;
        }

        float d = _npc.DistanceTo(target.X, target.Y);
        if (d > _npc.AggroRange * ChaseGiveUpRangeFactor)
        {
            _state = AiState.Idle;
            _targetId = -1;
            return;
        }

        if (d <= _npc.AttackRange)
        {
            _state = AiState.Attack;
            return;
        }

        float dx = target.X - _npc.X, dy = target.Y - _npc.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return;
        float step = _npc.MoveSpeed * dt;
        MoveTo(_npc.X + dx / len * step, _npc.Y + dy / len * step);
    }

    private void TickAttack(long now, float dt)
    {
        var target = _world.GetEntity(_targetId);
        if (target is null || !target.IsAlive)
        {
            _state = AiState.Idle;
            _targetId = -1;
            return;
        }

        float d = _npc.DistanceTo(target.X, target.Y);
        if (d > _npc.AttackRange)
        {
            _state = AiState.Chase;
            return;
        }

        if (now - _lastAttackTickMs >= AttackCooldownMs)
        {
            _lastAttackTickMs = now;
            var snap = new AttackerSnapshot(_npc.Id, _npc.Name, _npc.Kind,
                _npc.X, _npc.Y, _npc.Attack);
            _world.SendDamage(_targetId, snap, _npc.AttackRange + 0.5f);
        }
    }

    private void TickFlee(long now, float dt)
    {
        if (now >= _fleeUntilMs)
        {
            _state = AiState.Idle;
            return;
        }

        var attacker = _world.GetEntity(_targetId);
        if (attacker is null)
        {
            _state = AiState.Idle;
            return;
        }

        float dx = _npc.X - attacker.X, dy = _npc.Y - attacker.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) { dx = 1; dy = 0; len = 1; }
        float step = _npc.MoveSpeed * 1.2f * dt;
        MoveTo(_npc.X + dx / len * step, _npc.Y + dy / len * step);
    }

    private void MoveTo(float nx, float ny)
    {
        nx = Math.Clamp(nx, 0, _world.Width);
        ny = Math.Clamp(ny, 0, _world.Height);
        float ox = _npc.X, oy = _npc.Y;
        _npc.X = nx;
        _npc.Y = ny;
        Aoi.EntityMoved(_world.Spatial, _npc, ox, oy);
    }

    private static long NowMs() => Environment.TickCount64;
}
