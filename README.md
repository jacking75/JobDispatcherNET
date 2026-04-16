# JobDispatcherNET

Lock 없는 멀티스레드 작업 디스패처. C++ 원본의 .NET 포트.

## 핵심 아이디어

각 객체(`AsyncExecutable`)가 자기만의 작업 큐를 소유합니다.  
외부에서는 큐에 작업을 넣기만 하고, 실행은 한 번에 하나의 스레드만 합니다.  
→ **같은 객체의 작업은 lock 없이 안전하게 직렬화됩니다.**

```
패킷: 플레이어A "이동"  → ActorA.DoAsync(이동) ─┐
패킷: 플레이어B "이동"  → ActorB.DoAsync(이동) ─┼─ 완전 병렬!
패킷: 플레이어C "A공격" → ActorC.DoAsync(스냅샷) → ActorA.DoAsync(데미지)
```


## 주요 특징

- **전용 OS 스레드** — `JobDispatcher`가 `Thread`를 직접 생성하여 `ThreadLocal` 안정성 보장
- **Lock-free 직렬화** — `Interlocked` + `Channel` 기반, 같은 객체 내 작업은 자동 순서 보장
- **오브젝트 풀** — `Job.Rent()/Return` 패턴으로 GC 압력 최소화
- **고정밀 타이머** — `Stopwatch` 기반 `TimerQueue`, `DoAsyncAfter`로 지연 실행
- **커스텀 에러 핸들링** — `AsyncExecutable.OnError` 콜백


## 클래스 구조

| 클래스 | 역할 |
|---|---|
| `AsyncExecutable` | 자기만의 작업 큐를 가진 기본 클래스. `DoAsync()`, `DoAsyncAfter()` |
| `JobDispatcher<T>` | 전용 OS 스레드 N개를 생성하고 관리 |
| `IRunnable` | 워커 스레드의 메인 루프 인터페이스. `bool Run(CancellationToken)` |
| `Job` | `Action`을 감싸는 작업 단위. `ConcurrentBag` 풀링 |
| `TimerQueue` | `PriorityQueue` + `Stopwatch` 기반 지연 실행 |
| `ThreadContext` | 스레드별 저장소 (`ThreadLocal`). Timer, ExecuterQueue, TickCount |


## 빠른 시작

```csharp
// 1. AsyncExecutable을 상속받는 클래스 정의
public class PlayerActor : AsyncExecutable
{
    private int _hp = 100;

    public void TakeDamage(int amount)
    {
        DoAsync(() =>
        {
            _hp -= amount;  // lock 없이 안전!
            Console.WriteLine($"HP: {_hp}");
        });
    }
}

// 2. 워커 구현
public class GameWorker : IRunnable
{
    public bool Run(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        Thread.Sleep(1);
        return true;
    }

    public void Dispose() { }
}

// 3. 실행
var dispatcher = new JobDispatcher<GameWorker>(4);
_ = dispatcher.RunWorkerThreadsAsync();

var player = new PlayerActor();
player.TakeDamage(10);  // 어떤 스레드에서든 호출 가능
```


## 예제 프로젝트

| 프로젝트 | 설명 |
|---|---|
| `ExampleConsoleApp` | 기본 사용법, 워커 스레드, 데이터 처리 |
| `ExampleChatServer` | 멀티 채팅방 서버 — Room별 AsyncExecutable |
| `ExampleMmorpgServer` | MMORPG 서버 — 플레이어 Actor 패턴, 단일 존 병렬 처리 (.NET 10) |

```bash
dotnet run --project ExampleMmorpgServer
```


## 문서

[docs/architecture.html](docs/architecture.html) — 인터랙티브 SVG 애니메이션으로 아키텍처를 시각화한 가이드

- DoTask 내부 동작 (3가지 시나리오별 단계 애니메이션)
- 작업 흐름, 타이머, 실전 패턴 (MMORPG)


## 빌드

```bash
dotnet build All.sln
```

- 라이브러리: .NET 9
- ExampleMmorpgServer: .NET 10
