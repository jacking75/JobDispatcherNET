using System.Threading.Channels;


namespace JobDispatcherNET;

/// <summary>
/// Base class that enables asynchronous execution of methods
/// </summary>
public abstract class AsyncExecutable : IAsyncDisposable
{
    private readonly Channel<JobEntry> _jobQueue;
    private int _remainingTaskCount;
    private int _refCount;
    private readonly SemaphoreSlim _disposeSignal = new(1, 1);

    protected AsyncExecutable()
    {
        _jobQueue = Channel.CreateUnbounded<JobEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Executes a method asynchronously
    /// </summary>
    public void DoAsync(Action action)
    {
        var job = new Job(action);
        DoTask(job);
    }

    /// <summary>
    /// Executes a method with a delay
    /// </summary>
    public void DoAsyncAfter(TimeSpan delay, Action action)
    {
        var job = new Job(action);
        ThreadContext.Timer.ScheduleTask(this, delay, job);
    }

    internal void AddRef() => Interlocked.Increment(ref _refCount);

    internal void ReleaseRef() => Interlocked.Decrement(ref _refCount);

    internal void DoTask(JobEntry task)
    {
        if (Interlocked.Increment(ref _remainingTaskCount) > 1)
        {
            // Register the task in this dispatcher
            _jobQueue.Writer.TryWrite(task);
        }
        else
        {
            // Register the task in this dispatcher
            _jobQueue.Writer.TryWrite(task);

            AddRef(); // Reference count +1 for this object

            // Does any dispatcher exist occupying this worker-thread at this moment?
            var currentExecuter = ThreadContext.CurrentExecuter;
            if (currentExecuter is not null)
            {
                // Just register this dispatcher in this worker-thread
                ThreadContext.ExecuterList.Add(this);
            }
            else
            {
                try
                {
                    // Acquire
                    ThreadContext.CurrentExecuter = this;

                    // Invoke all tasks of this dispatcher
                    Flush();

                    // Invoke all tasks of other dispatchers registered in this thread
                    while (ThreadContext.ExecuterList.Count > 0)
                    {
                        var dispatcher = ThreadContext.ExecuterList[0];
                        ThreadContext.ExecuterList.RemoveAt(0);
                        dispatcher.Flush();
                        dispatcher.ReleaseRef();
                    }
                }
                finally
                {
                    // Release
                    ThreadContext.CurrentExecuter = null;
                    ReleaseRef(); // Reference count -1 for this object
                }
            }
        }
    }

    internal void Flush()
    {
        while (true)
        {
            if (_jobQueue.Reader.TryRead(out var job))
            {
                try
                {
                    job.Execute();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error executing job: {ex}");
                }

                if (Interlocked.Decrement(ref _remainingTaskCount) == 0)
                    break;
            }
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        await _disposeSignal.WaitAsync();
        try
        {
            // Wait for all jobs to complete
            while (_remainingTaskCount > 0)
            {
                await Task.Delay(10);
            }

            _jobQueue.Writer.Complete();
            _disposeSignal.Dispose();
        }
        finally
        {
            _disposeSignal.Release();
        }
    }
}