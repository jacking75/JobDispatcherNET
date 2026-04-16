namespace JobDispatcherNET;

/// <summary>
/// Manages dedicated worker threads for executing jobs.
/// Uses real OS threads (not thread pool) to guarantee ThreadLocal stability.
/// </summary>
public sealed class JobDispatcher<T> : IDisposable, IAsyncDisposable where T : IRunnable, new()
{
    private readonly int _workerCount;
    private readonly Thread[] _threads;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public JobDispatcher(int workerCount)
    {
        _workerCount = workerCount;
        _threads = new Thread[workerCount];
    }

    /// <summary>
    /// Starts all worker threads and returns a Task that completes when all workers exit.
    /// </summary>
    public Task RunWorkerThreadsAsync()
    {
        var tcs = new TaskCompletionSource();
        int completed = 0;

        for (int i = 0; i < _workerCount; i++)
        {
            _threads[i] = new Thread(() =>
            {
                RunWorker();
                if (Interlocked.Increment(ref completed) == _workerCount)
                    tcs.TrySetResult();
            })
            {
                IsBackground = true,
                Name = $"JobWorker-{i}"
            };
            _threads[i].Start();
        }

        return tcs.Task;
    }

    private void RunWorker()
    {
        using var runner = new T();
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                ThreadContext.TickCount = ThreadContext.Timer.GetCurrentTick();

                if (!runner.Run(_cts.Token))
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 예기치 않은 예외 — 워커가 죽기 전에 반드시 알린다
            AsyncExecutable.OnError?.Invoke(ex);
        }
        finally
        {
            ThreadContext.Timer.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        foreach (var thread in _threads)
        {
            if (thread is { IsAlive: true })
                thread.Join(TimeSpan.FromSeconds(5));
        }
        _cts.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
