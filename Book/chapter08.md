# Chapter 08: 설정·모니터링·로깅

## 8.1 JobOptions — Actor 큐 설정

`JobOptions`는 각 `AsyncExecutable` 인스턴스의 동작을 제어합니다.

```csharp
public sealed record JobOptions
{
    /// <summary>기본값: 큐 무제한 (메모리 허용하는 만큼)</summary>
    public static readonly JobOptions Default = new();

    /// <summary>
    /// 큐의 최대 작업 수.
    /// null = 무제한 (예전 동작)
    /// 게임 서버에서는 이 값을 반드시 설정하는 것을 권장!
    /// </summary>
    public int? MaxQueueSize { get; init; }

    /// <summary>큐가 가득 찼을 때 정책</summary>
    public DropPolicy DropPolicy { get; init; } = DropPolicy.Reject;

    /// <summary>작업이 거부됐을 때 콜백 (DropPolicy.Reject일 때만)</summary>
    public Action<AsyncExecutable, JobEntry>? OnDropped { get; init; }
}

public enum DropPolicy
{
    /// <summary>거부 + OnDropped 콜백 호출</summary>
    Reject,
    /// <summary>조용히 거부 (콜백 없음)</summary>
    Silent,
}
```

---

## 8.2 왜 MaxQueueSize를 설정해야 하나?

```
MaxQueueSize를 설정하지 않으면 (무제한):

악성 클라이언트 또는 패킷 폭주 상황:
  초당 100,000개 패킷 → 큐에 계속 쌓임
                       ↓
              메모리 계속 증가
                       ↓
           OutOfMemoryException!
                       ↓
                 서버 전체 다운

MaxQueueSize 설정 시:

  초당 100,000개 패킷
  큐가 가득 차면 → 초과분 거부 (DoAsync returns false)
  OnDropped 콜백으로 알림
                ↓
           정상 운영 유지
```

실제 사용 예:

```csharp
// NpcActor.cs (AdvancedMmorpgServer)
public sealed class NpcActor : AsyncExecutable
{
    private const int NpcQueueCapacity = 128;

    public NpcActor(Npc npc, GameWorld world, TimeSpan tickInterval)
        : base(new JobOptions
        {
            MaxQueueSize = NpcQueueCapacity,    // NPC 큐는 128개까지
            DropPolicy = DropPolicy.Reject,      // 초과 시 거부
        })
    { ... }
}

// PlayerActor.cs (AdvancedMmorpgServer)
public sealed class PlayerActor : AsyncExecutable
{
    private const int PlayerQueueCapacity = 256;

    public PlayerActor(Player p, GameWorld world)
        : base(new JobOptions
        {
            MaxQueueSize = PlayerQueueCapacity,  // 플레이어 큐는 256개까지
            DropPolicy = DropPolicy.Reject,
            OnDropped = (actor, _) =>
            {
                if (actor is PlayerActor pa)
                    JobLog.Warn($"[플레이어 #{pa.Id}] 큐 만원 — 작업 드롭");
            },
        })
    { ... }
}
```

---

## 8.3 JobMetrics — 실시간 메트릭 수집

```csharp
public readonly record struct JobMetricsSnapshot(
    long TotalJobsExecuted,     // 총 처리 완료 수
    long TotalJobsDropped,      // 총 거부 수
    long TotalJobsFailed,       // 총 예외 발생 수
    long PendingTimerJobs,      // 대기 중인 타이머 작업 수
    long PendingTimerDispatch,  // TimerDispatchQueue 대기 수
    long ActiveJobPoolSize,     // Job 풀 현재 크기
    long WorkerRestarts);       // 워커 재기동 횟수

public static class JobMetrics
{
    // 내부 카운터들 (Interlocked 기반, lock 없음)
    private static long _totalExecuted;
    private static long _totalDropped;
    private static long _totalFailed;
    private static long _workerRestarts;

    // 외부에서 스냅샷 조회
    public static JobMetricsSnapshot Snapshot() => new(
        TotalJobsExecuted: Interlocked.Read(ref _totalExecuted),
        TotalJobsDropped:  Interlocked.Read(ref _totalDropped),
        TotalJobsFailed:   Interlocked.Read(ref _totalFailed),
        PendingTimerJobs:  TimerQueue.PendingJobsAcrossAllInstances,
        PendingTimerDispatch: TimerDispatchQueue.Count,
        ActiveJobPoolSize: Job.PoolSize,
        WorkerRestarts:    Interlocked.Read(ref _workerRestarts));
}
```

---

## 8.4 메트릭 활용 패턴

```csharp
// 주기적인 헬스체크에서 사용
void PrintHealthStatus()
{
    var metrics = JobMetrics.Snapshot();

    Console.WriteLine("=== JobDispatcherNET 상태 ===");
    Console.WriteLine($"총 처리:     {metrics.TotalJobsExecuted:N0}");
    Console.WriteLine($"총 거부:     {metrics.TotalJobsDropped:N0}");
    Console.WriteLine($"총 실패:     {metrics.TotalJobsFailed:N0}");
    Console.WriteLine($"타이머 대기:  {metrics.PendingTimerJobs}");
    Console.WriteLine($"Dispatch 큐:  {metrics.PendingTimerDispatch}");
    Console.WriteLine($"Job 풀:      {metrics.ActiveJobPoolSize}");
    Console.WriteLine($"워커 재기동:  {metrics.WorkerRestarts}");

    // 경고 조건
    if (metrics.TotalJobsDropped > 0)
        Console.WriteLine("⚠️ 작업 거부 발생! MaxQueueSize 확인 필요");
    if (metrics.TotalJobsFailed > 0)
        Console.WriteLine("⚠️ 처리 실패 발생! 예외 로그 확인 필요");
    if (metrics.WorkerRestarts > 0)
        Console.WriteLine("⚠️ 워커 재기동 발생! 크래시 로그 확인 필요");
}
```

Prometheus/OpenTelemetry 연동 예:

```csharp
// 메트릭 주기적 수집
var timer = new System.Timers.Timer(5000);
timer.Elapsed += (_, _) =>
{
    var snap = JobMetrics.Snapshot();
    // Prometheus 게이지에 push
    jobsExecutedCounter.IncTo(snap.TotalJobsExecuted);
    pendingTimerGauge.Set(snap.PendingTimerJobs);
    workerRestartsCounter.IncTo(snap.WorkerRestarts);
};
```

---

## 8.5 IJobLogger — 로깅 추상화

```csharp
public enum JobLogLevel { Debug, Info, Warn, Error }

public interface IJobLogger
{
    bool IsEnabled(JobLogLevel level);
    void Log(JobLogLevel level, string message, Exception? exception = null);
}

// 전역 로거 접근점
public static class JobLog
{
    private static IJobLogger _instance = new ConsoleJobLogger();

    public static IJobLogger Current
    {
        get => _instance;
        set => _instance = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static void Debug(string message) { ... }
    public static void Info(string message)  { ... }
    public static void Warn(string message)  { ... }
    public static void Error(string message, Exception? ex = null) { ... }
}
```

---

## 8.6 기본 제공 로거들

```csharp
// ① ConsoleJobLogger (기본값)
// Warn 이상만 출력 (Info, Debug 출력 안 함 → hot path Console.WriteLine 방지)
public sealed class ConsoleJobLogger : IJobLogger
{
    public JobLogLevel MinLevel { get; init; } = JobLogLevel.Warn;

    public bool IsEnabled(JobLogLevel level) => level >= MinLevel;

    public void Log(JobLogLevel level, string message, Exception? exception = null)
    {
        var writer = level >= JobLogLevel.Warn ? Console.Error : Console.Out;
        writer.WriteLine($"[JobDispatcherNET][{level}] {message}" +
                         $"{(exception is null ? "" : $"\n{exception}")}");
    }
}

// ② NullJobLogger (로그 완전히 끄기)
public sealed class NullJobLogger : IJobLogger
{
    public bool IsEnabled(JobLogLevel level) => false;
    public void Log(JobLogLevel level, string message, Exception? exception = null) { }
}
```

---

## 8.7 커스텀 로거 연동

Serilog와 연동하는 예:

```csharp
// Serilog 어댑터
public class SerilogJobLogger : IJobLogger
{
    private readonly ILogger _logger;

    public SerilogJobLogger(ILogger logger)
    {
        _logger = logger.ForContext("SourceContext", "JobDispatcherNET");
    }

    public bool IsEnabled(JobLogLevel level) => level >= JobLogLevel.Info;

    public void Log(JobLogLevel level, string message, Exception? exception = null)
    {
        switch (level)
        {
            case JobLogLevel.Debug:
                _logger.Debug(exception, message);
                break;
            case JobLogLevel.Info:
                _logger.Information(exception, message);
                break;
            case JobLogLevel.Warn:
                _logger.Warning(exception, message);
                break;
            case JobLogLevel.Error:
                _logger.Error(exception, message);
                break;
        }
    }
}

// 서버 시작 시
JobLog.Current = new SerilogJobLogger(Log.Logger);
```

Microsoft.Extensions.Logging 연동:

```csharp
public class MsExtJobLogger : IJobLogger
{
    private readonly ILogger _logger;

    public MsExtJobLogger(ILogger<JobDispatcher<GameWorker>> logger)
    {
        _logger = logger;
    }

    public bool IsEnabled(JobLogLevel level) => _logger.IsEnabled(ToLogLevel(level));

    public void Log(JobLogLevel level, string message, Exception? exception = null)
        => _logger.Log(ToLogLevel(level), exception, message);

    private static LogLevel ToLogLevel(JobLogLevel level) => level switch
    {
        JobLogLevel.Debug => LogLevel.Debug,
        JobLogLevel.Info  => LogLevel.Information,
        JobLogLevel.Warn  => LogLevel.Warning,
        JobLogLevel.Error => LogLevel.Error,
        _                 => LogLevel.None,
    };
}
```

---

## 8.8 OnError 글로벌 예외 핸들러

```csharp
// AsyncExecutable 내부에서 발생한 미처리 예외를 전역으로 받기
AsyncExecutable.OnError = ex =>
{
    // Serilog, 파일 로그 등으로 기록
    Log.Fatal(ex, "Actor 작업 처리 중 예외 발생");

    // 알림 시스템 연동 (Slack, 이메일 등)
    AlertSystem.Send($"서버 예외: {ex.Message}");
};
```

---

## 8.9 설정 전체 예시 — 실무 권장 패턴

```csharp
// Program.cs (또는 서버 시작 코드)
static void ConfigureJobDispatcher()
{
    // 1. 로거 설정
    JobLog.Current = new SerilogJobLogger(Log.Logger);

    // 2. 전역 예외 핸들러
    AsyncExecutable.OnError = ex =>
    {
        Log.Error(ex, "Actor 처리 중 예외");
        MetricsService.IncrementError();
    };

    // 3. Job 풀 크기 (최대 동시 작업 예상치에 맞게)
    Job.MaxPoolSize = 100_000;

    // 4. Flush SpinWait 상한 (CPU 사용률과 응답성 트레이드오프)
    AsyncExecutable.MaxFlushSpinIterations = 1000;  // 기본값
}

// 각 Actor 클래스에서
public class PlayerActor : AsyncExecutable
{
    public PlayerActor(Player p, GameWorld world)
        : base(new JobOptions
        {
            MaxQueueSize = 256,
            DropPolicy = DropPolicy.Reject,
            OnDropped = (actor, _) =>
            {
                Log.Warning("Player {Id} queue full", ((PlayerActor)actor).Id);
                MetricsService.IncrementDropped();
            }
        })
    { ... }
}
```

---

## 8.10 정리

```
이번 장에서 배운 것
──────────────────────────────────────────────
✓ JobOptions: MaxQueueSize로 큐 폭주 방지
✓ DropPolicy: Reject(콜백 있음) vs Silent(조용히)
✓ JobMetrics: Interlocked 기반 메트릭 수집
  - 처리/거부/실패 카운터
  - 타이머 대기, 풀 크기, 워커 재기동
✓ IJobLogger: 로깅 추상화
  - ConsoleJobLogger: 기본, Warn 이상만
  - NullJobLogger: 완전 끄기
  - 커스텀: Serilog, M.E.Logging 연동 가능
✓ OnError: 전역 예외 콜백
```

---

*[← Chapter 07](./chapter07.md) | [→ Chapter 09: ExampleConsoleApp](./chapter09.md)*
