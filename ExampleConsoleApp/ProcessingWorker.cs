using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JobDispatcherNET;

namespace ExampleConsoleApp;

/// <summary>
/// Worker implementation for the data processing system
/// </summary>
public class ProcessingWorker : IRunnable
{
    // 워커의 고유 ID를 위한 카운터
    private static int _workerCounter = 0;
    private readonly int _workerId;

    public ProcessingWorker()
    {
        _workerId = Interlocked.Increment(ref _workerCounter);
        Console.WriteLine($"Processing worker {_workerId} created on thread {Environment.CurrentManagedThreadId}");
    }

    public async ValueTask<bool> RunAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        // 타이머 작업 처리
        // 이것은 AsyncExecutable과 Timer 시스템이 타이머 기반 작업을 실행할 수 있게 합니다
        var currentTick = ThreadContext.Timer.GetCurrentTick();
        ThreadContext.TickCount = currentTick;

        // 다른 스레드가 작업을 추가할 시간을 주기 위해 짧은 대기 시간 추가
        // 이는 CPU 사용량을 줄이는 데 도움이 됩니다
        try
        {
            await Task.Delay(Random.Shared.Next(1, 5), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // 작업자 로그 (디버깅 목적으로 때때로 작동 상태를 표시)
        if (Random.Shared.Next(100) < 5) // 5% 확률로 로그 출력
        {
            Console.WriteLine($"Worker {_workerId} on thread {Environment.CurrentManagedThreadId} active at tick {currentTick}");
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        Console.WriteLine($"Processing worker {_workerId} shutting down");
        return ValueTask.CompletedTask;
    }
}