using JobDispatcherNET;

namespace ExampleSectorServer;

public sealed record ZoneStatus(
    string ZoneSize, int Cols, int Rows, int TotalPlayers,
    IReadOnlyList<SectorStatus> Sectors);

public sealed record SectorStatus(
    string SectorId, float OriginX, float OriginY, float Width, float Height,
    IReadOnlyList<PlayerStatus> Players);

public sealed record PlayerStatus(
    string PlayerId, string Name, float X, float Y,
    int Hp, int MaxHp, bool IsAlive, bool IsTransferring);

/// <summary>
/// 존을 섹터 그리드로 나누고 플레이어 라우팅을 담당한다.
///
/// 본 버전은 GameZone 자체를 AsyncExecutable(actor)로 만들어
/// 라우팅 테이블(<see cref="_playerSectorMap"/>) 접근을 큐 안에서만 일어나게 한다.
///   - _playerSectorMap은 일반 Dictionary, lock 0개, TOCTOU race 없음
///   - 모든 패킷은 GameZone 큐 → 적절한 섹터 큐로 전달 → 섹터 큐 안에서 처리
///   - 같은 caller(예: 네트워크 세션)에서 들어온 순서대로 GameZone 큐에 적재됨
///
/// 구조 예시 (3x3, 섹터 크기 100x100):
///   ┌──────────┬──────────┬──────────┐
///   │ (0,0)    │ (1,0)    │ (2,0)    │  ← 각 섹터가 자기 큐 소유
///   │ 0~100    │ 100~200  │ 200~300  │
///   ├──────────┼──────────┼──────────┤
///   │ (0,1)    │ (1,1)    │ (2,1)    │
///   ├──────────┼──────────┼──────────┤
///   │ (0,2)    │ (1,2)    │ (2,2)    │
///   └──────────┴──────────┴──────────┘
///
/// ★ 섹터 경계 처리:
///   같은 섹터 작업 → 그 섹터 큐 안에서 lock 없이 직렬화
///   섹터간 작업    → 스냅샷 캡처 후 대상 섹터의 DoAsync로 전달
///   섹터 이동      → GameZone이 map 먼저 갱신 → 구 섹터에서 신 섹터로 핸드오프
///
/// 코딩 컨벤션 (ExampleMmorpgServer와 동일):
///   public Handle*/외부진입점 → DoAsync(() => Process*(args))   (큐에 푸시만)
///   private Process*(args)    → 실제 본문 (디버거/스택트레이스 친화적)
/// </summary>
public class GameZone : AsyncExecutable
{
    private readonly ZoneSector[,] _sectors;
    private readonly int _cols, _rows;
    private readonly float _sectorWidth, _sectorHeight;
    private readonly float _zoneWidth, _zoneHeight;

    // GameZone 큐 안에서만 접근 — 일반 Dictionary, lock 0개
    private readonly Dictionary<string, ZoneSector> _playerSectorMap = [];

    public int Cols => _cols;
    public int Rows => _rows;
    public ZoneSector GetSector(int col, int row) => _sectors[col, row];

    public GameZone(float zoneWidth, float zoneHeight, int cols, int rows)
    {
        _zoneWidth = zoneWidth;
        _zoneHeight = zoneHeight;
        _cols = cols;
        _rows = rows;
        _sectorWidth = zoneWidth / cols;
        _sectorHeight = zoneHeight / rows;
        _sectors = new ZoneSector[cols, rows];

        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
                _sectors[x, y] = new ZoneSector(x, y, x * _sectorWidth, y * _sectorHeight,
                    _sectorWidth, _sectorHeight);
    }

    private (int col, int row) GetSectorCoord(float x, float y) =>
        (Math.Clamp((int)(x / _sectorWidth), 0, _cols - 1),
         Math.Clamp((int)(y / _sectorHeight), 0, _rows - 1));

    private ZoneSector GetSectorAt(float x, float y)
    {
        var (c, r) = GetSectorCoord(x, y);
        return _sectors[c, r];
    }

    // ── 외부 진입점 (큐에 푸시만) ─────────────────────────────────────

    public void EnterZone(Player player, float spawnX, float spawnY)
        => DoAsync(() => ProcessEnterZone(player, spawnX, spawnY));

    public void LeaveZone(string playerId)
        => DoAsync(() => ProcessLeaveZone(playerId));

    public void HandleMove(string playerId, float newX, float newY)
        => DoAsync(() => ProcessMove(playerId, newX, newY));

    public void HandleMeleeAttack(string attackerId, string targetId)
        => DoAsync(() => ProcessMeleeAttack(attackerId, targetId));

    public void HandleAreaAttack(string attackerId, float cx, float cy, float radius)
        => DoAsync(() => ProcessAreaAttack(attackerId, cx, cy, radius));

    public void HandleWhisper(string senderId, string targetId, string message)
        => DoAsync(() => ProcessWhisper(senderId, targetId, message));

    // ── 실제 본문 (private, GameZone 큐에서 직렬 실행) ────────────────

    private void ProcessEnterZone(Player player, float spawnX, float spawnY)
    {
        if (_playerSectorMap.ContainsKey(player.PlayerId))
        {
            Console.WriteLine($"  [존] 이미 입장: {player.PlayerId}");
            return;
        }

        float x = Math.Clamp(spawnX, 0, _zoneWidth - 1);
        float y = Math.Clamp(spawnY, 0, _zoneHeight - 1);
        var sector = GetSectorAt(x, y);

        _playerSectorMap[player.PlayerId] = sector;
        // 좌표 mutation은 신 섹터 큐 안에서 일어남 — actor 컨벤션 준수
        sector.AddPlayer(player, x, y);
    }

    private void ProcessLeaveZone(string playerId)
    {
        if (_playerSectorMap.Remove(playerId, out var sector))
            sector.RemovePlayer(playerId);
    }

    private void ProcessMove(string playerId, float newX, float newY)
    {
        if (!_playerSectorMap.TryGetValue(playerId, out var currentSector))
            return;

        float clampedX = Math.Clamp(newX, 0, _zoneWidth - 1);
        float clampedY = Math.Clamp(newY, 0, _zoneHeight - 1);

        var newCoord = GetSectorCoord(clampedX, clampedY);
        bool crossesBoundary = newCoord != (currentSector.GridX, currentSector.GridY);

        if (!crossesBoundary)
        {
            currentSector.MovePlayer(playerId, clampedX, clampedY);
            return;
        }

        // ★ 섹터 경계 통과 — map 먼저 갱신 → 이후의 ProcessMeleeAttack/Whisper 등은 신 섹터로 라우팅
        var newSector = _sectors[newCoord.col, newCoord.row];
        _playerSectorMap[playerId] = newSector;
        currentSector.BeginTransferOut(playerId, newSector, clampedX, clampedY);
    }

    private void ProcessMeleeAttack(string attackerId, string targetId)
    {
        if (!_playerSectorMap.TryGetValue(attackerId, out var aSector)) return;
        if (!_playerSectorMap.TryGetValue(targetId, out var tSector)) return;

        if (aSector == tSector)
        {
            // ✅ 같은 섹터 — 그 섹터 큐 안에서 lock 없이 처리
            aSector.MeleeAttackSameSector(attackerId, targetId);
        }
        else
        {
            // ⚠ 섹터간 — 스냅샷 캡처 후 대상 섹터로 전달
            Console.WriteLine($"  [라우터] ⚡ 섹터간 공격 감지: " +
                              $"섹터{aSector.SectorId} → 섹터{tSector.SectorId}");
            aSector.InitiateCrossSectorMelee(attackerId, tSector, targetId);
        }
    }

    private void ProcessAreaAttack(string attackerId, float cx, float cy, float radius)
    {
        if (!_playerSectorMap.TryGetValue(attackerId, out var aSector)) return;

        var affectedSectors = GetSectorsInRadius(cx, cy, radius);
        aSector.InitiateAreaAttack(attackerId, cx, cy, radius, affectedSectors);
    }

    private void ProcessWhisper(string senderId, string targetId, string message)
    {
        if (!_playerSectorMap.TryGetValue(senderId, out var sSector)) return;
        if (!_playerSectorMap.TryGetValue(targetId, out var tSector)) return;

        bool sameSector = sSector == tSector;
        sSector.SendWhisper(senderId, tSector, targetId, message, sameSector);
    }

    private List<ZoneSector> GetSectorsInRadius(float cx, float cy, float radius)
    {
        var result = new List<ZoneSector>();
        int minCol = Math.Clamp((int)((cx - radius) / _sectorWidth), 0, _cols - 1);
        int maxCol = Math.Clamp((int)((cx + radius) / _sectorWidth), 0, _cols - 1);
        int minRow = Math.Clamp((int)((cy - radius) / _sectorHeight), 0, _rows - 1);
        int maxRow = Math.Clamp((int)((cy + radius) / _sectorHeight), 0, _rows - 1);

        for (int c = minCol; c <= maxCol; c++)
            for (int r = minRow; r <= maxRow; r++)
                result.Add(_sectors[c, r]);
        return result;
    }

    // ── 동기 read API (차단 스냅샷) ──────────────────────────────────

    /// <summary>
    /// 차단 스냅샷. caller 스레드(메인/시뮬레이션)에서 호출.
    /// 1) GameZone 큐 → 섹터 배열을 스냅샷 (라우팅 일관성)
    /// 2) 각 섹터 큐 → 그 섹터의 플레이어 스냅샷
    /// 두 단계 모두 큐 안에서 일어나므로 일관된 read.
    /// </summary>
    public ZoneStatus GetStatus()
    {
        // [1] GameZone 큐에서 매핑 카운트와 섹터 배열을 스냅샷
        using var mapEv = new ManualResetEventSlim(false);
        ZoneSector[]? sectorsToQuery = null;
        int totalPlayers = 0;
        DoAsync(() =>
        {
            totalPlayers = _playerSectorMap.Count;
            sectorsToQuery = new ZoneSector[_cols * _rows];
            int i = 0;
            for (int y = 0; y < _rows; y++)
                for (int x = 0; x < _cols; x++)
                    sectorsToQuery[i++] = _sectors[x, y];
            mapEv.Set();
        });
        mapEv.Wait();

        // [2] 각 섹터 큐에서 플레이어 스냅샷 (섹터별 차단 read)
        var sectorStatuses = new List<SectorStatus>(sectorsToQuery!.Length);
        foreach (var s in sectorsToQuery)
            sectorStatuses.Add(s.GetStatus());

        return new ZoneStatus(
            $"{_zoneWidth}x{_zoneHeight}", _cols, _rows, totalPlayers, sectorStatuses);
    }

    public void PrintStatus()
    {
        var s = GetStatus();
        Console.WriteLine($"\n  존 ({s.ZoneSize}), {s.Cols}x{s.Rows} 섹터, 플레이어:{s.TotalPlayers}명");
        foreach (var sec in s.Sectors)
        {
            if (sec.Players.Count == 0) continue;
            Console.WriteLine($"    섹터{sec.SectorId} [{sec.OriginX:F0},{sec.OriginY:F0}]~" +
                              $"[{sec.OriginX + sec.Width:F0},{sec.OriginY + sec.Height:F0}]: {sec.Players.Count}명");
            foreach (var p in sec.Players)
                Console.WriteLine($"      - {p.Name} ({p.X:F0},{p.Y:F0}) HP:{p.Hp}/{p.MaxHp} " +
                                  $"{(p.IsAlive ? "생존" : "사망")}{(p.IsTransferring ? " [이동중]" : "")}");
        }
    }

    /// <summary>
    /// 종료 — GameZone 큐 drain (라우팅 마감) → 모든 섹터 큐 drain.
    /// </summary>
    public async ValueTask DisposeAllAsync()
    {
        // GameZone 자체 큐 drain — 더 이상 새 라우팅이 섹터로 전달되지 않게 함
        await DisposeAsync();

        // 각 섹터 큐 drain
        for (int y = 0; y < _rows; y++)
            for (int x = 0; x < _cols; x++)
                await _sectors[x, y].DisposeAsync();
    }
}
