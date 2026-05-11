# Chapter 13: 실전 패턴과 모범 사례

## 13.1 핵심 설계 원칙

```
╔══════════════════════════════════════════════════════════════╗
║     JobDispatcherNET 설계의 황금 원칙                        ║
╠══════════════════════════════════════════════════════════════╣
║                                                              ║
║  1. Actor 내부 상태는 자기 큐에서만 읽고 쓴다                ║
║  2. 외부에서는 DoAsync로 메시지만 보낸다                     ║
║  3. 읽기도 큐를 통과시킨다 (GetSnapshot 패턴)               ║
║  4. 스레드 간 전달 데이터는 불변(readonly)으로 만든다        ║
║  5. hot path는 DoAsync<TState>로 클로저를 없앤다            ║
║  6. 큐 크기는 항상 제한한다 (MaxQueueSize)                  ║
║  7. 셧다운은 순서대로: 입력차단→drain→워커정지→타이머정리   ║
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
    public void TakeDamage(int damage)
        => DoAsync(() => ProcessTakeDamage(damage));

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
```

---

## 13.3 패턴 2: GetSnapshot — 안전한 외부 읽기

```csharp
public class Zone : AsyncExecutable
{
    private readonly List<Player> _players = new();

    // ✅ 안전한 읽기
    public ZoneSnapshot GetSnapshot()
    {
        using var ev = new ManualResetEventSlim(false);
        ZoneSnapshot? result = null;

        DoAsync(() =>
        {
            result = new ZoneSnapshot(_players.Select(p => p.ToSnapshot()).ToList());
            ev.Set();
        });

        ev.Wait();
        return result!;
    }

    // ❌ 위험한 읽기 — 외부 스레드와 Race Condition!
    public int PlayerCount => _players.Count;
}
```

주의사항:

```
GetSnapshot은 반드시 Actor 큐 밖에서 호출!

✅ 메인 스레드에서 호출
✅ 별도 통계 수집 스레드에서 호출
❌ 다른 Actor의 큐 안에서 호출 → 데드락 위험!
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

---

## 13.5 패턴 4: 자기복제 Heartbeat

```csharp
public class SomeActor : AsyncExecutable
{
    private volatile bool _stopped;  // volatile: 다른 스레드 변경 즉시 인식

    public void StartHeartbeat(TimeSpan period)
        => DoAsync(() => HeartbeatTick(period));

    private void HeartbeatTick(TimeSpan period)
    {
        if (_stopped) return;  // ← 반드시 종료 체크!

        // 실제 작업...
        DoWork();

        // 자기복제
        DoAsyncAfter(period, () => HeartbeatTick(period));
    }

    public override async ValueTask DisposeAsync()
    {
        _stopped = true;
        await base.DisposeAsync();
    }
}
```

---

## 13.6 패턴 5: TaskCompletionSource로 결과 반환

```csharp
public class DataStore : AsyncExecutable
{
    private readonly Dictionary<string, int> _data = new();

    // 비동기 읽기 — 큐를 통과, 결과를 TCS로 반환
    public Task<int?> GetValueAsync(string key)
    {
        var tcs = new TaskCompletionSource<int?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        DoAsync(() =>
        {
            tcs.SetResult(_data.TryGetValue(key, out var v) ? v : null);
        });

        return tcs.Task;
    }
}

// 사용
var value = await dataStore.GetValueAsync("score");
```

---

## 13.7 흔한 함정과 해결책

### 함정 1: 큐 안에서 느린 작업

```csharp
// ❌ 문제: DoAsync 안에서 Thread.Sleep
public void DoSlowWork()
    => DoAsync(() =>
    {
        Thread.Sleep(1000);  // ← 큐 블로킹! 다른 작업이 1초 대기
        ProcessWork();
    });

// ✅ 해결: DoAsyncAfter로 분리
public void DoSlowWork()
{
    DoAsync(() => ProcessWorkPart1());
    DoAsyncAfter(TimeSpan.FromSeconds(1), () => ProcessWorkPart2());
}
```

### 함정 2: 큐 안에서 GetSnapshot 호출

```csharp
// ❌ 문제: 데드락!
private void ProcessSomething()  // Actor-A 큐 안
{
    var snap = actorB.GetSnapshot();  // actorB가 actorA를 기다리면 데드락!
}

// ✅ 해결: Actor→Actor 메시지 패싱으로 처리
private void ProcessSomething()  // Actor-A 큐 안
{
    actorB.RequestData(this);  // actorB가 결과를 actorA에게 DoAsync로 보냄
}
```

### 함정 3: 공유 가변 컬렉션

```csharp
// ❌ 문제: 여러 Actor가 동시 접근
private List<int> _sharedList = new();  // 스레드 안전하지 않음!

public void AddItem(int item)
    => DoAsync(() => _sharedList.Add(item));  // OK
    // 하지만 외부에서 직접 읽으면?

// ✅ 해결 방법 1: ConcurrentBag/ConcurrentDictionary
private ConcurrentBag<int> _items = new();

// ✅ 해결 방법 2: 읽기도 큐 통과
public Task<List<int>> GetItemsAsync()
{
    var tcs = new TaskCompletionSource<List<int>>();
    DoAsync(() => tcs.SetResult(new List<int>(_sharedList)));
    return tcs.Task;
}
```

### 함정 4: Dispose 순서

```csharp
// ❌ 잘못된 순서
dispatcher.Dispose();       // 워커 종료
actor.DisposeAsync().Wait(); // 이미 워커 없음 → Flush 안 됨 → 영구 대기!

// ✅ 올바른 순서
AsyncExecutable.AcceptingWork = false;  // 새 작업 차단
actor.DisposeAsync().Wait();            // 큐 drain (워커가 처리)
dispatcher.Dispose();                   // 워커 종료
TimerRegistry.DisposeAll();             // 타이머 정리
```

---

## 13.8 성능 체크리스트

```
□ hot path에 DoAsync<TState> 사용 (초당 수천 번 이상 호출 시)
□ MaxQueueSize 설정 (모든 Actor에)
□ Job.MaxPoolSize 튜닝 (최대 동시 작업 수 기준)
□ NPC/Entity에 초기 분산 지연 적용
□ AsyncExecutable.MaxFlushSpinIterations 튜닝
  (CPU 사용률 vs 응답성 트레이드오프)
□ JobMetrics.Snapshot()으로 정기 메트릭 수집
□ IJobLogger를 상용 로거(Serilog 등)로 교체
```

---

## 13.9 운영 모니터링 체크리스트

```
정기적으로 확인할 메트릭:
□ TotalJobsDropped > 0 → MaxQueueSize 늘리거나 처리 속도 개선 필요
□ TotalJobsFailed > 0  → 예외 로그 확인 필요
□ WorkerRestarts > 0   → 워커 크래시 원인 조사 필요
□ PendingTimerJobs 증가 → 타이머 처리 지연 (워커 부족?)
□ ActiveJobPoolSize 급증 → 작업 누적 (처리 병목 확인)

LiveWorkerCount 모니터링:
  expected = dispatcher.LiveWorkerCount
  if expected < workerCount:
      // 일부 워커 영구 정지 알림!
```

---

## 13.10 언제 JobDispatcherNET이 적합한가?

```
✅ 잘 맞는 경우:
  - 게임 서버 (특히 MMORPG, 실시간 멀티플레이어)
  - 플레이어/엔티티 단위의 독립적 상태 관리
  - lock 없는 고성능 서버가 필요할 때
  - Actor 모델로 설계하는 시스템
  - ThreadLocal 상태가 중요한 워크로드

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
────────────────────────────  ─────────────────────────────
플레이어 상태 관리            PlayerActor (AsyncExecutable)
NPC AI 관리                   NpcActor (AsyncExecutable)
방/존 관리                    Room/Zone Actor
패킷 수신 순서 보장           Sequencer<T>
전용 게임 루프 스레드          IRunnable + JobDispatcher<T>
지연/주기 실행                DoAsyncAfter + 자기복제
서버 간 큐 (분산)             → RabbitMQ, Redis 등 외부 솔루션
외부 상태 읽기                GetSnapshot 패턴
비동기 결과 반환              TaskCompletionSource
큐 폭주 방어                  JobOptions.MaxQueueSize
워커 자동 복구                JobDispatcherOptions.RestartFailedWorkers
```

---

## 13.12 최종 정리 — 한눈에 보는 JobDispatcherNET

```
JobDispatcherNET 핵심 컴포넌트
═══════════════════════════════════════════════════════════

AsyncExecutable ─── 모든 Actor의 기반
  DoAsync()       → 즉시 실행 등록
  DoAsync<T>()    → 클로저 없는 최적화 버전
  DoAsyncAfter()  → 지연 실행 등록
  DisposeAsync()  → 우아한 큐 drain 후 종료

JobDispatcher<T> ─── 전용 OS 스레드 N개
  RunWorkerThreadsAsync() → 워커 시작
  Dispose()               → 워커 종료
  LiveWorkerCount         → 활성 워커 수
  (내장) 수퍼바이저       → 크래시 자동 재기동

IRunnable ─── 워커 스레드 실행 단위
  Run(CancellationToken) → true:계속 / false:종료
  Dispose()              → 정리

Sequencer<T> ─── IO 스레드 → 워커 순서 보장
  Enqueue(T)  → IO 스레드에서 항목 추가
  Stop()      → 셧다운 표시

TimerQueue ─── 고정밀 지연 실행
  ScheduleTask(owner, delay, job)
  (내부) PeriodicTimer 1ms 폴링

TimerDispatchQueue ─── 타이머→워커 브릿지
  (자동) TimerQueue → JobDispatcher.RunWorker() 드레인

JobOptions ─── Actor 큐 설정
  MaxQueueSize / DropPolicy / OnDropped

JobMetrics ─── 운영 메트릭
  Snapshot() → 처리/거부/실패/타이머 통계

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
