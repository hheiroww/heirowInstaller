using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
using Button = ProjectZ.Shared.Drawing.UI.Input.Button;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;

namespace HeirowUpdater;

// A manual check owns one feedback child. The background watcher and application
// keep their lifetimes when the user dismisses its result.
internal sealed class NativeManualUpdateFeedback(UpdateCompanionConfiguration profile, string configurationPath) : IAsyncDisposable
{
    private FeedbackState? state;
    private string? statePath;
    private Process? child;
    private Task<string>? output, errors;
    internal bool HasShownError { get; private set; }
    internal bool WindowFailed { get; private set; }

    internal void ObserveStatus(ApplicationLifecycleResult result)
    {
        if (!result.ManualCheck) HasShownError = false;
    }

    internal async Task StatusChangedAsync(ApplicationLifecycleResult result, CancellationToken token)
    {
        if (!result.ManualCheck || result.ManualRequestId is not { } requestId) return;
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new InvalidDataException("The manual update request identity is invalid.");
        if (result.Status == "application-removed") { await SuspendAsync(); await ShowStartupFailureAsync(token, profile); return; }
        if (result.Status is "available" or "cancelled") { await SuspendAsync(); return; }
        if (result.Status is not ("checking" or "current" or "error" or "update-staged")) return;
        if (state?.RequestId != requestId)
        {
            await SuspendAsync();
            state = new() { RequestId = requestId, ProductId = profile.ProductId, InstallDirectory = profile.InstallDirectory,
                PublisherKeySha256 = profile.PublisherKeySha256, ProductName = profile.Name, CurrentVersion = profile.CurrentVersion };
            HasShownError = false;
        }
        state.Status = result.Status;
        state.Message = SafeMessage(result.Status);
        if (WindowFailed) throw new InvalidOperationException("The update result window could not be opened.");
        await OpenOrUpdateAsync(token);
        if (result.Status is "current" or "error" or "update-staged")
        {
            await child!.WaitForExitAsync(token);
            await FinishChildAsync(requireTerminal: true);
        }
    }

    internal async Task SuspendAsync()
    {
        if (child is null) return;
        if (!child.HasExited && statePath is not null && state is not null)
        {
            await UpdaterSchedule.WriteJsonAsync(statePath, state with { Status = "closed" }, CancellationToken.None);
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException)
            {
                // This is our own result window, never the application or host.
                try { child.CloseMainWindow(); } catch (InvalidOperationException) { }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        await FinishChildAsync();
    }

    internal async Task ResumeCheckingAsync(CancellationToken token)
    {
        if (state is null || state.Status != "checking") return;
        await OpenOrUpdateAsync(token);
    }

    private async Task OpenOrUpdateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (child?.HasExited == true) await FinishChildAsync();
        statePath ??= Path.Combine(ApplicationUpdater.StateDirectory(profile), "manual-feedback-" + state!.RequestId + ".json");
        await UpdaterSchedule.WriteJsonAsync(statePath, state!, token);
        if (child is not null) return;
        var executable = await Task.Run(() => UpdaterHostResolver.FindExecutable(), token) ?? throw new FileNotFoundException("The trusted native updater executable is missing.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "show-manual-feedback", "--config", Path.GetFullPath(configurationPath), "--prompt-request", statePath }) start.ArgumentList.Add(argument);
        try { child = Process.Start(start) ?? throw new InvalidOperationException("The update check window did not start."); }
        catch { WindowFailed = true; throw; }
        output = child.StandardOutput.ReadToEndAsync(); errors = child.StandardError.ReadToEndAsync();
        state!.FeedbackProcessId = child.Id;
        state.FeedbackStartTicks = child.StartTime.ToUniversalTime().Ticks;
        await UpdaterSchedule.WriteJsonAsync(statePath, state, token);
        // The feed check may begin only after the child has presented its first progress frame.
        var readyPath = statePath + ".ready";
        var wait = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            PackageSafety.EnsureNoReparsePoints(readyPath);
            try { if (File.Exists(readyPath) && await File.ReadAllTextAsync(readyPath, token) == state.RequestId) break; }
            catch (IOException) { } // The first-frame acknowledgement may still be being written.
            if (child.HasExited || wait.Elapsed > TimeSpan.FromSeconds(20)) { WindowFailed = true; throw new InvalidOperationException("The update progress window did not become ready."); }
            await Task.Delay(25, token);
        }
    }

    private async Task FinishChildAsync(bool requireTerminal = false)
    {
        if (child is not null)
        {
            if (output is not null) _ = await output;
            if (errors is not null) _ = await errors;
            var exitCode = child.ExitCode;
            child.Dispose(); child = null; output = errors = null;
            FeedbackAcknowledgement? acknowledgement = null;
            var acknowledgementPath = statePath + ".ack.json";
            if (File.Exists(acknowledgementPath))
            {
                PackageSafety.EnsureNoReparsePoints(acknowledgementPath);
                acknowledgement = JsonSerializer.Deserialize<FeedbackAcknowledgement>(await File.ReadAllTextAsync(acknowledgementPath), InstallerJson.Options);
                File.Delete(acknowledgementPath);
            }
            if (statePath is not null && File.Exists(statePath)) File.Delete(statePath);
            if (statePath is not null) File.Delete(statePath + ".ready");
            statePath = null;
            var terminalShown = acknowledgement is { FramesRendered: > 0 } && acknowledgement.RequestId == state?.RequestId &&
                acknowledgement.Status == state?.Status && !string.IsNullOrEmpty(acknowledgement.VisibleTitle);
            if (exitCode != 0 || requireTerminal && !terminalShown)
            {
                WindowFailed = true;
                throw new InvalidOperationException("The update check window could not display its result.");
            }
            if (requireTerminal && acknowledgement?.Status == "error") HasShownError = true;
        }
    }

    public async ValueTask DisposeAsync() => await SuspendAsync();

    internal static async Task ShowStartupFailureAsync(CancellationToken token, UpdateCompanionConfiguration? fixtureProfile = null)
    {
        var executable = UpdaterHostResolver.FindExecutable() ?? throw new FileNotFoundException("The trusted native updater executable is missing.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("show-startup-error");
        if (fixtureProfile?.Isolated == true && InstallerEngine.ListInstalled(fixtureProfile.StateRoot).Any(product => product.Isolated && product.Id == fixtureProfile.ProductId && product.Scope == fixtureProfile.Scope))
            foreach (var argument in new[] { "--state-root", fixtureProfile.StateRoot, "--product-id", fixtureProfile.ProductId, "--scope", fixtureProfile.Scope }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The update result window did not start.");
        await process.WaitForExitAsync(token);
    }

    internal static Task<int> RunStartupErrorAsync(UpdaterProgram.Arguments args, CancellationToken token)
    {
        InstalledProduct? fixture = null;
        if (args.Get("state-root") is { } stateRoot)
            fixture = InstallerEngine.ListInstalled(stateRoot).SingleOrDefault(product => product.Isolated && product.Id == args.Require("product-id") && product.Scope == args.Require("scope"))
                ?? throw new InvalidDataException("Automated startup feedback requires an installed isolated fixture.");
        string? Control(string name) => fixture is null ? null : Environment.GetEnvironmentVariable(name);
        return RunGameAsync(new()
        {
            ProductId = fixture?.Id ?? "", ProductName = fixture?.Name ?? "Application updates", CurrentVersion = fixture?.Version ?? "",
            Status = "error", StartupFailure = true,
            Message = "This application's update setup could not be opened. Repair its installation, then check for updates again."
        }, null, Control("HEIROWUPDATER_TEST_MANUAL_CHOICE"), Control("HEIROWUPDATER_TEST_MANUAL_CAPTURE"),
            Control("HEIROWUPDATER_TEST_MANUAL_REPORT"), null, Control("HEIROWUPDATER_TEST_MANUAL_EVENTS"), token);
    }

    internal static async Task<int> RunChildAsync(UpdaterProgram.Arguments args, CancellationToken token)
    {
        var configuration = ApplicationUpdater.ReadConfiguration(args.Require("config"), requireEntryPoint: false);
        var path = Path.GetFullPath(args.Require("prompt-request"));
        PackageSafety.EnsureNoReparsePoints(path);
        if (!Path.GetDirectoryName(path)!.Equals(ApplicationUpdater.StateDirectory(configuration), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manual feedback must belong to the application's update state.");
        var first = ReadState(path);
        if (!Guid.TryParseExact(first.RequestId, "N", out _) || first.ProductId != configuration.ProductId ||
            !first.InstallDirectory.Equals(configuration.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
            !PackageSafety.HashEquals(first.PublisherKeySha256, configuration.PublisherKeySha256))
            throw new InvalidDataException("Manual update feedback application identity mismatch.");
        if (first.Status == "closed") return 0;
        string? Fixture(string variable) => configuration.Isolated ? Environment.GetEnvironmentVariable(variable) : null;
        var choice = Fixture("HEIROWUPDATER_TEST_MANUAL_CHOICE");
        if (choice is not null && choice != "close") throw new ArgumentException("Manual feedback acceptance choice must be close.");
        RecordFixtureEvent(Fixture("HEIROWUPDATER_TEST_MANUAL_EVENTS"), "child-start", first);
        if (Fixture("HEIROWUPDATER_TEST_MANUAL_FAIL_CHILD") == "1") throw new InvalidDataException("Isolated feedback child initialization failure.");
        using var stop = new ApplicationStopMonitor(configuration, resetForOwner: false, token);
        FeedbackState Current()
        {
            try
            {
                var current = ReadState(path);
                return current.RequestId == first.RequestId ? current with { ProductName = configuration.Name, CurrentVersion = configuration.CurrentVersion } : first;
            }
            catch (IOException) { return first; }
            catch (JsonException) { return first; }
        }
        return await RunGameAsync(first with { ProductName = configuration.Name, CurrentVersion = configuration.CurrentVersion }, Current,
            choice, Fixture("HEIROWUPDATER_TEST_MANUAL_CAPTURE"), Fixture("HEIROWUPDATER_TEST_MANUAL_REPORT"), path + ".ack.json",
            Fixture("HEIROWUPDATER_TEST_MANUAL_EVENTS"), stop.Token);
    }

    internal static void TryActivateExisting(UpdateCompanionConfiguration profile, string? requestId)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParseExact(requestId, "N", out _)) return;
        try
        {
            var path = Path.Combine(ApplicationUpdater.StateDirectory(profile), "manual-feedback-" + requestId + ".json");
            if (!File.Exists(path)) return;
            var state = ReadState(path);
            if (state.RequestId != requestId || state.ProductId != profile.ProductId ||
                !state.InstallDirectory.Equals(profile.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
                !PackageSafety.HashEquals(state.PublisherKeySha256, profile.PublisherKeySha256)) return;
            using var process = Process.GetProcessById(state.FeedbackProcessId);
            using var current = Process.GetCurrentProcess();
            var image = process.MainModule?.FileName;
            if (process.StartTime.ToUniversalTime().Ticks != state.FeedbackStartTicks || process.SessionId != current.SessionId ||
                image is null || !Path.GetFullPath(image).Equals(UpdaterHostResolver.FindExecutable(), StringComparison.OrdinalIgnoreCase)) return;
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero) return;
            ShowWindowAsync(window, 9); // Restore normally; Windows retains its foreground-lock rules.
            SetForegroundWindow(window);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    private static void RecordFixtureEvent(string? path, string kind, FeedbackState state)
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.AppendAllText(path, JsonSerializer.Serialize(new { kind, requestId = state.RequestId, productId = state.ProductId,
            status = state.Status, processId = Environment.ProcessId }, InstallerJson.Options) + Environment.NewLine);
    }

    private static FeedbackState ReadState(string path)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Manual update feedback is too large.");
        var state = JsonSerializer.Deserialize<FeedbackState>(File.ReadAllText(path), InstallerJson.Options) ?? throw new InvalidDataException("Manual feedback is empty.");
        if (state.Status is not ("checking" or "current" or "error" or "closed" or "update-staged")) throw new InvalidDataException("Unknown manual update feedback state.");
        return state;
    }

    private static string SafeMessage(string status) => status switch
    {
        "checking" => "Checking the publisher's update feed. You can keep using your application.",
        "current" => "You have the latest available version. No changes were made to your application.",
        "update-staged" => "An update is already prepared. Continue in the update installer to finish or cancel it.",
        _ => "We couldn't finish checking for updates. Try again from the app. If the issue continues, contact the publisher."
    };

    private sealed record FeedbackState
    {
        public string RequestId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string InstallDirectory { get; set; } = "";
        public string PublisherKeySha256 { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string Status { get; set; } = "checking";
        public string Message { get; set; } = "";
        public bool StartupFailure { get; set; }
        public int FeedbackProcessId { get; set; }
        public long FeedbackStartTicks { get; set; }
    }

    private sealed class FeedbackAcknowledgement
    {
        public string RequestId { get; set; } = "";
        public string Status { get; set; } = "";
        public int FramesRendered { get; set; }
        public string VisibleTitle { get; set; } = "";
        public string CloseButtonText { get; set; } = "";
    }

    private static Task<int> RunGameAsync(FeedbackState first, Func<FeedbackState>? readState, string? choice,
        string? capture, string? report, string? acknowledgementPath, string? fixtureEvents, CancellationToken token)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                using var game = new FeedbackGame(first, readState, choice, capture, () =>
                {
                    RecordFixtureEvent(fixtureEvents, "renderer-start", first);
                    if (acknowledgementPath is not null)
                    {
                        var readyPath = acknowledgementPath[..^".ack.json".Length] + ".ready";
                        PackageSafety.EnsureNoReparsePoints(readyPath);
                        File.WriteAllText(readyPath, first.RequestId);
                    }
                });
                using var cancellation = token.Register(() => game.Enqueue(game.Exit));
                game.Run();
                var acknowledgement = new FeedbackAcknowledgement { RequestId = first.RequestId, Status = game.Status,
                    FramesRendered = game.RenderedFrames, VisibleTitle = game.VisibleTitle, CloseButtonText = game.CloseButtonText };
                if (acknowledgementPath is not null)
                {
                    PackageSafety.EnsureNoReparsePoints(acknowledgementPath);
                    File.WriteAllText(acknowledgementPath, JsonSerializer.Serialize(acknowledgement, InstallerJson.Options));
                }
                if (report is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                    File.WriteAllText(report, JsonSerializer.Serialize(new { framework = "Project-Z", status = game.Status,
                        requestId = first.RequestId, productId = first.ProductId, statusesRendered = game.StatusesRendered,
                        visibleTitle = acknowledgement.VisibleTitle, closeButtonText = acknowledgement.CloseButtonText,
                        renderedFrames = acknowledgement.FramesRendered, pointerEventsInjected = game.Injected,
                        antiAliasing = 16, anisotropicFiltering = 16 }, InstallerJson.Options));
                }
                completion.SetResult(0);
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = false, Name = "heirowUpdater manual feedback" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private sealed class FeedbackGame : Game
    {
        private readonly GraphicsDeviceManager graphics;
        private readonly ConcurrentQueue<Action> actions = new();
        private readonly Func<FeedbackState>? readState;
        private readonly string? choice, capture;
        private readonly Action rendered;
        private FeedbackState state;
        private SceneManager manager = null!;
        private FeedbackScene scene = null!;
        private SpriteBatch batch = null!;
        private int frames, statusFrames;
        private bool captured;
        private readonly CancellationTokenSource polling = new();
        private bool pollingStarted;
        private bool firstFrameAcknowledged;
        internal string Status => state.Status;
        internal int RenderedFrames => statusFrames;
        internal string VisibleTitle => scene.Title.Text;
        internal string CloseButtonText => scene.CloseButton.Text;
        internal bool Injected { get; private set; }
        internal HashSet<string> StatusesRendered { get; } = [];
        internal FeedbackGame(FeedbackState state, Func<FeedbackState>? readState, string? choice, string? capture, Action rendered)
        {
            this.state = state; this.readState = readState; this.choice = choice; this.capture = capture;
            this.rendered = rendered;
            Window.Title = state.ProductName + " | Check for updates | heirowUpdater";
            Window.AllowUserResizing = false; IsMouseVisible = true; IsFixedTimeStep = true; TargetElapsedTime = TimeSpan.FromSeconds(1d / 60);
            InactiveSleepTime = TimeSpan.FromMilliseconds(40); Content.RootDirectory = "Content";
            graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 780, PreferredBackBufferHeight = 510,
                PreferMultiSampling = true, SynchronizeWithVerticalRetrace = true, GraphicsProfile = GraphicsProfile.HiDef };
        }
        internal void Enqueue(Action action) => actions.Enqueue(action);
        protected override void LoadContent()
        {
            batch = new SpriteBatch(GraphicsDevice);
            var resources = new ContentContainer(Content, GraphicsDevice); resources.LoadVectorFontsOnly();
            Services.AddService(typeof(SpriteBatch), batch); Services.AddService(typeof(GraphicsDevice), GraphicsDevice); Services.AddService(typeof(ContentContainer), resources);
            var quality = GraphicsQuality.ForDevice(GraphicsDevice); quality.AntiAliasingSamples = 16; quality.Filtering = TextureFiltering.Anisotropic16x; quality.PpiPerfectText = true;
            manager = new SceneManager(this, 60) { MinimumViewportSize = new Point(780, 510), UseHardwareInput = true };
            Rebuild();
        }
        private void Rebuild()
        {
            if (scene is not null) { manager.RemoveScene("manual-feedback"); scene.Dispose(); }
            scene = new FeedbackScene(manager, this, state); manager.AddScene("manual-feedback", scene); manager.ActiveScene = scene;
        }
        protected override void Update(GameTime time)
        {
            if (!firstFrameAcknowledged && frames > 0) { firstFrameAcknowledged = true; rendered(); }
            while (actions.TryDequeue(out var action)) action();
            scene.Animate(time.TotalGameTime.TotalSeconds, state.Status == "checking");
            manager.Tick(time);
            if (choice == "close" && !Injected && (state.Status is "current" or "error" or "update-staged") && frames > 30 && (capture is null || captured))
            {
                Injected = true; var rectangle = scene.CloseButton.Rectangle;
                manager.InjectMouseMove(rectangle.Center.X, rectangle.Center.Y, 780, 510);
                manager.InjectMouseLeftDown(rectangle.Center.X, rectangle.Center.Y, 780, 510);
                manager.InjectMouseLeftUp(rectangle.Center.X, rectangle.Center.Y, 780, 510);
            }
            base.Update(time);
        }
        protected override void Draw(GameTime time)
        {
            GraphicsDevice.Clear(FeedbackScene.Background); manager.Draw(time); frames++; statusFrames++; StatusesRendered.Add(state.Status);
            if (!pollingStarted && readState is not null)
            {
                pollingStarted = true;
                _ = PollStateAsync(polling.Token);
            }
            if (!captured && capture is not null && frames > 20)
            {
                captured = true; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                using var target = new RenderTarget2D(GraphicsDevice, 780, 510, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
                GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(FeedbackScene.Background); manager.Draw(time); GraphicsDevice.SetRenderTarget(null);
                using var file = File.Create(capture); target.SaveAsPng(file, 780, 510);
            }
            base.Draw(time);
        }
        private async Task PollStateAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    var next = await Task.Run(readState!, token);
                    Enqueue(() =>
                    {
                        if (next.Status == "closed") { Exit(); return; }
                        if (next.Status != state.Status || next.Message != state.Message) { state = next; statusFrames = 0; captured = false; Rebuild(); }
                    });
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { Enqueue(Exit); }
        }
        protected override void UnloadContent() { polling.Cancel(); scene?.Dispose(); batch?.Dispose(); base.UnloadContent(); }
    }

    private sealed class FeedbackScene : Scene
    {
        internal static readonly Color Background = new(12, 17, 25);
        private static readonly Color Surface = new(20, 27, 38), Border = new(43, 54, 69), TextColor = new(238, 243, 248),
            Muted = new(150, 166, 184), Lime = new(187, 255, 123), Orange = new(255, 179, 102);
        private readonly RectangleElement root;
        internal Button CloseButton { get; }
        internal TextElement Title { get; }
        private RectangleElement? checkingBar;
        private TextElement? checkingElapsed;
        internal void Animate(double seconds, bool checking)
        {
            if (!checking || checkingBar is null) return;
            checkingBar.Position = new(45 + (float)((Math.Sin(seconds * 3) + 1) / 2) * 550, 363);
            checkingBar.Margin = new((int)checkingBar.Position.X, 363, 0, 0);
            if (checkingElapsed is not null) checkingElapsed.Text = $"{seconds:0}s elapsed";
        }
        internal FeedbackScene(SceneManager manager, FeedbackGame game, FeedbackState state) : base(manager)
        {
            BackgroundColor = Background; EnableDesignTimeControls = false; isCursorVisible = false;
            root = new RectangleElement(this) { Position = Vector2.Zero, Size = new(780, 510), BackgroundColor = Background, Padding = new Thickness(0) }; AddElement(root);
            var logo = Add(new ImageElement(this) { Position = new(26, 23), Size = new(46, 46), isMouseBypassEnabled = true });
            if (!NativeUpdatePrompt.TryLoadLogo(logo)) { Box(26, 23, 46, 46, Surface); Label("h", 38, 27, 33, 39, 22, Lime, true); }
            Label("heirowUpdater", 85, 27, 400, 27, 14, TextColor, true);
            Label("SOCKETJACK  ·  APPLICATION UPDATES", 86, 57, 590, 20, 8, Muted, true); Box(28, 91, 724, 1, Border);
            Title = Label(state.Status switch { "checking" => "Checking for updates…", "current" => "You're up to date.", "update-staged" => "An update is already prepared.", _ => "Updates need your attention." }, 30, 112, 720, 43, 25, TextColor, true);
            Label(state.ProductName, 32, 166, 710, 34, 17, state.Status == "error" ? Orange : Lime, true);
            Label(state.CurrentVersion.Length == 0 ? "Check for updates" : "Installed version " + state.CurrentVersion, 33, 207, 710, 27, 11, Muted);
            Box(30, 252, 720, 121, Surface);
            Add(new Textbox(this) { Position = new(45, 268), Size = new(690, 89), Font = Font(10.5f), ForegroundColor = TextColor,
                BackgroundColor = Surface, BorderThickness = 0, IsReadOnly = true, TextPadding = Vector2.Zero, AcceptsReturn = true,
                WrapText = true, Padding = new Thickness(0), Text = state.Message });
            if (state.Status == "checking")
            {
                checkingBar = Add(new RectangleElement(this) { Position = new(45, 363), Size = new(140, 3), BackgroundColor = Lime, Padding = new Thickness(0), isMouseBypassEnabled = true });
                checkingElapsed = Label("0s elapsed", 32, 442, 250, 26, 10, Muted);
            }
            Label(state.Status == "checking" ? "This window will show the result when the check finishes." : state.StartupFailure ? "Your application is unchanged." : state.Status == "update-staged" ? "Close this message to return to the update installer." : "Your app can stay open. We'll also check after it closes.", 32, 387, 710, 26, 9.5f, Muted);
            CloseButton = Add(new Button(this) { AutoSize = ButtonAutoSize.None, Text = state.Status == "checking" ? "Hide" : "Close", Position = new(514, 435), Size = new(235, 44), Font = Font(10.5f, true),
                ForegroundColor = Background, BackgroundColor = Lime, BorderThickness = 0, CornerRadius = 8,
                MouseOverBackgroundColor = Color.Lerp(Lime, Color.White, .12f), MouseDownBackgroundColor = Color.Lerp(Lime, Color.Black, .12f), Padding = new Thickness(12, 6, 12, 6), isAnimated = false });
            CloseButton.MouseLeftClick += _ => game.Exit();
        }
        private static string Font(float points, bool bold = false) => ContentContainer.SystemFontName("Segoe UI", bold ? 600 : 400, points);
        private T Add<T>(T element) where T : SceneElement { element.Margin = new Thickness((int)element.Position.X, (int)element.Position.Y, 0, 0); root.Children.Add(element); return element; }
        private void Box(int x, int y, int width, int height, Color color) => Add(new RectangleElement(this) { Position = new(x, y), Size = new(width, height), BackgroundColor = color, BorderColor = Border, BorderThickness = height == 1 ? 0 : 1, CornerRadius = height == 1 ? 0 : 9, Padding = new Thickness(0), isMouseBypassEnabled = true });
        private TextElement Label(string text, int x, int y, int width, int height, float points, Color color, bool bold = false) => Add(new TextElement(this) { Text = text, Position = new(x, y), Size = new(width, height), Font = Font(points, bold), ForegroundColor = color, Padding = new Thickness(0), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = width, PpiPerfectText = true, isMouseBypassEnabled = true });
    }
}
