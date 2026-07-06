namespace AdvancedMmorpgServer;

/// <summary>
/// Fixed 2D sector grid. Cell size must be at least the client view/combat range
/// so a 3x3 view covers visible entities.
/// </summary>
public sealed class SectorGrid
{
    private readonly Sector[,] _sectors;
    private readonly float _cellSize;

    public int CellsX { get; }
    public int CellsY { get; }

    public SectorGrid(float worldW, float worldH, float cellSize)
    {
        _cellSize = cellSize;
        CellsX = Math.Max(1, (int)MathF.Ceiling(worldW / cellSize));
        CellsY = Math.Max(1, (int)MathF.Ceiling(worldH / cellSize));
        _sectors = new Sector[CellsY, CellsX];

        for (var y = 0; y < CellsY; y++)
        for (var x = 0; x < CellsX; x++)
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

    public ViewBounds ViewOf(int cx, int cy) => new(
        Math.Max(0, cx - 1),
        Math.Max(0, cy - 1),
        Math.Min(CellsX - 1, cx + 1),
        Math.Min(CellsY - 1, cy + 1));

    public void Add(Entity e) => SectorAt(e.X, e.Y).Entities[e.Id] = e;

    public void Remove(Entity e) => SectorAt(e.X, e.Y).Entities.TryRemove(e.Id, out _);

    public List<Entity> QueryRadius(float cx, float cy, float radius,
        EntityKind? onlyKind = null, int? excludeId = null, bool aliveOnly = true)
    {
        var result = new List<Entity>();
        var minX = Math.Max(0, (int)MathF.Floor((cx - radius) / _cellSize));
        var maxX = Math.Min(CellsX - 1, (int)MathF.Floor((cx + radius) / _cellSize));
        var minY = Math.Max(0, (int)MathF.Floor((cy - radius) / _cellSize));
        var maxY = Math.Min(CellsY - 1, (int)MathF.Floor((cy + radius) / _cellSize));
        var r2 = radius * radius;
        HashSet<int>? seen = maxX > minX || maxY > minY ? [] : null;

        for (var gy = minY; gy <= maxY; gy++)
        for (var gx = minX; gx <= maxX; gx++)
        {
            foreach (var e in _sectors[gy, gx].Entities.Values)
            {
                if (excludeId is int ex && e.Id == ex) continue;
                if (aliveOnly && !e.IsAlive) continue;
                if (onlyKind is EntityKind k && e.Kind != k) continue;
                if (seen is not null && !seen.Add(e.Id)) continue;

                var dx = e.X - cx;
                var dy = e.Y - cy;
                if (dx * dx + dy * dy <= r2)
                    result.Add(e);
            }
        }

        return result;
    }

    public Player? FindNearestPlayer(float cx, float cy, float maxRange)
    {
        var candidates = QueryRadius(cx, cy, maxRange, EntityKind.Player);
        Player? nearest = null;
        var bestSq = float.MaxValue;

        foreach (var e in candidates)
        {
            if (e is not Player p) continue;
            var dx = p.X - cx;
            var dy = p.Y - cy;
            var d = dx * dx + dy * dy;
            if (d >= bestSq) continue;
            bestSq = d;
            nearest = p;
        }

        return nearest;
    }
}

public readonly record struct ViewBounds(int MinX, int MinY, int MaxX, int MaxY)
{
    public bool Contains(int gx, int gy) =>
        gx >= MinX && gx <= MaxX && gy >= MinY && gy <= MaxY;
}
