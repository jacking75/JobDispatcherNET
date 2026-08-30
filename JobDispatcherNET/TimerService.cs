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
    /// Cancel the timer. Returns <c>true</c> if it was cancelled before firing; <c>false</c> if it
    /// had already fired (or was already cancelled). For a repeating timer, cancelling stops all
    /// further firings.
    /// </summary>
    bool Cancel();

    /// <summary>True while the timer is still scheduled to fire at least once more.</summary>
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
    private readonly List<TimerEntry> _dueBuffer = [];
    private readonly JobSystem _system;
    private readonly TimerPrecision _precision;
    private readonly int _spinThresholdMs;
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

        entry.DiscardJob();
        _system.Metrics.OnTimerDiscarded();
        return CancelledHandle.Instance;
    }

    public ITimerHandle ScheduleRepeating(AsyncExecutable owner, TimeSpan period, TimeSpan initialDelay, Action action)
    {
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), "period must be positive");

        if (Volatile.Read(ref _disposed) != 0)
        {
            _system.Metrics.OnTimerDiscarded();
            return CancelledHandle.Instance;
        }

        EnsureStarted();
        var entry = new TimerEntry(this, owner, job: null, repeatAction: action, period, repeating: true);
        if (Enqueue(entry, CurrentTick + ToMillis(initialDelay), isNew: true))
            return entry;

        _system.Metrics.OnTimerDiscarded();
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

            _queue.Enqueue(entry, dueTick);
            if (isNew)
                Interlocked.Increment(ref _pending);
            Monitor.Pulse(_lock);
            return true;
        }
    }

    /// <summary>Account for a timer that was cancelled before it fired.</summary>
    internal void OnCancelled()
    {
        Interlocked.Decrement(ref _pending);
        _system.Metrics.OnTimerCancelled();
    }

    private void Loop()
    {
        ThreadContext.CurrentSystem = _system;
        using var resolution = SystemTimerResolution.Acquire(_system.Options.RaiseSystemTimerResolution);

        while (Volatile.Read(ref _disposed) == 0)
        {
            long spinTarget = -1;

            TimerEntry[]? due = null;

            lock (_lock)
            {
                CollectDueLocked();

                if (_dueBuffer.Count > 0)
                {
                    // Take the entries out under the lock. DispatchDue runs unlocked, and
                    // Dispose can call DiscardAll from another thread; handing over a private
                    // array means the two can never touch the same list at once.
                    due = _dueBuffer.ToArray();
                    _dueBuffer.Clear();
                }

                if (due is null)
                {
                    if (_queue.Count == 0)
                    {
                        Monitor.Wait(_lock, MaxWaitMs);
                        continue;
                    }

                    _queue.TryPeek(out _, out var nextDue);
                    var remaining = nextDue - CurrentTick;
                    if (remaining <= 0)
                        continue;

                    if (_precision == TimerPrecision.High && remaining <= _spinThresholdMs)
                    {
                        spinTarget = nextDue;
                    }
                    else
                    {
                        Monitor.Wait(_lock, (int)Math.Min(remaining, MaxWaitMs));
                        continue;
                    }
                }
            }

            if (spinTarget >= 0)
            {
                SpinUntil(spinTarget);
                continue;
            }

            if (due is not null)
                DispatchDue(due);
        }

        DiscardAll();
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

    private void DispatchDue(TimerEntry[] due)
    {
        var now = CurrentTick;
        foreach (var entry in due)
        {
            if (entry.IsCancelled)
                continue;

            var lag = now - entry.DueTick;
            if (lag > 0)
                _system.Metrics.RecordTimerLag(lag);

            if (entry.Repeating)
            {
                var action = entry.RepeatAction;
                if (action is null)
                    continue;

                _system.Metrics.OnTimerFired();
                _system.DispatchTimerJob(entry.Owner, Job.Rent(action));

                // Re-arm from the scheduled time to avoid drift, but never schedule into the past.
                var next = entry.DueTick + ToMillis(entry.Period);
                if (next <= now)
                    next = now + ToMillis(entry.Period);

                if (!Enqueue(entry, next, isNew: false))
                {
                    // Disposed while we were dispatching: release this timer's pending slot.
                    Interlocked.Decrement(ref _pending);
                    _system.Metrics.OnTimerDiscarded();
                }
            }
            else
            {
                // TakeJob is the single arbiter for a one-shot timer. Whoever wins the exchange
                // owns the accounting, so a Cancel() racing this firing cannot also decrement.
                var job = entry.TakeJob();
                if (job is null)
                    continue;

                Interlocked.Decrement(ref _pending);
                _system.Metrics.OnTimerFired();
                _system.DispatchTimerJob(entry.Owner, job);
            }
        }
    }

    private void DiscardAll()
    {
        lock (_lock)
        {
            while (_queue.Count > 0)
                Discard(_queue.Dequeue());

            // Entries collected but not yet handed to DispatchDue still hold a pending slot.
            foreach (var entry in _dueBuffer)
                Discard(entry);
            _dueBuffer.Clear();
        }

        void Discard(TimerEntry entry)
        {
            if (entry.IsCancelled)
                return;     // already accounted for in OnCancelled

            if (!entry.Repeating)
            {
                // Taking the job is what claims a one-shot. If it is already gone, DispatchDue
                // fired it and did the accounting.
                var job = entry.TakeJob();
                if (job is null)
                    return;
                job.Discard();
            }

            Interlocked.Decrement(ref _pending);
            _system.Metrics.OnTimerDiscarded();
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

    internal sealed class TimerEntry : ITimerHandle
    {
        private readonly TimerService _service;
        private JobEntry? _job;
        private int _cancelled;

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

        public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;

        public bool IsPending => !IsCancelled && (Repeating || Volatile.Read(ref _job) is not null);

        /// <summary>
        /// Cancel the timer.
        ///
        /// For a one-shot, taking the job is what decides ownership: <see cref="DispatchDue"/>
        /// takes it with the same interlocked exchange, so exactly one of the two sides can claim
        /// the entry. Deciding by *reading* the job instead — as this used to — let a cancel that
        /// raced a firing decrement the pending count a second time, and a negative count made
        /// shutdown skip waiting for timers that really were still armed.
        /// </summary>
        public bool Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0)
                return false;

            if (Repeating)
            {
                // A repeating entry holds exactly one pending slot for its whole life and the
                // exchange above already made us its sole claimant.
                _service.OnCancelled();
                return true;
            }

            var job = Interlocked.Exchange(ref _job, null);
            if (job is null)
                return false;   // DispatchDue got there first: the timer has already fired

            job.Discard();
            _service.OnCancelled();
            return true;
        }

        public JobEntry? TakeJob() => Interlocked.Exchange(ref _job, null);

        public void DiscardJob() => Interlocked.Exchange(ref _job, null)?.Discard();
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
