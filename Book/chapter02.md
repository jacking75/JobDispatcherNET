# Chapter 02: Actor 모델과 직렬 실행의 마법

## 2.1 "직렬 실행"이 뭔가요?

직렬(Serial) 실행이란 한 번에 하나씩 순서대로 처리하는 것입니다.

```
병렬(Parallel) 실행:       직렬(Serial) 실행:
┌──────────────────┐       ┌──────────────────┐
│ 작업1 ────────── │       │ 작업1            │
│ 작업2 ────────── │       │       ▼          │
│ 작업3 ────────── │       │     작업2         │
│ (동시에!)        │       │       ▼          │
└──────────────────┘       │     작업3         │
                           │ (순서대로!)       │
                           └──────────────────┘
```

직렬 실행이면 같은 데이터에 동시에 접근하는 일이 없으므로 lock이 필요 없습니다!

---

## 2.2 큐(Queue)가 직렬 실행을 만드는 원리

JobDispatcherNET에서 모든 Actor는 **Channel(채널)**이라는 큐를 가집니다. 외부에서 작업을 넣으면(DoAsync), Actor는 큐에서 하나씩 꺼내 처리합니다.

```
                   외부 스레드들
         Thread-A    Thread-B    Thread-C
            │            │           │
            │ DoAsync()  │ DoAsync() │ DoAsync()
            ▼            ▼           ▼
         ┌────────────────────────────────┐
         │       Channel<JobEntry>        │
         │  ┌────────┐┌────────┐┌──────┐ │
         │  │ 작업 1 ││ 작업 2 ││작업3 │ │
         │  └───┬────┘└───┬────┘└──┬───┘ │
         └──────┼─────────┼────────┼─────┘
                │         │        │
                ▼ (순서대로)        │
         ┌──────────────────────────┐
         │       Flush 루프         │
         │  작업1 실행               │
         │  작업2 실행  ← 하나씩!   │
         │  작업3 실행               │
         └──────────────────────────┘
```

여러 스레드가 동시에 DoAsync를 호출해도, Channel 안에서 순서가 정해지고 하나씩 실행됩니다.

---

## 2.3 AsyncExecutable 기본 사용법

모든 Actor는 `AsyncExecutable`을 상속합니다.

```csharp
// 이렇게 만들면 됩니다!
public class Player : AsyncExecutable
{
    private int _hp = 100;      // lock 없이 안전하게 쓸 수 있는 필드

    // ✅ 외부에서 호출하는 진입점 — DoAsync로 큐에 넣기만 합니다
    public void TakeDamage(int damage)
        => DoAsync(() => ProcessTakeDamage(damage));

    // ✅ 실제 처리 — 항상 이 Actor의 큐에서 직렬 실행됩니다
    private void ProcessTakeDamage(int damage)
    {
        _hp -= damage;           // lock 없이 안전! (직렬 실행 보장)
        Console.WriteLine($"HP: {_hp}");
        if (_hp <= 0)
            ProcessDie();
    }

    private void ProcessDie()
    {
        Console.WriteLine("사망!");
    }
}
```

---

## 2.4 큐 기반 직렬 실행의 증명

코드로 직접 확인해봅시다:

```csharp
var player = new Player();

// 10개 스레드에서 동시에 데미지를 입힙니다
var tasks = Enumerable.Range(0, 10)
    .Select(i => Task.Run(() => player.TakeDamage(10)))
    .ToArray();

await Task.WhenAll(tasks);
// 결과: HP = 0 (정확히 100 - 10*10)
// lock 없이도 정확합니다!
```

```
시간 ─────────────────────────────────────────────────────►

Thread-1: TakeDamage(10) → DoAsync → 큐에 넣기 끝
Thread-2: TakeDamage(10) → DoAsync → 큐에 넣기 끝
Thread-3: TakeDamage(10) → DoAsync → 큐에 넣기 끝
...

         큐: [데미지10][데미지10][데미지10]...[데미지10]
              ↑ 순서대로 처리 →

HP: 100 → 90 → 80 → 70 → 60 → 50 → 40 → 30 → 20 → 10 → 0
         (한 번에 하나씩, 결과가 정확합니다!)
```

---

## 2.5 코딩 컨벤션 — Handle/Process 패턴

JobDispatcherNET 예제들은 일관된 코딩 패턴을 따릅니다:

```csharp
public class SomeActor : AsyncExecutable
{
    // ────────────────────────────────────────────────────
    // 외부 진입점 (public)
    // 규칙: "큐에 넣기만 하고 즉시 반환"
    // ────────────────────────────────────────────────────

    public void HandleSomething(int value)
        => DoAsync(() => ProcessSomething(value));
    //            ↑ 딱 이것만! 실제 처리는 private로 분리

    // ────────────────────────────────────────────────────
    // 실제 처리 (private)
    // 규칙: "Actor 큐에서 직렬 실행, 여기서 상태 변경"
    // ────────────────────────────────────────────────────

    private void ProcessSomething(int value)
    {
        // 여기서 안전하게 상태를 바꿀 수 있습니다
        _someState = value;
    }
}
```

왜 이렇게 나누나요?

```
┌─────────────────────────────────────────────────────────┐
│  Handle* vs Process* 분리의 이유                         │
├─────────────────────────────────────────────────────────┤
│  1. 디버거 스택트레이스가 명확해집니다                   │
│     "HandleMove → DoAsync → Flush → ProcessMove"        │
│                                                          │
│  2. grep으로 찾기 쉽습니다                               │
│     "ProcessMove를 grep → 실제 처리 로직 바로 찾기"     │
│                                                          │
│  3. 의도가 명확합니다                                    │
│     Handle = "접수만 함"                                │
│     Process = "실제 처리"                               │
└─────────────────────────────────────────────────────────┘
```

---

## 2.6 Actor 간 메시지 패싱

Actor끼리 서로에게 메시지를 보낼 수 있습니다. 이것이 진짜 Actor 모델입니다:

```csharp
public class Room : AsyncExecutable  // Room도 Actor!
{
    private List<Player> _players = new();

    // Room Actor에게 채팅 메시지 전달을 부탁합니다
    public void BroadcastChat(string message)
        => DoAsync(() => ProcessBroadcastChat(message));

    private void ProcessBroadcastChat(string message)
    {
        // Room 큐 안에서 실행 — _players 접근 안전!
        foreach (var player in _players)
        {
            // Player Actor의 큐에 메시지 전달을 넣습니다
            player.ReceiveMessage(message);  // player의 DoAsync 호출
        }
    }
}
```

```
Chat 이벤트 발생
      │
      ▼
room.BroadcastChat("안녕!")      ← 큐에 넣기
      │
      ▼
[Room 큐에서 실행]
ProcessBroadcastChat("안녕!")
  │
  ├─► player1.ReceiveMessage("안녕!")   ← player1 큐에 넣기
  ├─► player2.ReceiveMessage("안녕!")   ← player2 큐에 넣기
  └─► player3.ReceiveMessage("안녕!")   ← player3 큐에 넣기
                │
                ▼
         각 Player 큐에서
         직렬 실행됨
```

Room은 _players 목록에 lock 없이 접근하고, 각 Player에게 메시지를 보낼 때도 lock 없이 안전합니다. **모든 상태 접근이 직렬화됩니다.**

---

## 2.7 Actor 모델의 핵심 규칙

```
╔═══════════════════════════════════════════════════════════╗
║  Actor 모델의 황금률                                      ║
╠═══════════════════════════════════════════════════════════╣
║                                                           ║
║  ✅ Actor 내부 상태는 Actor 자신의 큐에서만 접근한다      ║
║  ✅ 외부에서는 DoAsync로 메시지를 보내기만 한다          ║
║  ✅ Actor 간 통신은 DoAsync를 통한 메시지 패싱으로 한다   ║
║                                                           ║
║  ❌ Actor 필드를 외부 스레드에서 직접 읽거나 쓰지 않는다 ║
║  ❌ Actor 메서드 안에서 다른 Actor의 큐를 기다리지 않는다 ║
║     (데드락 위험!)                                        ║
║                                                           ║
╚═══════════════════════════════════════════════════════════╝
```

---

## 2.8 언제 lock이 아직 필요한가?

Actor 모델이 완벽한 것은 아닙니다. 다음 상황에서는 여전히 주의가 필요합니다:

```csharp
// 예외 상황 1: 여러 Actor가 동시에 접근하는 공유 인덱스
// ConcurrentDictionary 같은 스레드 안전한 컬렉션을 사용합니다
private static readonly ConcurrentDictionary<int, Entity> _index = new();

// 예외 상황 2: Actor 외부에서 읽어야 하는 경우
// GetSnapshot() 패턴을 사용합니다
public SomeSnapshot GetSnapshot()
{
    var ev = new ManualResetEventSlim(false);
    SomeSnapshot? result = null;
    DoAsync(() =>
    {
        result = new SomeSnapshot(/* 현재 상태 복사 */);
        ev.Set();
    });
    ev.Wait();    // ← 큐가 처리할 때까지 대기
    return result!;
}
```

이 패턴들은 Chapter 10에서 채팅 서버 예제로 자세히 다룹니다.

---

## 2.9 정리

```
이번 장에서 배운 것
──────────────────────────────────────────────
✓ 직렬 실행 = 한 번에 하나씩 순서대로 처리
✓ Channel(큐)이 직렬 실행을 자동으로 보장
✓ Actor = AsyncExecutable을 상속한 클래스
✓ Handle*(외부 진입) / Process*(실제 처리) 패턴
✓ Actor 간 통신 = DoAsync를 통한 메시지 패싱
✓ lock 없이도 데이터 일관성 보장됨
```

---

*[← Chapter 01](./chapter01.md) | [→ Chapter 03: AsyncExecutable — 모든 것의 기반](./chapter03.md)*
