using JobDispatcherNET;

namespace ExampleChatServer;

/// <summary>
/// 채팅 서버를 위한 워커 스레드 구현.
/// 전용 OS 스레드에서 실행 — ThreadLocal 안정.
/// </summary>
public class ChatWorker : IRunnable
{
    private static int _workerCounter = 0;
    private readonly int _workerId;

    public ChatWorker()
    {
        _workerId = Interlocked.Increment(ref _workerCounter);
        Console.WriteLine($"채팅 워커 {_workerId} 시작 (스레드 ID: {Environment.CurrentManagedThreadId})");
    }

    public bool Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        try
        {
            Thread.Sleep(1);

            if (Random.Shared.Next(1000) < 2)
            {
                Console.WriteLine($"워커 {_workerId} 활성 상태 (스레드 ID: {Environment.CurrentManagedThreadId})");
            }
        }
        catch (ThreadInterruptedException)
        {
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        Console.WriteLine($"채팅 워커 {_workerId} 종료 (스레드 ID: {Environment.CurrentManagedThreadId})");
    }
}
