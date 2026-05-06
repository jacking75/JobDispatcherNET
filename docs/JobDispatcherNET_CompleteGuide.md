# JobDispatcherNET 완전 해설서

### Lock 없는 멀티스레드 서버 개발의 첫걸음

***

> 이 문서는 C#으로 작성된 `JobDispatcherNET` 라이브러리를 처음 접하는 서버 프로그래머를 위한 해설서다.\
> "왜 이렇게 설계했는가"부터 "실제 MMORPG 서버에서 어떻게 쓰이는가"까지, 코드와 함께 단계적으로 설명한다.

***

## 목차

1. [들어가며 — 멀티스레드 서버의 고통](#1-들어가며--멀티스레드-서버의-고통)
2. [JobDispatcherNET이란 무엇인가](#2-jobdispatchernet이란-무엇인가)
3. [핵심 개념: Actor 패턴과 메시지 패싱](#3-핵심-개념-actor-패턴과-메시지-패싱)
4. [라이브러리 구조 전체 한눈에 보기](#4-라이브러리-구조-전체-한눈에-보기)
5. [AsyncExecutable — 모든 것의 뿌리](#5-asyncexecutable--모든-것의-뿌리)
6. [JobDispatcher — 전용 스레드 관리자](#6-jobdispatcher--전용-스레드-관리자)
7. [IRunnable — 워커 스레드의 심장](#7-irunnable--워커-스레드의-심장)
8. [Job과 오브젝트 풀 — GC를 이기는 방법](#8-job과-오브젝트-풀--gc를-이기는-방법)
9. [TimerQueue — 고정밀 지연 실행](#9-timerqueue--고정밀-지연-실행)
10. [ThreadContext — 스레드별 저장소](#10-threadcontext--스레드별-저장소)
11. [DoAsync 내부 동작 완전 해부](#11-doasync-내부-동작-완전-해부)
12. [예제 1: ExampleConsoleApp — 기초 다지기](#12-예제-1-exampleconsoleapp--기초-다지기)
13. [예제 2: ExampleChatServer — 채팅 서버](#13-예제-2-examplechatserver--채팅-서버)
14. [고급 예제: AdvancedMmorpgServer — MMORPG 서버](#14-고급-예제-advancedmmorpgserver--mmorpg-서버)
15. [고급 예제: AdvancedMmorpgClient — 봇 클라이언트](#15-고급-예제-advancedmmorpgclient--봇-클라이언트)
16. [설계 패턴 정리 및 실전 팁](#16-설계-패턴-정리-및-실전-팁)
17. [자주 하는 실수와 주의사항](#17-자주-하는-실수와-주의사항)
18. [JobDispatcherNET의 한계와 단점](#18-jobdispatchernet의-한계와-단점)

***

## 1. 들어가며 — 멀티스레드 서버의 고통

게임 서버를 처음 만들 때 가장 먼저 맞닥뜨리는 벽은 **동시성(Concurrency)** 문제다.

서버에는 수백, 수천 명의 플레이어가 동시에 접속해 명령을 보낸다. 이 명령들은 여러 스레드에서 동시에 처리된다. 문제는 두 스레드가 **같은 플레이어의 체력(HP)을 동시에 수정**하려 할 때 발생한다.

```
스레드 A: player.Hp -= 30;  // NPC가 공격
스레드 B: player.Hp -= 50;  // 다른 플레이어가 공격
```

둘 다 "100"을 읽어서 각자 빼면 결과가 70이 되어야 하는데 20이 될 수도 있다. 이것을 **레이스 컨디션(Race Condition)** 이라 한다.

전통적인 해법은 `lock`이다.

```csharp
lock (_hpLock)
{
    player.Hp -= damage;
}
```

`lock`은 분명히 동작한다. 하지만 고성능 서버에서는 문제가 된다.

**lock의 문제점:**

* 한 스레드가 lock을 잡는 순간 다른 모든 스레드는 기다려야 한다 (스레드 블로킹)

* lock 범위를 잘못 잡으면 데드락(교착상태)이 발생한다

* lock이 많아질수록 코드가 복잡해지고 버그가 숨기 쉬워진다

* lock 경합(Contention)이 심해지면 CPU 코어를 여러 개 써도 성능이 안 나온다

JobDispatcherNET은 이 문제를 **근본적으로 다른 방식**으로 해결한다. lock을 쓰지 않고도 스레드 안전성을 보장하는 것이다.

***

## 2. JobDispatcherNET이란 무엇인가

JobDispatcherNET은 **C++로 작성된 멀티스레드 작업 디스패처의 .NET 이식(port)** 이다.  

**핵심 아이디어는 단 하나다:**

> 각 객체가 자기만의 작업 큐(Job Queue)를 소유한다.\
> 외부에서는 큐에 작업을 "넣기만" 하고, 실제 실행은 한 번에 하나의 스레드만 한다.

이렇게 하면 **같은 객체의 작업은 lock 없이도 자동으로 직렬화(Serialization)** 된다.

아래 그림으로 이해해보자.

```
외부에서 동시에 요청이 들어온다
────────────────────────────────────────────────
패킷: 플레이어A "이동"   → ActorA.DoAsync(이동)  ─┐
패킷: 플레이어B "이동"   → ActorB.DoAsync(이동)  ─┼─ 서로 다른 Actor → 완전 병렬 실행!
패킷: NPC가 플레이어A 공격 → ActorA.DoAsync(피해) ─┘

                            ActorA의 큐
                           ┌──────────┐
                           │  이동    │ ← 먼저 들어온 것부터 순서대로
                           │  피해    │
                           └──────────┘
                           한 번에 하나씩 처리 → lock 불필요!
```

플레이어 A의 "이동"과 "피해" 처리는 ActorA의 큐에 차례로 들어가서 순서대로 처리된다. 따라서 ActorA 내부에서는 절대 두 작업이 동시에 실행되지 않는다. **lock 없이 안전하다.**

반면 ActorA와 ActorB는 서로 다른 큐를 가지므로 동시에 실행된다. **병렬성도 극대화된다.**

***

## 3. 핵심 개념: Actor 패턴과 메시지 패싱

JobDispatcherNET의 설계는 **Actor 모델(Actor Model)** 에 기반한다.

Actor 모델은 1973년 칼 휴이트가 제안한 동시성 프로그래밍 모델이다. 핵심 규칙은 다음과 같다.

* 모든 Actor는 자신만의 상태(state)를 가진다

* Actor는 메시지를 받아서만 상태를 변경한다

* Actor들은 서로 직접 상태를 건드리지 않고, 메시지를 주고받는다

JobDispatcherNET에서는 이것이 이렇게 구현된다.

```
전통적인 방식 (위험):
    thread_A.player.Hp -= 30;  // 직접 접근 → 레이스 컨디션 위험

JobDispatcherNET 방식 (안전):
    player.DoAsync(() => player.Hp -= 30);  // 메시지로 전달 → 큐에서 순서대로 처리
```

`DoAsync`의 람다는 "메시지"다. 이 메시지는 큐에 쌓이고, 큐를 처리하는 스레드 하나만이 실제로 `player.Hp`를 건드린다. 따라서 동시 접근이 발생하지 않는다.

***

## 4. 라이브러리 구조 전체 한눈에 보기

JobDispatcherNET은 딱 5개의 파일로 이루어져 있다. 놀랍도록 작지만 강력하다.

```
JobDispatcherNET/
├── AsyncExecutable.cs   ← 핵심! 자기 큐를 가진 기반 클래스
├── JobDispatcher.cs     ← 전용 OS 스레드 N개를 생성·관리
├── IRunnable.cs         ← 워커 스레드의 루프 인터페이스
├── JobEntry.cs          ← 작업 단위 (오브젝트 풀 포함)
├── ThreadContext.cs     ← 스레드별 저장소 (ThreadLocal)
└── TimerQueue.cs        ← 고정밀 지연 실행 타이머
```

각 클래스의 관계를 보면 이렇다.

```
┌─────────────────────────────────────────────────────┐
│                  사용자 코드                         │
│  class PlayerActor : AsyncExecutable                 │
│  {                                                   │
│      public void TakeDamage(int dmg)                 │
│          => DoAsync(() => _hp -= dmg);   ← 큐에 넣기 │
│  }                                                   │
└──────────────────────────┬──────────────────────────┘
                           │ DoAsync() 호출
                           ▼
┌─────────────────────────────────────────────────────┐
│               AsyncExecutable                        │
│  - Channel<JobEntry> _jobQueue  ← 내부 큐            │
│  - DoAsync(Action)              ← 람다를 Job으로 포장 │
│  - DoAsyncAfter(delay, Action)  ← 지연 실행          │
│  - Flush()                      ← 큐를 비우는 실행기 │
└──────────────────────────┬──────────────────────────┘
             큐에서 Job 꺼내 실행하는 스레드
                           │
              ┌────────────┴────────────┐
              ▼                         ▼
┌─────────────────────┐   ┌─────────────────────────┐
│   JobDispatcher<T>  │   │      ThreadContext       │
│  - Thread[] (OS 스레드) │   │  - TimerQueue (스레드별)  │
│  - RunWorkerThreads │   │  - ExecuterQueue         │
│  - IRunnable.Run()  │   │  - CurrentExecuter       │
└─────────────────────┘   └─────────────────────────┘
```

***

## 5. AsyncExecutable — 모든 것의 뿌리

`AsyncExecutable`은 라이브러리의 핵심이다. 이 클래스를 상속받기만 하면 그 객체는 자기만의 작업 큐를 갖게 된다.

### 클래스 선언부

```csharp
public abstract class AsyncExecutable : IAsyncDisposable
{
    // 전역 에러 핸들러 — 작업 실행 중 예외 발생 시 호출됨
    public static Action<Exception>? OnError { get; set; }

    private readonly Channel<JobEntry> _jobQueue;  // 작업 큐 (내부용)
    private int _remainingTaskCount;               // 아직 남은 작업 수
    private volatile TaskCompletionSource? _drainTcs; // Dispose 시 대기용
    ...
}
```

`Channel<JobEntry>`는 .NET의 고성능 생산자-소비자 큐다. `ConcurrentQueue`보다 빠르고, 단일 소비자 최적화(`SingleReader = true`)로 더 빠른 처리가 가능하다.

### DoAsync — 작업 등록

```csharp
public void DoAsync(Action action)
{
    var job = Job.Rent(action);   // 풀에서 Job 객체를 빌린다 (GC 절약)
    DoTask(job);
}
```

`DoAsync`를 호출하면 람다가 `Job` 객체로 포장되어 큐에 들어간다. **호출한 스레드는 즉시 반환**된다. 실제 실행은 나중에 워커 스레드가 한다.

### DoAsyncAfter — 지연 실행

```csharp
public void DoAsyncAfter(TimeSpan delay, Action action)
{
    var job = Job.Rent(action);
    ThreadContext.Timer.ScheduleTask(this, delay, job);
}
```

`delay` 후에 `action`이 이 Actor의 큐를 통해 실행된다. 중요한 점은 **딜레이 후에도 이 Actor의 큐에 들어간다**는 것이다. 따라서 타이머로 실행되는 코드도 lock 없이 안전하다.

예를 들어 플레이어 부활 로직:

```csharp
// 플레이어가 죽으면 5초 후 부활 — 이 코드는 PlayerActor 내부에서 실행된다
DoAsyncAfter(TimeSpan.FromSeconds(5), () =>
{
    if (_despawned) return;
    Respawn();  // 5초 후에도 PlayerActor의 큐를 통해 직렬 실행됨 → 안전
});
```

### DisposeAsync — 깔끔한 종료

```csharp
public virtual async ValueTask DisposeAsync()
{
    if (Volatile.Read(ref _remainingTaskCount) > 0)
    {
        var tcs = new TaskCompletionSource(...);
        _drainTcs = tcs;
        if (Volatile.Read(ref _remainingTaskCount) > 0)
            await tcs.Task;  // 남은 작업이 모두 끝날 때까지 기다림
    }
    _jobQueue.Writer.Complete();
    GC.SuppressFinalize(this);
}
```

`DisposeAsync`는 폴링(polling)이 아닌 **신호 기반**으로 동작한다. 남은 작업이 0이 되는 순간 `TaskCompletionSource`가 완료 신호를 보내서 깨어난다. CPU를 낭비하지 않는다.

***

## 6. JobDispatcher — 전용 스레드 관리자

`JobDispatcher<T>`는 전용 OS 스레드들을 생성하고 관리한다.

```csharp
public sealed class JobDispatcher<T> : IDisposable, IAsyncDisposable
    where T : IRunnable, new()
{
    private readonly Thread[] _threads;
    private readonly CancellationTokenSource _cts = new();
    ...
}
```

타입 파라미터 `T`는 `IRunnable`을 구현해야 한다. 이것이 각 워커 스레드가 실행할 로직이다.

### 왜 Thread Pool이 아닌 전용 OS 스레드인가?

.NET의 `ThreadPool`이나 `Task`는 편리하지만, 스레드 풀 스레드는 언제든 재사용될 수 있다. 즉, **같은** **`ThreadLocal`** **변수를 여러 논리적 "워커"가 공유**할 수 있다.

JobDispatcherNET은 `ThreadLocal`로 타이머(`TimerQueue`)와 실행 컨텍스트(`ExecuterQueue`)를 관리한다. 이것이 올바르게 동작하려면 **스레드가 교체되지 않아야** 한다. 그래서 `new Thread()`로 전용 스레드를 직접 만든다.

```csharp
public Task RunWorkerThreadsAsync()
{
    for (int i = 0; i < _workerCount; i++)
    {
        _threads[i] = new Thread(() =>
        {
            RunWorker();   // 이 스레드는 서버가 종료될 때까지 RunWorker만 실행
            ...
        })
        {
            IsBackground = true,
            Name = $"JobWorker-{i}"
        };
        _threads[i].Start();
    }
    ...
}
```

각 스레드에는 이름(`JobWorker-0`, `JobWorker-1`, ...)이 붙는다. 디버거에서 어떤 스레드인지 바로 알 수 있어 디버깅이 쉬워진다.

### 워커 루프

```csharp
private void RunWorker()
{
    using var runner = new T();   // IRunnable 구현체 생성
    try
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            ThreadContext.TickCount = ThreadContext.Timer.GetCurrentTick(); // 시각 갱신
            if (!runner.Run(_cts.Token))  // 사용자 정의 루프 실행
                break;
        }
    }
    finally
    {
        ThreadContext.Timer.Dispose();   // 스레드 종료 시 타이머 정리
    }
}
```

매 루프마다 현재 시각(`TickCount`)을 갱신하고, `IRunnable.Run()`을 호출한다. `Run()`이 `false`를 반환하면 워커가 정상 종료된다.

***

## 7. IRunnable — 워커 스레드의 심장

`IRunnable`은 워커 스레드가 반복 실행할 로직의 인터페이스다.

```csharp
public interface IRunnable : IDisposable
{
    bool Run(CancellationToken cancellationToken);
}
```

단 하나의 메서드만 있다. `true`를 반환하면 계속 실행, `false`를 반환하면 종료다.

### 실제 구현 예

AdvancedMmorpgServer의 `GameWorker`:

```csharp
public sealed class GameWorker : IRunnable
{
    private static int _counter;
    private readonly int _id;

    public GameWorker()
    {
        _id = Interlocked.Increment(ref _counter);
        Console.WriteLine($"[워커 #{_id}] 시작");
    }

    public bool Run(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        Thread.Sleep(1);   // CPU 과점유 방지
        return true;
    }

    public void Dispose()
    {
        Console.WriteLine($"[워커 #{_id}] 종료");
    }
}
```

`Thread.Sleep(1)`이 중요하다. 이 워커는 별도의 게임 로직 없이 그냥 잠깐 자는 것처럼 보인다. 그런데 이게 맞다. **실제 작업은 AsyncExecutable.Flush()가 처리**한다. 워커 스레드는 그냥 "살아있는 스레드"로서 존재하기만 하면 된다.

### 왜 Thread.Sleep인가?

`Task.Delay`는 스레드 풀에서 재개(resume)될 수 있다. 전용 스레드에서는 `Thread.Sleep`을 써야 이 스레드가 그대로 깨어난다. `Thread.Sleep(1)`은 최소 1ms를 양보하면서도 과도한 CPU 점유를 막는다.

***

## 8. Job과 오브젝트 풀 — GC를 이기는 방법

`DoAsync()`를 호출할 때마다 람다를 감쌀 객체가 필요하다. 매번 `new`로 만들면 GC에 부담이 생긴다. 초당 수만 번 호출될 수 있는 서버에서는 이것이 누적되어 GC 멈춤(Stop-the-World)으로 이어진다.

JobDispatcherNET은 **오브젝트 풀(Object Pool)** 패턴으로 이를 해결한다.

```csharp
public sealed class Job : JobEntry
{
    private static readonly ConcurrentBag<Job> Pool = new();  // 전역 풀
    private Action? _action;

    // 풀에서 꺼내거나 없으면 새로 생성
    public static Job Rent(Action action)
    {
        if (!Pool.TryTake(out var job))
            job = new Job();
        job._action = action;
        return job;
    }

    public override void Execute()
    {
        try
        {
            _action?.Invoke();
        }
        finally
        {
            _action = null;
            Pool.Add(this);   // 사용 후 풀에 반납
        }
    }
}
```

흐름은 이렇다.

```
DoAsync(람다) 호출
    → Job.Rent(람다)  : 풀에서 Job 빌리기 (없으면 new)
    → 큐에 Job 넣기
    → Flush()에서 job.Execute() 호출
        → 람다 실행
        → Pool.Add(this) : Job을 풀에 돌려줌
    → 다음 DoAsync에서 같은 Job 재사용
```

`ConcurrentBag`은 스레드 안전한 bag(순서 없는 컬렉션)이다. 스레드 지역성(thread-local 최적화)이 있어 같은 스레드가 반납하고 꺼내는 경우 성능이 특히 좋다.

***

## 9. TimerQueue — 고정밀 지연 실행

`TimerQueue`는 `DoAsyncAfter`의 백엔드다. 정해진 시간 후에 작업을 실행시켜준다.

### 왜 Stopwatch인가?

`DateTime.UtcNow`는 시스템 시간을 반환하는데, Windows에서는 약 15ms 단위로 갱신된다. 1ms 단위 게임 로직에는 너무 거칠다. `Stopwatch`는 하드웨어 성능 카운터를 사용하므로 **나노초 단위 정밀도**를 갖는다.

```csharp
public long GetCurrentTick() =>
    (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
```

### 내부 자료구조

작업들은 `PriorityQueue`에 만료 시각(due time) 기준으로 저장된다.

```csharp
private readonly PriorityQueue<TimerJob, long> _queue = new();
```

`PriorityQueue`는 최소 힙(min-heap)으로 구현되어 있다. 가장 빨리 실행되어야 할 작업이 항상 맨 위에 있다.

### 처리 루프

```csharp
private async Task ProcessTimerJobsAsync()
{
    while (await _timer.WaitForNextTickAsync())  // 1ms마다 깨어남
    {
        ProcessDueJobs();   // 만료된 작업을 꺼내 실행
    }
}

private void ProcessDueJobs()
{
    lock (_lock)
    {
        var now = GetCurrentTick();
        while (_queue.Count > 0 && _queue.TryPeek(out _, out var dueTime) && now >= dueTime)
        {
            _jobBuffer.Add(_queue.Dequeue());
        }
    }
    foreach (var job in _jobBuffer)
    {
        job.Owner.DoTask(job.Task);  // Actor의 큐에 넣기
    }
}
```

중요한 점: 타이머가 발동하면 직접 실행하는 것이 아니라, **해당 Actor의 큐(`DoTask`)에 넣는다**. 따라서 타이머 콜백도 Actor 내에서 직렬화되어 안전하다.

***

## 10. ThreadContext — 스레드별 저장소

`ThreadContext`는 `ThreadLocal`을 사용해 스레드별로 독립적인 데이터를 관리한다.

```csharp
public static class ThreadContext
{
    private static readonly ThreadLocal<TimerQueue> _timer = new(() => new TimerQueue());
    private static readonly ThreadLocal<Queue<AsyncExecutable>> _executerQueue = new(() => new Queue<AsyncExecutable>());
    private static readonly ThreadLocal<AsyncExecutable?> _currentExecuter = new(() => null);
    private static readonly ThreadLocal<long> _tickCount = new();
    ...
}
```

각 필드의 역할:

**`Timer`**: 이 스레드에서 스케줄된 타이머들. 스레드마다 독립적이므로 타이머 큐 접근에 lock이 최소화된다.

**`ExecuterQueue`**: 이 스레드에서 처리 대기 중인 `AsyncExecutable` 목록. `DoAsync`가 연쇄적으로 호출될 때 재귀 호출 대신 큐에 쌓아서 나중에 처리한다.

**`CurrentExecuter`**: 지금 이 스레드에서 실행 중인 `AsyncExecutable`. 연쇄 호출 감지에 사용된다.

**`TickCount`**: `JobDispatcher`의 워커 루프가 매 틱마다 갱신하는 현재 시각. 작업 코드에서 `ThreadContext.TickCount`로 현재 시각을 빠르게 읽을 수 있다.

***

## 11. DoAsync 내부 동작 완전 해부

`DoAsync`가 어떻게 동작하는지를 이해하는 것이 JobDispatcherNET을 제대로 쓰는 핵심이다.

`DoTask`(DoAsync의 내부 구현)의 코드:

```csharp
internal void DoTask(JobEntry task)
{
    if (Interlocked.Increment(ref _remainingTaskCount) > 1)
    {
        // 케이스 A: 이미 다른 작업이 실행 중 → 큐에만 넣고 종료
        _jobQueue.Writer.TryWrite(task);
    }
    else
    {
        // 케이스 B 또는 C: 첫 번째 작업
        _jobQueue.Writer.TryWrite(task);

        var currentExecuter = ThreadContext.CurrentExecuter;
        if (currentExecuter is not null)
        {
            // 케이스 B: 이미 다른 Actor 실행 중 → ExecuterQueue에 예약
            ThreadContext.ExecuterQueue.Enqueue(this);
        }
        else
        {
            // 케이스 C: 완전히 새로운 시작 → 즉시 실행
            try
            {
                ThreadContext.CurrentExecuter = this;
                Flush();

                while (ThreadContext.ExecuterQueue.TryDequeue(out var dispatcher))
                {
                    dispatcher.Flush();  // 연쇄된 Actor들도 처리
                }
            }
            finally
            {
                ThreadContext.CurrentExecuter = null;
            }
        }
    }
}
```

세 가지 케이스를 구분해서 이해하자.

### 케이스 A: 이미 실행 중

```
Actor.DoAsync(작업2) 호출
    → _remainingTaskCount가 2 이상 → 이미 누군가 Flush() 중
    → 큐에 작업2만 넣고 반환
    → Flush()를 실행 중인 스레드가 나중에 작업2도 처리해줌
```

이것이 직렬화의 핵심이다. 실행 중인 스레드가 끝날 때까지 큐에 쌓인 작업들이 순서대로 처리된다.

### 케이스 B: 다른 Actor 실행 중

```
ActorA가 Flush() 하는 도중 ActorB.DoAsync(작업) 호출
    → _remainingTaskCount가 1 → 첫 작업
    → ThreadContext.CurrentExecuter = ActorA (이미 점유 중)
    → ExecuterQueue에 ActorB를 넣고 반환
    → ActorA의 Flush()가 끝나면 ExecuterQueue에서 ActorB를 꺼내 처리
```

이 케이스 덕분에 **재귀 호출 없이** 연쇄적인 Actor 호출을 처리할 수 있다. 스택 오버플로우가 발생하지 않는다.

### 케이스 C: 완전히 새로운 시작

```
아무것도 실행 중이지 않을 때 Actor.DoAsync(작업) 호출
    → _remainingTaskCount가 1 → 첫 작업
    → ThreadContext.CurrentExecuter = null → 아무도 실행 중 아님
    → 내가 직접 Flush() 실행
    → Flush() 중에 추가된 Actor들은 ExecuterQueue에서 처리
```

### Flush 내부

```csharp
internal void Flush()
{
    var spinner = new SpinWait();
    while (true)
    {
        if (_jobQueue.Reader.TryRead(out var job))
        {
            spinner.Reset();
            try { job.Execute(); }
            catch (Exception ex) { OnError?.Invoke(ex); }

            if (Interlocked.Decrement(ref _remainingTaskCount) == 0)
            {
                _drainTcs?.TrySetResult();  // Dispose 대기 중이면 신호
                break;
            }
        }
        else
        {
            spinner.SpinOnce();  // 큐가 잠깐 비었을 때 바쁜 대기
        }
    }
}
```

`SpinWait`는 처음에는 CPU를 적극적으로 사용해서 빠르게 재시도하고, 일정 횟수 이상이면 `Thread.Yield()`나 `Thread.Sleep()`으로 양보한다. 짧은 대기에 최적화된 패턴이다.

***

## 12. 예제 1: ExampleConsoleApp — 기초 다지기

가장 단순한 예제부터 시작하자.

### 기본 사용법 (BasicExampleAsync)

```csharp
await using var testObject = new TestObject();

// 세 가지 메서드를 비동기로 큐에 등록
testObject.DoAsync(() => testObject.TestFunc0());
testObject.DoAsync(() => testObject.TestFunc1(5));
testObject.DoAsync(() => testObject.TestFunc2(25, 10));

// 500ms 후에 실행될 작업 등록
testObject.DoAsyncAfter(TimeSpan.FromMilliseconds(500), () => testObject.TestFunc1(15));

await Task.Delay(1000);
```

이 코드에서 `TestObject`는 `AsyncExecutable`을 상속한다. 세 메서드 호출은 큐에 순서대로 들어가서 하나씩 처리된다.

### 워커 스레드 예제 (WorkerThreadExampleAsync)

```csharp
await using var dispatcher = new JobDispatcher<TestWorkerThread>(4); // 4개 스레드

var dispatcherTask = Task.Run(async () => await dispatcher.RunWorkerThreadsAsync());

await Task.Delay(TimeSpan.FromSeconds(5));

await dispatcher.DisposeAsync();  // 모든 워커 정상 종료
```

`JobDispatcher<TestWorkerThread>(4)`는 OS 스레드 4개를 만들고, 각 스레드에서 `TestWorkerThread.Run()`을 반복 호출한다.

### 데이터 처리 예제 (AdvancedExampleAsync)

```csharp
public class DataProcessor : AsyncExecutable
{
    private readonly Dictionary<string, int> _processedItems = new();

    public void ProcessItem(string itemId, int priority)
    {
        // 무거운 작업 시뮬레이션
        Thread.Sleep(100 * (1 + Random.Shared.Next(5)));

        _processedItems[itemId] = (_processedItems.GetValueOrDefault(itemId)) + 1;

        // 우선순위에 따라 후속 작업 예약
        if (priority > 5)
            DoAsync(() => HighPriorityFollowUp(itemId));
        else if (priority > 2)
            DoAsyncAfter(TimeSpan.FromMilliseconds(500), () => MediumPriorityFollowUp(itemId));
    }
}
```

`ProcessItem` 자체가 `DoAsync`로 호출되므로, 내부에서 `_processedItems`를 수정할 때 lock이 필요 없다. 항상 한 번에 하나의 스레드만 접근하기 때문이다.

후속 작업(`HighPriorityFollowUp`)도 `DoAsync`로 등록하므로 역시 안전하다.

***

## 13. 예제 2: ExampleChatServer — 채팅 서버

채팅 서버는 `AsyncExecutable`의 위력이 잘 드러나는 예제다. 채팅방(`Room`)이 각자 `AsyncExecutable`을 상속하여 독립적인 큐를 갖는다.

### 구조 개요

```
ChatServer (AsyncExecutable)
    ├── User (plain class)
    ├── Room (AsyncExecutable) ← 방마다 독립 큐
    │   ├── Room "일반 채팅"
    │   ├── Room "게임 채팅"
    │   └── Room "개발자 채팅"
    └── JobDispatcher<ChatWorker> ← 4개 워커 스레드
```

### ChatServer: 전체 조율자

`ChatServer`도 `AsyncExecutable`을 상속한다.

```csharp
public class ChatServer : AsyncExecutable
{
    private readonly Dictionary<string, User> _users = [];
    private readonly Dictionary<string, Room> _rooms = [];
    private readonly ReaderWriterLockSlim _usersLock = new();
    private readonly object _roomsLock = new();
    ...
}
```

사용자 목록(`_users`)은 여러 스레드에서 읽힐 수 있어 `ReaderWriterLockSlim`을 사용한다. 방 목록(`_rooms`)은 `object` lock으로 보호한다. 이것들은 `AsyncExecutable` 큐 바깥에서 접근될 수 있기 때문이다.

### 사용자 접속 처리

```csharp
// 외부(네트워크 스레드 등)에서 호출
public void HandleUserConnect(IChatClient client)
{
    DoAsync(() => ProcessUserConnect(client));  // ChatServer 큐에 넣기
}

// ChatServer의 큐에서 실행 — 단일 스레드 보장
private void ProcessUserConnect(IChatClient client)
{
    var user = new User(client);
    _usersLock.EnterWriteLock();
    try { _users[user.UserId] = user; }
    finally { _usersLock.ExitWriteLock(); }

    BroadcastSystemMessage(MessageType.UserConnect, $"{user.Username}님이 접속했습니다.", null);
}
```

### Room: 독립 실행 단위

방(Room)이 `AsyncExecutable`을 상속하는 것이 이 설계의 핵심이다.

```csharp
public class Room : AsyncExecutable
{
    private readonly Dictionary<string, User> _users = [];  // lock 불필요!

    public void AddUser(User user)
    {
        DoAsync(() => {
            _users[user.UserId] = user;  // 이 Room의 큐에서만 실행 → 안전
            BroadcastSystemMessage(MessageType.RoomJoin, $"{user.Username}님이 입장했습니다.");
        });
    }

    public void ProcessChatMessage(string userId, string content)
    {
        DoAsync(() => {
            if (_users.TryGetValue(userId, out var sender))
            {
                var message = new ChatMessage(...);
                foreach (var user in _users.Values)  // lock 없이 안전
                    _ = user.SendMessageAsync(message);
            }
        });
    }
}
```

`Room._users`는 `Dictionary`(lock이 없는 일반 컬렉션)인데도 안전하다. 항상 이 Room의 DoAsync 내에서만 수정되기 때문에 동시 접근이 불가능하다.

### 여러 Room이 동시에 처리되는 구조

```
워커 스레드 1: "일반 채팅" Room의 메시지 처리
워커 스레드 2: "게임 채팅" Room의 메시지 처리
워커 스레드 3: "개발자 채팅" Room의 메시지 처리
워커 스레드 4: ChatServer의 접속/퇴장 처리
```

각 Room이 독립적인 큐를 가지므로 서로 방해하지 않는다. 사용자가 늘어나 방이 많아져도 자연스럽게 병렬 처리가 늘어난다.

***

## 14. 고급 예제: AdvancedMmorpgServer — MMORPG 서버

이 예제는 실제 MMORPG 서버에 가장 가까운 구현이다. 플레이어와 NPC가 실시간으로 이동하고, 공격하고, 죽고, 부활하는 모든 로직이 담겨 있다.

### 전체 아키텍처

```
GameServer
├── GameWorld                        ← 단일 월드, Actor 컨테이너
│   ├── PlayerActor[] (AsyncExecutable) ← 플레이어마다 독립 큐
│   ├── NpcActor[]   (AsyncExecutable) ← NPC마다 독립 큐 + 자가 스케줄링
│   ├── BroadcastActor (AsyncExecutable) ← 주기적 전체 상태 브로드캐스트
│   └── SpatialIndex                ← 공간 쿼리 (ConcurrentDictionary 기반)
├── NetworkServer                   ← TCP Accept + ClientSession 관리
└── JobDispatcher<GameWorker>       ← 8개 전용 OS 스레드
```

### PlayerActor: 클라이언트의 대리인

```csharp
public sealed class PlayerActor : AsyncExecutable
{
    private readonly Player _player;
    private readonly GameWorld _world;
    private volatile bool _despawned;

    // 클라이언트가 이동 패킷을 보내면 호출
    public void Move(float newX, float newY)
    {
        DoAsync(() =>
        {
            if (_despawned || !_player.IsAlive) return;

            // 속도 제한 — 핵 방지
            float dx = newX - _player.X, dy = newY - _player.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float maxStep = _player.MoveSpeed * 0.5f;
            if (dist > maxStep && dist > 0.0001f)
            {
                float k = maxStep / dist;
                newX = _player.X + dx * k;
                newY = _player.Y + dy * k;
            }

            _player.X = Math.Clamp(newX, 0, _world.Width);
            _player.Y = Math.Clamp(newY, 0, _world.Height);
            _world.Spatial.UpdatePosition(_player, /* oldX, oldY */ ...);
        });
    }
}
```

이동 패킷은 어떤 스레드에서도 올 수 있다. `DoAsync`로 감싸면 실제 이동 처리는 항상 PlayerActor의 큐에서 직렬로 처리된다.

### Actor 간 데미지 전달 (핵심 패턴)

공격이 이루어질 때를 보자. NPC가 플레이어를 공격하는 경우:

```csharp
// NpcActor에서 실행 (NPC의 큐)
private void TickAttack(long now, float dt)
{
    ...
    // AttackerSnapshot: 불변 값 타입 — 복사본이 전달됨
    var snap = new AttackerSnapshot(_npc.Id, _npc.Name, _npc.Kind,
        _npc.X, _npc.Y, _npc.Attack);
    _world.SendDamage(_targetId, snap, _npc.AttackRange + 0.5f);
}

// GameWorld.SendDamage
public void SendDamage(int targetId, AttackerSnapshot atk, float meleeRange)
{
    if (_players.TryGetValue(targetId, out var pa))
        pa.ReceiveDamage(atk, meleeRange);   // PlayerActor의 큐에 넣기
    else if (_npcs.TryGetValue(targetId, out var na))
        na.ReceiveDamage(atk, meleeRange);   // NpcActor의 큐에 넣기
}

// PlayerActor.ReceiveDamage — PlayerActor의 큐에서 실행
public void ReceiveDamage(AttackerSnapshot atk, float meleeRange)
{
    DoAsync(() =>
    {
        if (_despawned || !_player.IsAlive) return;
        float d = _player.DistanceTo(atk.X, atk.Y);
        if (d > meleeRange) return;   // 사거리 서버 측 검증

        int dealt = _player.TakeDamage(atk.Attack);
        _world.NotifyAttack(atk.AttackerId, _player.Id, dealt);

        if (!_player.IsAlive)
        {
            _world.NotifyDeath(_player.Id, atk.AttackerId);
            DoAsyncAfter(TimeSpan.FromSeconds(5), () => Respawn());  // 5초 후 부활
        }
    });
}
```

`AttackerSnapshot`이 `readonly record struct`인 점에 주목하자. NPC의 위치와 공격력을 불변 값 타입으로 캡처하여 전달한다. 이렇게 하면 공유 메모리 없이 Actor 경계를 안전하게 넘을 수 있다.

```csharp
public readonly record struct AttackerSnapshot(
    int AttackerId,
    string AttackerName,
    EntityKind AttackerKind,
    float X,
    float Y,
    int Attack);
```

### NpcActor: 자가 스케줄링 AI

NPC의 AI는 스스로를 반복 실행시키는 패턴을 사용한다.

```csharp
public sealed class NpcActor : AsyncExecutable
{
    public void Start()
    {
        // 첫 Tick을 자기 큐에 예약
        DoAsync(() => {
            var initial = TimeSpan.FromMilliseconds(Random.Shared.Next(0, _tickInterval.Milliseconds));
            DoAsyncAfter(initial, Tick);  // 첫 틱까지 약간 분산
        });
    }

    private void Tick()
    {
        if (_despawned || _world.IsStopping) return;
        if (!_npc.IsAlive) return;  // 사망 중이면 체인 끊기

        // AI 상태 업데이트
        switch (_state)
        {
            case AiState.Idle:   TickIdle(now, dt);   break;
            case AiState.Chase:  TickChase(now, dt);  break;
            case AiState.Attack: TickAttack(now, dt); break;
            case AiState.Flee:   TickFlee(now, dt);   break;
        }

        // 자기 자신을 다음 틱에 다시 예약 — 자가 스케줄링 체인
        DoAsyncAfter(_tickInterval, Tick);
    }
}
```

이 패턴의 특징:

* NPC가 100마리면 100개의 독립된 타이머 체인이 돌아간다

* 모든 NPC의 Tick은 워커 풀에서 병렬 처리된다

* 같은 NPC의 Tick들은 자기 Actor 큐에서 직렬화된다 → lock 불필요

* `IsStopping`이 true면 체인을 더 이상 연장하지 않아 자연스럽게 종료된다

### NPC AI 상태 머신

```
┌─────────┐   플레이어 탐지    ┌─────────┐   사거리 내   ┌─────────┐
│  Idle   │ ─────────────────→ │  Chase  │ ────────────→ │ Attack  │
│(패트롤) │                   │(추적)   │              │(공격)   │
└─────────┘                   └─────────┘              └─────────┘
                                   ↑ 타겟 사거리 이탈        │
                                   └─────────────────────────┘
                                   ↑ 도망 시간 만료
                               ┌─────────┐
                               │  Flee   │ ← HP 낮을 때
                               │(도망)   │
                               └─────────┘
```

각 상태별 행동:

**Idle(패트롤)**: 스폰 위치 근방에서 무작위로 이동. 범위 내 플레이어가 보이면 Chase로 전환.

**Chase(추적)**: 타겟 방향으로 이동. 사거리 이탈 시 Idle로. 공격 거리에 들어오면 Attack으로.

**Attack(공격)**: 1.5초 쿨다운으로 데미지 전달. 타겟이 사거리를 벗어나면 Chase로.

**Flee(도망)**: HP가 임계값 이하일 때. 공격자 반대 방향으로 이동. 4초 후 자동으로 Idle로.

### SpatialIndex — 공간 쿼리 최적화

수백 개의 NPC가 매 틱마다 "주변에 플레이어가 있나?" 를 묻는다면 O(N²) 문제가 된다. `SpatialIndex`는 그리드 기반 공간 분할로 이를 해결한다.

```csharp
public sealed class SpatialIndex
{
    private readonly float _cellSize;  // 기본값: 50 유닛
    private readonly ConcurrentDictionary<(int, int), ConcurrentDictionary<int, Entity>> _grid = [];

    public Player? FindNearestPlayer(float cx, float cy, float maxRange)
    {
        // 반경에 걸치는 셀들만 검사
        var candidates = QueryRadius(cx, cy, maxRange, EntityKind.Player);
        ...
    }
}
```

월드를 50×50 크기의 격자로 나눈다. 엔티티는 자신이 있는 셀에 등록된다. "주변 탐색"은 중심 셀 주변의 몇 개 셀만 검사하면 되므로 전체를 순회하지 않아도 된다.

`ConcurrentDictionary`를 사용하므로 외부 lock 없이 여러 Actor가 동시에 읽고 쓸 수 있다.

### NetworkServer: 비동기 TCP 서버

```csharp
public sealed class NetworkServer
{
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var tcp = await _listener!.AcceptTcpClientAsync(ct);
            var session = new ClientSession(connId, tcp, _server, OnSessionClosed);
            _ = session.RunAsync(ct);   // 세션마다 독립적으로 실행
        }
    }
}
```

세션(`ClientSession`)은 수신 루프와 송신 루프가 분리되어 있다.

```csharp
public sealed class ClientSession
{
    private readonly Channel<string> _outgoing;  // 송신 전용 큐

    // 게임 로직(Actor)에서 호출 — 블로킹 없음
    public void SendPacket(string msg)
    {
        _outgoing.Writer.TryWrite(msg);   // 큐에 넣고 즉시 반환
    }

    // 별도 Task에서 실제 송신
    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var msg in _outgoing.Reader.ReadAllAsync(ct))
            await _stream!.WriteAsync(Encoding.UTF8.GetBytes(msg + "\n"), ct);
    }
}
```

`SendPacket`은 큐에 메시지를 넣고 즉시 반환한다. 실제 네트워크 I/O는 `SendLoop`가 담당한다. 이렇게 하면 **게임 로직 스레드가 네트워크 지연에 블로킹되지 않는다.**

### 패킷 프로토콜

이 예제는 텍스트 기반의 단순한 프로토콜을 사용한다. 실제 상용 게임에서는 이진 프로토콜을 쓰지만, 여기서는 이해하기 쉬운 텍스트로 구현했다.

**서버 → 클라이언트:**

```
WELCOME|playerId|x|y|worldW|worldH
SPAWN|id|kind|name|x|y|hp|maxHp|color
DESPAWN|id
STATE|id,x,y,hp|id,x,y,hp|...   ← 100ms마다 전체 상태 브로드캐스트
ATTACK|attackerId|targetId|damage
DEATH|id|killerId
RESPAWN|id|x|y|hp
```

**클라이언트 → 서버:**

```
LOGIN|botName
MOVE|x|y
ATTACK|targetId
LEAVE
```

### BroadcastActor: 주기적 상태 동기화

모든 클라이언트에게 100ms마다 전체 월드 상태를 전송한다.

```csharp
internal sealed class BroadcastActor : AsyncExecutable
{
    public void Start() => DoAsync(Tick);

    private void Tick()
    {
        if (_world.IsStopping) return;  // 종료 시 체인 끊기

        _world.BroadcastSnapshot();     // 전체 엔티티 위치·HP 전송

        DoAsyncAfter(_interval, Tick);  // 100ms 후 다시 예약 (자가 스케줄링)
    }
}
```

NpcActor와 같은 자가 스케줄링 패턴이다. 이 Actor도 워커 풀에서 실행되므로 다른 게임 로직과 병렬로 동작한다.

### 서버 종료 처리

서버 종료는 세심한 순서가 필요하다.

```csharp
public async Task StopAsync()
{
    _isStopping = true;               // 1. 새로운 AI tick 체인 연장 금지

    foreach (var s in _sessions.Values) s.Close();  // 2. 클라이언트 연결 종료
    _sessions.Clear();

    foreach (var na in _npcs.Values) na.Despawn();   // 3. 모든 Actor despawn
    foreach (var pa in _players.Values) pa.Despawn();

    if (_broadcaster is not null)
        await _broadcaster.DisposeAsync();           // 4. 브로드캐스터 완전 종료

    await Task.Delay(200);                           // 5. 잔여 작업 처리 시간

    foreach (var na in _npcs.Values) await na.DisposeAsync();   // 6. 큐 완전히 비우기
    foreach (var pa in _players.Values) await pa.DisposeAsync();
}
```

`DisposeAsync`는 남은 작업이 모두 처리될 때까지 기다리므로, 이 순서대로 하면 데이터 손실 없이 깔끔하게 종료된다.

***

## 15. 고급 예제: AdvancedMmorpgClient — 봇 클라이언트

클라이언트는 MonoGame 기반의 그래픽 클라이언트로, 여러 봇이 동시에 서버에 접속해 자동으로 전투를 벌이는 스트레스 테스트 도구다.

### 구조 개요

```
Game1 (MonoGame)
├── BotManager          ← N개 봇 생성·관리
│   ├── BotClient[0]    ← 봇마다 독립 AI + 네트워크
│   │   ├── NetworkClient  ← TCP 통신
│   │   └── AI Loop (Task)
│   ├── BotClient[1]
│   └── ...
└── WorldState          ← 모든 봇이 공유하는 월드 스냅샷
```

### WorldState: 공유 월드 뷰

```csharp
public sealed class WorldState
{
    public ConcurrentDictionary<int, EntityView> Entities { get; } = [];

    public void HandlePacket(string packet)
    {
        var parts = packet.Split('|');
        switch (parts[0])
        {
            case "SPAWN":
                Entities[id] = new EntityView { Id=id, ... };
                break;
            case "STATE":
                // 100ms마다 오는 전체 상태 — 위치·HP 갱신
                foreach (var segment in parts[1..])
                {
                    if (Entities.TryGetValue(eid, out var ev))
                    { ev.X = x; ev.Y = y; ev.Hp = hp; }
                }
                break;
            ...
        }
    }
}
```

여러 봇의 `NetworkClient`가 패킷을 받아 `WorldState.HandlePacket`을 호출한다. `ConcurrentDictionary` 덕분에 동시 접근이 안전하다.

### BotClient: 봇의 AI

```csharp
public sealed class BotClient
{
    private enum AiState { Wander, Engage, Flee }

    public async Task RunAiAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_cfg.Bots.TickIntervalMs);
        while (!ct.IsCancellationRequested && _net.Connected)
        {
            TickAi();
            await Task.Delay(interval, ct);
        }
    }

    private void TickAi()
    {
        if (!_world.Entities.TryGetValue(PlayerId, out var me)) return;
        if (!me.IsAlive) return;

        // HP 낮으면 도망
        if (me.Hp < me.MaxHp * FleeHpRatio)
        {
            DoFlee(me, now);
            return;
        }

        switch (_state)
        {
            case AiState.Wander: DoWander(me, now); break;
            case AiState.Engage: DoEngage(me, now); break;
            case AiState.Flee:   DoFlee(me, now);   break;
        }
    }
}
```

봇의 AI는 서버의 NpcActor AI와 매우 유사하다. `Wander → Engage → Flee` 상태 머신으로 동작한다.

흥미로운 점은 **봇은 JobDispatcherNET을 사용하지 않는다**. 봇마다 `Task`로 AI 루프를 돌리고, `WorldState`는 `ConcurrentDictionary`로 보호한다. 클라이언트는 상대적으로 단순하고 봇 수도 제한적이므로 이 정도면 충분하다.

### NetworkClient: 비동기 송수신 분리

```csharp
public sealed class NetworkClient
{
    private readonly Channel<string> _outgoing;  // 송신 큐

    public void SendMove(float x, float y)
        => Send($"MOVE|{x:F1}|{y:F1}");   // 큐에 넣고 즉시 반환

    // 별도 Task에서 실제 송신
    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var msg in _outgoing.Reader.ReadAllAsync(ct))
            await _stream!.WriteAsync(Encoding.UTF8.GetBytes(msg + "\n"), ct);
    }
}
```

서버의 `ClientSession`과 동일한 패턴이다. AI가 이동 패킷을 보낼 때 `SendMove`를 호출하면, 실제 TCP 송신은 `SendLoop`가 처리한다. AI가 네트워크 지연에 블로킹되지 않는다.

### MonoGame 렌더러

```csharp
protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Color.Black);
    _sb!.Begin(samplerState: SamplerState.PointClamp);
    _renderer!.Draw(_sb, gameTime);   // WorldState를 읽어 화면에 그리기
    _sb.End();
}
```

렌더러는 매 프레임 `WorldState.Entities`를 읽어 모든 엔티티를 화면에 그린다. `ConcurrentDictionary`를 읽는 것이므로 별도의 락 없이 안전하다.

***

## 16. 설계 패턴 정리 및 실전 팁

### 패턴 1: Actor 경계에서 불변 값 타입으로 데이터 전달

Actor 간에 데이터를 전달할 때는 **불변(immutable) 값 타입**을 사용하라.

```csharp
// 잘못된 예 — 레퍼런스 전달은 위험하다
public void SendDamage(int targetId, Npc attacker)
{
    // attacker의 X, Y가 다른 스레드에서 바뀔 수 있다!
    target.ReceiveDamage(attacker);
}

// 올바른 예 — 값 타입 스냅샷 전달
public void SendDamage(int targetId, AttackerSnapshot snap)
{
    // snap은 값 타입이므로 복사본 — 완전히 안전
    target.ReceiveDamage(snap);
}

public readonly record struct AttackerSnapshot(int Id, float X, float Y, int Attack);
```

### 패턴 2: Actor 내부 상태는 DoAsync 밖에서 읽지 않는다

```csharp
// 잘못된 예
int hp = playerActor.Player.Hp;  // 다른 스레드에서 읽는 것은 이론적으로 위험

// 올바른 예 (브로드캐스트처럼 성능이 중요한 경우)
// Volatile.Read로 최소한의 안전성 확보
int hp = Volatile.Read(ref player.Hp);

// 가장 안전한 예
playerActor.DoAsync(() => {
    ProcessHp(playerActor.Player.Hp);  // Actor 내부에서 읽기
});
```

### 패턴 3: 자가 스케줄링으로 주기적 작업 구현

```csharp
// IRunnable에서 폴링하는 방식보다 자가 스케줄링이 더 우아하다
public void StartPeriodicTask()
{
    DoAsync(PeriodicTick);
}

private void PeriodicTick()
{
    if (_stopping) return;  // 종료 시 체인 끊기

    DoWork();  // 실제 작업

    DoAsyncAfter(TimeSpan.FromMilliseconds(100), PeriodicTick);  // 다음 틱 예약
}
```

### 패턴 4: 전역 에러 핸들러 설정

```csharp
// 서버 시작 시 반드시 설정
AsyncExecutable.OnError = ex =>
{
    Logger.Error($"[JobDispatcher] 처리되지 않은 예외: {ex}");
    // 필요에 따라 서버 재시작 로직 등
};
```

설정하지 않으면 `Console.Error.WriteLine`으로만 출력된다. 상용 서버에서는 반드시 로깅 시스템과 연동해야 한다.

### 패턴 5: DoAsyncAfter는 워커 스레드 컨텍스트에서 호출

`DoAsyncAfter`는 `ThreadContext.Timer`를 사용한다. 워커 스레드가 아닌 곳(예: `Task.Run`, `async Task`)에서 호출하면 해당 스레드의 `TimerQueue`가 사용된다. `TimerRegistry.DisposeAll()`을 종료 시 호출하면 모든 타이머가 정리된다.

***

## 17. 자주 하는 실수와 주의사항

### 실수 1: DoAsync 내부에서 장시간 블로킹

```csharp
// 잘못된 예 — 이 작업이 끝날 때까지 다른 작업들이 기다린다
actor.DoAsync(() =>
{
    Thread.Sleep(5000);   // 5초 블로킹! 이 Actor의 다른 작업이 전부 지연됨
    DoWork();
});

// 올바른 예 — 비동기 작업은 별도 Task로 분리하거나 분할 처리
actor.DoAsync(() => DoWork());
actor.DoAsyncAfter(TimeSpan.FromSeconds(5), () => DoFollowUp());
```

### 실수 2: Actor 내부 컬렉션을 외부에 노출

```csharp
// 잘못된 예 — 외부에서 참조를 받아 직접 접근하면 안전하지 않음
public List<User> GetUsers() => _users;  // 위험!

// 올바른 예 — 복사본 반환
public List<User> GetUsers()
{
    // DoAsync 내부에서 호출되거나, lock 필요
    return new List<User>(_users);
}
```

### 실수 3: DisposeAsync를 await 없이 호출

```csharp
// 잘못된 예 — 남은 작업이 처리되기 전에 계속 진행될 수 있음
actor.DisposeAsync();  // await 없이 호출

// 올바른 예
await actor.DisposeAsync();
```

### 실수 4: 서버 종료 순서 무시

Actor들을 먼저 `Despawn` 신호로 멈추고, 그 다음 `DisposeAsync`로 드레인(drain)해야 한다. 순서를 지키지 않으면 이미 Dispose된 Actor에 작업이 들어올 수 있다.

```csharp
// 올바른 종료 순서
_isStopping = true;                         // 새 작업 생성 중단
foreach (var actor in _actors) actor.Stop(); // 자가 스케줄링 체인 종료
await Task.Delay(100);                       // 잠깐 대기 (진행 중인 DoAsyncAfter 처리)
foreach (var actor in _actors) await actor.DisposeAsync(); // 큐 완전 비우기
```

### 실수 5: 워커 수를 무한정 늘리기

워커 스레드가 많다고 항상 좋은 것은 아니다. CPU 코어 수보다 많은 스레드는 컨텍스트 스위칭 오버헤드를 증가시킨다.

```json
// config.json
{
    "server": {
        "workerThreads": 8   // CPU 코어 수 ~ CPU 코어 수 × 2 정도가 적당
    }
}
```

NPC 수가 많다고 워커를 늘리는 것보다, 각 NPC의 tick 간격을 늘리거나 로직을 단순화하는 것이 더 효과적일 때가 많다.

***

## 18. JobDispatcherNET의 한계와 단점

JobDispatcherNET은 강력하지만 만능은 아니다. 이 라이브러리를 도입하기 전에 아래의 한계들을 충분히 이해하고 있어야 한다. 장점만큼 단점을 아는 것이 좋은 설계의 출발점이다.

---

### 단점 1: async/await와 섞기 어렵다

.NET 생태계는 `async/await` 중심으로 설계되어 있다. 데이터베이스, HTTP 클라이언트, 파일 I/O 등 거의 모든 최신 라이브러리가 `Task`를 반환한다. 그런데 JobDispatcherNET의 `DoAsync`는 `Action`(반환값 없는 동기 람다)만 받는다.

```csharp
// 이런 코드는 쓸 수 없다
player.DoAsync(async () =>
{
    var result = await _db.LoadPlayerDataAsync(player.Id);  // ← 컴파일은 되지만 위험!
    player.ApplyData(result);
});
```

`DoAsync`에 `async` 람다를 넣으면 컴파일러가 `async void`로 처리한다. `async void`는 예외가 발생해도 `OnError`로 잡히지 않고 프로세스를 크래시시킬 수 있다. 또한 람다가 첫 번째 `await` 지점에서 즉시 반환되어버리므로, 이후 로직이 어떤 스레드에서 실행될지 보장할 수 없다. Actor의 직렬화 보장이 깨진다.

**현실적인 해결책:**

비동기 I/O 결과를 받은 다음 다시 Actor의 큐에 넣는 패턴을 써야 한다.

```csharp
// 올바른 패턴 — "비동기 I/O → 결과를 Actor 큐에 전달"
Task.Run(async () =>
{
    var data = await _db.LoadPlayerDataAsync(player.Id);  // 비동기 I/O (외부 스레드)
    playerActor.DoAsync(() => playerActor.ApplyData(data));  // 결과를 Actor 큐에 전달
});
```

이 패턴은 동작하지만 코드가 복잡해진다. 대규모 서버에서 DB 호출이 많으면 이 패턴이 도처에 반복되어 코드 가독성이 떨어진다.

**근본 원인:** JobDispatcherNET은 C++ 원본의 설계를 충실히 이식했다. C++에는 `async/await`가 없고, 비동기 I/O도 콜백이나 별도 스레드로 처리한다. .NET의 `Task` 생태계를 전제로 설계된 것이 아니다.

---

### 단점 2: DoAsync 내부에서 블로킹하면 전체 워커가 막힌다

```csharp
// 워커 스레드 4개인데, 4개 모두 아래처럼 블로킹되면?
actor.DoAsync(() =>
{
    var result = _db.Query(...);  // 동기 DB 쿼리 — 200ms 블로킹
    Process(result);
});
```

워커 스레드는 OS 스레드다. 블로킹되면 그 스레드는 다른 Actor를 처리하지 못한다. 워커 수보다 많은 Actor가 동시에 블로킹 작업을 하면 **전체 서버가 멈춘다**.

.NET의 `ThreadPool`은 블로킹이 감지되면 스레드를 추가(hill-climbing 알고리즘)해서 이 문제를 어느 정도 자동 해결한다. 하지만 JobDispatcherNET의 전용 스레드는 그런 자동 조절 기능이 없다. 워커 수는 시작 시 고정된다.

**대처 방법:**

DoAsync 내부에서는 순수 CPU 연산만 하고, I/O는 반드시 외부로 뺀다. 워커 수를 넉넉하게 잡는 것도 완화책이지만 근본 해결은 아니다.

---

### 단점 3: Actor 간 트랜잭션이 어렵다

여러 Actor에 걸친 원자적(atomic) 처리가 필요할 때 JobDispatcherNET은 취약하다.

예를 들어 "플레이어 A의 아이템을 플레이어 B에게 이전" 시나리오를 생각해보자.

```csharp
// 이런 코드는 원자성이 없다!
actorA.DoAsync(() => { actorA.RemoveItem(itemId); });   // A에서 제거
actorB.DoAsync(() => { actorB.AddItem(item); });        // B에 추가
```

두 작업 사이에 서버가 크래시하면 아이템이 사라진다. 둘 다 성공하거나 둘 다 실패해야 하는 "트랜잭션"을 순수 Actor 모델로 구현하기는 매우 복잡하다.

데이터베이스 트랜잭션이라면 DB 레이어에서 해결할 수 있다. 하지만 인메모리 상태에서의 트랜잭션은 별도의 saga 패턴이나 2PC(2-Phase Commit)를 직접 구현해야 한다.

**현실적인 대처:** 아이템 거래 같은 트랜잭션이 필요한 로직은 단일 "중재자 Actor"(예: ItemTransferActor)를 만들어 거기서 직렬로 처리하거나, DB 트랜잭션에 위임하는 것이 현실적이다.

---

### 단점 4: 큐 지연(Latency)이 누적될 수 있다

Actor 큐에 작업이 계속 쌓이면 처리 지연이 증가한다. 특히 한 Actor에 많은 작업이 몰리는 "핫스팟" 문제가 발생할 수 있다.

```
GameWorld Actor의 큐:
[접속 처리] [퇴장 처리] [방송 요청] [접속 처리] ... (100개 쌓임)
→ 마지막 작업은 앞의 99개가 끝날 때까지 기다려야 함
```

이것은 Actor 모델 전반의 한계이기도 하다. GameWorld처럼 중앙 집중적으로 요청을 받는 Actor가 있으면 자연히 병목이 된다.

**대처 방법:** 핫스팟 Actor는 책임을 여러 Actor로 분산시킨다. 예를 들어 존(Zone)을 여러 개로 나눠 각 Zone이 독립 Actor가 되게 하면 부하를 분산할 수 있다.

---

### 단점 5: 디버깅과 모니터링이 어렵다

전통적인 lock 기반 코드는 스택 트레이스를 보면 어디서 무슨 작업이 실행 중인지 바로 알 수 있다. Actor 기반 코드는 그렇지 않다.

```
// 스택 트레이스 예시
JobDispatcherNET.AsyncExecutable.Flush()
JobDispatcherNET.AsyncExecutable.DoTask()
AdvancedMmorpgServer.NpcActor.<Tick>b__12_0()  ← 여기가 어떤 NPC?
```

람다(익명 메서드)로 작업을 등록하기 때문에 스택 트레이스에 의미 있는 이름이 나오지 않을 수 있다. 어떤 NPC가 어떤 작업을 처리 중인지 파악하기 어렵다.

또한 각 Actor의 큐에 현재 작업이 몇 개 쌓여있는지, 평균 처리 지연이 얼마인지 같은 **큐 상태 모니터링** 기능이 현재 라이브러리에 내장되어 있지 않다. 상용 수준으로 쓰려면 직접 계측(instrumentation) 코드를 추가해야 한다.

```csharp
// 현재 라이브러리에 없는 것들 — 직접 구현해야 함
actor.QueueDepth       // 현재 큐 깊이
actor.AverageWaitTime  // 평균 대기 시간
actor.ProcessedCount   // 처리된 작업 수
```

---

### 단점 6: 학습 비용과 패러다임 전환

lock을 쓰던 개발자가 Actor 모델로 전환하면 초기에 상당한 개념적 혼란을 겪는다.

특히 아래 실수가 반복된다.

```csharp
// "그냥 편하게" 직접 접근하고 싶은 유혹
public int GetHp() => _player.Hp;  // Actor 외부에서 직접 읽기 — 이게 괜찮은가?

// 언제 DoAsync가 필요하고 언제 필요 없는가?
_world.Spatial.FindNearestPlayer(...)  // SpatialIndex는 ConcurrentDictionary → 괜찮음
_player.Hp                             // Volatile.Read 없이 읽어도 되는가? → 경우에 따라 다름
```

"어디까지 직렬화가 필요한가", "이 데이터는 불변인가 가변인가", "이 접근은 Actor 경계를 넘는가"를 항상 의식하며 코딩해야 한다. lock처럼 "쓰면 일단 안전"이라는 단순한 규칙이 없다.

팀 전체가 Actor 사고방식을 내재화하지 않으면 한 명의 실수로 버그가 생긴다. 코드 리뷰 기준도 새로 정립해야 한다.

---

### 단점 7: 메모리 모델의 미묘한 함정

JobDispatcherNET은 `Interlocked`와 `Volatile`로 스레드 안전성을 보장하지만, Actor 경계를 넘는 데이터 읽기는 여전히 조심해야 한다.

```csharp
// NpcActor 밖에서 Npc의 필드를 읽는 경우
var n = na.Npc;
Console.WriteLine($"{n.X}, {n.Y}");  // CPU 캐시 때문에 오래된 값이 보일 수 있다
```

.NET의 메모리 모델은 CPU 캐시 일관성을 완전히 보장하지 않는다. NpcActor의 큐에서 `n.X`가 수정되는 동시에 다른 스레드에서 읽으면 이전 값을 볼 수 있다. 이것은 버그가 아니라 "약한 일관성(weak consistency)"으로, 위치나 HP 같은 빠르게 변하는 값을 브로드캐스트에 쓸 때는 허용 가능하지만, 중요한 게임 로직 판단에는 쓰면 안 된다.

`Volatile.Read`를 쓰면 최소한의 보장은 되지만, Actor 내부에서 처리하는 것이 근본적으로 안전하다.

---

### 단점 8: 분산 서버로의 확장 한계

JobDispatcherNET은 **단일 프로세스 내**의 멀티스레드 문제를 해결하는 라이브러리다. 서버가 여러 머신으로 확장(scale-out)되는 분산 환경에서는 적용되지 않는다.

예를 들어 플레이어가 다른 서버 프로세스에 있는 NPC를 공격하면, `DoAsync`로 해결할 수 없다. 네트워크를 통한 메시지 전달이 필요하고, 이는 별도의 메시지 큐(Kafka, RabbitMQ 등)나 분산 Actor 프레임워크(Orleans, Akka.NET 등)를 도입해야 한다.

```
단일 프로세스 (JobDispatcherNET이 커버하는 범위):
    [플레이어 Actor] → DoAsync → [NPC Actor]  ← 가능

분산 프로세스 (JobDispatcherNET의 범위 밖):
    서버 A의 [플레이어 Actor] → ??? → 서버 B의 [NPC Actor]  ← 별도 해결 필요
```

대규모 MMORPG의 월드 서버, 분산 게이트웨이 구조 등을 구현하려면 JobDispatcherNET만으로는 부족하다.

---

### 어떤 상황에 가장 적합한가

단점들을 종합하면 JobDispatcherNET이 빛나는 상황과 그렇지 않은 상황이 명확해진다.

**적합한 상황:**

- 수천 개의 독립 엔티티(플레이어, NPC)가 각자의 상태를 관리하는 게임 서버
- DB I/O가 적고 대부분의 로직이 인메모리에서 처리되는 서버
- 단일 프로세스 또는 소규모 멀티 프로세스 구성
- 팀이 Actor 패턴에 익숙하거나 학습할 의지가 있는 경우

**적합하지 않은 상황:**

- 모든 요청에 DB 쿼리가 따라오는 CRUD 중심 서버
- `async/await` 기반의 I/O 집약적인 서버 (이 경우 ASP.NET Core + Channel이 더 자연스럽다)
- 아이템 거래, 결제 처리 등 강한 트랜잭션 보장이 필요한 로직이 핵심인 서버
- 수십 대 이상의 서버를 수평 확장해야 하는 대규모 분산 시스템

---

## 마치며

JobDispatcherNET은 게임 서버 개발에서 수십 년간 검증된 Actor 패턴을 .NET에 깔끔하게 이식한 라이브러리다. 핵심은 단 하나다.

> **각 객체가 자신의 큐를 소유하고, 메시지로만 소통한다.**

이 원칙 하나로 lock 없이도 스레드 안전성을 얻을 수 있고, 서로 다른 Actor 간에는 자연스럽게 병렬성을 극대화할 수 있다.

라이브러리 코드는 단 5개 파일, 수백 줄에 불과하다. 하지만 그 위에 채팅 서버, MMORPG 서버 같은 실전 수준의 시스템을 구축할 수 있다.

중요한 것은 개념을 이해하는 것이다. `DoAsync`를 "이 람다를 나의 큐에 넣어줘"로 이해하면, 언제 써야 하고 언제 쓰지 말아야 하는지 자연스럽게 감이 온다.

다음 단계로는 실제로 코드를 실행해보고, NPC 수와 워커 수를 바꿔가며 성능 변화를 관찰하는 것을 권장한다. 직접 손으로 뜯어보는 것만큼 좋은 학습은 없다.

***

*JobDispatcherNET은 C++의 멀티스레드 Actor 디스패처를 .NET으로 이식한 오픈소스 라이브러리다.*
