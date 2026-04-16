using System.Threading.Channels;

namespace JobDispatcherNET;

/// <summary>
/// Base class that enables asynchronous execution of methods.
/// Each instance has its own job queue — jobs within the same instance
/// are serialized automatically without locks.
/// </summary>
public abstract class AsyncExecutable : IAsyncDisposable
{
    /// <summary>
    /// Global error handler. Set this to receive job execution errors
    /// instead of losing them to Console.WriteLine.
    /// </summary>
    public static Action<Exception>? OnError { get; set; }

    private readonly Channel<JobEntry> _jobQueue;
    private int _remainingTaskCount;
    private volatile TaskCompletionSource? _drainTcs;

    protected AsyncExecutable()
    {
        _jobQueue = Channel.CreateUnbounded<JobEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Executes a method asynchronously through this dispatcher's queue.
    /// </summary>
    public void DoAsync(Action action)
    {
        var job = Job.Rent(action);
        DoTask(job);
    }

    /// <summary>
    /// Executes a method after a delay through the current thread's timer.
    /// Must be called from a worker thread context (inside DoAsync callback)
    /// for the timer to fire reliably.
    /// </summary>
    public void DoAsyncAfter(TimeSpan delay, Action action)
    {
        var job = Job.Rent(action);
        ThreadContext.Timer.ScheduleTask(this, delay, job);
    }

    internal void DoTask(JobEntry task)
    {
        if (Interlocked.Increment(ref _remainingTaskCount) > 1)
        {
            if (!_jobQueue.Writer.TryWrite(task))
            {
                // Channel closed (DisposeAsync called) — roll back to prevent Flush spin
                Interlocked.Decrement(ref _remainingTaskCount);
                return;
            }
        }
        else
        {
            if (!_jobQueue.Writer.TryWrite(task))
            {
                Interlocked.Decrement(ref _remainingTaskCount);
                return;
            }

            var currentExecuter = ThreadContext.CurrentExecuter;
            if (currentExecuter is not null)
            {
                ThreadContext.ExecuterQueue.Enqueue(this);
            }
            else
            {
                try
                {
                    ThreadContext.CurrentExecuter = this;

                    Flush();

                    while (ThreadContext.ExecuterQueue.TryDequeue(out var dispatcher))
                    {
                        dispatcher.Flush();
                    }
                }
                finally
                {
                    ThreadContext.CurrentExecuter = null;
                }
            }
        }
    }

    internal void Flush()
    {
        var spinner = new SpinWait();
        while (true)
        {
            if (_jobQueue.Reader.TryRead(out var job))
            {
                spinner.Reset();
                try
                {
                    job.Execute();
                }
                catch (Exception ex)
                {
                    if (OnError is { } handler)
                        handler(ex);
                    else
                        Console.Error.WriteLine($"[JobDispatcherNET] Unhandled job error: {ex}");
                }

                if (Interlocked.Decrement(ref _remainingTaskCount) == 0)
                {
                    _drainTcs?.TrySetResult();
                    break;
                }
            }
            else
            {
                spinner.SpinOnce();
            }
        }
    }

    /// <summary>
    /// Waits for all pending jobs to complete, then closes the queue.
    /// Signal-based (no polling).
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _remainingTaskCount) > 0)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainTcs = tcs;

            if (Volatile.Read(ref _remainingTaskCount) > 0)
                await tcs.Task;
        }

        _jobQueue.Writer.Complete();
        GC.SuppressFinalize(this);
    }
}
