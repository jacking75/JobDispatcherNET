# Chapter 13: 실전 패턴과 모범 사례

## 13.1 핵심 설계 원칙

```
╔══════════════════════════════════════════════════════════════╗
║     JobDispatcherNET 설계의 황금 원칙                        ║
╠══════════════════════════════════════════════════════════════╣
║                                                              ║
║  1. Actor 내부 상태는 자기 큐에서만 읽고 쓴다                ║
║  2. 외부에서는 DoAsync로 메시지만 보낸다                     ║
║  3. 읽기도 큐를 통과시킨다 (Ask / AskSync)                   ║
║  4. 스레드 간 전달 데이터는 불변(readonly)으로 만든다        ║
║  5. hot path는 DoAsync<TState>로 클로저를 없앤다             ║
║  6. 큐 크기는 항상 제한하고, 반환값 false 를 처리한다        ║
║  7. 주기 작업은 DoAsyncEvery 로 만들고 핸들을 보관한다       ║
║  8. 셧다운은 system.StopAsync 한 번으로 한다                 ║
║                                                              ║
╚══════════════════════════════════════════════════════════════╝
```

---

## 13.2 패턴 1: Handle/Process 분리

```csharp
// ✅ 권장
public class Player : AsyncExecutable
{
    // 외부 진입점 — 큐에 넣기만
    public bool TakeDamage(int damage)
        => DoAsync(static t => t.Self.ProcessTakeDamage(t.Damage), (Self: this, Damage: damage));

    // 실제 처리 — 직렬 실행 보장
    private void ProcessTakeDamage(int damage)
    {
        _hp -= damage;
        if (_hp <= 0) ProcessDie();
    }
}

// ❌ 피해야 할 패턴
public class Player : AsyncExecutable
{
    // 외부 진입점과 실제 처리가 섞임
    public void TakeDamage(int damage)
    {
        DoAsync(() => {
            _hp -= damage;
            // ... 긴 처리 로직이 람다 안에 모두 들어감
        });
    }
}
```

분리의 이점:

```
grep ProcessTakeDamage → 실제 처리 로직 즉시 찾기
디버거 스택: TakeDamage → ProcessTakeDamage (명확!)
단위 테스트: ProcessTakeDamage()를 직접 테스트 가능
static 람다로 바꾸기 쉬움 → 클로저 할당 제거
```

---

## 13.3 패턴 2: Ask / AskSync — 안전한 외부 읽기

```csharp
public class Zone : AsyncExecutable
{
    private readonly List<Player> _players = new();

    // ✅ async 호출자 — Task 로 받는다
    public Task<ZoneSnapshot> GetSnapshotAsync()
        => Ask(() => new ZoneSnapshot(_players.Select(p => p.ToSnapshot()).ToList()));

    // ✅ 동기 호출자(콘솔 명령, 헬스 프로브, Main) — 타임아웃 필수
    public ZoneSnapshot GetSnapshot()
        => AskSync(() => new ZoneSnapshot(_players.Select(p => p.ToSnapshot()).ToList()),
                   TimeSpan.FromSeconds(2));

    // ❌ 위험한 읽기 — 외부 스레드와 Race Condition!
    public int PlayerCount => _players.Count;
}
```

```
Ask 가 손수 만든 TaskCompletionSource 보다 나은 점
──────────────────────────────────────────────────────────
· 핸들러가 예외를 던지면 Task 가 그 예외로 실패한다
· 큐에 못 들어가면 JobRejectedException 으로 실패한다
  (직접 만든 TCS 는 이 경우 영원히 완료되지 않는다)
· AskSync 는 타임아웃이 필수이고, actor 안에서 호출하면 예외를 던진다
```

주의사항:

```
AskSync 는 반드시 Actor 큐 밖에서 호출!

✅ 메인 스레드 / 콘솔 명령 스레드에서 호출
✅ 별도 통계 수집 스레드에서 호출
❌ 다른 Actor의 큐 안에서 호출 → InvalidOperationException
   (JobSystemOptions.DetectBlockingWaitOnWorker 가 잡아 준다)
```

---

## 13.4 패턴 3: Actor→Actor 메시지 패싱

```csharp
// ✅ 올바른 패턴
public class Server : AsyncExecutable
{
    private void ProcessUserChat(string userId, string content)
    {
        if (!_rooms.TryGetValue(_userRoom[userId], out var room))
            return;

        // Room Actor에게 메시지 패싱 (즉시 반환!)
        room.BroadcastChat(userId, content);
        // room의 DoAsync가 내부적으로 호출됨
    }
}

// ❌ 피해야 할 패턴
public class Server : AsyncExecutable
{
    private void ProcessUserChat(string userId, string content)
    {
        // Room의 내부 상태를 직접 접근!
        foreach (var user in room._users)  // ← race condition!
            user.Send(content);
    }
}
```

actor 안에서 다른 actor 의 **결과가 필요하면** 기다리지 말고 콜백을 요청하세요.

```csharp
// ❌ actor 안에서 결과를 기다린다 → 데드락 (가드가 예외를 던진다)
private void ProcessSomething() => _ = room.GetSnapshot();

// ✅ 결과를 내 큐로 보내 달라고 부탁한다
private void ProcessSomething() => room.RequestSnapshot(replyTo: this);
```

---

## 13.5 패턴 4: 주기 작업은 DoAsyncEvery

```csharp
public class SomeActor : AsyncExecutable
{
    private ITimerHandle? _heartbeat;

    public void StartHeartbeat(TimeSpan period)
        => DoAsync(() => _heartbeat = DoAsyncEvery(period, HeartbeatTick));

    private void HeartbeatTick()
    {
        DoWork();     // 재예약 코드가 없다. 예외가 나도 다음 틱이 온다.
    }

    public override async ValueTask DisposeAsync()
    {
        _heartbeat?.Cancel();      // 플래그가 아니라 진짜 취소
        await base.DisposeAsync();
    }
}
```

```
자기복제 패턴(DoAsyncAfter 로 자기 자신 재예약)이 남긴 문제
──────────────────────────────────────────────────────────
① 틱 안에서 예외 한 번 → 재예약 줄에 도달 못 함 → 영구 정지 (13.7 함정 6)
② "끝난 시각 + period" 라서 드리프트가 쌓인다
③ 종료 시 _stopped 플래그로 "발화는 하되 무시" → 드레인이 끝나지 않는다
```

---

## 13.6 패턴 5: 비동기 작업 (DB/Redis/HTTP)

```csharp
public class Account : AsyncExecutable
{
    private int _gold;

    // ✅ actor 큐 안에서 await 한다 — continuation 이 이 actor 의 큐로 돌아온다
    public Task<bool> PurchaseAsync(int itemId, int price)
        => AskAsync(async () =>
        {
            if (_gold < price) return false;         // ① actor 큐 (안전)

            var ok = await _db.ChargeAsync(_id, price);   // ② await

            if (ok) _gold -= price;                  // ③ 다시 actor 큐 (안전)
            return ok;
        });
}
```

```
AsyncReentrancy.Interleaved (기본값)
  await 중에 이 actor 의 다른 작업이 실행된다 → 처리량이 높다.
  ③ 시점의 _gold 는 ① 때와 다를 수 있다는 것을 전제로 코드를 쓸 것.

AsyncReentrancy.Exclusive
  async 작업이 끝날 때까지 actor 가 아무것도 하지 않는다 → 추론이 쉽다.
  단, 느린 await 하나가 actor 전체를 멈춘다.
```

---

## 13.7 흔한 함정과 해결책

### 함정 1: 큐 안에서 느린 작업

```csharp
// ❌ 문제: DoAsync 안에서 Thread.Sleep / 동기 IO
public void DoSlowWork()
    => DoAsync(() =>
    {
        Thread.Sleep(1000);  // ← 큐 블로킹! 다른 작업이 1초 대기
        ProcessWork();
    });

// ✅ 해결 A: 비동기로 (continuation 이 큐로 돌아온다)
public Task DoSlowWorkAsync()
    => RunAsync(async () => { await Task.Delay(1000); ProcessWork(); });

// ✅ 해결 B: 나눠서 예약
public void DoSlowWork()
{
    DoAsync(() => ProcessWorkPart1());
    DoAsyncAfter(TimeSpan.FromSeconds(1), () => ProcessWorkPart2());
}
```

`JobSystemOptions.MaxJobDuration` 을 설정해 두면 이런 작업을 로그로 잡아낼 수 있습니다:

```
[JobDispatcherNET][Warn] Actor 'Npc#42' job ran 137.4ms (limit 50ms)
```

### 함정 2: 큐 안에서 동기 대기 (데드락)

```csharp
// ❌ 문제: 그 작업을 처리해 줄 스레드가 바로 지금 멈춰 있는 이 스레드다
private void ProcessSomething()      // Actor-A 큐 안
{
    var snap = actorB.GetSnapshot();       // AskSync → InvalidOperationException
    var v = actorB.SomethingAsync().Result; // .Result / .Wait() 도 같은 데드락
}

// ✅ 해결: Actor→Actor 메시지 패싱
private void ProcessSomething()
{
    actorB.RequestData(this);   // actorB 가 결과를 이쪽 큐로 DoAsync 해 준다
}
```

`AskSync` 는 이 실수를 예외로 알려주지만, `.Result` / `.Wait()` / `lock` 은
라이브러리가 막을 수 없습니다. 직접 만든 블로킹 API 앞에는
`JobDiagnostics.GuardBlockingWait(system, nameof(MyApi))` 를 넣어 두세요.

### 함정 3: 공유 가변 컬렉션

```csharp
// ❌ 문제: 여러 Actor가 동시 접근
private List<int> _sharedList = new();  // 스레드 안전하지 않음!

// ✅ 해결 방법 1: ConcurrentDictionary/ConcurrentBag (여러 actor 가 정말 공유해야 할 때)
private readonly ConcurrentDictionary<int, Entity> _lookup = new();

// ✅ 해결 방법 2: 읽기도 큐 통과
public Task<List<int>> GetItemsAsync() => Ask(() => new List<int>(_items));
```

### 함정 4: 셧다운 순서

```csharp
// ❌ 잘못된 순서
dispatcher.Dispose();            // 워커 종료
actor.DisposeAsync().Wait();     // 이미 워커 없음 → 큐를 비울 사람이 없다 → 영구 대기!

// ❌ 예전 방식 (v2.0) — 4단계 수동, 전역 static 게이트
AsyncExecutable.AcceptingWork = false;
actor.DisposeAsync().Wait();
dispatcher.Dispose();
TimerRegistry.DisposeAll();      // 지금은 no-op

// ✅ 지금 방식 — 한 줄
_network.Stop();                             // 외부 입력 차단
_world.Stop();                               // despawn + 타이머 Cancel
await system.StopAsync(TimeSpan.FromSeconds(10));   // 드레인 → 타이머 → 워커
```

**`StopAsync` 전에 주기 타이머를 취소해야 합니다.** 취소하지 않으면 타이머가 계속 새 작업을
만들어 내므로 드레인이 타임아웃까지 갑니다.

### 함정 5: DoAsync 의 반환값 무시 ★

```csharp
// ❌ 문제: 큐가 만원이어도, 셧다운 중이어도 그냥 지나간다
public void OnPacket(Packet p) => _actor.DoAsync(() => Handle(p));
//                                ↑ bool 반환값을 버리고 있다

// ✅ 해결: 거부를 하나의 정상적인 결과로 다룬다
public void OnPacket(Packet p)
{
    if (!_actor.DoAsync(static t => t.Self.Handle(t.P), (Self: this, P: p)))
    {
        // 클라이언트에게 백프레셔를 알리거나, 세션을 끊거나, 카운터를 올린다
        _session.SendBusy();
    }
}
```

`MaxQueueSize` 를 설정해 놓고 반환값을 무시하면, "메모리는 지켰지만 패킷이 조용히
사라지는" 서버가 됩니다. **어느 쪽이든 눈에 보이게 만드세요** — `OnDropped` 콜백과
`TotalJobsDropped` 메트릭이 그래서 있습니다.

### 함정 6: 자기복제 타이머 체인이 예외 하나로 죽는다 ★

```csharp
// ❌ 문제
private void Tick()
{
    UpdateAi();                              // ← 여기서 예외
    DoAsyncAfter(_interval, Tick);           // ← 도달하지 못함 → 이 NPC 영구 정지
}

// ✅ 해결: DoAsyncEvery
_tickTimer = DoAsyncEvery(_interval, Tick);  // 예외가 나도 다음 틱이 온다
```

굳이 자기복제를 유지해야 한다면 최소한 `try/finally` 로 재예약을 보장하세요. 하지만
`DoAsyncEvery` 가 드리프트까지 해결해 주므로 그럴 이유가 거의 없습니다.

### 함정 7: 비-워커 스레드가 actor 를 hijack 한다 ★

```csharp
// 소켓 수신 스레드에서
void OnPacketReceived(Packet p) => _world.HandleMove(p.Id, p.X, p.Y);
```

`_world` 가 idle 상태였다면, **이 소켓 스레드가 월드의 leader 가 되어 게임 로직 전체를
그 자리에서 실행합니다.** 그동안 이 소켓의 수신은 멈춥니다.

```csharp
// ✅ 해결 A: 그 actor 를 Scheduled 모드로
new JobOptions { Mode = ExecutionMode.Scheduled, System = system }

// ✅ 해결 B: 워커에 명시적으로 넘긴다
system.Post(() => _world.HandleMove(p.Id, p.X, p.Y));

// ✅ 해결 C: 세션 단위 순서 보장까지 필요하면 Sequencer
_sequencer = new Sequencer<Packet>(system, handler: HandleOnePacket);
```

```
언제 ExecutionMode.Scheduled 를 쓰나
──────────────────────────────────────────────────────────
쓴다:   소켓 IO 스레드 / ThreadPool continuation / ASP.NET 요청 스레드 /
        콘솔·관리 스레드가 직접 찌르는 actor (보통 월드·존·서버 같은 상위 actor)
안 쓴다: 워커 안에서 actor → actor 로만 불리는 actor
        (LeaderFlush 가 지연이 가장 짧다. Scheduled 로 두면 ready 큐를
         한 번 경유하는 비용만 늘어난다)
```

### 함정 8: interleaved async 작업에서 ConfigureAwait(false) ★

```csharp
// ❌ 문제
public Task SaveAsync()
    => RunAsync(async () =>
    {
        await _db.WriteAsync(_state).ConfigureAwait(false);   // ← actor 를 떠난다
        _lastSavedAt = DateTime.UtcNow;   // ThreadPool 스레드에서 actor 상태를 건드린다!
    });

// ✅ 해결: actor 작업 안에서는 ConfigureAwait(false) 를 쓰지 않는다
public Task SaveAsync()
    => RunAsync(async () =>
    {
        await _db.WriteAsync(_state);     // continuation 이 이 actor 의 큐로 돌아온다
        _lastSavedAt = DateTime.UtcNow;   // 안전
    });
```

`AsyncReentrancy.Interleaved`(기본값)는 actor 전용 `SynchronizationContext` 로 continuation 을
큐에 되돌립니다. `ConfigureAwait(false)` 는 정확히 그 장치를 끄는 스위치입니다.
라이브러리 코드에서는 옳은 습관이지만 **actor 작업 본문 안에서는 정반대**입니다.
(`AsyncReentrancy.Exclusive` 를 쓰면 actor 가 통째로 멈춰 있으므로 continuation 이 어디서
돌든 안전합니다.)

---

## 13.8 성능 체크리스트

```
□ hot path에 DoAsync<TState> 사용 (초당 수천 번 이상 호출 시)
□ MaxQueueSize 설정 (모든 Actor에) + 반환값 false 처리
□ Job.MaxPoolSize 튜닝 (최대 동시 작업 수 기준)
□ 주기 작업은 DoAsyncEvery + initialDelay 로 분산
□ 네트워크/ThreadPool 진입점 actor 는 ExecutionMode.Scheduled
□ 한 actor 가 워커를 독점하면 JobOptions.MaxJobsPerFlush 설정
□ 사용자 루프가 필요 없으면 비제네릭 JobDispatcher (Thread.Sleep(1) 폴링 제거)
□ AsyncExecutable.MaxFlushSpinIterations 튜닝
  (producer 의 CAS~Enqueue 창을 얼마나 스핀으로 견딜지)
□ system.Metrics.Snapshot()으로 정기 메트릭 수집
□ 지연 분포가 필요하면 EnableDetailedMetrics (기본 off — 비용 있음)
□ IJobLogger를 상용 로거(Serilog 등)로 교체
```

---

## 13.9 운영 모니터링 체크리스트

```
정기적으로 확인할 메트릭 (system.Metrics.Snapshot() 또는 meter "JobDispatcherNET")
──────────────────────────────────────────────────────────────────────────
□ TotalJobsDropped 증가   → 큐 만원/셧다운/faulted 중 무엇인지 OnDropped 로 구분
                            (jobdispatcher.jobs.dropped)
□ TotalJobsFailed 증가    → 예외 로그 확인 (jobdispatcher.jobs.failed)
□ ActorsFaulted > 0       → MaxConsecutiveFailures 를 넘긴 actor 존재.
                            원인 조사 후 ClearFault() (jobdispatcher.actors.faulted)
□ WorkerRestarts 증가     → 워커 크래시. 로그의 "while running actor 'X'" 를 볼 것
                            (jobdispatcher.worker.restarts)
□ LiveWorkers < 설정값     → 영구 정지된 슬롯 존재. RestartCountResetAfter 확인
                            (jobdispatcher.workers.live)
□ ReadyQueueDepth 상승    → 워커 부족 또는 특정 actor 독점 (jobdispatcher.ready.depth)
□ InFlightJobs 상승       → 유입 > 처리. 병목 actor 를 RemainingTaskCount 로 찾는다
                            (jobdispatcher.jobs.inflight)
□ PendingTimerJobs 상승   → 타이머 예약이 쌓인다. 취소를 빼먹은 곳이 있는지 확인
                            (jobdispatcher.timers.pending)
□ TimersDiscarded 급증    → 종료 시 살아 있던 예약이 많다 = Despawn 에서 Cancel 누락
                            (jobdispatcher.timers.discarded)
□ ActiveJobPoolSize 급감  → 풀보다 in-flight 가 많다. Job.MaxPoolSize 상향 검토
                            (jobdispatcher.pool.size)
□ (상세) job.duration p99 → 느린 작업. MaxJobDuration 경고 로그와 함께 본다
□ (상세) timer.lag p99    → 타이머 정확도. TimerPrecision 조정 판단 근거
──────────────────────────────────────────────────────────────────────────
dotnet-counters monitor --process-id <PID> --counters JobDispatcherNET
```

---

## 13.10 언제 JobDispatcherNET이 적합한가?

```
✅ 잘 맞는 경우:
  - 게임 서버 (특히 MMORPG, 실시간 멀티플레이어)
  - 플레이어/엔티티 단위의 독립적 상태 관리
  - lock 없는 고성능 서버가 필요할 때
  - Actor 모델로 설계하는 시스템
  - 전용 OS 스레드 위의 결정적인 실행이 중요한 워크로드

⚠️ 주의가 필요한 경우:
  - 단순 request-response 서버 (ASP.NET Core가 더 적합)
  - 주로 I/O 바운드 작업 (Task/await가 더 적합)
  - Actor 경계를 명확히 나누기 어려운 복잡한 트랜잭션

❌ 맞지 않는 경우:
  - 분산 시스템 (다른 프로세스 간 Actor → Orleans, Akka.NET)
  - 데이터베이스 트랜잭션 중심 로직
  - 단일 스레드로 충분한 간단한 앱
```

---

## 13.11 아키텍처 결정 참고 표

```
상황                          권장 도구
────────────────────────────  ──────────────────────────────────
플레이어 상태 관리            PlayerActor (AsyncExecutable)
NPC AI 관리                   NpcActor + DoAsyncEvery
방/존 관리                    Room/Zone Actor (+ ExecutionMode.Scheduled)
패킷 수신 순서 보장           Sequencer<T>(system, handler)
워커 풀 (사용자 루프 없음)     JobDispatcher (비제네릭)
전용 게임 루프 스레드          IRunnable + JobDispatcher<T>
IO 스레드 → 워커 전달          JobSystem.Post(Action)
지연 실행                     DoAsyncAfter → ITimerHandle
주기 실행                     DoAsyncEvery → ITimerHandle
외부 상태 읽기 (async)         Ask<TResult>
외부 상태 읽기 (동기)          AskSync<TResult>(func, timeout)
DB/HTTP 접근                  RunAsync / AskAsync
큐 폭주 방어                  JobOptions.MaxQueueSize + OnDropped
한 actor 의 워커 독점 방지     JobOptions.MaxJobsPerFlush
폭주 actor 격리               JobOptions.MaxConsecutiveFailures
actor 단위 오류 처리           protected override OnJobError
워커 자동 복구                JobDispatcherOptions.RestartFailedWorkers
프로세스 내 격리된 풀 2개      JobSystem 인스턴스 2개
우아한 종료                   system.StopAsync(drainTimeout)
서버 간 큐 (분산)             → RabbitMQ, Redis 등 외부 솔루션
```

---

## 13.12 최종 정리 — 한눈에 보는 JobDispatcherNET

```
JobDispatcherNET 핵심 컴포넌트
═══════════════════════════════════════════════════════════

JobSystem ─── 워커·타이머·메트릭·셧다운 게이트의 소유자
  Default             → 암묵적 프로세스 기본 시스템
  Post(Action)        → 워커에서 Action 실행
  StopAsync(timeout)  → 드레인 → 타이머 정지 → 워커 정지
  DrainAsync(timeout) → 정적 상태가 될 때까지 대기
  Metrics             → 이 시스템의 카운터
  AcceptingWork       → 셧다운 게이트

AsyncExecutable ─── 모든 Actor의 기반
  DoAsync()           → 작업 등록 (bool 반환 — 반드시 확인)
  DoAsync<T>()        → 클로저 없는 최적화 버전
  DoAsyncAfter()      → 지연 실행 → ITimerHandle
  DoAsyncEvery()      → 주기 실행 → ITimerHandle
  Ask() / AskSync()   → 결과 회수
  RunAsync() / AskAsync() → await 하는 작업
  OnJobError()        → actor 단위 오류 처리 (override)
  IsFaulted / ClearFault() → 연속 실패 격리
  DisposeAsync()      → 이 actor 만 드레인 후 종료

JobDispatcher ─── 전용 OS 스레드 N개 (사용자 루프 없음)
  RunWorkerThreadsAsync() → 워커 시작 (1회만)
  TryStop(timeout)        → 종료 + 성공 여부 반환
  (내장) 수퍼바이저       → 크래시 자동 재기동 + 예산 회복

JobDispatcher<T> / IRunnable ─── 자체 루프가 필요할 때
  Run(CancellationToken) → true:계속 / false:종료

Sequencer<T> ─── IO 스레드 → 워커 순서 보장
  Sequencer(system, handler, onError)
  Enqueue(T) → bool     → 거부되면 false
  Stop()                → 새 항목만 거부 (받은 것은 전부 처리)
  Abort()               → 남은 항목까지 폐기, 버린 수 반환

ITimerHandle ─── 취소 가능한 타이머
  Cancel() / IsPending

JobOptions ─── Actor 설정
  Name / System / MaxQueueSize / DropPolicy / OnDropped(actor, reason)
  Mode(LeaderFlush|Scheduled) / MaxJobsPerFlush
  MaxConsecutiveFailures / AsyncReentrancy

JobSystemOptions ─── 시스템 설정
  Name / TimerPrecision / EnableDetailedMetrics / PublishMeter
  DetectBlockingWaitOnWorker / MaxJobDuration / Logger

JobMetrics ─── 운영 메트릭 (system.Metrics)
  Snapshot() → 처리/거부/실패/타이머/워커/큐 깊이
  meter "JobDispatcherNET" 로 자동 노출

JobDiagnostics ─── 데드락 가드
  GuardBlockingWait / IsInsideActorJob / CurrentActor / IsWorkerThread

IJobLogger ─── 로깅
  ConsoleJobLogger (기본) / NullJobLogger / 커스텀
═══════════════════════════════════════════════════════════
```

---

## 13.13 마무리

JobDispatcherNET은 "락 없는 게임 서버"를 향한 실용적인 도구입니다.

핵심 아이디어는 단순합니다:

> **각 객체가 자신만의 큐를 가지고, 그 큐에서만 상태를 변경한다.**

이 원칙 하나에서 다음 모든 것이 따라옵니다:
- lock 없는 안전한 상태 관리
- 객체 간 병렬 처리
- 데드락 없는 협업
- 예측 가능한 실행 순서

그리고 v2.1 이 더한 것은 **"그 원칙이 깨지는 지점을 라이브러리가 알려준다"** 입니다.
큐가 만원이면 이유와 함께 알려주고, actor 안에서 블로킹하면 예외를 던지고, 워커가 죽어도
타이머는 살아남고, 종료는 실제로 끝났는지 확인해서 알려줍니다.

이 책에서 배운 패턴들을 실제 프로젝트에 적용하면서, 직접 경험을 쌓아나가시길 바랍니다.

---

```
          감사합니다!
          ────────────────────────────────────────
          이 책의 모든 예제는 F:\github\JobDispatcherNET
          에서 직접 실행해볼 수 있습니다.
          ────────────────────────────────────────
```

---

*[← Chapter 12](./chapter12.md) | [↑ 목차](./README.md)*
