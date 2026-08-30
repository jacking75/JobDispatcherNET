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

            TestSystem.SpinWaitFor(() => actor.Done == 40, TimeSpan.FromSeconds(10),
                $"round {round}: only {actor.Done} of 40 jobs ran");

            Assert.Equal(1, actor.MaxConcurrent);
            Assert.Equal(40, actor.NonAtomicCount);
            Assert.Equal(0, actor.RemainingTaskCount);
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

        // Every timer either fired or was cancelled, never both.
        Assert.Equal(armed, fired + cancelled);

        TestSystem.SpinWaitFor(() => actor.Executed == fired, TimeSpan.FromSeconds(20),
            $"{actor.Executed} callbacks ran but only {fired} timers reported firing — " +
            "a timer that reported being cancelled ran anyway");

        var metrics = host.System.Metrics.Snapshot();
        Assert.Equal(cancelled, metrics.TimersCancelled);
        Assert.Equal(fired, metrics.TimersFired);
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
