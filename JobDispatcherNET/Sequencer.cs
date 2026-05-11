using System.Collections.Concurrent;

namespace JobDispatcherNET;

/// <summary>
/// 같은 source 의 항목들을 도착 순서대로 한 번에 한 워커만 처리하도록 보장하는 헬퍼.
///
/// 왜 필요한가:
///   AsyncExecutable.DoTask 의 Increment+TryWrite 가 비원자적이라
///   여러 producer 가 같은 actor 에 동시에 push 하면 채널 안에서 순서가 뒤집힐 수 있다.
///   예: 네트워크 IO 가 EnterZone 패킷을 push, 곧이어 Move 를 push 했는데
///       두 워커가 각자 dequeue 해서 _zone.DoAsync 를 거의 동시에 호출하면
///       채널 안에서 Move 가 EnterZone 보다 먼저 들어갈 수 있음.
///
/// 사용 패턴 (세션):
///   1) 네트워크 RecvIO 스레드: <see cref="Enqueue"/> 로 패킷을 push.
///   2) 이 클래스가 CAS 로 단일 drainer 권한을 얻은 호출자만 scheduleDrain 콜백을 1회 호출.
///   3) scheduleDrain 콜백은 워커 큐(예: ConcurrentQueue&lt;Action&gt;)에 drain 명령 enqueue.
///   4) 워커가 dequeue → <see cref="Drain"/> 실행 → 항목들을 handler 로 순서대로 처리.
///   5) Drain 완료 후 큐에 남은 항목이 있으면 다시 scheduleDrain.
///
/// 보장:
///   - 같은 Sequencer 인스턴스의 항목은 한 시점에 한 스레드만 처리.
///   - 처리 순서는 Enqueue 순서.
///   - 처리 중 새 Enqueue 가 들어와도 race 없음 (CAS 해제 후 재스케줄).
/// </summary>
public sealed class Sequencer<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly Action<T> _handler;
    private readonly Action<Action> _scheduleDrain;
    private readonly Action<Exception>? _onError;
    private int _drainScheduled;
    private int _stopped;

    /// <param name="handler">각 항목을 처리하는 핸들러. 워커 스레드에서 직렬로 호출됨.</param>
    /// <param name="scheduleDrain">drain 작업을 워커 큐(예: GameWorker.InboundCommands)에 enqueue 하는 콜백.</param>
    /// <param name="onError">handler 가 던진 예외 처리 콜백. null 이면 <see cref="JobLog"/> 로 출력.</param>
    public Sequencer(Action<T> handler, Action<Action> scheduleDrain, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(scheduleDrain);
        _handler = handler;
        _scheduleDrain = scheduleDrain;
        _onError = onError;
    }

    /// <summary>현재 큐에 있는 항목 수 (메트릭용).</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// 항목을 enqueue. 호출 스레드는 producer (예: IO 스레드). 즉시 반환.
    /// </summary>
    public void Enqueue(T item)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        _queue.Enqueue(item);
        TryScheduleDrain();
    }

    private void TryScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
            return;

        try
        {
            _scheduleDrain(Drain);
        }
        catch
        {
            // schedule 실패 시 락 해제해서 다음 Enqueue 가 재시도 가능하게 한다.
            Volatile.Write(ref _drainScheduled, 0);
            throw;
        }
    }

    private void Drain()
    {
        try
        {
            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    _handler(item);
                }
                catch (Exception ex)
                {
                    if (_onError is not null) _onError(ex);
                    else JobLog.Error("Sequencer handler error", ex);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _drainScheduled, 0);

            // 락 해제와 producer 의 Enqueue 사이 race 처리.
            if (!_queue.IsEmpty && Volatile.Read(ref _stopped) == 0)
                TryScheduleDrain();
        }
    }

    /// <summary>
    /// 더 이상 새 항목을 받지 않도록 표시. 현재 진행 중인 drain 은 완료까지 실행되며,
    /// 큐에 남은 항목도 모두 처리된다. 셧다운 시 호출.
    /// </summary>
    public void Stop()
    {
        Volatile.Write(ref _stopped, 1);
    }
}
