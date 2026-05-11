namespace JobDispatcherNET;

/// <summary>
/// 큐가 가득 찼을 때 정책.
/// </summary>
public enum DropPolicy
{
    /// <summary>새 작업을 즉시 거부하고 OnDropped 콜백 호출. 호출자가 backpressure 인지.</summary>
    Reject,

    /// <summary>새 작업을 거부하되 OnDropped는 호출하지 않음(가장 조용함).</summary>
    Silent,
}

/// <summary>
/// AsyncExecutable 인스턴스의 동작을 제어하는 옵션.
/// 게임 서버처럼 long-running 환경에서는 unbounded 큐가 OOM 벡터가 되므로
/// MaxQueueSize 를 명시하는 것을 권장한다.
/// </summary>
public sealed record JobOptions
{
    /// <summary>기본값: 큐 무제한(예전 동작 그대로). 신규 코드는 명시적으로 한도를 설정할 것.</summary>
    public static readonly JobOptions Default = new();

    /// <summary>actor 큐의 최대 작업 수. null = 무제한.</summary>
    public int? MaxQueueSize { get; init; }

    /// <summary>큐가 가득 찼을 때 정책. MaxQueueSize 가 null 이면 무시됨.</summary>
    public DropPolicy DropPolicy { get; init; } = DropPolicy.Reject;

    /// <summary>큐 만원으로 작업이 거부됐을 때 호출되는 콜백(actor, dropped job). DropPolicy.Reject 일 때만.</summary>
    public Action<AsyncExecutable, JobEntry>? OnDropped { get; init; }
}
