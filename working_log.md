# Working Log

## 2026-09-04 10:38 KST - 개발 중 사용한 리뷰 문서 정리

- 코드 리뷰 작업 중 임시로 작성했던 `docs/review-0.10.0.md`, `docs/review-followup-2026-09-03.md`를 삭제했다. 두 문서 모두 리뷰 계획/추적용이었고 내용은 이미 코드에 반영되어 `CHANGELOG.md`와 `docs/` 내 정식 문서(예: `docs/benchmarks.md`)에 남아 있어 별도 보관이 불필요했다.
- `docs/benchmarks.md`에서 삭제된 문서를 가리키던 링크를 일반 텍스트 설명으로 교체해 깨진 링크를 없앴다.
- `docs/README.md` 색인에는 애초에 두 문서가 포함되어 있지 않아 추가 수정은 필요 없었다.

## 2026-09-04 02:12 KST - 후속 리뷰 구현: S1~S21 · S24 · S25 반영

- `docs/review-followup-2026-09-03.md`의 25건 중 **23건을 구현**했다. 테스트 100 → 123개, net8.0/net10.0 · Debug/Release 전부 통과.
- **S15/S16(회귀, 최우선)**: 드레인이 "요청한 흐름 자신"을 세지 않도록 `AsyncLocal<AsyncExecutable?>`로 async 작업 소유자를 전파. `DisposeAsync`는 자기 async 작업 1건(+Exclusive의 서스펜션 예약)을 면제하고, `DrainAsync`는 자기 시스템의 async 작업 1건을 뺀다. 문서의 1안(진단 예외)과 2안(`AsyncFlowOwner`)은 같은 상황에 다른 결과를 요구하므로 **2안을 기본으로 삼고, 2안이 구제 불가능한 경우(큐에 다른 작업이 남은 Exclusive 액터)에만 1안의 예외**를 남겼다. 같은 인프라로 S9(첫 await 이후 자기 Ask를 못 잡던 가드)도 함께 고쳤다.
- **S1**: Hosting 확장이 게이트를 열어 둔 채 드레인하도록 되돌리고 `RefuseNewWorkOnShutdown` 옵션 신설. Hosting 패키지에 테스트가 하나도 없어서 생긴 문제라 통합 테스트 2개를 추가했다(기본/옵트인이 서로 다른 결과를 내는지 검증).
- **S17**: `ObservedJobFailure` 마커 예외 + `Settle` 훅으로 `Ask`/`AskAsync`/`RunAsync` 실패를 메트릭·연속 실패 스트릭에 반영. `OnJobError`는 `RunAsync`만(관측자가 없을 수 있으므로), `Ask` 계열은 `ReportAwaitedFailures`로 옵트인.
- **S18/S19/S20/S21**: 타이머 예약 시점 상한(`MaxPendingTimers` + `DropReason.TimerQueueFull` + 취소 엔트리 lazy purge), `Scheduled` 판정을 `CurrentSystem` 기준으로(시스템 격리), 마지막 워커는 예산을 넘겨도 재시작(`KeepLastWorkerAlive`) + 교체가 없으면 죽는 스레드가 레디 큐를 비우고 나감, 막힌 드레인의 `PulseAll`을 조건부 `Pulse`로.
- **S2**: 액터 fan-out의 두 번째부터는 레디 큐로(`FanOutToWorkers`, 기본 true). 문서 권고대로 (a)안이며 액터 단위로 끌 수 있다.
- **S3·S4·S5·S6·S7·S8·S10~S14·S24·S25**: 스핀의 `Sleep(1)` 제거, `async void` 추적(`OperationStarted/Completed`), `DisposeAsync` 동시 호출 공유 TCS, 풀 상한 배치 올림, 무제한 Sequencer의 공유 RMW 제거, 드레인 델리게이트 캐시, 예외 경로의 서스펜션 회수, `Task.WaitAny` 기반 `AskSync`, 서로게이트 안전 절단, 거부된 tick의 `TimersFired` 제외, `PoolSize` 음수 방지, CAS-Enqueue 사이 실패 시 카운터 복구, 타이머 폴백 경고 재무장.
- **S22·S23은 코드를 바꾸지 않았다.** 문서가 "측정 후"로 적어 둔 항목이고 그 판단이 옳다 — 엔트리 풀링은 핸들 세대 번호가, thread-static 재구성은 풀 저장 구조 변경이 따라온다. 대신 판단 근거가 될 **벤치마크 하니스 4개**(`ActorRingThroughput`, `SequencerThroughput`, `TimerArmAndCancel`, `JobStateShape`)를 추가했다. S21은 결정적 자동 테스트를 만들 수 없어(프로세스 CPU 측정) 테스트 없이 반영.
- 문서 6종(shutdown/pitfalls/guarantees/timers/tuning/benchmarks)과 CHANGELOG를 함께 갱신했다.

## 2026-09-03 16:48 KST - 후속 리뷰: A·B·C 반영 이후 안정성·성능 재검토 (`docs/review-followup-2026-09-03.md`)

- 코어 라이브러리 전체와 Hosting 확장을 처음부터 다시 읽어 `aa2483d` 기준으로 리뷰. 코드는 변경하지 않고 발견 14건(S1~S14)을 심각도·위치·재현·수정 코드·테스트 계획과 함께 문서화.
- High 없음. Medium–High 2건: Hosting 확장이 `refuseNewWork: true`로 종료해 드레인 중 연쇄 작업이 `ShuttingDown`으로 버려지는 문제(S1), 액터→액터 fan-out이 스레드 로컬 `ExecuterQueue`에만 쌓여 워커 풀을 우회하는 설계 한계(S2, 링 벤치마크 평탄 곡선이 근거).
- Medium: `Flush` 스핀의 `Thread.Sleep(1)` 승격(S3), `async void`/미대기 async 람다가 드레인에 안 보이는 A3의 사각지대(S4), `DisposeAsync` 동시 호출 시 첫 호출자 hang(S5). 나머지는 Medium–Low/Low.
- 오늘 바꾼 경로의 불변식(드레인 Dekker, Exclusive 상태 머신+async 추적, 타이머 상태 머신, `JobPool` 단일 소유권, lifecycle lock, StopAsync 순서)은 다시 따라가 "문제 없음" 절에 근거와 함께 기록.
- **2차 패스(2026-09-03 17:15 KST)**: "문제가 있다"는 전제로 다시 검토. 의심 5건을 콘솔 프로브로 전부 재현, 새 결함 3건 추가 — **S15(High, A3 회귀)** Interleaved async 작업 안에서 `await this.DisposeAsync()`가 영원히 hang(드레인이 호출자 자신의 `_pendingAsync`를 기다림), S16 async 작업 안 `await system.StopAsync()`가 타임아웃 전체 소모 후 `drained=false`, S17 `Ask`/`RunAsync` 실패가 `OnJobError`·`TotalJobsFailed`·`MaxConsecutiveFailures`를 우회하고 실패한 `Ask`가 스트릭을 리셋. 각각 재현 출력·원인·수정 코드·테스트를 문서에 추가하고 권장 순서를 S15 → S1 → S17로 조정.
- **3차 패스(2026-09-03 21:32 KST)**: 앞 두 패스와 다른 종류를 노려 4건 프로브 전부 재현 — S18 `DoAsyncAfter`/`DoAsyncEvery`가 예약 시점에 `MaxQueueSize`를 안 봐 상한 4인 액터에 타이머 10,000개(세션당 무제한 메모리), S19 `Scheduled` 판정이 `IsWorkerThread`라 시스템 B 액터가 시스템 A 워커에서 실행(격리 누수), S20 마지막 워커 영구 정지 시 레디 큐의 액터가 영원히 좌초되는데 새 액터는 inline으로 멀쩡, S21 `DrainAsync`의 2ms `PulseAll`(오늘 A10(d))이 막힌 드레인 동안 8 워커에서 2초에 CPU 94ms. 관찰 4건(S22 타이머 락·할당, S23 공유 제네릭 thread-static 비용, S24 CAS-Enqueue 사이 OOM, S25 죽은 상태/영구 침묵 플래그) 추가. 총 25건.

## 2026-09-03 16:13 KST - 리뷰 C1~C6 성능 개선 (C4는 측정 후 보류)

- 먼저 리뷰의 측정 조건(8생산자×250k, 액터 1000개 / 링 64×16)을 재현하는 하니스를 만들어 기준선을 측정한 뒤 변경했다. 각 셀은 워밍업 후 7회 중앙값.
- C1(필수): `Job`/`Job<TState>` 풀을 `ConcurrentBag`+공유 카운터에서 **스레드 로컬 스택 + 32개 배치 공유 교환**(`JobPool<T>`)으로 교체. 8워커 `Scheduled` 2.03→18.37 M/s, 1워커 3.67→11.90, `LeaderFlush` 12.43→29.29. 기준 코드는 워커를 늘리면 **느려졌는데**(3.67→2.03) 이제 빨라진다(11.90→18.37).
- C2: `JobDispatcherOptions.SpinBeforeParkIterations`(기본 10) 추가 — park 전 짧은 스핀으로 `_waiters`를 0으로 유지해 생산자가 signal lock을 건너뛰게 함.
- C3: `_readyDepth`를 `StripedCounter` 2개로. C5: 타이머 `Enqueue`가 필요할 때만 `Pulse`, `_dueBuffer`는 `ToArray` 대신 버퍼 2개 스왑. C6: `StripedCounter` 셀을 128바이트로 패딩.
- C4(intrusive MPSC 액터 큐)는 **보류**. 별도 마이크로벤치에서 `ConcurrentQueue` 대비 1생산자 구간이 3회 중 2회 더 느렸고 런마다 부호가 뒤집힌다. 게다가 Vyukov 큐는 dequeue한 노드가 다음 센티널이 되어 `JobEntry.Execute()`의 자기 재활용 계약(public)을 깨야 한다. 측정으로 부호조차 확인되지 않는 이득에 ADR 0004 불변식이 걸린 큐를 재작성할 근거가 없다고 판단. 측정치·판단 근거를 `docs/benchmarks.md`에 기록.
- 풀 의미 변경(`MaxPoolSize`=공유 풀 상한, `PoolSize`=공유 풀만)에 맞춰 풀 테스트 4개를 할당량 측정 기반으로 재작성 + 1개 추가. 100개 전부 통과(net8.0/net10.0).

## 2026-09-03 15:45 KST - 리뷰 B1~B6 수정 (남용 내성 · 취약점)

- B1: `Sequencer<T>`에 `maxPending`(세션 백프레셔)과 `maxItemsPerDrain`(워커 독점 방지) 생성자 인자 추가. `PendingCount`를 `ConcurrentQueue.Count` 대신 전용 카운터로 바꾸고 `MaxPending`/`DroppedCount` 노출. 샘플 3곳(AdvancedMmorpgServer, PipelinesServer, 프로젝트 템플릿)과 README 예제도 상한을 걸고 거부를 처리하도록 수정.
- B2: `JobSystem.Post`를 `bool` 반환으로 변경 — 게이트가 닫혔거나 disposed면 큐에 넣지 않고 `false`. `Sequencer`는 스케줄 거부 시 드레인 클레임을 되돌려 큐가 영구히 "이미 예약됨" 상태로 막히지 않게 함.
- B3: 액터 `Name`의 제어문자를 `?`로 치환하고 128자로 절단 — 플레이어 닉네임이 로그 라인을 위조하지 못하게.
- B4: `JobSystemOptions.MinTimerPeriod`(기본 1ms) 신설. 그 아래 주기의 `DoAsyncEvery`는 `ArgumentOutOfRangeException`.
- B5: `JobSystemOptions.DefaultMaxQueueSize` 신설 — 액터가 `MaxQueueSize`를 지정하지 않으면 시스템 기본값 적용. `AsyncExecutable.MaxQueueSize`로 실제 적용 상한 조회 가능.
- B6: Exclusive 액터가 자기 작업 안에서 자신에게 `Ask`/`AskAsync` 하면 DEBUG(`DetectBlockingWaitOnWorker`)에서 예외. 블로킹이 아니라 `GuardBlockingWait`가 못 잡던 교착.
- 테스트 12개 추가(98개 전부 통과, net8.0/net10.0). 8개는 옛 동작에서 실패 확인. 기존 `ExclusiveActorStaysExclusiveAcrossManyAsyncHandshakes`의 잠재 레이스(Done 도달 시점과 카운터 감소 시점 차이)도 함께 수정.

## 2026-09-03 12:05 KST - 리뷰 A7~A12 수정 (A절 전체 완료)

- A7: `AskSync`가 `Task.Wait` 대신 완료 핸들을 기다리도록 바꿔 작업 예외를 `AggregateException`으로 감싸지 않고 원본 그대로 던지게 함.
- A8: `MeterOptions.Tags`로 계측기에 `jobdispatcher.system` 태그(시스템 이름) 부착 — 한 프로세스의 두 `JobSystem`을 구분 가능.
- A9: `RunWorkerThreadsAsync`의 disposed 검사를 `_lifecycleLock` 안으로 이동 — 시작/종료 경합 시 가짜 "worker crashed" 로그 제거.
- A10: `TryStop`을 (a) 스레드별이 아닌 전체 예산 하나로, (b) 자기 자신 Join 생략, (c) `TryStopAsync` 추가 후 `StopAsync`/`DisposeAsync`가 사용, (d) 종료·드레인 시 `SignalAllWork`(PulseAll)로 변경.
- A11: `Sequencer` 드레인이 aborted 상태에서도 dequeue-and-discard 하도록 바꾸고 `Abort`가 마지막에 `TryScheduleDrain()` 호출 — Abort와 경합한 `Enqueue` 항목의 영구 잔류 제거.
- A12: 반복 타이머가 `Disposed` 액터에 거부당하면 재무장하지 않고 스스로 은퇴 — `PendingTimerCount`가 영원히 1로 남아 `StopAsync`가 타임아웃되던 경로 제거.
- 회귀 테스트 8개 추가(86개 전부 통과, net8.0/net10.0). 7개는 옛 동작에서 실패하는 것을 확인(A11은 레이스라 재현율 약 75%).

## 2026-09-03 11:39 KST - 리뷰 A4·A5·A6 수정 (로거 예외 가드, 워커 재시작 안전화, 타이머 취소 시점)

- A4: `SafeJobLogger`로 라이브러리 내부 로그 호출을 전부 감싸고, 타이머 루프를 반복 단위(+연속 실패 백오프)와 항목 단위로 가드. `Flush`의 회계를 `finally`로 옮기고 `RunFlushLoop`에 리더십 복구 안전망 추가.
- A5: `MaxRestartBackoff`(기본 1분) 신설로 백오프 지수 증가 상한 설정, `TryRestart` 전체를 try/catch로 감싸 프로세스 종료 위험 제거, `Thread.Sleep` 대신 중지 토큰 대기로 변경. `OperationCanceledException`은 실제 중지 중일 때만 정상 종료로 처리.
- A6: `TimerEntry`를 4상태 머신(Armed/Fired/Executed/Cancelled)으로 재작성하고 콜백 Job이 엔트리를 state로 들도록 변경. 발화 후 액터 큐에서 대기 중인 콜백도 `Cancel()`로 취소 가능해짐(계약 변경 — CHANGELOG/timers.md/ADR 0003에 기록).
- 회귀 테스트 5개 추가(78개 전부 통과, net8.0/net10.0). 새 테스트가 옛 동작에서 실제로 실패하는 것까지 확인 — A4는 테스트 호스트 프로세스가 죽는 것으로 재현.

## 2026-09-03 10:40 KST - 리뷰 A1·A2·A3 버그 수정 (드레인 핸드셰이크, async 연속 작업, 드레인 조건)

- A1: `AsyncExecutable.DisposeAsync`의 드레인 핸드셰이크를 `Interlocked.Exchange`로 발행하도록 고쳐 store-load 재배치로 신호가 유실되던 경로를 차단. 상한을 줄 수 있는 `DisposeAsync(TimeSpan)`/`DisposeAsync(CancellationToken)` 오버로드 추가(실패 시 예외 대신 `false`).
- A2: Interleaved `await` 연속 작업 전용 진입점 `AdmitContinuation`을 만들어 `MaxQueueSize`와 `TryReserve`(Disposed/ShuttingDown/Faulted)를 우회. 거부 시 `RunAsync`/`AskAsync` Task가 영구 미완료되던 hang 제거.
- A3: `JobSystem.PendingAsyncJobs`/`AsyncExecutable.PendingAsyncJobs` 카운터를 추가해 `DrainAsync`·`DisposeAsync`가 await 중인 async 작업을 기다리게 함. 드레인 타임아웃 로그에 `async=` 추가.
- 회귀 테스트 6개 추가(72개 전부 통과, net8.0/net10.0). 새 테스트가 옛 동작에서 실제로 실패하는 것까지 확인. `docs/{shutdown,pitfalls,guarantees,tuning}.md`와 CHANGELOG 갱신.

## 2026-09-02 20:09 KST - 0.10.0 코어 라이브러리 리뷰: 버그·취약점·성능 개선 계획 문서화

- `JobDispatcherNET/*.cs` 전체와 Hosting/Logging 확장을 검토해 `docs/review-0.10.0.md`에 정리. 저장소 코드는 변경하지 않음.
- 버그 12건(A1 `DisposeAsync` Dekker 재배치로 드레인 신호 유실, A2 Interleaved 연속 작업이 `QueueFull`로 거부되어 Task 영구 미완료, A3 `DrainAsync`가 await 중 async 작업 미포함, A4 타이머 스레드 예외 가드 부재, A5 워커 재시작 백오프 오버플로로 프로세스 종료 가능, A6 발화 후 미실행 타이머 취소 불가 등)과 남용 내성 6건(B1 `Sequencer` 무제한 큐 등)을 심각도·구현 코드·테스트 계획과 함께 기록.
- A2·A3·A6·A7은 스크래치패드 콘솔 재현 코드로 실제 동작 확인. 기존 테스트 66개는 모두 통과.
- 성능은 측정 후 판단: `Job` 풀(`ConcurrentBag`+공유 카운터)이 워커 확장을 막고 있어 스레드 로컬 풀로 바꾸면 3~5배(예: 4워커 링 9.05→47.48 M/s). 이것만 "필수"로, 나머지(워커 스핀, 카운터 스트라이프, MPSC 큐 등)는 선택으로 분류.

## 2026-09-02 10:41 KST - 예제 프로젝트를 samples 디렉토리로 이동

- ExampleSectorServer, ExampleMmorpgServer, ExampleConsoleApp, ExampleChatServer, AdvancedMmorpgTests, AdvancedMmorpgServer, AdvancedMmorpgClient 7개 프로젝트를 `samples/` 밑으로 이동 (`git mv`).
- 이동한 프로젝트들의 `.csproj`/`.sln`에서 `JobDispatcherNET.csproj`를 가리키는 `ProjectReference` 상대 경로를 `..\` 한 단계 더 추가해 수정.
- `All.sln`의 프로젝트 경로를 `samples\...`로 갱신하고, 기존 samples 솔루션 폴더(PipelinesServer 등과 동일하게) 밑에 nest 되도록 `NestedProjects` 항목 추가.
- `.editorconfig`의 `AdvancedMmorpgTests` glob 경로, `README.md`/`README.ko.md`의 샘플 표와 `dotnet run` 예시, `AdvancedMmorpgServer`/`AdvancedMmorpgClient`의 `README.md` 내 `cd` 경로를 새 위치에 맞게 수정.
- `dotnet build All.sln` 로 전체 빌드 확인 (경고/오류 0개).
