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
- **오브젝트 풀** — `Job.Rent()/Return` 패턴으로 GC 압력 최소화 (풀 크기 상한 있음)
- **고정밀 타이머** — `Stopwatch` 기반 `TimerQueue`, `DoAsyncAfter`로 지연 실행
- **타이머 hijack 방지** — 타이머 콜백이 ThreadPool이 아닌 워커 스레드에서 실행됨
- **워커 supervisor** — 워커가 죽으면 지수 백오프로 자동 재기동 (한도 내)
- **백프레셔** — `JobOptions.MaxQueueSize`로 actor 큐 한도 + drop 콜백
- **Closure 알로케이션 회피** — `DoAsync<TState>` 오버로드로 hot path GC 압력 최소화
- **세션 순서 보장 헬퍼** — `Sequencer<T>` (multi-producer 환경의 패킷 순서 race 방지)
- **메트릭 / 로깅 추상화** — `JobMetrics.Snapshot()`, `IJobLogger` (`Serilog` 등으로 교체 가능)
- **셧다운 게이트** — `AsyncExecutable.AcceptingWork = false`로 신규 입력 차단


## 클래스 구조

### Core

| 클래스 | 역할 |
|---|---|
| `AsyncExecutable` | 자기만의 작업 큐를 가진 기본 클래스. `DoAsync()`, `DoAsync<TState>()`, `DoAsyncAfter()` |
| `JobDispatcher<T>` | 전용 OS 스레드 N개를 생성/관리. supervisor로 워커 사망 시 자동 재기동 |
| `IRunnable` | 워커 스레드의 메인 루프 인터페이스. `bool Run(CancellationToken)` |
| `Job` / `Job<TState>` | `Action`/`Action<TState>`을 감싸는 작업 단위. 풀링 + 상한 |
| `TimerQueue` | `PriorityQueue` + `Stopwatch` 기반 지연 실행. fire 시 워커 스레드로 라우팅 |
| `ThreadContext` | 스레드별 저장소(`ThreadLocal`). Timer, ExecuterQueue, TickCount |

### 옵션 / 헬퍼 / 관측성

| 클래스 | 역할 |
|---|---|
| `JobOptions` | actor 큐 옵션 (`MaxQueueSize`, `DropPolicy`, `OnDropped` 콜백) |
| `JobDispatcherOptions` | 워커 supervisor 옵션 (`RestartFailedWorkers`, `MaxRestartsPerWorker`, `RestartBackoff`) |
| `Sequencer<T>` | 같은 source의 항목을 도착 순서대로 워커 한 명만 직렬 처리 |
| `IJobLogger` / `JobLog` | 로깅 추상화. 기본 `ConsoleJobLogger`, 상용은 `Serilog` 등으로 교체 |
| `JobMetrics` | 전역 메트릭 스냅샷 (실행/드롭/실패/타이머/풀/워커재기동) |
| `TimerRegistry` | 비-워커 스레드에서 만든 `TimerQueue`를 추적하여 셧다운 시 정리 |


## 빠른 시작

### 1. 가장 단순한 사용

```csharp
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

var dispatcher = new JobDispatcher<GameWorker>(workerCount: 4);
_ = dispatcher.RunWorkerThreadsAsync();

var player = new PlayerActor();
player.TakeDamage(10);  // 어떤 스레드에서든 호출 가능
```

### 2. 상용 서버 권장 패턴

```csharp
// (a) 로깅 추상화 — Console 직접 호출 회피
JobLog.Current = new ConsoleJobLogger { MinLevel = JobLogLevel.Info };
// 상용에서는 Serilog/MEL 어댑터로 교체

// (b) actor 큐 한도 — OOM 방어
public sealed class PlayerActor : AsyncExecutable
{
    public PlayerActor() : base(new JobOptions
    {
        MaxQueueSize = 256,
        DropPolicy = DropPolicy.Reject,
        OnDropped = (actor, _) => JobLog.Warn("플레이어 큐 만원 — 작업 드롭"),
    }) { }

    // (c) closure 회피 — hot path는 DoAsync<TState>
    public void Move(float x, float y)
        => DoAsync<(PlayerActor A, float X, float Y)>(
            static t => t.A.ProcessMove(t.X, t.Y),
            (this, x, y));
}

// (d) 워커 supervisor 명시
var opts = new JobDispatcherOptions
{
    RestartFailedWorkers = true,
    MaxRestartsPerWorker = 5,
    RestartBackoff = TimeSpan.FromSeconds(1),
};
var dispatcher = new JobDispatcher<GameWorker>(8, opts);

// (e) 메트릭 노출
var m = JobMetrics.Snapshot();
Console.WriteLine($"실행={m.TotalJobsExecuted} 드롭={m.TotalJobsDropped} 워커재기동={m.WorkerRestarts}");

// (f) 셧다운 시퀀스
AsyncExecutable.AcceptingWork = false;   // 신규 입력 차단
world.Stop();                              // 잔여 작업 drain
dispatcher.Dispose();                      // 워커 정지 + Join
TimerRegistry.DisposeAll();                // 비-워커 timer 정리
```

### 3. IO 스레드와 워커 스레드 분리 (Sequencer)

네트워크 IO 스레드가 actor를 직접 호출하면, 그 actor의 Flush가 IO 스레드에서 실행되는 hijack이 발생합니다. `Sequencer<T>` + 공용 inbound 큐 패턴으로 해결합니다.

```csharp
public static readonly ConcurrentQueue<Action> InboundCommands = new();

// 세션 생성 시
var packetSequencer = new Sequencer<string>(
    handler: line => PacketHandler.Handle(server, session, line),
    scheduleDrain: drain => InboundCommands.Enqueue(drain));

// IO 스레드 (RecvLoop): 패킷을 push만
foreach (var line in receivedLines)
    packetSequencer.Enqueue(line);

// 워커 스레드 (IRunnable.Run): InboundCommands 드레인
if (InboundCommands.TryDequeue(out var cmd))
    cmd();
```

→ 같은 세션의 패킷은 항상 한 워커가 도착 순서대로 처리, actor의 leader는 항상 워커 스레드.


## 예제 프로젝트

| 프로젝트 | 설명 |
|---|---|
| `ExampleConsoleApp` | 기본 사용법, 워커 스레드, 데이터 처리 |
| `ExampleChatServer` | 멀티 채팅방 서버 — Room별 AsyncExecutable |
| `ExampleMmorpgServer` | MMORPG 서버 — 플레이어 Actor 패턴, 단일 존 병렬 처리 |
| `ExampleSectorServer` | 섹터 기반 MMORPG — NxN 섹터 분할, 경계 통과 핸드오프 |
| `AdvancedMmorpgServer` | **상용 권장 패턴 종합** — JobOptions / Sequencer / JobLog / JobMetrics / supervisor 모두 사용 |
| `AdvancedMmorpgClient` | MonoGame 기반 봇/뷰어 클라이언트 (`AdvancedMmorpgServer` 동작 시각화) |

```bash
dotnet run --project AdvancedMmorpgServer
# 콘솔 명령: status (게임 상태) / metrics (라이브러리 메트릭) / q (종료)
```


## 라이브러리 사용 패턴 비교

| 항목 | 단순 | 상용 권장 |
|---|---|---|
| 큐 크기 | unbounded | `JobOptions.MaxQueueSize` |
| Hot path | `DoAsync(() => Process(args))` | `DoAsync<TState>(static t => ..., state)` |
| 로깅 | `Console.WriteLine` | `JobLog` (상용 어댑터로 교체) |
| 셧다운 | `dispatcher.Dispose()` | `AcceptingWork=false → Stop → Dispose → TimerRegistry.DisposeAll` |
| 워커 supervisor | 기본 | `JobDispatcherOptions` 명시 |
| 패킷 순서 (multi-producer) | race 가능 | `Sequencer<T>` |
| 외부 read | 컬렉션 직접 노출 | DoAsync + `ManualResetEventSlim` 차단 스냅샷 |
| 메트릭 | 없음 | `JobMetrics.Snapshot()` |


## 문서

[docs/architecture.html](docs/architecture.html) — 인터랙티브 SVG 애니메이션으로 아키텍처를 시각화한 가이드

- DoTask 내부 동작 (3가지 시나리오별 단계 애니메이션)
- 작업 흐름, 타이머, 실전 패턴 (MMORPG)


## 빌드

```bash
dotnet build All.sln
```

- 모든 프로젝트(라이브러리 + 예제): .NET 10
