using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JobDispatcherNET;


/// <summary>
/// Manages timed jobs
/// </summary>
public sealed class TimerQueue
{
    private readonly PriorityQueue<TimerJob, long> _queue = new();
    private readonly object _lock = new();
    private readonly PeriodicTimer _timer;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTime _startTime = DateTime.UtcNow;

    public TimerQueue()
    {
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));
        _processingTask = ProcessTimerJobsAsync(_cts.Token);
    }

    public long GetCurrentTick() => (long)(DateTime.UtcNow - _startTime).TotalMilliseconds;

    public void ScheduleTask(AsyncExecutable owner, TimeSpan delay, JobEntry task)
    {
        var dueTime = GetCurrentTick() + (long)delay.TotalMilliseconds;
        owner.AddRef(); // Add ref for timer

        lock (_lock)
        {
            _queue.Enqueue(new TimerJob(owner, task), dueTime);
        }
    }

    private async Task ProcessTimerJobsAsync(CancellationToken cancellationToken)
    {
        while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            ThreadContext.TickCount = GetCurrentTick();

            await ProcessDueJobsAsync();
        }
    }

    private async Task ProcessDueJobsAsync()
    {
        List<TimerJob>? jobsToExecute = null;

        lock (_lock)
        {
            var currentTick = GetCurrentTick();

            while (_queue.Count > 0 && _queue.TryPeek(out _, out var dueTime) && currentTick >= dueTime)
            {
                var job = _queue.Dequeue();
                jobsToExecute ??= new List<TimerJob>();
                jobsToExecute.Add(job);
            }
        }

        if (jobsToExecute != null)
        {
            foreach (var job in jobsToExecute)
            {
                var owner = job.Owner;
                owner.DoTask(job.Task);
                owner.ReleaseRef(); // Release ref for timer
            }
        }

        await Task.Delay(1); // Give other tasks a chance to run
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _processingTask.ConfigureAwait(false);
        _cts.Dispose();
        _timer.Dispose();
    }

    private record TimerJob(AsyncExecutable Owner, JobEntry Task);
}