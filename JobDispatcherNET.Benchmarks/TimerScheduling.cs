using BenchmarkDotNet.Attributes;

namespace JobDispatcherNET.Benchmarks;

/// <summary>
/// ROADMAP §3.3, row 4 — "10,000 timers scheduled at once: firing lag".
///
/// <see cref="ScheduleAndFire"/> arms 10,000 one-shot timers and waits until every one of them has
/// executed on its actor, so the per-operation number is schedule + timer-thread wakeup + dispatch +
/// execute. On Windows the default <see cref="TimerPrecision.Coarse"/> mode means the floor is the
/// OS timer resolution (~15.6 ms unless something else on the box has raised it), which is exactly
/// what ROADMAP §4.4 asks to measure — so read this row as a lag measurement, not as pure CPU cost.
///
/// <see cref="ScheduleAndCancel"/> isolates the bookkeeping: arm 10,000 timers far enough out that
/// none of them can fire, then cancel them all.
/// </summary>
[MemoryDiagnoser]
public class TimerScheduling
{
    private const int TimerCount = 10_000;

    /// <summary>
    /// Far enough out that a whole schedule+cancel pass finishes first (it takes single-digit ms),
    /// but short enough that cancelled entries are purged from the timer heap quickly instead of
    /// piling up for the length of the run.
    /// </summary>
    private static readonly TimeSpan CancelDelay = TimeSpan.FromMilliseconds(20);

    private JobSystem _system = null!;
    private JobDispatcher _dispatcher = null!;
    private CounterActor _actor = null!;
    private ITimerHandle[] _handles = null!;

    /// <summary>Build the system, the pool and the receiving actor.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _system = Bench.NewSystem("timers");
        _dispatcher = Bench.StartWorkers(_system, 4);
        _actor = new CounterActor(new JobOptions
        {
            System = _system,
            Mode = ExecutionMode.Scheduled,
            Name = "timer-target",
        });
        _handles = new ITimerHandle[TimerCount];
    }

    /// <summary>Cancel anything still armed, then tear the system down.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i]?.Cancel();
            _handles[i] = null!;
        }
        Bench.Shutdown(_dispatcher, _system);
    }

    /// <summary>Arm 10,000 timers and wait for all of them to fire.</summary>
    [Benchmark(OperationsPerInvoke = TimerCount, Baseline = true, Description = "arm 10k timers, wait for all to fire")]
    public int ScheduleAndFire()
    {
        var actor = _actor;
        actor.Reset();

        for (var i = 0; i < TimerCount; i++)
            actor.DoAsyncAfter(TimeSpan.FromMilliseconds(1), static a => a.Touch(), actor);

        Bench.WaitFor(actor, TimerCount);
        return actor.Count;
    }

    /// <summary>Arm 10,000 timers and cancel every one before it can fire.</summary>
    [Benchmark(OperationsPerInvoke = TimerCount, Description = "arm 10k timers, cancel them all")]
    public int ScheduleAndCancel()
    {
        var actor = _actor;
        var handles = _handles;

        for (var i = 0; i < TimerCount; i++)
            handles[i] = actor.DoAsyncAfter(CancelDelay, static a => a.Touch(), actor);

        var cancelled = 0;
        for (var i = 0; i < TimerCount; i++)
        {
            if (handles[i].Cancel())
                cancelled++;
            handles[i] = null!;
        }

        return cancelled;
    }
}
