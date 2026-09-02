using System.Collections.Concurrent;
using JobDispatcherNET;

namespace ExampleMmorpgServer;

/// <summary>
/// MMORPG 서버용 워커 — 전용 OS 스레드에서 실행 (ThreadLocal 안전).
///
/// 학습 포인트:
///   1) 외부 입력(네트워크 IO 스레드/시뮬레이션)은 <see cref="InboundCommands"/>에
///      Action 형태로 푸시한다. IO 스레드와 패킷 처리 스레드를 완전 분리하기 위함.
///   2) 워커가 Run() 한 틱마다 큐에서 하나씩 꺼내 invoke 하면, 그 invoke 가 결국
///      GameServer.HandleX → GameZone.DoAsync → Flush 를 호출한다.
///      → 결과적으로 actor 의 Flush 가 워커 스레드에서 일어나고, IO 스레드는 push 만 한다.
///   3) <see cref="ThreadContext.TickCount"/>는 JobDispatcher가 매 Run 직전에 갱신해 주므로
///      워커별 주기 작업(여기선 5초마다 alive heartbeat 로그)을 락 없이 ThreadLocal 만으로 구현 가능.
/// </summary>
public class GameWorker : IRunnable
{
    /// <summary>
    /// 외부에서 enqueue 한 명령은 워커 스레드 풀에서 디스패치된다.
    /// 한 명령이 어떤 워커에서 실행될지는 알 수 없지만,
    /// 같은 actor(GameZone/PlayerActor)에 들어간 후속 작업은 actor 큐가 직렬화한다.
    /// </summary>
    public static readonly ConcurrentQueue<Action> InboundCommands = new();

    public static long TotalProcessed => Interlocked.Read(ref _totalProcessed);
    private static long _totalProcessed;

    private static int _workerCounter;
    private readonly int _workerId;
    private long _localProcessed;
    private long _lastHeartbeatTick;
    private const long HeartbeatPeriodMs = 5000;

    public GameWorker()
    {
        _workerId = Interlocked.Increment(ref _workerCounter);
        Console.WriteLine($"[워커 #{_workerId}] 시작 (스레드:{Environment.CurrentManagedThreadId})");
    }

    public bool Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        if (InboundCommands.TryDequeue(out var cmd))
        {
            try
            {
                cmd();
                _localProcessed++;
                Interlocked.Increment(ref _totalProcessed);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[워커 #{_workerId}] 명령 실행 실패: {ex.Message}");
            }
        }
        else
        {
            // 큐가 비어있으면 짧게 양보 (busy-spin 회피)
            Thread.Sleep(1);
        }

        // ThreadLocal Tick 을 사용한 워커별 주기 alive 로그
        long now = ThreadContext.TickCount;
        if (now - _lastHeartbeatTick >= HeartbeatPeriodMs)
        {
            _lastHeartbeatTick = now;
            Console.WriteLine($"  [워커 #{_workerId}] alive tick={now}ms 처리={_localProcessed}건 " +
                              $"(스레드:{Environment.CurrentManagedThreadId})");
        }

        return true;
    }

    public void Dispose()
    {
        Console.WriteLine($"[워커 #{_workerId}] 종료 처리={_localProcessed}건 (스레드:{Environment.CurrentManagedThreadId})");
    }
}
