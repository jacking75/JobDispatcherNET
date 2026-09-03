# 후속 코드 리뷰 — 안정성·성능 (A·B·C 반영 이후)

> **구현 상태 (2026-09-04 반영)**: 아래 **S1~S21, S24, S25를 모두 구현**했다. 테스트는 100개에서
> 123개로 늘었고 net8.0/net10.0 양쪽에서 통과한다. 변경 목록은 `CHANGELOG.md`의 Unreleased 절에
> "follow-up review, S*n*" 태그로 붙여 두었다.
>
> **S22(타이머 락·`TimerEntry` 할당)와 S23(공유 제네릭 `[ThreadStatic]` 비용)은 코드를 바꾸지 않았다.**
> 두 항목 모두 이 문서가 "측정 후"로 적어 둔 것이고, 그 판단은 지금도 옳다 — S22의 엔트리 풀링은 재사용된
> 엔트리를 옛 핸들이 취소하지 못하게 세대 번호를 붙여야 하고, S23은 풀의 스레드 로컬 저장 구조를 바꾸는
> 일이다. 둘 다 근거 없이 손댈 만한 크기가 아니다. 대신 **측정 하니스만 넣었다**:
> `JobDispatcherNET.Benchmarks`의 `TimerArmAndCancel`(S22), `JobStateShape`(S23),
> 그리고 결정 근거가 필요한 나머지 두 건 `ActorRingThroughput`(S2), `SequencerThroughput`(S7).
> S2는 이 문서의 권고대로 (a)안을 넣되 `JobOptions.FanOutToWorkers`로 끌 수 있게 했으므로, 링 벤치마크는
> 같은 실행 안에서 전후를 비교할 수 있다.
>
> 두 곳은 이 문서의 제안과 다르게 구현했고, 이유는 각 항목에 적어 두었다:
> **S15**는 1안(진단 예외)과 2안(`AsyncFlowOwner`)이 같은 상황에 서로 다른 결과를 요구하므로, 2안을
> 기본 동작으로 삼고 1안의 예외는 2안이 구제할 수 없는 경우 — 큐에 다른 작업이 남은 Exclusive 액터 —
> 에만 남겼다. **S21**은 결정적인 자동 테스트를 만들 수 없어(프로세스 CPU 측정은 병렬 실행 중인
> 테스트 스위트에서 흔들린다) 코드 리뷰 항목으로만 남긴다.

- **대상**: `JobDispatcherNET/*.cs`, `JobDispatcherNET.Extensions.Hosting`, 커밋 `aa2483d`
  (`review-0.10.0.md`의 A1~A12·B1~B6·C1~C3·C5·C6 반영 직후)
- **작성일**: 2026-09-03
- **방법**: 코어 라이브러리 전체를 처음부터 다시 읽었다. 오늘 바꾼 경로(드레인 핸드셰이크, async 추적, 타이머
  상태 머신, 스레드 로컬 풀, Sequencer 상한)는 새 결함이 있을 확률이 가장 높으므로 그 불변식을 하나씩
  다시 따라갔다. **저장소 코드는 변경하지 않았다.** 아래 재현 절차와 수정 코드는 문서로만 남긴다.
- **검증 상태**: 기존 테스트 100개 통과(net8.0/net10.0). 아래 항목 중 S1·S3·S5·S6·S7은 코드 추적만으로
  결정적이고, S2는 이미 측정된 링 벤치마크(워커 1/4/8에서 9.2~10.4 M/s로 평탄)가 근거다.
- **2차 패스(같은 날)**: "문제가 있다"는 전제로 1차에서 "문제 없음"으로 넘긴 경로를 다시 의심했다. 의심
  5건을 콘솔 프로브로 돌려 **5건 모두 재현**했고, 그중 3건(S15·S16·S17)이 새 결함이다. S15는 오늘 A3가
  만든 회귀다. 프로브 출력은 각 항목에 그대로 옮겼다.
- **3차 패스(같은 날)**: 앞의 두 패스가 본 곳(드레인·상태 머신·풀)이 아니라 **다른 종류**의 결함을 노렸다 —
  남용 경로 중 타이머, 두 `JobSystem` 사이의 격리, 워커가 전멸하는 시나리오, 그리고 오늘 넣은 변경이 만든
  부작용. 의심 4건을 프로브로 돌려 **4건 모두 재현**(S18~S21). 코드 추적만으로 결정적인 관찰 4건(S22~S25)을
  더했다.

## 요약

| ID | 분류 | 심각도 | 요약 |
|---|---|---|---|
| S1 | 안정성 | **Medium–High** | Hosting 확장이 `refuseNewWork: true`로 종료 → 문서화된 "드레인 중 연쇄 작업 허용" 계약과 반대. 종료 시 despawn 연쇄가 `ShuttingDown`으로 버려진다 |
| S2 | 성능 | **Medium–High** | 액터→액터 fan-out이 워커 풀을 전혀 쓰지 않는다. 방송 액터 하나가 100개 액터를 깨우면 100개 전부 같은 스레드에서 순차 실행 — 나머지 워커는 논다 |
| S3 | 성능·지연 | Medium | `Flush`의 스핀이 `SpinWait.SpinOnce()` 기본 동작으로 `Thread.Sleep(1)`(Windows 15.6ms)까지 올라간다. 생산자가 CAS와 enqueue 사이에서 선점되면 리더가 15ms 정지 |
| S4 | 안정성 | Medium | `PendingAsyncJobs`는 `RunAsync`/`AskAsync`만 센다. `DoAsync` 작업 안의 `async void`·미대기 async 람다의 연속 작업은 `AdmitContinuation`으로 상한·Disposed를 우회해 들어오지만 드레인·Dispose가 기다리지 않는다 |
| S5 | 안정성 | Medium | `DisposeAsync`를 동시에 두 번 호출하면 두 번째 `Interlocked.Exchange`가 첫 번째 TCS를 덮어써 첫 호출자가 영원히 대기 |
| S6 | 성능 | Medium–Low | `Job.MaxPoolSize`가 32 미만이면 공유 풀 배치가 한 번도 게시되지 않아 교차 스레드 경로가 매번 할당한다. 오류도 문서도 없이 조용히 풀이 꺼진다 |
| S7 | 성능 | Medium–Low | B1로 무제한 `Sequencer`도 항목마다 `_pending`에 공유 RMW를 하게 됐다. 생산자가 올리고 워커가 내리는 한 줄 — C1이 제거한 것과 같은 모양의 캐시라인 핑퐁 |
| S8 | 성능 | Low | `Sequencer.TryScheduleDrain`이 `_scheduleDrain(Drain)`으로 드레인 예약마다 델리게이트를 할당 |
| S9 | 진단 | Low | `GuardSelfAsk`는 Exclusive 작업의 **첫 await 이후**에는 잡지 못한다(연속 작업이 스레드풀에서 돌아 `CurrentExecuter == null`). `pitfalls.md` 12번이 이를 과장해 서술 |
| S10 | 안정성 | Low | `ExecuteJob`의 `finally`가 던지면 `Flush`의 `finally`는 카운터를 내리지만 Exclusive 서스펜션 상태를 처리하지 않아 예약이 영구 누수. A4 이후 사실상 도달 불가 |
| S11 | 자원 | Low | `AskSync`가 `AsyncWaitHandle`로 대기 → 느린 호출마다 커널 이벤트 하나가 Task가 수집될 때까지 남는다. `Task.WaitAny`면 할당 없이 같은 효과 |
| S12 | 정확성 | Low | `SanitizeName`의 `name[..128]`이 서로게이트 쌍을 자를 수 있어 고아 서로게이트가 이름에 남는다 |
| S13 | 관측성 | Low | 반복 타이머 tick이 `QueueFull`로 거부돼도 `TimersFired`가 증가 |
| S14 | 테스트 | Low | `JobPool<T>.Clear()`가 `Take()`와 경합하면 `_sharedBatchCount`가 음수 → `PoolSize` 음수. 테스트/벤치 헬퍼 경로 |
| **S15** | 안정성 | **High** (A3 회귀, 재현) | Interleaved 액터의 `RunAsync`/`AskAsync` 작업 안에서 `await this.DisposeAsync()`하면 **영원히 hang**. 드레인이 호출자 자신의 `_pendingAsync`를 기다린다. A3 이전에는 동작했다 |
| **S16** | 안정성 | Medium (재현) | async 작업 안에서 `await system.StopAsync(t)`하면 드레인이 호출자 자신을 기다리다 **타임아웃 전체를 소모**하고 `drained=false`를 돌려준다 |
| **S17** | 안정성·관측성 | Medium (재현) | `Ask`/`AskAsync`/`RunAsync` 작업의 예외는 TCS로만 흐르고 `OnJobError`·`TotalJobsFailed`·`MaxConsecutiveFailures`에 **전혀 잡히지 않는다**. 실패한 `Ask`는 오히려 성공으로 집계돼 연속 실패 스트릭을 **리셋**한다 |
| **S18** | 취약점 | Medium (재현) | `DoAsyncAfter`/`DoAsyncEvery`는 **예약 시점에 `MaxQueueSize`를 보지 않는다.** 상한 4인 액터에 타이머 10,000개가 걸린다 — 패킷마다 쿨타임 타이머를 거는 서버에서 세션당 무제한 메모리 |
| **S19** | 정확성 | Medium (재현) | `Scheduled` 판정이 `IsWorkerThread`(시스템 무관)라서 **시스템 B의 액터가 시스템 A의 워커에서 실행**된다. "게임 월드 / 백그라운드 IO 풀" 격리가 첫 교차 호출에서 깨진다 |
| **S20** | 안정성 | Medium (재현) | 마지막 워커가 `MaxRestartsPerWorker`를 넘겨 영구 정지하면 레디 큐에 있던 액터는 **영원히 좌초**된다 — 새 post는 그 뒤에 쌓이기만 하고, `DisposeAsync`는 끝나지 않는다. 그런데 새 액터는 inline으로 멀쩡히 돌아 시스템이 건강해 보인다 |
| **S21** | 성능 | Low–Medium (재현, 오늘 A10(d)가 만듦) | `DrainAsync`가 2ms마다 `PulseAll` → 드레인이 막혀 있는 동안 **유휴 워커 전부를 초당 500번 깨운다.** 8 워커 기준 2초에 CPU 94ms(≈코어의 5%) |
| S22 | 성능 | Low–Medium | 타이머 예약이 시스템당 락 하나에 직렬화되고 `TimerEntry`는 풀링되지 않는다. 초당 수만 건 예약하는 서버에서 락 경합 + gen0 |
| S23 | 성능 | Low | `JobPool<Job<TState>>`의 `[ThreadStatic]`이 참조형 `TState`에서는 공유 제네릭 코드로 컴파일돼 접근마다 제네릭 딕셔너리 조회를 거친다. `DoAsync(static a => …, this)` 관용구가 정확히 이 경우 |
| S24 | 안정성 | Low | `Admit`에서 CAS 성공 뒤 `_queue.Enqueue`가 OOM으로 던지면 카운터만 오른 채 큐는 비어 리더가 영원히 스핀(ADR 0004의 역방향) |
| S25 | 정리 | Low | `ThreadContext.TickCount`·`CurrentSystem`은 매 워커 반복마다 갱신되지만 라이브러리 어디서도 읽지 않는다. `WarnTimerFallbackOnce`는 일시적 워커 공백 뒤 영구히 침묵한다 |

1차 패스에서는 High가 없다고 썼으나, 2차 패스에서 **S15가 High**로 확인됐다. 오늘 고친 A·B·C 경로의 불변식은
마지막 절에 다시 검증한 대로 유지되지만, A3의 "async 작업을 드레인이 기다린다"는 규칙이 **드레인을 요청한
쪽이 그 async 작업 자신일 때**를 고려하지 않았다는 것이 S15·S16의 공통 원인이다.

---

## S1. Hosting 확장의 종료 순서가 라이브러리 계약과 반대 (Medium–High)

**위치**: `JobDispatcherNET.Extensions.Hosting/ServiceCollectionExtensions.cs:169`

```csharp
var drained = await _system.StopAsync(_drainTimeout, refuseNewWork: true).ConfigureAwait(false);
```

`JobSystem.StopAsync`의 문서와 `docs/shutdown.md`는 이렇게 약속한다:

> New work is still accepted while draining, so a job that enqueues follow-up work (an actor telling
> its peers to despawn, say) completes normally. … `refuseNewWork: true`: Pass it when external
> producers must be cut off *before* draining — a crash-stop, a health-check …

그런데 Generic Host 연동 — 이 라이브러리의 **권장 배선** — 은 crash-stop 변형을 기본으로 쓴다. 게이트가
먼저 닫히므로 드레인 중에 실행되는 작업이 다른 액터에 `DoAsync`하면 `TryReserve`가 `ShuttingDown`으로
거부한다. 월드 액터의 "모든 세션에 종료 알림" 같은 연쇄가 첫 홉에서 끊기고, `TotalJobsDropped`만 올라간다.
`Post`(B2)도 같은 게이트를 보므로 `Sequencer` 드레인 예약도 거부된다 — 세션 Sequencer에 남은 마지막
패킷(연결 종료 마커)이 처리되지 않는다.

**왜 지금까지 안 보였나**: 샘플 서버들은 `AddJobDispatcher`를 쓰지 않고 `system.StopAsync(...)`를 직접
부른다. Hosting 경로의 테스트는 없다.

**해결**

1. 기본값을 라이브러리 계약에 맞춘다. 외부 입력 차단은 호스트가 이미 하고 있다(Kestrel은 `StopAsync`
   단계에서 새 연결을 받지 않는다). 옵션으로 열어 둔다.

```csharp
// JobDispatcherBuilderOptions
/// <summary>
/// Close the shutdown gate before draining rather than after. Off by default: the library's
/// shutdown contract is "drain with the gate open, so cascading work completes", and the host
/// has already stopped accepting connections by the time this runs. Turn it on only when work
/// can still arrive from a source the host does not control.
/// </summary>
public bool RefuseNewWorkOnShutdown { get; set; }

// JobSystemHostedService.StopAsync
var drained = await _system.StopAsync(_drainTimeout, _refuseNewWork).ConfigureAwait(false);
```

2. `JobSystemHostedService`에 통합 테스트를 하나 둔다: 액터 A의 작업이 드레인 중 액터 B에 `DoAsync`하는
   구성으로 `IHost.StopAsync()`를 호출해 B의 작업이 실행됐는지, `TotalJobsDropped == 0`인지 확인.

3. `docs/shutdown.md`의 Hosting 절에 이 옵션과 기본값의 이유를 적는다.

---

## S2. 액터→액터 fan-out이 워커 풀을 우회한다 (Medium–High, 성능)

**위치**: `AsyncExecutable.Admit` (671~676행), `ScheduleOrFlush` (743~747행), `RunFlushLoop` (773~777행)

```csharp
if (ThreadContext.CurrentExecuter is not null)
{
    // Already flushing another actor on this thread — queue up instead of recursing.
    ThreadContext.ExecuterQueue.Enqueue(this);
    return true;
}
```

작업 안에서 **유휴 액터**에 post하면 그 액터는 시스템 레디 큐가 아니라 **현재 스레드의** `ExecuterQueue`에
들어가고, 현재 액터의 플러시가 끝난 뒤 같은 스레드가 순차로 처리한다. ADR 0001이 "재귀 대신 큐"로 의도한
설계이고 단일 홉 지연에는 최적이다. 하지만 이 큐는 스레드 로컬이라 **다른 워커가 훔칠 수 없다**.

결과: 존 액터가 100명의 플레이어 액터에 방송하면 100개 액터의 작업이 전부 그 워커 한 개에서 직렬로 돈다.
8 워커 중 7개는 논다. `MaxJobsPerFlush`는 도움이 안 된다 — 그것은 한 액터의 연속 작업 수를 제한할 뿐이고,
`ExecuterQueue`에 쌓인 *다른* 액터들은 어차피 다른 워커에 보이지 않는다. `ExecutionMode.Scheduled`도
"워커 스레드 위의 생산자는 여전히 inline"이라 여기엔 적용되지 않는다.

**근거**: `docs/benchmarks.md`의 링 벤치마크. 64개 링이 서로 독립인데도 워커 1/4/8에서 9.2~10.4 M/s로
평탄하다 — 링 전체가 `Start()`를 부른 스레드에서만 실행되기 때문이다. 게임 서버의 지배적 패턴(방송,
AOI 갱신)이 정확히 이 모양이다.

**해결** — 세 가지 중 (a)를 권한다. 워커 위에서 이미 다른 액터를 플러시 중일 때만 동작이 바뀌므로
비워커 생산자의 LeaderFlush 지연은 그대로다.

(a) **워커가 있으면 두 번째 액터부터는 레디 큐로.** 첫 후속 액터는 지역성을 위해 로컬 큐에 두고, 그
다음부터는 시스템에 넘겨 다른 워커가 병렬로 집게 한다.

```csharp
// Admit, CurrentExecuter != null 분기
if (ThreadContext.CurrentExecuter is not null)
{
    // The first actor this flush makes ready stays local: one hop of latency, no wake-up. Any
    // further ones go to the pool — a broadcast that wakes a hundred actors must not run them
    // all on this thread while seven other workers sit idle.
    if (_system.HasWorkers && ThreadContext.ExecuterQueue.Count > 0)
    {
        _system.Schedule(this);
        return true;
    }
    ThreadContext.ExecuterQueue.Enqueue(this);
    return true;
}
```

`ScheduleOrFlush`도 같은 규칙을 쓴다. `JobOptions`에 `FanOutToWorkers`(기본 true)를 두어 끌 수 있게
하면 단일 홉 지연이 절대적인 워크로드에서 이전 동작을 유지할 수 있다.

(b) `ExecutionMode.Scheduled`의 의미를 "워커 위에서도 항상 레디 큐"로 바꾸는 세 번째 값
`ExecutionMode.AlwaysScheduled` 추가. 액터 단위로 선택 가능하지만 사용자가 알아서 붙여야 한다.

(c) `ExecuterQueue` work-stealing. 가장 일반적이지만 스레드 로컬 큐를 공유 구조로 바꿔야 해 비용이 크다.
(a)로 충분하다.

**측정 계획**: 링 벤치마크(`perf` 하니스의 `Ring`)를 (a) 적용 전후로 워커 1/4/8에서 비교. 기대는 워커 수에
비례한 확장이다. 단일 액터 inline과 `LeaderFlush` fan-out은 변하지 않아야 한다(회귀 감시).

---

## S3. `Flush` 스핀이 `Thread.Sleep(1)`까지 올라간다 (Medium, 지연)

**위치**: `AsyncExecutable.Flush` (884~893행)

```csharp
if (++iterations >= MaxFlushSpinIterations)   // 1000
{
    Thread.Yield();
    iterations = 0;
    spinner = new SpinWait();
}
else
{
    spinner.SpinOnce();      // ← 기본 sleep1Threshold = 20
}
```

이 스핀은 "카운터는 >0인데 큐는 비어 있는" 창, 즉 생산자가 `Interlocked.CompareExchange`(예약)와
`_queue.Enqueue`(게시) **사이에서 선점된** 경우만을 위한 것이다(ADR 0004). 창은 수 ns지만 선점되면 수 µs~ms가
된다. `SpinWait.SpinOnce()`는 20번째 호출부터 `Thread.Sleep(1)`을 섞는데, Windows 기본 타이머 해상도에서
그것은 **15.6ms**다. 1000회 창 안에서 20, 40, 60…번째 반복마다 15ms를 잔다. 리더 — 워커 스레드 — 가
15ms 동안 아무 작업도 못 하고, 그 액터의 뒤 작업 전부와 `ExecuterQueue`에 쌓인 다른 액터들도 함께 멈춘다.

`JobDispatcher.SpinForWork`(C2)는 이미 `sleep1Threshold: -1`을 쓴다. 같은 이유가 여기에 더 강하게 적용된다.

**해결**

```csharp
else
{
    // Never Sleep(1) here: the wait is for a producer between its CAS and its enqueue, a window of
    // nanoseconds unless it was pre-empted. Sleeping a Windows timer tick for that stalls this
    // actor and everything queued behind it on this thread.
    spinner.SpinOnce(sleep1Threshold: -1);
}
```

`SpinOnce(sleep1Threshold: -1)`은 임계 이후 `Thread.Yield()`/`Sleep(0)`만 쓴다. 기존의 1000회마다
`Thread.Yield()`도 그대로 두면 된다.

**테스트**: 결정적 재현은 어렵다. 회귀 감시로 `MaxFlushSpinIterations`를 5로 낮추고 생산자를 CAS 직후에
`Thread.Sleep(50)`시키는 테스트 심(seam)을 넣으면 리더의 대기 시간이 50ms±α여야 하고 65ms 이상이면
`Sleep(1)`이 섞인 것이다. 심을 두기 싫으면 코드 리뷰 항목으로만 남긴다.

---

## S4. `async void`·미대기 async 람다는 드레인에 보이지 않는다 (Medium)

**위치**: `AsyncExecutable.StartAsyncJob` (422·461행)의 `BeginAsyncTracking`, `AdmitContinuation` (729행)

A3은 `RunAsync`/`AskAsync`가 돌려준 Task를 기준으로 `_pendingAsync`를 센다. 그런데 Interleaved 액터는
**모든** 작업에 `ActorSynchronizationContext`를 깔기 때문에(`ExecuteJob` 906행), 다음도 연속 작업을
`AdmitContinuation`으로 액터에 되돌린다:

```csharp
actor.DoAsync(() => HandleAsync());         // async void HandleAsync() { await ...; touch state; }
actor.DoAsync(() => _ = SaveAsync());       // 미대기 async 람다
```

이 연속 작업들은 A2 덕분에 상한과 `Disposed`를 우회해 실행된다(안 그러면 hang). 하지만 `_pendingAsync`에
잡히지 않으므로 `DrainAsync`·`DisposeAsync`는 이들을 기다리지 않는다. 결과:

- `StopAsync`가 드레인 완료를 선언하고 워커를 내린 뒤 연속 작업이 도착 → `HasWorkers == false` →
  스레드풀 스레드에서 **inline 실행**. 액터 코드가 종료된 시스템 위에서 돈다.
- `await actor.DisposeAsync()`가 끝난 뒤(`_completed == 1`) 연속 작업이 도착 → 폐기된 액터의 상태를
  건드린다.

`async void`는 원래 예외도 삼키는 안티패턴이라 "쓰지 말라"가 1차 답이지만, 이벤트 핸들러 서명 때문에
서버 코드에 흔하다.

**해결** — 두 단계.

1. **문서**. `guarantees.md`의 Async jobs 절과 `pitfalls.md`에 명시: "드레인이 기다리는 것은
   `RunAsync`/`AskAsync`뿐이다. 작업 안에서 `async void`를 부르거나 async 람다를 대기하지 않으면 그 연속
   작업은 셈에서 빠진다. `RunAsync`로 감싸라."

2. **선택 — Post 단위 추적**. `ActorSynchronizationContext`가 `OperationStarted`/`OperationCompleted`를
   구현하면 `async void` 메서드는 컴파일러가 이 둘을 호출해 준다(`AsyncVoidMethodBuilder`가 캡처한
   컨텍스트에 통지). 이것을 `_pendingAsync`에 연결하면 최소한 `async void`는 셈에 들어온다.

```csharp
private sealed class ActorSynchronizationContext(AsyncExecutable actor) : SynchronizationContext
{
    public override void OperationStarted() => actor.BeginAsyncTracking();
    public override void OperationCompleted() => actor.EndAsyncTracking();
    // Post/Send는 그대로
}
```

   미대기 async 람다(`_ = SaveAsync()`)는 `AsyncTaskMethodBuilder`라 통지가 없다 — 이쪽은 문서로만
   막을 수 있다. `OperationStarted`는 `async void` 메서드가 **시작되는 스레드의** 컨텍스트를 쓰므로 작업
   안에서 호출된 경우에만 잡힌다는 점도 문서에 적는다.

**재현(2차 패스 프로브)**:

```
Exp5 async void awaiting inside DoAsync: DrainAsync(300ms)=True pendingAsync=0 resumedOnActor=True -> BUG: drain ignored it
```

게이트가 열리기 전인데 드레인은 `true`를 돌려줬고, 게이트가 열린 뒤 연속 작업은 실제로 액터 위에서
실행됐다(`resumedOnActor=True`) — 드레인이 끝났다고 선언한 시스템 위에서다.

**테스트**: 위 프로브를 그대로 `ShutdownTests.DrainWaitsForAsyncVoidContinuations`로. 2번 적용 전에는
`true`(잘못), 적용 후에는 `false`.

---

## S5. `DisposeAsync` 동시 호출 시 첫 호출자가 영원히 대기 (Medium)

**위치**: `AsyncExecutable.DrainThenCompleteAsync` (1036~1058행)

```csharp
var tcs = new TaskCompletionSource(...);
Interlocked.Exchange(ref _drainTcs, tcs);     // 두 번째 호출이 첫 번째의 tcs를 덮어쓴다
if (!IsIdle)
    drained = await AwaitDrainAsync(tcs.Task, ...);
```

`SignalDrainedIfIdle`은 `_drainTcs`에 **마지막으로** 저장된 TCS만 완료시킨다. 세션 액터를 연결 종료 경로와
관리자 kick 경로가 동시에 폐기하면 — 실제 서버에서 드문 일이 아니다 — 먼저 들어온 쪽은 자기 TCS가
덮어써져 `await`에서 돌아오지 못한다. 타임아웃 오버로드를 썼다면 `false`로 빠져나오지만 무한 오버로드는
그대로 hang이다.

**해결** — TCS를 한 번만 만들고 뒤따르는 호출자는 그것을 공유한다.

```csharp
private async ValueTask<bool> DrainThenCompleteAsync(TimeSpan timeout, CancellationToken cancellationToken)
{
    var drained = true;

    if (!IsIdle)
    {
        var tcs = Volatile.Read(ref _drainTcs);
        if (tcs is null)
        {
            var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // CompareExchange rather than Exchange: a concurrent DisposeAsync must join the same
            // wait, not replace it — the signal only ever completes the TCS that is stored here.
            tcs = Interlocked.CompareExchange(ref _drainTcs, fresh, null) ?? fresh;
        }
        else
        {
            // Somebody is already waiting; the full fence below is still needed for the Dekker
            // handshake, and MemoryBarrier is the cheapest way to get one without a store.
            Interlocked.MemoryBarrier();
        }

        if (!IsIdle)
            drained = await AwaitDrainAsync(tcs.Task, timeout, cancellationToken).ConfigureAwait(false);
    }

    Volatile.Write(ref _completed, 1);
    GC.SuppressFinalize(this);
    return drained;
}
```

`CompareExchange`도 full fence이므로 A1의 Dekker 논증은 그대로 성립한다. 한 번 만들어진 TCS는 액터가 다시
바빠졌다가 다시 비어도 재사용된다 — 이미 완료된 TCS의 `TrySetResult`는 no-op이고, `Task`를 await하면
즉시 반환하므로 문제없다(`IsIdle` 재확인이 그 경우를 거른다).

**테스트**: 바쁜 액터(`BlockingActor.BlockAndWait()`)에 `DisposeAsync()`를 두 태스크에서 동시에 호출,
`Release()` 후 **둘 다** 5초 안에 완료돼야 한다. 현재 코드에서는 하나가 타임아웃한다.

---

## S6. `MaxPoolSize < 32`면 공유 풀이 조용히 꺼진다 (Medium–Low)

**위치**: `JobPool<T>.Return`

```csharp
if (Interlocked.Increment(ref _sharedBatchCount) * (long)BatchSize <= MaxPoolSize)   // BatchSize = 32
```

`MaxPoolSize = 8`이면 `1 * 32 <= 8`이 거짓이라 배치가 한 번도 게시되지 않는다. 로컬 스택은 여전히 동작하므로
같은 스레드 rent/recycle은 괜찮지만, 교차 스레드 경로(`Scheduled`, 타이머)는 생산자 쪽이 항상 빈 공유 풀을
보고 **매 작업 할당**한다. 오류도 경고도 없다. 이전 구현에서 `MaxPoolSize = 8`은 "8개까지 풀링"이었으므로
의미가 바뀐 채 문서에만 적혀 있다(`tuning.md`도 32 배수 조건은 말하지 않는다).

**해결** — 상한을 배치 단위로 올림하고 문서에 적는다.

```csharp
// JobPool<T>
private static long SharedCapInBatches
{
    get
    {
        var max = MaxPoolSize;
        return max <= 0 ? 0 : Math.Max(1, (max + BatchSize - 1) / BatchSize);   // 1..32 → 1 batch
    }
}

// Return
if (Interlocked.Increment(ref _sharedBatchCount) <= SharedCapInBatches)
```

`tuning.md`: "MaxPoolSize는 32 단위로 올림된다. 1~32는 모두 배치 하나(32개)를 뜻한다."

**테스트**: `Job.MaxPoolSize = 8`, `Scheduled` 액터에 50,000개 → `Job.PoolSize == 32`(배치 하나)이고
`RentingAndRecyclingOnOneThreadStopsAllocating`과 같은 방식으로 생산자 스레드의 할당량이 0에 가까워야 한다.

---

## S7. 무제한 `Sequencer`가 항목마다 공유 RMW를 하게 됐다 (Medium–Low, B1 회귀)

**위치**: `Sequencer<T>.TryReserveSlot` (155~174행), `Drain` (235행)

```csharp
if (_maxPending == 0)
{
    Interlocked.Increment(ref _pending);     // 생산자(IO 스레드)
    return true;
}
...
finally { Interlocked.Decrement(ref _pending); }   // 소비자(워커)
```

B1 이전의 `Enqueue`는 `ConcurrentQueue.Enqueue` 하나였다. 이제 상한이 없어도 `_pending` 한 줄을 생산자가
올리고 워커가 내린다 — C1이 `Job` 풀에서 제거한 것과 같은 모양의 캐시라인 핑퐁이 세션당 하나씩 생겼다.
`ConcurrentQueue`의 자체 CAS까지 합치면 항목당 공유 쓰기가 두 배다. 상한을 건 세션 Sequencer에는 필요한
비용이지만(백프레셔가 정확해야 한다), 상한 없는 Sequencer — 내부 커맨드 큐 용도 — 는 낼 이유가 없다.

**해결** — 무제한이면 카운터를 아예 건드리지 않고 `PendingCount`만 `_queue.Count`로 돌아간다.

```csharp
public int PendingCount => _maxPending == 0 ? _queue.Count : Volatile.Read(ref _pending);

private bool TryReserveSlot()
{
    if (_maxPending == 0)
        return true;                    // unbounded: no slot to claim, no shared write
    ...
}

// Drain의 finally
if (_maxPending != 0)
    Interlocked.Decrement(ref _pending);

// Abort의 루프도 동일하게 조건부
```

`ConcurrentQueue.Count`가 O(세그먼트)인 것은 무제한 Sequencer의 `PendingCount`가 관측용이라 감수할 수 있다
(B1 이전과 같다). XML 문서에 "상한이 없으면 `PendingCount`는 스냅샷용 근사치"라고 적는다.

**측정**: `perf` 하니스에 "8 생산자 → 무제한 Sequencer 1개, 워커 4" 셀을 추가해 전후 비교. 항목당 수십 ns
차이가 예상되며 상한 있는 셀은 변하지 않아야 한다.

---

## S8. `Sequencer` 드레인 예약마다 델리게이트 할당 (Low)

**위치**: `Sequencer<T>.TryScheduleDrain` (184행)

```csharp
scheduled = _scheduleDrain(Drain);      // 메서드 그룹 → 매번 new Action(Drain)
```

인스턴스 메서드 그룹 변환은 호출마다 델리게이트를 만든다. 드레인 예약은 항목당이 아니라 "큐가 비어 있다가
첫 항목이 들어올 때"마다이므로 규모는 작지만, 세션 수천 개가 초당 몇 번씩이면 gen0에 꾸준히 쌓인다.
B1 이전부터 있던 것이다.

**해결**

```csharp
private readonly Action _drainAction;
// 생성자
_drainAction = Drain;
// TryScheduleDrain
scheduled = _scheduleDrain(_drainAction);
```

---

## S9. `GuardSelfAsk`의 사각지대와 문서 과장 (Low)

**위치**: `AsyncExecutable.GuardSelfAsk` (383~398행), `docs/pitfalls.md` 12번

가드 조건은 `ReferenceEquals(ThreadContext.CurrentExecuter, this)`다. Exclusive 작업이 첫 `await`를 지나면
연속 작업은 `TaskScheduler.Default`에서 돌고 `CurrentExecuter`는 `null`이다. 그래서 다음은 잡지 못한다:

```csharp
RunAsync(async () =>
{
    await Task.Delay(1);          // 여기서 스레드풀로
    await Ask(() => 1);           // CurrentExecuter == null → 가드 통과 → 영원히 대기
});
```

`pitfalls.md` 12번은 "the library throws an `InvalidOperationException` at the `Ask`"라고 단정한다. 첫
await 이전에만 그렇다.

**해결**

1. 문서를 정확하게: "가드는 작업의 **동기 구간**(첫 await 이전)에서만 동작한다. await 뒤의 자기 Ask는 잡지
   못하므로 Exclusive 액터에서는 자기 자신에게 Ask하지 않는 것을 규칙으로 삼아라."
2. 선택 — `[ThreadStatic]`이 아닌 **`AsyncLocal<AsyncExecutable?>`**로 "이 async 흐름을 시작한 액터"를
   전파하면 await 뒤에도 잡을 수 있다. `ExecuteJob`에서 `StartAsyncJob` 경로에만 설정하면 비용은 async
   작업에 한정된다. `AsyncLocal` 쓰기는 `ExecutionContext` 복사를 유발하므로 동기 작업 경로에는 넣지 않는다.

```csharp
private static readonly AsyncLocal<AsyncExecutable?> AsyncFlowOwner = new();

// StartAsyncJob (양쪽 오버로드), fn() 호출 직전
AsyncFlowOwner.Value = this;
try { task = fn() ?? ...; }
finally { AsyncFlowOwner.Value = null; }   // 동기 구간이 끝나면 되돌린다; 캡처된 흐름에는 this가 남는다

// GuardSelfAsk
if (!ReferenceEquals(ThreadContext.CurrentExecuter, this) && !ReferenceEquals(AsyncFlowOwner.Value, this))
    return;
```

---

## S10. `ExecuteJob`의 `finally`가 던지면 Exclusive 예약이 누수 (Low, 이론적)

**위치**: `AsyncExecutable.Flush` (825~858행)

A4에서 `OnJobRetired`/`Decrement`를 `finally`로 옮겼다. 그 `finally`는 예외가 나도 카운터는 맞춰 주지만,
그 뒤의 `_suspendState` 처리는 건너뛴다. 던진 작업이 `StartAsyncJob`이었고 이미 `BeginExclusiveSuspension`
(예약 +1, 상태 Pending)을 마친 상태라면:

- 리더는 예외와 함께 사라진다(`RunFlushLoop`의 catch가 `Schedule`로 리더십을 되돌리려 하지만 카운터가
  예약 때문에 0이 아니므로 워커가 플러시 → **서스펜션 중인 액터를 플러시**한다는 것도 문제다).
- 나중에 async 작업이 끝나 `EndExclusiveSuspension` → CAS Pending→Completed 성공 → "플러셔가 정리할
  것"이라 믿고 반환. 플러셔는 없다. 예약이 영구히 남아 `DisposeAsync`가 끝나지 않는다.

A4 이후 `ExecuteJob`의 `finally`에서 던질 수 있는 것은 `SafeJobLogger`(삼킴)와 `Metrics.RecordJobDuration`
(`Histogram.Record`, 던지지 않음)뿐이라 **현재는 도달 불가**하다. 그러나 이 `finally`에 훅이 하나 추가되는
순간 되살아나는 종류의 결함이라 기록해 둔다.

**해결** — 서스펜션 정리를 `finally` 안으로. Pending이면 이 스레드가 리더이므로 예약을 직접 회수한다.

```csharp
finally
{
    _system.OnJobRetired();
    remaining = Interlocked.Decrement(ref _remainingTaskCount);

    // If ExecuteJob threw past a BeginExclusiveSuspension, nobody will ever park for it. Take
    // the suspension down here, while this thread still owns leadership.
    if (Volatile.Read(ref _suspendState) == SuspendPending
        && Interlocked.CompareExchange(ref _suspendState, SuspendNone, SuspendPending) == SuspendPending)
    {
        _system.OnJobRetired();
        Interlocked.Decrement(ref _remainingTaskCount);
    }
}
```

단, 이 코드는 정상 경로에서도 실행되므로 정상 경로의 `SuspendPending → SuspendParked` 전이보다 **먼저**
`SuspendNone`으로 바꿔 버린다 — 즉 위 코드는 예외 경로에서만 실행되도록 `catch`로 분리해야 한다
(`try { ExecuteJob } catch { …정리…; throw; } finally { 카운터 }`). 그러면 `EndExclusiveSuspension`은
CAS 실패 → Parked 경로로 가서 스스로 정리하고, 남은 예약은 위에서 회수한 것과 합쳐 0이 된다.

---

## S11. `AskSync`의 `AsyncWaitHandle` (Low, 자원)

**위치**: `AsyncExecutable.AskSync` (318행)

`((IAsyncResult)task).AsyncWaitHandle`은 첫 접근에 `ManualResetEvent`를 만들어 Task에 매단다. 이 이벤트는
아무도 `Dispose`하지 않고 Task가 수집될 때 finalizer로 정리된다. 블로킹 API라 빈도가 낮지만, 헬스 프로브가
초당 수십 번 `AskSync`를 부르고 매번 느리면 커널 핸들이 그만큼 finalizer 큐에 쌓인다.

**해결** — `Task.WaitAny`는 실패한 Task의 예외를 **전파하지 않고** 인덱스만 돌려주므로 A7의 목적을 할당
없이 달성한다.

```csharp
var task = Ask(func);
if (!task.IsCompleted && Task.WaitAny(new[] { task }, timeout) < 0)
    throw new TimeoutException(...);
return task.GetAwaiter().GetResult();
```

`new[] { task }` 배열 하나는 힙 할당이지만 이벤트 핸들과는 비교가 안 되며, `[task]` 컬렉션 식으로 스택
할당도 가능하다(.NET 8+ `ReadOnlySpan<Task>` 오버로드).

---

## S12. `SanitizeName`이 서로게이트 쌍을 자를 수 있다 (Low)

**위치**: `AsyncExecutable.SanitizeName` (108행)

`name[..128]`이 127번째가 상위 서로게이트인 문자열을 자르면 고아 서로게이트가 남는다. UTF-8 로그 인코더는
이를 U+FFFD로 바꾸거나(대부분) 예외를 던진다(엄격 설정). 이모지 닉네임에서 실제로 발생한다.

**해결**

```csharp
var cut = maxLength;
if (name.Length > maxLength && char.IsHighSurrogate(name[maxLength - 1]))
    cut--;
var capped = name.Length <= maxLength ? name : name[..cut];
```

`char.IsControl`은 서로게이트를 제어문자로 보지 않으므로 치환 루프는 그대로다.

---

## S13. 거부된 반복 tick도 `TimersFired`에 계수 (Low)

**위치**: `TimerService.DispatchOne` (395행)

```csharp
_system.Metrics.OnTimerFired();
if (!_system.DispatchTimerJob(...) && refusal == DropReason.Disposed) ...
```

`QueueFull`·`Faulted`·`ShuttingDown`으로 거부돼도 "fired"로 센다. one-shot 경로(426행)도 같다.
`tuning.md`의 "`TimersFired` vs `TotalJobsExecuted`: 발화는 오르는데 실행이 안 오른다 → 액터가 안 비운다"
진단이 거부 상황에서는 오독을 낳는다(실은 `TotalJobsDropped`가 원인).

**해결**: `DispatchTimerJob`이 `true`를 돌려줄 때만 `OnTimerFired()`. 거부는 이미 `OnDropped`에 잡힌다.
`timers.md` 메트릭 표의 정의를 "액터가 **수락한** 발화"로 고친다.

---

## S14. `JobPool<T>.Clear()`와 `Take()` 경합 시 음수 `PoolSize` (Low, 테스트 헬퍼)

`Clear()`가 `Exchange(_sharedBatchCount, 0)`한 직후 다른 스레드의 `Take()`가 방금 꺼낸 배치에 대해
`Decrement`하면 -1이 된다. 프로덕션 경로는 `Clear`를 부르지 않는다. 테스트가 병렬로 돌 때 `PoolSize`
단언이 흔들릴 수 있으므로 `SharedSize`를 `Math.Max(0, …)`로 감싸면 끝이다.

---

## S15. async 작업 안에서 `await this.DisposeAsync()`가 영원히 멈춘다 (High, A3 회귀, 재현)

**위치**: `AsyncExecutable.DrainThenCompleteAsync` (1036~1058행), `IsIdle` (141행),
`StartAsyncJob`의 `BeginAsyncTracking` 호출 시점 (422·461행)

세션 액터의 "저장하고 자신을 정리한다"는 흔한 모양이다:

```csharp
session.RunAsync(async () =>
{
    await SaveAsync();
    await DisposeAsync();      // ← 여기서 영원히 멈춘다
});
```

순서를 따라가면:

1. `fn()`의 동기 구간이 `DisposeAsync()`를 부른다. `IsIdle`? `_remainingTaskCount == 1`(이 작업 자신) →
   아니다 → `_drainTcs` 발행 → `AwaitDrainAsync` 대기 → 미완료 `ValueTask` 반환.
2. `fn()`이 그것을 await하고 미완료 Task를 돌려준다 → `StartAsyncJob`이 **이제** `BeginAsyncTracking()` →
   `_pendingAsync = 1`.
3. `Flush`가 카운터를 0으로 내리고 `SignalDrainedIfIdle()` → `_remainingTaskCount == 0`이지만
   `_pendingAsync == 1` → **신호하지 않는다**.
4. `_pendingAsync`는 async 작업이 끝나야 0이 되고, async 작업은 `_drainTcs`를 기다린다. **순환 대기.**

A3 이전에는 `IsIdle`이 `_remainingTaskCount`만 봤으므로 3단계에서 신호가 나가 정상 완료됐다. 즉 오늘 도입된
회귀다. `DisposeAsync(TimeSpan)` 오버로드면 타임아웃 뒤 `false`로 빠져나오지만, 무한 오버로드 —
`await using`과 `IAsyncDisposable` 경로 — 는 조용히 멈춘다. `GuardSelfAsk`(B6)는 `DisposeAsync`를 보지
않는다.

Exclusive 액터는 A3 이전에도 같은 이유(예약이 카운터를 1 이상으로 유지)로 멈췄다. Interleaved가 새로
합류한 것이다.

**재현(프로브)**:

```
Exp1  self-DisposeAsync inside RunAsync (Interleaved): completed=False remaining=0 pendingAsync=1 -> BUG: hangs
Exp1b DisposeAsync started inside DoAsync, awaited outside: completed=True -> PASS
```

`remaining=0, pendingAsync=1`이 정확히 위 3단계의 상태다. Exp1b가 보여주듯 **동기 작업에서 시작하고
await하지 않으면** 문제없다 — 회피책은 있다.

**해결** — 두 층으로.

1. **즉시(진단 + 문서)**: `DisposeAsync`가 호출자 자신의 async 작업 안에서 불렸는지 감지해 DEBUG에서 던진다.
   "자기 async 작업 안"은 `StartAsyncJob`이 `fn()`을 부르는 동안 세우는 `[ThreadStatic]` 플래그로 알 수
   있다(동기 구간). Interleaved에서는 await 뒤 연속 작업도 액터의 작업으로 실행되므로
   `CurrentExecuter == this`로 잡힌다.

```csharp
[ThreadStatic] private static bool t_insideAsyncJobBody;

// StartAsyncJob (양쪽), fn() 호출 부분
t_insideAsyncJobBody = true;
try { task = fn() ?? Task.CompletedTask; }
finally { t_insideAsyncJobBody = false; }

// DrainThenCompleteAsync 진입부
if (_system.Options.DetectBlockingWaitOnWorker
    && ReferenceEquals(ThreadContext.CurrentExecuter, this)
    && (t_insideAsyncJobBody || _reentrancy == AsyncReentrancy.Exclusive))
{
    throw new InvalidOperationException(
        $"Actor '{Name}' awaited its own DisposeAsync from inside one of its async jobs. The drain waits " +
        "for that job, and the job waits for the drain. Start the dispose without awaiting it " +
        "(`_ = DisposeAsync();`), or dispose from outside the actor.");
}
```

   문서(`shutdown.md`, `pitfalls.md`): "액터 자신의 async 작업 안에서는 `DisposeAsync`를 await하지 말고
   `_ = DisposeAsync();`로 시작만 하라. 그 작업이 끝나는 순간 드레인이 완료된다."

2. **근본(의미론)**: 드레인이 "요청한 흐름 자신"을 세지 않게 한다. S9에서 제안한
   `AsyncLocal<AsyncExecutable?> AsyncFlowOwner`를 `StartAsyncJob`에서 세우면, `DrainThenCompleteAsync`는
   `AsyncFlowOwner.Value == this`일 때 `_pendingAsync <= 1`을 유휴로 간주할 수 있다(자기 자신 1개는
   빼고). `SignalDrainedIfIdle`에서는 그 정보를 알 수 없으므로, `DrainThenCompleteAsync`가 "자기 흐름 제외
   유휴"를 만족하면 `_completed = 1`을 먼저 쓰고 **기다리지 않고 반환**하게 한다. 그 async 작업이 끝나면
   `_pendingAsync`가 0이 되고 액터는 실제로 비게 된다. 반환값은 `true`로 둬도 거짓이 아니다 — 남은 것은
   호출자 자신뿐이다.

   두 번째 안은 `AsyncLocal` 쓰기가 `ExecutionContext` 복사를 유발하므로 async 작업 경로에만 두어야 하고,
   S9와 같은 인프라를 공유하니 함께 구현한다.

**테스트**: 프로브 Exp1을 그대로 `ShutdownTests.DisposeAsyncAwaitedInsideOwnAsyncJobDoesNotHang`으로 —
1안 적용 시 `InvalidOperationException`, 2안 적용 시 5초 안에 완료. Exclusive 변형 한 케이스 더.

---

## S16. async 작업 안에서 `await system.StopAsync()`는 자기 자신을 기다린다 (Medium, 재현)

**위치**: `JobSystem.DrainAsync` (475행)의 `PendingAsyncJobs > 0` 조건

S15의 시스템 판이다. 관리자 명령 액터의 `RunAsync` 안에서 `await system.StopAsync(t)`를 하면
`DrainAsync`가 `PendingAsyncJobs`(자기 자신 포함)를 기다리다 `t`를 전부 소모하고 `false`를 돌려준다.
A3 이전에는 이 작업이 셈에 없어 드레인이 즉시 끝났다. 멈추지는 않는다 — 타임아웃 뒤 워커를 내리고,
연속 작업은 `HasWorkers == false`라 스레드풀에서 inline으로 이어진다 — 하지만 **모든 종료가 드레인
실패로 기록**되고 종료 시간이 `drainTimeout`만큼 늘어난다.

**재현(프로브)**:

```
Exp2 StopAsync(2s) awaited inside AskAsync: completed=True drained=False elapsed=2019ms -> BUG: drain waits on the caller itself
```

**해결**

1. 문서: "`StopAsync`/`DrainAsync`는 잡 시스템 **밖**에서(호스트, 시그널 핸들러, 콘솔 루프) 부른다. 액터
   작업 안에서 종료를 시작해야 하면 `_ = system.StopAsync(...)`로 시작만 하고 그 작업을 끝내라."
2. 코드: S15의 2안과 같은 `AsyncFlowOwner`가 있으면 `DrainAsync`가 자기 흐름을 제외할 수 있다 —
   `PendingAsyncJobs - (AsyncFlowOwner.Value is not null ? 1 : 0) > 0`. 시스템 카운터는 액터 구분이
   없으므로 "현재 흐름이 어떤 액터의 async 작업이면 1을 뺀다"가 최선이고, 그것으로 충분하다.
3. DEBUG 진단: `DrainAsync` 진입 시 `AsyncFlowOwner.Value is not null`이면 경고 로그(예외까지는 과하다 —
   타임아웃으로 회복되므로).

---

## S17. `Ask`/`AskAsync`/`RunAsync`의 실패가 장애 추적을 완전히 우회한다 (Medium, 재현)

**위치**: `AsyncExecutable.Ask` (265~272행)의 작업 람다, `Settle` (478~490행), `ExecuteJob` (909~921행)

`Ask`의 작업은 `try { Tcs.TrySetResult(Func()) } catch (ex) { Tcs.TrySetException(ex) }`다. 예외를
**삼켜서 TCS에 넣으므로** `ExecuteJob` 입장에서 이 작업은 성공이다. 따라서:

- `OnJobError`가 호출되지 않는다 — 액터별 장애 처리(세션 끊기 등)가 `Ask` 경로에는 없다.
- `TotalJobsFailed`가 오르지 않고 `TotalJobsExecuted`만 오른다.
- `MaxConsecutiveFailures` 스트릭에 **성공으로** 반영돼 `_consecutiveFailures`를 0으로 리셋한다. 실패하는
  `DoAsync` 사이에 실패하는 `Ask`가 하나 끼면 액터는 영원히 `Faulted`가 되지 않는다.

`RunAsync`/`AskAsync`도 같다: async 메서드가 예외를 Task에 캡처하므로 상태 머신 단계 작업은 던지지 않고,
`Settle`이 TCS로 넘길 뿐 액터에는 아무것도 알리지 않는다. **fire-and-forget `RunAsync`의 예외는 어디에도
남지 않는다** — TCS에 관측자가 없으니 로그도, 메트릭도, `OnJobError`도 없다(finalizer의
`UnobservedTaskException`은 기본 무시).

**재현(프로브)**:

```
Exp3 10 failing Ask/RunAsync jobs: IsFaulted=False OnJobError calls=0 TotalJobsFailed=0 TotalJobsExecuted=15 -> BUG
Exp4 throw, failing Ask, throw with MaxConsecutiveFailures=2: IsFaulted=False -> BUG: the failing Ask reset the streak
```

`MaxConsecutiveFailures=2`인 액터에 실패 작업 10개를 넣었는데 `IsFaulted=False`, `OnJobError` 0회,
`TotalJobsFailed=0`이다.

**해결** — "예외를 관측할 사람이 있는가"로 나눠서 처리한다.

1. **메트릭과 스트릭은 항상** 반영한다. `ExecuteJob`의 성공 경로 리셋을 피하려면 작업이 "실패했지만 예외는
   내가 전달했다"고 알려야 한다. 마커 예외 하나로 해결된다:

```csharp
/// <summary>Thrown by a job whose failure has already been handed to a waiting caller.</summary>
private sealed class ObservedJobFailure(Exception inner) : Exception(inner.Message, inner) { }

// Ask 작업 람다
catch (Exception ex)
{
    t.Tcs.TrySetException(ex);
    throw new ObservedJobFailure(ex);      // ExecuteJob이 실패로 집계하되 OnJobError는 부르지 않는다
}

// ExecuteJob의 catch
catch (ObservedJobFailure)
{
    _system.Metrics.OnExecuted();
    _system.Metrics.OnFailed();
    TrackFailureStreak();                  // HandleJobFailure에서 스트릭 부분만 분리
}
catch (Exception ex) { ...기존 그대로... }
```

   비용은 실패 경로에서만 예외 하나 더다. `Ask`의 호출자는 원래 예외를 그대로 받는다.

2. **async 작업**: `Settle`이 `IsFaulted`이면 `self.NoteAsyncFailure()` → `Metrics.OnFailed()` +
   `Interlocked.Increment(_consecutiveFailures)` + 임계 도달 시 `_faulted`. `Settle`은 임의 스레드에서
   돌지만 `HandleJobFailure`도 이미 `Interlocked`를 쓰므로 같은 규칙이다. 성공 경로 리셋(`ExecuteJob`)은
   상태 머신 단계 작업마다 실행되므로 async 작업 하나가 "여러 성공 + 마지막 실패"로 보이는 문제가 있다 —
   완벽하지 않지만 지금의 "0회"보다 낫고, 문서에 적는다.

3. **`OnJobError` 호출 여부**: `Ask`/`AskAsync`는 호출자가 결과를 기다리므로 부르지 않는다(이중 보고).
   `RunAsync`는 결과가 없어 fire-and-forget이 흔하므로 **부른다** — 관측자가 있으면 두 번 보게 되지만
   없을 때 침묵하는 것보다 낫다. `JobOptions.ReportAwaitedFailures`(기본 false)로 `Ask` 쪽도 켤 수 있게
   두면 된다.

4. 문서(`guarantees.md` "What DoAsync returning false means" 옆): "요청/응답 API의 예외는 반환된 Task로
   전달된다. 액터의 `OnJobError`·`TotalJobsFailed`·`MaxConsecutiveFailures`에 반영되는 것은 (수정 후)
   메트릭과 스트릭이며, `OnJobError`는 `RunAsync`에만 호출된다."

**테스트**: 프로브 Exp3·Exp4를 그대로 옮긴다. 수정 후 Exp4는 `IsFaulted=True`, Exp3은
`TotalJobsFailed=10`, `IsFaulted=True`, `OnJobError`는 `RunAsync` 5건에 대해 5회.

---

## S18. 타이머 예약은 `MaxQueueSize`를 보지 않는다 (Medium, 취약점, 재현)

**위치**: `AsyncExecutable.DoAsyncAfter` (192~213행), `DoAsyncEvery` (223~232행) — `TryReserve`만 거치고
`Admit`(상한 검사)은 **발화 시점**의 `DoTaskFromTimer`에서야 실행된다.

B1·B5가 세션의 큐를 막았지만 타이머는 그 옆으로 지나간다. 액터에 `MaxQueueSize = 4`를 걸어도
`DoAsyncAfter(30초)`는 개수 제한 없이 받아들이고, `TimerEntry` + 페이로드 `Job`이 힙(`PriorityQueue`)에
쌓인다. 상한은 30초 뒤 발화할 때 `QueueFull`로 **버리는** 데만 쓰인다 — 그때까지의 메모리와 힙 연산은 이미
지불한 뒤다. 공격/스킬마다 쿨타임 타이머를 거는 서버(`timers.md`가 권하는 패턴)에서 한 세션이 초당 수만 개를
예약하면:

- 세션당 `TimerEntry`(~80B) + `Job`(~32B) × N — 세션 하나가 수백 MB.
- `PriorityQueue`가 커지며 `Enqueue` O(log n)이 시스템 락 안에서 길어져 **다른 모든 액터의 타이머 예약이
  느려진다**(S22).
- `Cancel()`해도 엔트리는 due까지 힙에 남는다(ADR 0003) — 예약-취소 반복이 힙을 키운다.

**재현(프로브)**:

```
Exp6 MaxQueueSize=4, 10,000 x DoAsyncAfter(30s): accepted=10000 pendingTimers=10000 queue=0 -> BUG: bound bypassed until fire time
```

**해결** — 액터 단위 미발화 타이머 카운터를 두고 예약 시점에 검사한다.

```csharp
// JobOptions
/// <summary>
/// Most timers this actor may have armed at once. null defaults to MaxQueueSize (or unbounded if
/// that is unbounded). Timers bypass the queue bound until they fire, so without this a client
/// that arms a cooldown per packet grows the timer heap without limit.
/// </summary>
public int? MaxPendingTimers { get; init; }

// AsyncExecutable
private int _pendingTimers;
private readonly int _maxPendingTimers;   // ctor: options.MaxPendingTimers ?? options.MaxQueueSize ?? system default ?? 0

private bool TryReserveTimer()
{
    if (_maxPendingTimers == 0) { Interlocked.Increment(ref _pendingTimers); return true; }
    while (true)
    {
        var current = Volatile.Read(ref _pendingTimers);
        if (current >= _maxPendingTimers) return false;
        if (Interlocked.CompareExchange(ref _pendingTimers, current + 1, current) == current) return true;
    }
}
internal void OnTimerRetired() => Interlocked.Decrement(ref _pendingTimers);   // TimerEntry.Retire와 one-shot 발화에서 호출

// DoAsyncAfter / DoAsyncEvery
if (!TryReserveTimer()) { Refuse(DropReason.TimerQueueFull); return CancelledTimer.Instance; }
```

`DropReason.TimerQueueFull`을 추가하고(열거형 추가는 소스 호환), `OnDropped`로 세션이 알 수 있게 한다.
반복 타이머는 수명당 1로 센다(`PendingTimerCount`와 같은 규칙). 회계는 `TimerEntry.Retire`(취소·폐기)와
one-shot의 `TryBeginFiring` 성공 시점에서 내린다 — 이미 `_pending`에 대해 하는 것과 같은 자리다.

힙에 남는 취소 엔트리는 별개 문제다: `_queue.Count`가 활성 수의 2배를 넘으면 락 안에서 한 번 재구성
(`new PriorityQueue(live entries)`)하는 lazy purge를 넣으면 예약-취소 폭주에도 힙이 활성 수의 상수 배로
묶인다.

**테스트**: 프로브 Exp6을 `BoundedQueueTests.TimersRespectTheActorBound`로 — 상한 4에서 5번째
`DoAsyncAfter`가 `IsPending == false`이고 `OnDropped`가 `TimerQueueFull`을 받는다. 취소 후 슬롯이 돌아오는지
한 케이스 더.

---

## S19. `Scheduled` 판정이 시스템을 구분하지 않는다 (Medium, 정확성, 재현)

**위치**: `AsyncExecutable.Admit` (665행)

```csharp
if (_mode == ExecutionMode.Scheduled && !ThreadContext.IsWorkerThread && _system.HasWorkers)
```

`IsWorkerThread`는 "어떤 `JobDispatcher`의 워커인가"이지 "**이 액터의 시스템**의 워커인가"가 아니다.
`ThreadContext.CurrentSystem`은 `RunWorker`가 세우지만 아무도 읽지 않는다(S25). 그래서 두 시스템을 격리해
둔 프로세스에서 — `JobSystem` 문서가 첫 문단에서 권하는 "게임 월드 풀 / 백그라운드 IO 풀" 구성 —
시스템 A의 워커 위에서 시스템 B의 `Scheduled` 액터에 post하면 `IsWorkerThread == true`라 `Schedule`을
건너뛰고, **B의 액터가 A의 워커에서 inline 실행**된다. B가 `Scheduled`를 고른 이유(자기 풀에서만 돌기,
A의 지연 예산을 침범하지 않기)가 정확히 그 경계에서 깨진다. LeaderFlush 액터라면 어디서든 inline이 설계이므로
문제가 아니지만, `Scheduled`는 "내 풀에서 돈다"는 약속이다.

**재현(프로브)**:

```
Exp7 system-B Scheduled actor posted from system-A worker ran on 'JobWorker-A-0' -> BUG: cross-system isolation leak
```

**해결** — 판정을 시스템 소속으로 바꾼다. `CurrentSystem`은 이미 세워져 있다.

```csharp
if (_mode == ExecutionMode.Scheduled
    && !ReferenceEquals(ThreadContext.CurrentSystem, _system)   // not one of *our* workers
    && _system.HasWorkers)
{
    _system.Schedule(this);
    return true;
}
```

`ThreadContext.CurrentSystem`은 타이머 스레드도 세우므로(`Loop` 251행) 타이머 발화 경로의 판정도 일관된다
(그쪽은 `fromTimer` 분기가 먼저라 영향 없음). 한 시스템만 쓰는 프로세스에서는 `CurrentSystem == _system`이
`IsWorkerThread`와 동치라 동작이 바뀌지 않는다.

`ExecuterQueue` 경로(671행)도 같은 질문이 있다: A의 워커가 B의 LeaderFlush 액터를 ExecuterQueue에 넣어
A의 워커에서 플러시하는 것은 설계상 허용(LeaderFlush = 호출자가 돈다)이지만, S2의 (a)를 적용할 때
`_system.HasWorkers` 판정이 **액터의** 시스템 기준이어야 한다는 점만 지키면 된다.

**테스트**: 프로브 Exp7을 `ExecutionModeTests.ScheduledActorsStayOnTheirOwnSystemsWorkers`로.

---

## S20. 마지막 워커의 영구 정지는 레디 큐의 액터를 영원히 좌초시킨다 (Medium, 재현)

**위치**: `JobDispatcherBase.TryRestart` (259~265행), `JobSystem.DrainReady`/`Schedule`

워커가 `MaxRestartsPerWorker`를 넘기면 "permanently down"을 남기고 슬롯을 포기한다(A5 이후 프로세스는
산다). 그 워커가 **마지막**이었다면:

- 레디 큐에 이미 올라간 액터들은 카운터가 0이 아니므로(리더가 있다고 믿는다) 새 post가 와도 큐에 쌓이기만
  한다. 아무도 `DrainReady`를 돌리지 않으니 **영원히** 실행되지 않고, `DisposeAsync`(무한 오버로드)는
  끝나지 않는다.
- 반면 `HasWorkers == false`가 되므로 **새 액터**의 첫 post는 inline으로 실행돼 멀쩡해 보인다. 헬스체크는
  `live == 0`을 Unhealthy로 보고하지만, 좌초된 액터의 수는 어디에도 없다 — `ReadyQueueDepth`가 유일한 흔적.

즉 "워커 0개"는 부분 장애가 아니라 **일부 세션만 조용히 죽는** 상태다. 게임 서버에서 가장 나쁜 종류다.

**재현(프로브)**:

```
Exp8 last worker permanently down: readyBefore=1 ready=1 strandedActor.Ran=False queue=2 freshActor.Ran=True disposeOk=False -> BUG
```

좌초된 액터에 다시 post한 작업(`queue=2`)도 실행되지 않고, 같은 시스템의 새 액터(`freshActor`)는 정상
동작했다.

**해결** — 두 가지를 같이.

1. **마지막 워커는 포기하지 않는다.** 슬롯 예산은 "다른 워커가 남아 있을 때"의 정책이다. 워커 0개인 시스템은
   어차피 죽은 것이니 백오프 상한(`MaxRestartBackoff`)으로 계속 재시도하고, 매 시도를 Error로 남긴다.

```csharp
// TryRestart
var attempts = Interlocked.Increment(ref _restartCounts[slot]);
var lastOneStanding = System.LiveWorkerCount == 0;      // UnregisterWorker already ran for this slot
if (attempts > Options.MaxRestartsPerWorker && !lastOneStanding)
{
    System.Logger.Error($"Worker slot #{slot} exceeded max restarts ({Options.MaxRestartsPerWorker}) — permanently down");
    return false;
}
if (lastOneStanding && attempts > Options.MaxRestartsPerWorker)
    System.Logger.Error($"Worker slot #{slot} is the last worker on '{System.Name}'; restarting past the budget " +
                        $"(attempt {attempts}) because actors already on the ready queue have no other way to run");
```

   `JobDispatcherOptions.KeepLastWorkerAlive`(기본 true)로 끌 수 있게 둔다. 한 시스템에 디스패처가 둘이면
   `System.LiveWorkerCount`가 다른 디스패처의 워커도 세므로 "이 프로세스에 이 시스템의 워커가 하나도 없다"가
   정확한 조건이다.

2. **좌초를 관측 가능하게.** `HasWorkers`가 true→false로 바뀌는 순간(`UnregisterWorker`가 0을 만들 때)
   `ReadyQueueDepth > 0`이면 Error 로그: "N ready items have no worker left to run them". 헬스체크 데이터에
   `readyQueueDepth`는 이미 있으니 `live == 0 && readyQueueDepth > 0`을 메시지에 명시한다.

   근본적으로는 "리더가 레디 큐에 있다"는 상태를 되돌릴 수 있어야 한다 — `HasWorkers`가 false로 떨어질 때
   레디 큐의 액터를 꺼내 `RunFlushLoop`를 **호출자 스레드에서** 돌리는 것은 `UnregisterWorker`가 죽어가는
   워커 스레드에서 실행되므로 가능하다(마지막 워커의 `finally`에서 남은 레디 큐를 비우고 나간다). 1안이
   들어가면 이 경로는 드물어지지만, `RestartFailedWorkers = false`인 구성을 위해 넣을 가치가 있다.

**테스트**: 프로브 Exp8을 `ConcurrencyReviewTests.TheLastWorkerIsRestartedPastItsBudget`로 — 1안 적용 후
`strandedActor.Ran == true`.

---

## S21. `DrainAsync`가 2ms마다 워커 전부를 깨운다 (Low–Medium, 재현, 오늘 A10(d)가 만듦)

**위치**: `JobSystem.DrainAsync` (486행)

A10(d)에서 `SignalWork()`(대기자 하나)를 `SignalAllWork()`(전부)로 바꿨다. 종료 조인에서는 옳은 선택이지만
`DrainAsync`의 폴링 루프에도 같이 적용했고, 거기서는 **2ms마다 PulseAll**이 된다. 드레인이 막혀 있는 동안
— 취소 안 한 반복 타이머(pitfall 9), S15/S16의 자기 대기, 30초 드레인 타임아웃 — 유휴 워커 전부가 초당
500번 깨어나 C2의 스핀(10회)을 돌고 다시 잔다.

**재현(프로브)**:

```
Exp9 8 idle workers, 2s: CPU idle=0ms vs during a stuck DrainAsync=94ms (drained=False) -> BUG: drain polling wakes the whole pool
```

8 워커에서 코어 하나의 ~5%. 32 워커 서버가 30초 드레인 타임아웃에 걸리면 종료하는 30초 내내 코어 하나를
태운다. 치명적이진 않지만 존재할 이유가 없고, 제가 만든 것이다.

**해결** — 드레인 루프의 pulse는 "혹시 자고 있는 워커가 있으면 큐를 다시 보라"는 뜻이므로 `Pulse` 하나면
충분하고, 그마저도 큐가 비어 있으면 필요 없다.

```csharp
// DrainAsync 루프
if (ReadyQueueDepth > 0)
    SignalWork();                 // one waiter, and only when there is something to take
await Task.Delay(2).ConfigureAwait(false);
```

`SignalAllWork()`는 `TryStop`에만 남긴다.

**테스트**: 프로브 Exp9를 `ShutdownTests.AStuckDrainDoesNotSpinIdleWorkers`로 — 2초 드레인 동안 프로세스
CPU 증가가 20ms 이하.

---

## S22. 타이머 예약의 락 직렬화와 `TimerEntry` 할당 (Low–Medium, 성능 관찰)

**위치**: `TimerService.Enqueue` (186~212행), `Schedule` (128행)

시스템의 모든 스레드가 타이머 하나를 걸 때마다 `_lock`을 잡고 `PriorityQueue.Enqueue`(O(log n))를 한다.
C5가 불필요한 `Pulse`를 없앴지만 락 자체는 그대로다. 8 워커가 각자 "공격 → 쿨타임 타이머"를 초당 수만 건
걸면 이 락이 워커 간 유일한 공유 직렬화 지점이 된다 — C1이 풀에서 없앤 것과 같은 모양이다. 예약마다
`TimerEntry`(약 80B) 하나가 새로 할당되고 페이로드 `Job`은 풀링되는데 엔트리는 안 된다.

**해결(측정 후)**: (a) `TimerEntry`를 `JobPool<TimerEntry>`로 풀링 — `Retire`가 `Cancelled`에 도달한 뒤
필드를 지우고 반납. 핸들을 사용자가 들고 있으므로 **세대 번호**를 넣어 재사용된 엔트리에 대한 옛 핸들의
`Cancel()`이 `false`를 돌려주게 해야 한다(그렇지 않으면 다른 타이머를 취소한다). (b) 워커별 로컬 타이머
힙 + 타이머 스레드가 주기적으로 병합하거나, 계층 타이밍 휠(ADR 0003이 대안으로 적어 둠). (a)가 싸고
(b)는 프로파일에서 락이 보일 때.

**측정**: `perf` 하니스에 "8 스레드 × 100,000 `DoAsyncAfter(1h)` 후 전부 `Cancel`" 셀. 락 경합은 스레드
수 대비 확장 곡선으로, 할당은 gen0 카운트로 본다.

---

## S23. 공유 제네릭 코드의 `[ThreadStatic]` 접근 비용 (Low, 성능 관찰)

**위치**: `JobPool<T>` (`JobEntry.cs`), `Job<TState>.Rent`/`Recycle`

`[ThreadStatic]` 필드는 정확한 타입이 JIT 시점에 알려질 때 인라인 TLS 경로를 탄다. `Job<TState>`의
`TState`가 **참조형**이면 JIT는 `Job<__Canon>` 하나의 공유 코드를 만들고, 그 안에서
`JobPool<Job<__Canon>>._local`에 접근할 때마다 제네릭 딕셔너리에서 실제 타입 핸들을 찾아 헬퍼를 호출한다
(`JIT_GetSharedGenericThreadStaticBase`류). 값형 `TState`(튜플)는 타입별로 특수화돼 빠르다. 문서가 권하는
관용구 두 가지 중 `DoAsync(static a => a.Bump(), this)`는 `TState = 액터 클래스`(참조형)라 **느린 쪽**이고,
`DoAsync(static t => …, (Self: this, X: x))`는 빠른 쪽이다. 오늘 측정한 하니스는 전자를 썼으므로 3~9×라는
수치는 이 비용을 **포함한** 것이다 — 즉 여기에 여유가 더 있다.

**해결(측정 후)**: 스레드 로컬 저장을 제네릭 타입 밖으로 뺀다. 타입마다 정적 생성자에서 정수 슬롯 ID를
받고(`static readonly int SlotId = Interlocked.Increment(ref JobPoolSlots.Next)`), 비제네릭 클래스의
`[ThreadStatic] static object?[]? t_slots`에서 `t_slots[SlotId]`를 캐스팅해 쓴다. 비제네릭 thread-static은
인라인 경로를 타고, 배열 인덱싱 + 캐스트는 딕셔너리 조회보다 싸다. `perf` 하니스에서 `Counter`(참조형)
vs 튜플 상태를 나란히 재면 현재 격차가 보이고, 적용 후 좁혀져야 한다.

---

## S24. CAS와 `Enqueue` 사이의 OOM은 액터를 영구 정지시킨다 (Low)

**위치**: `AsyncExecutable.Admit` (628~645행)

ADR 0004는 "카운터 증가 뒤 큐 쓰기가 실패하면 리더가 없는 작업을 기다리며 영원히 스핀한다"를 v2.0의
결함으로 기록하고 CAS 순서로 고쳤다. 남은 창 하나: `_queue.Enqueue(task)`(`ConcurrentQueue`의 세그먼트
할당)가 `OutOfMemoryException`을 던지면 카운터는 올랐고 큐는 비었다. 이 스레드가 리더(`current == 0`)였다면
`RunFlushLoop`에 도달하지 못해 리더가 없고, 아니었다면 실제 리더의 `Flush`가 "카운터 > 0, 큐 비어 있음"에서
스핀한다(`MaxFlushSpinIterations`마다 `Yield`하며 영원히). OOM 상황에서 프로세스가 살아남는 경우는 드물지만
서버 GC의 큰 힙에서는 일시적 OOM이 회복되기도 한다.

**해결**: `Enqueue`를 try/catch로 감싸 실패 시 카운터를 되돌린다(`Interlocked.Decrement`) — 되돌린 뒤
0이 됐고 우리가 리더였다면 아무도 플러시할 게 없으니 그대로 반환, 0이 아니면 다른 리더가 있으니 그쪽이
정상 처리. `task.Discard()`도 잊지 않는다. 비용은 try 블록 하나(예외가 없으면 0에 가깝다).

---

## S25. 죽은 상태와 영구 침묵 플래그 (Low, 정리)

- `ThreadContext.TickCount`는 `PumpReadyQueue`가 워커 반복마다 `System.CurrentTick`(`Stopwatch` 호출)으로
  갱신하지만 라이브러리 어디서도 읽지 않는다. `ThreadContext.CurrentSystem`도 세우기만 하고 읽지 않는다
  (S19가 첫 소비자가 될 것이다). 둘 다 공개 API라 지우기보다는 문서에 "진단용, 라이브러리는 사용하지 않음"을
  적고 `TickCount` 갱신은 `EnableDetailedMetrics`에 묶는 정도가 맞다.
- `JobSystem.WarnTimerFallbackOnce`는 한 번 켜지면 영원히 침묵한다. 워커 재시작 백오프 동안(S20의 사촌)
  일시적으로 `HasWorkers == false`가 되면 그때 한 번 경고하고, 진짜 "디스패처 없이 배포된" 상황이 나중에
  와도 아무 말이 없다. `_timerFallbackWarned`를 `HasWorkers`가 다시 true가 될 때(`RegisterWorker`) 0으로
  되돌리면 된다.

---

## 검토했고 문제 없음

오늘 바꾼 경로의 불변식을 다시 따라간 결과다. 위 항목들과 구분해 두는 것은, 다음 리뷰어가 같은 곳을
다시 의심하지 않아도 되게 하기 위해서다.

- **드레인 핸드셰이크(A1·A3)**: `DrainThenCompleteAsync`의 `Interlocked.Exchange` ↔ `Flush`/
  `EndExclusiveSuspension`/`EndAsyncTracking`의 `Interlocked.Decrement` 후 `SignalDrainedIfIdle`. 양쪽 모두
  full fence 뒤에 상대 변수를 읽으므로 둘 중 하나는 반드시 상대의 저장을 본다. `_remainingTaskCount`와
  `_pendingAsync`를 둘 다 보는 `IsIdle`도 같은 구조다(각 카운터의 감소가 fence, 그 뒤 두 카운터 재확인).
- **Exclusive 상태 머신 + async 추적**(단, S15·S16의 "요청자가 자기 자신" 경우는 제외): `BeginExclusiveSuspension`(CAS None→Pending), `Flush`의
  Pending→Parked, 연속 작업의 Pending→Completed 중 정확히 하나만 이긴다. `EndAsyncTracking`이
  `EndExclusiveSuspension` **뒤에** 실행되므로 예약 해제가 먼저 일어나고, `_pendingAsync`가 남아 있는 동안
  `SignalDrainedIfIdle`은 신호하지 않는다. `ContinueWith`가 등록 시점에 이미 완료된 Task에 대해 동기
  실행되는 경우(플러시 스레드 안)도 Completed 경로로 정확히 처리된다.
- **`AdmitContinuation`(A2)의 리더 선출**: 상한만 우회하고 `_remainingTaskCount` CAS는 그대로 타므로
  ADR 0004의 "카운터 ≥ 큐 길이" 불변식이 유지된다. Scheduled 액터의 연속 작업은 스레드풀 스레드에서
  들어오므로 `Schedule`로 워커에 넘어간다.
- **타이머 상태 머신(A6·A12)**: `New/Armed/Fired/Executed/Cancelled`에서 pending 슬롯은 `Armed`에서만
  보유하고 `Retire`가 `state == Armed`일 때만 반납한다. one-shot의 `_job`은 `Run`(Fired→Executed 승자)과
  `Retire`(→Cancelled 승자) 중 CAS를 이긴 쪽만 만진다 — 이중 실행·이중 반납 없음. 반복 엔트리가 취소 직후
  재무장돼도 `CollectDueLocked`가 건너뛰므로 회계는 맞고, 힙에 남는 것은 ADR 0003이 이미 적은 비용이다.
- **`JobPool<T>`(C1) 단일 소유권**: 한 `Job`은 `Execute`(자기 `Recycle`) 또는 `Discard` 중 정확히 한 번만
  풀로 돌아간다. `DoTaskFromTimer`·`Admit`의 거부 경로, 타이머 `Retire`의 페이로드 폐기, `RentTickJob`
  래퍼 모두 확인했다. `[ThreadStatic]` 스택은 스레드 종료와 함께 수집되고, `Recycle`이 `_action`/`_state`를
  지우므로 사용자 상태가 풀에 남지 않는다. 배치 게시(`ConcurrentQueue.Enqueue`)는 release 의미론이라
  소비자가 복사된 내용을 본다.
- **워커 시작/종료(A9·A10)**: `RunWorkerThreadsAsync`의 disposed 검사와 `TryRestart`의 재확인이 모두
  `_lifecycleLock` 안에 있고, `TryStop`은 같은 락 안에서 취소+스냅샷을 찍는다. 자기 조인 생략 시
  `_cts.Dispose()`도 생략된다.
- **`Scheduled`의 스레드 판정은 단일 시스템에서만 옳다** — 두 시스템에서는 S19. 여기 적어 두는 이유는
  1차 패스가 이 분기를 "정상"으로 읽고 넘어갔기 때문이다.
- **`StopAsync` 순서**: 드레인 → `AcceptingWork = false` → 타이머 Dispose(스레드 조인) → 워커 정지.
  게이트가 타이머보다 먼저 닫히므로 타이머 발화가 종료 중 레디 큐에 액터를 남기는 창은 `TryReserve`와
  `Schedule` 사이 수 ns뿐이고, 그동안 워커는 살아 있다.
- **`SignalWork`/`WaitForWork` Dekker(C2 이후)**: 스핀 단계는 대기자로 등록하지 않으므로 유실 wake와
  무관하다. 등록 후의 `IsEmpty` 재확인은 그대로다.

---

## 권장 순서

0. **S15** — 오늘 만든 회귀이고 High다. 1안(진단 + 문서 + 프로브를 테스트로)을 먼저 넣어 침묵하는 hang을
   예외로 바꾸고, 2안(`AsyncFlowOwner`)은 S9·S16과 묶어 별도 커밋으로. CHANGELOG **Fixed**.
1. **S1** — 한 줄 수정 + 옵션 + 통합 테스트. Hosting을 쓰는 서버의 종료 동작이 바뀐다(좋은 쪽으로).
   CHANGELOG **Fixed**.
1'. **S17** — 마커 예외 + `Settle` 훅 + 문서. 프로브 Exp3·Exp4를 테스트로. 관측 가능성 결함이라 다음
   운영 사고에서 가장 먼저 아쉬울 항목이다.
1''. **S18, S19, S21** — 각각 20줄 이내이고 프로브가 이미 테스트 골격이다. S21은 오늘 만든 것이라 같은
   날 되돌리는 게 맞다. S18은 `DropReason` 값 추가라 CHANGELOG **Added**에도 한 줄.
1'''. **S20** — 1안(마지막 워커는 포기하지 않음)은 15줄, 2안(좌초 로그)은 5줄. 함께.
2. **S3, S5, S6, S8, S12, S13, S14** — 각각 10줄 이내, 위험 없음. 한 커밋으로.
3. **S7** — B1 회귀 복구. 측정 셀 하나 추가.
4. **S4** — 1단계(문서)는 즉시, 2단계(`OperationStarted`)는 별도 커밋에서 테스트와 함께.
5. **S2** — 측정이 필요한 유일한 항목. `perf` 하니스의 링 벤치마크로 전후 비교 후 결정. 기대대로 워커 수에
   비례해 확장되면 CHANGELOG **Performance**, 그렇지 않으면 이 문서에 측정치를 남기고 보류.
6. **S9, S10, S11** — 문서와 방어 코드. 급하지 않다.
