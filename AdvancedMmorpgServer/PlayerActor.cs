using JobDispatcherNET;

namespace AdvancedMmorpgServer;

public sealed class PlayerActor : AsyncExecutable
{
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
                    JobLog.Warn($"[Player #{pa.Id}] queue full, rejected job");
            },
        })
    {
        _player = p;
        _world = world;
    }

    public void EnterWorld()
        => DoAsync<PlayerActor>(static a => a.ProcessEnterWorld(), this);

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

    private void ProcessEnterWorld()
    {
        if (_despawned) return;
        Aoi.EnterWorld(_world.Spatial, _player);

        if (_world.AoiResyncInterval > TimeSpan.Zero)
            DoAsyncAfter(_world.AoiResyncInterval, ResyncTick);
    }

    private void ResyncTick()
    {
        if (_despawned || _world.IsStopping) return;
        if (_player.ViewCX < 0 || _player.ViewCY < 0) return;

        var g = _world.Spatial;
        var view = g.ViewOf(_player.ViewCX, _player.ViewCY);
        for (var gy = view.MinY; gy <= view.MaxY; gy++)
        for (var gx = view.MinX; gx <= view.MaxX; gx++)
        {
            foreach (var e in g[gx, gy].Entities.Values)
                _player.SendPacket?.Invoke(Packets.Spawn(e));
        }

        DoAsyncAfter(_world.AoiResyncInterval, ResyncTick);
    }

    private void ProcessMove(float newX, float newY)
    {
        if (_despawned || !_player.IsAlive) return;

        var oldX = _player.X;
        var oldY = _player.Y;

        var dx = newX - oldX;
        var dy = newY - oldY;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        var maxStep = _player.MoveSpeed * 0.5f;
        if (dist > maxStep && dist > 0.0001f)
        {
            var k = maxStep / dist;
            newX = oldX + dx * k;
            newY = oldY + dy * k;
        }

        _player.X = Math.Clamp(newX, 0, _world.Width);
        _player.Y = Math.Clamp(newY, 0, _world.Height);
        Aoi.PlayerMoved(_world.Spatial, _player, oldX, oldY);
    }

    private void ProcessMeleeAttack(int targetId)
    {
        if (_despawned || !_player.IsAlive) return;

        var snap = new AttackerSnapshot(_player.Id, _player.Name, _player.Kind,
            _player.X, _player.Y, _player.Attack);
        _world.SendDamage(targetId, snap, meleeRange: 3.5f);
    }

    private void ProcessReceiveDamage(AttackerSnapshot atk, float meleeRange)
    {
        if (_despawned || !_player.IsAlive) return;

        var d = _player.DistanceTo(atk.X, atk.Y);
        if (d > meleeRange) return;

        var dealt = _player.TakeDamage(atk.Attack);
        var s = _world.Spatial.SectorAt(_player.X, _player.Y);
        Aoi.Publish(s, Packets.Attack(atk.AttackerId, _player.Id, dealt));
        Aoi.Publish(s, Packets.StateOne(_player));

        if (!_player.IsAlive)
        {
            Aoi.Publish(s, Packets.Death(_player.Id, atk.AttackerId));
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
        var oldX = _player.X;
        var oldY = _player.Y;
        _player.Hp = _player.MaxHp;
        _player.X = Random.Shared.NextSingle() * _world.Width;
        _player.Y = Random.Shared.NextSingle() * _world.Height;
        Aoi.PlayerMoved(_world.Spatial, _player, oldX, oldY);
        Aoi.PublishAt(_world.Spatial, _player.X, _player.Y,
            Packets.Respawn(_player.Id, _player.X, _player.Y, _player.Hp));
    }

    private void ProcessDespawn()
    {
        if (_despawned) return;
        _despawned = true;
        Aoi.LeaveWorld(_world.Spatial, _player);
    }
}
