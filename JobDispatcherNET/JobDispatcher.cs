using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobDispatcherNET;

/// <summary>
/// Manages worker threads for executing jobs
/// </summary>
public sealed class JobDispatcher<T> : IAsyncDisposable where T : IRunnable, new()
{
    private readonly int _workerCount;
    private readonly List<Task> _workerTasks = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Creates a new JobDispatcher with the specified number of worker threads
    /// </summary>
    /// <param name="workerCount">Number of worker threads to create</param>
    public JobDispatcher(int workerCount)
    {
        _workerCount = workerCount;
    }

    /// <summary>
    /// Starts all worker threads
    /// </summary>
    public async Task RunWorkerThreadsAsync()
    {
        for (int i = 0; i < _workerCount; i++)
        {
            _workerTasks.Add(RunWorkerAsync());
        }

        await Task.WhenAll(_workerTasks);
    }

    private async Task RunWorkerAsync()
    {
        await using var runner = new T();

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                bool shouldContinue = await runner.RunAsync(_cts.Token);
                if (!shouldContinue)
                    break;

                // Process timer tasks
                await Task.Delay(1, _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
        {
            // Normal cancellation, ignore
        }
    }

    /// <summary>
    /// Stops all worker threads
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            if (_workerTasks.Count > 0)
                await Task.WhenAll(_workerTasks);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation exceptions
        }

        _cts.Dispose();
    }
}