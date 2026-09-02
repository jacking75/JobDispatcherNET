using System.Collections.Concurrent;
using JobDispatcherNET;

namespace AdvancedMmorpgServer;

public sealed record WorldSnapshot(
    int SessionCount,
    int LivePlayerCount,
    int TotalPlayerCount,
    int LiveNpcCount,
    int TotalNpcCount,
    int WorldQueueDepth);

public sealed class GameWorld : AsyncExecutable
{
    private const int WorldQueueCapacity = 10_000;

    public ServerConfig Config { get; }
    public float Width => Config.World.Width;
    public float Height => Config.World.Height;
    public SectorGrid Spatial { get; }
    public TimeSpan AoiResyncInterval => TimeSpan.FromMilliseconds(Config.Server.AoiResyncIntervalMs);

    private volatile bool _isStopping;
    public bool IsStopping => _isStopping;

    private readonly Dictionary<int, PlayerActor> _players = [];
    private readonly Dictionary<int, NpcActor> _npcs = [];
    private readonly Dictionary<int, ClientSession> _sessions = [];
    private readonly ConcurrentDictionary<int, Entity> _entityLookup = new();

    private int _nextEntityId;
    private readonly TimeSpan _tickInterval;

    public GameWorld(ServerConfig cfg, JobSystem system)
        : base(new JobOptions
        {
            Name = "World",
            System = system,
            MaxQueueSize = WorldQueueCapacity,
            DropPolicy = DropPolicy.Reject,

            // The world is also poked from the console thread (status/metrics). Scheduled mode
            // keeps those callers from becoming the world's leader and running game logic on a
            // non-worker thread.
            Mode = ExecutionMode.Scheduled,

            OnDropped = static (actor, reason) => JobLog.Warn(
                $"[World] job refused ({reason}), queue={actor.RemainingTaskCount}"),
        })
    {
        Config = cfg;
        Spatial = new SectorGrid(cfg.World.Width, cfg.World.Height, cfg.World.SpatialCellSize);
        _tickInterval = TimeSpan.FromMilliseconds(cfg.Npc.TickIntervalMs);
    }

    public int AllocateEntityId() => Interlocked.Increment(ref _nextEntityId);

    public void SpawnInitialNpcs() => DoAsync(ProcessSpawnInitialNpcs);

    public void AddPlayer(string name, ClientSession session)
        => DoAsync<(GameWorld W, string Name, ClientSession S)>(
            static t => t.W.ProcessAddPlayer(t.Name, t.S),
            (this, name, session));

    public void RemovePlayer(int playerId)
        => DoAsync<(GameWorld W, int Id)>(
            static t => t.W.ProcessRemovePlayer(t.Id),
            (this, playerId));

    public void HandleClientMove(int playerId, float x, float y)
        => DoAsync<(GameWorld W, int Id, float X, float Y)>(
            static t => t.W.ProcessHandleMove(t.Id, t.X, t.Y),
            (this, playerId, x, y));

    public void HandleClientAttack(int playerId, int targetId)
        => DoAsync<(GameWorld W, int A, int T)>(
            static t => t.W.ProcessHandleAttack(t.A, t.T),
            (this, playerId, targetId));

    public void SendDamage(int targetId, AttackerSnapshot atk, float meleeRange)
        => DoAsync<(GameWorld W, int Id, AttackerSnapshot Atk, float R)>(
            static t => t.W.ProcessRouteDamage(t.Id, t.Atk, t.R),
            (this, targetId, atk, meleeRange));

    public Entity? GetEntity(int id) =>
        _entityLookup.TryGetValue(id, out var e) ? e : null;

    private void ProcessSpawnInitialNpcs()
    {
        var types = Config.Npc.Types;
        if (types.Count == 0)
        {
            JobLog.Warn("[World] no NPC types configured, skipping spawn");
            return;
        }

        var totalWeight = types.Sum(t => Math.Max(1, t.Weight));
        for (var i = 0; i < Config.Npc.TotalCount; i++)
        {
            var picked = PickByWeight(types, totalWeight);
            var id = AllocateEntityId();
            var x = Random.Shared.NextSingle() * Width;
            var y = Random.Shared.NextSingle() * Height;
            var npc = new Npc(id, $"{picked.Kind}#{id}", picked, x, y);
            var actor = new NpcActor(npc, this, _tickInterval);
            _npcs[id] = actor;
            _entityLookup[id] = npc;
            Spatial.Add(npc);
            actor.Start();
        }

        JobLog.Info($"[World] spawned {Config.Npc.TotalCount} NPCs");
    }

    private static ServerConfig.NpcTypeConfig PickByWeight(
        List<ServerConfig.NpcTypeConfig> types, int totalWeight)
    {
        var r = Random.Shared.Next(totalWeight);
        foreach (var t in types)
        {
            var w = Math.Max(1, t.Weight);
            if (r < w) return t;
            r -= w;
        }

        return types[^1];
    }

    private void ProcessAddPlayer(string name, ClientSession session)
    {
        var id = AllocateEntityId();
        var p = new Player(id, name)
        {
            X = Random.Shared.NextSingle() * Width,
            Y = Random.Shared.NextSingle() * Height,
            SendPacket = session.SendPacket,
        };

        var actor = new PlayerActor(p, this);
        _players[id] = actor;
        _sessions[id] = session;
        _entityLookup[id] = p;

        session.OnLoggedIn(id);
        session.SendPacket(Packets.Welcome(p.Id, p.X, p.Y, Width, Height));
        actor.EnterWorld();
    }

    private void ProcessRemovePlayer(int playerId)
    {
        if (_players.Remove(playerId, out var actor))
        {
            _entityLookup.TryRemove(playerId, out _);
            actor.Despawn();
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

    /// <summary>
    /// Consistent read of world state, computed on the world's own queue.
    /// <see cref="AsyncExecutable.AskSync"/> refuses to run inside another job, so this can never
    /// become the "block a worker waiting for an actor" deadlock.
    /// </summary>
    public WorldSnapshot GetSnapshot()
    {
        try
        {
            return AskSync(BuildSnapshot, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            JobLog.Warn("[World] snapshot timed out");
            return new WorldSnapshot(0, 0, 0, 0, 0, RemainingTaskCount);
        }
    }

    private WorldSnapshot BuildSnapshot()
    {
        var alivePlayers = 0;
        foreach (var pa in _players.Values)
        {
            if (!pa.Despawned) alivePlayers++;
        }

        var aliveNpcs = 0;
        foreach (var na in _npcs.Values)
        {
            if (!na.Despawned && na.Npc.IsAlive) aliveNpcs++;
        }

        return new WorldSnapshot(
            SessionCount: _sessions.Count,
            LivePlayerCount: alivePlayers,
            TotalPlayerCount: _players.Count,
            LiveNpcCount: aliveNpcs,
            TotalNpcCount: _npcs.Count,
            WorldQueueDepth: RemainingTaskCount);
    }

    /// <summary>
    /// Despawn everything and cancel the timer chains. Afterwards the system has no self-sustaining
    /// work left, so <see cref="JobSystem.StopAsync"/> can reach a real quiescent state instead of
    /// the fixed sleep this sample used to rely on.
    /// </summary>
    public void Stop()
    {
        _isStopping = true;

        DoAsync(static w =>
        {
            foreach (var s in w._sessions.Values) s.Close();
            w._sessions.Clear();
            foreach (var na in w._npcs.Values) na.Despawn();
            foreach (var pa in w._players.Values) pa.Despawn();
        }, this);

        // Wait for the despawns (and everything they cascade into) to finish.
        if (!System.DrainAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult())
            JobLog.Warn("[World] world did not fully quiesce before shutdown");
    }
}
