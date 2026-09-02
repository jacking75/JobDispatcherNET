using System.Collections.Concurrent;

namespace ExampleMmorpgServer;

/// <summary>
/// 공간 인덱스. ConcurrentDictionary 기반 그리드로 주변 플레이어를 빠르게 조회한다.
///
/// lock 사용 현황:
///   - ConcurrentDictionary 내부의 버킷 단위 lock만 사용 (최소)
///   - 외부 lock 없음
///   - 읽기(QueryRadius)는 여러 스레드에서 동시에 가능
///   - 쓰기(UpdatePosition)도 서로 다른 셀이면 동시에 가능
/// </summary>
public class SpatialIndex
{
    private readonly float _cellSize;
    private readonly ConcurrentDictionary<(int, int), ConcurrentDictionary<string, Player>> _grid = [];

    public SpatialIndex(float cellSize = 50f)
    {
        _cellSize = cellSize;
    }

    private (int, int) GetCell(float x, float y) =>
        ((int)MathF.Floor(x / _cellSize), (int)MathF.Floor(y / _cellSize));

    /// <summary>
    /// 플레이어 등록
    /// </summary>
    public void Add(Player player)
    {
        var cell = GetCell(player.X, player.Y);
        var bucket = _grid.GetOrAdd(cell, _ => []);
        bucket[player.PlayerId] = player;
    }

    /// <summary>
    /// 플레이어 제거
    /// </summary>
    public void Remove(Player player)
    {
        var cell = GetCell(player.X, player.Y);
        if (_grid.TryGetValue(cell, out var bucket))
            bucket.TryRemove(player.PlayerId, out _);
    }

    /// <summary>
    /// 위치 갱신 — PlayerActor.DoAsync 안에서 호출됨
    /// </summary>
    public void UpdatePosition(Player player, float oldX, float oldY)
    {
        var oldCell = GetCell(oldX, oldY);
        var newCell = GetCell(player.X, player.Y);

        if (oldCell == newCell)
            return;

        // 구 셀에서 제거, 신 셀에 추가
        if (_grid.TryGetValue(oldCell, out var oldBucket))
            oldBucket.TryRemove(player.PlayerId, out _);

        var newBucket = _grid.GetOrAdd(newCell, _ => []);
        newBucket[player.PlayerId] = player;
    }

    /// <summary>
    /// 반경 내 플레이어 조회.
    /// ConcurrentDictionary 읽기이므로 여러 스레드에서 동시 호출 가능.
    /// </summary>
    public List<Player> QueryRadius(float centerX, float centerY, float radius)
    {
        var result = new List<Player>();

        // 검색 범위에 해당하는 셀만 순회
        int minCellX = (int)MathF.Floor((centerX - radius) / _cellSize);
        int maxCellX = (int)MathF.Floor((centerX + radius) / _cellSize);
        int minCellY = (int)MathF.Floor((centerY - radius) / _cellSize);
        int maxCellY = (int)MathF.Floor((centerY + radius) / _cellSize);

        float radiusSq = radius * radius;

        for (int cx = minCellX; cx <= maxCellX; cx++)
        {
            for (int cy = minCellY; cy <= maxCellY; cy++)
            {
                if (!_grid.TryGetValue((cx, cy), out var bucket))
                    continue;

                foreach (var player in bucket.Values)
                {
                    float dx = player.X - centerX;
                    float dy = player.Y - centerY;
                    if (dx * dx + dy * dy <= radiusSq)
                        result.Add(player);
                }
            }
        }

        return result;
    }
}
