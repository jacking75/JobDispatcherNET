using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace AdvancedMmorpgClient;

public sealed class Game1 : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch? _sb;
    private Renderer? _renderer;

    private readonly ClientConfig _cfg;
    private readonly BotManager _bots;
    private bool _botsStarted;
    private int _cameraIndex;
    private KeyboardState _previousKeyboard;
    private long _lastTtlSweepMs;

    public Game1(ClientConfig cfg, BotManager bots)
    {
        _cfg = cfg;
        _bots = bots;

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = cfg.Screen.Width,
            PreferredBackBufferHeight = cfg.Screen.Height,
            IsFullScreen = false,
            SynchronizeWithVerticalRetrace = true,
            HardwareModeSwitch = false,
        };

        IsFixedTimeStep = false;
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "AdvancedMmorpgClient - waiting for bots";
        Window.AllowUserResizing = true;
    }

    protected override void Initialize()
    {
        _graphics.ApplyChanges();
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        _renderer = new Renderer(GraphicsDevice, _cfg.Screen.Width, _cfg.Screen.Height);
    }

    protected override async void BeginRun()
    {
        base.BeginRun();
        if (_botsStarted) return;
        _botsStarted = true;

        try { await _bots.StartAsync(); }
        catch (Exception ex) { Console.Error.WriteLine($"[Game1] bot start failed: {ex.Message}"); }
    }

    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        if (keyboard.IsKeyDown(Keys.Escape))
        {
            _bots.Stop();
            Exit();
        }

        if (keyboard.IsKeyDown(Keys.Tab) && !_previousKeyboard.IsKeyDown(Keys.Tab))
        {
            if (_bots.Bots.Count > 0)
                _cameraIndex = (_cameraIndex + 1) % _bots.Bots.Count;
        }

        if (_bots.Bots.Count > 0 && _cameraIndex >= _bots.Bots.Count)
            _cameraIndex = 0;

        var now = Environment.TickCount64;
        if (_cfg.EntityTtlMs > 0 && now - _lastTtlSweepMs >= 1000)
        {
            foreach (var bot in _bots.Bots)
                bot.World.EvictStale(_cfg.EntityTtlMs);
            _lastTtlSweepMs = now;
        }

        UpdateWindowTitle();
        _previousKeyboard = keyboard;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        _sb!.Begin(samplerState: SamplerState.PointClamp);
        _renderer!.Draw(_sb, gameTime, CurrentWorld());
        _sb.End();
        base.Draw(gameTime);
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        _bots.Stop();
        base.OnExiting(sender, args);
    }

    private WorldState? CurrentWorld()
    {
        if (_bots.Bots.Count == 0) return null;
        return _bots.Bots[_cameraIndex].World;
    }

    private void UpdateWindowTitle()
    {
        if (_bots.Bots.Count == 0)
        {
            Window.Title = "AdvancedMmorpgClient - waiting for bots";
            return;
        }

        var bot = _bots.Bots[_cameraIndex];
        Window.Title = $"AdvancedMmorpgClient - camera {bot.Name} ({_cameraIndex + 1}/{_bots.Bots.Count})";
    }
}
