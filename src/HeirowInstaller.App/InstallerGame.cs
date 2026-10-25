using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    private NavigationCapture? navigationCapture;

    public InstallerGame(AppArguments arguments)
    {
        this.arguments = arguments;
        Window.Title = arguments.PackagePath is not null || arguments.MaintenanceProductId is not null ? "Application setup" : "heirowInstaller | SocketJack";
        Window.AllowUserResizing = true;
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1d / 60);
        InactiveSleepTime = TimeSpan.FromMilliseconds(40);
        Content.RootDirectory = "Content";
        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = arguments.PackagePath is not null || arguments.MaintenanceProductId is not null ? 1060 : 1180,
            PreferredBackBufferHeight = 800,
            PreferMultiSampling = true,
            SynchronizeWithVerticalRetrace = true,
            GraphicsProfile = GraphicsProfile.HiDef
        };
        if ((arguments.WizardSmokeDirectory is not null || arguments.SmokeDirectory is not null) && arguments.Isolated)
        {
            // Owned test rendering uses internal scene input, never the user's foreground window.
            var handle = Window.Handle;
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000)); // WS_EX_NOACTIVATE
            Window.Position = new Point(-20000, -20000);
            SetWindowPos(handle, new IntPtr(1), -20000, -20000, 0, 0, 0x0001 | 0x0010); // bottom, no resize/activation
        }
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
        if (arguments.MaintenanceOperation is not null && arguments.WizardSmokeDirectory is null)
            _ = MainScene.Controller.InitializeMaintenanceWizardAsync();
        else if (arguments.SelfUpdateSmokeFeed is not null)
            _ = MainScene.Controller.RunSelfUpdateAcceptanceAsync();
        else if (arguments.WizardSmokeDirectory is not null)
            _ = WizardSmoke.RunAsync(this, arguments);
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

    internal Task<IReadOnlyList<NavigationFrame>> CaptureNavigationAsync(string id, string directory)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<NavigationFrame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            if (navigationCapture is not null) { completion.SetException(new InvalidOperationException("A navigation frame capture is already running.")); return; }
            Directory.CreateDirectory(directory);
            navigationCapture = new(directory, completion);
            Activate(id);
        });
        return completion.Task;
    }

    protected override void Update(GameTime gameTime)
    {
        while (actions.TryDequeue(out var action)) action();
        MainScene.Controller.ExpirePublisherSession();
        manager.Tick(gameTime);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Theme.Background);
        if (navigationCapture is { } capture) DrawNavigationFrame(gameTime, capture);
        else manager.Draw(gameTime);
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

    // During acceptance this is the single scene render presented to the window,
    // not a delayed screenshot or an extra draw that could hide an empty frame.
    private void DrawNavigationFrame(GameTime time, NavigationCapture capture)
    {
        var width = GraphicsDevice.Viewport.Width; var height = GraphicsDevice.Viewport.Height;
        capture.Target ??= new RenderTarget2D(GraphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
        GraphicsDevice.SetRenderTarget(capture.Target);
        GraphicsDevice.Clear(Theme.Background);
        manager.Draw(time);
        GraphicsDevice.SetRenderTarget(null);
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
        spriteBatch.Draw(capture.Target, new Microsoft.Xna.Framework.Rectangle(0, 0, width, height), Color.White);
        spriteBatch.End();
        var pixels = new Color[width * height];
        capture.Target.GetData(pixels);
        var railPixels = 0; var headerPixels = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var pixel = pixels[y * width + x];
                if (x < 220 && pixel == Theme.Rail) railPixels++;
                if (x >= 250 && y >= 20 && y < 115 && pixel.R >= 100 && pixel.G >= 100 && pixel.B >= 100) headerPixels++;
            }
        var frame = new NavigationFrame(drawCount + 1, MainScene.Controller.Page.ToString(), width, height, railPixels, headerPixels, IsFixedTimeStep);
        capture.Frames.Add(frame);
        using (var file = File.Create(Path.Combine(capture.Directory, $"{capture.Frames.Count:00}.png"))) capture.Target.SaveAsPng(file, width, height);
        if (capture.Frames.Count < 4) return;
        navigationCapture = null;
        capture.Target.Dispose();
        capture.Completion.SetResult(capture.Frames);
    }

    internal sealed record NavigationFrame(int DrawNumber, string Page, int Width, int Height, int RailPixels, int HeaderTextPixels, bool FixedTimeStep);
    private sealed class NavigationCapture(string directory, TaskCompletionSource<IReadOnlyList<NavigationFrame>> completion)
    {
        internal string Directory { get; } = directory;
        internal TaskCompletionSource<IReadOnlyList<NavigationFrame>> Completion { get; } = completion;
        internal List<NavigationFrame> Frames { get; } = [];
        internal RenderTarget2D? Target { get; set; }
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
        navigationCapture?.Target?.Dispose();
        navigationCapture?.Completion.TrySetCanceled();
        MainScene?.Controller.Dispose();
        MainScene?.Dispose();
        spriteBatch?.Dispose();
        base.UnloadContent();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
