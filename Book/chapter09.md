# Chapter 09: ExampleConsoleApp — 기본기 익히기

## 9.1 프로젝트 구조

```
ExampleConsoleApp/
├── Program.cs              ← 세 가지 예제를 실행하는 진입점
├── TestObject.cs           ← 가장 기본적인 AsyncExecutable 구현
├── TestWorkerThread.cs     ← IRunnable 구현
├── DataProcessor.cs        ← Actor 스타일 심화 예제
└── ProcessingService.cs    ← DataProcessor를 래핑하는 서비스
```

이 프로젝트는 세 가지 예제를 순서대로 실행합니다:

```
main() 실행
    │
    ├── BasicExampleAsync()       ← 예제 1: DoAsync/DoAsyncAfter 기본
    │
    ├── WorkerThreadExampleAsync() ← 예제 2: IRunnable + JobDispatcher
    │
    └── AdvancedExampleAsync()    ← 예제 3: Actor 스타일 DataProcessor
```

---

## 9.2 TestObject — 가장 단순한 Actor

```csharp
public class TestObject : AsyncExecutable
{
    private int _testCount;  // lock 없이 안전하게 쓸 수 있는 필드

    public void TestFunc0()
    {
        Interlocked.Increment(ref _testCount);
    }

    public void TestFunc1(int b)
    {
        Interlocked.Add(ref _testCount, b);
    }

    public void TestFunc2(double a, int b)
    {
        Interlocked.Add(ref _testCount, b);

        // Actor 내부에서 자기 자신에게 다시 DoAsync!
        // → 새 작업이 현재 큐에 추가됨 (직렬 실행 보장)
        if (a < 50.0)
        {
            DoAsync(() => TestFunc1(b));
        }
    }

    public void TestFuncForTimer(int b)
    {
        // 50% 확률로 재귀적 타이머 예약
        if (Random.Shared.Next(2) == 0)
        {
            DoAsyncAfter(TimeSpan.FromSeconds(1), () => TestFuncForTimer(-b));
        }
    }

    public int GetTestCount() => _testCount;
}
```

> `TestFuncForTimer` 는 "자기 자신을 다시 예약"하는 옛 패턴을 일부러 남겨 둔 예제입니다.
> 실제 주기 작업에는 `DoAsyncEvery` 를 쓰세요 (5.5) — 예외에 강하고 취소가 쉽습니다.
> `DoAsyncAfter` 는 이제 `ITimerHandle` 을 반환하므로, 위처럼 반환값을 버리면 나중에
> 취소할 방법이 없다는 점도 기억해 두세요.

잠깐! `_testCount`에 `Interlocked.Add`를 쓰고 있습니다. Actor 내부에서 실행되는데 왜 Interlocked을 쓸까요?

```
설명:
─────────────────────────────────────────────────────────
TestObject는 AsyncExecutable을 상속하므로 내부 메서드들은
항상 직렬 실행됩니다.

하지만 GetTestCount()는 외부 스레드에서 직접 호출될 수 있습니다!
  TestObject._testCount를 읽기 위해 직접 호출하면
  → 읽기와 쓰기가 동시에 발생 가능

완전한 Actor 모델이라면 GetTestCount()도 큐를 통해야 합니다.
이 예제는 간단함을 위해 Interlocked를 사용한 것입니다.

실제 서버에서는 GetSnapshot() 패턴을 쓰는 것이 더 안전합니다!
─────────────────────────────────────────────────────────
```

---

## 9.3 예제 1: BasicExampleAsync

```csharp
static async Task BasicExampleAsync()
{
    Console.WriteLine("Basic Example:");

    await using var testObject = new TestObject();

    // ① 즉시 실행 — 큐에 넣고 이 스레드(Main)에서 바로 Flush!
    testObject.DoAsync(() => testObject.TestFunc0());
    testObject.DoAsync(() => testObject.TestFunc1(5));
    testObject.DoAsync(() => testObject.TestFunc2(25, 10));

    // ② 500ms 후 실행 — 시스템의 타이머 스레드에 예약
    testObject.DoAsyncAfter(TimeSpan.FromMilliseconds(500),
        () => testObject.TestFunc1(15));

    // ③ 500ms + 처리 시간을 기다림
    await Task.Delay(1000);

    Console.WriteLine($"Test count: {testObject.GetTestCount()}");
}
```

실행 순서 추적:

```
DoAsync(TestFunc0)
    → CAS 0→1  — 내가 첫 번째, 즉 leader!
    → 큐에 넣기
    → Flush 시작 (Main 스레드에서)
        TestFunc0 실행  (_testCount += 1)          ← +1
        Decrement(count=0)  — 큐 비었음 → return

DoAsync(TestFunc1(5))
    → CAS 0→1  — 또 첫 번째!
    → Flush 시작
        TestFunc1(5) 실행  (_testCount += 5)       ← +5
        Decrement(count=0)  — return

DoAsync(TestFunc2(25, 10))
    → CAS 0→1
    → Flush 시작
        TestFunc2(25, 10) 실행
            Interlocked.Add(10)                    ← +10
            a(25) < 50.0 → DoAsync(TestFunc1(10))
                → CAS 1→2  ← 이미 leader 가 있으므로 큐에만 넣기
        Decrement(count=1)  — 아직 작업 있음
        TestFunc1(10) 실행  (_testCount += 10)     ← +10
        Decrement(count=0)  — return

DoAsyncAfter(500ms, TestFunc1(15))
    → 타이머 스레드(JobTimer-default)의 PriorityQueue 에 예약
    → 500ms 후 만료 → testObject 의 큐로 투입
    → 이 프로세스에는 워커가 없다 → 타이머 스레드가 그 자리에서 Flush
       (Warn 로그 1회: "JobSystem 'default' has no worker threads ...")
        TestFunc1(15) 실행  (_testCount += 15)     ← +15

최종: _testCount = 1 + 5 + 10 + 10 + 15 = 41
출력:  Test count: 41
```

### ⚠️ v2.0 에서는 여기서 26 이 나왔습니다

이 예제는 P0-3 결함의 재현 코드이기도 합니다.

```
v2.0 출력:  Test count: 26      ( = 1 + 5 + 10 + 10 )
v2.1 출력:  Test count: 41      ( = 26 + 15 )
```

만료된 타이머 작업이 워커만 드레인하는 큐로 들어갔는데, 이 예제에는 `JobDispatcher` 가
없습니다. 그래서 `TestFunc1(15)` 이 **영원히 실행되지 않았고**, 경고도 없었습니다.
"디스패처 없이도 지연 실행이 정상 트리거된다"는 예전 설명은 v1 시절의 동작이었고,
v2.0 에서는 사실이 아니었습니다.

지금은 워커가 없으면 타이머 스레드가 직접 flush 합니다. 다시 "디스패처 없이도 동작"하지만,
**그 경우 콜백이 워커가 아니라 타이머 스레드에서 실행된다**는 조건이 붙습니다. 그래서 첫
발화 때 경고를 한 번 남깁니다. 실제 서버라면 `JobDispatcher` 를 띄우세요.

---

## 9.4 TestWorkerThread — IRunnable 구현

```csharp
public class TestWorkerThread : IRunnable
{
    private readonly List<TestObject> _testObjects = new();
    private const int TestObjectCount = 10;

    public TestWorkerThread()
    {
        // 이 워커가 소유할 10개의 TestObject 생성
        for (int i = 0; i < TestObjectCount; i++)
        {
            _testObjects.Add(new TestObject());
        }
    }

    public bool Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        int after = Random.Shared.Next(2000);  // 0~2000ms

        if (after > 1000)  // 절반 확률
        {
            // 랜덤 TestObject들에 작업 보내기
            int i1 = Random.Shared.Next(TestObjectCount);
            int i2 = Random.Shared.Next(TestObjectCount);
            int i3 = Random.Shared.Next(TestObjectCount);
            int i4 = Random.Shared.Next(TestObjectCount);

            _testObjects[i1].DoAsync(() => _testObjects[i1].TestFunc0());
            _testObjects[i2].DoAsync(() =>
                _testObjects[i2].TestFunc2(Random.Shared.Next(100), 2));
            _testObjects[i3].DoAsync(() => _testObjects[i3].TestFunc1(1));

            // 지연 실행도 테스트
            _testObjects[i4].DoAsyncAfter(
                TimeSpan.FromMilliseconds(after),
                () => _testObjects[i4].TestFuncForTimer(after));
        }

        // 카운트가 5000을 넘으면 강제 종료 (조기 종료 테스트)
        if (_testObjects[Random.Shared.Next(TestObjectCount)].GetTestCount() > 5000)
        {
            Console.WriteLine($"Thread {Environment.CurrentManagedThreadId} end by force");
            return false;  // ← false 반환으로 이 워커 종료
        }

        Thread.Sleep(1);  // CPU 양보
        return true;
    }

    public void Dispose()
    {
        // TestObject들을 비동기로 Dispose (큐 drain 대기)
        foreach (var testObject in _testObjects)
        {
            testObject.DisposeAsync().AsTask().Wait();
        }
    }
}
```

---

## 9.5 예제 2: WorkerThreadExampleAsync

```csharp
static async Task WorkerThreadExampleAsync()
{
    Console.WriteLine("Worker Thread Example:");

    // 4개의 전용 OS 스레드로 TestWorkerThread 실행
    await using var dispatcher = new JobDispatcher<TestWorkerThread>(4);

    // 스레드 시작 + 모든 완료를 기다리는 Task 반환
    var dispatcherTask = Task.Run(async () =>
        await dispatcher.RunWorkerThreadsAsync());

    // 5초 동안 실행
    Console.WriteLine("Running worker threads for 5 seconds...");
    await Task.Delay(TimeSpan.FromSeconds(5));

    // 종료 신호 + 스레드 Join
    Console.WriteLine("Stopping worker threads...");
    await dispatcher.DisposeAsync();

    Console.WriteLine("All workers have completed");
}
```

4개 워커 스레드의 동작:

```
JobWorker-0 (OS Thread A):
    TestWorkerThread-0 생성
    Run() 반복:
      → TestObject 0~9에 랜덤으로 DoAsync/DoAsyncAfter
      → 카운트 체크 → 5000 초과하면 false 반환
      → Dispose: TestObject들 drain 대기

JobWorker-1 (OS Thread B):
    TestWorkerThread-1 생성
    Run() 반복: (독립적으로!)

JobWorker-2 (OS Thread C): ...
JobWorker-3 (OS Thread D): ...

★ 같은 TestObject에 여러 워커가 DoAsync를 보낼 수 있음
  → 입장 CAS 가 leader 를 단 하나로 정한다 → 동시 실행 없음
  → 어떤 워커가 Flush하든 "한 번에 하나씩"이 보장된다
    (단, 서로 다른 producer 중 누가 먼저 넣는지는 보장되지 않는다 — 7장 Sequencer)
```

---

## 9.6 DataProcessor — Actor 스타일 심화

```csharp
public class DataProcessor : AsyncExecutable
{
    // lock이 단 하나도 없는 컬렉션!
    private readonly Dictionary<string, int> _processedItems = new();

    // 외부 진입점: 큐에 넣기만
    public void ProcessItem(string itemId, int priority)
    {
        Console.WriteLine($"[Thread {Environment.CurrentManagedThreadId}] " +
                          $"Processing {itemId} priority={priority}");

        Thread.Sleep(100 * (1 + Random.Shared.Next(5)));  // 처리 시뮬레이션

        // ★ lock 없이 안전하게 딕셔너리 갱신!
        if (_processedItems.TryGetValue(itemId, out var count))
            _processedItems[itemId] = count + 1;
        else
            _processedItems[itemId] = 1;

        // 우선순위에 따라 자기 자신에게 후속 작업 예약
        if (priority > 5)
            DoAsync(() => HighPriorityFollowUp(itemId));    // 즉시
        else if (priority > 2)
            DoAsyncAfter(TimeSpan.FromMilliseconds(500),
                () => MediumPriorityFollowUp(itemId));      // 500ms 후
    }

    private void HighPriorityFollowUp(string itemId) { ... }
    private void MediumPriorityFollowUp(string itemId) { ... }

    // ★ 중요: 읽기도 큐를 통과!
    public Task<Dictionary<string, int>> GetProcessingStatsAsync()
    {
        var tcs = new TaskCompletionSource<Dictionary<string, int>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        DoAsync(() => tcs.SetResult(new Dictionary<string, int>(_processedItems)));

        return tcs.Task;
    }
}
```

> **지금은 `Ask` 한 줄이면 됩니다.**
> ```csharp
> public Task<Dictionary<string, int>> GetProcessingStatsAsync()
>     => Ask(() => new Dictionary<string, int>(_processedItems));
> ```
> `Ask` 는 위와 똑같이 큐를 통과시키면서, 핸들러가 예외를 던지면 Task 를 실패시키고,
> 큐에 들어가지 못하면 `JobRejectedException` 으로 실패시킵니다. 손으로 만든
> `TaskCompletionSource` 는 거부됐을 때 영원히 완료되지 않는다는 함정이 있습니다.

왜 GetProcessingStatsAsync에서 큐를 사용하나요?

```
잘못된 방법 (직접 읽기):
──────────────────────────────────────────────────────────
// 이렇게 하면:
public Dictionary<string, int> GetStats()
    => new Dictionary<string, int>(_processedItems);

외부 스레드에서 호출:
  Thread-External: new Dict(_processedItems)  ← 복사 시작
  DataProcessor 큐: ProcessItem() 실행 중
    → _processedItems 수정!
  Thread-External: 복사 진행 중 ← 중간 상태 읽기!

올바른 방법 (큐를 통해 읽기):
──────────────────────────────────────────────────────────
DoAsync(() => tcs.SetResult(new Dict(_processedItems)))

→ ProcessItem이 모두 처리된 후에 읽기 실행
→ 일관된 스냅샷 보장!
→ 큐 순서에 따른 happens-before 일관성
```

---

## 9.7 예제 3: AdvancedExampleAsync

```csharp
static async Task AdvancedExampleAsync()
{
    await using var processingService = new ProcessingService();

    processingService.Start();

    // 5초 동안 실행
    await Task.Delay(TimeSpan.FromSeconds(5));

    // 통계 조회 — 큐를 통과하므로 안전!
    var stats = await processingService.Processor.GetProcessingStatsAsync();

    Console.WriteLine("\nProcessing Statistics:");
    Console.WriteLine($"Total unique items: {stats.Count}");
    Console.WriteLine($"Total operations: {stats.Values.Sum()}");

    foreach (var item in stats.OrderByDescending(x => x.Value))
        Console.WriteLine($"{item.Key}: {item.Value}회");
}
```

ProcessingService의 역할:

```csharp
public class ProcessingService : IAsyncDisposable
{
    private readonly DataProcessor _processor = new();

    public void Start()
    {
        // 별도 Task에서 작업을 DataProcessor에 보냄
        _processingTask = Task.Run(async () =>
        {
            for (int i = 0; i < _itemCount && !token.IsCancellationRequested; i++)
            {
                string item = _items[Random.Shared.Next(_items.Count)];
                int priority = Random.Shared.Next(1, 10);

                // ★ DoAsync로 DataProcessor 큐에 넣기
                _processor.DoAsync(() => _processor.ProcessItem(item, priority));

                await Task.Delay(Random.Shared.Next(10, 50), token);
            }
        }, token);
    }
}
```

---

## 9.8 우선순위 후속 작업 다이어그램

```mermaid
sequenceDiagram
    participant PS as ProcessingService
    participant DP as DataProcessor(Actor)
    participant H as HighPriFollowUp
    participant M as MedPriFollowUp

    PS->>DP: DoAsync(ProcessItem("A", priority=8))
    Note over DP: ProcessItem 실행
    DP->>DP: DoAsync(HighPriorityFollowUp("A"))
    Note over DP: HighPriorityFollowUp 실행

    PS->>DP: DoAsync(ProcessItem("B", priority=3))
    Note over DP: ProcessItem 실행
    DP->>DP: DoAsyncAfter(500ms, MedPriFollowUp("B"))
    Note over DP: (500ms 후)
    DP->>DP: MedPriorityFollowUp("B") 실행
```

---

## 9.9 핵심 학습 포인트 정리

```
BasicExample에서:
✓ DoAsync는 즉시 Flush를 돌려 같은 스레드(Main)에서 처리
✓ DoAsyncAfter는 시스템의 타이머 스레드에 예약된다
✓ 워커가 없으면 타이머 콜백이 타이머 스레드에서 실행된다 (경고 1회)
✓ Actor 내부에서 DoAsync 호출 시 기존 Flush에 합류
✓ 기대 출력은 41 — 26 이 나온다면 v2.0 이하다 (P0-3)

WorkerThreadExample에서:
✓ IRunnable + JobDispatcher<T> = 전용 OS 스레드
✓ 여러 워커가 같은 Actor에 DoAsync → 입장 CAS 가 직렬 실행을 보장
✓ Run()이 false 반환 → 해당 워커만 종료 (다른 워커 유지)
✓ 자체 루프가 필요 없다면 비제네릭 JobDispatcher 가 더 낫다 (6.2)

AdvancedExample에서:
✓ 읽기도 큐를 통과 → 일관된 스냅샷
✓ 결과 회수는 손수 만든 TaskCompletionSource 대신 Ask 로
✓ 자기 자신에게 후속 작업 예약 가능
```

---

*[← Chapter 08](./chapter08.md) | [→ Chapter 10: ExampleChatServer](./chapter10.md)*
