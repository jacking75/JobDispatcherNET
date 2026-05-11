using System.Diagnostics;

namespace JobDispatcherNET;

/// <summary>
/// 고정밀 지연 작업 큐. <see cref="Stopwatch"/> 기반.
///
/// v2 변경점:
///   - timer fire 시 owner.DoTask 를 직접 호출하지 않는다.
///     대신 <see cref="TimerDispatchQueue"/> 에 (owner, job) 을 enqueue 한다.
///     실제 owner.DoTask 는 워커 스레드의 Run() 루프에서 일어난다.
///   - 이로써 actor 의 Flush 가 ThreadPool 스레드에서 실행되는 hijack 을 막는다.
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

    private static long _pendingJobsAcrossAllInstances;

    /// <summary>모든 TimerQueue 인스턴스의 대기 작업 합계 (메트릭).</summary>
    public static long PendingJobsAcrossAllInstances => Interlocked.Read(ref _pendingJobsAcrossAllInstances);

    public TimerQueue() : this(TimeSpan.FromMilliseconds(1)) { }

    public TimerQueue(TimeSpan tickInterval)
    {
        _timer = new PeriodicTimer(tickInterval);
        _processingTask = ProcessTimerJobsAsync();
    }

    /// <summary>이 인스턴스 생성 후 경과 시간(ms).</summary>
    public long GetCurrentTick() =>
        (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

    /// <summary>이 인스턴스에 대기 중인 작업 수.</summary>
    public int PendingCount
    {
        get { lock (_lock) return _queue.Count; }
    }

    public void ScheduleTask(AsyncExecutable owner, TimeSpan delay, JobEntry task)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var dueTime = GetCurrentTick() + (long)delay.TotalMilliseconds;

        lock (_lock)
        {
            _queue.Enqueue(new TimerJob(owner, task), dueTime);
        }
        Interlocked.Increment(ref _pendingJobsAcrossAllInstances);
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
        catch (Exception ex)
        {
            JobLog.Error("TimerQueue processing loop terminated unexpectedly", ex);
        }
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

        if (_jobBuffer.Count == 0) return;

        Interlocked.Add(ref _pendingJobsAcrossAllInstances, -_jobBuffer.Count);

        // ★ 핵심 변경: owner.DoTask 를 ThreadPool 에서 직접 호출하지 않는다.
        // 워커 스레드의 Run() 루프가 TimerDispatchQueue 를 드레인하면서 자기 스레드에서 호출한다.
        foreach (var job in _jobBuffer)
        {
            TimerDispatchQueue.Enqueue(job.Owner, job.Task);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Dispose();
        try { _processingTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* shutdown */ }

        // 잔여 큐 카운터 정리
        lock (_lock)
        {
            if (_queue.Count > 0)
                Interlocked.Add(ref _pendingJobsAcrossAllInstances, -_queue.Count);
            _queue.Clear();
        }
    }

    private readonly record struct TimerJob(AsyncExecutable Owner, JobEntry Task);
}
