using System.Collections.Concurrent;

namespace JobDispatcherNET;

/// <summary>
/// Base class for all job entries
/// </summary>
public abstract class JobEntry
{
    public abstract void Execute();
}

/// <summary>
/// Pooled job implementation using delegates.
/// Rent/Return pattern avoids heap allocation on every DoAsync call.
/// </summary>
public sealed class Job : JobEntry
{
    private static readonly ConcurrentBag<Job> Pool = new();

    private Action? _action;

    private Job() { }

    /// <summary>
    /// Rent a Job from the pool (or create one if empty).
    /// </summary>
    public static Job Rent(Action action)
    {
        if (!Pool.TryTake(out var job))
            job = new Job();
        job._action = action;
        return job;
    }

    public override void Execute()
    {
        try
        {
            _action?.Invoke();
        }
        finally
        {
            _action = null;
            Pool.Add(this);
        }
    }
}
