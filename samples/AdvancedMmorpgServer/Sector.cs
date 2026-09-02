using System.Collections.Concurrent;

namespace AdvancedMmorpgServer;

/// <summary>
/// One fixed grid cell. Entity/subscriber writes are performed by owning actor jobs.
/// Reads may happen from any thread through ConcurrentDictionary snapshots.
/// </summary>
public sealed class Sector
{
    public readonly int GX;
    public readonly int GY;

    public readonly ConcurrentDictionary<int, Entity> Entities = new();
    public readonly ConcurrentDictionary<int, Player> Subscribers = new();

    public Sector(int gx, int gy)
    {
        GX = gx;
        GY = gy;
    }

    public bool HasSubscribers => !Subscribers.IsEmpty;
}
