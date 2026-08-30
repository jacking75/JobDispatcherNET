namespace JobDispatcherNET;

/// <summary>
/// Guards against the mistakes that the leader-flush model turns into hangs rather than errors.
/// </summary>
public static class JobDiagnostics
{
    /// <summary>
    /// Throw if the calling thread is currently running an actor job.
    ///
    /// Blocking inside a job while waiting for another actor deadlocks: the thread that would run
    /// the other actor's work is the one now parked. Call this at the top of any API that blocks.
    /// </summary>
    /// <param name="system">System whose <see cref="JobSystemOptions.DetectBlockingWaitOnWorker"/> decides whether the check is armed.</param>
    /// <param name="apiName">Name reported in the exception message.</param>
    /// <exception cref="InvalidOperationException">The caller is inside an actor job.</exception>
    public static void GuardBlockingWait(JobSystem system, string apiName)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!system.Options.DetectBlockingWaitOnWorker)
            return;

        if (ThreadContext.CurrentExecuter is not { } actor)
            return;

        throw new InvalidOperationException(
            $"{apiName} was called from inside actor '{actor.Name}'. Blocking there deadlocks: this " +
            "thread is the one that would run the work being waited on. Use await, or send a " +
            "message to the other actor and let it call back. " +
            $"Set {nameof(JobSystemOptions)}.{nameof(JobSystemOptions.DetectBlockingWaitOnWorker)} = false to disable this check.");
    }

    /// <summary>True when the calling thread is currently running an actor job.</summary>
    public static bool IsInsideActorJob => ThreadContext.CurrentExecuter is not null;

    /// <summary>The actor whose job the calling thread is running, or <c>null</c>.</summary>
    public static AsyncExecutable? CurrentActor => ThreadContext.CurrentExecuter;

    /// <summary>True when the calling thread is a dispatcher worker thread.</summary>
    public static bool IsWorkerThread => ThreadContext.IsWorkerThread;
}
