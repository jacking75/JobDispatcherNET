using Xunit;

namespace JobDispatcherNET.Tests;

/// <summary>
/// Cases from the follow-up review in <c>docs/review-followup-2026-09-03.md</c>. Each one started
/// life as a console probe in that document; the identifiers in the comments are its item numbers.
/// </summary>
public sealed class SelfDrainTests
{
    /// <summary>
    /// S15 — the "save, then dispose me" shape. The drain used to wait on the very async job that
    /// was awaiting it, and the infinite <c>DisposeAsync()</c> overload then hung for good.
    /// </summary>
    [Fact]
    public async Task DisposeAsyncAwaitedInsideOwnAsyncJobDoesNotHang()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new SelfDisposingActor(host.Options());

        var job = actor.SaveThenDisposeSelf();

        await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(actor.Disposed);
        Assert.False(actor.Work(), "the actor should refuse work after disposing itself");
    }

    /// <summary>
    /// S15 — the same, on an Exclusive actor with nothing else queued. The suspension reservation
    /// keeps the count above zero, so this needs the reservation excused as well.
    /// </summary>
    [Fact]
    public async Task ExclusiveActorCanDisposeItselfFromItsOwnAsyncJob()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new SelfDisposingActor(host.Options() with { AsyncReentrancy = AsyncReentrancy.Exclusive });

        var job = actor.SaveThenDisposeSelf();

        await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(actor.Disposed);
    }

    /// <summary>
    /// S15 — the one case excusing the caller cannot rescue: an Exclusive actor will not run the
    /// queued jobs until the async job returns, and the async job is waiting for them to run. That
    /// is a real deadlock and deserves an exception rather than silence.
    /// </summary>
    [Fact]
    public async Task ExclusiveSelfDisposeWithQueuedWorkThrowsInsteadOfHanging()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "self-dispose-deadlock",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
            DetectBlockingWaitOnWorker = true,
        });
        using var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 2, TimeSpan.FromSeconds(5), "workers did not start");

        var actor = new SelfDisposingActor(new JobOptions
        {
            System = system,
            AsyncReentrancy = AsyncReentrancy.Exclusive,
        });

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = actor.WaitThenDisposeSelf(gate.Task);

        // Queued behind the async job, which under Exclusive will not run until it returns.
        Assert.True(actor.Work());
        gate.SetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("DisposeAsync", error.Message, StringComparison.Ordinal);

        system.Dispose();
    }

    /// <summary>
    /// S16 — <c>StopAsync</c> awaited from inside an async job used to wait for that job, burn the
    /// whole drain timeout and report a failed drain on every single shutdown.
    /// </summary>
    [Fact]
    public async Task StopAsyncAwaitedInsideAnAsyncJobDoesNotWaitForItsOwnCaller()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "self-stop",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });
        using var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 2, TimeSpan.FromSeconds(5), "workers did not start");

        var admin = new StoppingActor(new JobOptions { System = system }, system);
        var started = DateTime.UtcNow;

        var drained = await admin.StopTheSystem(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(drained, "the drain reported failure while waiting on its own caller");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4),
            "the drain spent its whole timeout waiting for the job that asked for it");

        system.Dispose();
    }

    /// <summary>
    /// S5 — two closers at once. The second <c>DisposeAsync</c> used to overwrite the first one's
    /// completion source, and only the last one stored was ever signalled.
    /// </summary>
    [Fact]
    public async Task ConcurrentDisposeAsyncCallsBothComplete()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new BlockingActor(host.Options());
        actor.BlockAndWait();

        var first = Task.Run(async () => await actor.DisposeAsync());
        var second = Task.Run(async () => await actor.DisposeAsync());

        actor.Release();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// S9 — the self-Ask guard used to go blank at the first await, because the actor it compared
    /// against is thread-local and an await leaves the thread.
    /// </summary>
    [Fact]
    public async Task SelfAskIsCaughtAfterTheFirstAwaitToo()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "self-ask-after-await",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
            DetectBlockingWaitOnWorker = true,
        });
        using var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 2, TimeSpan.FromSeconds(5), "workers did not start");

        var actor = new SelfAskingActor(new JobOptions
        {
            System = system,
            AsyncReentrancy = AsyncReentrancy.Exclusive,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => actor.AskSelfAfterAwaiting().WaitAsync(TimeSpan.FromSeconds(5)));

        system.Dispose();
    }

    /// <summary>
    /// S4 — an <c>async void</c> called from a job resumes on the actor through the synchronisation
    /// context, so the drain has to know about it. It used to be invisible: the drain declared the
    /// system idle and stopped the workers with the continuation still on its way back.
    /// </summary>
    [Fact]
    public async Task DrainWaitsForAsyncVoidContinuations()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "async-void",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });
        using var dispatcher = new JobDispatcher(2, new JobDispatcherOptions { System = system, IdleWaitMs = 5 });
        _ = dispatcher.RunWorkerThreadsAsync();
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 2, TimeSpan.FromSeconds(5), "workers did not start");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = new AsyncVoidActor(new JobOptions { System = system });

        Assert.True(actor.FireAndForget(gate.Task));
        TestSystem.SpinWaitFor(() => actor.PendingAsyncJobs > 0, TimeSpan.FromSeconds(5),
            "the async void method was never counted");

        Assert.False(await system.DrainAsync(TimeSpan.FromMilliseconds(300)),
            "the drain claimed to be done with an async void continuation still outstanding");

        gate.SetResult();
        Assert.True(await system.DrainAsync(TimeSpan.FromSeconds(5)));
        Assert.True(actor.Resumed);

        system.Dispose();
    }

    private sealed class SelfDisposingActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public bool Work() => DoAsync(static a => a.Nothing(), this);

        public Task SaveThenDisposeSelf() => RunAsync(async () =>
        {
            await Task.Yield();                 // "save"
            await DisposeAsync();
            Interlocked.Exchange(ref _disposed, 1);
        });

        public Task WaitThenDisposeSelf(Task gate) => RunAsync(async () =>
        {
            await gate.ConfigureAwait(false);
            await DisposeAsync();
            Interlocked.Exchange(ref _disposed, 1);
        });

        private void Nothing() { }
    }

    private sealed class StoppingActor(JobOptions options, JobSystem system) : AsyncExecutable(options)
    {
        public Task<bool> StopTheSystem(TimeSpan timeout) => AskAsync(() => system.StopAsync(timeout));
    }

    private sealed class SelfAskingActor(JobOptions options) : AsyncExecutable(options)
    {
        public Task AskSelfAfterAwaiting() => RunAsync(async () =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            await Ask(() => 1);
        });
    }

    private sealed class AsyncVoidActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _resumed;

        public bool Resumed => Volatile.Read(ref _resumed) != 0;

        public bool FireAndForget(Task gate) => DoAsync(static t => t.Self.Handle(t.Gate), (Self: this, Gate: gate));

        // Deliberately async void: the shape an event-handler signature forces on server code, and
        // the one the drain could not see.
        private async void Handle(Task gate)
        {
            await gate;
            Interlocked.Exchange(ref _resumed, 1);
        }
    }
}

/// <summary>Failure accounting for the request/response and async APIs (S17).</summary>
public sealed class AwaitedFailureTests
{
    [Fact]
    public async Task FailingAskAndRunAsyncJobsAreCountedAsFailures()
    {
        using var host = new TestSystem(workers: 2);
        var errors = 0;
        var actor = new FailingActor(host.Options(), _ => Interlocked.Increment(ref errors));

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => actor.AskAndFail());
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => actor.RunAsyncAndFail());

        TestSystem.SpinWaitFor(() => host.System.Metrics.TotalJobsFailed >= 10, TimeSpan.FromSeconds(5),
            $"only {host.System.Metrics.TotalJobsFailed} of 10 failures were counted");

        // RunAsync is fire-and-forget shaped, so its failures reach OnJobError. Ask hands its
        // exception to the caller, so reporting it again would report it twice.
        TestSystem.SpinWaitFor(() => Volatile.Read(ref errors) == 5, TimeSpan.FromSeconds(5),
            $"OnJobError ran {Volatile.Read(ref errors)} times, expected 5");
    }

    [Fact]
    public async Task AFailingAskNoLongerResetsTheConsecutiveFailureStreak()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new FailingActor(host.Options() with { MaxConsecutiveFailures = 2 }, _ => { });

        Assert.True(actor.ThrowFromDoAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.AskAndFail());

        TestSystem.SpinWaitFor(() => actor.IsFaulted, TimeSpan.FromSeconds(5),
            "the failing Ask was counted as a success and reset the streak");
        Assert.False(actor.ThrowFromDoAsync(), "a faulted actor must refuse work");
    }

    /// <summary>
    /// A <c>RunAsync</c> body that throws before its first <c>await</c> completes inside the job, so
    /// the failure and the flush loop's streak reset happen in the same breath — and the reset came
    /// second.
    /// </summary>
    [Fact]
    public async Task ARunAsyncThatFaultsBeforeItsFirstAwaitCountsTowardsTheStreak()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new FailingActor(host.Options() with { MaxConsecutiveFailures = 2 }, _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.RunAsyncAndFailImmediately());
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.RunAsyncAndFailImmediately());

        TestSystem.SpinWaitFor(() => actor.IsFaulted, TimeSpan.FromSeconds(5),
            "the synchronous fault was counted and then reset by the same job");
    }

    [Fact]
    public async Task AskFailuresReachOnJobErrorWhenReportAwaitedFailuresIsOn()
    {
        using var host = new TestSystem(workers: 2);
        var errors = 0;
        var actor = new FailingActor(
            host.Options() with { ReportAwaitedFailures = true },
            _ => Interlocked.Increment(ref errors));

        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.AskAndFail());

        TestSystem.SpinWaitFor(() => Volatile.Read(ref errors) == 1, TimeSpan.FromSeconds(5),
            "the opted-in Ask failure never reached OnJobError");
    }

    private sealed class FailingActor(JobOptions options, Action<Exception> onError) : AsyncExecutable(options)
    {
        public Task<int> AskAndFail() => Ask<int>(() => throw new InvalidOperationException("ask boom"));

        public Task RunAsyncAndFail() => RunAsync(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("run boom");
        });

        /// <summary>Faults before its first await, so it returns an already-faulted task.</summary>
        public Task RunAsyncAndFailImmediately() =>
            RunAsync(() => throw new InvalidOperationException("immediate boom"));

        public bool ThrowFromDoAsync() =>
            DoAsync(static _ => throw new InvalidOperationException("do boom"), this);

        protected override void OnJobError(Exception exception) => onError(exception);
    }
}

/// <summary>Bounds and isolation the third review pass found missing (S18, S19, S2).</summary>
public sealed class TimerBoundAndIsolationTests
{
    /// <summary>
    /// S18 — a timer holds no queue slot until it fires, so <c>MaxQueueSize</c> alone let a client
    /// arm an unlimited number of them and only started dropping once they came due.
    /// </summary>
    [Fact]
    public void TimersRespectTheActorsBound()
    {
        using var host = new TestSystem(workers: 1);
        var refusals = new List<DropReason>();
        var actor = new TimerBoundActor(host.Options(maxQueue: 4, onDropped: (_, reason) =>
        {
            lock (refusals) { refusals.Add(reason); }
        }));

        var handles = new List<ITimerHandle>();
        for (var i = 0; i < 4; i++)
            handles.Add(actor.Arm(TimeSpan.FromSeconds(30)));

        Assert.All(handles, h => Assert.True(h.IsPending));
        Assert.Equal(4, actor.PendingTimerCount);

        var refused = actor.Arm(TimeSpan.FromSeconds(30));
        Assert.False(refused.IsPending, "the fifth timer was armed past the bound");
        lock (refusals)
        {
            Assert.Equal([DropReason.TimerQueueFull], refusals);
        }

        // Cancelling gives the slot back.
        Assert.True(handles[0].Cancel());
        Assert.Equal(3, actor.PendingTimerCount);
        Assert.True(actor.Arm(TimeSpan.FromSeconds(30)).IsPending);
    }

    [Fact]
    public void AFiredOneShotTimerGivesItsSlotBack()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new TimerBoundActor(host.Options(maxQueue: 2));

        Assert.True(actor.Arm(TimeSpan.FromMilliseconds(10)).IsPending);
        TestSystem.SpinWaitFor(() => actor.Fired == 1, TimeSpan.FromSeconds(5), "the timer never fired");
        TestSystem.SpinWaitFor(() => actor.PendingTimerCount == 0, TimeSpan.FromSeconds(5),
            $"the fired timer still holds {actor.PendingTimerCount} slot(s)");
    }

    [Fact]
    public void ARepeatingTimerCountsAsOneForItsWholeLife()
    {
        using var host = new TestSystem(workers: 2);
        var actor = new TimerBoundActor(host.Options());

        var handle = actor.Every(TimeSpan.FromMilliseconds(20));
        TestSystem.SpinWaitFor(() => actor.Fired >= 3, TimeSpan.FromSeconds(10), "the timer did not repeat");

        Assert.Equal(1, actor.PendingTimerCount);
        Assert.True(handle.Cancel());
        Assert.Equal(0, actor.PendingTimerCount);
    }

    /// <summary>
    /// S19 — <c>IsWorkerThread</c> is true on every dispatcher's threads, so a producer on system
    /// A's worker used to flush system B's Scheduled actor inline, on A's thread.
    /// </summary>
    [Fact]
    public void ScheduledActorsStayOnTheirOwnSystemsWorkers()
    {
        using var a = new TestSystem(workers: 1, options: new JobSystemOptions
        {
            Name = "iso-a",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
            DetectBlockingWaitOnWorker = false,
        });
        using var b = new TestSystem(workers: 1, options: new JobSystemOptions
        {
            Name = "iso-b",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
            DetectBlockingWaitOnWorker = false,
        });

        var onB = new ThreadNamingActor(b.Options(mode: ExecutionMode.Scheduled));

        // Post to B's actor from inside a job running on one of A's workers.
        Assert.True(a.System.Post(() => onB.Record()));

        TestSystem.SpinWaitFor(() => onB.ThreadName is not null, TimeSpan.FromSeconds(5),
            "B's actor never ran");

        Assert.StartsWith("JobWorker-iso-b", onB.ThreadName, StringComparison.Ordinal);
    }

    /// <summary>
    /// S2 — waking many actors from one job queued them all on a thread-local list no other worker
    /// could take from, so a broadcast ran on the one thread while the rest of the pool idled.
    /// </summary>
    [Fact]
    public void ABroadcastSpreadsAcrossTheWorkerPool()
    {
        using var host = new TestSystem(workers: 4);
        var targets = Enumerable.Range(0, 32).Select(_ => new ThreadNamingActor(host.Options())).ToArray();
        var zone = new BroadcastActor(host.Options(), targets);

        Assert.True(zone.Broadcast());

        TestSystem.SpinWaitFor(() => targets.All(t => t.ThreadName is not null), TimeSpan.FromSeconds(30),
            "not every target ran");

        var threads = targets.Select(t => t.ThreadName).Distinct(StringComparer.Ordinal).Count();
        Assert.True(threads > 1, $"all {targets.Length} actors ran on one thread; the pool was bypassed");
    }

    /// <summary>The pre-fix behaviour is still available for workloads that want the locality.</summary>
    [Fact]
    public void FanOutToWorkersFalseKeepsEverythingOnTheFlushingThread()
    {
        using var host = new TestSystem(workers: 4);
        var options = host.Options() with { FanOutToWorkers = false };
        var targets = Enumerable.Range(0, 32).Select(_ => new ThreadNamingActor(options)).ToArray();
        var zone = new BroadcastActor(options, targets);

        Assert.True(zone.Broadcast());

        TestSystem.SpinWaitFor(() => targets.All(t => t.ThreadName is not null), TimeSpan.FromSeconds(30),
            "not every target ran");

        var threads = targets.Select(t => t.ThreadName).Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(1, threads);
    }

    private sealed class TimerBoundActor(JobOptions options) : AsyncExecutable(options)
    {
        private int _fired;

        public int Fired => Volatile.Read(ref _fired);

        public ITimerHandle Arm(TimeSpan delay) => DoAsyncAfter(delay, static a => a.Tick(), this);

        public ITimerHandle Every(TimeSpan period) => DoAsyncEvery(period, Tick);

        private void Tick() => Interlocked.Increment(ref _fired);
    }

    /// <summary>The zone actor of the review: one job, a hundred peers woken.</summary>
    private sealed class BroadcastActor(JobOptions options, ThreadNamingActor[] targets)
        : AsyncExecutable(options)
    {
        public bool Broadcast() => DoAsync(static a => a.Wake(), this);

        private void Wake()
        {
            foreach (var target in targets)
                target.RecordSlowly();
        }
    }

    private sealed class ThreadNamingActor(JobOptions options) : AsyncExecutable(options)
    {
        private string? _threadName;

        public string? ThreadName => Volatile.Read(ref _threadName);

        public bool Record() => DoAsync(static a => a.Capture(sleep: false), this);

        public bool RecordSlowly() => DoAsync(static a => a.Capture(sleep: true), this);

        private void Capture(bool sleep)
        {
            if (sleep)
                Thread.Sleep(20);
            Volatile.Write(ref _threadName, Thread.CurrentThread.Name ?? "unnamed");
        }
    }
}

/// <summary>Worker-pool survival and pool sizing (S20, S6, S12).</summary>
public sealed class LastWorkerAndPoolTests
{
    /// <summary>
    /// S20 — the restart budget is a policy for "this slot is bad, the others can carry the load".
    /// With no other worker there is nothing to carry it, and the actors already on the ready queue
    /// have no way to run at all.
    /// </summary>
    [Fact]
    public void TheLastWorkerIsRestartedPastItsBudget()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "last-worker",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        CrashesThenSettlesRunnable.Reset();
        using var dispatcher = new JobDispatcher<CrashesThenSettlesRunnable>(1, new JobDispatcherOptions
        {
            System = system,
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 0,           // every restart is already past the budget
            RestartBackoff = TimeSpan.FromMilliseconds(1),
            MaxRestartBackoff = TimeSpan.FromMilliseconds(5),
            RestartCountResetAfter = TimeSpan.Zero,
            KeepLastWorkerAlive = true,
        });
        _ = dispatcher.RunWorkerThreadsAsync();

        TestSystem.SpinWaitFor(() => CrashesThenSettlesRunnable.Crashes >= 3, TimeSpan.FromSeconds(20),
            $"the slot gave up after {CrashesThenSettlesRunnable.Crashes} crash(es)");
        TestSystem.SpinWaitFor(() => system.LiveWorkerCount == 1, TimeSpan.FromSeconds(20),
            "the last worker was never brought back");

        system.Dispose();
    }

    /// <summary>
    /// S20 — when nothing is going to replace the last worker, whatever is on the ready queue is
    /// stranded: it is nobody's to run, and posts to those actors only queue up behind it.
    /// </summary>
    [Fact]
    public void TheLastWorkerDrainsTheReadyQueueOnItsWayOut()
    {
        var system = new JobSystem(new JobSystemOptions
        {
            Name = "stranded",
            Logger = NullJobLogger.Instance,
            PublishMeter = false,
        });

        BlockThenCrashRunnable.Reset();
        using var dispatcher = new JobDispatcher<BlockThenCrashRunnable>(1, new JobDispatcherOptions
        {
            System = system,
            RestartFailedWorkers = false,       // nothing will replace it
        });
        _ = dispatcher.RunWorkerThreadsAsync();

        Assert.True(BlockThenCrashRunnable.Entered.Wait(TimeSpan.FromSeconds(5)), "the runner never started");

        var ran = new ManualResetEventSlim(false);
        Assert.True(system.Post(ran.Set));

        BlockThenCrashRunnable.Release();

        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)),
            "the ready item was left stranded when the last worker went away");

        system.Dispose();
    }

    /// <summary>
    /// S6 — the shared pool trades in batches of 32, and truncating the cap meant anything under 32
    /// allowed zero batches. The pool switched itself off with no error and nothing in the docs.
    /// </summary>
    [Fact]
    public void ASmallMaxPoolSizeStillPublishesOneBatch()
    {
        var previous = Job<PoolProbe>.MaxPoolSize;
        try
        {
            Job<PoolProbe>.MaxPoolSize = 8;
            Job<PoolProbe>.ClearPool();

            // Recycle enough on this thread to overflow the local stack and publish a batch.
            var jobs = new List<Job<PoolProbe>>(512);
            for (var i = 0; i < 512; i++)
                jobs.Add(Job<PoolProbe>.Rent(static _ => { }, new PoolProbe()));
            foreach (var job in jobs)
                job.Execute();

            Assert.True(Job<PoolProbe>.PoolSize > 0,
                "a cap below one batch silently disabled the shared pool");
        }
        finally
        {
            Job<PoolProbe>.MaxPoolSize = previous;
            Job<PoolProbe>.ClearPool();
        }
    }

    /// <summary>S12 — truncation must not leave half of a surrogate pair behind.</summary>
    [Fact]
    public void ALongNameIsNotCutThroughASurrogatePair()
    {
        using var host = new TestSystem(workers: 0);

        // 127 plain chars then an emoji, so the 128-char cap falls between its two code units.
        var name = new string('a', 127) + "\U0001F600";
        var actor = new CountingActor(host.Options() with { Name = name });

        Assert.DoesNotContain(actor.Name, static c => char.IsSurrogate(c));
        Assert.Equal(127, actor.Name.Length);
    }

    public sealed class PoolProbe;

    private sealed class CrashesThenSettlesRunnable : IRunnable
    {
        private static int _crashes;

        public static int Crashes => Volatile.Read(ref _crashes);

        public static void Reset() => Interlocked.Exchange(ref _crashes, 0);

        public bool Run(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _crashes) <= 3)
                throw new InvalidOperationException("this worker crashes a few times before settling");

            Thread.Sleep(5);
            return true;
        }

        public void Dispose() { }
    }

    private sealed class BlockThenCrashRunnable : IRunnable
    {
        private static ManualResetEventSlim _gate = new(false);

        public static ManualResetEventSlim Entered { get; private set; } = new(false);

        public static void Reset()
        {
            _gate = new ManualResetEventSlim(false);
            Entered = new ManualResetEventSlim(false);
        }

        public static void Release() => _gate.Set();

        public bool Run(CancellationToken cancellationToken)
        {
            Entered.Set();
            _gate.Wait(TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("and now the last worker dies");
        }

        public void Dispose() { }
    }
}
