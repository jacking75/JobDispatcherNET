using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// NPC Actor. 자신만의 AI tick을 DoAsyncAfter로 자기 큐에 예약하므로
/// 전체 NPC가 워커 풀에서 병렬로 처리된다.
///
/// 핵심 패턴:
///   Start() → DoAsync(Tick) → 워커에서 Tick 실행 → DoAsyncAfter(interval, Tick)
///   → 같은 NPC의 tick은 자기 Actor 큐에서 직렬화되므로 lock 없음
///   → 서로 다른 NPC의 tick은 워커 풀에서 완전 병렬
/// </summary>
public sealed class NpcActor : AsyncExecutable
{
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

    public Npc Npc => _npc;
    public int Id => _npc.Id;
    public bool Despawned => _despawned;

    public NpcActor(Npc npc, GameWorld world, TimeSpan tickInterval)
    {
        _npc = npc;
        _world = world;
        _tickInterval = tickInterval;
    }

    /// <summary>
    /// 첫 Tick을 예약한다. GameWorld의 부트스트랩 시점에 호출 — 이후 자가 스케줄링 루프 진입.
    /// </summary>
    public void Start()
    {
        // 분산을 위해 첫 Tick은 0~tick 사이 무작위 지연
        var initial = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)_tickInterval.TotalMilliseconds));
        DoAsync(() =>
        {
            if (_despawned) return;
            DoAsyncAfter(initial, Tick);
        });
    }

    public void Despawn()
    {
        DoAsync(() =>
        {
            if (_despawned) return;
            _despawned = true;
            _world.Spatial.Remove(_npc);
        });
    }

    public void ReceiveDamage(AttackerSnapshot atk, float meleeRange)
    {
        DoAsync(() =>
        {
            if (_despawned || !_npc.IsAlive) return;

            float d = _npc.DistanceTo(atk.X, atk.Y);
            if (d > meleeRange) return;

            int dealt = _npc.TakeDamage(atk.Attack);
            _world.NotifyAttack(atk.AttackerId, _npc.Id, dealt);

            if (!_npc.IsAlive)
            {
                _state = AiState.Idle;
                _targetId = -1;
                _world.NotifyDeath(_npc.Id, atk.AttackerId);
                DoAsyncAfter(TimeSpan.FromSeconds(_world.Config.Npc.RespawnSeconds), Respawn);
                return;
            }

            // 어그로: 방금 때린 놈을 우선 타겟으로
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
        });
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
        _world.Spatial.UpdatePosition(_npc, oldX, oldY);
        _state = AiState.Idle;
        _targetId = -1;
        _lastTickMs = 0;
        _world.NotifyRespawn(_npc.Id, _npc.X, _npc.Y, _npc.Hp);

        // 사망 시 끊었던 tick 체인 재가동
        DoAsyncAfter(_tickInterval, Tick);
    }

    /// <summary>AI 메인 tick. 자기 자신을 다음 tick에 다시 예약한다.</summary>
    private void Tick()
    {
        if (_despawned)
            return;

        if (_world.IsStopping)
            return; // 더 이상 재예약하지 않음 → DoAsyncAfter 체인 자연 종료

        if (!_npc.IsAlive)
        {
            // 사망 상태: tick 체인을 끊고 Respawn에서 재시작 — 빈 tick CPU 낭비 방지
            return;
        }

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

        DoAsyncAfter(_tickInterval, Tick);
    }

    private void TickIdle(long now, float dt)
    {
        // 어그로: 주변 플레이어 탐색
        var target = _world.Spatial.FindNearestPlayer(_npc.X, _npc.Y, _npc.AggroRange);
        if (target is not null)
        {
            _targetId = target.Id;
            _state = AiState.Chase;
            return;
        }

        // 무작위 패트롤 — 일정 시간마다 방향 갱신
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

        // 스폰 반경 안에서만 패트롤
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

        // 타겟 방향으로 이동
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
        _world.Spatial.UpdatePosition(_npc, ox, oy);
    }

    private static long NowMs() => Environment.TickCount64;
}
