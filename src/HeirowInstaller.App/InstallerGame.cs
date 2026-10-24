using System.Collections.Concurrent;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ProjectZ.Shared.Content;
using ProjectZ.Shared.Drawing;

namespace HeirowInstaller.App;

internal sealed class InstallerGame : Game
{
    private readonly GraphicsDeviceManager graphics;
    private readonly ConcurrentQueue<Action> actions = new();
    private readonly ConcurrentQueue<(string Path, TaskCompletionSource<bool>? Completion)> captures = new();
    private readonly AppArguments arguments;
    private SceneManager manager = null!;
    internal InstallerScene MainScene { get; private set; } = null!;
    private SpriteBatch spriteBatch = null!;
    private int drawCount;
    private bool automaticCapture;

    public InstallerGame(AppArguments arguments)
    {
        this.arguments = arguments;
        Window.Title = "heirowInstaller | SocketJack";
        Window.AllowUserResizing = true;
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1d / 60);
        InactiveSleepTime = TimeSpan.FromMilliseconds(40);
        Content.RootDirectory = "Content";
        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1180,
            PreferredBackBufferHeight = 800,
            PreferMultiSampling = true,
            SynchronizeWithVerticalRetrace = true,
            GraphicsProfile = GraphicsProfile.HiDef
        };
    }

    protected override void LoadContent()
    {
        spriteBatch = new SpriteBatch(GraphicsDevice);
        var resources = new ContentContainer(Content, GraphicsDevice);
        resources.LoadVectorFontsOnly();
        Services.AddService(typeof(SpriteBatch), spriteBatch);
        Services.AddService(typeof(GraphicsDevice), GraphicsDevice);
        Services.AddService(typeof(ContentContainer), resources);
        var quality = GraphicsQuality.ForDevice(GraphicsDevice);
        quality.AntiAliasingSamples = 16;
        quality.Filtering = TextureFiltering.Anisotropic16x;
        quality.PpiPerfectText = true;
        manager = new SceneManager(this, 60) { MinimumViewportSize = new Point(980, 760), UseHardwareInput = true };
        MainScene = new InstallerScene(manager, this, arguments);
        manager.AddScene("installer", MainScene);
        manager.ActiveScene = MainScene;
        MainScene.Rebuild();
        if (arguments.SelfUpdateSmokeFeed is not null)
            _ = MainScene.Controller.RunSelfUpdateAcceptanceAsync();
        else if (arguments.SmokeDirectory is not null)
            _ = DesktopSmoke.RunAsync(this, arguments.SmokeDirectory);
        else if (arguments.PackagePath is not null)
            _ = MainScene.OpenPackageAsync(arguments.PackagePath);
    }

    internal void Enqueue(Action action) => actions.Enqueue(action);

    internal Task OnUiAsync(Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => { try { action(); completion.SetResult(true); } catch (Exception error) { completion.SetException(error); } });
        return completion.Task;
    }

    internal Task CaptureAsync(string path)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        captures.Enqueue((Path.GetFullPath(path), completion));
        return completion.Task;
    }

    protected override void Update(GameTime gameTime)
    {
        while (actions.TryDequeue(out var action)) action();
        MainScene.Controller.ExpirePublisherSession();
        manager.Tick(gameTime);
        MainScene.RefreshLayoutIfNeeded();
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Theme.Background);
        manager.Draw(gameTime);
        drawCount++;
        if (!automaticCapture && drawCount > 15 && arguments.CapturePath is not null)
        {
            automaticCapture = true;
            captures.Enqueue((arguments.CapturePath, null));
        }
        while (captures.TryDequeue(out var request))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.Path)!);
                var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
                var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
                using var target = new RenderTarget2D(GraphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
                GraphicsDevice.SetRenderTarget(target);
                GraphicsDevice.Clear(Theme.Background);
                manager.Draw(gameTime);
                GraphicsDevice.SetRenderTarget(null);
                using var file = File.Create(request.Path);
                target.SaveAsPng(file, width, height);
                request.Completion?.SetResult(true);
            }
            catch (Exception error) { request.Completion?.SetException(error); throw; }
        }
        base.Draw(gameTime);
    }

    internal void Activate(string id)
    {
        var rectangle = MainScene.Control(id).Rectangle;
        var x = rectangle.Center.X;
        var y = rectangle.Center.Y;
        manager.InjectMouseMove(x, y, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        manager.InjectMouseLeftDown(x, y, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        manager.InjectMouseLeftUp(x, y, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
    }

    internal void ResizeForAcceptance(int width, int height)
    {
        graphics.PreferredBackBufferWidth = width;
        graphics.PreferredBackBufferHeight = height;
        graphics.ApplyChanges();
    }

    protected override void UnloadContent()
    {
        MainScene?.Controller.Dispose();
        MainScene?.Dispose();
        spriteBatch?.Dispose();
        base.UnloadContent();
    }
}
