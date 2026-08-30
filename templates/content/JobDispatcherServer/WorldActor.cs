using JobDispatcherNET;

namespace JobDispatcherServer;

/// <summary>
/// Owns the session registry. Every mutation goes through its own queue, so the dictionary needs
/// no lock — that is the whole point of the actor model here.
/// </summary>
internal sealed class WorldActor : AsyncExecutable
{
    private readonly Dictionary<long, SessionActor> _sessions = [];
    private ITimerHandle? _tick;
    private long _nextId;

    public WorldActor(JobSystem system)
        : base(new JobOptions
        {
            Name = "World",
            System = system,
            MaxQueueSize = 10_000,

            // The console thread also asks this actor for status. Scheduled mode keeps that caller
            // from becoming the world's leader and running game logic on a non-worker thread.
            Mode = ExecutionMode.Scheduled,

            OnDropped = static (actor, reason) =>
                JobLog.Warn($"[{actor.Name}] job refused ({reason}), queue={actor.RemainingTaskCount}"),
        })
    {
    }

    public long AllocateId() => Interlocked.Increment(ref _nextId);

    /// <summary>
    /// A cancellable repeating timer. Prefer this over a job that re-schedules itself: one throwing
    /// tick no longer ends the chain, and shutdown just cancels the handle.
    /// </summary>
    public void Start() =>
        _tick = DoAsyncEvery(TimeSpan.FromMilliseconds(100), Tick);

    public void Stop()
    {
        _tick?.Cancel();
        DoAsync(static w => w.CloseAll(), this);
    }

    public void AddSession(SessionActor session) =>
        DoAsync(static t => t.W.Register(t.S), (W: this, S: session));

    public void RemoveSession(long id) =>
        DoAsync(static t => t.W.Unregister(t.Id), (W: this, Id: id));

    public void Broadcast(string message) =>
        DoAsync(static t => t.W.Fanout(t.Message), (W: this, Message: message));

    /// <summary>
    /// A consistent read, computed on the world's own queue. AskSync throws rather than deadlocking
    /// if it is ever called from inside another actor's job.
    /// </summary>
    public int GetSessionCount()
    {
        try
        {
            return AskSync(() => _sessions.Count, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            return -1;
        }
    }

    private void Register(SessionActor session)
    {
        _sessions[session.Id] = session;
        JobLog.Info($"session {session.Id} joined ({_sessions.Count} online)");
    }

    private void Unregister(long id)
    {
        if (_sessions.Remove(id))
            JobLog.Info($"session {id} left ({_sessions.Count} online)");
    }

    private void Fanout(string message)
    {
        foreach (var session in _sessions.Values)
            session.Send(message);
    }

    private void Tick()
    {
        // Your simulation step goes here. It runs on a worker thread, serialized against every
        // other job on this actor.
    }

    private void CloseAll()
    {
        foreach (var session in _sessions.Values)
            session.Close();
        _sessions.Clear();
    }
}
