using JobDispatcherNET;

namespace ExampleConsoleApp;

/// <summary>
/// AsyncExecutable의 actor 스타일 사용 예제.
///
/// 핵심 학습 포인트: 이 클래스에는 lock이 단 하나도 없다.
/// 외부에서 들어오는 모든 진입점(ProcessItem, GetProcessingStatsAsync)을 자기 자신의
/// DoAsync 큐로 통과시키기 때문이다. AsyncExecutable이 객체별 직렬 실행을 보장하므로,
/// _processedItems 는 항상 단일 스레드만 접근하게 되어 동기화가 자연스럽게 사라진다.
///
/// 만약 GetProcessingStats 를 큐 밖에서(외부 스레드에서) 직접 호출하도록 두면
/// reader / writer race 가 생겨 lock 이 다시 필요해진다.
/// 그래서 read 도 큐를 통과시키고, 결과는 TaskCompletionSource 로 회수한다.
/// </summary>
public class DataProcessor : AsyncExecutable
{
    private readonly Dictionary<string, int> _processedItems = new();

    public void ProcessItem(string itemId, int priority)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] Processing item {itemId} with priority {priority}");

        // 실제 처리 시뮬레이션
        Thread.Sleep(100 * (1 + Random.Shared.Next(5)));

        // _processedItems 는 항상 이 객체의 직렬 큐 위에서만 갱신되므로 lock 이 필요 없다.
        if (_processedItems.TryGetValue(itemId, out var count))
        {
            _processedItems[itemId] = count + 1;
        }
        else
        {
            _processedItems[itemId] = 1;
        }

        // 우선순위에 따라 자기 자신에게 후속 작업을 디스패치 — 같은 큐로 들어가 직렬 실행.
        if (priority > 5)
        {
            DoAsync(() => HighPriorityFollowUp(itemId));
        }
        else if (priority > 2)
        {
            DoAsyncAfter(TimeSpan.FromMilliseconds(500), () => MediumPriorityFollowUp(itemId));
        }
    }

    private void HighPriorityFollowUp(string itemId)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] High priority follow-up for {itemId}");
    }

    private void MediumPriorityFollowUp(string itemId)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] Medium priority follow-up for {itemId}");
    }

    /// <summary>
    /// 통계 스냅샷을 actor 큐를 통과시켜 받는다.
    /// 람다는 이 객체의 직렬 큐 위에서 실행되므로 _processedItems 를 안전하게 복사할 수 있다.
    /// 결과는 TaskCompletionSource 로 회수해 호출자에게 비동기로 돌려준다.
    ///
    /// 부수 효과: 호출 시점 이전에 큐에 쌓여 있던 모든 작업이 먼저 처리된 후의 스냅샷이 된다.
    /// 즉, 큐 순서에 따른 happens-before 일관성이 자동으로 따라온다.
    /// </summary>
    public Task<Dictionary<string, int>> GetProcessingStatsAsync()
    {
        var tcs = new TaskCompletionSource<Dictionary<string, int>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        DoAsync(() => tcs.SetResult(new Dictionary<string, int>(_processedItems)));

        return tcs.Task;
    }
}
