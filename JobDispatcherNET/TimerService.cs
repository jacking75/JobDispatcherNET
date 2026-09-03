using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JobDispatcherNET;

/// <summary>
/// A scheduled timer that can be cancelled.
/// </summary>
public interface ITimerHandle
{
    /// <summary>
    /// Cancel the timer. Returns <c>true</c> if the callback will not run — including when the
    /// timer has already fired and its job is still sitting on the owning actor's queue. Returns
    /// <c>false</c> only once the callback has actually run, or if it was already cancelled. For a
    /// repeating timer, cancelling stops all further firings and drops any tick already queued.
    ///
    /// <para>Cancelling from inside one of the owner's own jobs — a <c>Despawn</c> job, typically —
    /// therefore guarantees no further tick runs on that actor: the actor runs one job at a time,
    /// so the cancel is committed before any queued tick gets its turn.</para>
    /// </summary>
    bool Cancel();

    /// <summary>
    /// True while the callback may still run: the timer is scheduled, or it has fired and is
    /// waiting its turn on the owner's queue.
    /// </summary>
    bool IsPending { get; }
}

/// <summary>How hard the timer thread works to hit the exact due time.</summary>
public enum TimerPrecision
{
    /// <summary>
    /// Sleep until due. Accuracy is bounded by the OS timer resolution — roughly 15.6 ms on stock
    /// Windows, ~1 ms on Linux. Costs no CPU. The right default for most servers.
    /// </summary>
    Coarse,

    /// <summary>
    /// Sleep until shortly before due, then spin. Sub-millisecond accuracy at the cost of one
    /// thread briefly burning CPU before each firing. Use for tight simulation ticks.
    /// </summary>
    High,
}

/// <summary>Why a timer stopped being pending. Decides which counter is bumped.</summary>
internal enum TimerRetirement
{
    /// <summary>No counter. Used when the firing was already counted as a dropped job.</summary>
    None,

    /// <summary>The caller cancelled it.</summary>
    Cancelled,

    /// <summary>The timer service was disposed with the timer still armed.</summary>
    Discarded,
}

/// <summary>
/// One dedicated timer thread per <see cref="JobSystem"/>: a priority queue plus
/// <see cref="Monitor.Wait(object, int)"/>, with no dependency on the thread pool.
///
/// This replaces the per-thread <c>TimerQueue</c> of v1/v2.0, where a worker restart or a
/// short-lived producer thread silently took every timer it owned down with it.
/// </summary>
internal sealed class TimerService : IDisposable
{
    private const int MaxWaitMs = 1000;

    private readonly object _lock = new();
    private readonly PriorityQueue<TimerEntry, long> _queue = new();

    // Two buffers, swapped rather than copied: collecting under the lock and dispatching outside it
    // used to hand over a fresh array on every iteration that fired anything, which on a server
    // arming a timer per attack is a steady stream of garbage for no reason.
    private List<TimerEntry> _dueBuffer = [];
    private List<TimerEntry> _dispatchBuffer = [];
    private readonly JobSystem _system;
    private readonly TimerPrecision _precision;
    private readonly int _spinThresholdMs;
    private readonly TimeSpan _minPeriod;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly Thread _thread;
    private long _pending;
    private int _disposed;
    private int _started;

    public TimerService(JobSystem system, TimerPrecision precision, int spinThresholdMs)
    {
        _system = system;
        _precision = precision;
        _spinThresholdMs = Math.Max(1, spinThresholdMs);
        _minPeriod = system.Options.MinTimerPeriod > TimeSpan.Zero ? system.Options.MinTimerPeriod : TimeSpan.Zero;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = $"JobTimer-{system.Name}",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>Monotonic milliseconds since this service was created.</summary>
    public long CurrentTick => (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    /// <summary>Timers scheduled and not yet fired or cancelled.</summary>
    public long PendingCount => Interlocked.Read(ref _pending);

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _started) != 0)
            return;
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _thread.Start();
    }

    public ITimerHandle Schedule(AsyncExecutable owner, TimeSpan delay, JobEntry job)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            job.Discard();
            _system.Metrics.OnTimerDiscarded();
            return CancelledHandle.Instance;
        }

        EnsureStarted();
        var entry = new TimerEntry(this, owner, job, repeatAction: null, TimeSpan.Zero, repeating: false);
        if (Enqueue(entry, CurrentTick + ToMillis(delay), isNew: true))
            return entry;

        entry.Retire(TimerRetirement.Discarded);
        return CancelledHandle.Instance;
    }

    public ITimerHandle ScheduleRepeating(AsyncExecutable owner, TimeSpan period, TimeSpan initialDelay, Action action)
    {
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), "period must be positive");

        if (period < _minPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(period),
                $"period must be at least {_minPeriod.TotalMilliseconds:0.###}ms. A shorter one re-arms the " +
                "timer every tick and, under TimerPrecision.High, spins the timer thread. " +
                $"Lower {nameof(JobSystemOptions)}.{nameof(JobSystemOptions.MinTimerPeriod)} if you really need it.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            _system.Metrics.OnTimerDiscarded();
            return CancelledHandle.Instance;
        }

        EnsureStarted();
        var entry = new TimerEntry(this, owner, job: null, repeatAction: action, period, repeating: true);
        if (Enqueue(entry, CurrentTick + ToMillis(initialDelay), isNew: true))
            return entry;

        entry.Retire(TimerRetirement.Discarded);
        return CancelledHandle.Instance;
    }

    private static long ToMillis(TimeSpan span)
    {
        var ms = span.TotalMilliseconds;
        if (ms <= 0) return 0;
        return (long)Math.Ceiling(ms);
    }

    /// <summary>Place an entry on the queue and wake the timer thread.</summary>
    /// <param name="entry">The timer to schedule.</param>
    /// <param name="dueTick">Monotonic millisecond tick at which it should fire.</param>
    /// <param name="isNew">
    /// False when re-arming a repeating timer. A repeating timer counts as one pending timer for
    /// its whole life, so re-arming must not bump the counter again — doing so made
    /// <see cref="PendingCount"/> climb once per tick and never come back down.
    /// </param>
    /// <returns>
    /// False if the service was disposed. The disposed check has to happen under the lock: the
    /// check in <see cref="Schedule"/> can be overtaken by a concurrent <see cref="Dispose"/>, and
    /// an entry that lands after <see cref="DiscardAll"/> has run would sit in the queue with
    /// nobody left to drain it, pinning <see cref="PendingCount"/> above zero forever and making
    /// every later drain time out.
    /// </returns>
    private bool Enqueue(TimerEntry entry, long dueTick, bool isNew)
    {
        entry.DueTick = dueTick;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            // Only wake the timer thread when this entry actually changes when it next has
            // something to do: the queue was empty (it is parked for a full MaxWaitMs) or this
            // entry is due before the one it is already waiting for. Pulsing unconditionally woke
            // it once per scheduled timer, which on a server arming a timer per attack or skill is
            // tens of thousands of pointless wake-ups a second.
            var wake = !_queue.TryPeek(out _, out var nextDue) || dueTick < nextDue;

            _queue.Enqueue(entry, dueTick);
            if (isNew)
            {
                Interlocked.Increment(ref _pending);
                entry.MarkArmed();      // it now holds a pending slot; Retire is what gives it back
            }

            if (wake)
                Monitor.Pulse(_lock);
            return true;
        }
    }

    /// <summary>Account for a timer reaching its terminal state.</summary>
    /// <param name="releaseSlot">
    /// True when the entry still held a slot in <see cref="PendingCount"/>. A one-shot gives its
    /// slot back the moment it is handed to its actor, so a cancel landing after that must not give
    /// it back a second time — a negative count made shutdown skip waiting for timers that really
    /// were still armed.
    /// </param>
    /// <param name="retirement">Which counter to bump, if any.</param>
    internal void OnRetired(bool releaseSlot, TimerRetirement retirement)
    {
        if (releaseSlot)
            Interlocked.Decrement(ref _pending);

        switch (retirement)
        {
            case TimerRetirement.Cancelled:
                _system.Metrics.OnTimerCancelled();
                break;
            case TimerRetirement.Discarded:
                _system.Metrics.OnTimerDiscarded();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// The timer thread.
    ///
    /// Every iteration is guarded. This thread has no supervisor — nothing restarts it, and an
    /// escaping exception stops every timer on the system for the life of the process. The user
    /// code that runs here (an <see cref="JobOptions.OnDropped"/> callback, an
    /// <see cref="IJobLogger"/>, and the jobs themselves when there are no workers to hand them to)
    /// is not the library's to trust, so one bad iteration costs a log line and nothing more.
    /// </summary>
    private void Loop()
    {
        ThreadContext.CurrentSystem = _system;
        using var resolution = SystemTimerResolution.Acquire(_system.Options.RaiseSystemTimerResolution);

        var consecutiveFailures = 0;

        while (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                LoopOnce();
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _system.Logger.Error($"Timer thread '{_thread.Name}' iteration failed; continuing", ex);

                // A failure that repeats is usually a broken dependency rather than one bad timer.
                // Back off so the thread cannot spin a core logging the same thing thousands of
                // times a second, but keep the delay short enough that timers stay roughly on time
                // once whatever it was recovers.
                if (++consecutiveFailures >= 3)
                    Thread.Sleep(Math.Min(1000, 10 << Math.Min(consecutiveFailures, 7)));
            }
        }

        DiscardAll();
    }

    /// <summary>One pass: collect what is due, then either wait, spin, or dispatch.</summary>
    private void LoopOnce()
    {
        long spinTarget = -1;
        var due = false;

        lock (_lock)
        {
            CollectDueLocked();

            if (_dueBuffer.Count > 0)
            {
                // Move the entries out under the lock. DispatchDue runs unlocked, and Dispose can
                // call DiscardAll from another thread; swapping the two buffers means the two can
                // never touch the same list at once, and costs no allocation.
                (_dueBuffer, _dispatchBuffer) = (_dispatchBuffer, _dueBuffer);
                _dueBuffer.Clear();
                due = true;
            }

            if (!due)
            {
                if (_queue.Count == 0)
                {
                    Monitor.Wait(_lock, MaxWaitMs);
                    return;
                }

                _queue.TryPeek(out _, out var nextDue);
                var remaining = nextDue - CurrentTick;
                if (remaining <= 0)
                    return;

                if (_precision == TimerPrecision.High && remaining <= _spinThresholdMs)
                {
                    spinTarget = nextDue;
                }
                else
                {
                    Monitor.Wait(_lock, (int)Math.Min(remaining, MaxWaitMs));
                    return;
                }
            }
        }

        if (spinTarget >= 0)
        {
            SpinUntil(spinTarget);
            return;
        }

        if (due)
        {
            DispatchDue(_dispatchBuffer);
            _dispatchBuffer.Clear();    // drop the references; the entries may be long-lived
        }
    }

    private void CollectDueLocked()
    {
        _dueBuffer.Clear();
        var now = CurrentTick;
        while (_queue.Count > 0 && _queue.TryPeek(out _, out var due) && due <= now)
        {
            var entry = _queue.Dequeue();
            if (entry.IsCancelled)
            {
                // Already accounted for in OnCancelled.
                continue;
            }
            _dueBuffer.Add(entry);
        }
    }

    private void SpinUntil(long targetTick)
    {
        var spinner = new SpinWait();
        while (CurrentTick < targetTick && Volatile.Read(ref _disposed) == 0)
        {
            if (spinner.NextSpinWillYield)
                spinner = new SpinWait();
            spinner.SpinOnce();
        }
    }

    private void DispatchDue(List<TimerEntry> due)
    {
        var now = CurrentTick;
        foreach (var entry in due)
        {
            // Guarded per entry, not per batch: dispatch reaches user code (an OnDropped callback,
            // and the job itself when there are no workers to hand it to), and one bad timer must
            // not cost the rest of this batch their firing. They have already been taken out of the
            // queue, so dropping them here would strand their pending slots for good.
            try
            {
                DispatchOne(entry, now);
            }
            catch (Exception ex)
            {
                _system.Logger.Error($"Timer dispatch for actor '{entry.Owner.Name}' failed", ex);
            }
        }
    }

    private void DispatchOne(TimerEntry entry, long now)
    {
        if (entry.IsCancelled)
            return;

        var lag = now - entry.DueTick;
        if (lag > 0)
            _system.Metrics.RecordTimerLag(lag);

        if (entry.Repeating)
        {
            _system.Metrics.OnTimerFired();

            if (!_system.DispatchTimerJob(entry.Owner, entry.RentTickJob(), out var refusal)
                && refusal == DropReason.Disposed)
            {
                // The owner is gone for good, so re-arming can only fire into a closed door once a
                // period: a drop counted every tick, and a pending timer that never goes away, which
                // is exactly what makes StopAsync burn its whole drain timeout.
                entry.Retire(TimerRetirement.Discarded);
                return;
            }

            // Re-arm from the scheduled time to avoid drift, but never schedule into the past.
            var next = entry.DueTick + ToMillis(entry.Period);
            if (next <= now)
                next = now + ToMillis(entry.Period);

            if (!Enqueue(entry, next, isNew: false))
            {
                // Disposed while we were dispatching: release this timer's pending slot.
                entry.Retire(TimerRetirement.Discarded);
            }
            return;
        }

        // The state machine is the single arbiter for a one-shot. Whoever wins the transition out
        // of Armed owns the accounting, so a Cancel() racing this firing cannot decrement as well.
        if (!entry.TryBeginFiring())
            return;

        Interlocked.Decrement(ref _pending);
        _system.Metrics.OnTimerFired();

        if (!_system.DispatchTimerJob(entry.Owner, entry.RentTickJob(), out _))
        {
            // The actor refused the job (full, faulted, disposed, or the system is stopping), so
            // the callback will never run. Retire the handle silently: the refusal is already
            // counted as a drop, and the pending slot went back at the firing above.
            entry.Retire(TimerRetirement.None);
        }
    }

    private void DiscardAll()
    {
        lock (_lock)
        {
            // Retire is idempotent and knows whether the entry still holds a pending slot, so a
            // one-shot that DispatchDue already fired is left alone here.
            while (_queue.Count > 0)
                _queue.Dequeue().Retire(TimerRetirement.Discarded);

            foreach (var entry in _dueBuffer)
                entry.Retire(TimerRetirement.Discarded);
            _dueBuffer.Clear();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }

        if (Volatile.Read(ref _started) != 0 && _thread.IsAlive)
        {
            if (!_thread.Join(TimeSpan.FromSeconds(2)))
                _system.Logger.Warn($"Timer thread '{_thread.Name}' did not stop within 2s");
        }

        DiscardAll();
    }

    /// <summary>
    /// One scheduled timer, and the state machine that decides whether its callback runs.
    ///
    /// <para>The states exist because "fired" and "ran" are not the same moment. The timer thread
    /// hands the callback to the owning actor as an ordinary job, and if the actor is busy that job
    /// can sit on its queue for a good while. The entry itself is carried as that job's state and
    /// re-reads the state when it finally gets its turn, which is what lets a cancel land in that
    /// window — a repeating AI tick used to run once more *after* the entity that owned it had
    /// despawned and cancelled the handle.</para>
    ///
    /// <para>The same word also arbitrates the pending-count accounting: whoever wins the
    /// transition out of <c>Armed</c> owns the slot, so a cancel racing a firing can never give the
    /// same slot back twice. A negative <see cref="PendingCount"/> made shutdown skip waiting for
    /// timers that really were still armed.</para>
    /// </summary>
    internal sealed class TimerEntry : ITimerHandle
    {
        private const int New = 0;          // built, not yet queued: holds no pending slot
        private const int Armed = 1;        // in the timer queue, holding one pending slot
        private const int Fired = 2;        // handed to the owner's queue, slot already released
        private const int Executed = 3;     // the callback ran (one-shot only)
        private const int Cancelled = 4;    // terminal: the callback will not run

        private readonly TimerService _service;
        private JobEntry? _job;
        private int _state = New;

        public TimerEntry(TimerService service, AsyncExecutable owner, JobEntry? job,
            Action? repeatAction, TimeSpan period, bool repeating)
        {
            _service = service;
            Owner = owner;
            _job = job;
            RepeatAction = repeatAction;
            Period = period;
            Repeating = repeating;
        }

        public AsyncExecutable Owner { get; }
        public Action? RepeatAction { get; }
        public TimeSpan Period { get; }
        public bool Repeating { get; }
        public long DueTick { get; set; }

        public bool IsCancelled => Volatile.Read(ref _state) == Cancelled;

        /// <inheritdoc />
        public bool IsPending => Volatile.Read(ref _state) is Armed or Fired;

        /// <summary>
        /// Called under the service lock once the entry is on the queue and its pending slot has
        /// been counted. The handle is not published to the caller until this has happened, so
        /// nothing can be racing it.
        /// </summary>
        internal void MarkArmed() => Volatile.Write(ref _state, Armed);

        /// <summary>
        /// Claim a one-shot for firing. A repeating entry stays <c>Armed</c> across ticks — it is
        /// re-queued immediately and holds its single pending slot until it is retired.
        /// </summary>
        internal bool TryBeginFiring() =>
            Interlocked.CompareExchange(ref _state, Fired, Armed) == Armed;

        /// <summary>
        /// The job handed to the owning actor. It carries the entry rather than the user callback
        /// so the cancellation check happens when the actor runs it, not when the timer fired.
        /// </summary>
        internal JobEntry RentTickJob() => Job<TimerEntry>.Rent(static e => e.Run(), this);

        private void Run()
        {
            if (Repeating)
            {
                if (Volatile.Read(ref _state) == Cancelled)
                    return;     // cancelled while this tick sat on the actor's queue
                RepeatAction?.Invoke();
                return;
            }

            if (Interlocked.CompareExchange(ref _state, Executed, Fired) != Fired)
                return;         // cancelled first; whoever cancelled discarded the job

            Interlocked.Exchange(ref _job, null)?.Execute();
        }

        /// <inheritdoc />
        public bool Cancel() => Retire(TimerRetirement.Cancelled);

        /// <summary>
        /// Move to the terminal state, giving the pending slot back if this entry still holds one.
        /// Idempotent: only the first caller sees <c>true</c> and does the accounting.
        /// </summary>
        internal bool Retire(TimerRetirement retirement)
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if (state is Executed or Cancelled)
                    return false;

                if (Interlocked.CompareExchange(ref _state, Cancelled, state) != state)
                    continue;

                // Armed is the only state that still owns a slot in PendingCount: New never took
                // one, and a one-shot in Fired gave its own back when the timer thread dispatched it.
                _service.OnRetired(releaseSlot: state == Armed, retirement);
                Interlocked.Exchange(ref _job, null)?.Discard();
                return true;
            }
        }
    }

    private sealed class CancelledHandle : ITimerHandle
    {
        public static readonly CancelledHandle Instance = new();
        public bool Cancel() => false;
        public bool IsPending => false;
    }
}

/// <summary>
/// Optional Windows-only bump of the global system timer resolution to 1 ms.
/// Off by default: it is process-wide and raises power draw, so it is only worth it for
/// servers that genuinely need tight ticks.
/// </summary>
internal readonly struct SystemTimerResolution : IDisposable
{
    private readonly bool _acquired;

    private SystemTimerResolution(bool acquired) => _acquired = acquired;

    public static SystemTimerResolution Acquire(bool requested)
    {
        if (!requested || !OperatingSystem.IsWindows())
            return new SystemTimerResolution(false);

        try
        {
            return new SystemTimerResolution(NativeMethods.TimeBeginPeriod(1) == 0);
        }
        catch (DllNotFoundException)
        {
            return new SystemTimerResolution(false);
        }
        catch (EntryPointNotFoundException)
        {
            return new SystemTimerResolution(false);
        }
    }

    public void Dispose()
    {
        if (!_acquired || !OperatingSystem.IsWindows())
            return;
        try { NativeMethods.TimeEndPeriod(1); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        // Blittable signature, no marshalling — safe for AOT without the LibraryImport generator
        // (which would force AllowUnsafeBlocks on the whole library).
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
        internal static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
        internal static extern uint TimeEndPeriod(uint period);
    }
}
