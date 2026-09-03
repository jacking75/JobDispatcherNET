# 0.10.0 코드 리뷰 — 버그·취약점·성능 개선 구현 계획

- **대상**: `JobDispatcherNET` 코어 라이브러리(`JobDispatcherNET/*.cs`) 및 `Extensions.Hosting`, 커밋 `1849614` (0.10.0)
- **작성일**: 2026-09-02
- **검증 상태**: 기존 테스트 66개 전부 통과(net10.0, Release). 아래 A2·A3·A6·A7은 별도 콘솔 재현 코드로
  실제 동작을 확인했고, A1·A4·A5는 코드 추적으로 확인했다. 성능 수치는 이 문서 마지막 절의 조건에서
  직접 측정한 값이다.

- **구현 상태**: A절(A1~A12)·B절(B1~B6) 적용 완료(2026-09-03). C(성능)는 미착수.

우선순위는 **A(버그) → C1(성능, 필수) → B(남용 내성) → 나머지 C(성능, 선택)** 순서를 권장한다.

| ID | 분류 | 심각도 | 요약 |
|---|---|---|---|
| A1 | 버그 | High | `DisposeAsync`의 드레인 신호가 메모리 재배치로 유실될 수 있음 → `await actor.DisposeAsync()` 영구 대기 |
| A2 | 버그 | High | Interleaved async 연속 작업이 `QueueFull`/`Faulted`로 거부되면 `RunAsync`/`AskAsync` Task가 영원히 완료되지 않음 (재현) |
| A3 | 버그 | Medium | `DrainAsync`/`StopAsync`가 await 중인 Interleaved async 작업을 in-flight로 세지 않음 (재현) |
| A4 | 안정성 | Medium | 타이머 스레드에 예외 가드가 없음 → 사용자 로거 예외 한 번에 모든 타이머 영구 정지. 플러시 루프 내 로거 예외는 리더 소실 |
| A5 | 안정성 | Medium | 워커 재시작 경로: 백오프 오버플로·예외가 **프로세스를 종료**시킬 수 있음. `OperationCanceledException`을 정상 종료로 오판 |
| A6 | 의미론 | Medium | 발화 후 아직 실행되지 않은 타이머 콜백을 취소할 수 없음. Despawn에서 `Cancel()`해도 tick이 한 번 더 돌 수 있음 (재현) |
| A7 | API | Low | `AskSync`가 작업 예외를 `AggregateException`으로 감싸 던짐 (재현) |
| A8 | 관측성 | Low | 여러 `JobSystem`이 태그 없이 같은 계측기 이름을 게시 → 구분 불가 |
| A9–A12 | 기타 | Low | 시작/종료 경합, `TryStop` 순차 조인, `Sequencer.Abort` 직후 `Enqueue`, 폐기된 액터의 반복 타이머 |
| B1 | 취약점 | Medium | `Sequencer<T>`에 상한이 없음 → 세션당 패킷 플러딩으로 무제한 메모리 증가 |
| B2–B6 | 취약점 | Low | `Post` 무제한, 로그 인젝션, 반복 타이머 최소 주기, 기본 무제한 큐, Exclusive 자기 교착 |
| C1 | 성능 | **필수** | `Job` 풀(`ConcurrentBag` + 공유 카운터)이 모든 스레드가 공유하는 캐시라인을 매 작업마다 건드림 → 처리량 3~5배 손실, 워커 증가 시 확장 실패 |
| C2–C6 | 성능 | 선택 | 워커 스핀 후 park, 레디 큐 깊이 카운터 스트라이프, intrusive MPSC 액터 큐, 타이머 Pulse 절감, 카운터 패딩 |

---

## A. 버그

### A1. `DisposeAsync` 드레인 핸드셰이크의 메모리 재배치 (High)

**위치**: `AsyncExecutable.DisposeAsync` (790행), `SignalDrained` (780행)

```csharp
// DisposeAsync — 현재
var tcs = new TaskCompletionSource(...);
_drainTcs = tcs;                                    // volatile write = release
if (Volatile.Read(ref _remainingTaskCount) > 0)     // acquire load
    await tcs.Task.ConfigureAwait(false);

// Flush — 상대편
var remaining = Interlocked.Decrement(ref _remainingTaskCount);   // full fence
if (remaining == 0) SignalDrained();                              // _drainTcs?.TrySetResult()
```

두 스레드가 서로 다른 변수에 "쓰고 → 상대 변수를 읽는" Dekker 패턴이다. `Flush` 쪽은
`Interlocked`라 full fence지만, `DisposeAsync` 쪽은 release 저장 뒤 acquire 로드로 구성되어
**저장-로드 재배치(store buffer)가 허용된다**. x64에서도 허용되는 유일한 재배치가 바로 이것이다.

가능한 실행 순서:

1. `DisposeAsync`가 `_remainingTaskCount`를 읽음 → 1 (아직 `_drainTcs` 저장은 스토어 버퍼에 있음)
2. 리더가 마지막 작업을 끝내고 `Decrement` → 0, `SignalDrained()`에서 `_drainTcs`를 읽음 → **null**
3. `_drainTcs` 저장이 이제 메모리에 도달
4. `DisposeAsync`는 `tcs.Task`를 영원히 기다림

`Sequencer.Drain`은 같은 이유로 이미 `Interlocked.Exchange`로 고쳐져 있다(주석에 "Dekker handshake" 명시).
같은 수정을 여기에도 적용해야 한다. 세션 액터 수천 개가 접속 종료 시 마지막 작업이 끝나는 순간에 폐기되는
서버에서는 발생 가능한 창이다.

**구현**

```csharp
private TaskCompletionSource? _drainTcs;   // volatile 제거

public virtual async ValueTask DisposeAsync()
{
    if (Volatile.Read(ref _remainingTaskCount) > 0)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _drainTcs, tcs);           // full fence: 저장이 아래 로드보다 먼저 보인다
        if (Volatile.Read(ref _remainingTaskCount) > 0)
            await tcs.Task.ConfigureAwait(false);
    }
    Volatile.Write(ref _completed, 1);
}

private void SignalDrained() => Volatile.Read(ref _drainTcs)?.TrySetResult();
```

추가 권장: `DisposeAsync(TimeSpan timeout)` / `DisposeAsync(CancellationToken)` 오버로드. 워커가 이미
사라진 상태(레디 큐에 발이 묶인 액터)에서는 어떤 구현도 드레인이 끝나지 않으므로 호출자가 상한을 줄 수단이 필요하다.

**테스트**: 하드웨어 재배치라 결정적 재현은 어렵다. 8스레드 × 20,000회 "마지막 작업 종료와 동시에 DisposeAsync"
스트레스를 타임아웃 5초로 돌려 회귀 방지용으로 둔다.

---

### A2. Interleaved 연속 작업이 큐 상한·Faulted로 거부됨 (High, 재현)

**위치**: `AsyncExecutable.ActorSynchronizationContext.Post` (848행) → `DoAsync` → `TryReserve`/`Admit`

`await` 뒤의 연속 작업은 일반 `DoAsync`로 다시 들어오므로 `MaxQueueSize`와 `IsFaulted` 검사를 그대로 받는다.
거부되면 로그 한 줄만 남고 **async 상태 머신은 영원히 멈춘다**. `RunAsync`가 돌려준 Task도, 그 Task에 걸린
`ContinueWith`도 끝나지 않는다. 문서는 "disposed 또는 시스템 정지 시"만 언급하지만 `QueueFull`이 가장 흔한 경로다.
권장 설정(항상 `MaxQueueSize` 지정) + 기본값(Interleaved) + `RunAsync` 조합에서 부하 시 발생한다.

재현(`MaxQueueSize = 1`): async 작업이 await에 들어가 카운트가 0으로 돌아온 뒤, 다른 작업 하나로 슬롯을 채우고
await를 완료시키면 연속 작업이 `QueueFull`로 떨어지고 `RunAsync` Task는 완료되지 않는다.

```
[JobDispatcherNET][Error] Actor 'Parker' refused an async continuation; the awaiting task will never complete.
  RunAsync completed: False
```

**구현** — 연속 작업은 "새 작업"이 아니라 이미 admit된 작업의 후반부이므로 **상한과 상태 검사를 우회**한다.
거부하면 어떤 경우에도 hang이 되므로 항상 실행되어야 한다.

```csharp
// Admit에 우회 플래그 추가
private bool Admit(JobEntry task, bool fromTimer, bool bypassBound = false)
{
    int current;
    while (true)
    {
        current = Volatile.Read(ref _remainingTaskCount);
        if (!bypassBound && _maxQueueSize != 0 && current >= _maxQueueSize)
        { task.Discard(); return Refuse(DropReason.QueueFull); }
        if (Interlocked.CompareExchange(ref _remainingTaskCount, current + 1, current) == current)
            break;
    }
    ...
}

// 연속 작업 전용 진입점: TryReserve(게이트/Disposed/Faulted)를 거치지 않는다
internal void AdmitContinuation(SendOrPostCallback d, object? state) =>
    Admit(Job<(SendOrPostCallback, object?)>.Rent(static t => t.Item1(t.Item2), (d, state)),
          fromTimer: false, bypassBound: true);

// ActorSynchronizationContext.Post
public override void Post(SendOrPostCallback d, object? state) => actor.AdmitContinuation(d, state);
```

- 연속 작업도 `_remainingTaskCount`는 증가시킨다(리더 선출에 필요). 따라서 관측되는 큐 깊이가 `MaxQueueSize`를
  대기 중 연속 작업 수만큼 초과할 수 있음을 문서화한다.
- `Disposed`/`ShuttingDown` 상태에서도 실행한다. "Disposed는 새 작업을 거부한다"는 계약과 충돌하지 않으며,
  A3을 함께 적용하면 드레인이 이 작업들을 기다리므로 실제로는 게이트가 닫히기 전에 끝난다.
- 기존 오류 로그("refused an async continuation")는 제거한다. 발생할 수 없는 경로가 된다.

**테스트**: 위 재현 시나리오를 그대로 `AsyncJobTests.InterleavedContinuationIsNeverRefusedByQueueBound`로 추가.
`Faulted` 액터에서 await 중인 작업도 완료되는지 한 케이스 더.

---

### A3. `DrainAsync`가 await 중인 Interleaved 작업을 세지 않음 (Medium, 재현)

**위치**: `AsyncExecutable.StartAsyncJob` (305행), `JobSystem.DrainAsync` (361행)

Interleaved 모드에서 `fn()`이 미완료 Task를 돌려주면 그 시점에 작업은 "끝난" 것으로 카운트가 내려간다.
await가 걸린 async 작업은 `InFlightJobs`·`ReadyQueueDepth`·`PendingTimerCount` 어디에도 없으므로
`DrainAsync`는 즉시 `true`를 돌려준다. 재현: gate를 잡고 있는 `RunAsync` 하나가 살아있는데
`DrainAsync(300ms)`가 `True`를 반환했다.

결과: `StopAsync`가 드레인 완료로 판단 → 게이트 닫힘 → 타이머·워커 종료. 이후 await가 풀리면 연속 작업은
(A2 적용 후) 워커 없이 스레드풀 스레드에서 inline으로 실행되거나, 이미 정리된 리소스를 건드린다.

**구현** — 시스템 단위 카운터 하나, 액터 단위 카운터 하나.

```csharp
// JobSystem
private readonly StripedCounter _asyncStarted = new(), _asyncCompleted = new();
internal void OnAsyncJobStarted() => _asyncStarted.Increment();
internal void OnAsyncJobCompleted() => _asyncCompleted.Increment();
public long PendingAsyncJobs { get { var done = _asyncCompleted.Value; return Math.Max(0, _asyncStarted.Value - done); } }

// DrainAsync 조건
while (InFlightJobs > 0 || ReadyQueueDepth > 0 || PendingTimerCount > 0 || PendingAsyncJobs > 0)

// AsyncExecutable.StartAsyncJob — task.IsCompleted == false 분기
_system.OnAsyncJobStarted();
Interlocked.Increment(ref _pendingAsync);           // 액터 단위
task.ContinueWith(static (t, s) =>
{
    var (self, completion, exclusive) = ...;
    if (exclusive) self.EndExclusiveSuspension();
    Settle(t, completion);
    self._system.OnAsyncJobCompleted();
    if (Interlocked.Decrement(ref self._pendingAsync) == 0) self.SignalDrainedIfIdle();
}, ...);
```

`DisposeAsync`는 `_remainingTaskCount == 0 && _pendingAsync == 0`을 기다린다. `SignalDrained`는 두 카운터가
모두 0일 때만 완료시키도록 바꾼다(A1의 `Interlocked.Exchange` 방식 유지). 타임아웃 로그 메시지에
`async=…`를 추가해 pitfall 9와 같은 진단이 가능하게 한다.

**테스트**: 재현 시나리오를 `ShutdownTests.DrainWaitsForInterleavedAsyncJobs`로.

---

### A4. 타이머 스레드·플러시 루프의 예외 가드 부재 (Medium)

**위치**: `TimerService.Loop` (174행), `AsyncExecutable.ExecuteJob`/`HandleJobFailure` (697행)

`TimerService.Loop`에는 try/catch가 없다. 스레드 위에서 실행되는 사용자 코드는 다음과 같다:

- `Refuse` → `OnDropped` 콜백(감싸져 있음) → 그 catch 안의 `Logger.Error` (**감싸져 있지 않음**)
- `WarnTimerFallbackOnce` → `Logger.Warn`
- 워커 없음 폴백 시 `RunFlushLoop` → `HandleJobFailure`의 catch 안 `Logger.Error`, watchdog `Logger.Warn`

`IJobLogger` 구현(Serilog 싱크 장애, 디스크 풀 등)이 한 번 던지면 타이머 스레드가 조용히 죽고 **모든 타이머가
영구 정지**한다. P0-2와 같은 증상이며 감독자가 없어 복구되지 않는다. 같은 예외가 `Flush` 안에서 나면
`_remainingTaskCount`가 감소되지 않은 채 리더가 빠져나가 그 액터는 영원히 "누군가 플러시 중"으로 남는다.

**구현**

1. 로거를 시스템 차원에서 한 번 감싼다. 라이브러리 내부 호출은 모두 이 래퍼를 쓴다.

```csharp
internal sealed class SafeJobLogger(IJobLogger inner) : IJobLogger
{
    public bool IsEnabled(JobLogLevel level) { try { return inner.IsEnabled(level); } catch { return false; } }
    public void Log(JobLogLevel level, string message, Exception? exception = null)
    {
        try { inner.Log(level, message, exception); }
        catch { /* 로거 장애가 워커·타이머 스레드를 죽여서는 안 된다 */ }
    }
}
// JobSystem.Logger => _safeLogger (생성자에서 한 번 래핑; Options.Logger가 null이면 JobLog.Current를 매번 감싸지 않도록 캐시)
```

2. 타이머 루프 본문을 감싼다. 연속 실패 시 짧은 백오프를 둔다.

```csharp
while (Volatile.Read(ref _disposed) == 0)
{
    try { LoopOnce(); failures = 0; }
    catch (Exception ex)
    {
        _system.Logger.Error($"Timer thread '{_thread.Name}' iteration failed; continuing", ex);
        if (++failures >= 3) Thread.Sleep(Math.Min(1000, 10 << failures));
    }
}
```

3. `Flush`에서 `ExecuteJob` 이후의 회계(`OnJobRetired`, `Decrement`)가 예외에도 실행되도록 `ExecuteJob` 내부에서
   로거·메트릭 호출 실패를 흡수한다(1번으로 대부분 해결). `RunFlushLoop`의 `finally`에서
   `_remainingTaskCount > 0`이면 `ScheduleOrFlush()`로 리더십을 복구하는 안전망을 추가한다.

**테스트**: 항상 던지는 `IJobLogger`를 주입하고 `OnDropped` 경로·watchdog 경로를 타이머 발화로 유도한 뒤,
이후 타이머가 계속 발화하고 액터가 계속 작업을 처리하는지 확인.

---

### A5. 워커 재시작 경로의 프로세스 종료 위험 (Medium)

**위치**: `JobDispatcherBase.RunWorker` (160행), `TryRestart` (205행)

`TryRestart`는 `RunWorker`의 try/catch **밖**에서 호출된다. 전용 스레드에서 처리되지 않은 예외는 프로세스를
종료시킨다. 던질 수 있는 지점:

- `TimeSpan.FromMilliseconds(RestartBackoff × 2^(attempts-1))`: 상한이 없다. `Thread.Sleep(TimeSpan)`은
  약 24.8일(`int.MaxValue` ms)을 넘으면 `ArgumentOutOfRangeException`. 기본 1초 백오프에서는 26번째 시도,
  `Math.Pow`가 무한대가 되면 `OverflowException`. `MaxRestartsPerWorker`를 크게 준 설정에서 실제 도달한다.
- `Logger.Warn/Error`(A4와 동일), `thread.Start()`의 `OutOfMemoryException`.

또 `catch (OperationCanceledException) { exitedNormally = true; }`는 취소 요청 여부를 보지 않는다.
`IRunnable.Run`이 내부 `Task.Wait`의 취소 등으로 OCE를 던지면 **재시작도 로그도 없이 워커 슬롯이 사라진다**.

**구현**

```csharp
public TimeSpan MaxRestartBackoff { get; init; } = TimeSpan.FromMinutes(1);   // JobDispatcherOptions

// RunWorker
catch (OperationCanceledException) when (_cts.IsCancellationRequested) { exitedNormally = true; }
// 그 외 OCE는 아래 일반 catch로 떨어져 "crashed"로 기록되고 재시작 대상이 된다

if (!exitedNormally && Options.RestartFailedWorkers && ...)
{
    bool restarted = false;
    try { restarted = TryRestart(slot); }
    catch (Exception ex) { System.Logger.Error($"Worker slot #{slot} restart failed; slot is down", ex); }
    if (restarted) return;
}

// TryRestart
var factor = Math.Min(Math.Pow(2, attempts - 1), Options.MaxRestartBackoff / Options.RestartBackoff);
var backoff = TimeSpan.FromMilliseconds(Math.Min(Options.RestartBackoff.TotalMilliseconds * factor,
                                                 Options.MaxRestartBackoff.TotalMilliseconds));
```

**테스트**: `MaxRestartsPerWorker = 40, RestartBackoff = 1ms`로 계속 던지는 `IRunnable` → 프로세스가 살아 있고
로그에 "permanently down"이 남는지. OCE를 던지는 `IRunnable` → `WorkerRestarts` 메트릭이 증가하는지.

---

### A6. "발화됐지만 아직 실행 전" 타이머를 취소할 수 없음 (Medium, 재현)

**위치**: `TimerService.DispatchDue` (263행), `TimerEntry.Cancel` (399행)

타이머 스레드가 `TakeJob()`으로 작업을 가져가 액터 큐에 넣은 순간부터 `Cancel()`은 `false`를 돌려주고
콜백은 실행된다. 액터가 바쁘면(앞에 작업이 있으면) 그 사이 창이 수 ms~수백 ms로 벌어진다. 재현:

```
Cancel() returned False, IsPending=False, ran so far=0
after release: ran=1
```

반복 타이머도 같다. `DispatchDue`의 `IsCancelled` 검사와 `DispatchTimerJob` 사이에 `Cancel()`이 들어오면
tick 하나가 액터 큐에 남고, **Despawn 작업 뒤에 실행된다**. 문서(`timers.md`)는 "`_despawned` 플래그가 더 이상
필요 없다"고 하지만 현재 구현에서는 여전히 필요하다. 죽은 엔티티의 AI tick이 한 번 더 도는 것은 게임 서버에서
실제 버그(NRE, 이미 제거된 섹터 참조)로 나타난다.

**구현** — 콜백을 실행하는 Job이 `TimerEntry`를 state로 들고, **실행 시점**에 취소 여부를 다시 본다.

```csharp
internal sealed class TimerEntry : ITimerHandle
{
    private const int Armed = 0, Fired = 1, Executed = 2, Cancelled = 3;
    private int _state;

    // Execute가 Recycle하므로 발화마다 새로 Rent한다 (C1의 풀 위에서는 할당 없음)
    internal void Dispatch() =>
        _service.System.DispatchTimerJob(Owner, Job<TimerEntry>.Rent(static e => e.Run(), this));

    private void Run()
    {
        if (Volatile.Read(ref _state) == Cancelled) return;        // 큐에 있는 동안 취소됨
        if (!Repeating) Volatile.Write(ref _state, Executed);
        (RepeatAction ?? _oneShotAction)!.Invoke();
    }

    public bool Cancel()
    {
        while (true)
        {
            var s = Volatile.Read(ref _state);
            if (s is Executed or Cancelled) return false;
            if (Interlocked.CompareExchange(ref _state, Cancelled, s) == s)
            {
                _service.OnCancelled();                           // pending 회계는 한 번만
                return true;
            }
        }
    }
}
```

- 반복 타이머는 `Run`에서 `Executed`로 가지 않고 `Armed`를 유지한다.
- `TakeJob`/`_job` 필드는 사라지고 상태 머신이 단일 중재자가 된다. `DiscardAll`은 `Cancel()`을 호출하는 것과 같다.
- 계약 변경: `Cancel()`은 "콜백이 실행되지 않을 것"이면 `true`. 액터 안(자기 Despawn 작업)에서 호출하면
  이후 그 액터에서 tick이 절대 실행되지 않음을 **보장**할 수 있게 된다 — 액터 직렬화 덕분에 `Run`의 검사가
  `Cancel`의 저장 이후에 온다.

**테스트**: 위 재현(바쁜 액터에 발화 → `Cancel()` → 해제) 후 `ran == 0`, `Cancel()==true`.
반복 타이머: tick 큐잉 직후 Despawn 작업에서 `Cancel()` → 이후 tick 0회.

---

### A7. `AskSync`의 `AggregateException` 노출 (Low, 재현)

**위치**: `AsyncExecutable.AskSync` (252행)

`task.Wait(timeout)`은 Task가 실패한 경우 `AggregateException`을 던지므로 뒤의 `GetAwaiter().GetResult()`에
도달하지 못한다. 호출자는 `InvalidDataException` 대신 `AggregateException`을 받는다(재현 확인).

```csharp
public TResult AskSync<TResult>(Func<TResult> func, TimeSpan timeout)
{
    JobDiagnostics.GuardBlockingWait(_system, nameof(AskSync));
    var task = Ask(func);
    if (!task.IsCompleted && !((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeout))
        throw new TimeoutException(...);
    return task.GetAwaiter().GetResult();      // 원본 예외를 그대로 재던짐
}
```

`AsyncWaitHandle`은 첫 호출 시 이벤트 하나를 할당한다. 블로킹 API에는 문제되지 않는다.

---

### A8. 다중 `JobSystem`의 계측기 이름 충돌 (Low)

**위치**: `JobMetrics` 생성자 (78행)

시스템마다 `new Meter("JobDispatcherNET")`를 만들고 같은 계측기 이름을 등록한다. OpenTelemetry/`dotnet-counters`는
두 시스템의 `jobdispatcher.jobs.executed`를 구분할 수 없고 값이 겹쳐 보인다.

```csharp
_meter = new Meter(new MeterOptions(MeterName)
{
    Tags = [new KeyValuePair<string, object?>("jobdispatcher.system", system?.Name ?? "default")],
});
```

`MeterOptions.Tags`는 .NET 8+에서 지원되므로 두 타깃 모두 가능하다.

---

### A9–A12. 기타 (Low)

**A9. `RunWorkerThreadsAsync`의 disposed 검사가 lock 밖** (128행). `TryStop`과 동시에 호출되면 `_cts.Dispose()`
뒤에 새 스레드가 `_cts.Token`을 읽어 `ObjectDisposedException` → 가짜 "worker crashed" 로그. 검사를
`lock (_lifecycleLock)` 안으로 옮기면 끝난다.

**A10. `TryStop`** (261행). (a) 스레드마다 `joinTimeout` 전체를 순차 적용 → 최악 N×timeout. 데드라인을 하나 잡고
남은 시간으로 조인한다. (b) 워커 스레드 안에서(작업이 `system.Dispose()`를 부르면) 자기 자신을 `Join` → 항상
타임아웃 후 오류 로그. `thread == Thread.CurrentThread`면 건너뛴다. (c) `StopAsync`(async) 안에서 동기 `Join`으로
호출 스레드를 최대 5초×N 블로킹 → `TryStopAsync`를 추가해 `Task.Run`으로 오프로드하고 `StopAsync`가 그것을 쓴다.
(d) `SignalWork`는 `Pulse` 하나라 조인 대상이 아닌 워커가 깨어날 수 있다 → 종료용 `SignalAllWork`(`PulseAll`).

**A11. `Sequencer.Abort()` 직후 `Enqueue`** (67행)가 `true`를 돌려주지만 `_aborted` 때문에 아무도 꺼내지 않아
항목이 영원히 남는다. `Drain`이 aborted 상태에서도 dequeue-and-discard 하도록 바꾸고 `Abort`가 마지막에
`TryScheduleDrain()`을 호출하면 누수는 사라진다. 반환값은 문서로 "Abort와 경합한 Enqueue는 true를 돌려주지만
항목은 폐기될 수 있다"고 명시하는 것이 가장 저렴하다.

**A12. 폐기된 액터의 반복 타이머**. `DisposeAsync` 뒤에도 `DoAsyncEvery`는 매 주기 발화 → `Disposed`로 거부 →
`TotalJobsDropped` 증가, `PendingTimerCount`는 계속 1 → `DrainAsync` 영구 대기(pitfall 9). `DoTaskFromTimer`가
`Disposed`로 거부하면 `TimerService`가 그 반복 엔트리를 `Cancel()`한다. 한 줄 수정으로 pitfall 하나가 사라진다.

---

## B. 취약점 · 남용 내성

라이브러리는 프로세스 내부 컴포넌트이므로 공격 표면은 "신뢰할 수 없는 클라이언트 입력이 어디까지 흘러오는가"다.
샘플 서버들이 세션 → `Sequencer` → 액터 구조를 쓰므로 그 경로를 기준으로 본다.

### B1. `Sequencer<T>`에 상한이 없음 (Medium)

**위치**: `Sequencer<T>` 전체

`docs`가 권장하는 패턴은 세션당 `Sequencer` 하나에 수신 패킷을 넣는 것이다. 액터에는 `MaxQueueSize`가 있지만
`Sequencer`의 `ConcurrentQueue<T>`는 무제한이다. 악의적 클라이언트 하나가 처리 속도보다 빠르게 패킷을 보내면
그 세션의 큐가 메모리를 다 쓸 때까지 자란다. 서버 프로세스 전체가 죽는 DoS다.

```csharp
public Sequencer(Action<T> handler, Action<Action> scheduleDrain, Action<Exception>? onError = null,
                 int maxPending = 0)          // 0 = 무제한(호환)
private int _pending;                          // ConcurrentQueue.Count는 O(세그먼트)이고 스핀할 수 있어 별도 유지

public bool Enqueue(T item)
{
    if (Volatile.Read(ref _stopped) != 0) return false;
    if (_maxPending != 0)
    {
        int cur;
        do { cur = Volatile.Read(ref _pending); if (cur >= _maxPending) { Interlocked.Increment(ref _dropped); return false; } }
        while (Interlocked.CompareExchange(ref _pending, cur + 1, cur) != cur);
    }
    else Interlocked.Increment(ref _pending);
    _queue.Enqueue(item);
    TryScheduleDrain();
    return true;
}
// Drain: 항목 하나 처리 후 Interlocked.Decrement(ref _pending)
public int PendingCount => Volatile.Read(ref _pending);
public long Dropped => ...;
```

호출자(네트워크 계층)는 `false`를 받으면 세션을 끊는다. 액터 `MaxQueueSize`와 같은 방식이라 문서도 같은 절에
붙일 수 있다. 추가로 `MaxItemsPerDrain`(기본 무제한)을 두면 패킷 폭주 세션 하나가 워커를 독점하는 것도 막는다
(액터의 `MaxJobsPerFlush`와 대응).

### B2. `JobSystem.Post`의 무제한 적체 (Low–Medium)

`Post`는 `AcceptingWork`를 보지 않고, 워커가 없어도 레디 큐에 쌓인다. 종료 후에도 `Post`가 계속 들어오면
아무도 꺼내지 않는 큐가 자란다. `bool Post(Action)`으로 바꿔 게이트가 닫혔거나(`AcceptingWork == false`)
`_disposed`면 `false`를 돌려준다. 기존 `void` 시그니처는 바이너리 호환이 깨지므로 0.x에서 진행한다.

### B3. 로그 인젝션 (Low)

`Name`이 로그 문자열에 그대로 들어간다(`$"Actor '{Name}' ..."`). 플레이어 닉네임으로 액터 이름을 짓는 서버라면
개행·제어문자로 로그 라인을 위조할 수 있다. 생성자에서 제어문자를 `?`로 치환하고 길이를 제한(예: 128)하거나,
문서에 "Name에 외부 입력을 넣지 말 것"을 명시한다. 전자가 안전하다.

### B4. 반복 타이머 최소 주기 (Low)

`period = TimeSpan.FromTicks(1)`도 통과해 1ms마다 재무장한다. `TimerPrecision.High`면 타이머 스레드가 100%
스핀한다. 주기가 클라이언트 입력(스킬 쿨타임 등)에서 계산되는 서버라면 DoS가 된다. `JobSystemOptions.MinTimerPeriod`
(기본 1ms)를 두고 그 아래는 `ArgumentOutOfRangeException`으로 거부한다.

### B5. 기본 무제한 큐 (Info)

이미 문서화된 트레이드오프다. 기본값을 바꾸지는 않되 `JobSystemOptions.DefaultMaxQueueSize`(null = 무제한)를 추가해
액터가 `MaxQueueSize`를 지정하지 않으면 시스템 기본값을 쓰게 하면, 한 곳에서 전체 상한을 걸 수 있다.

### B6. Exclusive 모드의 자기 교착 (Info)

`AsyncReentrancy.Exclusive` 액터의 async 작업 안에서 **자기 자신**에게 `Ask`하고 await하면 영원히 끝나지 않는다
(액터가 자기 작업이 끝나길 기다리며 정지). `GuardBlockingWait`는 블로킹이 아니라 잡지 못한다. `Ask` 진입 시
`ThreadContext.CurrentExecuter == this && _suspendState != None`이면 DEBUG에서 예외를 던지고, `pitfalls.md`에
항목을 추가한다.

---

## C. 성능

측정 없이 손대지 않는다는 `tuning.md`의 원칙에 따라, 먼저 측정했다. 조건은 마지막 절에 있다.

### 측정 결과 (M jobs/s, 높을수록 좋음)

| 시나리오 | 기준 0.10.0 | B: 스레드로컬 풀 | C1: B + 워커 스핀 | C2: C1 + depth 스트라이프 |
|---|---|---|---|---|
| 외부 생산자 8 → `Scheduled` 액터 1000, 워커 1 | 3.89 | 12.71 | 10.80 | 11.58 |
| 〃 워커 4 | 2.76 | **25.99** | 18.00 | 23.24 |
| 〃 워커 8 | 2.24 | 11.80 | 18.63 | **21.20** |
| 외부 생산자 8 → `LeaderFlush` 액터 1000 (레디 큐 없음), 워커 0 | 11.86 | 53.49 | – | – |
| 액터→액터 링 64개, 워커 1 | 5.96 | 14.32 | – | – |
| 〃 워커 4 | 9.05 | 47.48 | – | – |
| 〃 워커 8 | 9.09 | 38.95 | – | – |
| 단일 액터, 단일 생산자, inline | 6.82 (147 ns/job) | – | – | – |

읽는 법:

- **기준 코드는 워커를 늘려도 빨라지지 않는다.** `Scheduled` 경로는 1→8 워커에서 오히려 느려지고(3.89→2.24),
  액터→액터 링은 4 워커에서 멈춘다. 공유 캐시라인 경합이 확장을 막고 있다는 뜻이다.
- 풀만 바꿔도(B) 전 시나리오가 3~5배 빨라지고 4 워커까지 정상 확장한다. 8 워커에서 다시 꺾이는 것은 레디 큐 경로다.
- C1·C2는 8 워커 구간을 회복시키지만(11.8→21.2) 단일 실행이라 노이즈가 크다. 4 워커에서는 차이가 노이즈 안이다.
- 작업이 필드 증가 하나라 오버헤드가 극대화된 수치다. 실제 1~10µs 작업에서는 상대 이득이 줄지만, **공유
  캐시라인 경합은 작업 크기와 무관하게 확장 상한을 만든다**.

### C1. `Job` 풀 교체 (필수)

**위치**: `JobEntry.cs` 전체

현재 풀은 `ConcurrentBag<Job>` + `static long _poolSize`다. 작업 하나마다 모든 스레드가 공유하는 캐시라인에
RMW가 세 번 들어간다:

1. `Rent` → `Interlocked.Decrement(ref _poolSize)`
2. `Recycle` → `Interlocked.Read` + `Interlocked.Increment(ref _poolSize)`
3. `ConcurrentBag.Add`의 내부 `_emptyToNonEmptyListTransitionCount`: 스레드 로컬 리스트가 0→1이 될 때
   증가하는데, 같은 스레드에서 rent(1→0)·recycle(0→1)을 반복하는 정상 상태에서는 **매 작업마다** 증가한다.

여기에 `Scheduled` 모드에서는 생산자(IO 스레드)가 rent하고 워커가 recycle하므로 생산자의 로컬 리스트는 항상
비어 있고, 모든 `TryTake`가 다른 스레드의 리스트를 `lock`으로 훔친다. `StripedCounter`를 도입한 이유와 정확히
같은 문제가 풀에 그대로 남아 있었다.

**구현** — 스레드 로컬 스택 + 배치 단위 공유 교환

```csharp
public sealed class Job : JobEntry
{
    private const int LocalCapacity = 256;           // 스레드당
    private const int Batch = 32;                    // 공유 풀과 주고받는 단위

    [ThreadStatic] private static Job?[]? t_local;
    [ThreadStatic] private static int t_count;
    private static readonly ConcurrentQueue<Job[]> SharedBatches = new();
    private static int _sharedBatchCount;             // 상한 검사용(정확할 필요 없음)

    public static int MaxPoolSize { get; set; } = 16 * 1024;   // 공유 배치 총량의 상한으로 의미 변경

    public static Job Rent(Action action)
    {
        var count = t_count;
        if (count == 0 && SharedBatches.TryDequeue(out var batch))          // 공유 → 로컬 (32개 한 번에)
        {
            Interlocked.Decrement(ref _sharedBatchCount);
            var local = t_local ??= new Job[LocalCapacity];
            Array.Copy(batch, local, Batch); count = Batch;
        }
        Job job;
        if (count > 0) { var local = t_local!; job = local[--count]!; local[count] = null; t_count = count; }
        else job = new Job();
        job._action = action;
        return job;
    }

    private void Recycle()
    {
        _action = null;
        var local = t_local ??= new Job[LocalCapacity];
        var count = t_count;
        if (count == LocalCapacity)                                          // 로컬 → 공유 (32개 한 번에)
        {
            if (Volatile.Read(ref _sharedBatchCount) * Batch < MaxPoolSize)
            {
                var batch = new Job[Batch];
                Array.Copy(local, LocalCapacity - Batch, batch, 0, Batch);
                Array.Clear(local, LocalCapacity - Batch, Batch);
                SharedBatches.Enqueue(batch);
                Interlocked.Increment(ref _sharedBatchCount);
            }
            else Array.Clear(local, LocalCapacity - Batch, Batch);           // 상한: GC에 맡긴다
            count = LocalCapacity - Batch;
        }
        local[count] = this; t_count = count + 1;
    }
}
```

- 같은 스레드에서 rent/recycle하는 `LeaderFlush`·액터→액터 경로는 공유 메모리를 전혀 건드리지 않는다.
- `Scheduled` 경로(생산자 rent, 워커 recycle)는 32개마다 한 번 공유 큐를 쓴다. 할당은 여전히 0이고
  공유 트래픽은 1/32이다. 위 측정의 B는 이 공유 교환이 없는 순수 로컬 버전(= 교차 스레드에서는 gen0 할당)이므로
  이 설계의 상한선에 해당한다.
- `PoolSize` 메트릭은 `_sharedBatchCount * Batch`로 근사한다(로컬 스택은 셀 수 없다). 문서에 명시.
- `Job<TState>`도 동일하게 바꾼다. 제네릭이라 `TState`마다 별도 static 풀인 점은 유지된다.
- `MaxPoolSize`의 의미가 "공유 배치 상한"으로 바뀌므로 CHANGELOG의 **Changed**에 기록한다.
- 스레드가 죽으면 그 로컬 스택은 GC된다(`[ThreadStatic]`이라 스레드와 함께 사라짐). 누수 없음.

**테스트**: 기존 `JobsAreReturnedToThePoolAfterRunning`·`PoolIsCappedByMaxPoolSize`를 새 의미에 맞게 수정.
`ManyActorsThroughput` 벤치마크에 `Workers = 1, 4, 8`을 추가해 확장 곡선을 기록으로 남긴다.

### C2. 워커의 park 전 짧은 스핀 (선택)

**위치**: `JobDispatcher.WorkerLoop` (331행)

작업이 짧으면 워커는 큐를 비운 직후 `WaitForWork`로 들어가 `_waiters`를 올린다. 그 순간부터 모든 생산자의
`Enqueue`가 `lock(_signal)` + `Pulse`를 하고, 깨어나는 워커는 컨텍스트 스위치를 낸다. 워커가 많을수록 심해지는
것이 8 워커 역확장의 원인이다. park 전 `SpinWait` 10회(수십 µs)만 돌리면 부하 시 `_waiters == 0`이 유지되어
생산자는 lock을 건너뛴다. 유휴 시 비용은 스핀 후 park이므로 사실상 0이다.

```csharp
if (PumpReadyQueue() != 0) continue;
var spinner = new SpinWait();
var found = false;
while (!spinner.NextSpinWillYield)
{
    spinner.SpinOnce(sleep1Threshold: -1);
    if (!System.ReadyQueueIsEmpty) { found = true; break; }
}
if (!found) System.WaitForWork(idleWait);
```

`JobDispatcherOptions.SpinBeforeParkIterations`(기본 10, 0 = 끔)으로 노출하면 저전력 환경에서 끌 수 있다.

### C3. `_readyDepth`를 스트라이프로 (선택)

**위치**: `JobSystem.Enqueue`/`DrainReady` (210행)

레디 항목마다 공유 `int`에 Interlocked ×2. `StripedCounter`로 바꾸고(`Add(-1)` 사용) `ReadyQueueDepth`는
`Math.Max(0, sum)`을 돌려준다. `DrainAsync`가 필요로 하는 "과대평가는 되되 과소평가는 안 됨" 성질은 유지된다
(증가가 enqueue 전, 감소가 실행 후). C2와 합쳐 8 워커에서 11.8→21.2를 봤지만 노이즈가 크니 대상 하드웨어에서
재측정 후 결정한다.

### C4. 액터 큐를 intrusive MPSC 큐로 (선택, 측정 필요)

**위치**: `AsyncExecutable._queue`

액터 큐는 정의상 **다중 생산자·단일 소비자**다(리더가 하나). `ConcurrentQueue<T>`는 MPMC라 dequeue에도 CAS가
있고, 깊이가 32를 넘으면 세그먼트를 할당한다. `JobEntry`가 이미 풀링된 참조 객체이므로 Vyukov MPSC 링크드 큐를
intrusive하게 쓸 수 있다:

```csharp
public abstract class JobEntry { internal JobEntry? Next; }

// enqueue (여러 생산자): Exchange 1회
var prev = Interlocked.Exchange(ref _head, task);
prev.Next = task;                                   // 이 저장 전에는 소비자가 tail.Next == null을 본다

// dequeue (리더 하나): CAS 0회
var next = Volatile.Read(ref _tail.Next);
if (next is null) return false;                     // 비었거나, 생산자가 Exchange와 링크 사이에 있음
_tail = next; return next;
```

- 생산자가 `Exchange` 뒤 `Next` 저장 전에 선점되면 소비자는 "카운트는 >0인데 큐는 비어 보이는" 상태를 본다.
  `Flush`는 ADR 0004 때문에 이미 이 상태에서 스핀하도록 되어 있으므로 불변식은 그대로다.
- 센티널 노드가 필요하다(액터당 1개, 재사용). 마지막으로 dequeue된 노드가 새 센티널이 되므로 **그 노드는 다음
  dequeue까지 풀에 반납할 수 없다** — `Execute`가 곧바로 `Recycle`하는 현재 구조와 충돌한다. 반납을 한 박자
  늦추거나(이전 tail을 반납), 센티널을 별도 더미 노드로 두고 tail 교체 시 값을 복사하는 변형을 쓴다.
- 예상 이득은 작업당 20~40ns와 세그먼트 할당 0이지만, C1~C3 이후에 다시 측정해서 결정한다. 기준 147ns/job의
  구성은 C1 적용 후 다시 프로파일링해야 의미가 있다.

### C5. 타이머 스레드 Pulse 절감·버퍼 재사용 (선택)

`Schedule`은 새 타이머의 due가 힙 머리보다 늦어도 매번 `Pulse`해 타이머 스레드를 깨운다. 공격·스킬마다 타이머를
거는 서버에서 초당 수만 번의 불필요한 wake가 된다. `_queue.TryPeek` 결과보다 빠를 때, 또는 큐가 비어 있었을 때만
`Pulse`한다. `_dueBuffer.ToArray()`는 발화가 있는 매 반복마다 배열을 할당하므로 `List` 두 개를 swap한다.

### C6. `StripedCounter` 패딩 (선택)

`Cell`이 64바이트지만 배열 헤더(16바이트) 때문에 셀 경계가 캐시라인과 어긋나 인접 셀이 라인을 공유한다.
`Size = 128, [FieldOffset(64)]`로 두면 인접 라인 프리페치 쌍(128B)까지 피한다. 비용은 스트라이프당 64바이트.

---

## 검토했고 문제 없음

- **Admission CAS와 Flush의 두 출구**(ADR 0004): 카운터 ≥ 큐 길이 불변식이 모든 경로에서 유지된다.
- **Exclusive 서스펜션 상태 머신**: `Begin`의 CAS, `Flush`의 Pending→Parked, 연속 작업의 Pending→Completed 중
  정확히 하나만 이기고, 예약 해제는 항상 리더십을 가진 쪽이 한다. `ExecuteSynchronously` 연속 작업이 플러시
  스레드에서 inline으로 돌아도 순서가 맞는다.
- **`SignalWork`/`WaitForWork`의 Dekker**: 생산자 쪽은 `ConcurrentQueue.Enqueue`의 tail CAS가, 소비자 쪽은
  `_waiters` Increment가 full fence다. 게다가 `ConcurrentQueue.IsEmpty`는 진행 중인 enqueue(tail은 갔지만
  sequence number가 아직)를 스핀 대기하므로 유실 wake는 없다. 최악의 경우 `IdleWaitMs` 지연.
- **타이머 pending 회계**: one-shot의 `TakeJob` 단일 중재, 반복 타이머의 수명당 1 카운트, 폐기 경로 모두 일관된다.
  (A6에서 상태 머신으로 바꾸면 회계는 `Cancelled` 전이 한 번으로 단순해진다.)
- **`Sequencer`의 드레인 클레임**: `Interlocked.Exchange` 해제 후 큐 재확인. `Stop`의 재확인도 맞다.
- **`TryStop`/`TryRestart`의 lifecycle lock**: 재시작이 종료 스냅샷을 놓치는 경합은 닫혀 있다(A9의 시작 경합만 남음).

---

## 구현 순서 제안

1. **A1, A2, A7** — 각각 10줄 내외, 위험 없음. 같은 커밋으로.
2. **A3** — A2와 함께 async 작업의 수명 회계를 완성한다. `DrainAsync` 조건과 로그 메시지 변경.
3. **C1** — 단독 커밋. 벤치마크 결과를 `docs/benchmarks.md`에 기록.
4. **A4, A5** — 안전 로거 래퍼 + 타이머/재시작 가드. `MaxRestartBackoff` 옵션 추가.
5. **A6** — `TimerEntry` 상태 머신. `timers.md`의 취소 계약 갱신.
6. **B1, B2** — `Sequencer` 상한, `Post` 반환값. 0.x이므로 시그니처 변경 가능. CHANGELOG **Changed**.
7. **A8–A12, B3–B6, C2–C6** — 각자 독립. 측정으로 필요가 확인된 것만.

각 항목에 위에 적은 테스트를 함께 추가한다. A1은 스트레스 테스트만 가능함을 테스트 주석에 남긴다.

---

## 측정 조건

- Windows 11 Pro 10.0.26200, 20 논리 코어, .NET SDK 10.0.400, Release, `ServerGarbageCollection=true`, `TieredPGO=true`
- 라이브러리 옵션: `PublishMeter=false`, `EnableDetailedMetrics=false`, `Logger=NullJobLogger`
- 각 셀은 워밍업 1회 후 **단일 실행** 값이다. BenchmarkDotNet이 아니므로 통계적 신뢰구간은 없다.
  ±20% 정도는 노이즈로 봐야 하며, 배수 차이(기준 vs B)만 결론에 사용했다.
- 작업 본문은 `int` 필드 증가 하나. 외부 생산자 시나리오는 스레드 8개 × 250,000회 `DoAsync<TState>`(static 람다),
  1,000개 액터에 분산. 링 시나리오는 링 64개 × 16 액터, 링당 50,000 hop.
- B/C1/C2 변형은 `JobDispatcherNET/*.cs`를 복사해 해당 부분만 바꾼 뒤 같은 프로그램으로 측정했다. 저장소 코드는
  변경하지 않았다.
