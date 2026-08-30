namespace JobDispatcherNET.Samples.Pipelines;

/// <summary>
/// One actor per connected client. Owns that client's position and its message counters, so no
/// lock guards them — jobs posted to one entity run one at a time.
///
/// <para><b>Why <see cref="ExecutionMode.Scheduled"/>.</b> Under the default
/// <see cref="ExecutionMode.LeaderFlush"/> whichever thread finds the actor idle runs the job
/// inline. Entities are reached from the session sequencer's drain (a worker — fine) but also from
/// the accept path and from socket completions, and we never want game logic executing on an IO
/// thread. <c>Scheduled</c> makes a non-worker producer hand the actor to the ready queue and
/// return immediately.</para>
///
/// <para><b>Why a bounded queue.</b> A client that floods faster than a worker drains would
/// otherwise grow this queue until the process dies. At <see cref="MaxQueue"/> the actor refuses
/// work and <see cref="JobOptions.OnDropped"/> fires — here that kills the session, which is the
/// only honest answer to "you are talking faster than we can listen".</para>
/// </summary>
public sealed class EntityActor : AsyncExecutable
{
    /// <summary>Jobs (queued + in flight) one entity may hold before it starts refusing.</summary>
    public const int MaxQueue = 512;

    private readonly WorldActor _world;
    private readonly SessionConnection _session;

    private float _x;
    private float _y;
    private long _moves;
    private long _chats;

    /// <summary>Create the actor for a freshly logged-in client.</summary>
    public EntityActor(int id, string name, SessionConnection session, WorldActor world, JobSystem system)
        : base(BuildOptions(id, session, system))
    {
        Id = id;
        DisplayName = name;
        _session = session;
        _world = world;

        // Spread the starting positions so a snapshot is not 200 identical points.
        _x = id % 64 * 4f;
        _y = id / 64 % 64 * 4f;
    }

    private static JobOptions BuildOptions(int id, SessionConnection session, JobSystem system) => new()
    {
        Name = $"entity#{id}",
        System = system,
        MaxQueueSize = MaxQueue,
        DropPolicy = DropPolicy.Reject,

        // Signature is Action<AsyncExecutable, DropReason>. The rejected job itself is recycled by
        // the library and never handed out, so all we can do is decide the session's fate.
        OnDropped = (actor, reason) => session.OnJobDropped(actor, reason),

        // Non-worker producers only enqueue; a worker runs the job.
        Mode = ExecutionMode.Scheduled,

        // Fairness: after 64 jobs the flushing worker hands this actor back to the ready queue so
        // one chatty client cannot own a worker while 199 others wait.
        MaxJobsPerFlush = 64,
    };

    /// <summary>Entity id, unique for the lifetime of the process.</summary>
    public int Id { get; }

    /// <summary>Login name.</summary>
    public string DisplayName { get; }

    /// <summary>The connection this entity speaks through.</summary>
    public SessionConnection Session => _session;

    /// <summary>Queue a move. Called from the session's sequencer drain, on a worker.</summary>
    public void PostMove(MoveRequest request) =>
        DoAsync(static s => s.Self.ApplyMove(s.Request), (Self: this, Request: request));

    /// <summary>Queue a chat line.</summary>
    public void PostChat(ChatRequest request) =>
        DoAsync(static s => s.Self.ApplyChat(s.Request), (Self: this, Request: request));

    private void ApplyMove(MoveRequest request)
    {
        _x = request.X;
        _y = request.Y;
        _moves++;

        // Tell the world where we are. The world actor owns the registry the snapshot tick reads,
        // so this is an actor-to-actor post, not a shared dictionary write.
        _world.PostPosition(Id, _x, _y);

        _session.TrySend(FrameCodec.Encode(Op.MoveAck, new MoveResponse
        {
            EntityId = Id,
            X = _x,
            Y = _y,
            ClientTicks = request.ClientTicks,
        }));
    }

    private void ApplyChat(ChatRequest request)
    {
        _chats++;
        _session.TrySend(FrameCodec.Encode(Op.ChatAck, new ChatResponse
        {
            EntityId = Id,
            Text = request.Text,
            ClientTicks = request.ClientTicks,
        }));
    }

    /// <summary>Moves and chats handled so far. Only read for diagnostics.</summary>
    public (long Moves, long Chats) Counters => (Interlocked.Read(ref _moves), Interlocked.Read(ref _chats));

    /// <inheritdoc />
    protected override void OnJobError(Exception exception)
    {
        JobLog.Error($"[entity #{Id}] job failed", exception);
        _session.Close("entity job failed");
    }
}

/// <summary>
/// The single owner of the entity registry.
///
/// <para>Everything that mutates the registry is a job on this actor, so the accept thread, a
/// worker draining a session and the tick timer never race. The tick itself is a
/// <c>DoAsyncEvery</c> timer on this same actor, which means the snapshot is built while nothing
/// else is touching the registry — no lock, no copy-on-read.</para>
/// </summary>
public sealed class WorldActor : AsyncExecutable
{
    /// <summary>
    /// Entities carried in one snapshot. The frame length field is 16 bits, so an unbounded
    /// snapshot would eventually not fit; a real server would send an area-of-interest slice
    /// instead (see <c>AdvancedMmorpgServer</c>'s AOI grid).
    /// </summary>
    public const int MaxSnapshotEntities = 64;

    private readonly Dictionary<int, EntityActor> _entities = [];
    private readonly Dictionary<int, SnapshotEntity> _positions = [];
    private readonly TimeSpan _tickPeriod;

    private ITimerHandle? _tick;
    private long _tickCount;
    private long _snapshotsSent;
    private int _entityCount;
    private int _nextEntityId;

    /// <summary>Create the world.</summary>
    public WorldActor(JobSystem system, TimeSpan tickPeriod)
        : base(new JobOptions
        {
            Name = "world",
            System = system,
            MaxQueueSize = 100_000,
            DropPolicy = DropPolicy.Reject,
            OnDropped = static (actor, reason) =>
            {
                if (reason != DropReason.ShuttingDown)
                    JobLog.Warn($"[world] job dropped: {reason} (actor {actor.Name})");
            },
            Mode = ExecutionMode.Scheduled,
            MaxJobsPerFlush = 512,
        })
    {
        _tickPeriod = tickPeriod;
    }

    /// <summary>Entities currently in the world. Snapshot value, safe to read from any thread.</summary>
    public int EntityCount => Volatile.Read(ref _entityCount);

    /// <summary>Ticks completed.</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    /// <summary>Snapshot frames handed to sessions.</summary>
    public long SnapshotsSent => Interlocked.Read(ref _snapshotsSent);

    /// <summary>Hand out the next entity id. Safe from the IO path.</summary>
    public int NextEntityId() => Interlocked.Increment(ref _nextEntityId);

    /// <summary>Start the broadcast tick. Idempotent-ish: call once at startup.</summary>
    public void StartTick()
    {
        // DoAsyncEvery survives an exception in one tick, unlike the "job re-schedules itself" idiom.
        _tick = DoAsyncEvery(_tickPeriod, Tick, initialDelay: _tickPeriod);
        JobLog.Info($"[world] snapshot tick every {_tickPeriod.TotalMilliseconds:F0}ms");
    }

    /// <summary>Register a logged-in entity.</summary>
    public void PostAdd(EntityActor entity) =>
        DoAsync(static s => s.Self.AddCore(s.Entity), (Self: this, Entity: entity));

    /// <summary>Remove an entity that disconnected.</summary>
    public void PostRemove(int entityId) =>
        DoAsync(static s => s.Self.RemoveCore(s.Id), (Self: this, Id: entityId));

    /// <summary>Record a position for the next snapshot.</summary>
    public void PostPosition(int entityId, float x, float y) =>
        DoAsync(static s => s.Self.SetPosition(s.Id, s.X, s.Y), (Self: this, Id: entityId, X: x, Y: y));

    /// <summary>
    /// Cancel the tick and empty the registry, then complete. Awaited by the host during shutdown
    /// so the system can reach quiescence — a repeating timer left running would keep
    /// <c>PendingTimerCount</c> above zero and <c>StopAsync</c> would spin until its timeout.
    /// </summary>
    public Task<int> StopAsync() => Ask(() =>
    {
        _tick?.Cancel();
        _tick = null;
        var removed = _entities.Count;
        _entities.Clear();
        _positions.Clear();
        Volatile.Write(ref _entityCount, 0);
        return removed;
    });

    private void AddCore(EntityActor entity)
    {
        _entities[entity.Id] = entity;
        _positions[entity.Id] = new SnapshotEntity { Id = entity.Id };
        Volatile.Write(ref _entityCount, _entities.Count);
    }

    private void RemoveCore(int entityId)
    {
        _entities.Remove(entityId);
        _positions.Remove(entityId);
        Volatile.Write(ref _entityCount, _entities.Count);
    }

    private void SetPosition(int entityId, float x, float y)
    {
        if (!_positions.TryGetValue(entityId, out var slot))
            return;     // already gone
        slot.X = x;
        slot.Y = y;
    }

    private void Tick()
    {
        var tick = Interlocked.Increment(ref _tickCount);
        if (_entities.Count == 0)
            return;

        var count = Math.Min(_positions.Count, MaxSnapshotEntities);
        var entities = new SnapshotEntity[count];
        var i = 0;
        foreach (var slot in _positions.Values)
        {
            if (i == count) break;
            entities[i++] = slot;
        }

        // Serialize once, send the same array to every session. With 200 clients that is one
        // MessagePack pass per tick instead of 200.
        var frame = FrameCodec.Encode(Op.Snapshot, new SnapshotMessage
        {
            Tick = tick,
            TotalEntities = _positions.Count,
            Entities = entities,
        });

        var sent = 0;
        foreach (var entity in _entities.Values)
        {
            if (entity.Session.TrySend(frame))
                sent++;
        }
        Interlocked.Add(ref _snapshotsSent, sent);
    }
}
