using JobDispatcherNET;

namespace ExampleSectorServer;

/// <summary>
/// 섹터 전용 워커 스레드.
/// 전용 OS 스레드에서 실행되며 타이머 틱을 갱신한다.
/// 실제 섹터 작업은 ZoneSector(AsyncExecutable)의 DoAsync로 처리됨.
/// </summary>
public class SectorWorker : IRunnable
{
    private static int _counter;
    private readonly int _id;

    public SectorWorker()
    {
        _id = Interlocked.Increment(ref _counter);
        Console.WriteLine($"섹터 워커 #{_id} 시작 (스레드:{Environment.CurrentManagedThreadId})");
    }

    public bool Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;
        Thread.Sleep(1);
        return true;
    }

    public void Dispose()
    {
        Console.WriteLine($"섹터 워커 #{_id} 종료 (스레드:{Environment.CurrentManagedThreadId})");
    }
}
