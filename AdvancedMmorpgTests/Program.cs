using AdvancedMmorpgClient;
using AdvancedMmorpgServer;
using JobDispatcherNET;
using System.Net.Sockets;

var tests = new (string Name, Action Body)[]
{
    ("SectorGrid clamps cells and preserves SpatialIndex query behavior", SectorGridQueries),
    ("AOI enter world sends initial spawns and publishes new player", AoiEnterWorld),
    ("AOI player movement updates visibility subscriptions", AoiPlayerMovementDiff),
    ("WorldState evicts stale non-owned entities", WorldStateEvictsStaleEntities),
    ("LEAVE packet despawns player for nearby observers", LeavePacketDespawnsPlayer),
    ("GameServer dispose drains world without dropped shutdown jobs", GameServerDisposeDoesNotDropShutdownJobs),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

void SectorGridQueries()
{
    var grid = new SectorGrid(100, 100, 25);
    EqualValue((0, 0), grid.CellOf(-1, -1));
    EqualValue((3, 3), grid.CellOf(1000, 1000));

    var alive = new Player(1, "Alive") { X = 10, Y = 10 };
    var dead = new Player(2, "Dead") { X = 12, Y = 10, Hp = 0 };
    var far = new Player(3, "Far") { X = 80, Y = 80 };
    grid.Add(alive);
    grid.Add(dead);
    grid.Add(far);

    var aliveOnly = grid.QueryRadius(10, 10, 10, EntityKind.Player);
    EqualValue(1, aliveOnly.Count);
    EqualValue(1, aliveOnly[0].Id);

    var includingDead = grid.QueryRadius(10, 10, 10, EntityKind.Player, aliveOnly: false)
        .Select(e => e.Id)
        .Order()
        .ToArray();
    EqualList([1, 2], includingDead);
    EqualValue(1, grid.FindNearestPlayer(0, 0, 100)!.Id);
}

void AoiEnterWorld()
{
    var grid = new SectorGrid(100, 100, 25);
    var existing = new Player(1, "Existing") { X = 10, Y = 10 };
    var entering = new Player(2, "Entering") { X = 12, Y = 10 };
    var existingPackets = new List<string>();
    var enteringPackets = new List<string>();
    existing.SendPacket = existingPackets.Add;
    entering.SendPacket = enteringPackets.Add;

    Aoi.EnterWorld(grid, existing);
    Aoi.EnterWorld(grid, entering);

    True(enteringPackets.Any(p => p.StartsWith("SPAWN|1|")));
    True(enteringPackets.Any(p => p.StartsWith("SPAWN|2|")));
    True(existingPackets.Any(p => p.StartsWith("SPAWN|2|")));
    True(grid.SectorAt(existing.X, existing.Y).Subscribers.ContainsKey(existing.Id));
    True(grid.SectorAt(entering.X, entering.Y).Entities.ContainsKey(entering.Id));
}

void AoiPlayerMovementDiff()
{
    var grid = new SectorGrid(200, 200, 50);
    var mover = new Player(1, "Mover") { X = 25, Y = 25 };
    var oldVisible = new Npc(2, "Old", NpcCfg(), 25, 25);
    var newVisible = new Npc(3, "New", NpcCfg(), 175, 25);
    var packets = new List<string>();
    mover.SendPacket = packets.Add;

    grid.Add(oldVisible);
    grid.Add(newVisible);
    Aoi.EnterWorld(grid, mover);
    packets.Clear();

    var oldX = mover.X;
    var oldY = mover.Y;
    mover.X = 125;
    mover.Y = 25;
    Aoi.PlayerMoved(grid, mover, oldX, oldY);

    True(packets.Any(p => p.StartsWith("SPAWN|3|")));
    True(packets.Any(p => p == "DESPAWN|2"));
    True(packets.Any(p => p.StartsWith("STATE|1,")));
    True(grid.SectorAt(mover.X, mover.Y).Entities.ContainsKey(mover.Id));
    False(grid.SectorAt(oldX, oldY).Entities.ContainsKey(mover.Id));
}

void WorldStateEvictsStaleEntities()
{
    var world = new WorldState();
    world.RegisterMyBot(1);
    world.HandlePacket("SPAWN|1|Player|Me|1.0|1.0|100|100|#78B4FF");
    world.HandlePacket("SPAWN|2|Slime|Mob|2.0|2.0|10|10|#7CFC00");

    world.Entities[1].LastSeenMs = Environment.TickCount64 - 20_000;
    world.Entities[2].LastSeenMs = Environment.TickCount64 - 20_000;
    world.EvictStale(10_000);

    True(world.Entities.ContainsKey(1));
    False(world.Entities.ContainsKey(2));
}

void LeavePacketDespawnsPlayer()
{
    var cfg = new ServerConfig
    {
        Server = new ServerConfig.ServerSection
        {
            Port = 9301,
            WorkerThreads = 2,
            AoiResyncIntervalMs = 0,
        },
        World = new ServerConfig.WorldSection
        {
            Name = "Test",
            Width = 50,
            Height = 50,
            SpatialCellSize = 64,
        },
        Npc = new ServerConfig.NpcSection
        {
            TotalCount = 0,
            Types =
            [
                new ServerConfig.NpcTypeConfig { Kind = "Slime", Weight = 1, MaxHp = 1 }
            ],
        },
    };

    using var server = new GameServer(cfg);
    server.Start();

    using var clientA = ConnectClient(cfg.Server.Port);
    clientA.Writer.WriteLine("LOGIN|A");
    var aWelcome = clientA.Reader.ReadLine();
    var aSelf = clientA.Reader.ReadLine();

    using var clientB = ConnectClient(cfg.Server.Port);
    clientB.Writer.WriteLine("LOGIN|B");
    var bWelcome = clientB.Reader.ReadLine();
    var bFirstSpawn = clientB.Reader.ReadLine();
    var bSecondSpawn = clientB.Reader.ReadLine();
    var aSeesB = clientA.Reader.ReadLine();

    clientB.Writer.WriteLine("LEAVE");
    var aSeesBLeave = clientA.Reader.ReadLine();

    True(aWelcome?.StartsWith("WELCOME|") == true);
    True(aSelf?.StartsWith("SPAWN|") == true && aSelf.Contains("|Player|A|"));
    True(bWelcome?.StartsWith("WELCOME|") == true);
    True(new[] { bFirstSpawn, bSecondSpawn }.Any(p => p?.Contains("|Player|A|") == true));
    True(new[] { bFirstSpawn, bSecondSpawn }.Any(p => p?.Contains("|Player|B|") == true));
    True(aSeesB?.StartsWith("SPAWN|") == true && aSeesB.Contains("|Player|B|"));
    True(aSeesBLeave?.StartsWith("DESPAWN|") == true);
}

TestClient ConnectClient(int port)
{
    var tcp = new TcpClient("127.0.0.1", port);
    var stream = tcp.GetStream();
    stream.ReadTimeout = 5000;
    return new TestClient(
        tcp,
        new StreamReader(stream),
        new StreamWriter(stream) { AutoFlush = true });
}

void GameServerDisposeDoesNotDropShutdownJobs()
{
    JobMetrics.Reset();
    var cfg = new ServerConfig
    {
        Server = new ServerConfig.ServerSection
        {
            Port = 9302,
            WorkerThreads = 1,
            AoiResyncIntervalMs = 0,
        },
        World = new ServerConfig.WorldSection
        {
            Name = "DisposeTest",
            Width = 10,
            Height = 10,
            SpatialCellSize = 64,
        },
        Npc = new ServerConfig.NpcSection
        {
            TotalCount = 0,
            Types =
            [
                new ServerConfig.NpcTypeConfig { Kind = "Slime", Weight = 1, MaxHp = 1 }
            ],
        },
    };

    using (var server = new GameServer(cfg))
    {
        server.Start();
    }

    EqualValue(0, JobMetrics.Snapshot().TotalJobsDropped);
}

ServerConfig.NpcTypeConfig NpcCfg() => new()
{
    Kind = "Slime",
    MaxHp = 10,
    Attack = 1,
    Defense = 0,
    MoveSpeed = 1,
};

void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("expected true");
}

void False(bool condition)
{
    if (condition) throw new InvalidOperationException("expected false");
}

void EqualValue<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected}, got {actual}");
}

void EqualList<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
{
    if (expected.Count != actual.Count)
        throw new InvalidOperationException($"expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");

    for (var i = 0; i < expected.Count; i++)
        EqualValue(expected[i], actual[i]);
}

internal sealed class TestClient(TcpClient tcp, StreamReader reader, StreamWriter writer) : IDisposable
{
    public StreamReader Reader => reader;
    public StreamWriter Writer => writer;

    public void Dispose()
    {
        writer.Dispose();
        reader.Dispose();
        tcp.Dispose();
    }
}
