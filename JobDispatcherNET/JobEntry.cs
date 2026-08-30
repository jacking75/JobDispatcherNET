using System.Collections.Concurrent;

namespace JobDispatcherNET;

/// <summary>
/// Base of every unit of work placed on an actor queue.
/// </summary>
public abstract class JobEntry
{
    /// <summary>Run the job. Implementations recycle themselves afterwards.</summary>
    public abstract void Execute();

    /// <summary>
    /// Recycle without running — used when a job is refused or a timer is cancelled.
    /// </summary>
    internal abstract void Discard();
}

/// <summary>
/// Pooled <see cref="Action"/> job. The pool is capped, so a burst beyond the cap
/// is left to the GC instead of growing memory without bound.
/// </summary>
public sealed class Job : JobEntry
{
    private static readonly ConcurrentBag<Job> Pool = [];
    private static long _poolSize;

    /// <summary>
    /// Maximum number of pooled instances. Size it to the steady-state peak of concurrent
    /// in-flight jobs; anything beyond is collected normally. Default 16384.
    /// </summary>
    public static int MaxPoolSize { get; set; } = 16 * 1024;

    /// <summary>Instances currently parked in the pool (metric).</summary>
    public static long PoolSize => Interlocked.Read(ref _poolSize);

    private Action? _action;

    private Job() { }

    /// <summary>Take an instance from the pool, or allocate one.</summary>
    public static Job Rent(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Pool.TryTake(out var job))
            Interlocked.Decrement(ref _poolSize);
        else
            job = new Job();
        job._action = action;
        return job;
    }

    /// <inheritdoc />
    public override void Execute()
    {
        var action = _action;
        try
        {
            action?.Invoke();
        }
        finally
        {
            Recycle();
        }
    }

    internal override void Discard() => Recycle();

    private void Recycle()
    {
        _action = null;
        if (Interlocked.Read(ref _poolSize) < MaxPoolSize)
        {
            Interlocked.Increment(ref _poolSize);
            Pool.Add(this);
        }
    }

    /// <summary>Empty the pool. Test/benchmark helper.</summary>
    internal static void ClearPool()
    {
        while (Pool.TryTake(out _))
            Interlocked.Decrement(ref _poolSize);
        Interlocked.Exchange(ref _poolSize, 0);
    }
}

/// <summary>
/// Pooled job carrying explicit state, so the hot path allocates no closure.
/// Pass a <c>static</c> lambda and put every captured value in <typeparamref name="TState"/>:
/// <code>
/// actor.DoAsync(static t => t.Self.ProcessMove(t.X, t.Y), (Self: this, X: x, Y: y));
/// </code>
/// </summary>
public sealed class Job<TState> : JobEntry
{
    private static readonly ConcurrentBag<Job<TState>> Pool = [];
    private static long _poolSize;

    /// <summary>Maximum number of pooled instances for this state type.</summary>
    public static int MaxPoolSize { get; set; } = 16 * 1024;

    /// <summary>Instances currently parked in the pool (metric).</summary>
    public static long PoolSize => Interlocked.Read(ref _poolSize);

    private Action<TState>? _action;
    private TState? _state;

    private Job() { }

    /// <summary>Take an instance from the pool, or allocate one.</summary>
    public static Job<TState> Rent(Action<TState> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Pool.TryTake(out var job))
            Interlocked.Decrement(ref _poolSize);
        else
            job = new Job<TState>();
        job._action = action;
        job._state = state;
        return job;
    }

    /// <inheritdoc />
    public override void Execute()
    {
        var action = _action;
        var state = _state;
        try
        {
            // state may legitimately be null for reference types — pass it through as-is.
            action?.Invoke(state!);
        }
        finally
        {
            Recycle();
        }
    }

    internal override void Discard() => Recycle();

    private void Recycle()
    {
        _action = null;
        _state = default;
        if (Interlocked.Read(ref _poolSize) < MaxPoolSize)
        {
            Interlocked.Increment(ref _poolSize);
            Pool.Add(this);
        }
    }

    /// <summary>Empty the pool. Test/benchmark helper.</summary>
    internal static void ClearPool()
    {
        while (Pool.TryTake(out _))
            Interlocked.Decrement(ref _poolSize);
        Interlocked.Exchange(ref _poolSize, 0);
    }
}
