using System.Collections.Concurrent;
using JobDispatcherNET;

namespace ExampleSectorServer;

/// <summary>
/// 섹터 서버용 워커 — 전용 OS 스레드에서 실행 (ThreadLocal 안전).
///
/// 학습 포인트:
///   1) 외부 입력(네트워크 IO 스레드/시뮬레이션)은 <see cref="InboundCommands"/>에
///      Action 형태로 푸시한다. "패킷을 넣는 스레드"와 "패킷을 처리하는 스레드"를 분리하기 위함.
///   2) 워커가 Run() 한 틱마다 큐에서 하나씩 꺼내 invoke 하면, 그 invoke 가 결국
///      <c>GameZone.HandleX → DoAsync</c> 를 호출한다. 결과적으로 actor 의 Flush 가
///      워커 스레드에서 일어나고, 시뮬레이터/IO 스레드는 push 만 한다.
///   3) <see cref="ThreadContext.TickCount"/>는 JobDispatcher 가 매 Run 직전에 갱신해 주므로
///      워커별 주기 alive 로그를 락 없이 ThreadLocal 만으로 구현 가능.
///
/// 주의 — multi-producer race:
///   여러 워커가 InboundCommands 에서 동시에 명령을 꺼내 _zone.DoAsync 를 호출하면,
///   AsyncExecutable 내부의 Increment+TryWrite 가 원자적이지 않아서 채널 안에서 순서가
///   뒤집힐 수 있다. 실제 네트워크 서버는 같은 클라이언트의 패킷 순서 보장을 위해
///   세션별 직렬 drain 패턴을 사용한다.
///   (참고: ExampleMmorpgServer 의 ClientSession.TryScheduleDrain / DrainPackets)
///   본 예제는 단일 시뮬레이터 스레드가 producer 이므로 이 race 가 발생하지 않는다.
/// </summary>
public class SectorWorker : IRunnable
{
    /// <summary>
    /// 외부에서 enqueue 한 명령은 워커 스레드 풀에서 디스패치된다.
    /// 한 명령이 어떤 워커에서 실행될지는 알 수 없지만,
    /// 같은 actor(GameZone/ZoneSector)에 들어간 후속 작업은 actor 큐가 직렬화한다.
    /// </summary>
    public static readonly ConcurrentQueue<Action> InboundCommands = new();

    public static long TotalProcessed => Interlocked.Read(ref _totalProcessed);
    private static long _totalProcessed;

    private static int _counter;
    private readonly int _workerId;
    private long _localProcessed;
    private long _lastHeartbeatTick;
    private const long HeartbeatPeriodMs = 3000;

    public SectorWorker()
    {
        _workerId = Interlocked.Increment(ref _counter);
        Console.WriteLine($"[섹터 워커 #{_workerId}] 시작 (스레드:{Environment.CurrentManagedThreadId})");
    }

    public bool Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        if (InboundCommands.TryDequeue(out var cmd))
        {
            try
            {
                // 이 invoke 안에서 _zone.DoAsync 가 호출됨 →
                // 워커 스레드가 GameZone/ZoneSector 의 Flush 를 실행하게 된다.
                cmd();
                _localProcessed++;
                Interlocked.Increment(ref _totalProcessed);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[섹터 워커 #{_workerId}] 명령 실행 실패: {ex.Message}");
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
            Console.WriteLine($"  [섹터 워커 #{_workerId}] alive tick={now}ms 처리={_localProcessed}건 " +
                              $"(스레드:{Environment.CurrentManagedThreadId})");
        }

        return true;
    }

    public void Dispose()
    {
        Console.WriteLine($"[섹터 워커 #{_workerId}] 종료 처리={_localProcessed}건 " +
                          $"(스레드:{Environment.CurrentManagedThreadId})");
    }
}
