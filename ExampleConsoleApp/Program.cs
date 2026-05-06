// See https://aka.ms/new-console-template for more information
using System;
using System.Threading.Tasks;
using ExampleConsoleApp;
using JobDispatcherNET;

class Program
{
    private const int TestWorkerThreadCount = 4;

    static async Task Main(string[] args)
    {
        Console.WriteLine("Hello, World!");

        Console.WriteLine("JobDispatcher Demo");
        Console.WriteLine("=================");

        // Basic example - simple async execution
        await BasicExampleAsync();

        // Worker thread example - running tasks in parallel
        await WorkerThreadExampleAsync();

        // Advanced example - real-world data processing
        await AdvancedExampleAsync();

        Console.WriteLine("\nAll examples completed");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }




    /// <summary>
    /// AsyncExecutable의 심화 활용 — actor 스타일 패턴을 보여준다.
    /// - 공유 객체(DataProcessor)의 모든 진입점(ProcessItem, GetProcessingStatsAsync)을
    ///   자기 자신의 DoAsync 큐로 통과시킨다. 결과적으로 _processedItems 는 항상 단일 스레드만
    ///   접근하게 되어 클래스 안에 lock 이 단 하나도 없다.
    /// - 객체가 자기 자신에게 DoAsync / DoAsyncAfter 로 우선순위 기반 후속 작업을 디스패치하며
    ///   작업 그래프를 스스로 키워 간다 (즉시 실행 vs 500ms 지연 실행).
    /// - 통계 read 또한 큐를 통과하므로, 결과는 "그 시점까지 큐에 쌓여 있던 모든 작업이
    ///   처리된 후"의 일관된 스냅샷이 된다 (큐 순서에 따른 happens-before 일관성).
    /// 학습 포인트: 모든 read/write 를 직렬 큐로 통과시키면 lock 자체가 사라진다는 actor 모델의 핵심.
    /// </summary>
    static async Task AdvancedExampleAsync()
    {
        Console.WriteLine("Advanced Example - Data Processing:");

        await using var processingService = new ProcessingService();

        Console.WriteLine("Starting data processing (priority-based follow-up demo)...");
        processingService.Start();

        // Run for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        // 통계 read 도 actor 큐를 통과시켜 안전하게 스냅샷을 받는다
        var stats = await processingService.Processor.GetProcessingStatsAsync();

        Console.WriteLine("\nProcessing Statistics:");
        Console.WriteLine($"Total unique items: {stats.Count}");
        Console.WriteLine($"Total processing operations: {stats.Values.Sum()}");

        foreach (var item in stats.OrderByDescending(x => x.Value))
        {
            Console.WriteLine($"{item.Key}: Processed {item.Value} times");
        }
    }



    /// <summary>
    /// IRunnable + JobDispatcher 패턴, 즉 "전용 OS 스레드 위에서 도는 워커"를 보여준다.
    /// - JobDispatcher가 ThreadPool이 아닌 진짜 OS 스레드 4개를 띄우고,
    ///   각 스레드 위에서 TestWorkerThread.Run() 을 반복 호출한다 (스핀 루프는 라이브러리가 관리).
    /// - 전용 스레드이기 때문에 ThreadLocal 상태(예: ThreadContext.TickCount)가 호출 사이에 유지되고,
    ///   yielding은 Task.Delay 가 아니라 Thread.Sleep 으로 한다.
    /// - 워커는 그 안에서 다시 AsyncExecutable 객체들에 DoAsync / DoAsyncAfter 로 일을 던진다 —
    ///   "워커 스레드(생산자) ↔ AsyncExecutable(직렬 소비자)" 결합 형태.
    /// 학습 포인트: 라이브러리가 제공하는 두 메커니즘(IRunnable / AsyncExecutable)이 어떻게 협업하는지.
    /// </summary>
    static async Task WorkerThreadExampleAsync()
    {
        Console.WriteLine("Worker Thread Example:");

        await using var dispatcher = new JobDispatcher<TestWorkerThread>(4); // 4 worker threads

        // Run worker threads
        var dispatcherTask = Task.Run(async () => await dispatcher.RunWorkerThreadsAsync());

        // Let it run for a while
        Console.WriteLine("Running worker threads for 5 seconds...");
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Stop worker threads
        Console.WriteLine("Stopping worker threads...");
        await dispatcher.DisposeAsync();

        Console.WriteLine("All workers have completed");
    }


    /// <summary>
    /// AsyncExecutable의 가장 기본적인 사용법을 보여준다 — JobDispatcher 없이 단독으로 동작.
    /// - DoAsync 첫 호출은 호출자 스레드(여기선 Main)에서 즉시 Flush 를 돌려 큐를 비운다.
    ///   같은 인스턴스에 들어온 후속 DoAsync 는 그 Flush 루프 안에서 함께 처리되므로,
    ///   결과적으로 한 인스턴스의 작업들은 "한 번에 하나씩" 직렬로 실행된다 (객체별 직렬 실행 보장).
    /// - DoAsyncAfter 는 ThreadLocal TimerQueue 가 가진 자체 백그라운드 PeriodicTimer 로 발화하므로,
    ///   호출 스레드가 워커가 아니어도 (Main 스레드여도) 지연 실행이 정상 트리거된다.
    /// 학습 포인트: 락 없이도 객체 상태를 안전하게 갱신할 수 있는 actor 스타일 모델의 핵심 API,
    ///            그리고 별도 디스패처 스레드 없이도 단독으로 쓸 수 있다는 점.
    /// </summary>
    static async Task BasicExampleAsync()
    {
        Console.WriteLine("Basic Example:");

        await using var testObject = new TestObject();

        // Execute methods asynchronously
        testObject.DoAsync(() => testObject.TestFunc0());
        testObject.DoAsync(() => testObject.TestFunc1(5));
        testObject.DoAsync(() => testObject.TestFunc2(25, 10));

        // Scheduled for execution after 500ms
        testObject.DoAsyncAfter(TimeSpan.FromMilliseconds(500), () => testObject.TestFunc1(15));

        // Wait for jobs to complete
        await Task.Delay(1000);

        Console.WriteLine($"Test count: {testObject.GetTestCount()}");
    }
}