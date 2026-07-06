namespace AdvancedMmorpgServer;

/// <summary>
/// Push AOI orchestration. Call methods only from the owner actor job of the
/// entity being changed. Notifications go straight to Player.SendPacket.
/// </summary>
public static class Aoi
{
    public static void PublishAt(SectorGrid g, float x, float y, string packet, int excludeId = -1)
        => Publish(g.SectorAt(x, y), packet, excludeId);

    public static void Publish(Sector s, string packet, int excludeId = -1)
    {
        foreach (var p in s.Subscribers.Values)
        {
            if (p.Id != excludeId)
                p.SendPacket?.Invoke(packet);
        }
    }

    public static void EnterWorld(SectorGrid g, Player self)
    {
        var c = g.CellOf(self.X, self.Y);
        g[c.X, c.Y].Entities[self.Id] = self;

        self.ViewCX = c.X;
        self.ViewCY = c.Y;
        var view = g.ViewOf(c.X, c.Y);

        for (var gy = view.MinY; gy <= view.MaxY; gy++)
        for (var gx = view.MinX; gx <= view.MaxX; gx++)
        {
            var s = g[gx, gy];
            s.Subscribers[self.Id] = self;
            foreach (var e in s.Entities.Values)
                self.SendPacket?.Invoke(Packets.Spawn(e));
        }

        Publish(g[c.X, c.Y], Packets.Spawn(self), excludeId: self.Id);
    }

    public static void LeaveWorld(SectorGrid g, Player self)
    {
        var c = g.CellOf(self.X, self.Y);
        Publish(g[c.X, c.Y], Packets.Despawn(self.Id), excludeId: self.Id);
        g[c.X, c.Y].Entities.TryRemove(self.Id, out _);

        if (self.ViewCX < 0 || self.ViewCY < 0) return;
        var view = g.ViewOf(self.ViewCX, self.ViewCY);
        for (var gy = view.MinY; gy <= view.MaxY; gy++)
        for (var gx = view.MinX; gx <= view.MaxX; gx++)
            g[gx, gy].Subscribers.TryRemove(self.Id, out _);
    }

    public static void LeaveWorldNpc(SectorGrid g, Npc self)
    {
        var s = g.SectorAt(self.X, self.Y);
        Publish(s, Packets.Despawn(self.Id));
        s.Entities.TryRemove(self.Id, out _);
    }

    public static void EntityMoved(SectorGrid g, Entity e, float oldX, float oldY)
    {
        var oc = g.CellOf(oldX, oldY);
        var nc = g.CellOf(e.X, e.Y);

        if (oc != nc)
        {
            var oldS = g[oc.X, oc.Y];
            var newS = g[nc.X, nc.Y];
            newS.Entities[e.Id] = e;
            oldS.Entities.TryRemove(e.Id, out _);

            string? spawn = null;
            string? despawn = null;

            foreach (var p in newS.Subscribers.Values)
            {
                if (p.Id != e.Id && !oldS.Subscribers.ContainsKey(p.Id))
                    p.SendPacket?.Invoke(spawn ??= Packets.Spawn(e));
            }

            foreach (var p in oldS.Subscribers.Values)
            {
                if (p.Id != e.Id && !newS.Subscribers.ContainsKey(p.Id))
                    p.SendPacket?.Invoke(despawn ??= Packets.Despawn(e.Id));
            }
        }

        var cur = g[nc.X, nc.Y];
        if (cur.HasSubscribers)
            Publish(cur, Packets.StateOne(e));
    }

    public static void PlayerMoved(SectorGrid g, Player self, float oldX, float oldY)
    {
        EntityMoved(g, self, oldX, oldY);

        var nc = g.CellOf(self.X, self.Y);
        if (nc.X == self.ViewCX && nc.Y == self.ViewCY) return;

        var oldView = g.ViewOf(self.ViewCX, self.ViewCY);
        var newView = g.ViewOf(nc.X, nc.Y);
        self.ViewCX = nc.X;
        self.ViewCY = nc.Y;

        for (var gy = newView.MinY; gy <= newView.MaxY; gy++)
        for (var gx = newView.MinX; gx <= newView.MaxX; gx++)
        {
            if (oldView.Contains(gx, gy)) continue;
            var s = g[gx, gy];
            s.Subscribers[self.Id] = self;
            foreach (var e in s.Entities.Values)
            {
                if (e.Id != self.Id)
                    self.SendPacket?.Invoke(Packets.Spawn(e));
            }
        }

        for (var gy = oldView.MinY; gy <= oldView.MaxY; gy++)
        for (var gx = oldView.MinX; gx <= oldView.MaxX; gx++)
        {
            if (newView.Contains(gx, gy)) continue;
            var s = g[gx, gy];
            s.Subscribers.TryRemove(self.Id, out _);
            foreach (var e in s.Entities.Values)
            {
                if (e.Id != self.Id)
                    self.SendPacket?.Invoke(Packets.Despawn(e.Id));
            }
        }

        var cur = g[nc.X, nc.Y];
        if (cur.HasSubscribers)
            Publish(cur, Packets.StateOne(self));
    }
}
