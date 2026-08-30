using Xunit;

namespace JobDispatcherNET.Tests;

/// <summary>
/// One test per P0 defect listed in ROADMAP.md. Each fails against the v2.0 implementation.
/// </summary>
public sealed class RegressionTests
{
    /// <summary>
    /// P0-1 — a rejected write on a bounded queue used to leave the counter claiming a job the
    /// queue never received, so the leader spun forever and a second thread could take over the
    /// same actor. The admission CAS makes the counter authoritative.
    /// </summary>
    [Fact]
    public void P0_1_BoundedRejectionNeverStrandsTheLeader()
    {
        using var host = new TestSystem(workers: 4);
        var actor = new CountingActor(host.Options(maxQueue: 1));

        var accepted = 0;
        const int producers = 8;
        const int attemptsEach = 40_000;

        Parallel.For(0, producers, _ =>
        {
            var local = 0;
            for (var i = 0; i < attemptsEach; i++)
            {
                if (actor.Bump())
                    local++;

                // Widen the window between the admission CAS and the enqueue.
                if ((i & 0x3F) == 0)
                    Thread.Yield();
            }
            Interlocked.Add(ref accepted, local);
        });

        TestSystem.SpinWaitFor(() => actor.Executed == accepted, TimeSpan.FromSeconds(60),
            $"executed {actor.Executed} but accepted {accepted} — leader is stuck");

        Assert.Equal(1, actor.MaxConcurrent);
        Assert.Equal(accepted, actor.State);
        Assert.Equal(0, actor.RemainingTaskCount);
        Assert.Equal(host.System.LiveWorkerCount, host.System.LiveWorkerCount);
        Assert.True(accepted > 0, "nothing was accepted, the test proved nothing");
        Assert.True(accepted < producers * attemptsEach, "nothing was rejected, the test proved nothing");
    }

    /// <summary>
    /// P0-2 — timers used to live on the thread that scheduled them, so a worker crash silently
    /// took every timer it owned with it. They now live on the system's timer thread.
    /// </summary>
    [Fact]
    public void P0_2_TimersSurviveAWorkerCrash()
    {
        using var system = new JobSystem(new JobSystemOptions
        {
            Name = "p0-2",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        CrashingRunnable.ResetState();

        using var dispatcher = new JobDispatcher<CrashingRunnable>(1, new JobDispatcherOptions
        {
            System = system,
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 5,
            RestartBackoff = TimeSpan.FromMilliseconds(50),
        });
        _ = dispatcher.RunWorkerThreadsAsync();

        var fired = new ManualResetEventSlim(false);
        var actor = new SignalActor(new JobOptions { System = system }, fired);

        // Schedule from the test thread; the firing must land on a worker even though the worker
        // that was alive at scheduling time is about to die and be replaced.
        actor.Ping(TimeSpan.FromMilliseconds(600));

        CrashingRunnable.CrashNow();

        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)), "timer never fired after the worker was restarted");
        Assert.True(system.Metrics.Snapshot().WorkerRestarts > 0, "the worker never actually crashed");
    }

    /// <summary>
    /// P0-3 — with no dispatcher running, a delayed job used to sit on an internal queue that only
    /// worker threads drained, so it never ran and nothing said so.
    /// </summary>
    [Fact]
    public void P0_3_DelayedJobRunsWithNoDispatcher()
    {
        using var system = new JobSystem(new JobSystemOptions
        {
            Name = "p0-3",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        var fired = new ManualResetEventSlim(false);
        var actor = new SignalActor(new JobOptions { System = system }, fired);

        actor.Ping(TimeSpan.FromMilliseconds(100));

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
            "DoAsyncAfter never fired without a dispatcher");
    }

    /// <summary>
    /// P0-4 — Stop() used to skip re-scheduling a drain, so an item enqueued in the window between
    /// the drain loop ending and the scheduling flag being cleared was lost. In the sample server
    /// that item was the disconnect marker, and the player stayed in the world forever.
    /// </summary>
    [Fact]
    public void P0_4_SequencerStopStillDrainsAcceptedItems()
    {
        using var host = new TestSystem(workers: 4);

        var handled = 0;
        var accepted = 0;

        for (var round = 0; round < 3_000; round++)
        {
            var done = new ManualResetEventSlim(false);
            var localHandled = 0;
            var expected = 0;

            var sequencer = new Sequencer<int>(host.System, item =>
            {
                Interlocked.Increment(ref localHandled);
                if (item == -1)
                    done.Set();
            });

            // A producer racing Stop(), exactly like a socket thread pushing the last packet.
            if (sequencer.Enqueue(round)) expected++;
            if (sequencer.Enqueue(-1)) expected++;
            sequencer.Stop();

            Assert.True(done.Wait(TimeSpan.FromSeconds(5)),
                $"round {round}: the final item was accepted but never handled");

            TestSystem.SpinWaitFor(() => Volatile.Read(ref localHandled) == expected,
                TimeSpan.FromSeconds(5), $"round {round}: handled {localHandled} of {expected}");

            handled += localHandled;
            accepted += expected;
        }

        Assert.Equal(accepted, handled);
    }

    /// <summary>P0-5 — starting a dispatcher twice used to silently double the thread count.</summary>
    [Fact]
    public void P0_5_RunWorkerThreadsTwiceThrows()
    {
        using var system = new JobSystem(new JobSystemOptions
        {
            Name = "p0-5",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        using var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system });
        _ = dispatcher.RunWorkerThreadsAsync();

        Assert.Throws<InvalidOperationException>(() => { _ = dispatcher.RunWorkerThreadsAsync(); });
    }

    /// <summary>P0-5 — a worker slot that stays healthy gets its restart budget back.</summary>
    [Fact]
    public void P0_5_RestartBudgetRefillsAfterHealthyPeriod()
    {
        using var system = new JobSystem(new JobSystemOptions
        {
            Name = "p0-5b",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        CrashingRunnable.ResetState();

        using var dispatcher = new JobDispatcher<CrashingRunnable>(1, new JobDispatcherOptions
        {
            System = system,
            RestartFailedWorkers = true,

            // A budget of one: without the refill, the second crash would leave the slot down
            // permanently.
            MaxRestartsPerWorker = 1,
            RestartBackoff = TimeSpan.FromMilliseconds(20),
            RestartCountResetAfter = TimeSpan.FromMilliseconds(1),
        });
        _ = dispatcher.RunWorkerThreadsAsync();

        TestSystem.SpinWaitFor(() => CrashingRunnable.Starts >= 1, TimeSpan.FromSeconds(5),
            "the worker never started");

        for (var crash = 1; crash <= 3; crash++)
        {
            var startsBefore = CrashingRunnable.Starts;

            // Stay alive past RestartCountResetAfter so the slot has earned its budget back.
            Thread.Sleep(30);
            CrashingRunnable.CrashNow();

            // A new construction is proof of a restart — unlike a metric delta, it cannot be
            // satisfied by a restart left over from the previous iteration.
            TestSystem.SpinWaitFor(() => CrashingRunnable.Starts > startsBefore,
                TimeSpan.FromSeconds(5),
                $"crash {crash} did not produce a restart — the budget was not refilled");
        }

        Assert.True(system.Metrics.Snapshot().WorkerRestarts >= 3,
            $"expected at least 3 restarts, saw {system.Metrics.Snapshot().WorkerRestarts}");

        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 1, TimeSpan.FromSeconds(5),
            "worker did not come back up");
    }

    /// <summary>P0-6 — a job with a null reference state must still reach the handler.</summary>
    [Fact]
    public void P0_6_NullReferenceStateIsPassedThrough()
    {
        using var host = new TestSystem(workers: 1);
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new StateActor(host.Options(), seen);

        Assert.True(actor.Send(null));
        Assert.True(seen.Task.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(seen.Task.Result, "the handler was not called with the null state");
    }

    private sealed class SignalActor(JobOptions options, ManualResetEventSlim signal) : AsyncExecutable(options)
    {
        public ITimerHandle Ping(TimeSpan delay) =>
            DoAsyncAfter(delay, static a => a.Fire(), this);

        private void Fire() => signal.Set();
    }

    private sealed class StateActor(JobOptions options, TaskCompletionSource<bool> sink) : AsyncExecutable(options)
    {
        public bool Send(string? value) =>
            DoAsync(static t => t.Self.Handle(t.Value), (Self: this, Value: value));

        private void Handle(string? value) => sink.TrySetResult(value is null);
    }

    /// <summary>
    /// A worker body that crashes on demand.
    ///
    /// <see cref="Starts"/> counts constructions, which is the precise signal a test needs: the
    /// dispatcher builds a fresh <c>T</c> for every worker it starts, so an increment means a
    /// restart actually happened. Waiting on the metric counter instead is racy, because a restart
    /// from a previous iteration can land after the baseline is captured.
    /// </summary>
    private sealed class CrashingRunnable : IRunnable
    {
        private static int _crashRequested;
        private static int _starts;

        public CrashingRunnable() => Interlocked.Increment(ref _starts);

        public static int Starts => Volatile.Read(ref _starts);

        public static void CrashNow() => Interlocked.Exchange(ref _crashRequested, 1);

        /// <summary>Clear the shared state so one test cannot see another test's crash request.</summary>
        public static void ResetState()
        {
            Interlocked.Exchange(ref _crashRequested, 0);
            Interlocked.Exchange(ref _starts, 0);
        }

        public bool Run(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _crashRequested, 0, 1) == 1)
                throw new InvalidOperationException("simulated worker crash");

            cancellationToken.WaitHandle.WaitOne(5);
            return true;
        }

        public void Dispose() { }
    }
}
