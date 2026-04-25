using System.IO;
using System.Text.Json;

namespace AdvancedMmorpgClient;

public sealed class ClientConfig
{
    public ServerSection Server { get; set; } = new();
    public ScreenSection Screen { get; set; } = new();
    public BotSection Bots { get; set; } = new();

    public sealed class ServerSection
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 9100;
    }

    public sealed class ScreenSection
    {
        public int Width { get; set; } = 2560;
        public int Height { get; set; } = 1440;
    }

    public sealed class BotSection
    {
        public int Count { get; set; } = 16;
        public int TickIntervalMs { get; set; } = 250;
        public int SpawnSpacingPixels { get; set; } = 200;
        public string NamePrefix { get; set; } = "Bot";
    }

    public static ClientConfig Load(string path)
    {
        if (!File.Exists(path))
            return new ClientConfig();

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        return JsonSerializer.Deserialize<ClientConfig>(json, options) ?? new ClientConfig();
    }
}
