using System.Collections.Concurrent;

namespace ExampleSectorServer;

/// <summary>
/// 존을 섹터 그리드로 나누고 플레이어 라우팅을 담당한다.
///
/// 구조 예시 (3x3, 섹터 크기 100x100):
///   ┌──────────┬──────────┬──────────┐
///   │ (0,0)    │ (1,0)    │ (2,0)    │  ← 스레드#1 담당
///   │ 0~100    │ 100~200  │ 200~300  │
///   ├──────────┼──────────┼──────────┤
///   │ (0,1)    │ (1,1)    │ (2,1)    │  ← 스레드#2 담당
///   ├──────────┼──────────┼──────────┤
///   │ (0,2)    │ (1,2)    │ (2,2)    │  ← 스레드#3 담당
///   └──────────┴──────────┴──────────┘
///
/// ★ 위험 지점: 섹터 경계선 (예: x=100 부근)
///   섹터(0,0)의 플레이어가 섹터(1,0)의 플레이어를 공격할 때
///   → 반드시 스냅샷 + 대상 섹터 DoAsync 패턴을 사용해야 안전
/// </summary>
public class GameZone
{
    private readonly ZoneSector[,] _sectors;
    private readonly int _cols, _rows;
    private readonly float _sectorWidth, _sectorHeight;
    private readonly float _zoneWidth, _zoneHeight;

    // 플레이어 → 섹터 매핑 (라우팅용, ConcurrentDictionary로 여러 스레드에서 조회 가능)
    private readonly ConcurrentDictionary<string, ZoneSector> _playerSectorMap = [];

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

    // ── 플레이어 입장/퇴장 ──

    public void EnterZone(Player player, float spawnX, float spawnY)
    {
        player.X = Math.Clamp(spawnX, 0, _zoneWidth - 1);
        player.Y = Math.Clamp(spawnY, 0, _zoneHeight - 1);

        var sector = GetSectorAt(player.X, player.Y);
        _playerSectorMap[player.PlayerId] = sector;
        sector.AddPlayer(player);
    }

    public void LeaveZone(string playerId)
    {
        if (_playerSectorMap.TryRemove(playerId, out var sector))
            sector.RemovePlayer(playerId);
    }

    // ── 이동 (섹터 경계 통과 감지) ──

    public void HandleMove(string playerId, float newX, float newY)
    {
        if (!_playerSectorMap.TryGetValue(playerId, out var currentSector))
            return;

        float clampedX = Math.Clamp(newX, 0, _zoneWidth - 1);
        float clampedY = Math.Clamp(newY, 0, _zoneHeight - 1);

        var newSectorCoord = GetSectorCoord(clampedX, clampedY);
        bool crossesBoundary = newSectorCoord != (currentSector.GridX, currentSector.GridY);

        if (!crossesBoundary)
        {
            // 같은 섹터 내 이동 — 간단
            currentSector.MovePlayer(playerId, clampedX, clampedY);
        }
        else
        {
            // ★ 섹터 경계 통과! — 주의해서 처리
            var newSector = _sectors[newSectorCoord.col, newSectorCoord.row];
            TransferPlayer(playerId, currentSector, newSector, clampedX, clampedY);
        }
    }

    /// <summary>
    /// ★ 섹터 간 플레이어 이동 — 가장 위험한 작업.
    ///
    /// 위험 요소:
    ///   1. 구 섹터에서 제거 ~ 신 섹터에 추가 사이의 시간차
    ///   2. 이 시간차 동안 플레이어가 "유령" 상태가 됨
    ///   3. 공격이 빗나가거나, 이중 적용될 수 있음
    ///
    /// 해결: IsTransferring 플래그로 이동 중임을 표시
    ///   → 대상 섹터의 공격 처리에서 이 플래그를 확인하여 무효 처리
    /// </summary>
    private void TransferPlayer(string playerId, ZoneSector oldSector, ZoneSector newSector,
        float newX, float newY)
    {
        oldSector.BeginTransferOut(playerId, newSector, player =>
        {
            // 구 섹터의 DoAsync 안에서 실행됨 — 플레이어 위치 갱신 안전
            player.X = newX;
            player.Y = newY;

            // 라우팅 맵 갱신 (ConcurrentDictionary — 스레드 안전)
            _playerSectorMap[playerId] = newSector;
        });
    }

    // ── 근접 공격 (같은 섹터 vs 다른 섹터) ──

    public void HandleMeleeAttack(string attackerId, string targetId)
    {
        if (!_playerSectorMap.TryGetValue(attackerId, out var aSector)) return;
        if (!_playerSectorMap.TryGetValue(targetId, out var tSector)) return;

        if (aSector == tSector)
        {
            // ✅ 같은 섹터 — 간단하고 안전
            aSector.MeleeAttackSameSector(attackerId, targetId);
        }
        else
        {
            // ⚠ 다른 섹터 — 스냅샷 패턴 사용!
            //   1. 공격자 섹터: 스냅샷 캡처 (공격자 DoAsync 안)
            //   2. 대상 섹터: 스냅샷으로 데미지 적용 (대상 DoAsync 안)
            //   → 두 섹터의 데이터를 동시에 건드리지 않으므로 lock 불필요
            Console.WriteLine($"  [라우터] ⚡ 섹터간 공격 감지: " +
                              $"섹터{aSector.SectorId} → 섹터{tSector.SectorId}");
            aSector.InitiateCrossSectorMelee(attackerId, tSector, targetId);
        }
    }

    // ── 범위 공격 (여러 섹터에 걸칠 수 있음) ──

    public void HandleAreaAttack(string attackerId, float cx, float cy, float radius)
    {
        if (!_playerSectorMap.TryGetValue(attackerId, out var aSector)) return;

        // AoE 범위에 걸치는 모든 섹터를 계산
        var affectedSectors = GetSectorsInRadius(cx, cy, radius);

        // 공격자 섹터의 DoAsync 안에서:
        //   1. 같은 섹터 내 AoE 처리
        //   2. 스냅샷 캡처 후 다른 섹터로 팬아웃
        aSector.InitiateAreaAttack(attackerId, cx, cy, radius, affectedSectors);
    }

    // ── 귓속말 (다른 섹터 간) ──

    /// <summary>
    /// ★ 귓속말 — 다른 섹터의 플레이어에게 메시지 전달.
    ///
    /// 위험 요소:
    ///   귓속말 대상 Player 객체에 직접 접근하면 → 다른 스레드의 섹터 데이터 접근 → 위험!
    ///
    /// 해결:
    ///   대상 플레이어의 섹터를 찾아서 그 섹터의 DoAsync로 전달.
    ///   → 대상 섹터의 스레드에서 안전하게 처리됨.
    /// </summary>
    public void HandleWhisper(string senderId, string targetId, string message)
    {
        if (!_playerSectorMap.TryGetValue(senderId, out var sSector)) return;
        if (!_playerSectorMap.TryGetValue(targetId, out var tSector)) return;

        bool sameSector = sSector == tSector;

        // 발신자 섹터에서 발신자 이름 캡처 → 대상 섹터로 전달
        sSector.SendWhisper(senderId, tSector, targetId, message, sameSector);
    }

    // ── 유틸 ──

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

    public void PrintStatus()
    {
        Console.WriteLine($"\n  존 ({_zoneWidth}x{_zoneHeight}), {_cols}x{_rows} 섹터, 플레이어:{_playerSectorMap.Count}명");
        for (int y = 0; y < _rows; y++)
            for (int x = 0; x < _cols; x++)
                _sectors[x, y].PrintStatus();
    }

    public async Task DisposeAllAsync()
    {
        for (int y = 0; y < _rows; y++)
            for (int x = 0; x < _cols; x++)
                await _sectors[x, y].DisposeAsync();
    }
}
