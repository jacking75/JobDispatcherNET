# JobDispatcherNET 완전 정복
## 초보자를 위한 친절한 가이드

> **대상 독자**: C#을 어느 정도 다룰 줄 알지만, 멀티스레딩과 Actor 패턴이 낯선 개발자
> **목표**: 코드 한 줄 한 줄의 의미를 이해하고, 직접 게임 서버에 적용할 수 있는 수준

---

```
  ╔══════════════════════════════════════════════════════╗
  ║       JobDispatcherNET 완전 정복                     ║
  ║                                                      ║
  ║   "lock 없이도 안전한 게임 서버를 만드는 방법"        ║
  ║                                                      ║
  ╚══════════════════════════════════════════════════════╝
```

---

## 목차

이 책은 개념 이해부터 라이브러리 내부 구조, 서버 예제와 실전 적용까지 총 7개 장으로 구성한다.

| 챕터 | 제목 | 핵심 내용 |
|------|------|-----------|
| [제1장](./chapter01.md) | JobDispatcherNET 입문과 Actor 모델 | 멀티스레딩 문제, Actor 모델, 직렬 실행, 기본 사용법과 메시지 패싱 |
| [제2장](./chapter02.md) | AsyncExecutable 실행 코어와 작업 풀링 | DoAsync, 입장 CAS, Flush, Ask/RunAsync, JobEntry와 오브젝트 풀링 |
| [제3장](./chapter03.md) | 타이머와 워커 스레드의 실행 구조 | TimerService, 지연·주기 실행, ThreadContext, JobDispatcher와 IRunnable |
| [제4장](./chapter04.md) | Sequencer와 설정·모니터링·로깅 | 패킷 순서 보장, IO 스레드 분리, 큐 제한, 오류 격리, 설정과 운영 관찰 |
| [제5장](./chapter05.md) | 콘솔과 채팅 서버 예제 분석 | ExampleConsoleApp의 기본 API부터 ChatServer·Room·User Actor 협업까지 |
| [제6장](./chapter06.md) | MMORPG 서버의 기본 구조와 고급 패턴 | ExampleMmorpgServer와 AdvancedMmorpgServer, 전투·공간 인덱스·NPC AI·세션 처리 |
| [제7장](./chapter07.md) | 실전 패턴과 모범 사례 | 설계 원칙, 안전한 상태 읽기, 비동기 작업, 흔한 함정과 운영 체크리스트 |

---

## 전체 구조 한눈에 보기

```mermaid
graph TD
    A[외부 스레드 / IO 스레드] -->|Sequencer.Enqueue / JobSystem.Post| R[JobSystem ready 큐]
    A -->|DoAsync| B[AsyncExecutable 큐]
    R -->|시그널로 깨어난 워커가 드레인| C[워커 스레드]
    C -->|FlushAsLeader| B
    B -->|Flush| C
    C -->|DoAsync| E[다른 AsyncExecutable 들]
    T[TimerService - 시스템당 스레드 1개] -->|due 시각 도달| B
    B -->|leader 경로| R
```

`JobSystem` 하나가 워커 스레드, ready 큐, 타이머 스레드, 메트릭, 셧다운 게이트를 모두
소유합니다. 명시적으로 만들지 않으면 `JobSystem.Default` 가 쓰입니다.

---

## 빠른 참조 카드

```
┌──────────────────────────────────────────────────────────────────────┐
│  내가 원하는 것                →  사용할 것                          │
├──────────────────────────────────────────────────────────────────────┤
│  객체 내부를 lock 없이          →  AsyncExecutable 상속               │
│  스레드 안전하게 다루고 싶다        + DoAsync (반환 bool 확인!)        │
├──────────────────────────────────────────────────────────────────────┤
│  hot path에서 할당을 없애고 싶다 →  DoAsync<TState>(static 람다, state)│
├──────────────────────────────────────────────────────────────────────┤
│  N ms 후에 한 번 실행           →  DoAsyncAfter(delay, ...)           │
│  나중에 취소도 하고 싶다            → 반환된 ITimerHandle.Cancel()     │
├──────────────────────────────────────────────────────────────────────┤
│  N ms 마다 반복 실행            →  DoAsyncEvery(period, action,       │
│                                     initialDelay)                     │
│                                     (자기 재예약 패턴은 이제 불필요)   │
├──────────────────────────────────────────────────────────────────────┤
│  actor 의 상태를 읽어오고 싶다   →  Ask<T>(func)         (async 호출자)│
│                                    AskSync<T>(func, timeout) (동기)   │
├──────────────────────────────────────────────────────────────────────┤
│  actor 안에서 DB/HTTP await     →  RunAsync / AskAsync                │
│                                    (ConfigureAwait(false) 쓰지 말 것) │
├──────────────────────────────────────────────────────────────────────┤
│  워커 풀을 띄우고 싶다           →  new JobDispatcher(n)              │
│  워커에 자체 루프가 필요하다      →  IRunnable + JobDispatcher<T>      │
├──────────────────────────────────────────────────────────────────────┤
│  IO 스레드가 게임 로직을         →  JobOptions.Mode =                 │
│  실행하지 않게 하고 싶다            ExecutionMode.Scheduled            │
│                                    또는 JobSystem.Post(action)        │
├──────────────────────────────────────────────────────────────────────┤
│  패킷 순서를 보장하고 싶다        →  Sequencer<T>(system, handler)     │
├──────────────────────────────────────────────────────────────────────┤
│  큐 깊이를 제한하고 싶다         →  JobOptions.MaxQueueSize           │
│  거부 사유를 알고 싶다            →  OnDropped(actor, DropReason)      │
├──────────────────────────────────────────────────────────────────────┤
│  계속 터지는 actor 를 격리        →  JobOptions.MaxConsecutiveFailures │
│  actor 단위로 오류를 처리         →  protected override OnJobError     │
├──────────────────────────────────────────────────────────────────────┤
│  메트릭을 보고 싶다              →  system.Metrics.Snapshot()         │
│                                    (전역: JobMetrics.GetSnapshot())   │
│                                    dotnet-counters: JobDispatcherNET  │
├──────────────────────────────────────────────────────────────────────┤
│  서버를 우아하게 종료하고 싶다    →  await system.StopAsync(timeout)   │
│                                    (4단계 수동 종료는 더 이상 불필요)  │
└──────────────────────────────────────────────────────────────────────┘
```
