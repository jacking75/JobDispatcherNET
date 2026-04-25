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
    private readonly WorldState _world;
    private readonly BotManager _bots;
    private bool _botsStarted;

    public Game1(ClientConfig cfg, WorldState world, BotManager bots)
    {
        _cfg = cfg;
        _world = world;
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
        Window.Title = "AdvancedMmorpgClient — JobDispatcherNET stress test";
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
        _renderer = new Renderer(GraphicsDevice, _world, _cfg.Screen.Width, _cfg.Screen.Height);
    }

    protected override async void BeginRun()
    {
        base.BeginRun();
        if (_botsStarted) return;
        _botsStarted = true;
        try { await _bots.StartAsync(); }
        catch (Exception ex) { Console.Error.WriteLine($"[Game1] 봇 시작 실패: {ex.Message}"); }
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape))
        {
            _bots.Stop();
            Exit();
        }
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        _sb!.Begin(samplerState: SamplerState.PointClamp);
        _renderer!.Draw(_sb, gameTime);
        _sb.End();
        base.Draw(gameTime);
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        _bots.Stop();
        base.OnExiting(sender, args);
    }
}
