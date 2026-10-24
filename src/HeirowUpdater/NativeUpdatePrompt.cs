using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ProjectZ.Shared.Content;
using ProjectZ.Shared.Drawing;
using ProjectZ.Shared.Drawing.UI;
using ProjectZ.Shared.Drawing.UI.Advanced;
using ProjectZ.Shared.Drawing.UI.Input;
using ProjectZ.Shared.Drawing.UI.Primitives;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Button = ProjectZ.Shared.Drawing.UI.Input.Button;

namespace HeirowUpdater;

internal static class NativeUpdatePrompt
{
    internal static Task<ApplicationUpdateChoice> ShowAsync(ApplicationUpdatePrompt context, string? acceptanceChoice, string? capturePath,
        string? reportPath, CancellationToken cancellationToken, string configurationPath, bool authentication = false, bool failure = false)
        => ShowInChildAsync(new() { Context = context, AcceptanceChoice = acceptanceChoice, CapturePath = capturePath, ReportPath = reportPath,
            ConfigurationPath = Path.GetFullPath(configurationPath), Authentication = authentication, Failure = failure }, cancellationToken);

    // Each prompt owns one native renderer lifetime. The lifecycle owner keeps
    // waiting between prompts, while native window teardown stays in this child.
    private static async Task<ApplicationUpdateChoice> ShowInChildAsync(PromptRequest request, CancellationToken token)
    {
        var directory = ApplicationUpdater.StateDirectory(request.Context.Application);
        var requestPath = Path.Combine(directory, "prompt-" + Guid.NewGuid().ToString("N") + ".json");
        var resultPath = requestPath + ".result.json";
        try
        {
            await UpdaterSchedule.WriteJsonAsync(requestPath, request, token);
            var executable = UpdaterHostResolver.FindExecutable() ?? throw new FileNotFoundException("The trusted native updater executable is missing.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("show-prompt"); start.ArgumentList.Add("--config");
            start.ArgumentList.Add(request.ConfigurationPath); start.ArgumentList.Add("--prompt-request"); start.ArgumentList.Add(requestPath);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("The native update prompt did not start.");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync(token);
            _ = await output;
            var errorText = await error;
            token.ThrowIfCancellationRequested();
            if (child.ExitCode != 0 || !File.Exists(resultPath)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText) ? "The native update prompt did not return a choice." : errorText.Trim());
            return JsonSerializer.Deserialize<PromptResult>(await File.ReadAllTextAsync(resultPath, token), InstallerJson.Options)?.Choice
                ?? throw new InvalidDataException("The native update prompt returned an empty choice.");
        }
        finally
        {
            if (File.Exists(requestPath)) File.Delete(requestPath);
            if (File.Exists(resultPath)) File.Delete(resultPath);
        }
    }

    internal static async Task<int> RunChildAsync(UpdaterProgram.Arguments args, CancellationToken token)
    {
        var configuration = ApplicationUpdater.ReadConfiguration(args.Require("config"), requireEntryPoint: false);
        var requestPath = Path.GetFullPath(args.Require("prompt-request"));
        PackageSafety.EnsureNoReparsePoints(requestPath);
        if (!Path.GetDirectoryName(requestPath)!.Equals(ApplicationUpdater.StateDirectory(configuration), StringComparison.OrdinalIgnoreCase) || new FileInfo(requestPath).Length > 1024 * 1024)
            throw new InvalidDataException("Native prompt requests must belong to this application's update state.");
        var request = JsonSerializer.Deserialize<PromptRequest>(await File.ReadAllTextAsync(requestPath, token), InstallerJson.Options) ?? throw new InvalidDataException("Native prompt request is empty.");
        if (request.Context.Application.ProductId != configuration.ProductId || !request.Context.Application.InstallDirectory.Equals(configuration.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
            !PackageSafety.HashEquals(request.Context.Application.PublisherKeySha256, configuration.PublisherKeySha256)) throw new InvalidDataException("Native prompt application identity mismatch.");
        if (!configuration.Isolated && (request.AcceptanceChoice is not null || request.CapturePath is not null || request.ReportPath is not null))
            throw new InvalidDataException("Automated native prompt controls require an isolated installation.");
        using var maintenance = new ApplicationStopMonitor(configuration.InstallDirectory, resetForOwner: false, token);
        var context = request.Context with { Application = configuration };
        var choice = await RunInProcessAsync(context, request.AcceptanceChoice, request.CapturePath, request.ReportPath, maintenance.Token, request.Authentication, request.Failure);
        await UpdaterSchedule.WriteJsonAsync(requestPath + ".result.json", new PromptResult { Choice = choice }, CancellationToken.None);
        return 0;
    }

    private static Task<ApplicationUpdateChoice> RunInProcessAsync(ApplicationUpdatePrompt context, string? acceptanceChoice, string? capturePath,
        string? reportPath, CancellationToken cancellationToken, bool authentication, bool failure)
    {
        if (acceptanceChoice is not null && acceptanceChoice is not ("postpone" or "update")) throw new ArgumentException("Prompt acceptance choice must be postpone or update.");
        var completion = new TaskCompletionSource<ApplicationUpdateChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                using var game = new PromptGame(context, acceptanceChoice, capturePath, authentication, failure);
                using var registration = cancellationToken.Register(() => game.Enqueue(game.Exit));
                game.Run();
                if (reportPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(new { framework = "Project-Z", choice = game.Choice.ToString(), applicationRunning = context.ApplicationRunning,
                        antiAliasing = 16, anisotropicFiltering = 16, pointerEventsInjected = game.Injected, productId = context.Application.ProductId }, HeirowInstaller.Core.InstallerJson.Options));
                }
                cancellationToken.ThrowIfCancellationRequested();
                completion.SetResult(game.Choice);
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = false, Name = "heirowUpdater native prompt" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private sealed class PromptRequest
    {
        public ApplicationUpdatePrompt Context { get; set; } = null!;
        public string ConfigurationPath { get; set; } = "";
        public string? AcceptanceChoice { get; set; }
        public string? CapturePath { get; set; }
        public string? ReportPath { get; set; }
        public bool Authentication { get; set; }
        public bool Failure { get; set; }
    }
    private sealed class PromptResult { public ApplicationUpdateChoice Choice { get; set; } }
    internal static async Task<bool> SignInAsync(HeirowInstaller.Core.UpdateCompanionConfiguration profile, string configurationPath, CancellationToken token)
    {
        var context = new ApplicationUpdatePrompt(profile, new() { CurrentVersion = profile.CurrentVersion, Feed = new() { Version = profile.CurrentVersion,
            ReleaseNotes = "Approve sign-in in your browser to access this application's updates. Your session stays in memory; no account credentials are saved in the app's update profile." } }, true);
        return await ShowAsync(context, null, null, null, token, configurationPath, authentication: true) == ApplicationUpdateChoice.UpdateNow;
    }
    internal static Task<ApplicationUpdateChoice> ShowFailureAsync(HeirowInstaller.Core.UpdateCompanionConfiguration profile, string configurationPath, Exception error, CancellationToken token)
    {
        var message = error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
            ? "Your SocketJack account does not currently have access to this application's updates. Check the publisher's access requirements, then try again from the app."
            : error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                ? "SocketJack could not authorize this update request. Sign in from the app and check for updates again."
                : error.Message;
        var context = new ApplicationUpdatePrompt(profile, new() { CurrentVersion = profile.CurrentVersion, Feed = new() { Version = profile.CurrentVersion, ReleaseNotes = message } }, true);
        return ShowAsync(context, null, null, null, token, configurationPath, failure: true);
    }

    private sealed class PromptGame : Game
    {
        private readonly GraphicsDeviceManager graphics;
        private readonly ConcurrentQueue<Action> actions = new();
        private readonly ApplicationUpdatePrompt context;
        private readonly string? acceptanceChoice, capturePath;
        private readonly bool authentication, failure;
        private SceneManager manager = null!;
        private PromptScene scene = null!;
        private SpriteBatch batch = null!;
        private int frames;
        private bool captured;
        internal bool Injected { get; private set; }
        internal ApplicationUpdateChoice Choice { get; private set; } = ApplicationUpdateChoice.Postpone;

        internal PromptGame(ApplicationUpdatePrompt context, string? acceptanceChoice, string? capturePath, bool authentication, bool failure)
        {
            this.context = context; this.acceptanceChoice = acceptanceChoice; this.capturePath = capturePath;
            this.authentication = authentication; this.failure = failure;
            Window.Title = context.Application.Name + " update | heirowUpdater";
            Window.AllowUserResizing = false; IsMouseVisible = true; IsFixedTimeStep = true; TargetElapsedTime = TimeSpan.FromSeconds(1d / 60);
            InactiveSleepTime = TimeSpan.FromMilliseconds(40); Content.RootDirectory = "Content";
            graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 780, PreferredBackBufferHeight = 510, PreferMultiSampling = true, SynchronizeWithVerticalRetrace = true, GraphicsProfile = GraphicsProfile.HiDef };
        }
        internal void Enqueue(Action action) => actions.Enqueue(action);
        internal void Choose(ApplicationUpdateChoice choice) { Choice = choice; Exit(); }
        protected override void LoadContent()
        {
            batch = new SpriteBatch(GraphicsDevice);
            var resources = new ContentContainer(Content, GraphicsDevice); resources.LoadVectorFontsOnly();
            Services.AddService(typeof(SpriteBatch), batch); Services.AddService(typeof(GraphicsDevice), GraphicsDevice); Services.AddService(typeof(ContentContainer), resources);
            var quality = GraphicsQuality.ForDevice(GraphicsDevice); quality.AntiAliasingSamples = 16; quality.Filtering = TextureFiltering.Anisotropic16x; quality.PpiPerfectText = true;
            manager = new SceneManager(this, 60) { MinimumViewportSize = new Point(780, 510), UseHardwareInput = true };
            scene = new PromptScene(manager, this, context, authentication, failure); manager.AddScene("update", scene); manager.ActiveScene = scene;
        }
        protected override void Update(GameTime time)
        {
            while (actions.TryDequeue(out var action)) action();
            manager.Tick(time);
            if (frames > 20 && frames % 20 == 0 && !context.ApplicationRunning && ApplicationProcesses.AnyRunning(Path.Combine(context.Application.InstallDirectory, context.Application.EntryPoint), context.HostExecutable, ApplicationHostRegistry.Paths(context.Application)))
                Choose(ApplicationUpdateChoice.ApplicationReopened);
            if (acceptanceChoice is not null && !Injected && frames > 30 && (capturePath is null || captured))
            {
                Injected = true; var rectangle = scene.Button(acceptanceChoice).Rectangle; var x = rectangle.Center.X; var y = rectangle.Center.Y;
                manager.InjectMouseMove(x, y, 780, 510); manager.InjectMouseLeftDown(x, y, 780, 510); manager.InjectMouseLeftUp(x, y, 780, 510);
            }
            base.Update(time);
        }
        protected override void Draw(GameTime time)
        {
            GraphicsDevice.Clear(PromptScene.Background); manager.Draw(time); frames++;
            if (!captured && capturePath is not null && frames > 20)
            {
                captured = true; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
                using var target = new RenderTarget2D(GraphicsDevice, 780, 510, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
                GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(PromptScene.Background); manager.Draw(time); GraphicsDevice.SetRenderTarget(null);
                using var file = File.Create(capturePath); target.SaveAsPng(file, 780, 510);
            }
            base.Draw(time);
        }
        protected override void UnloadContent() { scene?.Dispose(); batch?.Dispose(); base.UnloadContent(); }
    }

    private sealed class PromptScene : Scene
    {
        internal static readonly Color Background = new(12, 17, 25);
        private static readonly Color Surface = new(20, 27, 38), Border = new(43, 54, 69), TextColor = new(238, 243, 248), Muted = new(150, 166, 184), Lime = new(187, 255, 123), Orange = new(255, 179, 102);
        private readonly RectangleElement root;
        private readonly Dictionary<string, Button> buttons = [];
        internal Button Button(string key) => buttons[key];
        internal PromptScene(SceneManager manager, PromptGame game, ApplicationUpdatePrompt context, bool authentication, bool failure) : base(manager)
        {
            BackgroundColor = Background; EnableDesignTimeControls = false; isCursorVisible = false;
            root = new RectangleElement(this) { Position = Vector2.Zero, Size = new(780, 510), BackgroundColor = Background, Padding = new Thickness(0) }; AddElement(root);
            var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "heirowInstaller.png");
            if (File.Exists(logo)) { var image = Add(new ImageElement(this) { Position = new(26, 23), Size = new(46, 46), isMouseBypassEnabled = true }); image.Load(logo); }
            Label("heirowUpdater", 85, 27, 400, 27, 14, TextColor, true);
            Label("SOCKETJACK  ·  APPLICATION UPDATES", 86, 57, 590, 20, 8, Muted, true);
            Box(28, 91, 724, 1, Border);
            Label(failure ? "Updates need your attention." : authentication ? "Sign in to get updates." : "A new release is ready.", 30, 112, 710, 43, 25, TextColor, true);
            Label(context.Application.Name, 32, 166, 710, 34, 17, Lime, true);
            Label(failure ? "Your installed application is unchanged." : authentication ? "Your SocketJack session needs approval." : context.Application.CurrentVersion + "   →   " + context.Update.Feed.Version, 33, 207, 710, 27, 11, Muted);
            Box(30, 252, 720, 121, Surface);
            var notes = Add(new Textbox(this) { Position = new(45, 268), Size = new(690, 89), Font = Font(10.5f), ForegroundColor = TextColor,
                BackgroundColor = Surface, BorderThickness = 0, IsReadOnly = true, TextPadding = Vector2.Zero, AcceptsReturn = true, WrapText = true,
                Text = string.IsNullOrWhiteSpace(context.Update.Feed.ReleaseNotes) ? "Your publisher has released an update for this application." : context.Update.Feed.ReleaseNotes, Padding = new Thickness(0) });
            Label(failure ? "You can check again from the app when you are ready." : authentication ? "Publisher access rules still apply after sign-in." : context.ApplicationRunning ? "Installation will wait until every app window has closed." : "The app is closed. Your download is verified before installation.", 32, 387, 710, 26, 9.5f, Muted);
            if (!failure) Action("postpone", authentication ? "Later" : "Postpone 24 hours", 31, 435, 218, Muted, false, () => game.Choose(ApplicationUpdateChoice.Postpone));
            Action("update", failure ? "Close" : authentication ? "Sign in  ↗" : "Update now  →", 514, 435, 235, failure ? Orange : Lime, true, () => game.Choose(ApplicationUpdateChoice.UpdateNow));
        }
        private static string Font(float points, bool bold = false) => ContentContainer.SystemFontName("Segoe UI", bold ? 600 : 400, points);
        private T Add<T>(T element) where T : SceneElement { element.Margin = new Thickness((int)element.Position.X, (int)element.Position.Y, 0, 0); root.Children.Add(element); return element; }
        private void Box(int x, int y, int width, int height, Color color) => Add(new RectangleElement(this) { Position = new(x, y), Size = new(width, height), BackgroundColor = color, BorderColor = Border, BorderThickness = height == 1 ? 0 : 1, CornerRadius = height == 1 ? 0 : 9, Padding = new Thickness(0), isMouseBypassEnabled = true });
        private void Label(string text, int x, int y, int width, int height, float points, Color color, bool bold = false) => Add(new TextElement(this) { Text = text, Position = new(x, y), Size = new(width, height), Font = Font(points, bold), ForegroundColor = color, Padding = new Thickness(0), TextTrimming = TextTrimming.CharacterEllipsis, PpiPerfectText = true, isMouseBypassEnabled = true });
        private void Action(string key, string text, int x, int y, int width, Color accent, bool primary, Action click)
        {
            var button = Add(new Button(this) { AutoSize = ButtonAutoSize.None, Text = text, Position = new(x, y), Size = new(width, 44), Font = Font(10.5f, true),
                ForegroundColor = primary ? Background : accent, BackgroundColor = primary ? accent : Surface, BorderColor = primary ? Color.Transparent : Border, BorderThickness = primary ? 0 : 1,
                CornerRadius = 8, MouseOverBackgroundColor = primary ? Color.Lerp(accent, Color.White, .12f) : new Color(40, 54, 67), MouseDownBackgroundColor = primary ? Color.Lerp(accent, Color.Black, .12f) : Border,
                Padding = new Thickness(12, 6, 12, 6), isAnimated = false });
            button.MouseLeftClick += _ => click(); buttons[key] = button;
        }
    }
}
