using System.Collections.Concurrent;

namespace JobDispatcherNET;

/// <summary>
/// 라이브러리 내부 — Timer 콜백을 워커 스레드로 옮기는 중간 큐.
///
/// 왜 필요한가:
///   TimerQueue 의 백그라운드 Task 는 ThreadPool 에서 await PeriodicTimer 한다.
///   만약 ProcessDueJobs 가 owner.DoTask(job) 를 직접 호출하면, 그 호출이 leader 가 되어
///   actor 의 Flush 를 ThreadPool 스레드에서 실행하게 된다.
///   → "전용 OS 스레드(워커)에서만 actor 실행" 이라는 약속이 깨진다.
///   → ThreadContext.TickCount, ThreadLocal Timer 등이 비-워커 스레드에서 잘못된 값을 반환.
///
/// 해결:
///   TimerQueue 가 due 한 작업을 이 큐에 enqueue 만 한다.
///   JobDispatcher 의 워커는 매 Run() tick 마다 이 큐를 드레인해 owner.DoTask(job) 를
///   자기(워커) 스레드에서 실행한다. → leader 가 워커, 약속 유지.
///
/// 글로벌이므로 한 프로세스 내 여러 JobDispatcher 가 공유한다. 워커가 없으면
/// 큐에 들어간 작업은 처리되지 않는다는 점에 유의.
/// </summary>
internal static class TimerDispatchQueue
{
    private static readonly ConcurrentQueue<TimerDispatchItem> _queue = new();
    private static long _count;

    public static long Count => Interlocked.Read(ref _count);

    public static void Enqueue(AsyncExecutable owner, JobEntry job)
    {
        Interlocked.Increment(ref _count);
        _queue.Enqueue(new TimerDispatchItem(owner, job));
    }

    public static bool TryDequeue(out TimerDispatchItem item)
    {
        if (_queue.TryDequeue(out item))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }
        return false;
    }

    /// <summary>워커 스레드가 한 tick 동안 처리할 timer dispatch 수의 상한.</summary>
    public const int MaxDrainPerTick = 256;

    public readonly record struct TimerDispatchItem(AsyncExecutable Owner, JobEntry Job);
}
