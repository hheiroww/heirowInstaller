using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;
using HeirowUpdater.SocketJack;
using System.Text.Json;

namespace HeirowInstaller.App;

internal enum DesktopPage { Install, Installed, Workspace, Updates, Activity, Settings }
internal enum SetupStage { Package, Features, Destination, Review, Progress, Complete }
internal sealed record ActivityEntry(DateTimeOffset Time, string Title, string Detail, bool Error = false);

internal sealed class AppController : IDisposable
{
    private readonly InstallerGame game;
    private readonly AppArguments arguments;
    private readonly InstallerEngine engine = new();
    private PublisherSession? publisherSession;
    internal bool SigningIn { get; private set; }
    internal bool PublisherSignedIn => publisherSession is { } session && session.ExpiresUtc > DateTimeOffset.UtcNow;
    internal string PublisherAccount => PublisherSignedIn ? $"Signed in as {publisherSession!.Username} · expires {publisherSession.ExpiresUtc.ToLocalTime():HH:mm}" : "Sign in to SocketJack to publish, or use publisher credentials from your environment.";
    internal DesktopSettings Settings { get; }
    internal DesktopPage Page { get; set; } = DesktopPage.Install;
    internal SetupStage Stage { get; set; }
    internal PackageManifest? Manifest { get; private set; }
    internal InstallPlan? Plan { get; private set; }
    internal string PackagePath { get; private set; } = "";
    internal string TargetDirectory { get; set; } = "";
    internal bool OriginalInstallerDestination => Manifest is { } manifest && (manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null) && string.IsNullOrWhiteSpace(manifest.InstallDirectory) && arguments.TargetDirectory is null;
    internal string DisplayInstallDirectory => OriginalInstallerDestination ? "Original installer default" : TargetDirectory;
    internal string Scope { get; set; } = "user";
    internal HashSet<string> Features { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<InstalledProduct> Installed { get; private set; } = [];
    internal InstalledProduct? SelectedProduct { get; set; }
    internal UpdateCheckResult? Update { get; private set; }
    internal List<ActivityEntry> Activity { get; } = [];
    internal bool Busy { get; private set; }
    internal bool Failed { get; private set; }
    internal string Status { get; private set; } = "Ready when you are";
    internal string StatusDetail { get; private set; } = "Open a package to install, or build one in the workspace.";
    internal double Progress { get; private set; }
    internal string ManifestPath { get; set; } = "";
    internal string SourceDirectory { get; set; } = "";
    internal string OutputPath { get; set; } = "";
    internal string ReleaseNotes { get; set; } = "";
    internal string? LicenseText { get; private set; }
    internal bool LicenseAccepted { get; set; }
    internal string? BuiltPackagePath { get; private set; }
    internal string? LastReceiptPath { get; private set; }
    internal string UpdaterOutput { get; private set; } = "Check your installed applications to see published update information here.";
    internal string UpdaterPath
    {
        get
        {
            var installed = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "heirowUpdater.exe");
            return File.Exists(installed) ? installed : Path.Combine(AppContext.BaseDirectory, "heirowUpdater.exe");
        }
    }
    internal CancellationTokenSource? Cancellation { get; private set; }
    internal event Action? Changed;

    internal AppController(InstallerGame game, AppArguments arguments)
    {
        this.game = game;
        this.arguments = arguments;
        Settings = DesktopSettings.Load();
        OutputPath = Path.Combine(Settings.DefaultOutputDirectory, "application-Setup.exe");
        RefreshInstalled();
        AddActivity("Desktop ready", "Project-Z native graphics · 16× AA · 16× AF");
    }

    internal void Notify() => Changed?.Invoke();
    internal void ExpirePublisherSession()
    {
        if (publisherSession is { } session && session.ExpiresUtc <= DateTimeOffset.UtcNow) { session.Dispose(); publisherSession = null; Notify(); }
    }

    internal void Navigate(DesktopPage page)
    {
        if (Busy && page == DesktopPage.Install && Stage == SetupStage.Progress) { Page = page; Notify(); return; }
        Page = page;
        if (page == DesktopPage.Installed) RefreshInstalled();
        Notify();
    }

    internal async Task OpenPackageAsync(string path)
    {
        if (Busy) return;
        Begin("Verifying package", Path.GetFileName(path));
        try
        {
            var loaded = await Task.Run(async () =>
            {
                using var package = PackageReader.Open(path);
                arguments.ValidatePackageIdentity(package);
                await package.VerifyAsync(cancellationToken: Cancellation!.Token);
                string? license = null;
                if (!string.IsNullOrWhiteSpace(package.Manifest.LicenseFile))
                {
                    var payload = package.Manifest.Payload.FirstOrDefault(file => file.Path.Equals(package.Manifest.LicenseFile, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("The package license is missing from the verified payload.");
                    if (payload.Size > 512 * 1024) throw new InvalidDataException("The package license exceeds the desktop text display limit.");
                    var temporary = Path.Combine(Path.GetTempPath(), "heirowInstaller-license-" + Guid.NewGuid().ToString("N"));
                    await package.ExtractVerifiedAsync(temporary, [payload], cancellationToken: Cancellation.Token);
                    var licensePath = Path.GetFullPath(Path.Combine(temporary, payload.Path));
                    if (!licensePath.StartsWith(Path.GetFullPath(temporary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The license path is outside the temporary package directory.");
                    license = await File.ReadAllTextAsync(licensePath, Cancellation.Token);
                    File.Delete(licensePath);
                }
                return (Manifest: package.Manifest, License: license);
            });
            await game.OnUiAsync(() =>
            {
                var metadata = loaded.Manifest;
                PackagePath = Path.GetFullPath(path);
                Manifest = metadata;
                LicenseText = loaded.License;
                LicenseAccepted = arguments.AcceptLicense;
                Features.Clear();
                foreach (var feature in metadata.Features.Where(f => f.Required || f.Default)) Features.Add(feature.Id);
                Scope = arguments.Scope ?? metadata.Scope;
                TargetDirectory = arguments.TargetDirectory ?? "";
                if (arguments.Features is not null)
                {
                    Features.Clear();
                    foreach (var feature in arguments.Features) Features.Add(feature);
                }
                Replan();
                Stage = SetupStage.Package;
                Page = DesktopPage.Install;
                End("Package verified", $"{metadata.Name} {metadata.Version} · {metadata.Payload.Count} files");
            });
            if (arguments.Install && (LicenseText is null || LicenseAccepted)) await InstallAsync();
        }
        catch (Exception error) { await ErrorAsync("Could not open package", error); }
    }

    internal void Replan()
    {
        if (Manifest is null) return;
        using var reader = PackageReader.Open(PackagePath);
        arguments.ValidatePackageIdentity(reader);
        Plan = engine.Plan(reader, Options());
        if (string.IsNullOrWhiteSpace(TargetDirectory) && !OriginalInstallerDestination) TargetDirectory = Plan.InstallDirectory;
    }

    internal void NextStage()
    {
        if (Manifest is null || Busy) return;
        try
        {
            if (Stage < SetupStage.Review) { Replan(); Stage++; Notify(); }
            else if (Stage == SetupStage.Review) _ = InstallAsync();
        }
        catch (Exception error) { _ = ErrorAsync("Review needs attention", error); }
    }

    internal void PreviousStage()
    {
        if (Stage > SetupStage.Package && Stage < SetupStage.Progress) { Stage--; Notify(); }
    }

    internal InstallOptions Options() => new()
    {
        TargetDirectory = OriginalInstallerDestination || string.IsNullOrWhiteSpace(TargetDirectory) ? null : TargetDirectory,
        StateRoot = arguments.StateRoot,
        Scope = Scope,
        Features = Features.ToList(),
        RegisterProduct = !arguments.Isolated && !arguments.NoRegister && Settings.RegisterProducts,
        Isolated = arguments.Isolated,
        Silent = arguments.Silent,
        Launch = arguments.SmokeDirectory is null && Settings.LaunchAfterInstall,
        InstallerPath = Environment.ProcessPath,
        TrustedPublisherKeySha256 = arguments.TrustedPublisherKeySha256,
        RequireSignature = arguments.RequireSignature,
        LegacyArguments = arguments.LegacyArguments,
        LogPath = arguments.LogPath
    };

    internal async Task InstallAsync()
    {
        if (Manifest is null || Busy) return;
        if (LicenseText is not null && !LicenseAccepted) { Stage = SetupStage.Package; Notify(); return; }
        try { Replan(); }
        catch (Exception error) { await ErrorAsync("Installation needs attention", error); return; }
        if (Plan!.RequiresElevation && !InstallerEngine.IsElevated && arguments.SmokeDirectory is null)
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add("--install"); start.ArgumentList.Add(PackagePath);
                if (LicenseAccepted) start.ArgumentList.Add("--accept-license");
                if (!OriginalInstallerDestination) { start.ArgumentList.Add("--target"); start.ArgumentList.Add(TargetDirectory); }
                start.ArgumentList.Add("--scope"); start.ArgumentList.Add(Scope);
                if (Features.Count > 0) { start.ArgumentList.Add("--features"); start.ArgumentList.Add(string.Join(',', Features)); }
                if (arguments.StateRoot is not null) { start.ArgumentList.Add("--state-root"); start.ArgumentList.Add(arguments.StateRoot); }
                Process.Start(start);
                Status = "Installation opened with administrator access";
                StatusDetail = "Complete installation in the elevated heirowInstaller window.";
                Notify();
            }
            catch (Exception error) { await ErrorAsync("Administrator access was not granted", error); }
            return;
        }
        Stage = SetupStage.Progress;
        Page = DesktopPage.Install;
        Begin("Preparing installation", Manifest.Name);
        var options = Options();
        var packagePath = PackagePath;
        try
        {
            var result = await Task.Run(async () =>
            {
                using var reader = PackageReader.Open(packagePath);
                arguments.ValidatePackageIdentity(reader);
                return await engine.InstallAsync(reader, options, Reporter(), Cancellation!.Token);
            });
            await game.OnUiAsync(() =>
            {
                LastReceiptPath = result.ReceiptPath;
                Stage = SetupStage.Complete;
                Progress = 1;
                RefreshInstalled();
                End(result.RebootRequired ? "Installed · restart required" : "Installation complete", $"{Manifest!.Name} {result.Version} is ready to use.");
            });
        }
        catch (Exception error) { await ErrorAsync("Installation stopped", error); }
    }

    internal void RefreshInstalled()
    {
        Installed = InstallerEngine.ListInstalled(arguments.StateRoot);
        SelectedProduct = Installed.FirstOrDefault(p => p.Id == SelectedProduct?.Id) ?? Installed.FirstOrDefault();
    }

    internal async Task RepairAsync()
    {
        var product = SelectedProduct;
        if (product is null || Busy) return;
        if (NeedsMaintenanceElevation(product)) { await ElevatedMaintenanceAsync(product, "repair"); return; }
        Begin("Repairing application", product.Name);
        try
        {
            var result = await Task.Run(() => engine.RepairAsync(product.Id, OptionsFor(product), Reporter(), Cancellation!.Token));
            await game.OnUiAsync(() => { LastReceiptPath = result.ReceiptPath; RefreshInstalled(); End("Repair complete", $"Verified and restored {product.Name}."); });
        }
        catch (Exception error) { await ErrorAsync("Repair stopped", error); }
    }

    internal async Task UninstallAsync()
    {
        var product = SelectedProduct;
        if (product is null || Busy) return;
        if (NeedsMaintenanceElevation(product)) { await ElevatedMaintenanceAsync(product, "uninstall"); return; }
        Begin("Uninstalling application", product.Name);
        try
        {
            await Task.Run(() => engine.UninstallAsync(product.Id, OptionsFor(product), Reporter(), Cancellation!.Token));
            await game.OnUiAsync(() => { Update = null; RefreshInstalled(); End("Uninstall complete", $"Removed {product.Name} and its recorded installation changes."); });
        }
        catch (Exception error) { await ErrorAsync("Uninstall stopped", error); }
    }

    private InstallOptions OptionsFor(InstalledProduct product) => new()
    {
        StateRoot = arguments.StateRoot,
        TargetDirectory = product.UseOriginalInstallDirectory ? null : product.InstallDirectory,
        Scope = product.Scope,
        Features = product.Features.ToList(),
        RegisterProduct = product.RegisteredProduct,
        Isolated = product.Isolated,
        InstallerPath = Environment.ProcessPath,
        Silent = arguments.Silent,
        TrustedPublisherKeySha256 = product.PublisherKeySha256,
        RequireSignature = product.PublisherKeySha256 is not null,
        LogPath = arguments.LogPath
    };

    internal static bool NeedsMaintenanceElevation(InstalledProduct product) => !product.Isolated && !InstallerEngine.IsElevated &&
        (product.Scope == "machine" || product.Modules.Any(module => module.Kind == "service" || module.Kind == "registry" && module.Values.GetValueOrDefault("hive") == "machine"));

    private async Task ElevatedMaintenanceAsync(InstalledProduct product, string operation)
    {
        Begin(operation == "repair" ? "Repairing application" : "Uninstalling application", "Windows will request administrator access.");
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add(operation); start.ArgumentList.Add(product.Id);
            start.ArgumentList.Add("--scope"); start.ArgumentList.Add(product.Scope);
            start.ArgumentList.Add("--silent");
            if (arguments.StateRoot is not null) { start.ArgumentList.Add("--state-root"); start.ArgumentList.Add(arguments.StateRoot); }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the maintenance operation.");
            await process.WaitForExitAsync();
            if (process.ExitCode is not (0 or 3010)) throw new InvalidOperationException($"The maintenance operation returned exit code {process.ExitCode}.");
            await game.OnUiAsync(() => { RefreshInstalled(); End(operation == "repair" ? "Repair complete" : "Uninstall complete", product.Name); });
        }
        catch (Exception error) { await ErrorAsync("Maintenance stopped", error); }
    }

    internal async Task CheckUpdatesAsync()
    {
        var product = SelectedProduct;
        if (product is null || Busy) return;
        if (string.IsNullOrWhiteSpace(product.UpdateUrl)) { End("No update feed", "This publisher has not supplied an update feed for this application."); return; }
        Begin("Checking for updates", product.Name);
        try
        {
            using var client = CreateUpdateClient();
            var result = await client.CheckAsync(product.UpdateUrl, product.Version, product.Id, Cancellation!.Token);
            await game.OnUiAsync(() => { Update = result; End(result.Available ? $"Update available · {result.Feed.Version}" : "You're up to date", result.Available ? result.Feed.ReleaseNotes : $"{product.Name} {product.Version} is the latest published version."); });
        }
        catch (Exception error) { await ErrorAsync("Update check failed", error); }
    }

    internal async Task ApplyUpdateAsync()
    {
        if (Update is not { Available: true } update || SelectedProduct is not { } product || Busy) return;
        Begin("Downloading update", $"{product.Name} {update.Feed.Version}");
        try
        {
            using var client = CreateUpdateClient();
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "heirowInstaller", "Downloads");
            var download = await client.DownloadAsync(update.Feed, directory, Reporter(), Cancellation!.Token);
            if (DesktopUpdateHandoff.IsRunningProduct(product))
            {
                DesktopUpdateHandoff.Start(download, update.Feed, product, OptionsFor(product));
                await game.OnUiAsync(game.Exit);
                return;
            }
            InstallResult result;
            if (NeedsMaintenanceElevation(product))
            {
                await game.OnUiAsync(() => { Status = "Installing update"; StatusDetail = "Windows will request administrator access."; Notify(); });
                result = await DesktopElevatedUpdate.InstallAsync(download, update.Feed, product, OptionsFor(product), Reporter(), Cancellation.Token);
            }
            else result = await client.ApplyAsync(download, update.Feed, OptionsFor(product), Reporter(), Cancellation.Token);
            await game.OnUiAsync(() => { Update = null; RefreshInstalled(); End(result.RebootRequired ? "Update installed · restart required" : "Update installed", $"{product.Name} {update.Feed.Version} is ready."); });
        }
        catch (Exception error) { await ErrorAsync("Update stopped", error); }
    }

    internal async Task BuildAsync()
    {
        if (Busy) return;
        Begin("Building installer package", Path.GetFileName(OutputPath));
        var manifest = ManifestPath; var source = SourceDirectory; var output = OutputPath;
        try
        {
            var signingKey = Environment.GetEnvironmentVariable("HEIROWINSTALLER_SIGNING_KEY_PATH");
            signingKey = string.IsNullOrWhiteSpace(signingKey) ? null : signingKey;
            PackageManifest result;
            if (output.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var metadata = JsonSerializer.Deserialize<PackageManifest>(await File.ReadAllTextAsync(manifest, Cancellation!.Token), InstallerJson.Options) ?? throw new InvalidDataException("The application manifest is empty.");
                var project = File.Exists(source) && Path.GetExtension(source).ToLowerInvariant() is ".csproj" or ".vbproj" or ".fsproj";
                var built = await StandaloneInstallerBuilder.BuildAsync(manifest, output, new()
                {
                    SourceDirectory = project ? null : source, ApplicationProjectPath = project ? source : null,
                    LauncherPath = Environment.GetEnvironmentVariable("HEIROWINSTALLER_LAUNCHER_PATH") ?? Environment.ProcessPath,
                    SigningKeyPath = signingKey, UpdateCompanionExecutablePath = metadata.UpdateCompanion is not null && File.Exists(UpdaterPath) ? UpdaterPath : null
                }, Reporter(), Cancellation.Token);
                result = built.Manifest;
            }
            else result = await Task.Run(() => PackageBuilder.Build(manifest, source, output, new() { SigningKeyPath = signingKey }));
            await game.OnUiAsync(() => { BuiltPackagePath = Path.GetFullPath(output); End(signingKey is null ? "Installer built and verified" : "Signed installer built and verified", $"{result.Name} {result.Version} · {new FileInfo(output).Length:N0} bytes"); });
        }
        catch (Exception error) { await ErrorAsync("Build failed", error); }
    }

    internal async Task PublishAsync()
    {
        if (Busy) return;
        var path = BuiltPackagePath ?? OutputPath;
        Begin("Publishing to SocketJack", Path.GetFileName(path));
        try
        {
            var token = PublisherSignedIn ? publisherSession!.AccessToken : Environment.GetEnvironmentVariable("SOCKETJACK_PUBLISHER_TOKEN");
            var cookie = PublisherSignedIn ? null : Environment.GetEnvironmentVariable("SOCKETJACK_PUBLISHER_COOKIE");
            if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(cookie)) throw new InvalidOperationException("Sign in as an authorized SocketJack publisher, or supply SOCKETJACK_PUBLISHER_TOKEN or SOCKETJACK_PUBLISHER_COOKIE through your environment. Credentials are never saved in the project or desktop settings.");
            using (var reader = PackageReader.Open(path)) await reader.VerifyAsync(cancellationToken: Cancellation!.Token);
            using var publisher = new PublisherClient(new Uri("https://socketjack.com"), bearerToken: token, sessionCookie: cookie);
            await publisher.PublishAsync(path, Cancellation!.Token, ReleaseNotes);
            await game.OnUiAsync(() => End("Release published", "SocketJack accepted your verified package and generated its update feed."));
        }
        catch (Exception error) { await ErrorAsync("Publish failed", error); }
    }

    internal async Task SignInPublisherAsync()
    {
        if (Busy) { if (SigningIn) Cancellation?.Cancel(); return; }
        if (PublisherSignedIn) { publisherSession!.Dispose(); publisherSession = null; End("Signed out", "The in-memory SocketJack publisher session was cleared."); return; }
        SigningIn = true;
        Begin("Approve SocketJack sign-in", "Complete the publisher approval in your browser. You can cancel sign-in here.");
        try
        {
            using var approval = await PublisherSignInClient.BeginAsync(new Uri("https://socketjack.com"), Cancellation!.Token);
            Process.Start(new ProcessStartInfo(approval.BrowserUrl.AbsoluteUri) { UseShellExecute = true });
            var session = await approval.CompleteAsync(Cancellation.Token);
            await game.OnUiAsync(() => { publisherSession?.Dispose(); publisherSession = session; End("SocketJack sign-in complete", PublisherAccount); });
        }
        catch (Exception error) { await ErrorAsync("SocketJack sign-in failed", error); }
        finally { await game.OnUiAsync(() => { SigningIn = false; Notify(); }); }
    }

    public void Dispose() { Cancellation?.Cancel(); Cancellation?.Dispose(); publisherSession?.Dispose(); publisherSession = null; }

    internal void SaveSettings()
    {
        try { Settings.Save(); End("Settings saved", "Your desktop preferences were saved for the next launch."); }
        catch (Exception error) { _ = ErrorAsync("Could not save settings", error); }
    }

    internal async Task RunUpdaterAsync(params string[] command)
    {
        if (Busy) return;
        Begin("heirowUpdater", "Connecting to the companion update manager.");
        try
        {
            if (!File.Exists(UpdaterPath)) throw new FileNotFoundException("Install the complete heirowInstaller release to include heirowUpdater next to the desktop executable.");
            var start = new ProcessStartInfo(UpdaterPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(UpdaterPath)! };
            foreach (var item in command) start.ArgumentList.Add(item);
            if (command.FirstOrDefault() == "update")
            {
                start.ArgumentList.Add("--application-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add("--application-start"); start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (arguments.StateRoot is not null) { start.ArgumentList.Add("--state-root"); start.ArgumentList.Add(arguments.StateRoot); }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The companion updater did not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await standardOutput;
            var error = await standardError;
            await game.OnUiAsync(() =>
            {
                UpdaterOutput = FormatUpdaterOutput(output, error);
                if (string.IsNullOrWhiteSpace(UpdaterOutput)) UpdaterOutput = process.ExitCode == 0 ? "Operation completed successfully." : $"The updater returned exit code {process.ExitCode}.";
                if (UpdaterOutput.Length > 128 * 1024) UpdaterOutput = UpdaterOutput[..(128 * 1024)];
                RefreshInstalled();
                End(process.ExitCode == 0 ? "Updater operation complete" : "Updater needs attention", process.ExitCode == 0 ? "Review the companion updater result below." : $"Exit code {process.ExitCode}. Review the updater result for details.");
                Failed = process.ExitCode != 0;
                Notify();
                if (process.ExitCode == 0 && RequiresExitForStagedUpdate(output)) game.Exit();
            });
        }
        catch (Exception error) { await ErrorAsync("Updater unavailable", error); }
    }

    internal void Cancel() => Cancellation?.Cancel();

    private UpdateClient CreateUpdateClient() => new(allowLoopback: arguments.SelfUpdateSmokeFeed is not null, transport: new SocketJackUpdateTransport(
        credentialOrigin: new Uri("https://socketjack.com"),
        cookie: Environment.GetEnvironmentVariable("SOCKETJACK_DOWNLOAD_COOKIE"),
        bearerToken: Environment.GetEnvironmentVariable("SOCKETJACK_DOWNLOAD_TOKEN")));

    private static bool RequiresExitForStagedUpdate(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in document.RootElement.EnumerateArray())
                if (item.TryGetProperty("requiresApplicationExit", out var exit) && exit.ValueKind == JsonValueKind.True &&
                    item.TryGetProperty("installDirectory", out var root) && root.GetString() is { } directory &&
                    Environment.ProcessPath is { } executable && Path.GetFullPath(executable).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (JsonException) { }
        return false;
    }

    private static string FormatUpdaterOutput(string output, string error)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.IsNullOrWhiteSpace(error) ? "" : error.Trim();
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("enabled", out var enabled))
            {
                var active = enabled.ValueKind == JsonValueKind.True;
                var interval = root.TryGetProperty("intervalMinutes", out var minutes) && minutes.TryGetInt32(out var count) ? count : 60;
                var automatic = root.TryGetProperty("apply", out var apply) && apply.ValueKind == JsonValueKind.True;
                var unsigned = root.TryGetProperty("allowUnsigned", out var allowUnsigned) && allowUnsigned.ValueKind == JsonValueKind.True;
                var conflict = root.TryGetProperty("conflict", out var other) && other.ValueKind == JsonValueKind.True;
                return $"Background checks enabled: {(active ? "Yes" : "No")}\nCheck interval: {interval} minutes\nAutomatic updates: {(automatic ? "On" : "Off")}\nPublisher verification: {(unsigned ? "Optional (manually enabled)" : "Required")}" +
                    (conflict ? "\nAnother application is using this schedule. Choose a different schedule in the updater." : "");
            }
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return "No applications with an update feed are registered yet.\nInstalled applications appear here when their publisher supplies updates.";
                return string.Join("\n\n", root.EnumerateArray().Select(item =>
                {
                    string Read(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                    var name = Read("name"); if (name.Length == 0) name = Read("productId");
                    var current = Read("currentVersion"); var latest = Read("latestVersion");
                    var state = Read("status") switch
                    {
                        "updated" => "Update installed",
                        "completed-as-administrator" => "Update completed with administrator approval",
                        "elevated-update-failed" => "The administrator update did not complete. Review Activity for details.",
                        "update-staged" => "Update prepared. Restarting the application.",
                        "update-available" => "Update available",
                        "requires-elevation" => "Administrator approval is needed to install this update.",
                        "current" => "Up to date",
                        "error" => Read("error"),
                        _ => "Check complete"
                    };
                    var versions = current.Length > 0 ? $"\nInstalled: {current}" : "";
                    if (latest.Length > 0) versions += (current.Length > 0 ? "  ·  " : "\n") + $"Latest: {latest}";
                    return $"{name}{versions}\n{state}";
                }));
            }
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(error) ? "The updater completed. Review Activity for details." : error.Trim();
    }

    internal async Task RunSelfUpdateAcceptanceAsync()
    {
        var report = arguments.SelfUpdateSmokeReport!;
        try
        {
            var product = InstallerEngine.ListInstalled(arguments.StateRoot).Single(DesktopUpdateHandoff.IsRunningProduct);
            if (!product.Isolated || product.Scope != "user") throw new InvalidOperationException("Self-update acceptance requires an isolated per-user product.");
            product.UpdateUrl = arguments.SelfUpdateSmokeFeed;
            await game.OnUiAsync(() => { SelectedProduct = product; Page = DesktopPage.Installed; });
            await CheckUpdatesAsync();
            if (Failed || Update is not { Available: true }) throw new InvalidOperationException("The self-update acceptance feed did not produce an applicable update.");
            PackageSafety.EnsureNoReparsePoints(report);
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { phase = "handoff", oldPid = Environment.ProcessId, productId = product.Id, oldVersion = product.Version, newVersion = Update.Feed.Version, publisherKeySha256 = product.PublisherKeySha256 }, InstallerJson.Options));
            await ApplyUpdateAsync();
            if (Failed) throw new InvalidOperationException(StatusDetail);
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { phase = "failed", error = error.Message }, InstallerJson.Options));
            await game.OnUiAsync(() => { Environment.ExitCode = 1; game.Exit(); });
        }
    }

    private IProgress<InstallProgress> Reporter() => new UiProgress(value => game.Enqueue(() =>
    {
        Progress = value.Fraction;
        Status = value.Phase;
        StatusDetail = value.Message;
        AddActivity(value.Phase, value.Message);
        Notify();
    }));

    private void Begin(string title, string detail)
    {
        Cancellation?.Dispose(); Cancellation = new CancellationTokenSource();
        Busy = true; Failed = false; Progress = 0; Status = title; StatusDetail = detail;
        AddActivity(title, detail); Notify();
    }

    private void End(string title, string detail)
    {
        Busy = false; Failed = false; Status = title; StatusDetail = detail;
        AddActivity(title, detail); Notify();
    }

    private Task ErrorAsync(string title, Exception error) => game.OnUiAsync(() =>
    {
        Busy = false; Failed = true; Status = error is OperationCanceledException ? "Operation canceled" : title;
        StatusDetail = error.Message;
        AddActivity(Status, StatusDetail, true); Notify();
    });

    private void AddActivity(string title, string detail, bool error = false)
    {
        Activity.Insert(0, new(DateTimeOffset.Now, title, detail, error));
        if (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
    }

    private sealed class UiProgress(Action<InstallProgress> callback) : IProgress<InstallProgress>
    { public void Report(InstallProgress value) => callback(value); }
}
