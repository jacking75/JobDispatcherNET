namespace JobDispatcherNET;

/// <summary>
/// 라이브러리 전역 메트릭 스냅샷.
/// Prometheus/OpenTelemetry exporter 와 연결하기 위한 최소 surface.
/// </summary>
public readonly record struct JobMetricsSnapshot(
    long TotalJobsExecuted,
    long TotalJobsDropped,
    long TotalJobsFailed,
    long PendingTimerJobs,
    long PendingTimerDispatch,
    long ActiveJobPoolSize,
    long WorkerRestarts);

/// <summary>
/// 라이브러리 메트릭 카운터. Interlocked 기반.
/// </summary>
public static class JobMetrics
{
    private static long _totalExecuted;
    private static long _totalDropped;
    private static long _totalFailed;
    private static long _workerRestarts;

    internal static void IncrementExecuted() => Interlocked.Increment(ref _totalExecuted);
    internal static void IncrementDropped() => Interlocked.Increment(ref _totalDropped);
    internal static void IncrementFailed() => Interlocked.Increment(ref _totalFailed);
    internal static void IncrementWorkerRestarts() => Interlocked.Increment(ref _workerRestarts);

    /// <summary>외부 모니터링용 비차단 스냅샷.</summary>
    public static JobMetricsSnapshot Snapshot() => new(
        TotalJobsExecuted: Interlocked.Read(ref _totalExecuted),
        TotalJobsDropped: Interlocked.Read(ref _totalDropped),
        TotalJobsFailed: Interlocked.Read(ref _totalFailed),
        PendingTimerJobs: TimerQueue.PendingJobsAcrossAllInstances,
        PendingTimerDispatch: TimerDispatchQueue.Count,
        ActiveJobPoolSize: Job.PoolSize,
        WorkerRestarts: Interlocked.Read(ref _workerRestarts));

    /// <summary>테스트/벤치마크용 카운터 리셋.</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _totalExecuted, 0);
        Interlocked.Exchange(ref _totalDropped, 0);
        Interlocked.Exchange(ref _totalFailed, 0);
        Interlocked.Exchange(ref _workerRestarts, 0);
    }
}
