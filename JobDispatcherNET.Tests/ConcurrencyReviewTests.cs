using System.Diagnostics;
using Xunit;

namespace JobDispatcherNET.Tests;

/// <summary>
/// Regressions for the races found by the adversarial review of the v2.1 rewrite.
/// Each one exercises an interleaving the earlier suite could not reach.
/// </summary>
public sealed class ConcurrencyReviewTests
{
    /// <summary>
    /// CRITICAL-1 / CRITICAL-2 — coverage for the Exclusive handshake between the flushing thread
    /// and an async job's continuation.
    ///
    /// <para><b>What this does and does not prove.</b> The two races the review found are a couple
    /// of instructions wide and hinge on the counter passing through zero between a flusher's
    /// decrement and its next read. Reproducing them from outside the library needs test seams
    /// planted inside the handshake, which are not worth carrying in shipped code — so treat this
    /// as coverage of the paths and a guard against a future regression that widens the window,
    /// not as a reproduction of the original defect. The fixes themselves rest on the invariant,
    /// not on this test: the suspension's reservation is now released exactly once, by whichever
    /// party holds leadership when the handshake resolves, so the counter can never reach zero
    /// while a thread still intends to keep flushing.</para>
    ///
    /// <para>What it does check is the observable contract: an Exclusive actor runs nothing else
    /// while an async job is awaiting, every queued job eventually runs, exactly once, and the
    /// actor settles empty.</para>
    /// </summary>
    [Fact]
    public void ExclusiveActorStaysExclusiveAcrossManyAsyncHandshakes()
    {
        using var host = new TestSystem(workers: 8);

        for (var round = 0; round < 200; round++)
        {
            var actor = new ExclusiveProbe(new JobOptions
            {
                System = host.System,
                AsyncReentrancy = AsyncReentrancy.Exclusive,
            });

            // Nothing else queued, so the counter really does cross zero when the continuation
            // runs — the case the original Exclusive test could not reach, because it always
            // queued work while the await was still pending.
            var pending = actor.ShortAsync();

            // Producers arriving from several threads while the handshake resolves.
            Parallel.For(0, 4, _ =>
            {
                for (var i = 0; i < 10; i++)
                    Assert.True(actor.Quick());
            });

            Assert.True(pending.Wait(TimeSpan.FromSeconds(10)), $"round {round}: async job never finished");

            // Both conditions in one wait: Done is incremented inside the job, but the counter
            // only drops once the flush loop has retired that job, so Done can reach 40 a moment
            // before the actor is actually empty.
            TestSystem.SpinWaitFor(() => actor.Done == 40 && actor.RemainingTaskCount == 0,
                TimeSpan.FromSeconds(10),
                $"round {round}: {actor.Done} of 40 jobs ran, queue depth {actor.RemainingTaskCount}");

            Assert.Equal(1, actor.MaxConcurrent);
            Assert.Equal(40, actor.NonAtomicCount);
        }

        Assert.Equal(0, host.System.InFlightJobs);
    }

    /// <summary>
    /// Back-to-back async jobs on one Exclusive actor: the second must not start while the first is
    /// still awaiting, and the actor must settle empty afterwards.
    /// </summary>
    [Fact]
    public void BackToBackExclusiveAsyncJobsStayExclusive()
    {
        using var host = new TestSystem(workers: 8);
        var actor = new ExclusiveProbe(new JobOptions
        {
            System = host.System,
            AsyncReentrancy = AsyncReentrancy.Exclusive,
        });

        for (var i = 0; i < 200; i++)
        {
            var first = actor.ShortAsync();
            var second = actor.ShortAsync();

            Assert.True(Task.WhenAll(first, second).Wait(TimeSpan.FromSeconds(10)),
                $"iteration {i}: async jobs did not complete");
        }

        TestSystem.SpinWaitFor(() => actor.RemainingTaskCount == 0, TimeSpan.FromSeconds(10),
            $"actor did not settle, queue={actor.RemainingTaskCount}");

        Assert.Equal(1, actor.MaxConcurrent);
        Assert.Equal(0, host.System.InFlightJobs);
    }

    /// <summary>
    /// HIGH-3 — <see cref="JobSystem.Post"/> work was invisible to the drain gate, so a graceful
    /// shutdown could stop the workers with posted actions still queued. That also covered every
    /// <see cref="Sequencer{T}"/> built on the system-aware constructor, whose drains all go
    /// through Post — so a clean shutdown could silently lose a session's packets.
    ///
    /// <para>This asserts the contract rather than the interleaving: the original defect needed a
    /// producer to be caught between its enqueue and its counter bump at the instant the drain
    /// polled, which is a couple of instructions wide. The fix is unconditional — the depth is now
    /// raised before the item is visible and lowered only after it has run — so the count can only
    /// ever over-estimate, never under-estimate.</para>
    /// </summary>
    [Fact]
    public async Task StopAsyncDrainsPostedActionsNotJustActorJobs()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "post-drain",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        using var dispatcher = new JobDispatcher(4, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 4, TimeSpan.FromSeconds(5), "workers did not start");

        var ran = 0;
        const int posted = 500;
        for (var i = 0; i < posted; i++)
        {
            system.Post(() =>
            {
                Thread.SpinWait(500);
                Interlocked.Increment(ref ran);
            });
        }

        var drained = await system.StopAsync(TimeSpan.FromSeconds(30));

        Assert.True(drained, "shutdown reported a timeout instead of draining");
        Assert.Equal(posted, Volatile.Read(ref ran));

        system.Dispose();
    }

    /// <summary>
    /// HIGH-3, via the path that actually bites: a sequencer whose drains are posted to the system
    /// must have handled every accepted item by the time <see cref="JobSystem.StopAsync"/> returns.
    /// </summary>
    [Fact]
    public async Task StopAsyncDrainsSequencerWorkScheduledThroughPost()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "seq-drain",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        using var dispatcher = new JobDispatcher(4, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 4, TimeSpan.FromSeconds(5), "workers did not start");

        var handled = 0;
        var sequencer = new Sequencer<int>(system, _ =>
        {
            Thread.SpinWait(200);
            Interlocked.Increment(ref handled);
        });

        var accepted = 0;
        for (var i = 0; i < 2_000; i++)
            if (sequencer.Enqueue(i))
                accepted++;

        sequencer.Stop();
        var drained = await system.StopAsync(TimeSpan.FromSeconds(30));

        Assert.True(drained, "shutdown reported a timeout instead of draining");
        Assert.Equal(accepted, Volatile.Read(ref handled));

        system.Dispose();
    }

    /// <summary>
    /// HIGH-4 — cancelling a one-shot timer that was firing at that instant decremented the pending
    /// count twice. The count drifted negative, and a negative count made the shutdown drain skip
    /// waiting for timers that really were still armed.
    /// </summary>
    [Fact]
    public void CancelRacingAFiringTimerKeepsThePendingCountHonest()
    {
        using var host = new TestSystem(workers: 4);
        var actor = new CountingActor(host.Options());

        var fired = 0;
        var cancelled = 0;
        var armed = 0;

        // Batch size decides who tends to win: with a batch of one the cancel lands before the
        // timer thread has even woken, while a large batch is still being cancelled at the back
        // when the timer thread is already dispatching the front. Sweeping the sizes means both
        // outcomes occur on a fast machine and on an oversubscribed CI runner alike — a single
        // fixed size made this test environment-sensitive.
        int[] batchSizes = [1, 2, 4, 16, 64, 256];

        foreach (var perBatch in batchSizes)
        {
            for (var repeat = 0; repeat < 40; repeat++)
            {
                var handles = new ITimerHandle[perBatch];
                for (var i = 0; i < perBatch; i++)
                    handles[i] = actor.DoAsyncAfter(TimeSpan.Zero, static a => a.Bump(), actor);

                armed += perBatch;

                foreach (var handle in handles)
                {
                    if (handle.Cancel())
                        cancelled++;
                    else
                        fired++;
                }
            }
        }

        TestSystem.SpinWaitFor(() => host.System.PendingTimerCount == 0, TimeSpan.FromSeconds(20),
            $"pending timer count settled at {host.System.PendingTimerCount}, not 0 — " +
            "a negative count means a cancel and a firing both claimed the same timer");

        // Every handle resolved exactly once: Cancel() either claimed it or reported it had run.
        Assert.Equal(armed, fired + cancelled);

        TestSystem.SpinWaitFor(() => actor.Executed == fired, TimeSpan.FromSeconds(20),
            $"{actor.Executed} callbacks ran but only {fired} timers reported firing — " +
            "a timer that reported being cancelled ran anyway");

        var metrics = host.System.Metrics.Snapshot();
        Assert.Equal(cancelled, metrics.TimersCancelled);

        // TimersFired counts entries the timer thread handed to the actor, which is not the same as
        // callbacks that ran: a cancel landing while the job is still queued on the actor claims it
        // (A6). So the counter sits between the callbacks that ran and everything ever armed.
        Assert.InRange(metrics.TimersFired, actor.Executed, armed);

        Assert.True(fired > 0, "no timer ever won the race; the test proved nothing");
        Assert.True(cancelled > 0, "no cancel ever won the race; the test proved nothing");
    }

    /// <summary>
    /// MEDIUM-6 — a supervisor restart racing shutdown could start a worker after
    /// <see cref="JobDispatcherBase.TryStop"/> had already scanned the thread array, so shutdown
    /// claimed success while a worker was still coming up.
    /// </summary>
    [Fact]
    public void ShutdownDuringAWorkerRestartStillStopsEveryThread()
    {
        for (var round = 0; round < 20; round++)
        {
            var system = new JobSystem(new JobSystemOptions
            {
                Name = $"restart-race-{round}",
                Logger = NullJobLogger.Instance,
                PublishMeter = false,
            });

            FlakyRunnable.ResetState();
            var dispatcher = new JobDispatcher<FlakyRunnable>(2, new JobDispatcherOptions
            {
                System = system,
                RestartFailedWorkers = true,
                MaxRestartsPerWorker = 100,
                RestartBackoff = TimeSpan.FromMilliseconds(1),
            });
            _ = dispatcher.RunWorkerThreadsAsync();

            TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 2, TimeSpan.FromSeconds(5),
                $"round {round}: workers did not start");

            // Crash continuously so a restart is almost certainly in flight when we stop.
            FlakyRunnable.CrashContinuously();
            Thread.Sleep(5);

            var stopped = dispatcher.TryStop(TimeSpan.FromSeconds(10));

            Assert.True(stopped, $"round {round}: TryStop reported a straggler");
            TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 0, TimeSpan.FromSeconds(10),
                $"round {round}: {system.LiveWorkerCount} workers still alive after TryStop returned true");

            system.Dispose();
        }
    }

    /// <summary>
    /// MEDIUM-7 — scheduling a timer while the system was being disposed left the entry queued with
    /// nobody to drain it, pinning the pending count above zero for the life of the process.
    /// </summary>
    [Fact]
    public void SchedulingWhileTheSystemIsDisposedDoesNotLeakAPendingTimer()
    {
        for (var round = 0; round < 50; round++)
        {
            var system = new JobSystem(new JobSystemOptions
            {
                Name = $"timer-dispose-{round}",
                Logger = NullJobLogger.Instance,
                PublishMeter = false,
            });

            var actor = new CountingActor(new JobOptions { System = system });

            // Arm the timer thread, then race scheduling against disposal.
            actor.DoAsyncAfter(TimeSpan.FromSeconds(30), static a => a.Bump(), actor).Cancel();

            var scheduler = new Thread(() =>
            {
                for (var i = 0; i < 200; i++)
                    actor.DoAsyncAfter(TimeSpan.FromSeconds(30), static a => a.Bump(), actor);
            });
            scheduler.Start();

            Thread.Sleep(1);
            system.Dispose();
            scheduler.Join(TimeSpan.FromSeconds(10));

            Assert.Equal(0, system.PendingTimerCount);
        }
    }

    /// <summary>MEDIUM-9 — the observed peak is a CAS loop now, so it cannot go backwards.</summary>
    [Fact]
    public void MaxObservedQueueDepthNeverGoesBackwards()
    {
        using var host = new TestSystem(workers: 1);
        var actor = new BlockingActor(host.Options(maxQueue: 512, mode: ExecutionMode.Scheduled));
        actor.BlockAndWait();

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 50; i++)
                actor.Enqueue();
        });

        var depth = actor.RemainingTaskCount;
        Assert.True(actor.MaxObservedQueueDepth >= depth,
            $"peak {actor.MaxObservedQueueDepth} is below the live depth {depth}");

        actor.Release();
    }

    /// <summary>
    /// A5 — the restart path runs outside the worker's own try/catch, so anything it throws is an
    /// unhandled exception on a dedicated thread and ends the process. Unbounded backoff doubling
    /// is the reachable one: Thread.Sleep(TimeSpan) rejects anything past ~24.8 days.
    /// </summary>
    [Fact]
    public void ExhaustingTheRestartBudgetLeavesTheSlotDownWithoutKillingTheProcess()
    {
        var log = new RecordingJobLogger();
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "restart-budget",
            Logger = log,
            PublishMeter = false,
        });

        AlwaysCrashingRunnable.Reset();
        var dispatcher = new JobDispatcher<AlwaysCrashingRunnable>(1, new JobDispatcherOptions
        {
            System = system,
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 40,          // attempt 26 overflowed the old backoff
            RestartBackoff = TimeSpan.FromMilliseconds(1),
            MaxRestartBackoff = TimeSpan.FromMilliseconds(5),
            RestartCountResetAfter = TimeSpan.Zero,

            // This pool has one worker, so it is also the system's last, and the default is now to
            // keep restarting it rather than strand whatever is on the ready queue (S20). Opt out:
            // what this case is about is the budget being honoured without the backoff overflowing.
            KeepLastWorkerAlive = false,
        });
        _ = dispatcher.RunWorkerThreadsAsync();

        TestSystem.SpinWaitFor(() => log.Contains("permanently down"), TimeSpan.FromSeconds(30),
            $"the slot never gave up; it crashed {AlwaysCrashingRunnable.Crashes} times");

        Assert.Equal(40, system.Metrics.Snapshot().WorkerRestarts);
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 0, TimeSpan.FromSeconds(10),
            "the exhausted slot is still running");

        dispatcher.Dispose();
        system.Dispose();
    }

    /// <summary>
    /// A5 — an OperationCanceledException with no stop in progress is a crash, not a clean exit.
    /// Treating every OCE as normal made the slot vanish with neither a log line nor a restart.
    /// </summary>
    [Fact]
    public void AnUnexpectedCancellationCrashesTheWorkerInsteadOfRetiringItSilently()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "unexpected-oce",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        CancellingRunnable.Reset();
        var dispatcher = new JobDispatcher<CancellingRunnable>(1, new JobDispatcherOptions
        {
            System = system,
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 3,
            RestartBackoff = TimeSpan.FromMilliseconds(1),
            RestartCountResetAfter = TimeSpan.Zero,
        });
        _ = dispatcher.RunWorkerThreadsAsync();

        TestSystem.SpinWaitFor(() => system.Metrics.Snapshot().WorkerRestarts >= 3, TimeSpan.FromSeconds(20),
            "the worker retired silently instead of being restarted");

        dispatcher.Dispose();
        system.Dispose();
    }

    /// <summary>
    /// A9 — the disposed check in <see cref="JobDispatcherBase.RunWorkerThreadsAsync"/> sat outside
    /// the lifecycle lock, so a start racing a stop could put threads up after <c>TryStop</c> had
    /// disposed the cancellation source they were about to read. That surfaced as an
    /// <see cref="ObjectDisposedException"/> logged as a worker crash that never happened.
    /// </summary>
    [Fact]
    public void StartingWorkersWhileStoppingNeverLogsACrashThatDidNotHappen()
    {
        for (var round = 0; round < 50; round++)
        {
            var log = new RecordingJobLogger();
            var system = new JobSystem(new JobSystemOptions
            {
                Name = $"start-stop-race-{round}",
                Logger = log,
                PublishMeter = false,
            });
            var dispatcher = new JobDispatcher(4, new JobDispatcherOptions { System = system, IdleWaitMs = 1 });

            var starter = new Thread(() =>
            {
                // Losing the race is a legitimate outcome; being told so is the point.
                try { _ = dispatcher.RunWorkerThreadsAsync(); }
                catch (ObjectDisposedException) { }
            });
            starter.Start();

            dispatcher.TryStop(TimeSpan.FromSeconds(10));
            Assert.True(starter.Join(TimeSpan.FromSeconds(10)), $"round {round}: the starter hung");

            TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 0, TimeSpan.FromSeconds(10),
                $"round {round}: {system.LiveWorkerCount} workers still alive");
            Assert.False(log.Contains("crashed"), $"round {round}: a worker logged a crash that never happened");

            system.Dispose();
        }
    }

    /// <summary>
    /// A10(a) — the join timeout was applied to each thread in turn, so a pool of N stuck workers
    /// took N × timeout to give up rather than the timeout the caller asked for.
    /// </summary>
    [Fact]
    public void TryStopSpendsOneBudgetAcrossEveryWorker()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "join-budget",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        StuckRunnable.Reset();
        var dispatcher = new JobDispatcher<StuckRunnable>(4, new JobDispatcherOptions
        {
            System = system,
            RestartFailedWorkers = false,
        });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 4, TimeSpan.FromSeconds(5),
            "workers did not start");

        StuckRunnable.Stick();      // every worker now ignores the token
        Thread.Sleep(20);

        var started = Stopwatch.GetTimestamp();
        Assert.False(dispatcher.TryStop(TimeSpan.FromMilliseconds(500)));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(elapsed < TimeSpan.FromMilliseconds(1_200),
            $"TryStop took {elapsed.TotalMilliseconds:F0}ms to spend a 500ms budget across 4 workers");

        StuckRunnable.Release();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 0, TimeSpan.FromSeconds(10),
            "workers never exited");
        system.Dispose();
    }

    /// <summary>
    /// A10(b) — a job that stops its own pool cannot join the thread it is running on. TryStop used
    /// to try anyway, burn the whole budget on it and report a straggler that was itself.
    /// </summary>
    [Fact]
    public void StoppingThePoolFromInsideAJobDoesNotJoinItsOwnThread()
    {
        var log = new RecordingJobLogger();
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "self-stop",
            Logger = log,
            PublishMeter = false,
        });
        var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 2, TimeSpan.FromSeconds(5),
            "workers did not start");

        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        system.Post(() => stopped.TrySetResult(dispatcher.TryStop(TimeSpan.FromSeconds(2))));

        Assert.True(stopped.Task.Wait(TimeSpan.FromSeconds(10)), "the job never returned from TryStop");
        Assert.True(stopped.Task.Result, "TryStop reported a straggler for the thread it was running on");
        Assert.False(log.Contains("did not stop within"), "TryStop logged its own thread as a straggler");

        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 0, TimeSpan.FromSeconds(10),
            "workers never exited");
        system.Dispose();
    }

    private sealed class StuckRunnable : IRunnable
    {
        private static readonly ManualResetEventSlim Gate = new(false);
        private static int _stuck;

        public static void Reset()
        {
            Interlocked.Exchange(ref _stuck, 0);
            Gate.Reset();
        }

        public static void Stick() => Interlocked.Exchange(ref _stuck, 1);

        public static void Release() => Gate.Set();

        public bool Run(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _stuck) != 0)
                Gate.Wait(TimeSpan.FromSeconds(30));
            else
                cancellationToken.WaitHandle.WaitOne(2);
            return true;
        }

        public void Dispose() { }
    }

    private sealed class AlwaysCrashingRunnable : IRunnable
    {
        private static int _crashes;

        public static int Crashes => Volatile.Read(ref _crashes);

        public static void Reset() => Interlocked.Exchange(ref _crashes, 0);

        public bool Run(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _crashes);
            throw new InvalidOperationException("this worker never survives an iteration");
        }

        public void Dispose() { }
    }

    private sealed class CancellingRunnable : IRunnable
    {
        private static int _runs;

        public static void Reset() => Interlocked.Exchange(ref _runs, 0);

        public bool Run(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);

            // A token of our own, already cancelled: nothing to do with the dispatcher stopping.
            // This is what an inner Task.Wait that gets cancelled looks like from out here.
            using var own = new CancellationTokenSource();
            own.Cancel();
            own.Token.ThrowIfCancellationRequested();
            return true;
        }

        public void Dispose() { }
    }

    private sealed class RecordingJobLogger : IJobLogger
    {
        private readonly List<string> _lines = [];

        public bool IsEnabled(JobLogLevel level) => true;

        public void Log(JobLogLevel level, string message, Exception? exception = null)
        {
            lock (_lines)
            {
                _lines.Add(message);
            }
        }

        public bool Contains(string fragment)
        {
            lock (_lines)
            {
                return _lines.Exists(line => line.Contains(fragment, StringComparison.Ordinal));
            }
        }
    }

    private sealed class ExclusiveProbe(JobOptions options) : AsyncExecutable(options)
    {
        private int _concurrent;
        private int _maxConcurrent;
        private int _done;
        private int _nonAtomic;

        public int Done => Volatile.Read(ref _done);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        /// <summary>Deliberately non-atomic: it drifts if two threads ever run this actor at once.</summary>
        public int NonAtomicCount => _nonAtomic;

        public Task ShortAsync() => RunAsync(async () =>
        {
            Enter();
            await Task.Yield();
            await Task.Delay(1);
            Leave();
        });

        public bool Quick() => DoAsync(static a => a.Tick(), this);

        private void Tick()
        {
            Enter();
            // Long enough that two threads running this actor at once would overlap observably.
            Thread.SpinWait(2_000);
            _nonAtomic = _nonAtomic + 1;
            Interlocked.Increment(ref _done);
            Leave();
        }

        private void Enter()
        {
            var now = Interlocked.Increment(ref _concurrent);
            if (now > Volatile.Read(ref _maxConcurrent))
                Volatile.Write(ref _maxConcurrent, now);
        }

        private void Leave() => Interlocked.Decrement(ref _concurrent);
    }

    private sealed class FlakyRunnable : IRunnable
    {
        private static int _crashing;

        public static void ResetState() => Interlocked.Exchange(ref _crashing, 0);

        public static void CrashContinuously() => Interlocked.Exchange(ref _crashing, 1);

        public bool Run(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _crashing) != 0)
                throw new InvalidOperationException("simulated worker crash");

            cancellationToken.WaitHandle.WaitOne(2);
            return true;
        }

        public void Dispose() { }
    }
}
