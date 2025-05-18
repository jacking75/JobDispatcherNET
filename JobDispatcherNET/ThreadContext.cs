using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobDispatcherNET;

/// <summary>
/// Manages thread-local storage for the job dispatcher
/// </summary>
public static class ThreadContext
{
    private static readonly ThreadLocal<TimerQueue> _timer = new(() => new TimerQueue());
    private static readonly ThreadLocal<List<AsyncExecutable>> _executerList = new(() => new List<AsyncExecutable>());
    private static readonly ThreadLocal<AsyncExecutable?> _currentExecuter = new(() => null);
    private static readonly AsyncLocal<long> _tickCount = new();

    /// <summary>
    /// Gets the timer for the current thread
    /// </summary>
    public static TimerQueue Timer => _timer.Value;

    /// <summary>
    /// Gets the list of executers registered with the current thread
    /// </summary>
    public static List<AsyncExecutable> ExecuterList => _executerList.Value;

    /// <summary>
    /// Gets or sets the current executer occupying this thread
    /// </summary>
    public static AsyncExecutable? CurrentExecuter
    {
        get => _currentExecuter.Value;
        set => _currentExecuter.Value = value;
    }

    /// <summary>
    /// Gets or sets the current tick count
    /// </summary>
    public static long TickCount
    {
        get => _tickCount.Value;
        set => _tickCount.Value = value;
    }
}
