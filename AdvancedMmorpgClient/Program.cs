using AdvancedMmorpgClient;

var configPath = args.Length > 0 ? args[0] : "clientconfig.json";
var cfg = ClientConfig.Load(configPath);

var world = new WorldState();
var bots = new BotManager(cfg, world);

using var game = new Game1(cfg, world, bots);
game.Run();

bots.Stop();
