using JobDispatcherNET;

namespace ExampleMmorpgServer;

/// <summary>
/// 워커 스레드 구현.
/// 전용 OS 스레드에서 실행되며 ThreadLocal 상태가 보장된다.
/// TickCount 갱신은 JobDispatcher가 자동으로 처리하므로 워커에서는 불필요.
/// </summary>
public class GameWorker : IRunnable
{
    private static int _workerCounter;
    private readonly int _workerId;

    public GameWorker()
    {
        _workerId = Interlocked.Increment(ref _workerCounter);
        Console.WriteLine($"게임 워커 #{_workerId} 시작 (스레드:{Environment.CurrentManagedThreadId})");
    }

    public bool Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        // 전용 스레드에서 Thread.Sleep — 컨텍스트 스위칭 없음
        Thread.Sleep(1);

        return true;
    }

    public void Dispose()
    {
        Console.WriteLine($"게임 워커 #{_workerId} 종료 (스레드:{Environment.CurrentManagedThreadId})");
    }
}
