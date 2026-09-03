using System.Collections.Concurrent;

namespace JobDispatcherNET;

/// <summary>
/// Keeps items from one source in arrival order and guarantees only one thread handles them at a
/// time.
///
/// <para><b>Why this exists.</b> An actor serialises its own jobs, but it does not fix the order in
/// which two producers reach it. If a socket thread pushes <c>EnterZone</c> and then <c>Move</c>,
/// and two workers pick them up, nothing stops <c>Move</c> from being queued first. Funnel a
/// session's packets through one sequencer and the order is the order they arrived.</para>
///
/// <para><b>Use it like this.</b> The IO thread only calls <see cref="Enqueue"/>. The first caller
/// to find no drain scheduled wins a CAS and invokes the <c>scheduleDrain</c> callback once; that
/// callback hands a drain action to a worker (<see cref="JobSystem.Post(Action)"/> does this for
/// you if you use the <see cref="Sequencer{T}(JobSystem, Action{T}, Action{Exception}, int, int)"/>
/// constructor). The worker runs the handler for each queued item in order.</para>
///
/// <para><b>Bound it.</b> A sequencer fed by a network socket needs <c>maxPending</c>. Without one a
/// single client that sends faster than the handler drains grows that session's queue until the
/// process runs out of memory.</para>
/// </summary>
public sealed class Sequencer<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly Action<T> _handler;
    private readonly Func<Action, bool> _scheduleDrain;
    private readonly Action _drainAction;
    private readonly Action<Exception>? _onError;
    private readonly int _maxPending;
    private readonly int _maxItemsPerDrain;

    // Kept by hand rather than read from the queue: ConcurrentQueue.Count walks its segments and
    // can spin, which is the wrong shape for a check on every inbound packet. Only maintained when
    // there is a bound to enforce — see TryReserveSlot.
    private int _pending;
    private long _dropped;
    private int _drainScheduled;
    private int _stopped;
    private int _aborted;

    /// <param name="handler">Handles one item. Called serially, on whichever thread runs the drain.</param>
    /// <param name="scheduleDrain">Hands the drain action to a worker thread.</param>
    /// <param name="onError">Called when <paramref name="handler"/> throws. Defaults to logging.</param>
    /// <param name="maxPending">
    /// Most items that may be accepted and unhandled at once; <c>0</c> (the default) is unbounded.
    /// <b>Set this on anything fed by untrusted input.</b> The documented pattern is one sequencer
    /// per session, and an unbounded one is a per-session memory bomb: a client that sends faster
    /// than the handler drains grows that queue until the process dies. This is the sequencer's
    /// counterpart to <see cref="JobOptions.MaxQueueSize"/>, and it behaves the same way —
    /// <see cref="Enqueue"/> returns <c>false</c> and the network layer drops the session.
    /// </param>
    /// <param name="maxItemsPerDrain">
    /// Items one drain handles before handing the rest back to the worker pool; <c>0</c> (the
    /// default) drains to empty. The counterpart to <see cref="JobOptions.MaxJobsPerFlush"/>: it
    /// stops one flooding session from owning a worker until its queue runs dry.
    /// </param>
    public Sequencer(Action<T> handler, Action<Action> scheduleDrain, Action<Exception>? onError = null,
        int maxPending = 0, int maxItemsPerDrain = 0)
        : this(handler, Wrap(scheduleDrain), onError, maxPending, maxItemsPerDrain)
    {
    }

    /// <summary>
    /// Convenience overload that schedules drains onto <paramref name="system"/>'s worker pool,
    /// so callers no longer need their own inbound command queue.
    /// </summary>
    /// <param name="system">System whose worker pool runs the drains.</param>
    /// <param name="handler">Handles one item. Called serially, on whichever worker runs the drain.</param>
    /// <param name="onError">Called when <paramref name="handler"/> throws. Defaults to logging.</param>
    /// <param name="maxPending">
    /// Most items that may be accepted and unhandled at once; <c>0</c> (the default) is unbounded.
    /// Set it on anything fed by untrusted input — see the other constructor.
    /// </param>
    /// <param name="maxItemsPerDrain">
    /// Items one drain handles before handing the rest back to the pool; <c>0</c> drains to empty.
    /// </param>
    public Sequencer(JobSystem system, Action<T> handler, Action<Exception>? onError = null,
        int maxPending = 0, int maxItemsPerDrain = 0)
        : this(handler, PostTo(system), onError, maxPending, maxItemsPerDrain)
    {
    }

    private Sequencer(Action<T> handler, Func<Action, bool> scheduleDrain, Action<Exception>? onError,
        int maxPending, int maxItemsPerDrain)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPending);
        ArgumentOutOfRangeException.ThrowIfNegative(maxItemsPerDrain);

        _handler = handler;
        _scheduleDrain = scheduleDrain;
        _onError = onError;
        _maxPending = maxPending;
        _maxItemsPerDrain = maxItemsPerDrain;

        // Cached, because a method group converted at the call site allocates a fresh delegate each
        // time. A drain is scheduled whenever the queue goes from empty to non-empty, so on a server
        // with thousands of sessions doing that a few times a second it is a steady gen0 drip.
        _drainAction = Drain;
    }

    private static Func<Action, bool> Wrap(Action<Action> scheduleDrain)
    {
        ArgumentNullException.ThrowIfNull(scheduleDrain);
        return drain =>
        {
            scheduleDrain(drain);
            return true;   // a caller-supplied scheduler has no way to say no
        };
    }

    private static Func<Action, bool> PostTo(JobSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system.Post;
    }

    /// <summary>
    /// Items accepted and not yet handled.
    ///
    /// <para>With a bound in force this is a counter, incremented before the item reaches the queue
    /// and released after the handler returns, so it reads high rather than low — which is what a
    /// bound needs. An unbounded sequencer keeps no counter and this falls back to the queue's own
    /// count, which is a snapshot approximation and walks the queue's segments to produce it.</para>
    /// </summary>
    public int PendingCount => _maxPending == 0 ? _queue.Count : Volatile.Read(ref _pending);

    /// <summary>The <c>maxPending</c> bound this sequencer was built with. <c>0</c> is unbounded.</summary>
    public int MaxPending => _maxPending;

    /// <summary>Items refused because <see cref="MaxPending"/> was reached.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>True once <see cref="Stop"/> or <see cref="Abort"/> has been called.</summary>
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <summary>
    /// Add an item. Returns <c>false</c> if the sequencer is stopped, so the caller can tell the
    /// difference between "queued" and "thrown away".
    ///
    /// <para>One caveat: a call that races <see cref="Abort"/> can return <c>true</c> and still have
    /// its item discarded. Nothing can close that window — the decision is made before
    /// <see cref="Abort"/> runs — so the drain path throws the item away rather than leaving it
    /// queued with nobody to take it.</para>
    /// </summary>
    public bool Enqueue(T item)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return false;

        if (!TryReserveSlot())
            return false;

        _queue.Enqueue(item);
        TryScheduleDrain();
        return true;
    }

    /// <summary>
    /// Claim one slot under <see cref="MaxPending"/>. A CAS rather than increment-then-check, for
    /// the same reason the actor's admission uses one: two producers that both incremented past the
    /// bound would each have to undo it, and by then the count has already lied.
    /// </summary>
    private bool TryReserveSlot()
    {
        if (_maxPending == 0)
        {
            // Nothing to claim, so nothing to write. Counting here put a shared read-modify-write on
            // one line into every item — raised by the producing IO thread, lowered by the worker
            // that handles it, one cache line ping-pong per session on top of the queue's own CAS.
            // A bounded sequencer needs that accounting to be exact; an unbounded one does not.
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref _pending);
            if (current >= _maxPending)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            if (Interlocked.CompareExchange(ref _pending, current + 1, current) == current)
                return true;
        }
    }

    private void TryScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
            return;

        bool scheduled;
        try
        {
            scheduled = _scheduleDrain(_drainAction);
        }
        catch
        {
            // Release the claim so a later Enqueue can try again. Interlocked for the same
            // ordering reason as the release in Drain.
            Interlocked.Exchange(ref _drainScheduled, 0);
            throw;
        }

        if (!scheduled)
        {
            // The system refused the drain: it is shutting down or disposed. Release the claim so
            // Stop, Abort or a later Enqueue can try again rather than leaving the queue with a
            // drain that is permanently "already scheduled" and will never run.
            Interlocked.Exchange(ref _drainScheduled, 0);
        }
    }

    private void Drain()
    {
        var handled = 0;

        try
        {
            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    // Dequeue first, check abort second. Stopping at the check instead would leave
                    // an item enqueued by a producer that raced Abort sitting in the queue for the
                    // life of the session: Abort has already run its own drain, and no later drain
                    // would take it. Aborted means "do not handle these", not "do not remove them".
                    if (Volatile.Read(ref _aborted) != 0)
                        continue;

                    try
                    {
                        _handler(item);
                    }
                    catch (Exception ex)
                    {
                        if (_onError is not null) _onError(ex);
                        else JobLog.Error("Sequencer handler error", ex);
                    }
                }
                finally
                {
                    // In a finally because _onError is user code and can throw: leaking a slot on
                    // that path would shrink a bounded sequencer a little on every handler failure
                    // until it refused everything. Unbounded sequencers claim no slot to release.
                    if (_maxPending != 0)
                        Interlocked.Decrement(ref _pending);
                }

                // Fairness, mirroring MaxJobsPerFlush: hand the rest back rather than letting one
                // flooding session hold a worker until its queue runs dry. The finally below
                // reschedules, so nothing is left behind.
                if (_maxItemsPerDrain != 0 && ++handled >= _maxItemsPerDrain)
                    return;
            }
        }
        finally
        {
            // Interlocked, not Volatile.Write: this is one half of a Dekker handshake with
            // Enqueue, and a release store does not order against the load of _queue that follows.
            // Without the full fence the store can still be sitting in this core's store buffer
            // while a producer's CAS reads the stale 1 and skips scheduling — leaving an item
            // queued with no drain pending, which for a closing session is its disconnect marker.
            Interlocked.Exchange(ref _drainScheduled, 0);

            // Anything that arrived between the last dequeue and the release above still has to
            // be taken off the queue. Neither Stopped nor Aborted belongs in this condition:
            // Stop() means "no new items", not "throw away the ones already accepted" — checking it
            // here is what used to lose a session's final disconnect marker — and an aborted
            // sequencer still has to drain-and-discard, or the item is stranded.
            if (!_queue.IsEmpty)
                TryScheduleDrain();
        }
    }

    /// <summary>
    /// Refuse new items. Everything already accepted is still handled, in order.
    /// Use this for an orderly session close.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        // A producer may have enqueued while we were flipping the flag; make sure it still drains.
        if (!_queue.IsEmpty && Volatile.Read(ref _aborted) == 0)
            TryScheduleDrain();
    }

    /// <summary>
    /// Refuse new items and discard everything still queued. Use only when the remaining items
    /// genuinely must not run — a hard shutdown, or a session whose socket is already gone.
    /// </summary>
    /// <returns>
    /// The number of items this call discarded. An item from a producer that was already inside
    /// <see cref="Enqueue"/> can land afterwards; it is discarded by the drain instead, so it is not
    /// counted here.
    /// </returns>
    public int Abort()
    {
        Volatile.Write(ref _stopped, 1);

        // Interlocked, not a release store: the loop below reads the queue that a producer past the
        // Stopped check is about to write, and store-load is the reordering that lets the two miss
        // each other. The fence does not close the window — nothing can — but it narrows it to the
        // producers genuinely in flight, and the reschedule below covers those.
        Interlocked.Exchange(ref _aborted, 1);

        var discarded = 0;
        while (_queue.TryDequeue(out _))
        {
            if (_maxPending != 0)
                Interlocked.Decrement(ref _pending);
            discarded++;
        }

        // A racing producer calls TryScheduleDrain too, but its CAS can lose to a drain that is
        // already on its way out and has passed its own emptiness check. Without this the item — and
        // whatever it holds a reference to — sits in the queue for the life of the process.
        if (!_queue.IsEmpty)
            TryScheduleDrain();

        return discarded;
    }
}
