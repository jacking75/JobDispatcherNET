namespace AdvancedMmorpgClient;

/// <summary>
/// N개의 봇을 관리한다. 접속, AI 루프 시작, 종료를 담당한다.
/// </summary>
public sealed class BotManager
{
    private readonly ClientConfig _cfg;
    private readonly WorldState _world;
    private readonly List<BotClient> _bots = [];
    private readonly CancellationTokenSource _cts = new();

    public IReadOnlyList<BotClient> Bots => _bots;

    public BotManager(ClientConfig cfg, WorldState world)
    {
        _cfg = cfg;
        _world = world;
    }

    public async Task StartAsync()
    {
        for (int i = 0; i < _cfg.Bots.Count; i++)
        {
            var name = $"{_cfg.Bots.NamePrefix}{i:D2}";
            var bot = new BotClient(_world, _cfg, name, seed: i * 7919 + 13);
            _bots.Add(bot);

            try
            {
                await bot.ConnectAsync(_cts.Token);
                _ = bot.RunAiAsync(_cts.Token);
                // 접속 분산을 위한 작은 간격
                await Task.Delay(30, _cts.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BotManager] '{name}' 접속 실패: {ex.Message}");
            }
        }
        Console.WriteLine($"[BotManager] {_bots.Count}개 봇 접속 완료");
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        foreach (var b in _bots) b.Close();
    }
}
