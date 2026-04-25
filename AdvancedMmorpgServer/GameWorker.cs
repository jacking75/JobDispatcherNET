using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// 워커 스레드. 전용 OS 스레드에서 ThreadLocal 안정성을 보장하는 자리를 차지하기 위해 존재한다.
/// 실제 작업 큐 처리는 AsyncExecutable.DoTask가 직접 잡아 처리하므로
/// 본 루프는 깨어 있기만 하면 된다.
/// </summary>
public sealed class GameWorker : IRunnable
{
    private static int _counter;
    private readonly int _id;

    public GameWorker()
    {
        _id = Interlocked.Increment(ref _counter);
        Console.WriteLine($"[워커 #{_id}] 시작");
    }

    public bool Run(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        Thread.Sleep(1);
        return true;
    }

    public void Dispose()
    {
        Console.WriteLine($"[워커 #{_id}] 종료");
    }
}
