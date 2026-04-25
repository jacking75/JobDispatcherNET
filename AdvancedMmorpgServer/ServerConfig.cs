using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdvancedMmorpgServer;

/// <summary>
/// config.json에서 로드되는 서버 설정. NPC 종류, 월드 크기, 스레드 수 등 모든 튜닝 파라미터를 보관.
/// </summary>
public sealed class ServerConfig
{
    public ServerSection Server { get; set; } = new();
    public WorldSection World { get; set; } = new();
    public NpcSection Npc { get; set; } = new();

    public sealed class ServerSection
    {
        public int Port { get; set; } = 9100;
        public int WorkerThreads { get; set; } = 8;
        public int BroadcastIntervalMs { get; set; } = 100;
    }

    public sealed class WorldSection
    {
        public string Name { get; set; } = "AdvancedField";
        public float Width { get; set; } = 1000f;
        public float Height { get; set; } = 1000f;
        public float SpatialCellSize { get; set; } = 50f;
    }

    public sealed class NpcSection
    {
        public int TotalCount { get; set; } = 50;
        public int TickIntervalMs { get; set; } = 200;
        public float RespawnSeconds { get; set; } = 8f;
        public List<NpcTypeConfig> Types { get; set; } = [];
    }

    public sealed class NpcTypeConfig
    {
        public string Kind { get; set; } = "Slime";
        public int Weight { get; set; } = 1;
        public int MaxHp { get; set; } = 100;
        public int Attack { get; set; } = 10;
        public int Defense { get; set; } = 2;
        public float MoveSpeed { get; set; } = 2f;
        public float AggroRange { get; set; } = 10f;
        public float AttackRange { get; set; } = 2f;
        public float FleeHpRatio { get; set; }
        public string Color { get; set; } = "#FFFFFF";
    }

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"[설정] {path} 파일이 없어 기본값 사용");
            return new ServerConfig();
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };
        return JsonSerializer.Deserialize<ServerConfig>(json, options) ?? new ServerConfig();
    }
}
