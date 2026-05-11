# Chapter 04: JobEntry와 오브젝트 풀링

## 4.1 왜 풀링(Pooling)이 필요한가?

게임 서버에서는 매 초 수십만 번의 작업이 발생합니다. 매번 새 객체를 만들면:

```
초당 100,000번 DoAsync 호출
        │
        ▼
  새 Job 객체 100,000개 생성
        │
        ▼
  GC(가비지 컬렉터)가 100,000개를 수거해야 함
        │
        ▼
  GC 실행 시 게임 서버 수십 ms 멈춤! (STW: Stop-The-World)
        │
        ▼
  플레이어: "렉이다!!!"
```

풀링(Pooling)이란 객체를 재사용하는 기법입니다. 사용 후 버리지 않고 풀에 돌려놓고, 다음 번에 새로 만들지 않고 풀에서 꺼내 씁니다.

---

## 4.2 JobEntry 클래스 계층 구조

```
JobEntry (abstract)         ← 모든 작업의 기반
    │
    ├── Job                 ← 람다(Action) 기반 작업
    │                          풀링 지원
    │
    └── Job<TState>         ← 제네릭 state 기반 작업
                               클로저 없이 state 전달
                               풀링 지원
```

---

## 4.3 JobEntry 기반 클래스

```csharp
public abstract class JobEntry
{
    public abstract void Execute();
}
```

딱 하나의 메서드만 있습니다. "실행해라"입니다. 단순함이 핵심입니다.

---

## 4.4 Job 클래스 — 람다 기반 풀링 작업

```csharp
public sealed class Job : JobEntry
{
    // ───────────────────────────────────────────────
    // 풀 (ConcurrentBag = 스레드 안전한 가방)
    // ───────────────────────────────────────────────
    private static readonly ConcurrentBag<Job> Pool = new();
    private static long _poolSize;

    /// <summary>
    /// 풀 최대 크기. 기본 16,384개.
    /// 초과분은 GC에 맡깁니다 (메모리 무한 증가 방지).
    /// </summary>
    public static int MaxPoolSize { get; set; } = 16 * 1024;

    // 인스턴스 필드
    private Action? _action;

    private Job() { }  // ← 외부에서 new Job() 불가! Rent를 써야 함

    // ───────────────────────────────────────────────
    // Rent — 풀에서 가져오기 (없으면 새로 만들기)
    // ───────────────────────────────────────────────
    public static Job Rent(Action action)
    {
        if (Pool.TryTake(out var job))      // 풀에 있으면 꺼내기
            Interlocked.Decrement(ref _poolSize);
        else
            job = new Job();               // 없으면 새로 생성

        job._action = action;
        return job;
    }

    // ───────────────────────────────────────────────
    // Execute — 실행 후 풀에 반납
    // ───────────────────────────────────────────────
    public override void Execute()
    {
        try
        {
            _action?.Invoke();
        }
        finally
        {
            _action = null;  // 람다 참조 해제 (메모리 누수 방지)

            // 풀 크기가 한도 미만이면 반납, 초과면 GC에 맡김
            if (Interlocked.Read(ref _poolSize) < MaxPoolSize)
            {
                Interlocked.Increment(ref _poolSize);
                Pool.Add(this);  // 풀에 반납!
            }
        }
    }
}
```

풀의 동작을 그림으로:

```
처음 시작: Pool = []

첫 번째 DoAsync:
  Rent() → Pool이 비어있음 → new Job() 생성
  Execute() → 실행 후 Pool.Add(job) → Pool = [job1]

두 번째 DoAsync:
  Rent() → Pool.TryTake() → job1 재사용!
  Execute() → 실행 후 Pool.Add(job1) → Pool = [job1]

세 번째 DoAsync:
  같은 job1 재사용...
  (GC에게 할당 없음!)
```

---

## 4.5 Job\<TState\> — 클로저 없는 풀링 작업

```csharp
public sealed class Job<TState> : JobEntry
{
    private static readonly ConcurrentBag<Job<TState>> Pool = new();
    private static long _poolSize;

    public static int MaxPoolSize { get; set; } = 16 * 1024;

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
            if (_action is { } a && _state is { } s)
                a(s);
            else
                _action?.Invoke(default!);
        }
        finally
        {
            _action = null;
            _state = default;  // state 참조도 해제!
            if (Interlocked.Read(ref _poolSize) < MaxPoolSize)
            {
                Interlocked.Increment(ref _poolSize);
                Pool.Add(this);
            }
        }
    }
}
```

---

## 4.6 Job vs Job\<TState\> 비교

```
방법 1: 일반 DoAsync (람다)
─────────────────────────────────────────────────

player.DoAsync(() => player.Move(newX, newY));

내부 처리:
  1. () => player.Move(newX, newY)
     ↑ 이 람다가 newX, newY를 "캡처"합니다
     → 클로저(Closure) 객체 생성 (힙 할당!)
  2. Job.Rent(closure) → Job 객체 대여
  3. Job 안에 클로저 저장

할당 발생: 클로저 1개 + (풀에 Job 없을 경우) Job 1개


방법 2: DoAsync<TState> (state 전달)
─────────────────────────────────────────────────

player.DoAsync<(float X, float Y)>(
    static (state) => player.Move(state.X, state.Y),
    (newX, newY));   // state를 명시적으로 전달

내부 처리:
  1. static 람다 → 클로저 없음! (힙 할당 없음)
  2. (newX, newY)는 ValueTuple → 스택 할당
  3. Job<(float, float)>.Rent(staticLambda, (newX,newY))
     → Job<T> 객체 대여 (풀에서)

할당 발생: 0개! (모두 풀에서 재사용)
```

---

## 4.7 ConcurrentBag의 특성

풀로 `ConcurrentBag<T>`를 쓴 이유는 무엇일까요?

```
ConcurrentBag의 특징:
──────────────────────────────────────────────
✓ 스레드 안전 (여러 스레드에서 동시 Add/TryTake 가능)
✓ LIFO에 가까운 동작 (최근에 반납된 것이 먼저 나옴)
  → CPU 캐시 친화적 (최근 객체가 캐시에 남아있을 가능성)
✓ lock보다 가벼운 인터로크 방식 내부 구현
──────────────────────────────────────────────
주의:
△ 순서 보장 없음 (풀이니 순서는 상관없음)
△ 크기 조회가 상대적으로 비쌈 (→ 별도 _poolSize 카운터 사용)
```

---

## 4.8 풀 크기 튜닝

```csharp
// 기본값: 16,384개
// 게임 서버 환경에 맞게 조정 가능

// 예: 동시 in-flight 작업이 최대 50,000개라면
Job.MaxPoolSize = 50_000;

// 메트릭으로 현재 풀 상태 확인
Console.WriteLine($"Job 풀 현재 크기: {Job.PoolSize}");
```

```
풀 크기 설정 가이드:
──────────────────────────────────────────────────────────
너무 작으면:   풀이 자주 비어서 new Job() 생성 → GC 압박
너무 크으면:   사용 안 하는 Job 객체가 메모리 차지

권장: 동시 처리 작업 수의 1.5~2배
      (예: 초당 최대 동시 작업 10,000개 → MaxPoolSize = 20,000)
──────────────────────────────────────────────────────────
```

---

## 4.9 실전 예시 — AdvancedMmorpgServer의 활용

`AdvancedMmorpgServer`의 `NpcActor`에서 실제 사용 예를 봅시다:

```csharp
public sealed class NpcActor : AsyncExecutable
{
    // hot path — 많은 플레이어가 동시에 이 NPC를 공격할 수 있음!
    public void ReceiveDamage(AttackerSnapshot atk, float meleeRange)
        => DoAsync<(NpcActor A, AttackerSnapshot Atk, float R)>(
            // ① static 람다 → 클로저 없음
            static t => t.A.ProcessReceiveDamage(t.Atk, t.R),
            // ② ValueTuple로 state 전달 → 힙 할당 없음
            (this, atk, meleeRange));
```

이렇게 하면:
- 100마리 플레이어가 동시에 같은 NPC를 공격해도
- 클로저 할당이 0개
- 풀에서 `Job<(NpcActor, AttackerSnapshot, float)>` 재사용

---

## 4.10 정리

```
이번 장에서 배운 것
──────────────────────────────────────────────
✓ JobEntry = 작업 항목의 추상 기반 클래스
✓ Job = 람다 기반, ConcurrentBag 풀링
✓ Job<TState> = state 전달, 클로저 없음
✓ Rent() → 풀에서 대여 / Execute() → 실행 후 반납
✓ 풀 크기 한도로 메모리 무한 증가 방지
✓ hot path는 DoAsync<TState>로 GC 압박 최소화
```

---

*[← Chapter 03](./chapter03.md) | [→ Chapter 05: ThreadContext와 TimerQueue](./chapter05.md)*
