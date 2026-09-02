# JobDispatcherNET

[![NuGet](https://img.shields.io/nuget/v/JobDispatcherNET.svg)](https://www.nuget.org/packages/JobDispatcherNET/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**.NET 게임 서버를 위한 lock 없는 actor 스타일 작업 디스패처.**
각 객체가 자기 작업 큐를 소유합니다. 같은 객체의 작업은 lock 없이 직렬화되고, 서로 다른 객체는
전용 OS 스레드 위에서 완전히 병렬로 돕니다.

English: **[README.md](README.md)** · 전체 가이드: **[Book/](Book/README.md)**

```
패킷: 플레이어A "이동"  → actorA.DoAsync(이동)   ─┐
패킷: 플레이어B "이동"  → actorB.DoAsync(이동)   ─┼─ 완전 병렬
패킷: 플레이어C "A공격" → actorC.DoAsync(스냅샷) → actorA.DoAsync(데미지)
```

---

## 30초 예제

```csharp
using JobDispatcherNET;

public sealed class PlayerActor : AsyncExecutable
{
    private int _hp = 100;   // lock 이 필요 없다

    public void TakeDamage(int amount) =>
        DoAsync(static t => t.Self.Apply(t.Amount), (Self: this, Amount: amount));

    private void Apply(int amount)
    {
        _hp -= amount;                       // 이 안에는 항상 스레드 하나만 들어온다
        if (_hp <= 0) DoAsyncAfter(TimeSpan.FromSeconds(5), Respawn);
    }

    private void Respawn() => _hp = 100;
}

// 전용 OS 워커 스레드 풀 기동
using var dispatcher = new JobDispatcher(workerCount: 8);
_ = dispatcher.RunWorkerThreadsAsync();

var player = new PlayerActor();
player.TakeDamage(10);                       // 어떤 스레드에서 호출해도 안전

// 우아한 종료 — 진행 중인 작업을 모두 흘려보낸 뒤 정지
await JobSystem.Default.StopAsync(TimeSpan.FromSeconds(10));
```

설치:

```bash
dotnet add package JobDispatcherNET
```

---

## 왜 기본 제공 대안이 아니라 이것인가

`MaxDegreeOfParallelism = 1` 인 `ActionBlock<T>` 도 같은 *직렬화* 보장을 줍니다. 많은 경우 그 쪽이
정답입니다. 이 라이브러리가 존재하는 이유는 게임 서버가 그 직렬화와 **동시에** 다른 네 가지를
원하기 때문입니다.

| | JobDispatcherNET | `ActionBlock<T>` | raw `Channel<T>` | Akka.NET | Orleans |
|---|---|---|---|---|---|
| 런타임 의존성 | 없음 | 없음(내장) | 없음(내장) | 여럿 | 여럿 |
| 스레드 | 전용 OS 스레드 | 스레드풀 | 스레드풀 | 전용 풀 | 스레드풀 |
| actor→actor 호출 지연 | 인라인, hop 없음 | 스케줄러 hop | 스케줄러 hop | 메일박스 hop | 메일박스 hop |
| hot path 할당 | 없음 (`DoAsync<TState>` + 풀링) | 클로저 + Task | 클로저 | 메시지 객체 | 메시지 객체 |
| actor 타이머 | 내장, 취소 가능 | 없음 | 없음 | 있음 | 있음 |
| 백프레셔 | actor별 한도 + drop 콜백 | bounded capacity | bounded | 메일박스 | 해당 없음 |
| 분산/클러스터링 | **없음** | 없음 | 없음 | 있음 | 있음 |
| 읽어야 할 코드량 | 약 2,000줄 | — | — | 많음 | 많음 |

**이럴 땐 쓰지 마세요:** 프로세스 여러 대에 걸친 actor 가 필요하면 Orleans, 단순 데이터 파이프라인
이면 TPL Dataflow, 거의 모든 작업이 DB `await` 하나뿐이면 스레드풀 기반 설계가 낫습니다(섞어 써야
한다면 `AsyncReentrancy` 로 대응할 수 있습니다).

---

## 제공하는 것

- **lock 없는 직렬화** — 한 actor 의 작업은 겹치지 않으므로 필드에 동기화가 필요 없습니다.
  [동작 원리](docs/concepts.md)
- **전용 OS 스레드** — 스레드풀이 아닌 진짜 스레드라 긴 루프와 스레드별 상태가 안전합니다.
  유휴 워커는 시그널에 블로킹하며, 폴링이 없습니다.
- **할당 없는 hot path** — `DoAsync<TState>(static 람다, state)` + 상한 있는 Job 풀
- **취소 가능·주기 타이머** — `DoAsyncAfter` / `DoAsyncEvery` 가 `ITimerHandle` 을 반환합니다.
  타이머 스레드는 시스템당 하나라 워커가 죽어도 타이머가 함께 사라지지 않습니다.
- **백프레셔** — `MaxQueueSize` + 거부 *사유*까지 알려주는 drop 콜백
- **두 가지 실행 모드** — actor 간 호출에는 인라인(`LeaderFlush`), 소켓/스레드풀 스레드에서 닿는
  actor 에는 `Scheduled` 로 두면 IO 스레드에서 게임 로직이 돌지 않습니다.
- **async/await 지원** — `RunAsync` / `AskAsync`, interleaved·exclusive 재진입 정책 선택.
  continuation 이 actor 큐로 돌아옵니다.
- **요청/응답** — `Ask` 는 `Task<T>` 반환. `AskSync` 는 안전하게 블로킹하되 데드락이 될 위치에서
  호출하면 *예외를 던집니다*.
- **관측성** — `System.Diagnostics.Metrics` 로 카운터/게이지/히스토그램 노출. OpenTelemetry 와
  `dotnet-counters` 가 별도 설정 없이 인식합니다.
- **producer 간 순서 보장** — `Sequencer<T>` 가 한 세션의 패킷을 도착 순서로 처리
- **한 번의 셧다운** — `StopAsync` 가 연쇄 작업까지 포함해 drain 한 뒤 타이머와 워커를 정지

---

## 핵심 타입

| 타입 | 역할 |
|---|---|
| `AsyncExecutable` | actor 베이스 클래스. `DoAsync`, `DoAsync<TState>`, `DoAsyncAfter`, `DoAsyncEvery`, `Ask`, `RunAsync` |
| `JobSystem` | 워커·타이머 스레드·메트릭·셧다운 게이트 소유. `JobSystem.Default` 가 암묵 기본값 |
| `JobDispatcher` | 사용자 루프가 없는 워커 풀 — 워커가 작업이 올 때까지 블로킹 |
| `JobDispatcher<T>` | 각 스레드에서 사용자의 `IRunnable` 루프를 도는 워커 풀 |
| `JobOptions` | actor별 큐 한도, drop 정책, 실행 모드, 공정성, 실패 한도 |
| `Sequencer<T>` | 한 source(세션 패킷)를 도착 순서로 단일 drainer 가 처리 |
| `ITimerHandle` | 예약·주기 타이머 취소 |
| `JobMetrics` | 카운터 및 `JobDispatcherNET` 미터 |
| `JobDiagnostics` | "워커를 막고 actor 를 기다리는" 실수를 hang 이 아니라 예외로 |

---

## 상용 서버 형태

```csharp
// actor 집합당 시스템 하나. 대부분의 서버는 하나면 충분합니다.
var system = new JobSystem(new JobSystemOptions
{
    Name = "game",
    Logger = new MicrosoftLoggerAdapter(logger),   // JobDispatcherNET.Extensions.Logging
    MaxJobDuration = TimeSpan.FromMilliseconds(50),
});

using var dispatcher = new JobDispatcher(8, new JobDispatcherOptions { System = system });
_ = dispatcher.RunWorkerThreadsAsync();

public sealed class PlayerActor : AsyncExecutable
{
    public PlayerActor(Player p, JobSystem system) : base(new JobOptions
    {
        Name    = $"Player#{p.Id}",
        System  = system,
        MaxQueueSize = 256,                       // OOM 방어
        OnDropped = static (actor, reason) => Log.Warn($"{actor.Name} 작업 거부: {reason}"),
        MaxConsecutiveFailures = 10,              // 망가진 actor 격리
    }) { }

    // hot path — static 람다 + 명시적 state 로 클로저 할당 0
    public void Move(float x, float y) =>
        DoAsync(static t => t.Self.ProcessMove(t.X, t.Y), (Self: this, X: x, Y: y));
}

// IO 스레드가 받은 패킷을 순서대로, 워커에서 처리:
var packets = new Sequencer<string>(system, line => PacketHandler.Handle(session, line));
// ...소켓 스레드에서:
packets.Enqueue(line);

// 종료
await system.StopAsync(TimeSpan.FromSeconds(10));
```

ASP.NET Core / Generic Host 라면 `JobDispatcherNET.Extensions.Hosting` 이 배선을 대신합니다.

```csharp
services.AddJobDispatcher(o => o.WorkerCount = 8);
```

---

## 문서

| 문서 | 내용 |
|---|---|
| [Concepts](docs/concepts.md) | **먼저 읽으세요.** 내 작업이 어느 스레드에서 도는가 |
| [Guarantees](docs/guarantees.md) | 순서·가시성·재진입·예외, 그리고 보장하지 *않는* 것 |
| [Timers](docs/timers.md) | 정밀도, 취소, OS 해상도 주의점 |
| [Shutdown](docs/shutdown.md) | drain 시퀀스 |
| [Tuning](docs/tuning.md) | 워커 수, 큐 크기, 메트릭 읽는 법 |
| [Pitfalls](docs/pitfalls.md) | 이 모델에서 hang 으로 이어지는 실수들 |
| [ADR](docs/adr/README.md) | 설계가 이렇게 된 이유 |
| [Benchmarks](docs/benchmarks.md) | 수치 재현 방법 |
| [Book (한국어 13장)](Book/README.md) | 기초부터 훑는 전체 가이드 |

---

## 예제 프로젝트

| 프로젝트 | 설명 |
|---|---|
| `samples/ExampleConsoleApp` | 기본기 — `DoAsync`, `DoAsyncAfter`, 워커 스레드 |
| `samples/ExampleChatServer` | 멀티 채팅방 서버 — Room 하나당 actor 하나 |
| `samples/ExampleMmorpgServer` | 단일 존 MMORPG — 플레이어 actor, 공간 인덱스 |
| `samples/ExampleSectorServer` | 섹터 분할 월드와 경계 통과 핸드오프 |
| `samples/AdvancedMmorpgServer` | **레퍼런스 서버.** 큐 한도 / `Sequencer` / 메트릭 / supervisor / push AOI / 한 번의 셧다운 |
| `samples/AdvancedMmorpgClient` | 서버를 구동시키는 MonoGame 봇·뷰어 클라이언트 |
| `samples/PipelinesServer` | **바이너리 프로토콜 서버** — `System.IO.Pipelines`, 길이 프리픽스 MessagePack 프레이밍, 세션당 스레드 없음 |
| `samples/LoadClient` | 위 서버용 헤드리스 부하 도구. 지연 백분위 리포트, 실패 시 non-zero 종료 |
| `samples/Observability` | Generic Host + OpenTelemetry 메트릭 |

```bash
dotnet run --project samples/AdvancedMmorpgServer      # 25100 포트 리스닝
# 콘솔 명령: status | metrics | q

# 바이너리 프로토콜 서버 + 200 클라이언트 부하:
dotnet run -c Release --project samples/PipelinesServer -- --port 25120 --workers 8
dotnet run -c Release --project samples/LoadClient    -- --port 25120 --clients 200 --duration 20
```

템플릿에서 바로 시작할 수도 있습니다.

```bash
dotnet new install JobDispatcherNET.Templates
dotnet new jobdispatcher-server -n MyGameServer
```

---

## 빌드와 테스트

```bash
dotnet build All.sln
dotnet test JobDispatcherNET.Tests/JobDispatcherNET.Tests.csproj --filter "Category!=Stress"
dotnet run  -c Release --project JobDispatcherNET.Benchmarks -- --filter *
```

**net8.0** / **net10.0** 을 타깃하며 런타임 의존성이 없습니다.

## 기여

[CONTRIBUTING.md](CONTRIBUTING.md) 를 참고하세요. 동시성 관련 변경은 수정 전에 실패하는 회귀
테스트가 필요합니다 — [`RegressionTests.cs`](JobDispatcherNET.Tests/RegressionTests.cs) 가 본보기입니다.

## 감사의 말

액터가 자기 job 큐를 소유하고 producer 가 flush 리더를 선출하는 실행 모델은
C++ [JobDispatcher](https://github.com/ujentus/JobDispatcher) ([ujentus](https://github.com/ujentus)) 의 설계를
따릅니다. 이 저장소의 .NET 코드는 그 설계를 보고 독립적으로 구현한 것이며,
원본 소스를 옮기거나 복사하지 않았습니다.

## 라이선스

[MIT](LICENSE).
