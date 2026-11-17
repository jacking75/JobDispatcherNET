# 제6장: MMORPG 서버의 기본 구조와 고급 패턴

ExampleMmorpgServer의 존, 플레이어, 전투와 공간 인덱스를 먼저 분석한다. 이어서 AdvancedMmorpgServer의 JobSystem 소유 구조, NPC AI, 세션 순서 보장, 최적화와 종료 흐름을 비교한다.

## 6.1 ExampleMmorpgServer — 프로젝트 구조

```
ExampleMmorpgServer/
├── Program.cs          ← 시뮬레이션 진입점
├── GameServer.cs       ← 서버 컨테이너
├── GameWorker.cs       ← IRunnable 워커
├── GameZone.cs         ← 존 Actor (플레이어 집합)
├── PlayerActor.cs      ← 플레이어 단위 Actor
├── Player.cs           ← 플레이어 데이터 모델
├── Packets.cs          ← 패킷 정의
└── SpatialIndex.cs     ← 공간 인덱스 (범위 쿼리)
```

---

## 6.2 ExampleMmorpgServer — 전체 구조

```mermaid
graph TD
    SIM[시뮬레이션/네트워크] -->|InboundCommands| GW[GameWorker x4]
    GW -->|cmd| GS[GameServer\n패킷 라우팅]
    GS -->|HandleX| GZ[GameZone Actor\n플레이어 관리]
    GZ -->|Move/Attack/...| PA1[PlayerActor-1]
    GZ -->|Move/Attack/...| PA2[PlayerActor-2]
    GZ -->|ReceiveDamage| PA1
    GZ -->|ReceiveDamage| PA2
    PA1 -->|자기복제| PA1
    GZ -->|5s heartbeat| GZ
```

---

## 6.3 GameZone — 존 Actor

존의 핵심 역할은 **플레이어 관리**와 **공격 라우팅**입니다.

```csharp
public class GameZone : AsyncExecutable
{
    private readonly Dictionary<string, PlayerActor> _actors = [];
    public readonly SpatialIndex Spatial;

    // ── 플레이어 입장/퇴장 ────────────────────────────────

    public void EnterZone(Player player, float spawnX, float spawnY)
        => DoAsync(() => ProcessEnterZone(player, spawnX, spawnY));

    private void ProcessEnterZone(Player player, float spawnX, float spawnY)
    {
        // ① PlayerActor 생성 및 등록
        var actor = new PlayerActor(player, this);
        _actors[player.PlayerId] = actor;

        player.X = spawnX;
        player.Y = spawnY;
        Spatial.Add(player);  // 공간 인덱스에 추가

        // ② 자동 회복 시작
        actor.StartRegen();

        Console.WriteLine($"[존] {player.Name} 입장 ({spawnX:F1},{spawnY:F1})");
    }

    public void LeaveZone(string playerId)
        => DoAsync(() => ProcessLeaveZone(playerId));

    private void ProcessLeaveZone(string playerId)
    {
        if (!_actors.Remove(playerId, out var actor)) return;

        // spatial 제거는 actor 자기 큐에서 — 잔여 Move/Damage 작업과 직렬화된다.
        actor.Despawn();  // ← PlayerActor에게 Despawn 메시지 전달 (즉시 반환)
    }
}
```

> **여기서 `actor.DisposeAsync().Wait()` 을 부르면 안 됩니다.**
> `ProcessLeaveZone` 은 GameZone 의 큐 안에서 실행됩니다. 그 안에서 `PlayerActor.Despawn()`
> 을 부르면, 이 스레드가 이미 GameZone 을 flush 중이므로 `PlayerActor` 는
> `ThreadContext.ExecuterQueue` 에 실려 **이 flush 가 끝난 뒤에** 처리됩니다.
> 그 상태에서 `DisposeAsync().Wait()` 로 드레인을 기다리면, 그 큐를 비워 줄 스레드가
> 바로 지금 멈춰 있는 이 스레드이므로 영구 대기입니다.
>
> 큐 드레인을 기다려야 한다면 **actor 큐 밖에서** 하세요. 실제 `GameZone.DisposeAsync` 는
> 외부 스레드에서 `Despawn` 을 일괄 전송한 뒤 각 `PlayerActor` 를 드레인합니다.
> 더 간단한 방법은 이 장의 `AdvancedMmorpgServer` 예제처럼 `system.StopAsync()` / `system.DrainAsync()` 에 맡기는 것입니다.

---

## 6.4 공격 처리 흐름 — Actor 간 메시지 패싱

근접 공격이 처리되는 과정을 단계별로 봅시다:

```csharp
// GameZone
public void HandleMeleeAttack(string attackerId, string targetId)
    => DoAsync(() => ProcessMeleeAttack(attackerId, targetId));

private void ProcessMeleeAttack(string attackerId, string targetId)
{
    // ① 공격자 Actor 찾기 (GameZone 큐 안 → _actors 안전)
    if (!_actors.TryGetValue(attackerId, out var attacker)) return;
    if (!_actors.TryGetValue(targetId, out _)) return;

    // ② 공격자 Actor에게 "공격해!" 메시지 전달
    attacker.MeleeAttack(targetId, meleeRange: 3.0f);
}

// PlayerActor
public void MeleeAttack(string targetId, float meleeRange)
    => DoAsync(() => ProcessMeleeAttack(targetId, meleeRange));

private void ProcessMeleeAttack(string targetId, float meleeRange)
{
    if (_despawned || !_player.IsAlive) return;

    // ③ 공격자 스냅샷 생성 (불변 데이터!)
    var snapshot = new AttackerSnapshot(
        _player.PlayerId, _player.Name,
        _player.X, _player.Y, _player.Attack);

    // ④ GameZone에게 데미지 전달 요청
    _zone.SendMeleeDamage(targetId, snapshot, meleeRange);
}

// GameZone
public void SendMeleeDamage(string targetId, AttackerSnapshot snapshot, float meleeRange)
    => DoAsync(() =>
    {
        // ⑤ 대상 Actor 찾기 (GameZone 큐 안 → 안전!)
        if (_actors.TryGetValue(targetId, out var target))
            target.ReceiveMeleeDamage(snapshot, meleeRange);
    });

// 대상 PlayerActor
public void ReceiveMeleeDamage(AttackerSnapshot attacker, float meleeRange)
    => DoAsync(() => ProcessReceiveMeleeDamage(attacker, meleeRange));

private void ProcessReceiveMeleeDamage(AttackerSnapshot attacker, float meleeRange)
{
    if (_despawned || !_player.IsAlive) return;

    float dist = _player.DistanceTo(attacker.X, attacker.Y);
    if (dist > meleeRange) return;  // ← 서버사이드 거리 검증!

    int damage = _player.TakeDamage(attacker.Attack);
    if (!_player.IsAlive)
        HandleDeath(attacker.Name);
}
```

전체 흐름:

```
클라이언트 패킷: ATTACK target=player2

GameWorker → server.HandleMeleeAttack("player1", "player2")
    │
    ▼
GameZone Actor 큐:
  ProcessMeleeAttack("player1", "player2")
  → attacker.MeleeAttack("player2", 3.0f)
    │
    ▼
PlayerActor-1(player1) 큐:
  ProcessMeleeAttack("player2", 3.0f)
  → 스냅샷 생성 (불변!)
  → zone.SendMeleeDamage("player2", snapshot, 3.0f)
    │
    ▼
GameZone Actor 큐:
  target = _actors["player2"]
  → target.ReceiveMeleeDamage(snapshot, 3.0f)
    │
    ▼
PlayerActor-2(player2) 큐:
  ProcessReceiveMeleeDamage(snapshot, 3.0f)
  → 거리 검증
  → TakeDamage()
  → HP 차감 (lock 없이!)
  → SendPacket("DAMAGE|50|30")
```

---

## 6.5 AttackerSnapshot — 불변 데이터의 중요성

```csharp
// 불변(readonly) 구조체 — 스레드 간 전달 안전!
public readonly record struct AttackerSnapshot(
    string PlayerId, string Name,
    float X, float Y, int Attack);
```

왜 스냅샷을 쓰나요?

```
직접 Player 객체 전달 시:

PlayerActor-1 큐에서:
  snap = player1 (직접 참조 전달)
  zone.SendMeleeDamage(playerId2, player1_ref, range)

GameZone 큐에서:
  target.ReceiveMeleeDamage(player1_ref, range)

PlayerActor-2 큐에서:
  player1_ref.X 읽기  ← 이 시점에 player1이 이동했을 수 있음!
  → Race Condition!

스냅샷 전달 시:

PlayerActor-1 큐에서:
  snap = new AttackerSnapshot(
      player1.X,  ← 지금 이 순간의 값을 복사!
      player1.Y,
      player1.Attack)

이후 player1이 어디로 이동해도 snap의 값은 변하지 않음
→ 완전 안전!
```

---

## 6.6 범위 공격 처리

```csharp
// 범위 공격 (AoE) 처리
public void AreaAttack(float centerX, float centerY, float radius, float maxCastRange)
    => DoAsync(() => ProcessAreaAttack(centerX, centerY, radius, maxCastRange));

private void ProcessAreaAttack(float centerX, float centerY, float radius, float maxCastRange)
{
    if (_despawned || !_player.IsAlive) return;

    // 시전 거리 검증
    float distToCenter = _player.DistanceTo(centerX, centerY);
    if (distToCenter > maxCastRange) return;

    var snapshot = new AttackerSnapshot(...);

    // GameZone에게 범위 데미지 fan-out 위임!
    _zone.DispatchAreaDamage(snapshot, centerX, centerY, radius);
}

// GameZone
public void DispatchAreaDamage(
    AttackerSnapshot attacker, float centerX, float centerY, float radius)
    => DoAsync(() => ProcessDispatchAreaDamage(attacker, centerX, centerY, radius));

private void ProcessDispatchAreaDamage(
    AttackerSnapshot attacker, float centerX, float centerY, float radius)
{
    // ① 공간 인덱스로 범위 내 플레이어 검색
    var targets = Spatial.QueryRange(centerX, centerY, radius);

    // ② 각 대상에게 AoE 데미지 전달
    foreach (var targetPlayer in targets)
    {
        if (_actors.TryGetValue(targetPlayer.PlayerId, out var targetActor))
        {
            targetActor.ReceiveAreaDamage(attacker, centerX, centerY, radius);
        }
    }
}
```

---

## 6.7 PlayerActor의 자기복제 Regen

```csharp
// 자동 회복 시작 (GameZone이 EnterZone 후 호출)
public void StartRegen() => DoAsync(RegenTick);

private void RegenTick()
{
    if (_despawned) return;  // ← Despawn 시 체인 자동 종료!

    if (_player.IsAlive && _player.Hp < _player.MaxHp)
    {
        int before = _player.Hp;
        _player.Hp = Math.Min(_player.MaxHp, _player.Hp + RegenAmount);
        int healed = _player.Hp - before;

        if (healed > 0)
        {
            _player.SendPacket?.Invoke($"HP_UPDATE|{_player.Hp}|{_player.MaxHp}");
        }
    }

    // 자기복제!
    DoAsyncAfter(RegenInterval, RegenTick);  // 3초 후 다시 실행
}
```

> **v2.1 스타일로 바꾼다면:**
> ```csharp
> private ITimerHandle? _regen;
>
> public void StartRegen()
>     => DoAsync(() => _regen = DoAsyncEvery(RegenInterval, RegenTick));
>
> private void RegenTick()      // 재예약 코드가 사라진다
> {
>     if (_player.IsAlive && _player.Hp < _player.MaxHp) { ... }
> }
>
> private void ProcessDespawn()
> {
>     _despawned = true;
>     _regen?.Cancel();          // _despawned 플래그로 "발화는 하되 무시" 하지 않는다
> }
> ```
> 이 장의 `AdvancedMmorpgServer` 가 정확히 이 형태로 이관되어 있습니다.

---

## 6.8 사망과 부활 처리

```csharp
// ReceiveMeleeDamage 안에서 호출됨 (자기 큐 안)
private ITimerHandle? _respawnTimer;

private void HandleDeath(string killerName)
{
    _player.SendPacket?.Invoke($"DEATH|{killerName}");

    // 5초 후 자동 부활 예약! 반환된 핸들을 보관해 두면 Despawn 시 취소할 수 있다.
    _respawnTimer = DoAsyncAfter(RespawnDelay, ProcessRespawn);
}

private void ProcessRespawn()
{
    if (_despawned) return;

    float oldX = _player.X, oldY = _player.Y;
    _player.Hp = _player.MaxHp;
    _player.X = Random.Shared.NextSingle() * 200f;
    _player.Y = Random.Shared.NextSingle() * 200f;

    // 공간 인덱스 갱신
    _zone.Spatial.UpdatePosition(_player, oldX, oldY);

    _player.SendPacket?.Invoke($"RESPAWN|{_player.X:F1}|{_player.Y:F1}|{_player.Hp}");
}
```

사망 후 타임라인:

```
t=0:   ProcessReceiveMeleeDamage
         → HP = 0
         → HandleDeath() 호출 (직접, 큐 안)
         → DoAsyncAfter(5s, ProcessRespawn)

t=5s:  ProcessRespawn()
         → HP 회복
         → 위치 랜덤 배치
         → Spatial 갱신
         → 클라이언트에 RESPAWN 패킷

t=8s:  RegenTick() (3초 주기 자동 실행)
         → HP 30 회복...
```

---

## 6.9 SpatialIndex — 공간 인덱스

```csharp
public class SpatialIndex
{
    // 그리드 기반 공간 분할
    // 각 셀: ConcurrentDictionary (여러 스레드에서 동시 접근 가능)
    private readonly ConcurrentDictionary<(int cx, int cy), ConcurrentDictionary<string, Player>>
        _grid = new();

    // 범위 쿼리: centerX, centerY에서 radius 이내의 플레이어 목록
    public List<Player> QueryRange(float centerX, float centerY, float radius) { ... }

    // 위치 갱신: 셀이 바뀌면 이전 셀에서 제거 후 새 셀에 추가
    public void UpdatePosition(Player player, float oldX, float oldY) { ... }
}
```

왜 `ConcurrentDictionary`를 쓰나요?

```
SpatialIndex는 여러 Actor가 동시에 접근합니다:

PlayerActor-1 큐: UpdatePosition(player1, ...)
PlayerActor-2 큐: UpdatePosition(player2, ...)
GameZone 큐:      QueryRange(centerX, centerY, ...)

→ 각기 다른 스레드에서 동시 실행 가능
→ ConcurrentDictionary의 내부 버킷 lock이 경합 최소화
→ (개별 PlayerActor 내부 상태는 Actor 큐가 보호)
```

---

## 6.10 PrintStatus — GetSnapshot 패턴

`GameZone.GetSnapshot()` 은 5.17 과 같은 `ManualResetEventSlim` 패턴으로 구현되어 있습니다.
새로 쓴다면 `AskSync` 한 줄입니다:

```csharp
public ZoneSnapshot GetSnapshot()
    => AskSync(() => new ZoneSnapshot(_name, _actors.Values.Select(a => a.Snapshot()).ToList()),
               TimeSpan.FromSeconds(2));
```

`AskSync` 는 타임아웃이 필수이고, actor 큐 안에서 호출하면 데드락 대신 예외를 던집니다 (5.17).

```csharp
public void PrintStatus()
{
    // ★ 메인/콘솔 스레드에서 호출할 것. actor 작업 안에서는 금지.
    var snap = _zone.GetSnapshot();

    Console.WriteLine("========== 서버 상태 ==========");
    Console.WriteLine($"[{snap.Name}] 플레이어: {snap.Players.Count}명");
    foreach (var p in snap.Players)
    {
        Console.WriteLine($"  - {p.Name} ({p.X:F1},{p.Y:F1}) " +
                          $"HP:{p.Hp}/{p.MaxHp} {(p.IsAlive ? "생존" : "사망")}");
    }
}
```

---

## 6.11 ExampleMmorpgServer — 핵심 학습 포인트

```
MmorpgServer 예제에서:
✓ PlayerActor = 플레이어 단위 Actor (병렬!)
✓ GameZone Actor = 전체 존 관리 (직렬!)
✓ 공격 라우팅: 공격자 Actor → GameZone → 대상 Actor
✓ AttackerSnapshot = 불변 데이터로 안전한 스레드 간 전달
✓ 공간 인덱스: ConcurrentDictionary로 다중 Actor 접근 허용
✓ 주기/지연: 예제는 자기복제, 새 코드는 DoAsyncEvery + ITimerHandle.Cancel()
✓ 서버사이드 검증: 거리, 이동 속도 등
✓ actor 큐 안에서 다른 actor 의 드레인을 기다리면 데드락
  → Despawn 은 메시지로 보내고, 드레인은 큐 밖(또는 system.StopAsync)에서
```

---

기본 MMORPG 예제의 전투와 상태 관리 흐름을 확인했다. 이제 고급 예제와 비교하며 현재 API를 적용한 구조와 최적화를 살펴본다.

## 6.12 ExampleMmorpgServer와의 차이점

`AdvancedMmorpgServer` 는 v2.1 API 로 **이관이 끝난** 유일한 샘플입니다. 5장의 채팅 서버와 이 장 앞부분의 MMORPG 예제가
"예전 방식"의 기록이라면, 이 고급 예제는 "지금 권장하는 방식"을 보여 준다.

```
ExampleMmorpgServer (v2.0 스타일)   AdvancedMmorpgServer (v2.1)
────────────────────────────────   ──────────────────────────────────────────
기본 PlayerActor                    DoAsync<TState>로 클로저 없음
옵션 없는 JobDispatcher<GameWorker>  JobSystem + 비제네릭 JobDispatcher
GameWorker.cs (IRunnable +          GameWorker.cs 삭제 — 워커 루프 자체가 없음
  InboundCommands + Sleep(1))
NPC 없음                            NpcActor — DoAsyncEvery 로 도는 AI tick
Sequencer 없음                      Sequencer(system, ...) 로 세션별 순서 보장
자기복제 타이머 + _despawned 플래그   ITimerHandle 보관 → Despawn 시 Cancel()
ManualResetEventSlim 스냅샷          AskSync 스냅샷
4단계 수동 셧다운 + Thread.Sleep(200) system.StopAsync / DrainAsync
설정 파일 없음                       config.json (포트 25100)
NetworkServer 없음                   실제 TCP 네트워크 서버
```

---

## 6.13 GameServer — JobSystem 소유와 셧다운

이 서버는 **자기만의 `JobSystem`** 을 만듭니다. 워커·타이머 스레드·메트릭·셧다운 게이트가
전부 그 안에 들어 있으므로, 종료가 한 줄이 됩니다.

```csharp
public sealed class GameServer : IDisposable
{
    private readonly JobSystem _system;
    private readonly GameWorld _world;
    private readonly NetworkServer _network;
    private JobDispatcher? _dispatcher;

    public GameServer(ServerConfig config)
    {
        _config = config;

        // 이 서버의 워커·타이머·메트릭을 소유하는 시스템 하나.
        _system = new JobSystem(new JobSystemOptions
        {
            Name = "game",
            TimerPrecision = TimerPrecision.Coarse,
            MaxJobDuration = TimeSpan.FromMilliseconds(50),   // 50ms 넘는 작업은 경고
        });

        _world = new GameWorld(config, _system);
        _network = new NetworkServer(this, config.Server.Port);
    }

    public void Start()
    {
        // 사용자 루프가 없는 비제네릭 디스패처: 워커는 할 일이 없으면 시그널을 기다린다.
        // 예전에 필요했던 IRunnable + Thread.Sleep(1) 워커는 사라졌다.
        _dispatcher = new JobDispatcher(_config.Server.WorkerThreads, new JobDispatcherOptions
        {
            System = _system,
            RestartFailedWorkers = true,
            MaxRestartsPerWorker = 5,
            RestartBackoff = TimeSpan.FromSeconds(1),
        });
        _ = _dispatcher.RunWorkerThreadsAsync();

        _world.SpawnInitialNpcs();
        _network.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // ① 외부 입력 차단. 내부 종료 작업은 아직 actor 들이 필요하다.
        _network.Stop();

        // ② 전부 despawn + 타이머 체인 취소 → 시스템이 정적 상태에 도달할 수 있게 만든다.
        _world.Stop();

        // ③ 남은 것을 드레인하고, 타이머 스레드와 워커를 정지.
        var drained = _system.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (!drained)
            JobLog.Warn("[Server] some work was still in flight at shutdown");

        _system.Dispose();
    }
}
```

이 순서가 중요한 이유:

```
① 네트워크 정지
   → IO 스레드가 Sequencer 에 새 패킷을 넣지 않는다
   → 이미 들어온 패킷과 disconnect 마커는 그대로 처리된다 (4.6)

② World.Stop
   → 모든 NpcActor / PlayerActor 에 Despawn 전송
   → 각 actor 가 자기 타이머 핸들을 Cancel()
   → ★ 이게 없으면 주기 타이머가 계속 새 작업을 만들어 드레인이 끝나지 않는다

③ system.StopAsync
   → in-flight 작업 + ready 큐 + 대기 타이머가 0 이 될 때까지 대기
   → AcceptingWork = false
   → 타이머 스레드 정지
   → 이 시스템에 붙은 디스패처들 정지 (워커 Join)
```

> **v2.0 에서는 이랬다**
> ```csharp
> AsyncExecutable.AcceptingWork = false;   // 프로세스 전역 static
> _network.Stop();
> _world.Stop();                            // 내부에 Thread.Sleep(200)
> _dispatcher?.Dispose();
> TimerRegistry.DisposeAll();
> AsyncExecutable.AcceptingWork = true;     // 다음 인스턴스(테스트)를 위해 복구 ←?!
> ```
> 마지막 줄이 전역 static 게이트의 어색함을 그대로 보여줍니다. 프로세스에 서버가 둘이면
> 서로의 셧다운을 건드리게 되죠. 지금은 게이트가 `JobSystem` 안에 있어 복구가 필요 없습니다.

---

## 6.14 GameWorld — Scheduled 모드와 드레인

```csharp
public sealed class GameWorld : AsyncExecutable
{
    private const int WorldQueueCapacity = 10_000;

    public GameWorld(ServerConfig cfg, JobSystem system)
        : base(new JobOptions
        {
            Name = "World",
            System = system,
            MaxQueueSize = WorldQueueCapacity,
            DropPolicy = DropPolicy.Reject,

            // 월드는 콘솔 스레드(status/metrics)에서도 찔린다. Scheduled 모드로 두면
            // 그런 호출자가 월드의 leader 가 되어 비-워커 스레드에서 게임 로직을 돌리는 일이 없다.
            Mode = ExecutionMode.Scheduled,

            OnDropped = static (actor, reason) => JobLog.Warn(
                $"[World] job refused ({reason}), queue={actor.RemainingTaskCount}"),
        })
    { ... }
}
```

`Mode = ExecutionMode.Scheduled` 는 v2.1 에서 추가된 옵션입니다 (2.3). 이 한 줄이
"첫 호출자가 그 자리에서 actor 를 돌린다"는 기본 동작의 부작용 —
**호출자 hijack** — 을 막습니다.

### Stop — Thread.Sleep 대신 실제 드레인

```csharp
public void Stop()
{
    _isStopping = true;

    DoAsync(static w =>
    {
        foreach (var s in w._sessions.Values) s.Close();
        w._sessions.Clear();
        foreach (var na in w._npcs.Values)    na.Despawn();   // 내부에서 tick 타이머 Cancel
        foreach (var pa in w._players.Values) pa.Despawn();   // 내부에서 resync 타이머 Cancel
    }, this);

    // despawn 과 그로 인해 파생된 작업이 전부 끝날 때까지 기다린다.
    if (!System.DrainAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult())
        JobLog.Warn("[World] world did not fully quiesce before shutdown");
}
```

```
v2.0:  Thread.Sleep(200);        // "이 정도면 되겠지"
v2.1:  System.DrainAsync(5s)     // in-flight == 0 && ready == 0 && pendingTimers == 0
                                 // 실제로 정적 상태가 됐는지 확인하고, 안 되면 경고
```

### GetSnapshot — AskSync

```csharp
/// <summary>월드 자기 큐에서 계산되는 일관된 읽기.</summary>
public WorldSnapshot GetSnapshot()
{
    try
    {
        return AskSync(BuildSnapshot, TimeSpan.FromSeconds(2));
    }
    catch (TimeoutException)
    {
        JobLog.Warn("[World] snapshot timed out");
        return new WorldSnapshot(0, 0, 0, 0, 0, RemainingTaskCount);
    }
}
```

`AskSync` 는 actor 작업 안에서 호출되면 예외를 던지므로(`JobDiagnostics.GuardBlockingWait`),
이 메서드가 "워커를 멈춰 세우고 actor 를 기다리는" 데드락이 되는 일은 구조적으로 없습니다.
예전 샘플의 `ManualResetEventSlim` + `ev.Wait()` 조합은 같은 실수를 조용한 행으로 만들었습니다.

---

## 6.15 NpcActor — DoAsyncEvery 로 도는 AI tick

NPC는 스스로 AI를 실행하는 Actor입니다.

```csharp
public sealed class NpcActor : AsyncExecutable
{
    private const int NpcQueueCapacity = 128;   // tick 1개 + 다수 공격자 피격 흡수

    private volatile bool _despawned;
    private ITimerHandle? _tickTimer;      // ★ AI tick 핸들
    private ITimerHandle? _respawnTimer;   // ★ 부활 타이머 핸들

    public NpcActor(Npc npc, GameWorld world, TimeSpan tickInterval)
        : base(new JobOptions
        {
            Name = $"Npc#{npc.Id}",
            System = world.System,
            MaxQueueSize = NpcQueueCapacity,
            DropPolicy = DropPolicy.Reject,
        })
    { ... }

    // ① Start: 자기 큐 안에서 주기 타이머를 무장한다
    public void Start()
        => DoAsync<NpcActor>(static a => a.ProcessStart(), this);

    private void ProcessStart()
    {
        if (_despawned) return;

        // 첫 틱을 0~interval 사이로 흩어서 NPC 50마리가 같은 ms 에 몰리지 않게 한다
        var jitter = TimeSpan.FromMilliseconds(
            Random.Shared.Next(0, Math.Max(1, (int)_tickInterval.TotalMilliseconds)));

        _tickTimer = DoAsyncEvery(_tickInterval, Tick, jitter);
    }

    // ② Tick: AI 메인 루프 — 재예약 코드가 없다!
    private void Tick()
    {
        if (_despawned) return;
        if (_world.IsStopping) return;
        if (!_npc.IsAlive) return;      // 죽은 NPC 는 그냥 아무것도 안 한다

        long now = NowMs();
        float dt = ...;

        switch (_state)
        {
            case AiState.Idle:   TickIdle(now, dt); break;
            case AiState.Chase:  TickChase(now, dt); break;
            case AiState.Attack: TickAttack(now, dt); break;
            case AiState.Flee:   TickFlee(now, dt); break;
        }
    }

    // ③ Despawn: 플래그가 아니라 실제 취소
    private void ProcessDespawn()
    {
        if (_despawned) return;
        _despawned = true;

        _tickTimer?.Cancel();
        _respawnTimer?.Cancel();

        Aoi.LeaveWorldNpc(_world.Spatial, _npc);
    }
}
```

### 자기복제 → 주기 타이머로 바꾸면서 사라진 문제들

```
v2.0 (Tick 마지막 줄에서 DoAsyncAfter(_tickInterval, Tick)):

  ① Tick 안에서 예외가 한 번 나면 → 재예약 줄에 도달 못 함 → 그 NPC 영구 정지
  ② 죽었을 때 체인을 끊고, Respawn 에서 다시 시작해야 했다 (상태 두 벌 관리)
  ③ 종료 시 _despawned 플래그로 "발화는 하되 무시" → 타이머가 계속 작업을 만든다
  ④ 워커가 크래시로 재기동되면 그 스레드에 걸린 tick 체인이 통째로 사라졌다 (P0-2)

v2.1 (DoAsyncEvery + ITimerHandle):

  ① 예외가 나도 다음 틱이 온다
  ② 죽으면 Tick 이 곧바로 return 할 뿐, 체인은 계속 돈다 → Respawn 이 재무장할 필요 없음
  ③ Despawn 에서 Cancel() → 진짜로 멈춘다 → DrainAsync 가 끝난다
  ④ 타이머는 시스템 소유 스레드에 있으므로 워커 재기동과 무관하다
```

`Respawn` 에 재무장 코드가 없다는 점을 확인해 보세요:

```csharp
private void Respawn()
{
    if (_despawned) return;
    _npc.Hp = _npc.MaxHp;
    ...
    // 주기 tick 은 계속 돌고 있었고, 죽어 있는 동안 아무것도 하지 않았을 뿐이다.
    // 다시 무장할 것이 없다.
}
```

---

## 6.16 AI 상태 머신

```
NPC AI 상태 전환:

                ┌─────────────────────────────┐
                │         IDLE                │
                │  (Wander / 플레이어 탐지)   │
                └──────────────┬──────────────┘
                               │ 플레이어 AggroRange 진입
                               ▼
                ┌─────────────────────────────┐
                │         CHASE               │◄──────────┐
                │  (플레이어 추적)            │           │
                └──────────────┬──────────────┘    공격   │
                               │ AttackRange 진입  범위 벗 │어남
                               ▼
                ┌─────────────────────────────┐
                │         ATTACK              │
                │  (공격 쿨다운마다 공격)     │
                └──────────────┬──────────────┘
                               │ HP < FleeHpRatio
                               ▼
                ┌─────────────────────────────┐
                │         FLEE                │
                │  (4초간 도주)               │
                └──────────────┬──────────────┘
                               │ 4초 경과
                               ▼
                            IDLE로 복귀
```

코드로 보면:

```csharp
private void TickChase(long now, float dt)
{
    var target = _world.GetEntity(_targetId);
    if (target is null || !target.IsAlive)
    {
        _state = AiState.Idle; _targetId = -1; return;
    }

    float d = _npc.DistanceTo(target.X, target.Y);

    // 너무 멀어지면 포기
    if (d > _npc.AggroRange * ChaseGiveUpRangeFactor)
    {
        _state = AiState.Idle; _targetId = -1; return;
    }

    // 공격 범위 안에 들어왔으면 공격 상태로
    if (d <= _npc.AttackRange)
    {
        _state = AiState.Attack; return;
    }

    // 플레이어 방향으로 이동
    float dx = target.X - _npc.X, dy = target.Y - _npc.Y;
    float len = MathF.Sqrt(dx * dx + dy * dy);
    if (len < 0.001f) return;
    float step = _npc.MoveSpeed * dt;
    MoveTo(_npc.X + dx / len * step, _npc.Y + dy / len * step);
}
```

---

## 6.17 ReceiveDamage — DoAsync\<TState\> 최적화

```csharp
// ❌ 일반 방법 — 클로저 생성
public void ReceiveDamage_Slow(AttackerSnapshot atk, float meleeRange)
    => DoAsync(() => ProcessReceiveDamage(atk, meleeRange));
//             ↑ this, atk, meleeRange 캡처 → 클로저 힙 할당!

// ✅ 최적화 방법 — 클로저 없음
public void ReceiveDamage(AttackerSnapshot atk, float meleeRange)
    => DoAsync<(NpcActor A, AttackerSnapshot Atk, float R)>(
        // static 람다 → 힙 할당 0
        static t => t.A.ProcessReceiveDamage(t.Atk, t.R),
        // ValueTuple → Job<T> 풀에서 재사용
        (this, atk, meleeRange));
```

이게 중요한 이유:

```
NPC가 50마리, 매 틱 각 NPC가 1~5번 공격 받는다면:

초당 250~1250번 ReceiveDamage 호출

일반 방법:
  그만큼의 클로저 객체 생성
  → GC 압박 → 게임 틱 불규칙

DoAsync<TState> 방법:
  Job<(NpcActor, AttackerSnapshot, float)> 풀에서 재사용
  → 0 추가 할당 → 부드러운 틱
```

거부됐을 때도 `Job` 은 풀로 돌아옵니다 (2.21). NPC 큐가 128 로 제한되어 있어 몰매를 맞는
NPC 는 실제로 거부가 발생하는데, 그 경로에서 풀이 새지 않습니다.

---

## 6.18 PlayerActor — 이동 속도 검증과 AOI 타이머

```csharp
private void ProcessMove(float newX, float newY)
{
    if (_despawned || !_player.IsAlive) return;

    var oldX = _player.X;
    var oldY = _player.Y;

    // ★ 이동 속도 제한 — 속임 클라이언트 방어!
    var dx = newX - oldX, dy = newY - oldY;
    var dist = MathF.Sqrt(dx * dx + dy * dy);
    var maxStep = _player.MoveSpeed * 0.5f;   // 0.5초 분량

    if (dist > maxStep && dist > 0.0001f)
    {
        var k = maxStep / dist;               // 최대 이동 거리로 클리핑
        newX = oldX + dx * k;
        newY = oldY + dy * k;
    }

    // 월드 경계 클리핑
    _player.X = Math.Clamp(newX, 0, _world.Width);
    _player.Y = Math.Clamp(newY, 0, _world.Height);

    Aoi.PlayerMoved(_world.Spatial, _player, oldX, oldY);   // 섹터 이동 + 시야 갱신
}
```

AOI 재동기화도 주기 타이머입니다:

```csharp
private void ProcessEnterWorld()
{
    if (_despawned) return;
    Aoi.EnterWorld(_world.Spatial, _player);

    // 자기 재예약 대신 주기 타이머 하나. Despawn 에서 핸들 하나만 취소하면 된다.
    if (_world.AoiResyncInterval > TimeSpan.Zero)
        _resyncTimer = DoAsyncEvery(_world.AoiResyncInterval, ResyncTick);
}
```

---

## 6.19 세션 — Sequencer 가 워커 풀에 직접 예약

```csharp
public sealed class ClientSession
{
    private readonly Sequencer<string> _packetSequencer;

    public ClientSession(long connId, TcpClient tcp, GameServer server, ...)
    {
        // ★ JobSystem 을 넘기는 생성자 — drain 이 system.Post 로 워커에 예약된다.
        //   예전에 필요했던 수동 InboundCommands 큐가 사라졌다.
        _packetSequencer = new Sequencer<string>(
            server.System,
            handler: HandleOnePacket,
            onError: ex => JobLog.Error($"[session #{connId}] packet handling failed", ex));
    }

    // 수신 IO 스레드 — push 만 하고 즉시 반환
    private void RecvLoop()
    {
        // ... 개행 단위로 파싱
        _packetSequencer.Enqueue(line);
    }

    // 워커 스레드 — 도착 순서대로, 세션당 한 번에 하나씩
    private void HandleOnePacket(string line)
    {
        if (ReferenceEquals(line, DisconnectMarker))
        {
            if (PlayerId != 0)
                _server.World.RemovePlayer(PlayerId);
            return;
        }
        PacketHandler.Handle(_server, this, line);
    }
}
```

```
IO Thread (NetRecv-N)          워커 풀
       │                          │
       │ Enqueue(line)            │
       ▼                          │
  Sequencer ──CAS 1회──► system.Post(Drain) ──► 워커가 Drain 실행
                                                  handler(line1)
                                                  handler(line2)   ← 도착 순서 보장
                                                  ...
                                                     │
                                                     ▼
                                            world.HandleClientMove(...)
                                                     │
                                                     ▼
                                             PlayerActor 큐
```

### 유령 플레이어를 막는 disconnect 마커

```csharp
private void HandleDisconnect()
{
    if (Interlocked.Exchange(ref _closeNotified, 1) != 0) { Close(); return; }

    // 이미 도착한 패킷들 뒤에 마커를 넣는다 → RemovePlayer 가 마지막에 실행된다
    if (!_packetSequencer.Enqueue(DisconnectMarker) && PlayerId != 0)
    {
        // false = 이미 Stop 된 sequencer (서버 셧다운) → 직접 정리
        _server.World.RemovePlayer(PlayerId);
    }

    Close();   // 내부에서 _packetSequencer.Stop()
}
```

`Close()` 는 곧바로 `Stop()` 을 부릅니다. v2.0 에서는 이 조합이 **마커를 잃을 수 있었고**
(P0-4, 4.6), 그 결과 접속이 끊긴 플레이어가 월드에 남았습니다. 지금은 `Stop()` 이
"새 항목만 거부"이므로 마커는 반드시 처리됩니다. 그리고 `Enqueue` 의 반환값을 확인해
거부된 경우의 대체 경로까지 두었습니다.

---

## 6.20 config.json으로 서버 설정

```json
{
  "server": {
    "port": 25100,
    "workerThreads": 8,
    "aoiResyncIntervalMs": 3000
  },
  "world": {
    "name": "AdvancedField",
    "width": 1000.0,
    "height": 1000.0,
    "spatialCellSize": 64.0
  },
  "npc": {
    "totalCount": 50,
    "tickIntervalMs": 200,
    "respawnSeconds": 8.0,
    "types": [ { "kind": "Slime", "weight": 4, "maxHp": 60, ... } ]
  }
}
```

```csharp
var config = ServerConfig.Load(configPath);   // 기본 "config.json"
var server = new GameServer(config);
server.Start();
```

> 포트가 9100 에서 **25100** 으로 바뀌었습니다. 이 저장소의 개발용 포트 대역
> (TCP 25001–25199) 규칙에 맞춘 것으로, 테스트 클라이언트도 같은 포트를 씁니다.

---

## 6.21 NPC 초기 분산 전략

NPC 50마리가 동시에 첫 Tick을 실행하면 한꺼번에 워커 큐에 몰립니다.
`DoAsyncEvery` 의 `initialDelay` 인자로 분산합니다:

```csharp
var jitter = TimeSpan.FromMilliseconds(
    Random.Shared.Next(0, Math.Max(1, (int)_tickInterval.TotalMilliseconds)));

_tickTimer = DoAsyncEvery(_tickInterval, Tick, jitter);
//                        ↑ 주기       ↑ 첫 발화까지의 지연
```

200ms 틱 간격, NPC 50마리의 경우:

```
분산 없음:              분산 있음 (0~200ms 랜덤 initialDelay):

t=0ms: 50개 NPC Tick    t=3ms:   1개 NPC
        → 큐 폭주!      t=11ms:  1개 NPC
                        t=24ms:  2개 NPC
                        ...
                        t=198ms: 1개 NPC  → 고르게 분산

한 번 분산되면 그 위상이 계속 유지된다 —
주기 타이머는 "예정 시각 + period" 로 재무장하므로 드리프트가 없다 (3.5).
```

---

## 6.22 콘솔 명령 — 상태와 메트릭

```csharp
static void PrintStatus(GameServer s)
{
    var snap = s.World.GetSnapshot();     // AskSync — 월드 큐에서 계산된 일관 스냅샷
    Console.WriteLine($"[상태] 세션 {snap.SessionCount} / 플레이어 {snap.LivePlayerCount}/{snap.TotalPlayerCount} " +
                      $"/ NPC {snap.LiveNpcCount}/{snap.TotalNpcCount} / WorldQueue {snap.WorldQueueDepth}");
}

static void PrintMetrics(GameServer s)
{
    var m = s.System.Metrics.Snapshot();  // ★ 인스턴스 메트릭
    Console.WriteLine(
        $"[metrics] executed={m.TotalJobsExecuted} dropped={m.TotalJobsDropped} failed={m.TotalJobsFailed} " +
        $"inFlight={m.InFlightJobs} pendingTimers={m.PendingTimerJobs} ready={m.ReadyQueueDepth} " +
        $"jobPool={m.ActiveJobPoolSize} workers={m.LiveWorkers} restarts={m.WorkerRestarts}");
}
```

이 두 명령은 콘솔 입력 스레드(비-워커)에서 실행됩니다. `GetSnapshot` 이 `AskSync` 이고
월드가 `ExecutionMode.Scheduled` 이므로, 콘솔 스레드가 게임 로직을 실행하는 일은 없습니다.

---

## 6.23 고급 패턴 요약

```mermaid
graph LR
    subgraph 최적화
        A1[DoAsync&lt;TState&gt; 클로저 없음] --> O1[GC 압박 감소]
        A2[JobOptions.MaxQueueSize] --> O2[OOM 방지]
        A3[DoAsyncEvery initialDelay 분산] --> O3[큐 폭주 방지]
    end
    subgraph 안전성
        B1[AttackerSnapshot 불변 데이터] --> S1[Race Condition 없음]
        B2[Sequencer - system 생성자] --> S2[패킷 순서 + 유령 플레이어 방지]
        B3[ExecutionMode.Scheduled] --> S3[호출자 hijack 방지]
        B4[AskSync + 데드락 가드] --> S4[조용한 행 대신 예외]
    end
    subgraph 운영
        C1[JobDispatcherOptions 수퍼바이저] --> M1[워커 자동 재기동]
        C2[system.StopAsync / DrainAsync] --> M2[검증된 우아한 종료]
        C3[system.Metrics.Snapshot] --> M3[운영 모니터링]
        C4[MaxJobDuration 워치독] --> M4[느린 작업 추적]
    end
```

---

## 6.24 핵심 학습 포인트

```
AdvancedMmorpgServer에서:
✓ JobSystem 하나가 워커·타이머·메트릭·셧다운 게이트를 소유한다
✓ 비제네릭 JobDispatcher — IRunnable 도, InboundCommands 도, Sleep(1) 도 없다
✓ ExecutionMode.Scheduled 로 비-워커 호출자의 hijack 차단
✓ DoAsync<TState>: hot path에서 클로저 할당 0
✓ JobOptions: World 10,000 / Player 256 / NPC 128 로 큐 제한 + OnDropped(reason)
✓ NpcActor AI: DoAsyncEvery 로 도는 상태 머신, Despawn 에서 Cancel()
✓ Sequencer(system, ...): 세션별 패킷 순서 + disconnect 마커 보장
✓ AskSync: 데드락 가드가 붙은 일관 스냅샷
✓ 셧다운: 네트워크 정지 → World.Stop(취소+드레인) → system.StopAsync
✓ 포트는 개발 대역 25100
```

---

*[← 제5장: 콘솔과 채팅 서버 예제 분석](./chapter05.md) | [↑ 목차](./README.md) | [→ 제7장: 실전 패턴과 모범 사례](./chapter07.md)*
