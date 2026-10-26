using System.Diagnostics;
using HeirowInstaller.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ProjectZ.Shared.Drawing;
using ProjectZ.Shared.Drawing.UI;
using ProjectZ.Shared.Drawing.UI.Advanced;
using ProjectZ.Shared.Drawing.UI.Input;
using ProjectZ.Shared.Drawing.UI.Primitives;
using Button = ProjectZ.Shared.Drawing.UI.Input.Button;
using Textbox = ProjectZ.Shared.Drawing.UI.Input.Textbox;

namespace HeirowInstaller.App;

internal sealed partial class InstallerScene : Scene
{
    private readonly InstallerGame game;
    internal AppController Controller { get; }
    private readonly Dictionary<string, SceneElement> controls = [];
    private RectangleElement? root;
    private int lastWidth, lastHeight;
    private bool dirty;
    private int installedPage;
    private int activityPage;
    private bool confirmUninstall;
    private bool showLicense;
    private bool showWizardDetails;
    internal int ArtworkDecodeCount { get; private set; }
    internal int RebuildCount { get; private set; }
    private readonly Dictionary<string, Texture2D> artwork = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> usedArtwork = new(StringComparer.OrdinalIgnoreCase);
    private TextElement? statusTitle, statusDetail, progressPercent, progressDetail;
    private RectangleElement? progressBar;
    private bool activityDirty;
    private long activityRefresh;
    private int RailWidth => Controller.IsWizard ? 248 : 222;
    private int Width => game.GraphicsDevice.Viewport.Width;
    private int Height => game.GraphicsDevice.Viewport.Height;
    private int Left => RailWidth + 34;
    private int BodyWidth => Width - Left - 34;
    private int BodyBottom => Height - 103;

    internal InstallerScene(SceneManager manager, InstallerGame game, AppArguments arguments) : base(manager)
    {
        this.game = game;
        BackgroundColor = Theme.Background;
        EnableDesignTimeControls = false;
        isCursorVisible = false;
        Controller = new AppController(game, arguments);
        confirmUninstall = !Controller.IsMaintenanceWizard && arguments.MaintenanceOperation == "uninstall" && !arguments.MaintenanceConfirmed;
        Controller.Changed += () => dirty = true;
        Controller.ProgressChanged += RefreshProgress;
        ViewportResized += (_, _) => dirty = true;
    }

    internal SceneElement Control(string id) => controls.TryGetValue(id, out var control) ? control : throw new InvalidOperationException($"Control '{id}' is not visible.");
    internal bool HasControl(string id) => controls.ContainsKey(id);
    internal Task OpenPackageAsync(string path) => Controller.OpenPackageAsync(path);

    internal void RefreshLayoutIfNeeded()
    {
        if (dirty || lastWidth != Width || lastHeight != Height) Rebuild();
    }

    public override void Tick(GameTime gameTime)
    {
        if (activityDirty && Stopwatch.GetElapsedTime(activityRefresh).TotalMilliseconds >= 250)
        { activityDirty = false; activityRefresh = Stopwatch.GetTimestamp(); dirty = true; }
        // Input is dispatched by SceneManager immediately before this tick.
        // Register the replacement before base.Tick processes the scene queues;
        // drawing a tree queued after that point would produce an empty frame.
        RefreshLayoutIfNeeded();
        base.Tick(gameTime);
    }

    internal void Rebuild()
    {
        RebuildCount++;
        statusTitle = statusDetail = progressPercent = progressDetail = null;
        progressBar = null;
        usedArtwork.Clear();
        var previousRoot = root;
        var previousControls = controls.ToArray();
        controls.Clear();
        root = new RectangleElement(this) { Position = Vector2.Zero, Size = new Vector2(Width, Height), BackgroundColor = Theme.Background, Padding = new Thickness(0) };
        try
        {
            if (Controller.IsWizard) { BuildWizardRail(); BuildWizardHeader(); if (Controller.IsMaintenanceWizard) BuildMaintenanceWizard(); else BuildInstall(); }
            else
            {
                BuildRail(); BuildHeader();
                switch (Controller.Page)
                {
                    case DesktopPage.Install: BuildInstall(); break;
                    case DesktopPage.Installed: BuildInstalled(); break;
                    case DesktopPage.Workspace: BuildWorkspace(); break;
                    case DesktopPage.Updates: BuildUpdates(); break;
                    case DesktopPage.Activity: BuildActivity(); break;
                    case DesktopPage.Settings: BuildSettings(); break;
                }
            }
            BuildStatus();
            if (confirmUninstall) BuildUninstallConfirmation();
            if (showLicense) BuildLicense();
            if (showWizardDetails) BuildWizardDetails();
            if (Controller.RunningApplicationsDialog is not null) BuildRunningApplicationsDialog();
            AddElement(root);
            previousRoot?.Dispose();
            foreach (var path in artwork.Keys.Where(path => !usedArtwork.Contains(path)).ToArray())
            { artwork[path].Dispose(); artwork.Remove(path); }
            dirty = false; lastWidth = Width; lastHeight = Height;
        }
        catch
        {
            root.Dispose(); root = previousRoot;
            controls.Clear();
            foreach (var control in previousControls) controls.Add(control.Key, control.Value);
            throw;
        }
    }

    private T Add<T>(T element, string? id = null) where T : SceneElement
    {
        element.Margin = new Thickness((int)element.Position.X, (int)element.Position.Y, 0, 0);
        root!.Children.Add(element);
        if (id is not null) controls[id] = element;
        return element;
    }

    private RectangleElement Box(float x, float y, float width, float height, Color? color = null, float radius = 12, Color? border = null)
        => Add(new RectangleElement(this) { Position = new(x, y), Size = new(width, height), BackgroundColor = color ?? Theme.Surface, CornerRadius = radius, BorderColor = border ?? Theme.Border, BorderThickness = border == Color.Transparent ? 0 : 1, Padding = new Thickness(0), isMouseBypassEnabled = true });

    private TextElement Text(string text, float x, float y, float width, float height = 28, float points = 11, Color? color = null, bool bold = false, bool wrap = false)
        => Add(new TextElement(this) { Text = text, Position = new(x, y), Size = new(width, height), Font = Theme.Font(points, bold), ForegroundColor = color ?? Theme.Text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = width, Padding = new Thickness(0), isMouseBypassEnabled = true, PpiPerfectText = true });

    private Button Action(string id, string label, float x, float y, float width, Action click, bool primary = false, bool enabled = true, Color? accent = null)
    {
        var tone = accent ?? Theme.Lime;
        var button = Add(new Button(this)
        {
            AutoSize = ButtonAutoSize.None, Text = label, Position = new(x, y), Size = new(width, 42),
            Font = Theme.Font(10.5f, true), ForegroundColor = !enabled ? Theme.Muted : primary ? Theme.Background : tone,
            BackgroundColor = primary && enabled ? tone : Theme.SurfaceRaised,
            MouseOverBackgroundColor = primary ? Color.Lerp(tone, Color.White, 0.12f) : new Color(40, 54, 67),
            MouseDownBackgroundColor = primary ? Color.Lerp(tone, Color.Black, 0.13f) : new Color(45, 65, 72),
            CornerRadius = 8, BorderColor = primary ? Color.Transparent : Theme.Border, BorderThickness = primary ? 0 : 1,
            isEnabled = enabled && (Controller.RunningApplicationsDialog is null || id.StartsWith("running.", StringComparison.Ordinal)), Padding = new Thickness(12, 6, 12, 6), isAnimated = false
        }, id);
        button.MouseLeftClick += _ => click();
        return button;
    }

    private Textbox Field(string id, string value, float x, float y, float width, Action<string> changed, bool multiline = false, float height = 42)
    {
        var field = Add(new Textbox(this)
        {
            Position = new(x, y), Size = new(width, height), Font = Theme.Font(10.5f), ForegroundColor = Theme.Text,
            BackgroundColor = Theme.Field, BorderColor = Theme.Border, BorderThickness = 1, CornerRadius = 8,
            TextPadding = new Vector2(12, multiline ? 10 : 9), AcceptsReturn = multiline, WrapText = multiline,
            Padding = new Thickness(0), Text = value, isEnabled = !Controller.Busy && Controller.RunningApplicationsDialog is null
        }, id);
        field.OnTextChanged += change => changed(change.NewText);
        return field;
    }

    private void Pill(string label, float x, float y, float width, Color? tone = null)
    {
        Box(x, y, width, 28, new Color(27, 40, 37), 14, Color.Transparent);
        Text(label, x + 12, y + 5, width - 24, 20, 9, tone ?? Theme.Lime, true);
    }

    private void ReadOnlyField(Textbox field)
    {
        field.IsReadOnly = true;
        field.isEnabled = Controller.RunningApplicationsDialog is null;
    }

    private void Logo(float x, float y, int size)
    {
        var path = Controller.IsWizard ? Controller.WizardArtworkPath : Path.Combine(AppContext.BaseDirectory, "Assets", "heirowInstaller.png");
        if (path is not null && File.Exists(path))
        {
            usedArtwork.Add(path);
            var image = Add(new ImageElement(this) { Position = new(x, y), Size = new(size, size), isMouseBypassEnabled = true });
            if (!artwork.TryGetValue(path, out var texture))
            {
                using var file = File.OpenRead(path);
                texture = Texture2D.FromStream(game.GraphicsDevice, file);
                artwork.Add(path, texture);
                ArtworkDecodeCount++;
            }
            image.Texture = texture; // Borrow the scene-owned texture across layout changes.
        }
        else
        {
            Box(x, y, size, size, Theme.GreenSurface, 10, Color.Transparent);
            var letter = Controller.Manifest?.Name.FirstOrDefault(char.IsLetterOrDigit) ?? '\0';
            var initial = Controller.IsWizard ? letter == '\0' ? "↓" : letter.ToString().ToUpperInvariant() : "h";
            Text(initial, x + size * 0.22f, y + size * 0.01f, size, size, size * 0.5f, Theme.Lime, true);
        }
    }

    private void BuildRail()
    {
        Box(0, 0, RailWidth, Height, Theme.Rail, 0, Color.Transparent);
        Box(RailWidth - 1, 0, 1, Height, Theme.Border, 0, Color.Transparent);
        Logo(21, 23, 45);
        Text("heirowInstaller", 77, 27, 138, 25, 13, bold: true);
        Text("SOCKETJACK", 78, 53, 130, 18, 7.5f, Theme.Muted, true);
        Text("RELEASE CENTER", 24, 113, 180, 22, 8, Theme.Muted, true);
        Nav(DesktopPage.Install, "Install a package", "↓", 152, "nav.install");
        Nav(DesktopPage.Installed, "Installed apps", "▦", 204, "nav.installed");
        Nav(DesktopPage.Workspace, "Build & publish", "◇", 256, "nav.workspace");
        Nav(DesktopPage.Updates, "Updates", "↻", 308, "nav.updates");
        Nav(DesktopPage.Activity, "Activity", "≡", 360, "nav.activity");
        Nav(DesktopPage.Settings, "Settings", "⚙", 412, "nav.settings");
        Box(20, Height - 210, RailWidth - 40, 113, Theme.Surface, 10);
        Text("Native. Modular. Yours.", 34, Height - 194, 160, 26, 10, bold: true);
        Text("Packages, updates and publishing\nin one focused workspace.", 34, Height - 162, 154, 52, 9, Theme.Muted, wrap: true);
        Text("PROJECT-Z", 24, Height - 62, 140, 20, 8, Theme.Lime, true);
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "2026.10.24.1";
        Text("Native desktop · " + version, 24, Height - 40, 190, 22, 8.5f, Theme.Muted);
    }

    private void Nav(DesktopPage page, string label, string icon, float y, string id)
    {
        var selected = Controller.Page == page;
        var button = Action(id, "", 16, y, RailWidth - 32, () => { confirmUninstall = false; Controller.Navigate(page); });
        button.BackgroundColor = selected ? new Color(32, 47, 40) : Theme.Rail;
        button.BorderThickness = 0;
        if (selected) Box(16, y + 9, 3, 24, Theme.Lime, 1, Color.Transparent);
        Text(icon, 31, y + 10, 28, 30, 14, selected ? Theme.Lime : Theme.Muted);
        Text(label, 66, y + 12, 137, 24, 10, selected ? Theme.Lime : Theme.Muted, selected);
    }

    private void BuildHeader()
    {
        var title = Controller.Page switch { DesktopPage.Install => "Install a package", DesktopPage.Installed => "Installed applications", DesktopPage.Workspace => "Build & publish", DesktopPage.Updates => "Application updates", DesktopPage.Activity => "Activity", _ => "Desktop settings" };
        var subtitle = Controller.Page switch
        {
            DesktopPage.Install => "A clear path from download to desktop.",
            DesktopPage.Installed => "Maintain every application installed with heirowInstaller.",
            DesktopPage.Workspace => "Turn your application into a verified, updateable release.",
            DesktopPage.Updates => "Keep published applications current with heirowUpdater.",
            DesktopPage.Activity => "Your operations, verification and installation history.",
            _ => "Make this release workspace your own."
        };
        Text(title, Left, 28, BodyWidth - 155, 43, 23, bold: true);
        Text(subtitle, Left, 81, BodyWidth - 30, 26, 10.5f, Theme.Muted);
        Pill("●  NATIVE DESKTOP", Width - 197, 33, 163);
        Box(Left, 120, BodyWidth, 1, Theme.Border, 0, Color.Transparent);
    }

    private void BuildStatus()
    {
        var y = Height - 82;
        Box(Left, y, BodyWidth, 59, Theme.Surface, 10, Controller.Failed ? Theme.Danger : Theme.Border);
        Box(Left + 15, y + 17, 7, 7, Controller.Failed ? Theme.Danger : Controller.Busy ? Theme.Orange : Theme.Lime, 4, Color.Transparent);
        statusTitle = Text(Controller.Status, Left + 33, y + 8, BodyWidth - 50, 22, 10, Controller.Failed ? Theme.Danger : Theme.Text, true);
        statusDetail = Text(Controller.StatusDetail, Left + 33, y + 33, BodyWidth - 50, 20, 8.5f, Theme.Muted);
    }

    private void RefreshProgress()
    {
        static void Set(TextElement? element, string value) { if (element is not null && element.Text != value) element.Text = value; }
        Set(statusTitle, Controller.Status); Set(statusDetail, Controller.StatusDetail);
        Set(progressPercent, $"{Controller.Progress:P0}"); Set(progressDetail, Controller.StatusDetail);
        if (progressBar is not null) progressBar.Size = new(Math.Max(8, (BodyWidth - 48) * (float)Controller.Progress), 8);
        if (Controller.Page == DesktopPage.Activity || showWizardDetails) activityDirty = true;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { foreach (var texture in artwork.Values) texture.Dispose(); artwork.Clear(); }
    }

    private void BrowsePackage()
    {
        using var picker = new System.Windows.Forms.OpenFileDialog { Title = "Open a heirowInstaller package", Filter = "heirowInstaller packages|*.hinst;*.exe|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) _ = Controller.OpenPackageAsync(picker.FileName);
    }

    private static string SizeLabel(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : $"{Math.Max(bytes, 0) / 1024d:0.0} KB";
    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(path) }, UseShellExecute = true });
    }
}
