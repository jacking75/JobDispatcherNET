# 범위 공격(AoE)과 AOI 조회의 동시성 설계 개선안

> 대상: `AdvancedMmorpgServer` (JobDispatcherNET v2 기반 Actor 모델 서버)
>
> 다루는 두 가지 질문:
> 1. **범위 공격** — 피격자 수만큼 job이 생기고, 처리 결과를 공격자에게 되돌려줘야 할 때
>    생기는 구현 복잡도와 job 폭발을 어떻게 줄일 것인가?
> 2. **AOI 조회** — AOI 관리 객체에서 범위 내 엔티티를 찾을 때 lock을 걸어야 하는가?
>    lock 없이 어떻게 안전성을 보장하는가?

---

## 0. 현재 구조 요약

```
[클라이언트 패킷]
      │
      ▼
GameWorld.HandleClientAttack ──(World 큐)──► PlayerActor.MeleeAttack ──(공격자 큐)──►
      GameWorld.SendDamage ──(World 큐)──► 피해자 Actor.ReceiveDamage ──(피해자 큐)──► HP 차감
```

- `PlayerActor` / `NpcActor` / `GameWorld` 모두 `AsyncExecutable` — 각자 큐에서 직렬 실행.
- 공격자 상태는 `AttackerSnapshot`(불변 record struct)으로 복사되어 전달.
- 피해자 쪽 `ProcessReceiveDamage`가 거리·생존 여부를 **다시 검증**한 뒤 데미지 적용.

단일 대상 공격 하나가 이미 **큐 4개를 경유(job 4개)** 하고, 그중 2개가 단일 `GameWorld`
큐를 통과한다. 이 사실이 아래 문제 1의 출발점이다.

---

## 1. 문제 1 — 범위 공격: job 폭발과 결과 회수

### 1.1 진짜 문제가 무엇인지부터 분리하자

**"피격자 수만큼 job이 생긴다"는 것 자체는 문제가 아니다.**

- 피해자 N명의 HP 변경은 각자의 소유 Actor 큐에서 직렬화되어야 하므로,
  일의 양 자체가 본질적으로 O(N)이다. job N개는 그 일을 나르는 봉투일 뿐이다.
- `DoAsync<TState>` + static 람다를 쓰면 closure 할당이 없고, `Job<TState>`는
  오브젝트 풀에서 재사용된다(4장 참조). enqueue 1건은 수십 ns 수준이다.
- 수치 감각: 50명 피격 AoE가 초당 100번 터져도 5,000 jobs/s — 워커 풀 기준으로 미미하다.

진짜 문제는 두 가지다.

| # | 문제 | 왜 문제인가 |
|---|------|-------------|
| 1 | **모든 데미지가 단일 World 큐를 경유** (`ProcessRouteDamage`) | AoE N명 → World 큐 job N개 + 피해자 큐 job N개 = **2N개**. 게다가 World 큐는 이동·입장·브로드캐스트까지 처리하는 서버 전체의 직렬화 지점이라, 전투가 몰리면 여기가 먼저 막힌다 |
| 2 | **요청–응답(request-response) 사고방식** — "처리를 다 하고 나서 공격자에게 넘긴다" | 공격 하나가 여러 단계의 비동기 상태 기계가 되고, 공격자가 그 사이 다른 행동을 하면 상태가 뒤엉킨다. 구현 복잡도의 근원 |

아래 개선안 1~3은 지금의 엔티티-Actor 구조를 유지한 채 이 둘을 제거하고,
개선안 4는 구조 자체를 바꾸는 대안이다.

### 1.2 개선 1 — "처리를 넘긴다"를 "이벤트를 흘려보낸다"로 바꾼다

복잡함의 뿌리는 *공격자가 결과를 기다린다*는 모델이다. Actor 모델에서 올바른 모델은:

> **피해자 job은 필요한 모든 정보를 스냅샷으로 들고 간다 (단방향).**
> **공격자가 나중에 알아야 할 결과는 별도의 단방향 이벤트 job으로 되돌아온다.**
> **공격자는 아무것도 기다리지 않는다.**

이미 `AttackerSnapshot`이 앞 절반을 구현하고 있다. 뒷절반(결과 회신)을 추가하면:

```csharp
/// <summary>피해자 → 공격자로 흘러가는 불변 결과 이벤트.</summary>
public readonly record struct DamageResult(
    int AttackSeq,     // 몇 번째 공격의 결과인지
    int TargetId,
    int Dealt,
    bool Killed);
```

공격자 쪽 처리:

```csharp
// PlayerActor — 피해자 actor가 호출. 공격자 큐에서 직렬 실행되므로 lock 불필요.
public void NotifyDamageResult(DamageResult r)
    => DoAsync<(PlayerActor A, DamageResult R)>(
        static t => t.A.ProcessDamageResult(t.R), (this, r));

private void ProcessDamageResult(DamageResult r)
{
    // 이미 다음 공격/이동을 시작했어도 상관없다 —
    // 이 job은 공격자 큐에서 다른 행동들과 자연스럽게 순서대로 섞여 실행된다.
    if (r.Killed) GrantKillReward(r.TargetId);
    ApplyLifesteal(r.Dealt);
}
```

**"공격자가 다른 동작을 하면?"에 대한 답**: 아무 일도 안 해도 된다.
공격자의 다음 행동도, 늦게 도착한 결과 이벤트도 전부 공격자 큐에 들어와
도착 순서대로 직렬 실행된다. 경합도, 핸드오프도, lock도 없다.
결과가 *어느 공격*의 것인지만 구분하면 되는데, 그게 `AttackSeq`다:

```csharp
// PlayerActor 내부 — actor 큐 안에서만 접근하므로 평범한 int면 충분
private int _attackSeq;

private void ProcessAreaAttack(float radius)
{
    if (_despawned || !_player.IsAlive) return;

    int seq = ++_attackSeq;
    var snap = new AttackerSnapshot(_player.Id, _player.Name, _player.Kind,
        _player.X, _player.Y, _player.Attack /*, seq 필드 추가 */);
    // ... (1.3에서 계속)
}
```

피해자는 받은 `AttackSeq`를 `DamageResult`에 그대로 실어 돌려준다.
공격 2번을 연달아 시전한 뒤 1번의 결과가 늦게 도착해도 정확히 귀속된다.

> ⚠ **절대 규칙**: actor job 안에서 `ManualResetEventSlim.Wait()` 같은 차단 대기로
> 결과를 기다리지 말 것. 워커 스레드는 공유 자원이라 서로 기다리는 순간
> 데드락 또는 워커 고갈이 온다. `GetSnapshot()`처럼 외부(비워커) 스레드에서만 허용된다.

### 1.3 개선 2 — World 큐 우회: 피해자 Actor로 직접 라우팅

현재 `GameWorld.SendDamage`가 World 큐를 경유하는 이유는 `_players`/`_npcs`
Dictionary가 World 큐 전용이기 때문이다. 그런데 이 저장소의 lock-free 쌍둥이가
이미 존재한다 — `_entityLookup`(ConcurrentDictionary, 쓰기는 World 큐에서만,
읽기는 어디서든). **같은 패턴을 Actor 조회에도 적용하면 World 큐 경유가 사라진다.**

```csharp
// 공통 인터페이스 — PlayerActor / NpcActor가 구현
public interface IDamageTarget
{
    void ReceiveDamage(AttackerSnapshot atk, float range);   // 이미 양쪽에 존재
    void NotifyDamageResult(DamageResult r);                 // 개선 1에서 추가
}

// GameWorld — _entityLookup과 동일한 규칙: 쓰기는 World 큐에서만, 읽기는 lock-free
private readonly ConcurrentDictionary<int, IDamageTarget> _damageTargets = new();

public IDamageTarget? GetDamageTarget(int id) =>
    _damageTargets.TryGetValue(id, out var t) ? t : null;
```

이제 범위 공격 본문:

```csharp
private void ProcessAreaAttack(float radius)
{
    if (_despawned || !_player.IsAlive) return;

    int seq = ++_attackSeq;
    var snap = new AttackerSnapshot(..., seq);

    // 1) lock-free 공간 조회 — 결과는 '힌트'다 (2장 참조)
    var victims = _world.Spatial.QueryRadius(
        _player.X, _player.Y, radius, excludeId: _player.Id);

    // 2) 피해자 actor 큐로 직접 enqueue — World 큐를 건드리지 않는다
    foreach (var v in victims)
        _world.GetDamageTarget(v.Id)?.ReceiveDamage(snap, radius);
}
```

효과:

- job 수: **2N → N** (피해자당 정확히 1개 — 이론적 최소치).
- World 큐는 전투 트래픽에서 완전히 해방된다. 등장/퇴장/브로드캐스트만 남는다.
- 스냅샷 struct 1개를 N개 job이 공유하므로 공격자 상태 복사도 1회뿐이다.

**이게 안전한 이유** — 조회와 enqueue 사이에 피해자가 despawn/사망/이탈할 수 있지만,
데미지의 *권위 판정*은 피해자 큐 안 `ProcessReceiveDamage`에서 일어난다.
그 안에서 `_despawned`, `IsAlive`, 거리 재검증을 이미 하고 있으므로
낡은(stale) 대상으로 보낸 job은 그냥 no-op이 된다.
**"조회는 힌트, 검증은 소유자 큐 안에서"** — 이 불변식이 lock-free 라우팅 전체를 지탱한다.

### 1.4 개선 3 — 결과 *집계*가 정말 필요할 때

대부분의 게임 메커니즘은 전체 집계 없이 **증분 적용**으로 충분하다:

- 처치 보상/경험치 → 킬 이벤트가 올 때마다 지급
- 흡혈 → 데미지 이벤트가 올 때마다 회복
- 콤보 스택 → "적중" 이벤트 수신 시 스택 +1, 사용 시점에 유효성 검증

정말 모아야 할 때(전투 로그 "5명 적중, 총 1,250 데미지" 등)만 집계 상태를 둔다.
공격자 큐 안에서만 접근하므로 평범한 Dictionary면 된다:

```csharp
// PlayerActor — 전부 actor 큐 전용. lock 없음.
private sealed class AoeAggregate
{
    public int Expected;   // 시전 시점 조회된 대상 수
    public int Received;
    public int TotalDealt;
    public int Kills;
}
private readonly Dictionary<int, AoeAggregate> _pendingAoe = [];

private void ProcessAreaAttack(float radius)
{
    // ... (조회 + fan-out은 위와 동일)
    _pendingAoe[seq] = new AoeAggregate { Expected = victims.Count };

    // 안전망: 피해자가 despawn 되면 회신이 영영 안 올 수 있다 → 타임아웃 마감
    DoAsyncAfter(TimeSpan.FromMilliseconds(500),
        static t => t.A.FinalizeAoe(t.Seq), (A: this, Seq: seq));
}

private void ProcessDamageResult(DamageResult r)
{
    if (!_pendingAoe.TryGetValue(r.AttackSeq, out var agg)) return; // 이미 마감됨
    agg.Received++;
    agg.TotalDealt += r.Dealt;
    if (r.Killed) agg.Kills++;
    if (agg.Received >= agg.Expected) FinalizeAoe(r.AttackSeq);     // 조기 마감
}

private void FinalizeAoe(int seq)
{
    if (!_pendingAoe.Remove(seq, out var agg)) return;              // 중복 마감 방지
    // 전투 로그 전송, 통계 등
}
```

핵심 성질:

- 집계 중에도 공격자는 자유롭게 다음 행동을 한다 — 집계는 그저 큐에 섞여 도는 이벤트일 뿐.
- "회신 수 도달 or 타임아웃" 이중 마감 + `Remove`로 멱등성 확보.
- 공격 여러 개가 동시에 집계 중이어도 `seq` 키로 분리되므로 간섭이 없다.

### 1.5 개선 4 — 구조적 대안: 섹터(Zone) Actor 소유권 모델

메시지 수를 *구조적으로* 줄이는 방법은 소유권 단위를 엔티티에서 **공간**으로 바꾸는 것이다.
이 리포지토리의 `ExampleSectorServer`가 정확히 이 모델이다
(`ZoneSector.AreaAttackSameSector`, `InitiateAreaAttack` 참조).

- 섹터 Actor가 그 구역의 모든 엔티티를 소유한다.
- **같은 섹터 내 AoE = job 1개.** 그 job 안에서 피해자 전원을 동기적으로 순회하며
  데미지를 적용하고, 결과(적중 수, 킬 목록)도 그 자리에서 바로 얻는다 — 회신 자체가 불필요.
- 섹터 경계에 걸친 AoE만 인접 섹터로 스냅샷을 fan-out한다.
  AoE 반경 ≤ 섹터 한 변이면 영향 섹터는 최대 4개 — **job 수가 N(피격자 수)이 아니라
  S(섹터 수, 1~4)에 비례**하게 된다.

두 모델 비교:

| | 엔티티 Actor (현재) | 섹터 Actor (`ExampleSectorServer`) |
|---|---|---|
| AoE job 수 | 피격자당 1개 (개선 후 N) | 영향 섹터당 1개 (1~4) |
| 결과 회수 | 비동기 이벤트 회신 필요 | 같은 섹터 분은 즉시·동기 |
| 병렬성 단위 | 엔티티 (매우 잘게) | 섹터 (뭉텅이) |
| 핫스팟 위험 | 특정 엔티티 큐 폭주(보스 등) — `MaxQueueSize`로 방어 | **인구 밀집 섹터가 병목** — 섹터 분할 크기로 조절 |
| 경계 처리 | 없음 | 섹터 이동 프로토콜 필요 (`IsTransferring`, 2단계 이양) |
| 게임 로직 작성 | 항상 비동기 메시지 | 같은 섹터 안은 평범한 동기 코드 |

선택 기준:

- **전투가 밀집형**(레이드, 대규모 PvP — AoE가 곧 일상)이면 섹터 소유권이 유리하다.
  전투 로직이 동기 코드가 되어 복잡도가 크게 준다.
- **월드가 성기고 엔티티별 로직이 무겁다**(개별 NPC AI가 주 부하)면 현재의
  엔티티 Actor + 개선 1~3이 낫다. 병렬성을 잘게 유지할 수 있기 때문이다.
- 혼합도 가능하다: 상태 소유권은 섹터 Actor로, 세션 I/O는 지금처럼 엔티티별로.

### 1.6 요약 — 무엇부터 할 것인가

1. `IDamageTarget` + `ConcurrentDictionary` 레지스트리로 **World 큐 우회** (개선 2).
   변경량 대비 효과가 가장 크고, 기존 `_entityLookup` 패턴의 반복이라 위험이 낮다.
2. 결과가 필요한 스킬에만 `AttackSeq` + `DamageResult` **이벤트 회신** 도입 (개선 1),
   집계는 정말 필요한 곳에만 (개선 3).
3. job 수가 병목이라는 *증거*(`JobMetrics`, `RemainingTaskCount`)가 나온 뒤에야
   섹터 모델 전환(개선 4)을 검토한다. 구조 전환은 비용이 크다 — 측정이 먼저다.

---

## 2. 문제 2 — AOI 조회에 lock이 필요한가?

### 2.1 결론부터

**전역 lock은 필요 없다.** 필요한 것은 두 가지뿐이다:

1. **메모리 안전성** — 조회 중 크래시/무한루프/찢어진 컬렉션이 없을 것.
   → `ConcurrentDictionary`(현재 구현) 또는 불변 스냅샷이 보장한다.
2. **권위자 재검증** — 조회 결과로 상태를 *직접* 바꾸지 말고, 소유자 Actor 큐에
   전달해 그 안에서 다시 검증할 것. → `ProcessReceiveDamage`가 이미 하고 있다.

이 관점 전환이 핵심이다:

> **AOI 조회 결과는 힌트(hint)이지 진실(truth)이 아니다.**
>
> 조회가 한 틱 낡은 위치를 보거나, 이동 중인 엔티티를 놓치는 것은 게임적으로 무해하다
> (다음 틱에 잡힌다). 진짜 판정 — "정말 사거리 안인가, 정말 살아있는가" — 은
> 피해자 큐 안에서 그 시점의 권위 상태로 다시 이뤄진다.
> 이 불변식을 지키는 한, AOI 인덱스에 선형화 가능성(linearizability)을 요구할 이유가 없고,
> 따라서 lock도 필요 없다.

lock을 걸었을 때의 비용을 생각해보면: NPC 수천 마리가 매 틱 `FindNearestPlayer`를
호출하고 모든 이동이 `UpdatePosition`을 호출하는 구조에서, 전역 lock(또는
`ReaderWriterLockSlim`)은 가장 뜨거운 경로를 단일 직렬화 지점으로 만든다.
정확성에 도움이 안 되는데(어차피 재검증한다) 성능만 깎는 것이다.

### 2.2 현재 `SpatialIndex`가 보장하는 것과 못 하는 것

현재 구현: `ConcurrentDictionary<(int,int), ConcurrentDictionary<int, Entity>>` 그리드.

보장되는 것:
- 읽기(`QueryRadius`의 열거 포함)는 lock-free이고 예외 없이 안전하다.
- 쓰기는 CD 내부의 버킷 단위 fine-grained lock — 전역 경합 없음.
- 엔티티 X/Y의 변경은 소유자 Actor 큐에서만 일어난다(단일 작성자).

보장되지 않는 것(약한 일관성) — 그리고 각각에 대한 처방:

**허점 A — 셀 전환 중 조회 누락.** `UpdatePosition`이 `TryRemove(구 셀) → Add(신 셀)`
순서라, 그 사이에 스캔한 조회자는 엔티티를 **어느 셀에서도 못 본다**.
AoE라면 맞아야 할 대상이 빠지는 것 — 중복보다 해롭다. 순서를 뒤집으면
"둘 다에서 보임(중복)"으로 바뀌고, 중복은 걸러내기 쉽다:

```csharp
public void UpdatePosition(Entity e, float oldX, float oldY)
{
    var oldCell = Cell(oldX, oldY);
    var newCell = Cell(e.X, e.Y);
    if (oldCell == newCell) return;

    // add → remove 순서: 조회자가 '어디에도 없음'을 보는 순간이 사라진다.
    // 잠깐 두 셀 모두에서 보일 수 있으므로 QueryRadius가 Id로 dedupe한다.
    // (같은 엔티티의 이동은 소유자 큐에서 직렬화되므로 add/remove가 엇갈리는 ABA는 없다)
    var newBucket = _grid.GetOrAdd(newCell, _ => []);
    newBucket[e.Id] = e;
    if (_grid.TryGetValue(oldCell, out var oldBucket))
        oldBucket.TryRemove(e.Id, out _);
}
```

```csharp
// QueryRadius 내부 — 여러 셀을 스캔할 때만 dedupe 비용을 지불
HashSet<int>? seen = (maxX > minX || maxY > minY) ? [] : null;
foreach (var e in bucket.Values)
{
    if (seen is not null && !seen.Add(e.Id)) continue;
    // ... 기존 필터
}
```

**허점 B — 위치 쌍의 비원자성.** X와 Y가 별도 float 필드라 조회자가
`(새 X, 옛 Y)` 조합을 볼 수 있다. .NET에서 정렬된 32비트 읽기/쓰기는 원자적이므로
각 축의 값 자체가 찢어지진 않고, 오차는 최대 한 스텝 이동량이다 — 힌트 용도로는
거의 항상 무해하다. 정말 원자적 위치 쌍이 필요해지면(판정 로그의 재현성 등)
두 float을 long 하나로 패킹한다:

```csharp
// Entity에 추가 (선택) — 위치 쌍을 단일 64비트 원자 값으로
private long _packedPos;

public void SetPosition(float x, float y) =>          // 소유자 큐에서만 호출
    Volatile.Write(ref _packedPos,
        ((long)BitConverter.SingleToInt32Bits(x) << 32) |
        (uint)BitConverter.SingleToInt32Bits(y));

public (float X, float Y) ReadPosition()              // 어디서든 안전
{
    long v = Volatile.Read(ref _packedPos);
    return (BitConverter.Int32BitsToSingle((int)(v >> 32)),
            BitConverter.Int32BitsToSingle((int)v));
}
```

**허점 C — 조회 알로케이션.** `QueryRadius`가 호출마다 `List<Entity>`를 새로 만든다.
NPC 수천 마리 × 매 틱 `FindNearestPlayer`면 GC 압박이 된다. 호출자 버퍼 재사용이나
visitor 패턴(static 델리게이트 + state로 closure 회피)으로 바꿀 수 있다:

```csharp
public void QueryRadius<TState>(float cx, float cy, float radius,
    TState state, Action<TState, Entity> visit, ...)
{
    // 셀 순회는 동일 — 매치마다 visit(state, e) 호출. 리스트 할당 0.
}
```

### 2.3 대안 비교 — lock 없이 AOI를 유지하는 4가지 방법

| 방식 | 일관성 | 조회 비용 | 쓰기 비용 | 적합한 상황 |
|---|---|---|---|---|
| **① CD 그리드** (현재) | 약함 (셀 단위, 위 처방으로 누락 제거) | lock-free, 즉시 | 버킷 lock, 저렴 | 조회·갱신이 모두 빈번한 일반적 경우. **기본 권장** |
| **② 불변 스냅샷 더블 버퍼** | 스냅샷 내부는 완전 일관 (틱만큼 낡음) | **경합 0** — 그냥 읽기 | 주기마다 O(전체) 재구축 + 메모리 2배 | 조회 ≫ 갱신, 틱 수준 staleness 허용 (AOI 브로드캐스트 필터링 등) |
| **③ 섹터 Actor 소유** | 섹터 안은 엄격히 일관 | 비동기 (job으로 조회) | 없음 (소유자 직접 접근) | 1.5의 섹터 모델을 채택했을 때 자연스러운 귀결 |
| **④ ReaderWriterLockSlim** | 강함 | reader 경합 시작 시 급락 | writer가 전체 차단 | 소규모거나 프로토타입. 규모가 커지면 병목 1순위 |

②의 뼈대 — 참조 교체 한 번이 발행(publish)의 전부다:

```csharp
public sealed class SpatialSnapshot
{
    // 발행 이후 절대 변하지 않는다 → 어떤 스레드가 읽어도 동기화 불필요
    public required Dictionary<(int, int), Entity[]> Cells { get; init; }
    public List<Entity> QueryRadius(...) { /* 순수 읽기 */ }
}

private volatile SpatialSnapshot _snapshot;

// World 큐(또는 브로드캐스트 틱)에서 주기적으로:
_snapshot = BuildSnapshotFromLiveGrid();   // volatile write = 발행

// 조회자는 어디서든:
var snap = _snapshot;                      // 이 참조는 끝까지 일관된 세계
var hits = snap.QueryRadius(cx, cy, r);
```

주의: 스냅샷 속 `Entity` 참조를 통해 읽는 X/Y/Hp는 여전히 라이브 값이다.
"그 틱의 값"까지 얼려야 하면 `Entity[]` 대신 위치·HP를 복사한 경량 struct 배열을 담는다
(브로드캐스트용 `STATE` 패킷 생성과 통합하면 재구축 비용을 한 번으로 합칠 수 있다).

### 2.4 권장안

1. **현재의 CD 그리드를 유지**하고 허점 A(add→remove 순서 + dedupe)만 고친다.
   이것으로 "lock 없이 안전한 AOI 조회"는 완성이다.
2. **"조회는 힌트, 검증은 소유자 큐"** 불변식을 코드 리뷰 기준으로 명문화한다.
   조회 결과의 Entity를 소유자 큐 밖에서 mutate하는 코드가 등장하는 순간 이 설계는 깨진다.
3. AOI 기반 관심 관리(범위 내 플레이어에게만 브로드캐스트)를 도입하는 시점에
   ② 스냅샷 방식을 검토한다 — 브로드캐스트 틱과 재구축 주기가 자연스럽게 일치한다.
4. 성능 판단은 추측이 아니라 `JobMetrics.Snapshot()`과 큐 깊이(`RemainingTaskCount`)로.

---

## 3. 고전적 Map / Sector(2차원 배열) 모델 — 시야 검색에 lock이 필요한가?

실제 상용 게임에서 흔한 구조를 기준으로 다시 묻는 질문:

> `Map` 객체가 `Sector[,]` 2차원 배열을 가지고, 캐릭터가 이동하면 이동 거리에 따라
> 이전 Sector에서 빼서 새 Sector에 넣는다. 시야 안 객체 검색은 주변 Sector를 뒤진다.
> **이때 검색하려면 lock을 걸어야 하지 않나?**

### 3.1 결론부터 — "무언가"는 필요하지만, 걱정하는 그 lock은 아니다

일반 컬렉션(List/Dictionary)을 여러 워커 스레드가 동시에 넣고 빼고 열거하면
당연히 깨진다. 그러니 동기화 장치는 **반드시 있어야 한다**. 그러나 선택지는
"Map 전역 lock" 하나가 아니라 세 가지이고, 성능을 망치는 것은 그중
전역 lock뿐이다.

먼저 문제를 반으로 줄이자:

- **`Sector[,]` 배열 자체는 lock이 필요 없다.** 맵 로드 시 1회 생성 후 절대
  바뀌지 않는(리사이즈 없는) 불변 구조이므로, 어떤 스레드가 `_sectors[gy, gx]`로
  Sector *참조*를 읽어도 항상 안전하다.
- 따라서 동기화가 필요한 것은 **각 Sector 내부의 엔티티 컨테이너뿐**이며,
  lock을 걸더라도 그 범위는 Map이 아니라 **Sector 하나**다.

### 3.2 선택지 A — Sector 단위 초단기 lock + copy-out (전통적 정답)

많은 상용 MMO 서버(특히 C++ 계열)가 실제로 쓰는 방식이다.
lock을 걸되, **임계 구역 안에서는 참조 복사만 하고 즉시 푼다.**

```csharp
public sealed class Sector
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Entity> _entities = [];

    public void Add(Entity e)    { lock (_gate) _entities[e.Id] = e; }
    public void Remove(int id)   { lock (_gate) _entities.Remove(id); }

    /// <summary>조회 = 참조를 버퍼에 복사만 하고 즉시 unlock. 할당 0 (버퍼 재사용).</summary>
    public void CopyTo(List<Entity> buffer)
    {
        lock (_gate)
            foreach (var e in _entities.Values)
                buffer.Add(e);
    }
}
```

```csharp
public sealed class Map
{
    private readonly Sector[,] _sectors;   // 로드 시 1회 생성 — 배열 자체는 불변
    private readonly float _sectorSize;    // ★ 최대 시야 반경 이상으로 잡는다

    /// <summary>시야 검색 — 자기 섹터 + 주변 8개(3×3)만 보면 된다.</summary>
    public void QueryView(float x, float y, List<Entity> buffer)
    {
        var (cx, cy) = CellIndex(x, y);
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gx = cx - 1; gx <= cx + 1; gx++)
            if (InRange(gx, gy))
                _sectors[gy, gx].CopyTo(buffer);   // 섹터당 lock 한 번, 즉시 해제

        // 거리 필터·판정은 전부 lock 밖에서 — 결과는 여전히 '힌트'일 뿐이다
    }
}
```

**이게 성능 문제가 안 되는 이유** — lock의 비용은 "lock이 있다"가 아니라
"얼마나 오래, 얼마나 많은 스레드가 같은 lock을 두고 다투는가"다.

- 보유 시간: 엔티티 참조 수십 개 복사 = 수백 ns. 경합 없는 `lock` 진입은 ~20ns.
- 경합 분산: lock이 섹터마다 따로 있으므로, 맵 전체에 초당 수만 번의 시야 검색이
  일어나도 **섹터 하나당** 도착률은 낮다. 서로 다른 지역의 전투는 아예 다른 lock을 쓴다.
- 깨지는 경우는 단 하나 — 모두가 한 섹터에 몰릴 때(보스방). 이건 lock 제거가 아니라
  설계(인스턴스 분리, 섹터 세분화, 아래 B/C안)로 푸는 문제다.

대신 **세 가지 금지 규칙**을 어기는 순간 이 방식은 무너진다:

| 금지 | 이유 |
|---|---|
| ① Map 전역 lock | 서버의 모든 이동·시야 검색이 한 줄로 직렬화 — 질문에서 우려한 바로 그 시나리오 |
| ② lock 안에서 게임 로직·패킷 전송·다른 Actor 호출 | 보유 시간이 ns → ms로 폭증, lock 안에서 또 다른 큐/lock을 건드리면 역전·데드락 |
| ③ 두 Sector의 lock 동시 보유 | 데드락의 고전적 조건. 이동 시 remove/add를 **별개의 임계 구역**으로 나누면 애초에 필요 없다 |

이동(구간 전환)은 이렇게 처리한다:

```csharp
// 소유자 Actor 큐 안에서 실행 — 같은 엔티티의 이동은 이미 직렬이다
public void OnMoved(Entity e, float oldX, float oldY)
{
    var oldC = CellIndex(oldX, oldY);
    var newC = CellIndex(e.X, e.Y);
    if (oldC == newC) return;                     // 대부분의 이동은 여기서 끝

    _sectors[newC.Y, newC.X].Add(e);              // 임계구역 1: 신 섹터에 add
    _sectors[oldC.Y, oldC.X].Remove(e.Id);        // 임계구역 2: 구 섹터에서 remove
    // add → remove 순서: 조회자가 '어디에도 없음'을 보는 순간이 없다 (2.2 허점 A와 동일).
    // 잠깐 양쪽에 보일 수 있으므로 QueryView 쪽에서 Id로 dedupe.
}
```

두 lock을 동시에 잡지 않아도 안전한 이유: 같은 엔티티의 섹터 전환은 소유자 큐에서
직렬화되므로 add/remove가 서로 엇갈리는(ABA) 일이 없고, 조회자 입장의 일시적
중복은 dedupe로 무해하다.

참고로 `ReaderWriterLockSlim`은 이 경우 대체로 과잉이다 — 보유 시간이 이렇게 짧으면
RW lock의 진입 오버헤드가 평범한 `lock`(Monitor)보다 오히려 크다.

### 3.3 선택지 B — Sector 컨테이너를 concurrent 컬렉션으로 (조회 경로 lock 0)

Sector 내부를 `ConcurrentDictionary<int, Entity>`로 바꾸면 A안의 lock마저 사라진다:

```csharp
public sealed class Sector
{
    private readonly ConcurrentDictionary<int, Entity> _entities = new();

    public void Add(Entity e)  => _entities[e.Id] = e;
    public void Remove(int id) => _entities.TryRemove(id, out _);

    // 열거는 lock-free·예외 없음·약한 일관성 — '힌트'로 쓰기에 정확히 충분
    public IEnumerable<Entity> Entities => _entities.Values;
}
```

눈치챘겠지만 **이것은 현재 `SpatialIndex`와 본질적으로 같은 구조**다.
차이는 셀 컨테이너가 `ConcurrentDictionary<(int,int), …>`에 매달려 있느냐,
고정 2차원 배열에 매달려 있느냐뿐이고, 고정 배열 쪽이 오히려 한 단계 낫다
(외부 CD 조회가 배열 인덱싱으로 바뀌고, 맵 크기가 고정이라면 셀의 동적 생성도 불필요).

트레이드오프는 2장에서 정리한 그대로다: 약한 일관성(이동 중 엔티티의 일시적
중복/누락 가능성 — add→remove 순서와 dedupe로 처방)을 받아들이는 대신,
조회 경로에서 경합이 원천적으로 없다.

### 3.4 선택지 C — Map(존)을 단일 스레드가 소유 (lock이 아예 없는 모델)

존 스레드(zone-thread) 모델: Map과 그 안의 모든 Sector·엔티티 상태를
하나의 Actor(또는 전용 스레드)가 소유한다. 이동·섹터 전환·시야 검색이 전부
존 큐 안에서 직렬 실행되므로 **동시성 자체가 없고, 따라서 lock도 없다.**
시야 검색은 평범한 `List` 순회이고, 완벽하게 일관적이다.

- 장점: 게임 로직이 전부 동기 코드 — 구현 복잡도가 극적으로 준다. 고전 MMO의 정석.
- 단점: 존 하나의 처리량 = 코어 하나. 스케일 단위가 존/채널/인스턴스가 된다.
- 1.5의 섹터 Actor 모델은 이것의 세분화 버전이다(존 전체 → 섹터 묶음 단위 소유).

### 3.5 세 방식 비교와 선택

| | A. Sector lock + copy-out | B. concurrent 컨테이너 | C. 존 단일 스레드 소유 |
|---|---|---|---|
| 시야 검색 시 lock | 섹터당 1회, ns 단위 | **없음** | 없음 (동시성 자체가 없음) |
| 일관성 | 섹터 단위 일관 | 약함 (힌트 용도로 충분) | 완전 일관 |
| 경합 조건 | 한 섹터 밀집 시 | 사실상 없음 | 존 전체가 한 코어 |
| 구현 난이도 | 낮음 (규칙 3개 준수) | 낮음 | 구조 재편 필요 |
| 어울리는 곳 | 전통적 멀티스레드 서버 | **지금의 Actor 서버 (최소 변경)** | 존/채널제 게임 |

이 서버(워커 풀 + 엔티티 Actor)에는 **B가 자연스럽다** — 이미 같은 패턴을 쓰고 있고,
"조회는 힌트, 검증은 소유자 큐" 불변식도 이미 지키고 있다. A를 골라도 규칙 3개만
지키면 실전에서 전혀 문제없으며, "lock = 성능 문제"라는 등식은 전역 lock에만 성립한다.

### 3.6 덧붙임 — 시야 진입/이탈(Spawn/Despawn) 통지까지 가면

실제 게임의 AOI는 검색으로 끝나지 않고, 섹터 전환 시 **시야 델타**를 계산해
스폰/디스폰 패킷을 보낸다. 구조는 같은 원리의 연장이다:

```
이전 3×3 섹터 집합 vs 새 3×3 섹터 집합
  ├─ 새로 포함된 섹터의 엔티티 → 서로에게 Spawn 통지
  └─ 벗어난 섹터의 엔티티     → 서로에게 Despawn 통지
```

이때도 섹터에서 하는 일은 "그 순간의 목록 copy-out"뿐이고, 통지 자체는
각 대상의 소유자 큐/세션 Sequencer로 enqueue한다 — lock 보유 구간과 게임 로직이
분리되는 한, 어떤 방식(A/B/C)을 골라도 이 확장은 그대로 성립한다.

---

## 4. AOI를 처음부터 구현한다면 — 안정성과 성능을 모두 만족하는 권장 설계

현재 서버에는 AOI가 없다. 모든 통지가 전역 브로드캐스트다:

- `BroadcastDirect` — 모든 이벤트(공격/사망/스폰)를 **모든 세션**에 전송
- `BroadcastSnapshotDirect` — 전체 엔티티 STATE 문자열을 **World 큐 한 곳에서** 만들어
  모든 세션에 전송 → 네트워크 O(플레이어 × 엔티티), 생성 비용은 단일 큐에 집중

AOI가 해야 할 일은 세 가지다:

1. **시야 목록 유지** — 각 플레이어가 "지금 보이는 엔티티 집합"을 안다
2. **진입/이탈 통지** — 시야에 들어오면 Spawn, 나가면 Despawn 패킷
3. **관심 필터링** — STATE 갱신과 전투 이벤트를 시야 안 관전자에게만 전송

### 4.1 설계 원칙 — 앞 장들의 결론을 그대로 조합한다

| 원칙 | 적용 |
|---|---|
| 가변 상태는 소유자가 하나 | 시야 집합(`HashSet<int>`)은 **각 PlayerActor 소유** — 자기 큐 안에서만 접근, lock 0 |
| 공간 조회는 힌트 | 그리드 인덱스는 lock-free(2장 B안), 결과의 일시적 오차 허용 |
| 오차는 자기 치유되게 | diff 기반 갱신 — 이번 틱의 누락/중복은 다음 틱에 수렴 |
| 패킷은 멱등하게 | 클라이언트가 중복 Spawn·모르는 id를 무해하게 처리 (4.4) |
| 전송은 어디서든 | `ClientSession.SendPacket`은 이미 스레드 안전(`BlockingCollection.TryAdd`) + slow-client drop 내장 — **어느 actor 큐에서든 직접 호출 가능** |

마지막 항목이 중요하다. 세션 전송이 스레드 안전하므로 "통지를 보내기 위해 World 큐를
경유한다"는 제약 자체가 없다. AOI의 모든 전송은 발신 지점에서 직접 이뤄질 수 있다.

### 4.2 아키텍처 선택 — Pull vs Push vs 중앙 집중

| | **Pull (플레이어 틱)** | Push (섹터 구독) | 중앙 집중 (스냅샷 액터) |
|---|---|---|---|
| 동작 | 각 PlayerActor가 주기적으로 주변을 조회해 diff | 섹터마다 관전자 목록, 이동 시 구독 갱신 + 즉시 통지 | 한 액터가 전체 스냅샷 → 플레이어별 diff |
| 공유 가변 상태 | **없음** (시야 집합이 actor 소유) | 구독 목록 — 또 하나의 동기화 대상 | 없음 (하지만…) |
| 진입/이탈 지연 | ≤ AOI 틱 (100~250ms) | 즉시 | ≤ 틱 |
| 이벤트 fan-out | 발생 지점 주변 조회 (섹터 3×3 스캔) | O(구독자) — 가장 저렴 | 액터 경유 |
| 부하 분포 | **워커 풀에 자연 분산** | 분산 | **한 큐에 O(전체) 집중** — 지금 문제의 재생산 |
| 구현 복잡도 | 낮음 | 높음 (경계 왕복, 구독 정리) | 중간 |

**권장: Pull 모델로 시작한다.** 이 코드베이스의 기존 패턴(NpcActor의 자가 스케줄링
Tick)과 정확히 같은 모양이고, 공유 가변 상태가 하나도 생기지 않아 안정성 논증이
가장 짧다. 이벤트 발생률이 지배적이라는 측정이 나오면 그때 섹터 구독(push)으로
진화한다 — 시야 집합·멱등 계약 등 나머지 설계는 그대로 재사용된다.

### 4.3 구현 스케치

**① 시야 상태 — 전부 PlayerActor 큐 전용, 전부 재사용 (핫패스 할당 0)**

```csharp
// PlayerActor — 자기 큐 안에서만 접근. lock 불필요.
private HashSet<int> _visible = [];          // 현재 시야
private HashSet<int> _nextVisible = [];      // 다음 시야 (매 틱 swap)
private readonly List<Entity> _queryBuf = new(64);
private readonly StringBuilder _stateBuf = new(2048);
```

**② AOI 틱 — NpcActor.Tick과 동일한 자가 스케줄링 패턴**

```csharp
public void StartAoi()   // ProcessAddPlayer 직후 호출
{
    // 첫 틱을 0~interval 사이 무작위 지연 — 전 플레이어의 틱이 한 순간에 몰리지 않게
    DoAsyncAfter(RandomJitter(_aoiInterval), AoiTick);
}

private void AoiTick()
{
    if (_despawned || _world.IsStopping) return;

    // 1) lock-free 힌트 조회 (자기 섹터 + 주변 8개)
    _queryBuf.Clear();
    _world.Spatial.QueryRadius(_player.X, _player.Y, ViewRange, _queryBuf);

    // 2) diff — 진입자에게 Spawn, 보이는 것만 STATE에 누적
    _nextVisible.Clear();
    _stateBuf.Clear();
    _stateBuf.Append("STATE");
    foreach (var e in _queryBuf)
    {
        if (e.Id == _player.Id) continue;
        if (!_nextVisible.Add(e.Id)) continue;         // 셀 전환 중 중복 dedupe (2.2)
        if (!_visible.Contains(e.Id))
            _session.SendPacket(Packets.Spawn(e));     // 시야 진입
        _stateBuf.Append('|').Append(e.Id).Append(',')
                 .Append(e.X.ToString("F1")).Append(',')
                 .Append(e.Y.ToString("F1")).Append(',').Append(e.Hp);
    }

    // 3) 이탈자에게 Despawn
    foreach (var id in _visible)
        if (!_nextVisible.Contains(id))
            _session.SendPacket(Packets.Despawn(id));

    // 4) swap (할당 0) + 시야 한정 STATE 전송
    (_visible, _nextVisible) = (_nextVisible, _visible);
    _session.SendPacket(_stateBuf.ToString());

    DoAsyncAfter(_aoiInterval, AoiTick);
}
```

X/Y/Hp를 소유자 큐 밖에서 약하게 읽는 것은 지금의 전역 `BroadcastSnapshotDirect`와
동일한 보장 수준이다 — 브로드캐스트 용도로 이미 허용한 트레이드오프.

**③ 전투 이벤트 — 전역 브로드캐스트를 "발생 지점 주변 전송"으로 교체**

먼저 `ProcessAddPlayer`에서 `p.SendPacket = session.SendPacket`을 연결해 둔다
(`Player.SendPacket` 델리게이트는 이미 선언만 되어 있다). 그러면:

```csharp
// GameWorld — World 큐 경유 없음. 조회는 lock-free, 전송은 세션이 스레드 안전.
public void NotifyCombatEvent(float x, float y, string packet)
{
    // 이벤트를 '볼 수 있는' 사람 = 발생 지점에서 시야 반경 안의 플레이어
    foreach (var e in Spatial.QueryRadius(x, y, ViewRange, EntityKind.Player))
        if (e is Player p) p.SendPacket?.Invoke(packet);
}
```

`NotifyAttack`/`NotifyDeath`/`NotifyRespawn`이 전부 이 형태로 바뀌고,
**World 큐는 브로드캐스트 경로에서 완전히 빠진다.**

**④ 초기 스냅샷·입장 통지의 소멸**

diff 기반 AOI의 부수 효과로 기존 코드 두 덩이가 그냥 사라진다:

- `SendInitialSnapshot`(전체 엔티티 전송) → 삭제. 입장 직후 첫 AoiTick이
  시야 안 엔티티에게만 Spawn을 보낸다.
- `BroadcastSpawnDirect`(입장을 전 세션에 통지) → 삭제. 주변 플레이어들의
  다음 AoiTick이 신규 진입자를 발견한다 (지연 ≤ 1틱; 즉시성이 필요하면
  입장 시점에 ③과 같은 주변 조회 통지 한 번).

### 4.4 안정성의 마지막 조각 — 클라이언트 멱등 계약

시야 정보는 여러 경로(AOI 틱, 이벤트)로 비동기 도착하므로 순서 역전이 원리적으로
가능하다. 예: 공격 이벤트가 그 공격자의 Spawn보다 먼저 도착. 서버에서 순서를
맞추려 하면(전역 직렬화) 지금까지의 설계가 무너진다. 대신 클라이언트 규칙 두 줄로 끝낸다:

1. **중복 Spawn = 갱신(upsert)** — 이미 아는 id의 Spawn은 상태 덮어쓰기
2. **모르는 id는 무시** — Despawn·이벤트·STATE 항목 모두. 다음 틱에 수렴한다

이 계약이 있으면 서버 쪽 시야 오차는 전부 "최대 1 AOI 틱짜리 일시적 현상"이 되고,
서버는 어떤 순서 보장도 추가로 짊어지지 않는다.

**시야 경계 flapping** — 경계에 걸친 채 미세하게 왕복하는 엔티티가 Spawn/Despawn을
반복 유발하는 문제는 히스테리시스로 막는다: 조회는 이탈 반경(예: 50)으로 하되,
*신규 진입*만 더 작은 진입 반경(예: 45)으로 판정한다. 한 번 보이기 시작한 엔티티는
이탈 반경을 벗어나야 사라진다.

```csharp
foreach (var e in _queryBuf)              // LeaveRange 로 조회된 결과
{
    bool known = _visible.Contains(e.Id);
    if (!known && DistanceSq(e) > EnterRangeSq) continue;  // 진입은 더 엄격하게
    // ... (이하 동일)
}
```

부수 결정 하나: 현재 `QueryRadius`는 `!IsAlive`를 걸러낸다. 사망 엔티티를
시체로 보여줄 것이면 `includeDead` 오버로드를 추가하고, "사망 = 시야에서 제거"로
갈 것이면 그대로 둔다 (Death 이벤트 + Despawn이 자연히 전달된다).

### 4.5 왜 이 조합이 안정성·성능을 동시에 만족하는가

**안정성** — 위험 요소별 방어가 전부 구조적이다 (런타임 운에 기대는 부분이 없다):

| 위험 | 방어 |
|---|---|
| 데이터 레이스 | 시야 집합=PlayerActor 소유, 엔티티 상태=소유자 큐, 인덱스=concurrent 컨테이너. **lock이 0개라 데드락·우선순위 역전이 원리적으로 불가능** |
| 조회 누락/중복 | add→remove 순서 + dedupe (2.2) + diff의 자기 치유 |
| 패킷 순서 역전 | 클라이언트 멱등 계약 (4.4) |
| 경계 왕복 폭주 | 히스테리시스 |
| 느린 클라이언트 | 세션 송신 큐 한도 + drop — **이미 구현되어 있음** |
| 틱 몰림 스파이크 | 첫 틱 지터 + 자가 스케줄링 — 워커 풀에 자연 분산 |

**성능** — 병목이 있던 자리마다 무엇이 바뀌는지:

| | 현재 (전역 브로드캐스트) | AOI 도입 후 |
|---|---|---|
| 네트워크 총량 | O(P × E) — 1,000명 × 6,000엔티티 = 틱당 600만 엔트리 | O(P × V) — V는 시야 내 평균(수십). **밀도가 일정하면 플레이어당 상수** |
| STATE 생성 | World 큐 한 곳에서 O(E) | 각 PlayerActor가 O(V) — **P개 큐로 분산, 단일 직렬화 지점 소멸** |
| 이벤트 전송 | 전 세션 순회 | 발생 지점 3×3 섹터 스캔 |
| 조회 경합 | — | lock-free (그리드) |
| 핫패스 할당 | STATE 문자열 매 틱 | 버퍼·HashSet·StringBuilder 재사용 + `DoAsyncAfter<TState>` — 0 |

튜닝 노브는 둘뿐이다: **AOI 틱 주기**(100~250ms — 진입/이탈 체감 지연과 조회
비용의 트레이드오프)와 **섹터 크기**(시야 반경 이상 — 3×3 스캔 보장).

### 4.6 단계적 도입 순서

1. `SpatialIndex` 보강 — add→remove 순서(2.2 허점 A), 호출자 버퍼 오버로드,
   (필요 시) `includeDead`. 기존 호출부는 그대로 동작.
2. `ProcessAddPlayer`에서 `Player.SendPacket` 연결 — 직접 전송 기반 마련.
3. `PlayerActor`에 시야 집합 + `AoiTick` 추가, `SendInitialSnapshot`·입장 전역
   통지 제거.
4. `NotifyAttack`/`NotifyDeath`/`NotifyRespawn`을 위치 기반 전송으로 교체.
5. 전역 `BroadcastSnapshotDirect` 제거 — AoiTick의 시야 STATE가 대체.
   (`BroadcastActor`는 통째로 필요 없어진다.)
6. 측정 — `JobMetrics.Snapshot()`, 큐 깊이, 플레이어당 송신 바이트.
   이벤트 fan-out 비용이 지배한다는 숫자가 나오면 그때 섹터 구독(push) 검토.

각 단계가 독립적으로 배포·검증 가능하고, 1~2단계는 기존 동작을 전혀 바꾸지 않는다.

---

## 5. 한 장 요약

```
범위 공격
  ├─ job N개 자체는 문제가 아니다 (일이 O(N)이고 job은 풀링됨)
  ├─ 진짜 병목 ① World 큐 경유(2N + 단일 직렬화 지점)
  │     → IDamageTarget 레지스트리(ConcurrentDictionary)로 피해자 큐 직접 enqueue
  ├─ 진짜 병목 ② 요청-응답 사고방식
  │     → 스냅샷 단방향 전달 + 결과는 이벤트로 회신 + AttackSeq로 귀속
  │     → 공격자는 아무것도 기다리지 않는다: 다음 행동과 결과 이벤트는
  │        공격자 큐에서 자연스럽게 직렬화된다
  ├─ 집계가 필요하면: 공격자 큐 전용 Dictionary + 회신수/타임아웃 이중 마감
  └─ 밀집 전투가 일상이면: 섹터 Actor 소유권 (job이 섹터 수에 비례, ExampleSectorServer 참조)

AOI 조회
  ├─ 전역 lock 불필요 — 필요한 건 메모리 안전성 + 소유자 재검증뿐
  ├─ "조회는 힌트, 진실은 소유자 큐 안" 불변식이 전부를 지탱한다
  ├─ 현재 CD 그리드 유지 + UpdatePosition을 add→remove로 (누락 → 중복으로 바꾸고 dedupe)
  └─ 조회 ≫ 갱신 구간이 생기면 불변 스냅샷 더블 버퍼 (경합 0, 틱만큼 낡음)

Map + Sector[,] (고전적 2차원 배열 그리드)
  ├─ Sector[,] 배열 자체는 불변 — lock 대상은 각 Sector 내부 컨테이너뿐
  ├─ 동기화는 반드시 필요하지만 선택지는 셋:
  │    A. 섹터 단위 초단기 lock + copy-out (전역 lock 금지 / lock 안 로직 금지 / 이중 lock 금지)
  │    B. 섹터 컨테이너를 ConcurrentDictionary로 → 조회 lock 0 (현 SpatialIndex와 동질)
  │    C. 존 단일 스레드 소유 → 동시성 자체가 없음 (고전 존 스레드 모델)
  ├─ 이동 = add(신 섹터) → remove(구 섹터), 별개 임계구역, 조회 측 Id dedupe
  └─ "lock = 성능 문제" 등식은 Map 전역 lock에만 성립한다

AOI 구현 (안정성 + 성능 동시 만족)
  ├─ Pull 모델: 각 PlayerActor가 자가 스케줄링 AoiTick에서 주변 3×3 조회 → diff
  │    → 시야 집합(HashSet)이 actor 소유라 공유 가변 상태 0개 = lock 0, 데드락 불가능
  ├─ 진입자 Spawn / 이탈자 Despawn / 보이는 것만 STATE — 전부 자기 큐에서, 버퍼 재사용
  ├─ 전투 이벤트는 발생 지점 주변 조회 후 직접 전송 (SendPacket은 이미 스레드 안전)
  │    → World 큐가 브로드캐스트 경로에서 완전히 빠진다
  ├─ 안정성 마감재: 클라이언트 멱등 계약(중복 Spawn=upsert, 모르는 id 무시)
  │    + 히스테리시스(진입 반경 < 이탈 반경) + 첫 틱 지터
  ├─ 네트워크 O(P×E) → O(P×V), STATE 생성이 P개 큐로 분산 — 단일 직렬화 지점 소멸
  └─ 이벤트 비율이 지배한다는 측정이 나오면 그때 섹터 구독(push)으로 진화
```
