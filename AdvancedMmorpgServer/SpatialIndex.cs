using System.Collections.Concurrent;

namespace AdvancedMmorpgServer;

/// <summary>
/// ConcurrentDictionary 기반 그리드 공간 인덱스. 외부 lock 없음.
/// Entity의 X/Y는 자기 Actor 안에서만 변경되지만,
/// 위치 셀 갱신은 ConcurrentDictionary의 버킷 단위 lock으로 안전성 확보.
/// </summary>
public sealed class SpatialIndex
{
    private readonly float _cellSize;
    private readonly ConcurrentDictionary<(int, int), ConcurrentDictionary<int, Entity>> _grid = [];

    public SpatialIndex(float cellSize)
    {
        _cellSize = cellSize;
    }

    private (int, int) Cell(float x, float y) =>
        ((int)MathF.Floor(x / _cellSize), (int)MathF.Floor(y / _cellSize));

    public void Add(Entity e)
    {
        var bucket = _grid.GetOrAdd(Cell(e.X, e.Y), _ => []);
        bucket[e.Id] = e;
    }

    public void Remove(Entity e)
    {
        if (_grid.TryGetValue(Cell(e.X, e.Y), out var bucket))
            bucket.TryRemove(e.Id, out _);
    }

    public void UpdatePosition(Entity e, float oldX, float oldY)
    {
        var oldCell = Cell(oldX, oldY);
        var newCell = Cell(e.X, e.Y);
        if (oldCell == newCell) return;

        if (_grid.TryGetValue(oldCell, out var oldBucket))
            oldBucket.TryRemove(e.Id, out _);
        var newBucket = _grid.GetOrAdd(newCell, _ => []);
        newBucket[e.Id] = e;
    }

    /// <summary>반경 내 엔티티 조회. 종류 필터 가능.</summary>
    public List<Entity> QueryRadius(float cx, float cy, float radius, EntityKind? onlyKind = null, int? excludeId = null)
    {
        var result = new List<Entity>();
        int minX = (int)MathF.Floor((cx - radius) / _cellSize);
        int maxX = (int)MathF.Floor((cx + radius) / _cellSize);
        int minY = (int)MathF.Floor((cy - radius) / _cellSize);
        int maxY = (int)MathF.Floor((cy + radius) / _cellSize);
        float r2 = radius * radius;

        for (int gx = minX; gx <= maxX; gx++)
        for (int gy = minY; gy <= maxY; gy++)
        {
            if (!_grid.TryGetValue((gx, gy), out var bucket)) continue;
            foreach (var e in bucket.Values)
            {
                if (excludeId is int ex && e.Id == ex) continue;
                if (!e.IsAlive) continue;
                if (onlyKind is EntityKind k && e.Kind != k) continue;
                float dx = e.X - cx, dy = e.Y - cy;
                if (dx * dx + dy * dy <= r2)
                    result.Add(e);
            }
        }
        return result;
    }

    /// <summary>가장 가까운 플레이어. NPC AI에서 사용.</summary>
    public Player? FindNearestPlayer(float cx, float cy, float maxRange)
    {
        var candidates = QueryRadius(cx, cy, maxRange, EntityKind.Player);
        Player? nearest = null;
        float bestSq = float.MaxValue;
        foreach (var e in candidates)
        {
            if (e is not Player p) continue;
            float dx = p.X - cx, dy = p.Y - cy;
            float d = dx * dx + dy * dy;
            if (d < bestSq) { bestSq = d; nearest = p; }
        }
        return nearest;
    }
}
