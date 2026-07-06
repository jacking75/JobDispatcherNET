# Push 모델 AOI 구현 계획

> 대상: `AdvancedMmorpgServer` + `AdvancedMmorpgClient`
> 설계 배경: [aoe-aoi-design.md](./aoe-aoi-design.md) 3장(섹터 모델)·4장(AOI 설계)
> 이 문서는 **다음 작업 턴에서 그대로 구현하기 위한 실행 계획**이다. 모든 결정 사항은
> 확정값으로 명시했고, 코드 스케치는 실제 코드베이스의 타입·메서드명 기준이다.

---

## 0. 확정 결정 사항 (구현 중 재논의 불필요)

| # | 항목 | 결정 | 근거 |
|---|------|------|------|
| D1 | 시야 규칙 | 자기 셀 + 주변 8개 = **3×3 섹터** | 섹터 AOI 표준. 셀 크기 ≥ 시야 반경이면 보장됨 |
| D2 | 셀 크기 | `spatialCellSize` **50 → 64** (config.json) | 클라이언트 봇 `EngageRange = 60` (BotClient.cs:24). 3×3이 보장하는 최소 가시 반경 = 셀 크기이므로 64 ≥ 60 필요. 월드 1000×1000 → **16×16 = 256 섹터** |
| D3 | 위치/HP 동기화 | 기존 `STATE` 패킷을 **단일 엔티티 델타**로 재사용 (`STATE\|id,x,y,hp`) | 클라 파서(WorldState.cs:72)가 엔트리 수 무관·모르는 id 무시로 이미 동작 → **프로토콜 변경 0** |
| D4 | 신규 패킷 | 없음 | D3 덕분. WELCOME/SPAWN/DESPAWN/STATE/ATTACK/DEATH/RESPAWN 전부 재사용 |
| D5 | 전역 브로드캐스트 | `BroadcastActor` + 전역 STATE + 전역 이벤트 통지 **전부 삭제** | push가 완전 대체 |
| D6 | 시체(사망 엔티티) | 섹터에 **남긴다** (현재 동작 유지 — 죽어도 grid에서 제거 안 함). AOI 스캔은 사망 포함, NPC AI 쿼리만 생존 필터 | 클라가 hp=0 시체를 렌더링하는 현 동작 보존 |
| D7 | 재조정(안전망) | 플레이어별 저주파 SPAWN 재전송 틱, `aoiResyncIntervalMs = 3000` (0=off) + 클라 TTL 축출 10초 | push의 잔여 레이스(§6 R3)를 유한 시간 내 수렴 보장 |
| D8 | 클라이언트 구조 | **봇마다 자기 WorldState** + 렌더러는 카메라 봇(Tab으로 순환) 뷰 표시 | 공유 WorldState는 AOI와 양립 불가 (§5.1) |
| D9 | 데미지 라우팅 | **현행 유지** (World 큐 경유 `SendDamage`) | AoE 직접 라우팅(설계 문서 1.3)은 별도 작업 — 이번 범위에서 제외 |
| D10 | 셀 경계 히스테리시스 | **구현 보류** | 섹터 방식은 셀 경계를 넘을 때만 diff가 발생해 빈도가 낮음. 문제가 측정되면 경계 데드존 추가 (§8 후속) |

**구현하지 않는 것**: AoE 직접 라우팅(D9), 히스테리시스(D10), 패킷 배칭, 거리 등급별
갱신 주기, 관측자별 known-set 추적.

---

## 1. 아키텍처 개요

```
                    ┌─ Sector[gy,gx] (고정 2차원 배열, 생성 후 불변) ─┐
                    │  Entities:    ConcurrentDictionary<int,Entity>  │
                    │  Subscribers: ConcurrentDictionary<int,Player>  │
                    └──────────────────────────────────────────────────┘

이동(소유자 큐 안):                          이벤트(소유자 큐 안):
  등록 이동(add신→remove구)                    피격/사망/리스폰 발생 즉시
  + Sub(신)\Sub(구) → SPAWN                    → 그 셀 Subscribers에게
  + Sub(구)\Sub(신) → DESPAWN                    ATTACK/STATE/DEATH/RESPAWN push
  + Sub(신) → STATE(위치 에코)
  (플레이어면) 시야 diff → 자신에게 SPAWN/DESPAWN + 구독 갱신
```

핵심 불변식 5개 — 구현 전체가 이 위에 서 있다. 코드 리뷰 기준으로 사용할 것:

1. **소유자 큐 규칙**: 엔티티 E의 섹터 등록/구독 변경은 E의 소유자 actor 큐 안에서만.
   (같은 엔티티에 대한 grid 쓰기가 절대 경합하지 않는다 — CD는 서로 다른 엔티티 간
   동시 쓰기만 감당하면 된다)
2. **등록 먼저, 스캔은 그다음** (핸드셰이크): 자신을 `Entities`/`Subscribers`에 넣은
   *뒤에* 상대 목록을 스캔한다. 두 주체가 동시에 서로의 시야에 들어와도
   **최소 한쪽의 통지는 반드시 발생**하고, 양쪽 다 발생하면 클라 멱등성이 흡수한다.
3. **add(신 셀) → remove(구 셀)** 순서: 조회자가 "어디에도 없음"을 보는 순간 제거.
   일시적 중복은 멱등성이 흡수.
4. **통지는 세션 큐 직행**: `Player.SendPacket`(= `ClientSession.SendPacket`,
   BlockingCollection 기반 스레드 안전, NetworkServer.cs:278)을 발신자 job 안에서
   직접 호출한다. **수신자 actor 큐도, World 큐도 쓰지 않는다** — actor 큐 부하 0.
5. **클라 멱등 + 서버 재조정**: 클라는 이미 멱등(§5.0 표). 잔여 레이스는 재조정 틱이
   유한 시간 내 수렴시킨다.

---

## 2. 서버 신규 파일

### 2.1 `AdvancedMmorpgServer/Sector.cs` (신규)

```csharp
using System.Collections.Concurrent;

namespace AdvancedMmorpgServer;

/// <summary>
/// 그리드의 한 칸. 두 컬렉션 모두 쓰기는 각 엔티티의 소유자 큐에서만 일어나고
/// (엔티티별 단일 작성자), 읽기는 어느 스레드에서든 lock-free.
/// </summary>
public sealed class Sector
{
    public readonly int GX;
    public readonly int GY;

    /// <summary>이 셀에 위치한 엔티티들 (시체 포함).</summary>
    public readonly ConcurrentDictionary<int, Entity> Entities = new();

    /// <summary>이 셀을 시야(3×3)에 포함하는 플레이어들 = 통지 대상.</summary>
    public readonly ConcurrentDictionary<int, Player> Subscribers = new();

    public Sector(int gx, int gy) { GX = gx; GY = gy; }

    public bool HasSubscribers => !Subscribers.IsEmpty;
}
```

### 2.2 `AdvancedMmorpgServer/SectorGrid.cs` (신규 — `SpatialIndex` 대체)

기존 `SpatialIndex`의 public API(`Add`/`Remove`/`UpdatePosition`/`QueryRadius`/
`FindNearestPlayer`)를 그대로 제공해 NPC AI 호출부 수정을 최소화하고,
AOI용 API를 추가한다. **`SpatialIndex.cs`는 삭제한다.**

```csharp
namespace AdvancedMmorpgServer;

/// <summary>
/// 고정 2차원 배열 섹터 그리드. 배열 자체는 생성 후 불변 — 인덱싱은 항상 lock-free 안전.
/// 셀 크기는 클라이언트 최대 시야/교전 반경(60) 이상이어야 3×3 시야가 보장된다.
/// </summary>
public sealed class SectorGrid
{
    private readonly Sector[,] _sectors;   // [gy, gx]
    private readonly float _cellSize;

    public int CellsX { get; }
    public int CellsY { get; }

    public SectorGrid(float worldW, float worldH, float cellSize)
    {
        _cellSize = cellSize;
        CellsX = Math.Max(1, (int)MathF.Ceiling(worldW / cellSize));
        CellsY = Math.Max(1, (int)MathF.Ceiling(worldH / cellSize));
        _sectors = new Sector[CellsY, CellsX];
        for (int y = 0; y < CellsY; y++)
            for (int x = 0; x < CellsX; x++)
                _sectors[y, x] = new Sector(x, y);
    }

    public (int X, int Y) CellOf(float x, float y) => (
        Math.Clamp((int)(x / _cellSize), 0, CellsX - 1),
        Math.Clamp((int)(y / _cellSize), 0, CellsY - 1));

    public Sector this[int gx, int gy] => _sectors[gy, gx];

    public Sector SectorAt(float x, float y)
    {
        var c = CellOf(x, y);
        return _sectors[c.Y, c.X];
    }

    /// <summary>중심 셀의 3×3 시야 블록 (월드 경계 클램프).</summary>
    public ViewBounds ViewOf(int cx, int cy) => new(
        Math.Max(0, cx - 1), Math.Max(0, cy - 1),
        Math.Min(CellsX - 1, cx + 1), Math.Min(CellsY - 1, cy + 1));

    // ─── 기존 SpatialIndex 호환 API (NPC AI / 등록용) ───

    public void Add(Entity e) => SectorAt(e.X, e.Y).Entities[e.Id] = e;

    public void Remove(Entity e) => SectorAt(e.X, e.Y).Entities.TryRemove(e.Id, out _);

    /// <summary>
    /// 반경 내 엔티티 조회 (NPC AI용). aliveOnly=true 가 기존 동작.
    /// 기존 SpatialIndex.QueryRadius와 동일한 셀 범위 스캔 — 셀 전환 중 중복 대비 dedupe.
    /// </summary>
    public List<Entity> QueryRadius(float cx, float cy, float radius,
        EntityKind? onlyKind = null, int? excludeId = null, bool aliveOnly = true)
    {
        var result = new List<Entity>();
        int minX = Math.Max(0, (int)MathF.Floor((cx - radius) / _cellSize));
        int maxX = Math.Min(CellsX - 1, (int)MathF.Floor((cx + radius) / _cellSize));
        int minY = Math.Max(0, (int)MathF.Floor((cy - radius) / _cellSize));
        int maxY = Math.Min(CellsY - 1, (int)MathF.Floor((cy + radius) / _cellSize));
        float r2 = radius * radius;
        HashSet<int>? seen = (maxX > minX || maxY > minY) ? [] : null;

        for (int gy = minY; gy <= maxY; gy++)
        for (int gx = minX; gx <= maxX; gx++)
        {
            foreach (var e in _sectors[gy, gx].Entities.Values)
            {
                if (excludeId is int ex && e.Id == ex) continue;
                if (aliveOnly && !e.IsAlive) continue;
                if (onlyKind is EntityKind k && e.Kind != k) continue;
                if (seen is not null && !seen.Add(e.Id)) continue;
                float dx = e.X - cx, dy = e.Y - cy;
                if (dx * dx + dy * dy <= r2) result.Add(e);
            }
        }
        return result;
    }

    /// <summary>가장 가까운 생존 플레이어 (NPC AI). 기존 시그니처 유지.</summary>
    public Player? FindNearestPlayer(float cx, float cy, float maxRange)
    {
        // 기존 SpatialIndex.FindNearestPlayer 본문 그대로 (QueryRadius 위에 구현)
    }
}

public readonly record struct ViewBounds(int MinX, int MinY, int MaxX, int MaxY)
{
    public bool Contains(int gx, int gy) =>
        gx >= MinX && gx <= MaxX && gy >= MinY && gy <= MaxY;
}
```

주의: 기존 `SpatialIndex.UpdatePosition`은 `Aoi.EntityMoved`(§2.3)가 흡수하므로
SectorGrid에는 만들지 않는다. `Remove(Entity)`는 기존과 동일하게 현재 좌표의 셀에서
제거한다 (호출은 항상 소유자 큐 → 좌표가 그 시점의 진실).

### 2.3 `AdvancedMmorpgServer/Aoi.cs` (신규 — 통지 오케스트레이션)

상태 없는 static 클래스. **모든 메서드는 "해당 엔티티의 소유자 actor 큐 안"에서만
호출**한다는 계약을 XML 주석에 명시할 것.

```csharp
namespace AdvancedMmorpgServer;

/// <summary>
/// Push AOI 오케스트레이션. 모든 메서드는 대상 엔티티의 소유자 actor 큐 안에서만 호출.
/// 통지는 Player.SendPacket(세션 송신 큐)으로 직행 — actor 큐/World 큐를 쓰지 않는다.
/// </summary>
public static class Aoi
{
    /// <summary>지정 좌표 셀의 관전자 전원에게 push. 관전자 없으면 no-op.</summary>
    public static void PublishAt(SectorGrid g, float x, float y, string packet, int excludeId = -1)
        => Publish(g.SectorAt(x, y), packet, excludeId);

    public static void Publish(Sector s, string packet, int excludeId = -1)
    {
        foreach (var p in s.Subscribers.Values)
            if (p.Id != excludeId)
                p.SendPacket?.Invoke(packet);
    }

    /// <summary>
    /// 입장. WELCOME 전송 후, 플레이어 actor의 EnterWorld job에서 호출.
    /// 순서: ①자기 등록 → ②구독 등록 → ③섹터 스캔(자신에게 SPAWN — 자기 자신 포함,
    /// 클라가 자기 엔티티를 알아야 함) → ④기존 관전자에게 내 SPAWN.
    /// </summary>
    public static void EnterWorld(SectorGrid g, Player self)
    {
        var c = g.CellOf(self.X, self.Y);
        g[c.X, c.Y].Entities[self.Id] = self;                    // ①

        self.ViewCX = c.X; self.ViewCY = c.Y;
        var view = g.ViewOf(c.X, c.Y);
        for (int gy = view.MinY; gy <= view.MaxY; gy++)
        for (int gx = view.MinX; gx <= view.MaxX; gx++)
        {
            var s = g[gx, gy];
            s.Subscribers[self.Id] = self;                       // ② 등록 먼저
            foreach (var e in s.Entities.Values)                 // ③ 그다음 스캔
                self.SendPacket?.Invoke(Packets.Spawn(e));
        }

        Publish(g[c.X, c.Y], Packets.Spawn(self), excludeId: self.Id);   // ④
    }

    /// <summary>
    /// 퇴장. 소유자 큐(ProcessDespawn)에서 호출.
    /// 통지 → 등록 해제 → 구독 해제 순.
    /// </summary>
    public static void LeaveWorld(SectorGrid g, Player self)
    {
        var c = g.CellOf(self.X, self.Y);
        Publish(g[c.X, c.Y], Packets.Despawn(self.Id), excludeId: self.Id);
        g[c.X, c.Y].Entities.TryRemove(self.Id, out _);

        var view = g.ViewOf(self.ViewCX, self.ViewCY);
        for (int gy = view.MinY; gy <= view.MaxY; gy++)
        for (int gx = view.MinX; gx <= view.MaxX; gx++)
            g[gx, gy].Subscribers.TryRemove(self.Id, out _);
    }

    /// <summary>NPC 퇴장 (구독이 없으므로 등록만 정리).</summary>
    public static void LeaveWorldNpc(SectorGrid g, Npc self)
    {
        var s = g.SectorAt(self.X, self.Y);
        Publish(s, Packets.Despawn(self.Id));
        s.Entities.TryRemove(self.Id, out _);
    }

    /// <summary>
    /// 이동 공통 처리 (플레이어·NPC). 텔레포트(리스폰)도 동일 경로 — diff 수학이
    /// 인접 이동과 원거리 점프를 구분하지 않는다.
    ///   1) 셀이 바뀌면 등록 이동(add신→remove구) + 관전자 diff 통지
    ///   2) 새 셀 관전자 전원에게 위치 STATE push (자기 자신 포함 — 서버 권위 에코)
    /// </summary>
    public static void EntityMoved(SectorGrid g, Entity e, float oldX, float oldY)
    {
        var oc = g.CellOf(oldX, oldY);
        var nc = g.CellOf(e.X, e.Y);

        if (oc != nc)
        {
            var oldS = g[oc.X, oc.Y];
            var newS = g[nc.X, nc.Y];
            newS.Entities[e.Id] = e;                              // add 먼저
            oldS.Entities.TryRemove(e.Id, out _);

            // 새로 보게 된 관전자 → SPAWN / 더는 못 보는 관전자 → DESPAWN.
            // 패킷 문자열은 필요해질 때 1회만 생성해 공유.
            string? spawn = null, despawn = null;
            foreach (var p in newS.Subscribers.Values)
                if (p.Id != e.Id && !oldS.Subscribers.ContainsKey(p.Id))
                    p.SendPacket?.Invoke(spawn ??= Packets.Spawn(e));
            foreach (var p in oldS.Subscribers.Values)
                if (p.Id != e.Id && !newS.Subscribers.ContainsKey(p.Id))
                    p.SendPacket?.Invoke(despawn ??= Packets.Despawn(e.Id));
        }

        var cur = g[nc.X, nc.Y];
        if (cur.HasSubscribers)
            Publish(cur, Packets.StateOne(e));
    }

    /// <summary>
    /// 플레이어 이동 = 엔티티 이동 + 시야(구독) 갱신.
    /// 시야 중심 셀이 바뀌면: 진입 섹터는 "구독 등록 → 엔티티 스캔(자신에게 SPAWN)",
    /// 이탈 섹터는 "구독 해제 → 자신에게 DESPAWN".
    /// </summary>
    public static void PlayerMoved(SectorGrid g, Player self, float oldX, float oldY)
    {
        EntityMoved(g, self, oldX, oldY);

        var nc = g.CellOf(self.X, self.Y);
        if (nc.X == self.ViewCX && nc.Y == self.ViewCY) return;

        var oldView = g.ViewOf(self.ViewCX, self.ViewCY);
        var newView = g.ViewOf(nc.X, nc.Y);
        self.ViewCX = nc.X; self.ViewCY = nc.Y;

        for (int gy = newView.MinY; gy <= newView.MaxY; gy++)
        for (int gx = newView.MinX; gx <= newView.MaxX; gx++)
        {
            if (oldView.Contains(gx, gy)) continue;               // 유지 섹터
            var s = g[gx, gy];
            s.Subscribers[self.Id] = self;                        // 등록 먼저
            foreach (var e in s.Entities.Values)                  // 그다음 스캔
                if (e.Id != self.Id)
                    self.SendPacket?.Invoke(Packets.Spawn(e));
        }

        for (int gy = oldView.MinY; gy <= oldView.MaxY; gy++)
        for (int gx = oldView.MinX; gx <= oldView.MaxX; gx++)
        {
            if (newView.Contains(gx, gy)) continue;
            var s = g[gx, gy];
            s.Subscribers.TryRemove(self.Id, out _);
            foreach (var e in s.Entities.Values)
                if (e.Id != self.Id)
                    self.SendPacket?.Invoke(Packets.Despawn(e.Id));
        }
    }
}
```

diff 계산에 집합 자료구조가 없음에 주목 — 3×3 대 3×3 비교는 `ViewBounds.Contains`
정수 비교만으로 끝난다 (**할당 0**).

---

## 3. 서버 기존 파일 수정

### 3.1 `Packets.cs`

추가 1개:

```csharp
/// <summary>단일 엔티티 위치/HP 델타. 클라이언트의 기존 STATE 파서가 그대로 처리한다.</summary>
public static string StateOne(Entity e) =>
    $"STATE|{e.Id},{e.X.ToString("F1", Inv)},{e.Y.ToString("F1", Inv)},{e.Hp}";
```

### 3.2 `Entity.cs`

`Player`에 시야 중심 셀 필드 추가 (X/Y와 동일한 소유권 규칙 — **소유자 큐에서만 접근**):

```csharp
public sealed class Player : Entity
{
    public Action<string>? SendPacket { get; set; }   // 기존 — 이제 실제로 연결된다

    /// <summary>구독 중인 시야의 중심 셀. 소유자 actor 큐에서만 읽고 쓴다.</summary>
    public int ViewCX = -1;
    public int ViewCY = -1;
    ...
}
```

### 3.3 `ServerConfig.cs` / `config.json`

```csharp
public sealed class ServerSection
{
    ...
    // BroadcastIntervalMs 삭제
    /// <summary>AOI 재조정(시야 SPAWN 재전송) 주기. 0이면 끔.</summary>
    public int AoiResyncIntervalMs { get; set; } = 3000;
}
```

`config.json`: `"broadcastIntervalMs": 100` 삭제, `"aoiResyncIntervalMs": 3000` 추가,
`"spatialCellSize": 50.0` → `64.0`. `WorldSection.SpatialCellSize` XML 주석에
"클라이언트 최대 시야/교전 반경(60) 이상" 제약 명시.

### 3.4 `GameWorld.cs`

| 항목 | 변경 |
|---|---|
| `Spatial` 프로퍼티 | `public SectorGrid Spatial { get; }` — 생성자에서 `new SectorGrid(cfg.World.Width, cfg.World.Height, cfg.World.SpatialCellSize)` |
| `ProcessAddPlayer` | `Spatial.Add(p)` / `SendInitialSnapshot(session)` / `BroadcastSpawnDirect(p)` 호출 **삭제**. 대신: `p.SendPacket = session.SendPacket;` 연결 후 WELCOME 전송, 마지막에 `actor.EnterWorld();` (플레이어 큐로 위임 — AOI 등록·초기 SPAWN 은 소유자 큐에서) |
| `ProcessSpawnInitialNpcs` | `Spatial.Add(npc)` 유지 (부팅 시 관전자 0명 → 통지 불필요, 등록만) |
| `ProcessRemovePlayer` | `BroadcastDespawnDirect(playerId)` 삭제 — `actor.Despawn()` 안에서 AOI 통지 수행 |
| `SendInitialSnapshot` | **메서드 삭제** |
| `BroadcastSpawn` / `BroadcastDespawn` / `NotifyAttack` / `NotifyDeath` / `NotifyRespawn` / `BroadcastSpawnDirect` / `BroadcastDespawnDirect` / `BroadcastDirect` | **전부 삭제** (호출부는 §3.5/§3.6에서 Aoi 직접 호출로 대체) |
| `StartBroadcaster` / `RouteBroadcastSnapshot` / `BroadcastSnapshotDirect` / `BroadcastActor` 클래스 / `_broadcaster` 필드 / `_broadcastInterval` | **전부 삭제** |
| `GetSnapshot` / `Stop` | 유지 (Stop에서 `_broadcaster` 관련 2단계 제거) |
| 신규 | `public TimeSpan AoiResyncInterval => TimeSpan.FromMilliseconds(Config.Server.AoiResyncIntervalMs);` |

`_sessions` Dictionary는 세션 수 집계(GetSnapshot)와 Stop의 일괄 close에만 남는다.

### 3.5 `PlayerActor.cs`

```csharp
// ── 신규: 입장 (World 큐의 ProcessAddPlayer가 호출) ──
public void EnterWorld() => DoAsync<PlayerActor>(static a => a.ProcessEnterWorld(), this);

private void ProcessEnterWorld()
{
    if (_despawned) return;
    Aoi.EnterWorld(_world.Spatial, _player);
    if (_world.AoiResyncInterval > TimeSpan.Zero)
        DoAsyncAfter(_world.AoiResyncInterval, ResyncTick);
}

// ── 신규: 재조정 틱 (D7) — 시야 내 전 엔티티 SPAWN 재전송 (멱등 upsert) ──
private void ResyncTick()
{
    if (_despawned || _world.IsStopping) return;
    var g = _world.Spatial;
    var view = g.ViewOf(_player.ViewCX, _player.ViewCY);
    for (int gy = view.MinY; gy <= view.MaxY; gy++)
    for (int gx = view.MinX; gx <= view.MaxX; gx++)
        foreach (var e in g[gx, gy].Entities.Values)
            _player.SendPacket?.Invoke(Packets.Spawn(e));
    DoAsyncAfter(_world.AoiResyncInterval, ResyncTick);
}
```

기존 메서드 수정:

| 메서드 | 변경 |
|---|---|
| `ProcessMove` | 마지막 줄 `_world.Spatial.UpdatePosition(_player, oldX, oldY)` → `Aoi.PlayerMoved(_world.Spatial, _player, oldX, oldY)` |
| `ProcessReceiveDamage` | `_world.NotifyAttack(...)` → `Aoi.PublishAt(_world.Spatial, _player.X, _player.Y, Packets.Attack(atk.AttackerId, _player.Id, dealt));` + `Aoi.PublishAt(..., Packets.StateOne(_player));` (HP 반영). 사망 시 `_world.NotifyDeath(...)` → `Aoi.PublishAt(..., Packets.Death(_player.Id, atk.AttackerId))` |
| `Respawn` | `Spatial.UpdatePosition` → `Aoi.PlayerMoved(_world.Spatial, _player, oldX, oldY)` (텔레포트 = 동일 경로), `_world.NotifyRespawn(...)` → `Aoi.PublishAt(_world.Spatial, _player.X, _player.Y, Packets.Respawn(_player.Id, _player.X, _player.Y, _player.Hp))` |
| `ProcessDespawn` | `_world.Spatial.Remove(_player)` → `Aoi.LeaveWorld(_world.Spatial, _player)` |

`PublishAt`을 같은 좌표로 연속 호출하면 섹터 lookup이 중복되므로, 구현 시
`var s = _world.Spatial.SectorAt(x, y)` 한 번 받아 `Aoi.Publish(s, ...)` 2~3회로 처리.

### 3.6 `NpcActor.cs`

| 메서드 | 변경 |
|---|---|
| `MoveTo` | `_world.Spatial.UpdatePosition(_npc, ox, oy)` → `Aoi.EntityMoved(_world.Spatial, _npc, ox, oy)` |
| `ProcessReceiveDamage` | `NotifyAttack`/`NotifyDeath` → §3.5와 동일하게 `Aoi.Publish` (ATTACK + StateOne, 사망 시 DEATH) |
| `Respawn` | `Spatial.UpdatePosition` → `Aoi.EntityMoved(...)`, `NotifyRespawn` → `Aoi.PublishAt(..., Packets.Respawn(...))` |
| `ProcessDespawn` | `_world.Spatial.Remove(_npc)` → `Aoi.LeaveWorldNpc(_world.Spatial, _npc)` |

NPC 이동 push는 `EntityMoved` 내부의 `HasSubscribers` 체크 덕분에 **관전자 없는
구역에서 패킷 생성 비용이 0**이다 — 한적한 월드 영역의 NPC 50마리가 공짜가 된다.

### 3.7 `GameServer.cs`

`Start()`에서 `_world.StartBroadcaster();` 삭제. 그 외 변경 없음.

---

## 4. 통지 매트릭스 (구현 검증용 기준표)

| 사건 | 실행 큐 | 수신자 | 패킷 |
|---|---|---|---|
| 입장 | 본인 (EnterWorld job) | 본인: 3×3 전 엔티티(자신 포함) / 셀 관전자: 본인 | SPAWN |
| 같은 셀 이동 | 소유자 | Sub(셀) — 본인 포함(권위 에코) | STATE(1) |
| 셀 경계 이동 | 소유자 | Sub(신)\Sub(구): SPAWN / Sub(구)\Sub(신): DESPAWN / Sub(신): STATE | |
| ↳ 플레이어면 추가 | 본인 | 본인: 진입 섹터 엔티티 SPAWN, 이탈 섹터 엔티티 DESPAWN | |
| 피격 | 피해자 | Sub(피해자 셀) | ATTACK, STATE(1) |
| 사망 | 피해자 | Sub(피해자 셀) | DEATH |
| 리스폰(텔레포트) | 본인 | 이동 diff와 동일 + Sub(신 셀): RESPAWN | |
| 퇴장 | 본인 | Sub(셀)\본인 | DESPAWN |
| 재조정 틱 | 본인 | 본인: 3×3 전 엔티티 | SPAWN (재전송) |

---

## 5. 클라이언트 수정 (`AdvancedMmorpgClient`)

### 5.0 프로토콜 호환성 — 수정 불필요 확인표

| 패킷 | 현재 처리 (WorldState.cs) | AOI 하에서 |
|---|---|---|
| SPAWN | `Entities[id] = new EntityView` — **upsert** | 중복 SPAWN(핸드셰이크 양쪽 발화, 재조정) 무해 ✔ |
| DESPAWN | `TryRemove` | 모르는 id 무해 ✔ |
| STATE | 아는 id만 갱신, 엔트리 수 무관 | 단일 엔트리 델타 그대로 동작 ✔ |
| DEATH/RESPAWN | 아는 id만 갱신 | ✔ |
| ATTACK | 무시 | ✔ |

**단 하나의 구조적 문제**: 모든 봇이 `WorldState` 하나를 공유한다
(Program → BotManager → 전 BotClient + Renderer). AOI에서는 봇 A의 시야 이탈
DESPAWN이 봇 B가 보고 있는 엔티티까지 지운다. → **봇별 뷰 분리(D8) 필수.**

### 5.1 봇별 WorldState 분리

| 파일 | 변경 |
|---|---|
| `BotClient.cs` | 생성자에서 `WorldState` 파라미터 제거, 내부에서 `World = new WorldState()` 생성 후 `NetworkClient`에 전달. `public WorldState World { get; }` 노출. AI(`TickAi`/`FindNearestEnemy` 등)는 `_world` → `World` 사용 (자기 시야만 보게 됨 — AOI 의미상 올바른 동작) |
| `BotManager.cs` | 생성자에서 `WorldState` 파라미터 제거 |
| `Program.cs` | 공유 `WorldState` 생성 제거, `Game1`에 `BotManager`만 전달 |
| `Game1.cs` | `_world` 필드 제거. `int _cameraIndex` 추가, `Update`에서 **Tab** 키로 순환(키 edge 검출 필요 — 이전 프레임 상태 보관). `Window.Title`에 카메라 봇 이름 표시. `Renderer`에는 매 프레임 `_bots.Bots.Count > 0 ? _bots.Bots[_cameraIndex].World : null` 전달 |
| `Renderer.cs` | 생성자 `WorldState` → 제거하고 `Draw(SpriteBatch, GameTime, WorldState? world)`로 변경 (또는 `SetWorld`). null이면 "접속 대기" 표시 |
| `WorldState.cs` | `MyBotIds`는 봇별 인스턴스라 자기 id 하나만 가지게 됨 — API 유지, 동작 자연 축소 |

### 5.2 TTL 축출 (D7의 클라 측 절반)

유령 엔티티(시야에 없는데 클라 dict에 남은 것)를 서버 재전송이 없으면 만료시킨다:

| 파일 | 변경 |
|---|---|
| `EntityView.cs` | `public long LastSeenMs;` 추가 |
| `WorldState.cs` | SPAWN/STATE/DEATH/RESPAWN 처리 시 `LastSeenMs = Environment.TickCount64` 갱신. 신규 메서드 `EvictStale(long ttlMs)`: `Entities`에서 `now - LastSeenMs > ttlMs`이고 `!IsMyBot(id)`인 항목 제거 |
| `Game1.cs` | `Update`에서 1초에 1회 카메라 뿐 아니라 **모든 봇 뷰**에 `EvictStale(10_000)` 호출 (봇 AI도 유령을 쫓으면 안 되므로) |

TTL 10초 근거: 재조정 주기 3초 × 3회 이상 놓쳐야 축출 — 재조정이 켜져 있는 한
시야 내 엔티티가 잘못 축출될 수 없다. (`aoiResyncIntervalMs = 0`으로 끄면
TTL도 비활성화되도록 `ClientConfig`에 `entityTtlMs` 추가, 0=off, 기본 10000.)

---

## 6. 레이스 카탈로그 — 알려진 경합과 처리 (구현 시 주석으로 남길 것)

| # | 시나리오 | 결과 | 처리 |
|---|---|---|---|
| R1 | A, B가 동시에 서로의 시야로 진입 (각자 자기 큐에서) | 한쪽이 상대 스캔에서 못 볼 수 있음 | **핸드셰이크 (불변식 2)**: 양쪽 다 "자기 등록 → 상대 목록 스캔" 순서면, A의 스캔이 B를 놓쳤다 = B의 등록이 A의 스캔보다 늦음 → B의 스캔은 A의 등록 이후 → B가 A에게 통지. 최소 한쪽 보장, 양쪽 발화 시 중복 SPAWN → 멱등 |
| R2 | 이동 중 관전자가 구/신 섹터 어디에도 없는 순간 | 통지 누락 | add신→remove구 (불변식 3) — "어디에도 없음"이 "양쪽에 있음"으로 바뀜 → 중복 통지 → 멱등 |
| R3 | B가 셀 이동하는 정확히 그 순간 관전자 O도 신 셀을 구독 — B의 DESPAWN이 O의 자체 SPAWN보다 *늦게* 세션 큐에 도착 | O의 클라에서 B가 잘못 제거됨 (유령 누락) | 창이 극히 좁은 희귀 케이스. **재조정 틱(D7)이 3초 내 SPAWN 재전송으로 치유** — push의 잔여 불일치 전 클래스에 대한 수렴 보장 장치 |
| R4 | 관전자 목록 순회 중 대상 세션이 닫힘 | — | `SendPacket`이 `_closed` 체크 후 무시 (기존 구현) |
| R5 | 관전자 목록 순회 중 구독 추가/제거 | CD 열거는 약한 일관성 — 새 구독자 누락 가능 | 누락자는 자기 진입 스캔에서 이미 SPAWN 수신(R1 핸드셰이크) 또는 재조정 치유 |
| R6 | 두 엔티티가 같은 섹터 CD에 동시 쓰기 | — | 서로 다른 키 (불변식 1: 엔티티별 단일 작성자) — CD가 보장 |
| R7 | 느린 클라이언트 | 송신 큐 포화 | 기존 drop + 강제 종료 정책 그대로 (NetworkServer.cs:293) |

---

## 7. 구현 순서 — 5단계, 각 단계는 독립적으로 빌드·실행 가능

빌드는 `dotnet build All.sln`. 각 단계 끝에서 서버+클라이언트를 실제 구동해 확인한다.

**1단계 — 그리드 교체 (동작 불변)**
`Sector.cs`, `SectorGrid.cs` 신규 (§2.1, §2.2) + 임시로 SectorGrid에
`UpdatePosition(e, oldX, oldY)` (add신→remove구만 하는 버전) 추가.
`SpatialIndex.cs` 삭제, `GameWorld.Spatial` 타입 교체. 전역 브로드캐스트는 그대로.
→ 검증: 기존과 완전 동일하게 동작 (봇 전투, NPC 어그로, STATE 렌더링).

**2단계 — 클라이언트 봇별 뷰 (§5.1)**
전역 브로드캐스트 서버에서도 정상 동작한다 (각 봇이 전체 데이터를 자기 뷰에 수신).
→ 검증: Tab으로 카메라 전환, 각 뷰가 전체 월드 표시(아직 AOI 없음), 봇 AI 정상.

**3단계 — 서버 push 전환 (핵심)**
`Aoi.cs` 신규 (§2.3), `Packets.StateOne` (§3.1), `Player.ViewCX/CY` (§3.2),
`PlayerActor`/`NpcActor`/`GameWorld`/`GameServer` 수정 (§3.4~3.7 — 재조정 틱 제외),
1단계의 임시 `UpdatePosition` 삭제, config `spatialCellSize: 64`.
전역 브로드캐스트 전부 삭제.
→ 검증 (§8 시나리오 전체): 카메라 봇 시야에 근처 엔티티만 보임, 이동 시
SPAWN/DESPAWN 발생, 원거리 전투 미표시, 사망/리스폰 표시.

**4단계 — 재조정 + TTL (§3.5 ResyncTick, §5.2)**
config `aoiResyncIntervalMs: 3000` 추가.
→ 검증: 임의 봇 뷰에서 유령/누락이 10초 내 자가 수복 (디버그로 강제 유발:
일시적으로 R3 재현이 어려우므로, 클라에서 무작위 엔티티를 수동 제거해 재조정이
되살리는지 확인하는 임시 테스트 코드 사용 후 제거).

**5단계 — 측정·정리**
`ClientSession`에 송신 패킷 카운터(`Interlocked` long) 추가, 연결 종료 로그에 출력.
→ AOI 전(2단계)/후(4단계) 세션당 송신량 비교 수치 확보. `JobMetrics.Snapshot()`
큐 깊이 확인. 문서(README)에 결과 반영.

---

## 8. 검증 시나리오 체크리스트 (3단계 완료 시점)

- [ ] 봇 30마리 접속 — 카메라 봇 화면에 **주변 엔티티만** 렌더링됨 (전체 아님)
- [ ] 카메라 봇이 이동하면 전방 엔티티 SPAWN, 후방 엔티티 DESPAWN
- [ ] 멀리 있는 봇끼리의 전투가 카메라 뷰에 나타나지 않음
- [ ] 시야 안 NPC 피격 시 HP 바 즉시 갱신 (STATE 델타), 사망 시 hp=0 시체 표시
- [ ] NPC 리스폰(8초) — 스폰 지점이 시야 안이면 다시 나타남
- [ ] 봇 사망 → 5초 후 원거리 리스폰: 구 위치 관전자에겐 사라지고, 신 위치
      관전자에겐 나타나며, 본인 뷰는 새 지역으로 전면 교체됨
- [ ] 봇 강제 종료(LEAVE) — 주변 관전자 뷰에서 즉시 DESPAWN
- [ ] 서버 정상 종료 (`GameWorld.Stop` 경로) — 예외/행 없음
- [ ] 월드 모서리(0,0 부근)에서 이동 — 경계 클램프로 예외 없음
- [ ] `JobMetrics` 드롭 카운트 0, World 큐 깊이가 전투 중에도 안정

---

## 9. 성능 체크리스트 (구현 중 준수)

- [ ] 패킷 문자열은 **통지 대상이 있을 때만, 1회만** 생성해 전 관전자가 공유
      (`spawn ??=` 패턴, `HasSubscribers` 선체크)
- [ ] 시야 diff는 `ViewBounds.Contains` 정수 비교 — 집합 할당 금지
- [ ] hot path 진입점은 기존 관례대로 `DoAsync<TState>` + static 람다 (closure 0)
- [ ] 통지는 세션 큐 직행 — 수신자 actor 큐/World 큐에 job을 만들지 않는다 (불변식 4)
- [ ] 재조정 틱은 `DoAsyncAfter` 자가 스케줄링, 입장 시점 분산은 자연 확보
      (접속 시각이 이미 분산) — 필요 시 첫 틱 지터 추가
- [ ] `QueryRadius`는 NPC AI 전용으로 유지 — AOI 경로에서 호출 금지
      (AOI는 항상 3×3 섹터 직접 순회)

---

## 10. 후속 과제 (이번 범위 밖, 별도 턴)

1. **AoE 직접 라우팅** — 설계 문서 1.3의 `IDamageTarget` 레지스트리 (D9)
2. **셀 경계 데드존** — 경계 왕복 flapping이 측정되면 (D10)
3. `QueryRadius` 호출자 버퍼/visitor 패턴 — NPC AI GC 압박 시
4. 패킷 배칭 — 세션당 송신량이 문제 되면 STATE 다중 엔트리 병합
