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
/// Free list for one job type: a per-thread stack, with hand-off to a shared pool in batches.
///
/// <para>The shape matters more than it looks. The previous pool was a <c>ConcurrentBag</c> plus a
/// shared <c>long</c>, which put three read-modify-writes on one cache line into <b>every</b> job:
/// a decrement to rent, a read and an increment to recycle, and the bag's own
/// empty-to-non-empty transition counter — which, on a thread that rents and recycles one at a
/// time, moves on every single job. Every thread in the process contended for that line, so
/// throughput fell as workers were added instead of rising. It is the same problem
/// <see cref="StripedCounter"/> exists to solve, left in the one place it hurt most.</para>
///
/// <para>Renting and recycling on the same thread — <see cref="ExecutionMode.LeaderFlush"/>, and
/// every actor-to-actor call — now touches no shared memory at all. The asymmetric case (a producer
/// rents, a worker recycles) drains one side and fills the other, so the two exchange a batch of
/// <see cref="BatchSize"/> at a time: shared traffic falls by that factor and the allocation count
/// stays at zero.</para>
/// </summary>
/// <typeparam name="T">The pooled job type.</typeparam>
internal static class JobPool<T> where T : class
{
    /// <summary>Jobs one thread parks locally before handing a batch to the shared pool.</summary>
    private const int LocalCapacity = 256;

    /// <summary>Jobs moved between a thread's local stack and the shared pool in one operation.</summary>
    private const int BatchSize = 32;

    [ThreadStatic] private static T?[]? _local;
    [ThreadStatic] private static int _localCount;

    private static readonly ConcurrentQueue<T[]> SharedBatches = new();
    private static int _sharedBatchCount;

    /// <summary>Cap on the shared pool. <c>0</c> or less disables pooling entirely.</summary>
    public static int MaxPoolSize { get; set; } = 16 * 1024;

    /// <summary>Instances parked in the shared pool. Per-thread stacks are not counted.</summary>
    public static long SharedSize => (long)Volatile.Read(ref _sharedBatchCount) * BatchSize;

    /// <summary>Take an instance, or <c>null</c> if neither pool has one.</summary>
    public static T? Take()
    {
        if (MaxPoolSize <= 0)
            return null;

        var count = _localCount;
        if (count == 0)
        {
            if (!SharedBatches.TryDequeue(out var batch))
                return null;

            Interlocked.Decrement(ref _sharedBatchCount);
            var refill = _local ??= new T?[LocalCapacity];
            Array.Copy(batch, refill, BatchSize);
            count = BatchSize;

            // `batch` is now garbage: one small gen0 array per 32 jobs, and only on the
            // cross-thread path. Pooling those arrays too would cost a shared queue operation on
            // each side to save an allocation cheaper than the operation itself.
        }

        var local = _local!;
        var item = local[--count]!;
        local[count] = null;
        _localCount = count;
        return item;
    }

    /// <summary>Park an instance for reuse. Silently drops it when the pool is full or disabled.</summary>
    public static void Return(T item)
    {
        if (MaxPoolSize <= 0)
            return;

        var local = _local ??= new T?[LocalCapacity];
        var count = _localCount;

        if (count == LocalCapacity)
        {
            // The local stack is full, which means this thread recycles more than it rents — the
            // worker half of the asymmetric case. Hand a batch over so the renting side can use it.
            //
            // Claim the slot with the increment and give it back if it was not there, rather than
            // checking first: two threads overflowing at once would both see room for the last
            // batch and the cap would drift upward by a batch per racing thread.
            if (Interlocked.Increment(ref _sharedBatchCount) * (long)BatchSize <= MaxPoolSize)
            {
                var batch = new T[BatchSize];
                Array.Copy(local, LocalCapacity - BatchSize, batch, 0, BatchSize);
                SharedBatches.Enqueue(batch);
            }
            else
            {
                // Over the cap: drop the batch, which is what the cap is for.
                Interlocked.Decrement(ref _sharedBatchCount);
            }

            Array.Clear(local, LocalCapacity - BatchSize, BatchSize);
            count = LocalCapacity - BatchSize;
        }

        local[count] = item;
        _localCount = count + 1;
    }

    /// <summary>
    /// Empty the shared pool and the calling thread's local stack. Test and benchmark helper —
    /// other threads' local stacks cannot be reached from here and are left alone.
    /// </summary>
    public static void Clear()
    {
        while (SharedBatches.TryDequeue(out _))
        {
        }
        Interlocked.Exchange(ref _sharedBatchCount, 0);

        if (_local is { } local)
            Array.Clear(local);
        _localCount = 0;
    }
}

/// <summary>
/// Pooled <see cref="Action"/> job. The pool is capped, so a burst beyond the cap
/// is left to the GC instead of growing memory without bound.
/// </summary>
public sealed class Job : JobEntry
{
    /// <summary>
    /// Cap on the shared pool. Size it to the steady-state peak of concurrent in-flight jobs;
    /// anything beyond is collected normally. <c>0</c> disables pooling. Default 16384.
    ///
    /// Each thread also keeps a small local stack of its own, which this does not cover — see
    /// <see cref="PoolSize"/>.
    /// </summary>
    public static int MaxPoolSize
    {
        get => JobPool<Job>.MaxPoolSize;
        set => JobPool<Job>.MaxPoolSize = value;
    }

    /// <summary>
    /// Instances parked in the shared pool (metric). Per-thread stacks hold a bounded number more
    /// — at most 256 per thread that has recycled a job — and cannot be counted from here, so this
    /// reads low on a workload that rents and recycles on the same thread.
    /// </summary>
    public static long PoolSize => JobPool<Job>.SharedSize;

    private Action? _action;

    private Job() { }

    /// <summary>Take an instance from the pool, or allocate one.</summary>
    public static Job Rent(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var job = JobPool<Job>.Take() ?? new Job();
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
        JobPool<Job>.Return(this);
    }

    /// <summary>Empty the pool. Test/benchmark helper.</summary>
    internal static void ClearPool() => JobPool<Job>.Clear();
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
    /// <summary>Cap on the shared pool for this state type. <c>0</c> disables pooling.</summary>
    public static int MaxPoolSize
    {
        get => JobPool<Job<TState>>.MaxPoolSize;
        set => JobPool<Job<TState>>.MaxPoolSize = value;
    }

    /// <summary>Instances parked in the shared pool for this state type (metric).</summary>
    public static long PoolSize => JobPool<Job<TState>>.SharedSize;

    private Action<TState>? _action;
    private TState? _state;

    private Job() { }

    /// <summary>Take an instance from the pool, or allocate one.</summary>
    public static Job<TState> Rent(Action<TState> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);
        var job = JobPool<Job<TState>>.Take() ?? new Job<TState>();
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
        JobPool<Job<TState>>.Return(this);
    }

    /// <summary>Empty the pool. Test/benchmark helper.</summary>
    internal static void ClearPool() => JobPool<Job<TState>>.Clear();
}
