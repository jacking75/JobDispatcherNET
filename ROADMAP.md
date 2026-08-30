# JobDispatcherNET 로드맵

> **최초 작성** 2026-08-30 · **실행 완료** 2026-08-30 (v2.1.0) · **환경** .NET SDK 10.0.301 / Windows 11
> 이 문서는 원래 "무엇을 할지" 계획서였다. 아래 §1~§6 의 항목은 **대부분 구현되었고**, 각 절에
> 실제 결과와 검증 방법을 함께 적었다. 남은 일은 §9 에 모아 두었다.

---

## 0. 실행 결과 요약

| 영역 | 상태 |
|---|---|
| P0 안정성 버그 4건 + 소형 항목 | ✅ 수정 + 회귀 테스트 (§1) |
| P1 아키텍처 8개 항목 | ✅ 구현 (§2) |
| OSS 인프라 (LICENSE / NuGet 패키징 / 테스트 / 벤치마크) | ✅ 구축 (§3) |
| CI 파이프라인 | ❌ 저장소 소유자 요청으로 제거 — §3.1 참고 |
| P2 성능 | ⚠️ 측정 근거가 있는 것만 반영, 나머지는 벤치 대기 (§4) |
| P2 생태계 (Hosting / Logging / 네트워크 샘플 / 템플릿) | ✅ 구현 (§5) |
| 문서 (영어 README / docs / Book 정정) | ✅ 작성 (§6) |
| Unity(netstandard2.1) 지원 | ❌ 보류 — 판단 근거는 §5.5 |

**검증 상태**
- `dotnet build All.sln -c Release` → 오류 0, 경고 0 (16개 프로젝트)
- `dotnet test JobDispatcherNET.Tests` → **66 통과 / 0 실패**, net8.0·net10.0 양쪽
- `AdvancedMmorpgTests` 통합 테스트 6건 통과
- `ExampleConsoleApp` 실행 결과가 26 → **41** (P0-3 실증)
- 템플릿에서 생성한 서버가 TCP 접속·에코·종료까지 실동작 확인
- 벤치마크 스모크 런 성공 (ping-pong 3.9µs inline / 4.4µs scheduled, 할당 0)

---

## 1. P0 — 안정성 버그 (완료)

각 항목에 **먼저 실패하는 테스트를 쓰고** 고쳤다. 테스트는 `JobDispatcherNET.Tests/RegressionTests.cs`.

### P0-1. bounded 큐 거부 시 leader 워커 영구 고착 ✅

**원인** `DoTask` 가 `Interlocked.Increment` **후에** `TryWrite` 를 시도하고 실패 시 되돌리는 구조라,
"카운터에는 있는데 큐에는 없는" 유령 항목이 생겼다. `Flush` 는 작업 실행 직후의 Decrement 결과가
0일 때만 탈출했으므로, 그 유령을 기다리며 영원히 스핀했다. 이후 새 producer 가 leader 가 되면
**같은 actor 를 두 스레드가 동시에 실행**할 수 있었다.

**수정** 입장 판정을 채널이 아니라 **카운터 CAS** 로 옮겼다 (`AsyncExecutable.Admit`).
`count < max` 를 CAS 로 확정한 뒤에만 큐에 넣으므로 카운터와 큐가 어긋나지 않는다.
큐를 `Channel<JobEntry>` 에서 `ConcurrentQueue<JobEntry>` 로 바꿔 bounded 채널의 락도 사라졌다.
안전망으로 `Flush` 의 dequeue 실패 경로에서 `count == 0` 이면 탈출한다.

**테스트** `P0_1_BoundedRejectionNeverStrandsTheLeader` — producer 8개 × 40,000회, 의도적으로
CAS-enqueue 창을 벌린 뒤 동시 실행이 1을 넘지 않는지 + 비원자 카운터가 정확히 일치하는지 검증.

### P0-2. 워커 재기동 시 그 스레드의 타이머 전부 유실 ✅

**원인** 타이머가 `ThreadLocal<TimerQueue>` 로 스레드에 묶여 있었고, 워커 종료 경로에서
`Dispose()` → `_queue.Clear()` 로 이관 없이 폐기했다.

**수정** 타이머를 스레드에서 떼어내 **`JobSystem` 당 전용 타이머 스레드 1개**로 옮겼다
(`TimerService`). `PriorityQueue` + `Monitor.Wait(다음 due 까지)` 라 ThreadPool 의존도, 스레드마다
도는 1ms `PeriodicTimer` 도 없다. 워커가 죽어도 타이머는 그대로다.

**테스트** `P0_2_TimersSurviveAWorkerCrash` — 타이머 예약 → 워커 강제 크래시 → 재기동 후 발화 확인.

### P0-3. 디스패처 없이 `DoAsyncAfter` 무동작 ✅

**원인** 타이머 발화가 `TimerDispatchQueue` 에만 쌓이고, 그 큐는 워커 루프만 드레인했다.

**수정** ROADMAP 기본값 **A(폴백 디스패치)** 채택. 워커가 없으면 타이머 스레드에서 직접 실행하고
`JobLog.Warn` 을 **딱 한 번** 남긴다 (`JobSystem.WarnTimerFallbackOnce`).

**실증** `ExampleConsoleApp` 의 `Test count` 가 기대값 **41** 로 복구되었다 (수정 전 26).
테스트: `P0_3_DelayedJobRunsWithNoDispatcher`.

### P0-4. `Sequencer.Stop()` 레이스로 마지막 항목 유실 ✅

**원인** drain 종료 후 재스케줄 조건에 `_stopped == 0` 이 들어 있어, Stop 이 먼저 보이면 이미 받아
둔 항목을 버렸다. 샘플 서버에서는 그 항목이 disconnect marker 였고, 결과는 유령 플레이어였다.

**수정** `Stop()` 의 의미를 "새 항목만 거부, 받은 것은 전부 처리"로 확정하고 재스케줄 조건에서
`_stopped` 검사를 제거했다. `Enqueue` 는 `bool` 을 반환하고, 정말 버려야 할 때를 위해 `Abort()` 를
따로 뒀다. `Sequencer(JobSystem, handler, onError)` 생성자로 drain 을 워커 풀에 직접 넘긴다.

**테스트** `P0_4_SequencerStopStillDrainsAcceptedItems` — Enqueue 직후 Stop 을 3,000회 반복.

### P0-5. `JobDispatcher` 라이프사이클 가드 ✅

- `RunWorkerThreadsAsync()` 2회 호출 → `InvalidOperationException`
- `TryStop(TimeSpan)` 이 정지 성공 여부를 `bool` 로 반환, Join 타임아웃 시 스레드 이름과 함께 Error 로그
- `RestartCountResetAfter`(기본 5분) — 오래 건강했던 슬롯은 재기동 예산을 회복
- 워커 크래시 로그에 **당시 실행 중이던 actor 이름**을 포함

**테스트** `P0_5_RunWorkerThreadsTwiceThrows`, `P0_5_RestartBudgetRefillsAfterHealthyPeriod`

### P0-6. 소형 결함 ✅

| 항목 | 조치 |
|---|---|
| `Job<TState>` 가 null 참조 state 를 `default!` 로 대체 | 그대로 전달 (`P0_6_NullReferenceStateIsPassedThrough`) |
| 거부된 Job 이 풀로 반환되지 않고 `OnDropped` 에 노출 | `JobEntry.Discard()` 로 회수, 콜백은 `(actor, DropReason)` |
| `AcceptingWork` 가 프로세스 전역 | `JobSystem.AcceptingWork` 로 이동, 구 API 는 `[Obsolete]` |
| `Console.ReadKey()` 가 리다이렉트 환경에서 크래시 | `Console.IsInputRedirected` 검사 |
| 테스트/샘플 포트가 개발 대역 밖 | 25100(서버) / 25101·25102(테스트) / 25110·25120·25150 으로 이동 |

### P0-8. (신규) 재작성 자체에 대한 적대적 동시성 리뷰 ✅

작업 막바지에 코어 동시성 코드를 **불변식 단위로 적대적 리뷰**했고, v2.1 재작성이 새로 들여온
결함 6건을 릴리스 전에 잡았다. 상세는 `CHANGELOG.md` 의 Fixed 절 앞부분.

| 심각도 | 결함 | 수정 |
|---|---|---|
| CRITICAL | Exclusive async 가 빈 actor 에서 끝나면 **flusher 두 개**가 생길 수 있었다. continuation 이 예약을 무조건 반납해 카운터가 0을 지나갔고, 그 틈에 producer 가 리더십을 가져갔다 | 예약은 handshake 가 끝날 때 리더십을 쥔 쪽이 **정확히 한 번만** 반납 |
| CRITICAL | `BeginExclusiveSuspension` 의 blind write 때문에 오래된 continuation 이 **다음 async 작업의 토큰**을 가져갈 수 있었다 (exclusivity 상실 + 워커 코어 spin) | `None` 에서 시작하는 CAS 로 전환 |
| HIGH | `JobSystem.Post` 작업이 drain 게이트에 안 보여, 셧다운이 큐에 일감을 남긴 채 워커를 멈출 수 있었다. `Sequencer` 의 drain 이 전부 `Post` 를 타므로 **세션 패킷 유실**로 이어질 수 있었다 | 깊이를 enqueue **전에** 올리고 실행 **후에** 내림 (항상 과대 추정) |
| HIGH | 발화 중인 one-shot 타이머를 취소하면 pending 카운터가 **두 번** 감소 → 음수 → 셧다운이 살아있는 타이머를 기다리지 않고 넘어감 | job 을 가져가는 쪽(`Interlocked.Exchange`)이 유일한 소유자 |
| HIGH | `Sequencer` 의 release-store/load 핸드셰이크가 약한 메모리 모델에서 **마지막 항목을 유실** (= P0-4 재발 경로) | interlocked exchange 로 전체 배리어 확보 |
| MEDIUM | 워커 재기동이 `TryStop` 과 경합해 셧다운이 성공을 보고한 뒤에도 워커가 살아 있을 수 있었다 | start/stop 을 lifecycle lock 으로 직렬화, 전원 종료 후에만 CTS dispose |

**재현 검증** HIGH-4 는 수정 전 코드에서 `pending timer count settled at -1` 로 실제 재현되는
회귀 테스트(`CancelRacingAFiringTimerKeepsThePendingCountHonest`)를 확보했다.
두 CRITICAL 은 창이 명령어 두 개 폭이라 프로덕션 코드에 테스트 시임을 심지 않고는 외부에서
결정적으로 재현되지 않는다 — 시임을 남기는 값어치가 없다고 판단해 걷어냈고, 대신 계약
(exclusivity·정확히 한 번 실행·빈 상태 수렴)을 검증하는 커버리지 테스트를 남겼다.
테스트 파일에 이 한계를 명시해 두었다.

### P0-7. (신규) 반복 타이머 pending 카운터 누수 ✅

스트레스 테스트가 잡아낸 **계획에 없던 버그**: 반복 타이머가 재장전될 때마다 pending 카운터가
증가해 영원히 0으로 돌아오지 않았다. 셧다운 drain 이 끝나지 않는 원인이 될 수 있었다.
재장전 경로에서 카운터를 올리지 않도록 수정 (`TimerService.Enqueue(..., isNew:)`).

---

## 2. P1 — 아키텍처 (완료)

### 2.1 실행 모델: 호출자 hijack 해결 ✅
`JobOptions.Mode` = `LeaderFlush`(기본, 호환) / `Scheduled`. Scheduled 는 비-워커 producer 가
actor 를 시스템 ready 큐에 넘기고 즉시 반환한다. `JobOptions.MaxJobsPerFlush` 로 한 actor 가 워커를
독점하는 것도 막는다. `JobSystem.Post(Action)` 이 사용자의 수동 inbound 큐를 대체한다.

### 2.2 async/await 통합 ✅
`RunAsync(Func<Task>)` / `AskAsync<T>(Func<Task<T>>)` / `Ask<T>` / `AskSync<T>`.
`AsyncReentrancy.Interleaved`(기본)는 actor 전용 `SynchronizationContext` 로 continuation 을 큐로
되돌리고, `Exclusive` 는 예약(reservation)으로 actor 를 통째로 정지시킨다.
> 오버로드 이름은 `DoAsync` 가 아니라 `RunAsync`/`AskAsync` — `Func<T>` 와 `Func<Task<T>>` 가
> 람다에서 모호해지기 때문.

### 2.3 타이머 재설계 ✅
`ITimerHandle`(취소) · `DoAsyncEvery`(주기) · 시스템당 스레드 1개 · `TimerPrecision.Coarse|High` ·
Windows 전용 opt-in `RaiseSystemTimerResolution` · 타이머 lag 히스토그램.

### 2.4 인스턴스 스코프 라이프사이클 ✅
`JobSystem` / `JobSystemOptions` 도입, `JobSystem.Default` 로 기존 코드 무수정 동작.
`StopAsync(drainTimeout, refuseNewWork)` 한 번으로 **연쇄 작업까지 drain → 게이트 차단 → 타이머 정지
→ 워커 정지**. 샘플의 `Thread.Sleep(200)` 셧다운 휴리스틱이 사라졌다.

### 2.5 오류 격리·supervision ✅
`protected virtual OnJobError(Exception)` (actor 단위) · `MaxConsecutiveFailures` → `IsFaulted` /
`ClearFault()` · 워커 크래시 로그에 실행 중이던 actor 이름.

### 2.6 관측성 ✅
`System.Diagnostics.Metrics`, 미터 이름 `JobDispatcherNET`. 카운터 8종 / 게이지 5종 / 히스토그램 2종
(`EnableDetailedMetrics` opt-in). 카운터는 캐시라인 스트라이핑(`StripedCounter`).
`samples/Observability` 에서 OpenTelemetry 콘솔 익스포터로 실제 출력 확인.

### 2.7 데드락 가드 ✅
`JobDiagnostics.GuardBlockingWait` — actor 작업 안에서 블로킹하면 예외. `AskSync` 가 이를 호출한다.
`JobSystemOptions.DetectBlockingWaitOnWorker`(Debug 기본 on) / `MaxJobDuration` 워치독.

### 2.8 워커 유휴 대기: `Sleep(1)` 폴링 제거 ✅
비제네릭 `JobDispatcher` 신설 — 워커가 시그널에 블로킹한다(`JobSystem.WaitForWork`).
`IRunnable` 이 필요 없다. 시그널 프로토콜은 waiter 카운트를 큐 검사보다 **먼저** 올려 pulse 유실을
막는다. `JobDispatcher<T>` 는 호환을 위해 유지.

---

## 3. P1 — 오픈소스 인프라 (완료)

### 3.1 라이선스·패키징 ✅ (CI 는 제외)
`LICENSE`(MIT) · `Directory.Build.props` 로 버전 중앙화 · `net8.0;net10.0` 멀티타겟 ·
NuGet 메타/SourceLink/snupkg/XML 문서 · `TreatWarningsAsErrors` + `IsAotCompatible` ·
`dependabot.yml` · `CHANGELOG.md` / `CONTRIBUTING.md` / `CODE_OF_CONDUCT.md` / `SECURITY.md` /
`.editorconfig` / 이슈·PR 템플릿.

> **GitHub Actions 워크플로는 저장소 소유자 요청으로 제거했다.** 한 번 만들었던
> `ci.yml`(Windows+Linux 매트릭스) 과 `release.yml`(태그 푸시 시 NuGet publish) 은 삭제된
> 상태다. 따라서 **빌드·테스트 검증은 로컬에서 수동으로 해야 한다** —
> `CONTRIBUTING.md` 에 PR 전 실행할 명령을 적어 두었다. 나중에 파이프라인이 필요해지면
> 이 커밋 이전 이력에서 두 워크플로를 되살릴 수 있다.

### 3.2 테스트 ✅ — **66개, 0 실패**
`JobDispatcherNET.Tests` (xUnit, net8.0+net10.0). 직렬화 보장 / P0 회귀 7건 / bounded 큐 /
타이머(정확도·취소·주기·예외 내성) / 예외 격리·faulted / 워커 supervisor / 셧다운 / 풀 /
Sequencer / 메트릭 / 실행 모드 / async·Ask / 동시성 리뷰 회귀 8건 / 스트레스(`Category=Stress`).
스트레스 테스트가 P0-7 을, 적대적 리뷰가 P0-8 의 6건을 발견했다.

### 3.3 벤치마크 ✅
`JobDispatcherNET.Benchmarks` (BenchmarkDotNet). 단일 actor 처리량(closure vs `TState`) /
다중 actor / ping-pong 지연 / 타이머 / 거부 비용 / 풀 on-off /
**대안 비교**(raw `Channel<T>`, Dataflow `ActionBlock`). Akka.NET·Proto.Actor 는 의존성이 무거워
TODO 로 남김. 스모크 런 실측: ping-pong 3.875µs(inline) / 4.365µs(scheduled), **할당 0**.

---

## 4. P2 — 성능 (부분 완료)

측정 없이 바꾸지 않는다는 원칙을 지켰다.

| # | 항목 | 상태 |
|---|---|---|
| 4.1 | Job 풀 구조 | ⏸ 벤치(`PoolEffect`)는 준비, 교체 판단은 데이터 확보 후 |
| 4.2 | 큐 자료구조 | 🔸 `Channel` → `ConcurrentQueue` (bounded 락 제거). intrusive MPSC 는 미착수 |
| 4.3 | Flush 스핀 | ✅ 카운터 기반 즉시 탈출로 무한 스핀 제거 |
| 4.4 | 타이머 정확도 | 🔸 `TimerPrecision` + Windows 해상도 opt-in 제공. **베어 서버 실측은 미완** |
| 4.5 | 메트릭 카운터 | ✅ `StripedCounter` (캐시라인 분리) |
| 4.6 | 워커 스레드 옵션 | ✅ `ThreadPriority` / `MaxStackSize` / `BackgroundThreads` |
| 4.7 | `ThreadLocal` 접근 | ✅ `[ThreadStatic]` 로 교체 |
| 4.8 | `TimerRegistry` | ✅ 제거(obsolete no-op 셸만 유지) |

---

## 5. P2 — 생태계 (완료, Unity 제외)

- **5.1 `JobDispatcherNET.Extensions.Hosting`** ✅ — `AddJobDispatcher(...)`, `IHostedService`,
  `IHealthCheck`(Healthy/Degraded/Unhealthy 4개 상태 실검증).
- **5.2 `JobDispatcherNET.Extensions.Logging`** ✅ — `MicrosoftLoggerAdapter`. 코어는 무의존 유지.
  `samples/Observability` 에서 OpenTelemetry 연동 실증.
- **5.3 네트워크 샘플** ✅ — `samples/PipelinesServer`(System.IO.Pipelines + 길이 프리픽스 바이너리
  프레이밍 + MessagePack) / `samples/LoadClient`(헤드리스 부하 도구, 지연 백분위 리포트).
  실측: 200 클라이언트 / 20초 → 42,952 송신, 87,364 수신, p50 0.37ms / p99 1.75ms,
  서버 측 82,203 job 실행에 **drop 0 / fail 0**, `drained=True` 로 종료.
  프레이밍 분할 처리는 `--selftest` 로 검증(1~55바이트 청크, 55개 절단 prefix).
- **5.4 `dotnet new` 템플릿** ✅ — `jobdispatcher-server`. 생성 → 빌드 → TCP 실동작까지 검증.
- **5.5 Unity(`netstandard2.1`)** ❌ **보류.** 근거: 새 타이머 서비스가 `PeriodicTimer` 의존을
  없애 장벽 하나는 사라졌지만, 여전히 `System.Threading.Channels`(제거됨) 대신 쓰는
  `ConcurrentQueue` 는 OK, `System.Diagnostics.Metrics`·`PriorityQueue`·컬렉션 식 등이 폴리필을
  요구한다. 실제 Unity 사용자 수요가 확인되기 전에는 타깃을 늘리는 유지보수 비용이 이득보다 크다.

---

## 6. 문서 (완료)

- **6.1 README** ✅ — 영어를 기본(`README.md`), 한국어를 `README.ko.md` 로. 배지, 30초 예제,
  **대안 비교표**(`ActionBlock`/`Channel`/Akka/Orleans), **"쓰지 말아야 할 때"** 절, 깨진
  `docs/architecture.html` 링크 제거.
- **6.2 `docs/`** ✅ — `concepts`(스레딩 보장표) / `guarantees` / `timers` / `shutdown` / `tuning` /
  `pitfalls` / `adr/`(0001~0004 + 기존 AOI 설계 문서 이관) / `benchmarks`.
  `GUIDE.html` 2개도 `docs/` 로 이동.
- **6.3 Book·예제 정정** ✅ — 13개 장을 새 API 기준으로 정정. 특히 9장의 "디스패처 없이도 지연
  실행이 트리거된다"는 잘못된 서술(P0-3)과 12장의 `GameWorker`/`InboundCommands` 설명을 교체.
- **6.4 커뮤니티 문서** ✅ — §3.1 참조.

---

## 7. 원래 계획 대비 달라진 점

| 계획 | 실제 | 이유 |
|---|---|---|
| `DoAsync(Func<Task>)` 오버로드 | `RunAsync` / `AskAsync` 로 개명 | 람다에서 `Func<T>` 와 `Func<Task<T>>` 가 모호 |
| `JobMetrics.Snapshot()` 정적 유지 | 인스턴스 메서드 + 정적은 `GetSnapshot()` | 같은 시그니처의 정적·인스턴스 메서드 공존 불가 |
| Exclusive 재진입을 우선순위 큐로 | 예약(reservation) + ThreadPool continuation | 훨씬 단순하고, "actor 를 통째로 멈춘다"는 의미와 정확히 일치 |
| 세션 10회로 나눠 진행 | 1회에 통합 실행 | 코어 변경이 서로 얽혀 있어 분할이 오히려 위험 |

---

## 8. §8 결정 사항 — 채택된 기본값

문서에 적힌 기본값대로 진행했다. 되돌리려면 아래를 바꾸면 된다.

1. 라이선스 **MIT** (`LICENSE`) — ⚠️ C++ 원본 저장소 링크는 저장소에 정보가 없어 비워 두었다.
   `README.md` 하단 TODO 주석 참조. **원저자 표기는 확인 후 채워야 한다.**
2. P0-3 처리 **A(폴백 디스패치)**
3. 타깃 **`net8.0;net10.0`**
4. 전역 static: **v2.1 에서 `[Obsolete]`, v4.0 제거 예고**
5. README **영어 기본** + `README.ko.md`
6. 실행 모드 기본 **`LeaderFlush`** (호환), 문서에서 `Scheduled` 권장
7. 패키지 **분리** (코어 무의존 유지)

---

## 9. 남은 일

| 우선순위 | 항목 |
|---|---|
| **높음** | C++ 원본 저장소 링크와 원저자 표기를 `README.md`·`LICENSE` 에 채우기 (법적/예의 문제) |
| **높음** | NuGet 첫 배포 — 자동 릴리스 워크플로가 없으므로 **수동**으로: `dotnet pack -c Release` 후 `dotnet nuget push`. 패키징 메타데이터는 이미 갖춰져 있다 |
| 중간 | 벤치마크 정식 실행 후 `docs/benchmarks.md`·README 표에 실측치 반영 (§4.1/§4.2 판단 근거) |
| 중간 | 타이머 정확도를 베어 Windows Server 에서 실측 (§4.4) |
| 중간 | GitHub Discussions 개설, 저장소 About/토픽 설정 |
| 낮음 | Akka.NET·Proto.Actor 벤치마크 추가 |
| 낮음 | intrusive MPSC 큐 실험 (§4.2) — 벤치가 병목이라고 말할 때만 |
| 낮음 | Book 영어 번역 |
| 보류 | Unity `netstandard2.1` 타깃 (§5.5) |

---

## 부록. 검증 로그

- 2026-08-30 `dotnet build All.sln -c Release` → 오류 0 / 경고 0 (16 프로젝트)
- 2026-08-31 `dotnet test JobDispatcherNET.Tests -c Release` → 66 통과 / 0 실패 (net8.0, net10.0)
- 2026-08-30 `dotnet run --project AdvancedMmorpgTests` → 6/6 PASS
- 2026-08-30 `dotnet run --project ExampleConsoleApp` → `Test count: 41` (수정 전 26), 정상 종료
- 2026-08-30 템플릿: pack → install → generate → build(경고 0) → TCP 접속 후 에코 응답 확인
- 2026-08-30 `samples/Observability` → OpenTelemetry 콘솔 익스포터로 전 계측기 출력, exit 0
- 2026-08-30 벤치마크 스모크: ping-pong 3.875µs / 4.365µs / 49.055µs, 할당 0
- 2026-08-31 소크(NPC 300, tick 100ms, 워커 8, 25초) → 73,125 job 실행, **drop 0 / fail 0 / 재기동 0**,
  `pendingTimers=300` (NPC 당 정확히 1개 — P0-7 회귀 없음), 경고 없이 종료
- 2026-08-31 `samples/PipelinesServer` + `LoadClient` (100 클라이언트 / 10초) → PASS,
  p50 0.51ms / p99 4.68ms, 서버 `drained=True`
- 2026-08-31 `dotnet build All.sln -c Release --no-incremental` → 오류 0 / 경고 0 (16 프로젝트)
