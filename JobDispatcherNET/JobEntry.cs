using System.Collections.Concurrent;

namespace JobDispatcherNET;

/// <summary>
/// 모든 작업 항목의 기반.
/// </summary>
public abstract class JobEntry
{
    public abstract void Execute();
}

/// <summary>
/// 람다(Action) 기반 풀링 작업.
/// 풀 크기 상한이 있어 피크 시점에 오버플로된 인스턴스는 GC 에 위임된다(메모리 무한 증가 방지).
/// </summary>
public sealed class Job : JobEntry
{
    private static readonly ConcurrentBag<Job> Pool = new();
    private static long _poolSize;

    /// <summary>
    /// 풀에 보관할 수 있는 최대 Job 수. 게임 서버 환경에서 영구 고점에 맞춰 설정.
    /// 기본 16384 — 동시 in-flight 작업이 이 이상이면 초과분은 GC 에 맡김.
    /// </summary>
    public static int MaxPoolSize { get; set; } = 16 * 1024;

    /// <summary>현재 풀에 들어있는 Job 수 (메트릭용).</summary>
    public static long PoolSize => Interlocked.Read(ref _poolSize);

    private Action? _action;

    private Job() { }

    /// <summary>풀에서 한 개를 임대(없으면 신규).</summary>
    public static Job Rent(Action action)
    {
        if (Pool.TryTake(out var job))
            Interlocked.Decrement(ref _poolSize);
        else
            job = new Job();
        job._action = action;
        return job;
    }

    public override void Execute()
    {
        try
        {
            _action?.Invoke();
        }
        finally
        {
            _action = null;
            // 풀 상한 초과 시 GC 에 맡김 (Add 안 함).
            if (Interlocked.Read(ref _poolSize) < MaxPoolSize)
            {
                Interlocked.Increment(ref _poolSize);
                Pool.Add(this);
            }
        }
    }
}

/// <summary>
/// 제네릭 state 기반 풀링 작업.
/// <see cref="Job"/> 와 달리 closure 객체를 만들지 않아 GC 압력이 낮다.
///
/// 사용 예:
///   actor.DoAsync((float x, float y) => actor.ProcessMove(x, y), newX, newY);
///   // → 내부적으로 Job&lt;ValueTuple&lt;float,float&gt;&gt;.Rent(static (state, _) => ..., (newX,newY))
/// </summary>
public sealed class Job<TState> : JobEntry
{
    private static readonly ConcurrentBag<Job<TState>> Pool = new();
    private static long _poolSize;

    public static int MaxPoolSize { get; set; } = 16 * 1024;
    public static long PoolSize => Interlocked.Read(ref _poolSize);

    private Action<TState>? _action;
    private TState? _state;

    private Job() { }

    public static Job<TState> Rent(Action<TState> action, TState state)
    {
        if (Pool.TryTake(out var job))
            Interlocked.Decrement(ref _poolSize);
        else
            job = new Job<TState>();
        job._action = action;
        job._state = state;
        return job;
    }

    public override void Execute()
    {
        try
        {
            if (_action is { } a && _state is { } s) a(s);
            else _action?.Invoke(default!);
        }
        finally
        {
            _action = null;
            _state = default;
            if (Interlocked.Read(ref _poolSize) < MaxPoolSize)
            {
                Interlocked.Increment(ref _poolSize);
                Pool.Add(this);
            }
        }
    }
}
