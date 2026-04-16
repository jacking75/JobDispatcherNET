using System.Diagnostics;

namespace JobDispatcherNET;

/// <summary>
/// Manages timed jobs using Stopwatch for high-precision timing.
/// Background PeriodicTimer ensures scheduled tasks fire even when
/// DoAsyncAfter is called from non-worker threads.
/// </summary>
public sealed class TimerQueue : IDisposable
{
    private readonly PriorityQueue<TimerJob, long> _queue = new();
    private readonly object _lock = new();
    private readonly PeriodicTimer _timer;
    private readonly Task _processingTask;
    private readonly long _startTicks = Stopwatch.GetTimestamp();
    private readonly List<TimerJob> _jobBuffer = [];
    private int _disposed;

    public TimerQueue()
    {
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));
        _processingTask = ProcessTimerJobsAsync();
    }

    /// <summary>
    /// Returns milliseconds since this TimerQueue was created.
    /// Uses Stopwatch for sub-millisecond precision (vs DateTime.UtcNow's ~15ms).
    /// </summary>
    public long GetCurrentTick() =>
        (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

    public void ScheduleTask(AsyncExecutable owner, TimeSpan delay, JobEntry task)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var dueTime = GetCurrentTick() + (long)delay.TotalMilliseconds;

        lock (_lock)
        {
            _queue.Enqueue(new TimerJob(owner, task), dueTime);
        }
    }

    private async Task ProcessTimerJobsAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                ProcessDueJobs();
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void ProcessDueJobs()
    {
        _jobBuffer.Clear();

        lock (_lock)
        {
            var currentTick = GetCurrentTick();
            while (_queue.Count > 0 && _queue.TryPeek(out _, out var dueTime) && currentTick >= dueTime)
            {
                _jobBuffer.Add(_queue.Dequeue());
            }
        }

        foreach (var job in _jobBuffer)
        {
            job.Owner.DoTask(job.Task);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Dispose();
        try { _processingTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* shutdown */ }
    }

    /// <summary>
    /// Value type — no heap allocation per schedule.
    /// </summary>
    private readonly record struct TimerJob(AsyncExecutable Owner, JobEntry Task);
}
