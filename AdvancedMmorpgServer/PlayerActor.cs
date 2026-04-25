using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// 플레이어 단위 Actor. 클라이언트에서 들어온 패킷이 여기에서 직렬 처리된다.
/// 다른 Actor가 보낸 데미지(ReceiveDamage)도 여기에서 직렬화되므로 lock 불필요.
/// </summary>
public sealed class PlayerActor : AsyncExecutable
{
    private readonly Player _player;
    private readonly GameWorld _world;
    private volatile bool _despawned;

    public Player Player => _player;
    public int Id => _player.Id;
    public bool Despawned => _despawned;

    public PlayerActor(Player p, GameWorld world)
    {
        _player = p;
        _world = world;
    }

    public void Move(float newX, float newY)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive) return;

            float oldX = _player.X, oldY = _player.Y;

            // 이동 속도 제한 — 속이는 클라이언트로부터 보호.
            // 한 번 MOVE에서 0.5초 분량까지 허용 (봇 tick 250ms + 약간의 jitter 흡수).
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
        });
    }

    public void MeleeAttack(int targetId)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive) return;

            var snap = new AttackerSnapshot(_player.Id, _player.Name, _player.Kind,
                _player.X, _player.Y, _player.Attack);

            // 타겟 Actor로 전달 — 다른 스레드에서 처리됨 (Actor 단위 직렬)
            _world.SendDamage(targetId, snap, meleeRange: 3.5f);
        });
    }

    public void ReceiveDamage(AttackerSnapshot atk, float meleeRange)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive) return;

            float d = _player.DistanceTo(atk.X, atk.Y);
            if (d > meleeRange) return;

            int dealt = _player.TakeDamage(atk.Attack);
            _world.NotifyAttack(atk.AttackerId, _player.Id, dealt);

            if (!_player.IsAlive)
            {
                _world.NotifyDeath(_player.Id, atk.AttackerId);
                // 5초 후 부활
                DoAsyncAfter(TimeSpan.FromSeconds(5), () =>
                {
                    if (_despawned) return;
                    Respawn();
                });
            }
        });
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

    public void Despawn()
    {
        DoAsync(() =>
        {
            if (_despawned) return;
            _despawned = true;
            _world.Spatial.Remove(_player);
        });
    }
}
