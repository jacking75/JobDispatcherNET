using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JobDispatcherNET;

namespace ExampleChatServer;

/// <summary>
/// 채팅 서버를 위한 워커 스레드 구현
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

    public async ValueTask<bool> RunAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        // 타이머 작업 처리 - JobDispatcher의 스레드 컨텍스트에서 필요한 작업 수행
        var currentTick = ThreadContext.Timer.GetCurrentTick();
        ThreadContext.TickCount = currentTick;

        // 작업이 대기 중인 Room과 AsyncExecutable 객체들을 위한 처리
        // 이 처리로 AsyncExecutable 객체들의 작업이 타이머 큐 및 작업 큐를 통해 실행됨

        try
        {
            // 다른 스레드에게 실행 기회를 주기 위한 짧은 지연
            await Task.Delay(1, cancellationToken);

            // 주기적으로 상태 로그 출력 (낮은 빈도로)
            if (Random.Shared.Next(1000) < 2) // 0.2% 확률
            {
                Console.WriteLine($"워커 {_workerId} 활성 상태 (스레드 ID: {Environment.CurrentManagedThreadId})");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        Console.WriteLine($"채팅 워커 {_workerId} 종료 (스레드 ID: {Environment.CurrentManagedThreadId})");
        return ValueTask.CompletedTask;
    }
}

/*
ChatWorker 클래스는 다음과 같은 역할을 수행합니다:

워커 관리:

고유 ID를 각 워커에 할당하여 로깅 및 디버깅을 용이하게 합니다.
생성과 종료 시점에 로그를 출력하여 워커의 수명주기를 추적합니다.
주요 기능:

RunAsync 메서드는 주기적으로 호출되며 타이머 작업을 처리합니다.
ThreadContext.Timer.GetCurrentTick()을 호출하여 현재 틱을 업데이트하고, 이 값을 ThreadContext.TickCount에 저장합니다.
이 과정을 통해 AsyncExecutable이 예약한 지연 작업(DoAsyncAfter)이 적절한 시간에 처리됩니다.
스레드 관리:

워커 스레드가 CPU를 독점하지 않도록 짧은 대기 시간(1ms)을 포함합니다.
주기적으로 낮은 빈도(0.2%)로 로그를 출력하여 워커가 활성 상태임을 확인할 수 있게 합니다.
취소 처리:

cancellationToken을 확인하여 서버 종료 시 워커도 안전하게 종료됩니다.
취소 요청이 있으면 false를 반환하여 워커 루프를 종료합니다.
이 구현은 JobDispatcherNET의 워커 스레드 풀의 일부로 동작하며, 주로 타이머와 스케줄링된 작업의 실행을 담당합니다. Room 객체와 ChatServer가 AsyncExecutable을 상속받아 등록한 작업들이 이 워커 스레드 풀에서 실행됩니다.
 */ 