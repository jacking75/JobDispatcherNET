namespace AdvancedMmorpgClient;

/// <summary>
/// 단일 봇. 자체 네트워크 연결을 보유하고 자체 AI tick으로 동작한다.
/// 모든 봇은 같은 프로세스에서 실행되며 WorldState를 공유한다.
/// </summary>
public sealed class BotClient
{
    private enum AiState { Wander, Engage, Flee }

    private readonly WorldState _world;
    private readonly ClientConfig _cfg;
    private readonly string _name;
    private readonly NetworkClient _net;
    private readonly Random _rng;

    private AiState _state = AiState.Wander;
    private int _engageTargetId = -1;
    private long _stateChangedAt;
    private float _wanderTargetX, _wanderTargetY;
    private long _wanderRetargetAt;
    private long _lastAttackAt;

    private const float EngageRange = 60f;
    private const float AttackRange = 3f;
    private const float FleeHpRatio = 0.30f;
    private const long AttackCooldownMs = 1500;

    public int PlayerId => _net.MyPlayerId;
    public string Name => _name;
    public bool Connected => _net.Connected;

    public BotClient(WorldState world, ClientConfig cfg, string name, int seed)
    {
        _world = world;
        _cfg = cfg;
        _name = name;
        _rng = new Random(seed);
        _net = new NetworkClient(world);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _net.ConnectAndLoginAsync(_cfg.Server.Host, _cfg.Server.Port, _name, ct);
    }

    /// <summary>봇 AI 루프. 자기 자신의 Task로 구동.</summary>
    public async Task RunAiAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_cfg.Bots.TickIntervalMs);
        await Task.Delay(_rng.Next(0, _cfg.Bots.TickIntervalMs), ct);

        while (!ct.IsCancellationRequested && _net.Connected)
        {
            try { TickAi(); }
            catch (Exception ex) { Console.Error.WriteLine($"[Bot {_name}] AI 오류: {ex.Message}"); }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void TickAi()
    {
        if (PlayerId == 0) return;
        if (!_world.Entities.TryGetValue(PlayerId, out var me)) return;
        if (!me.IsAlive) return;

        long now = Environment.TickCount64;

        // HP 낮으면 Flee
        if (me.MaxHp > 0 && me.Hp < me.MaxHp * FleeHpRatio)
        {
            if (_state != AiState.Flee)
            {
                _state = AiState.Flee;
                _stateChangedAt = now;
            }
            DoFlee(me, now);
            return;
        }

        switch (_state)
        {
            case AiState.Wander: DoWander(me, now); break;
            case AiState.Engage: DoEngage(me, now); break;
            case AiState.Flee:
                if (now - _stateChangedAt > 4000)
                {
                    _state = AiState.Wander;
                    _stateChangedAt = now;
                }
                else DoFlee(me, now);
                break;
        }
    }

    private void DoWander(EntityView me, long now)
    {
        // 가까운 적 탐색
        var enemy = FindNearestEnemy(me);
        if (enemy is not null)
        {
            _engageTargetId = enemy.Id;
            _state = AiState.Engage;
            _stateChangedAt = now;
            return;
        }

        // 무작위 패트롤 — 목표 지점에 거의 도달했으면 새 지점
        if (now >= _wanderRetargetAt ||
            (Sq(_wanderTargetX - me.X) + Sq(_wanderTargetY - me.Y)) < 4f)
        {
            _wanderTargetX = _rng.NextSingle() * _world.WorldWidth;
            _wanderTargetY = _rng.NextSingle() * _world.WorldHeight;
            _wanderRetargetAt = now + 3000 + _rng.Next(0, 2000);
        }

        StepToward(me, _wanderTargetX, _wanderTargetY, 4f);
    }

    private void DoEngage(EntityView me, long now)
    {
        if (!_world.Entities.TryGetValue(_engageTargetId, out var target) || !target.IsAlive)
        {
            _state = AiState.Wander;
            _stateChangedAt = now;
            _engageTargetId = -1;
            return;
        }

        float dx = target.X - me.X, dy = target.Y - me.Y;
        float d = MathF.Sqrt(dx * dx + dy * dy);

        if (d > EngageRange * 1.5f)
        {
            _state = AiState.Wander;
            _engageTargetId = -1;
            return;
        }

        if (d <= AttackRange)
        {
            // 공격
            if (now - _lastAttackAt >= AttackCooldownMs)
            {
                _net.SendAttack(target.Id);
                _lastAttackAt = now;
            }
        }
        else
        {
            StepToward(me, target.X, target.Y, 4f);
        }
    }

    private void DoFlee(EntityView me, long now)
    {
        // 가장 가까운 적의 반대 방향으로 도망
        var enemy = FindNearestEnemy(me);
        float dirX, dirY;
        if (enemy is not null)
        {
            dirX = me.X - enemy.X;
            dirY = me.Y - enemy.Y;
            float len = MathF.Sqrt(dirX * dirX + dirY * dirY);
            if (len < 0.001f) { dirX = 1; dirY = 0; len = 1; }
            dirX /= len; dirY /= len;
        }
        else
        {
            float a = _rng.NextSingle() * MathF.Tau;
            dirX = MathF.Cos(a); dirY = MathF.Sin(a);
        }

        float nx = Math.Clamp(me.X + dirX * 6f, 0, _world.WorldWidth);
        float ny = Math.Clamp(me.Y + dirY * 6f, 0, _world.WorldHeight);
        _net.SendMove(nx, ny);
    }

    private EntityView? FindNearestEnemy(EntityView me)
    {
        EntityView? best = null;
        float bestSq = EngageRange * EngageRange;
        foreach (var e in _world.Entities.Values)
        {
            if (e.Id == me.Id) continue;
            if (!e.IsAlive) continue;
            // 대상: NPC. 다른 봇은 같은 편으로 간주.
            if (e.Kind == EntityKindView.Player) continue;
            float dx = e.X - me.X, dy = e.Y - me.Y;
            float d = dx * dx + dy * dy;
            if (d < bestSq) { bestSq = d; best = e; }
        }
        return best;
    }

    private void StepToward(EntityView me, float tx, float ty, float maxStep)
    {
        float dx = tx - me.X, dy = ty - me.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return;
        float k = MathF.Min(1f, maxStep / len);
        float nx = Math.Clamp(me.X + dx * k, 0, _world.WorldWidth);
        float ny = Math.Clamp(me.Y + dy * k, 0, _world.WorldHeight);
        _net.SendMove(nx, ny);
    }

    private static float Sq(float x) => x * x;

    public void Close() => _net.Close();
}
