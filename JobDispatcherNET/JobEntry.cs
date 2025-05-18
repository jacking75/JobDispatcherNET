using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobDispatcherNET;


/// <summary>
/// Base class for all job entries
/// </summary>
public abstract class JobEntry
{
    /// <summary>
    /// Executes the job
    /// </summary>
    public abstract void Execute();
}

/// <summary>
/// Job implementation using delegates
/// </summary>
public sealed class Job : JobEntry
{
    private readonly Action _action;

    public Job(Action action)
    {
        _action = action;
    }

    public override void Execute() => _action();
}
