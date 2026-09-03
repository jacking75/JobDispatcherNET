# Working Log

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
