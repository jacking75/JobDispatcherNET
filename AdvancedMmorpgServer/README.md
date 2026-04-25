# AdvancedMmorpgServer / AdvancedMmorpgClient

JobDispatcherNET을 멀티스레드 부하 환경에서 검증하기 위한 한 쌍의 샘플 프로젝트입니다.

- **AdvancedMmorpgServer** — NPC AI를 각자의 Actor로 가지는 단일 월드 게임 서버
- **AdvancedMmorpgClient** — MonoGame 기반 더미 봇 클라이언트 (한 프로세스에 N개의 봇 + 부감 시점 렌더러)


## 1. 무엇을 보여주는 샘플인가

기존 `ExampleMmorpgServer`보다 한 단계 고도화된 형태로, 다음을 동시에 검증합니다.

1. **다수 객체의 멀티스레드 상호작용 (최우선)** — 50마리 NPC가 각자 자기 큐를 가진 Actor로 워커 풀에서 병렬로 tick하면서 이동/공격/도망/부활을 수행할 때, 같은 객체 내 작업은 직렬화되고 서로 다른 객체는 완전히 병렬로 처리되는지
2. **동시 접속** — 16개 봇이 한 프로세스에서 단일 서버에 동시 접속해 끊임없이 MOVE/ATTACK 패킷을 흘릴 때 패킷 라우팅이 안정적인지
3. **장기 안정성** — 송신 큐 포화, NPC 사망/부활 사이클, 타이머 체인 등 부하 패턴을 오래 걸어도 누수/데드락이 없는지

실행 시점의 워커 스레드 수, NPC 수, 봇 수, tick 주기 등은 모두 설정 파일에서 조정 가능합니다.


## 2. 스레딩 모델 한눈에 보기

```
                ┌─ NpcActor #1 ─ DoAsyncAfter(Tick) ─┐
                ├─ NpcActor #2 ─ DoAsyncAfter(Tick) ─┤
JobDispatcher ─┼─ ...                               ├─ 같은 Actor의 작업은 직렬,
 (워커 N개)     ├─ PlayerActor #1 ─ DoAsync(Move) ───┤  서로 다른 Actor는 병렬
                ├─ PlayerActor #2 ─ DoAsync(Attack) ─┤
                └─ BroadcastActor ─ DoAsyncAfter() ──┘

ClientSession ── Channel<string> 송신 큐 ── SendLoopAsync (별도 Task)
              └─ ReadLine 수신 루프 ────── PacketHandler.Handle → World.HandleClient*
```

- 각 NPC/Player는 `AsyncExecutable`을 상속한 Actor — 자기만의 작업 큐를 소유
- NPC의 AI tick은 `DoAsyncAfter(_tickInterval, Tick)`로 자가 스케줄링 (별도 타이머 스레드 불필요)
- 다른 Actor에게 데미지를 줄 때도 `_world.SendDamage(...)` → 타겟 Actor의 큐에 `DoAsync` 등록 → 그쪽 큐에서 순서대로 처리
- 네트워크 송신은 `Channel<string>`(bounded, 1000) 분리 — 게임 로직이 IO에 블로킹되지 않음
- 송신 큐가 200회 이상 누적 드롭되면 슬로우 클라이언트로 간주해 세션 강제 종료


## 3. 빌드 및 실행

### 사전 준비
- .NET 10 SDK
- Windows / macOS / Linux (서버는 콘솔, 클라이언트는 MonoGame DesktopGL — 크로스 플랫폼)

### 솔루션 빌드

```bash
dotnet build All.sln
```

### 서버 실행

```bash
cd AdvancedMmorpgServer
dotnet run
# 다른 설정 파일 사용:
# dotnet run -- mytestconfig.json
```

콘솔에 `'q' 입력 시 종료. Ctrl+C 도 가능.`이 뜨면 준비 완료. `status`를 입력하면 현재 세션/플레이어/NPC 수를 출력합니다.

```
[월드] NPC 50마리 스폰 완료
[네트워크] 포트 9100 리스닝 시작
[워커 #1] 시작
...
[워커 #8] 시작
'q' 입력 시 종료. Ctrl+C 도 가능.

> status
[상태] 세션 16 / 플레이어 16 / 살아있는 NPC 47/50
```

### 클라이언트 실행

서버가 켜져 있는 상태에서:

```bash
cd AdvancedMmorpgClient
dotnet run
```

`2560 × 1440` 창이 열리며, 16개 봇이 30 ms 간격으로 차례로 접속 → AI(이동/교전/도망)를 자동 수행 → 모든 엔티티가 한 화면에 보입니다. ESC로 종료.

> 봇 봇은 자신의 봇은 노란 외곽선으로 표시됩니다.


## 4. 설정 파일

### `AdvancedMmorpgServer/config.json`

| 키 | 기본값 | 의미 |
|---|---|---|
| `server.port` | 9100 | TCP 리스닝 포트 |
| `server.workerThreads` | 8 | `JobDispatcher`가 생성할 워커 스레드 수 |
| `server.broadcastIntervalMs` | 100 | 전체 STATE 스냅샷 브로드캐스트 주기 |
| `world.width` / `world.height` | 1000 | 월드 크기 (단위 없음, 클라이언트에서 화면에 자동 맞춤) |
| `world.spatialCellSize` | 50 | `SpatialIndex`의 격자 셀 크기 |
| `npc.totalCount` | 50 | 부팅 시 스폰할 NPC 수 |
| `npc.tickIntervalMs` | 200 | NPC AI tick 간격 |
| `npc.respawnSeconds` | 8 | 사망 후 부활 대기 시간 |
| `npc.types[]` | 5종 | 종류별 능력치 / 가중치 / 색상 |

NPC 종류는 `weight` 비율로 무작위 스폰됩니다. 예: Slime 4 / Goblin 4 / Wolf 3 / Skeleton 3 / Boss 1 → 평균 100마리당 Boss 약 7마리.

`color`는 `#RRGGBB` 헥스 — 서버가 SPAWN 패킷에 같이 실어 보내고, 클라이언트가 그대로 사용합니다 (`Renderer.ResolveColor`).

### `AdvancedMmorpgClient/clientconfig.json`

| 키 | 기본값 | 의미 |
|---|---|---|
| `server.host` / `server.port` | 127.0.0.1 / 9100 | 접속 대상 서버 |
| `screen.width` / `screen.height` | 2560 / 1440 | 창 크기 |
| `bots.count` | 16 | 한 프로세스에서 띄울 봇 수 |
| `bots.tickIntervalMs` | 250 | 각 봇의 AI tick 간격 (MOVE/ATTACK 발생 주기) |
| `bots.spawnSpacingPixels` | 200 | 봇 사이 접속 시간 분산 (봇 N번째는 N×30ms 지연 접속) |
| `bots.namePrefix` | Bot | 로그인 이름 접두어 |


## 5. 패킷 프로토콜 (텍스트, 줄바꿈 구분, `|` 필드 구분)

### Server → Client
```
WELCOME|playerId|x|y|worldW|worldH
SPAWN|id|kind|name|x|y|hp|maxHp|color
DESPAWN|id
STATE|id,x,y,hp|id,x,y,hp|...
ATTACK|attackerId|targetId|damage
DEATH|id|killerId
RESPAWN|id|x|y|hp
```

### Client → Server
```
LOGIN|botName
MOVE|x|y
ATTACK|targetId
LEAVE
```

전부 텍스트 기반인 이유는 본 샘플의 핵심이 네트워크가 아니라 JobDispatcherNET의 동시성 검증이기 때문입니다.


## 6. 부하 시나리오 예시

| 목적 | 변경할 설정 |
|---|---|
| **NPC 만 늘려 객체 간 상호작용 부하 측정** | `npc.totalCount = 500`, `npc.tickIntervalMs = 100` |
| **동시 접속 부하** | `bots.count = 64` (필요하면 클라이언트 프로세스를 여러 개 띄움) |
| **워커 스레드 수와 처리량의 관계 비교** | `server.workerThreads`를 1 / 2 / 4 / 8 / 16으로 바꿔가며 측정 |
| **장기 안정성** | 위 조합으로 수 시간 방치, `status` 명령으로 누적 NPC/세션 변화 추적 |


## 7. 디렉터리 구조

```
AdvancedMmorpgServer/
  Program.cs            ── 진입점, 콘솔 입력 루프
  GameServer.cs         ── JobDispatcher, NetworkServer, GameWorld 조립
  GameWorld.cs          ── 모든 Actor와 세션 보유, 브로드캐스트
  GameWorker.cs         ── IRunnable — 전용 OS 스레드의 메인 루프
  NpcActor.cs           ── NPC AI (Idle/Chase/Attack/Flee), 자가 스케줄링 tick
  PlayerActor.cs        ── 플레이어 입력 처리 + 데미지 수신
  Entity.cs             ── Player / Npc 데이터
  SpatialIndex.cs       ── ConcurrentDictionary 기반 격자 공간 인덱스
  Packets.cs            ── 텍스트 패킷 인코딩/디코딩
  NetworkServer.cs      ── TCP Accept + ClientSession (Send/Recv 분리)
  ServerConfig.cs       ── config.json 로더
  AttackerSnapshot.cs   ── Actor 간 데미지 전달용 불변 스냅샷
  config.json

AdvancedMmorpgClient/
  Program.cs
  Game1.cs              ── MonoGame Game 진입점
  Renderer.cs           ── 부감 시점 렌더러 (월드 → 화면 자동 스케일)
  PixelFont.cs          ── 5×7 비트맵 폰트 (Content Pipeline 미사용)
  WorldState.cs         ── 모든 봇의 패킷을 받아 갱신되는 단일 월드 뷰
  EntityView.cs         ── 클라이언트 측 엔티티
  BotManager.cs         ── 봇 N개 스폰
  BotClient.cs          ── 봇 AI (Wander/Engage/Flee)
  NetworkClient.cs      ── 봇 1개당 TcpClient + Channel<string> 송신
  ClientConfig.cs       ── clientconfig.json 로더
  clientconfig.json
```


## 8. 트러블슈팅

- **클라이언트 창이 흰색으로 안 뜨고 검정만 보임** — 서버가 안 켜져 있거나 포트가 다른 경우. 서버 콘솔 로그에서 `[네트워크] 포트 N 리스닝 시작`을 먼저 확인.
- **`The process cannot access the file ... AdvancedMmorpgServer.exe` 빌드 에러** — 이전 인스턴스가 종료되지 않은 상태. `taskkill /F /IM AdvancedMmorpgServer.exe` (Windows) 후 재빌드.
- **로그에 `[세션 #N] 송신 큐 포화 — 200개 드롭, 연결 종료`** — 클라이언트가 STATE를 200ms 가까이 못 받아갈 만큼 느린 상황. 정상 보호 동작이며, 클라이언트 부하를 낮추거나 `server.broadcastIntervalMs`를 늘려 해결.
