using JobDispatcherNET;

namespace ExampleConsoleApp;

/// <summary>
/// Worker implementation for the data processing system.
/// Runs on a dedicated OS thread — ThreadLocal state is stable.
/// </summary>
public class ProcessingWorker : IRunnable
{
    private static int _workerCounter = 0;
    private readonly int _workerId;

    public ProcessingWorker()
    {
        _workerId = Interlocked.Increment(ref _workerCounter);
        Console.WriteLine($"Processing worker {_workerId} created on thread {Environment.CurrentManagedThreadId}");
    }

    public bool Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        // Thread.Sleep on dedicated thread — no thread context switch
        Thread.Sleep(Random.Shared.Next(1, 5));

        if (Random.Shared.Next(100) < 5)
        {
            Console.WriteLine($"Worker {_workerId} on thread {Environment.CurrentManagedThreadId} " +
                              $"active at tick {ThreadContext.TickCount}");
        }

        return true;
    }

    public void Dispose()
    {
        Console.WriteLine($"Processing worker {_workerId} shutting down");
    }
}
