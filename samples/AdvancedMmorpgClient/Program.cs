using AdvancedMmorpgClient;

var configPath = args.Length > 0 ? args[0] : "clientconfig.json";
var cfg = ClientConfig.Load(configPath);

var bots = new BotManager(cfg);

using var game = new Game1(cfg, bots);
game.Run();

bots.Stop();
