using System.Collections.Concurrent;
using JobDispatcherNET;

namespace AdvancedMmorpgServer;

/// <summary>
/// 워커 스레드. 전용 OS 스레드에서 ThreadLocal 안정성을 보장하는 자리를 차지하기 위해 존재한다.
///
/// IO 스레드와 워커 스레드 분리 방식:
///   IO 스레드(NetRecv-N) → <see cref="InboundCommands"/> 에 Action 으로 push
///   워커 스레드 → InboundCommands 에서 dequeue → invoke → invoke 안에서 _world.HandleX
///                → world/actor 의 DoAsync → Flush 가 워커 스레드에서 실행
///   결과: actor 의 leader 가 항상 워커 스레드 (IO 스레드가 leader 가 되는 hijack 방지)
/// </summary>
public sealed class GameWorker : IRunnable
{
    /// <summary>
    /// 외부(IO/시뮬레이션 등)에서 enqueue 한 명령을 워커 스레드 풀에서 디스패치.
    /// 같은 actor 에 들어가는 후속 작업은 actor 큐가 직렬화한다.
    /// 같은 클라이언트의 패킷 순서 보장은 세션별 <see cref="Sequencer{T}"/> 가 담당.
    /// </summary>
    public static readonly ConcurrentQueue<Action> InboundCommands = new();

    private static int _counter;
    private readonly int _id;
    private long _localProcessed;

    public static long LocalProcessedTotal => Interlocked.Read(ref _processedAcrossWorkers);
    private static long _processedAcrossWorkers;

    public GameWorker()
    {
        _id = Interlocked.Increment(ref _counter);
        JobLog.Info($"[워커 #{_id}] 시작 (스레드:{Environment.CurrentManagedThreadId})");
    }

    public bool Run(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;

        if (InboundCommands.TryDequeue(out var cmd))
        {
            try
            {
                cmd();
                _localProcessed++;
                Interlocked.Increment(ref _processedAcrossWorkers);
            }
            catch (Exception ex)
            {
                JobLog.Error($"[워커 #{_id}] 명령 실행 실패", ex);
            }
        }
        else
        {
            // 큐가 비어있으면 짧게 양보 (busy-spin 회피)
            Thread.Sleep(1);
        }
        return true;
    }

    public void Dispose()
    {
        JobLog.Info($"[워커 #{_id}] 종료 처리={_localProcessed}건");
    }
}
