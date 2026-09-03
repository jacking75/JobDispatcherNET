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
/// you if you use the <see cref="Sequencer{T}(JobSystem, Action{T}, Action{Exception})"/>
/// constructor). The worker runs the handler for each queued item in order.</para>
/// </summary>
public sealed class Sequencer<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly Action<T> _handler;
    private readonly Action<Action> _scheduleDrain;
    private readonly Action<Exception>? _onError;
    private int _drainScheduled;
    private int _stopped;
    private int _aborted;

    /// <param name="handler">Handles one item. Called serially, on whichever thread runs the drain.</param>
    /// <param name="scheduleDrain">Hands the drain action to a worker thread.</param>
    /// <param name="onError">Called when <paramref name="handler"/> throws. Defaults to logging.</param>
    public Sequencer(Action<T> handler, Action<Action> scheduleDrain, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(scheduleDrain);
        _handler = handler;
        _scheduleDrain = scheduleDrain;
        _onError = onError;
    }

    /// <summary>
    /// Convenience overload that schedules drains onto <paramref name="system"/>'s worker pool,
    /// so callers no longer need their own inbound command queue.
    /// </summary>
    public Sequencer(JobSystem system, Action<T> handler, Action<Exception>? onError = null)
        : this(handler, ScheduleOn(system), onError)
    {
    }

    private static Action<Action> ScheduleOn(JobSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system.Post;
    }

    /// <summary>Items waiting to be handled.</summary>
    public int PendingCount => _queue.Count;

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

        _queue.Enqueue(item);
        TryScheduleDrain();
        return true;
    }

    private void TryScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
            return;

        try
        {
            _scheduleDrain(Drain);
        }
        catch
        {
            // Release the claim so a later Enqueue can try again. Interlocked for the same
            // ordering reason as the release in Drain.
            Interlocked.Exchange(ref _drainScheduled, 0);
            throw;
        }
    }

    private void Drain()
    {
        try
        {
            while (_queue.TryDequeue(out var item))
            {
                // Dequeue first, check abort second. Stopping at the check instead would leave an
                // item enqueued by a producer that raced Abort sitting in the queue for the life of
                // the session: Abort has already run its own drain, and no later drain would take
                // it. Aborted means "do not handle these", not "do not remove them".
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
            discarded++;

        // A racing producer calls TryScheduleDrain too, but its CAS can lose to a drain that is
        // already on its way out and has passed its own emptiness check. Without this the item — and
        // whatever it holds a reference to — sits in the queue for the life of the process.
        if (!_queue.IsEmpty)
            TryScheduleDrain();

        return discarded;
    }
}
