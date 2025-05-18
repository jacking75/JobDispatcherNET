# JobDispatcherNET


## 주요 이점 및 기능
1. **최신 C# 기능**:
   - 효율적인 비동기 작업을 위한 `ValueTask` 사용
   - 고성능 메시지 전달을 위한 `System.Threading.Channels` 활용
   - 적절한 리소스 정리를 위한 `IAsyncDisposable` 구현
   - 불변 데이터 타입을 위한 레코드(records) 사용
   - 향상된 타입 안전성을 위한 널 허용 참조 타입(nullable reference types) 사용

2. **C++ 원본과 비교**:
   - 수동 메모리 관리를 .NET 가비지 컬렉션으로 대체
   - 템플릿 메타프로그래밍 대신 델리게이트 사용
   - async/await 패턴과 통합
   - 예외 처리 및 안전성 제공

3. **스레드 관리**:
   - 스레드별 데이터를 위한 ThreadLocal 저장소
   - 효율적인 작업자 스레드 관리
   - 적절한 취소 지원

4. **타이밍 및 스케줄링**:
   - 밀리초 단위의 정밀한 작업 스케줄링
   - 우선순위 기반의 예약된 작업 실행
   - 예약된 작업의 자동 정리  
   
   
## JobDispatcherNET 성능 개선 방안

### 1. 객체 풀링 구현
현재 C# 구현에서는 원본 C++ 코드의 ObjectPool이 구현되어 있지 않습니다. 객체 풀링을 도입하면 GC 부담을 크게 줄일 수 있다.

```csharp
public class ObjectPool<T> where T : class, new()
{
    private readonly ConcurrentBag<T> _objects;
    private readonly Func<T> _objectGenerator;
    
    public ObjectPool(Func<T> objectGenerator)
    {
        _objectGenerator = objectGenerator ?? throw new ArgumentNullException(nameof(objectGenerator));
        _objects = new ConcurrentBag<T>();
    }
    
    public T Get() => _objects.TryTake(out T? item) ? item : _objectGenerator();
    
    public void Return(T item) => _objects.Add(item);
}

// Job 객체를 위한 풀링 구현 예
public sealed class JobPool
{
    private static readonly ObjectPool<Job> _jobPool = new(() => new Job(null!));
    
    public static Job Get(Action action)
    {
        var job = _jobPool.Get();
        job.Reset(action);
        return job;
    }
    
    public static void Return(Job job)
    {
        job.Reset(null!); // 참조 정리
        _jobPool.Return(job);
    }
}
```
  
  
### 2. Lock-Free 알고리즘 도입
높은 경합 지점에서는 lock 대신 lock-free 자료구조를 사용하여 성능을 개선할 수 있다.

```csharp
// 예: 원자적 연산을 활용한 카운터
private long _taskCount;

public void IncrementTaskCount()
{
    Interlocked.Increment(ref _taskCount);
}

// 예: Channel 대신 커스텀 lock-free 큐 구현
public class LockFreeQueue<T>
{
    private class Node
    {
        public T Value;
        public Node Next;
    }
    
    private Node _head;
    private Node _tail;
    
    public LockFreeQueue()
    {
        _head = _tail = new Node();
    }
    
    public void Enqueue(T item)
    {
        var node = new Node { Value = item };
        Node oldTail;
        
        do
        {
            oldTail = _tail;
        } 
        while (Interlocked.CompareExchange(ref oldTail.Next, node, null) != null);
        
        Interlocked.CompareExchange(ref _tail, node, oldTail);
    }
    
    // Dequeue 메서드 구현 생략...
}
```
  
  
### 3. 값 타입(Value Types)과 Span 활용
참조 타입 대신 값 타입과 Span을 활용하여 힙 할당과 GC 부담을 줄일 수 있다.

```csharp
// 기존 참조 타입 메시지 클래스 대신 레코드 구조체 사용
public readonly record struct ChatMessageStruct(
    Guid Id,
    MessageType Type,
    string Sender,
    string? Recipient,
    string? RoomId,
    ReadOnlyMemory<char> Content,
    DateTimeOffset Timestamp);

// 메모리 버퍼 재사용 예시
public class MessageProcessor
{
    private readonly byte[] _buffer = new byte[4096];
    
    public void ProcessMessage(ReadOnlySpan<byte> messageData)
    {
        // Span을 활용한 버퍼 처리
        messageData.CopyTo(_buffer);
        // 처리 로직...
    }
}
```
  
  
### 4. 작업 배치 처리(Batching)
작은 작업들을 그룹으로 묶어 배치 처리하면 오버헤드를 줄일 수 있다.

```csharp
public class BatchProcessor : AsyncExecutable
{
    private readonly List<Action> _pendingActions = [];
    private readonly SemaphoreSlim _batchSemaphore = new(1, 1);
    private readonly int _batchSize = 100;
    private readonly int _maxBatchDelayMs = 5;
    private bool _batchScheduled = false;
    
    public void QueueAction(Action action)
    {
        DoAsync(() => {
            _pendingActions.Add(action);
            
            if (_pendingActions.Count >= _batchSize && !_batchScheduled)
            {
                _batchScheduled = true;
                ScheduleBatch();
            }
            else if (_pendingActions.Count == 1 && !_batchScheduled)
            {
                _batchScheduled = true;
                DoAsyncAfter(TimeSpan.FromMilliseconds(_maxBatchDelayMs), ScheduleBatch);
            }
        });
    }
    
    private void ScheduleBatch()
    {
        DoAsync(async () => {
            await _batchSemaphore.WaitAsync();
            try
            {
                var actionsToProcess = _pendingActions.ToList();
                _pendingActions.Clear();
                _batchScheduled = false;
                
                foreach (var action in actionsToProcess)
                {
                    action();
                }
            }
            finally
            {
                _batchSemaphore.Release();
            }
        });
    }
}
```
  
  
### 5. 워커 스레드 최적화
워커 스레드 수를 동적으로 조정하고 스레드 선호도(Thread Affinity)를 설정하여 CPU 캐시 효율을 높일 수 있다.

```csharp
public class AdaptiveJobDispatcher<T> : IAsyncDisposable where T : IRunnable, new()
{
    private readonly int _minWorkers;
    private readonly int _maxWorkers;
    private readonly List<Task> _workerTasks = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly PeriodicTimer _adjustmentTimer;
    private int _currentWorkerCount;
    private readonly object _adjustmentLock = new();
    
    public AdaptiveJobDispatcher(int minWorkers, int maxWorkers)
    {
        _minWorkers = minWorkers;
        _maxWorkers = maxWorkers;
        _currentWorkerCount = minWorkers;
        _adjustmentTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        
        // 5초마다 워커 수 조정
        _ = AdjustWorkersPeriodicAsync();
    }
    
    public async Task RunWorkerThreadsAsync()
    {
        for (int i = 0; i < _minWorkers; i++)
        {
            StartNewWorker();
        }
        
        await Task.WhenAll(_workerTasks);
    }
    
    private void StartNewWorker()
    {
        _workerTasks.Add(Task.Factory.StartNew(
            async () => await RunWorkerAsync(),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default));
        
        Interlocked.Increment(ref _currentWorkerCount);
    }
    
    private async Task RunWorkerAsync()
    {
        // 현재 스레드의 CPU 선호도 설정 (윈도우 환경)
        if (OperatingSystem.IsWindows())
        {
            var threadId = Environment.CurrentManagedThreadId;
            var processorId = threadId % Environment.ProcessorCount;
            
            // 실제 구현에서는 Platform Invoke(P/Invoke)를 사용해 
            // SetThreadAffinityMask 함수를 호출해야 합니다.
            Console.WriteLine($"스레드 {threadId}를 CPU {processorId}에 할당");
        }
        
        await using var runner = new T();
        
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                bool shouldContinue = await runner.RunAsync(_cts.Token);
                if (!shouldContinue)
                    break;
                
                await Task.Delay(1, _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
        {
            // 정상적인 취소
        }
    }
    
    private async Task AdjustWorkersPeriodicAsync()
    {
        while (await _adjustmentTimer.WaitForNextTickAsync(_cts.Token))
        {
            lock (_adjustmentLock)
            {
                // 시스템 부하에 따라 워커 수 조정
                double cpuUsage = GetCpuUsage(); // 실제로는 CPU 사용률 측정 필요
                
                if (cpuUsage > 80 && _currentWorkerCount < _maxWorkers)
                {
                    StartNewWorker();
                    Console.WriteLine($"워커 수 증가: {_currentWorkerCount}");
                }
                else if (cpuUsage < 30 && _currentWorkerCount > _minWorkers)
                {
                    // 워커 수 감소 로직 (복잡하므로 생략)
                }
            }
        }
    }
    
    private double GetCpuUsage()
    {
        // 실제 CPU 사용률 측정 로직
        return 50.0; // 예시 값
    }
    
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        
        try
        {
            if (_workerTasks.Count > 0)
                await Task.WhenAll(_workerTasks);
        }
        catch (OperationCanceledException)
        {
            // 취소 예외 무시
        }
        
        _cts.Dispose();
        await _adjustmentTimer.DisposeAsync();
    }
}
```
  
  
### 6. 작업 우선순위 시스템 도입
작업 처리에 우선순위를 부여하여 중요한 작업이 먼저 처리되도록 할 수 있다.

```csharp
public enum JobPriority { Low, Normal, High, Critical }

public class PriorityAsyncExecutable : AsyncExecutable
{
    private readonly PriorityQueue<JobEntry, int> _jobQueue = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    
    public void DoAsyncWithPriority(JobPriority priority, Action action)
    {
        int priorityValue = (int)priority;
        var job = new Job(action);
        
        DoAsync(async () => {
            await _queueLock.WaitAsync();
            try
            {
                _jobQueue.Enqueue(job, -priorityValue); // 음수로 저장해 높은 우선순위가 먼저 오게 함
                ProcessQueue();
            }
            finally
            {
                _queueLock.Release();
            }
        });
    }
    
    private void ProcessQueue()
    {
        DoAsync(async () => {
            await _queueLock.WaitAsync();
            try
            {
                while (_jobQueue.TryDequeue(out var job, out _))
                {
                    job.Execute();
                }
            }
            finally
            {
                _queueLock.Release();
            }
        });
    }
}
```
  
  
### 7. JIT 최적화와 AOT 컴파일 활용
성능이 중요한 부분은 런타임 최적화를 위한 특성을 사용할 수 있다.

```csharp
using System.Runtime.CompilerServices;

public class OptimizedExecutor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteFast(Action action)
    {
        // 중요한 성능 로직
        action();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ProcessBatch(Span<Action> actions)
    {
        foreach (var action in actions)
        {
            action();
        }
    }
}
```
  
  
### 8. 프로파일링과 모니터링 기능 추가
성능 병목을 식별하고 모니터링하기 위한 기능을 추가할 수 있다.

```csharp
public class PerformanceMonitor
{
    private readonly ConcurrentDictionary<string, Metrics> _metricsTable = new();
    
    public IDisposable TrackOperation(string operationName)
    {
        return new OperationTracker(this, operationName);
    }
    
    private class OperationTracker : IDisposable
    {
        private readonly PerformanceMonitor _monitor;
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch = new();
        
        public OperationTracker(PerformanceMonitor monitor, string operationName)
        {
            _monitor = monitor;
            _operationName = operationName;
            _stopwatch.Start();
        }
        
        public void Dispose()
        {
            _stopwatch.Stop();
            _monitor._metricsTable.AddOrUpdate(
                _operationName,
                new Metrics { Count = 1, TotalTime = _stopwatch.ElapsedMilliseconds },
                (_, existing) => {
                    existing.Count++;
                    existing.TotalTime += _stopwatch.ElapsedMilliseconds;
                    return existing;
                });
        }
    }
    
    public class Metrics
    {
        public long Count { get; set; }
        public long TotalTime { get; set; }
        public double AverageTime => Count > 0 ? (double)TotalTime / Count : 0;
    }
    
    public void PrintReport()
    {
        Console.WriteLine("=== 성능 보고서 ===");
        foreach (var entry in _metricsTable.OrderByDescending(x => x.Value.TotalTime))
        {
            Console.WriteLine($"{entry.Key}: {entry.Value.Count}회 호출, 평균 {entry.Value.AverageTime:F2}ms, 총 {entry.Value.TotalTime}ms");
        }
    }
}
```
  
  
## 9. 메모리 지역성 최적화
데이터 구조를 캐시 친화적으로 변경하여 메모리 접근 성능을 향상시킬 수 있다.

```csharp
// 캐시 라인 크기에 맞춘 구조체 패딩
[StructLayout(LayoutKind.Explicit, Size = 128)] // 일반적인 캐시 라인 크기
public struct CacheAlignedCounter
{
    [FieldOffset(0)]
    public long Value;
    
    // 나머지 공간은 패딩으로 사용됨
}

// 정렬된 메모리 접근을 위한 매트릭스 구현 예시
public class CacheOptimizedMatrix
{
    private readonly float[] _data;
    private readonly int _rows;
    private readonly int _cols;
    
    public CacheOptimizedMatrix(int rows, int cols)
    {
        _rows = rows;
        _cols = cols;
        _data = new float[rows * cols];
    }
    
    // 행 우선 접근 (캐시 친화적)
    public void ProcessRowMajor()
    {
        for (int i = 0; i < _rows; i++)
        {
            for (int j = 0; j < _cols; j++)
            {
                int index = i * _cols + j;
                _data[index] = ComputeValue(i, j);
            }
        }
    }
    
    private float ComputeValue(int row, int col) => row + col; // 예시 계산
}
```
  
  
### 결론
JobDispatcherNET의 성능을 개선하기 위해서는 위와 같은 여러 기법을 적용할 수 있습니다. 이러한 최적화는 애플리케이션의 성능 요구사항과 병목 지점에 따라 선택적으로 적용되어야 합니다. 또한 개선 전과 후의 성능을 정확하게 측정하여 실제 성능 향상을 확인하는 것이 중요하다.

실제 적용 시에는 프로파일링 도구를 사용하여 실제 병목 지점을 확인한 후, 가장 효과적인 최적화 기법을 선택하는 것이 바람직하다. 대부분의 경우 객체 풀링과 메모리 할당 최소화가 가장 큰 성능 개선을 가져올 수 있다.   