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

internal sealed partial class AppController : IDisposable
{
    private readonly InstallerGame game;
    private readonly AppArguments arguments;
    private readonly InstallerEngine engine = new();
    private PublisherSession? publisherSession;
    private PackageReader? verifiedPlanningPackage;
    internal bool SigningIn { get; private set; }
    internal bool PublisherSignedIn => publisherSession is { } session && session.ExpiresUtc > DateTimeOffset.UtcNow;
    internal string PublisherAccount => PublisherSignedIn ? $"Signed in as {publisherSession!.Username} · expires {publisherSession.ExpiresUtc.ToLocalTime():HH:mm}" : "Sign in to SocketJack to publish, or use publisher credentials from your environment.";
    internal DesktopSettings Settings { get; private set; } = new();
    internal DesktopPage Page { get; set; } = DesktopPage.Install;
    internal SetupStage Stage { get; set; }
    internal bool IsMaintenanceWizard => arguments.MaintenanceProductId is not null;
    internal bool IsWizard => arguments.PackagePath is not null || IsMaintenanceWizard;
    internal string? MaintenanceOperation => arguments.MaintenanceOperation;
    internal PackageManifest? Manifest { get; private set; }
    internal InstallPlan? Plan { get; private set; }
    internal string PackagePath { get; private set; } = "";
    internal string TargetDirectory { get; set; } = "";
    internal bool OriginalInstallerDestination => Manifest is { } manifest && (manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null) && string.IsNullOrWhiteSpace(manifest.InstallDirectory) && arguments.TargetDirectory is null;
    internal string DisplayInstallDirectory => !IsMaintenanceWizard && OriginalInstallerDestination ? "Original installer default" : TargetDirectory;
    internal string Scope { get; set; } = "user";
    internal HashSet<string> Features { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<InstalledProduct> Installed { get; private set; } = [];
    internal InstalledProduct? SelectedProduct { get; set; }
    internal UpdateCheckResult? Update { get; private set; }
    internal List<ActivityEntry> Activity { get; } = [];
    internal bool Busy { get; private set; }
    internal bool CheckingUpdates { get; private set; }
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
    // Presentation snapshot only. Operations resolve and verify again off the UI thread.
    internal string UpdaterPath { get; private set; } = "";
    private int refreshingUpdater;
    private static string ResolveUpdater()
    {
        try { return UpdaterHostResolver.FindExecutable() ?? ""; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
        { return ""; } // Invalid own-root profiles still fail closed.
    }
    private async Task RefreshUpdaterAvailabilityAsync()
    {
        if (Interlocked.CompareExchange(ref refreshingUpdater, 1, 0) != 0) return;
        try
        {
            var path = await Task.Run(ResolveUpdater);
            await game.OnUiAsync(() => { if (UpdaterPath != path) { UpdaterPath = path; Notify(); } });
        }
        finally { Interlocked.Exchange(ref refreshingUpdater, 0); }
    }
    internal CancellationTokenSource? Cancellation { get; private set; }
    internal event Action? Changed;
    internal event Action? ProgressChanged;

    internal AppController(InstallerGame game, AppArguments arguments)
    {
        this.game = game;
        this.arguments = arguments;
        OutputPath = Path.Combine(Settings.DefaultOutputDirectory, "application-Setup.exe");
        AddActivity("Desktop ready", "Project-Z native graphics · 16× AA · 16× AF");
    }

    internal async Task InitializeDesktopAsync()
    {
        Begin(IsWizard ? "Preparing setup" : "Loading applications", IsWizard ? "Loading your setup preferences." : "Reading installed applications and preparing the update manager.");
        try
        {
            var settings = Task.Run(DesktopSettings.Load);
            if (IsWizard) await settings;
            else await Task.WhenAll(RefreshInstalledAsync(), RefreshUpdaterAvailabilityAsync(), settings);
            await game.OnUiAsync(() => { Settings = settings.Result; OutputPath = Path.Combine(Settings.DefaultOutputDirectory, "application-Setup.exe"); End("Ready when you are", "Open a package or check your installed applications for updates."); });
        }
        catch (Exception error) { await ErrorAsync("Could not load applications", error); }
    }

    internal void Notify() => Changed?.Invoke();
    internal void ExpirePublisherSession()
    {
        if (publisherSession is { } session && session.ExpiresUtc <= DateTimeOffset.UtcNow) { session.Dispose(); publisherSession = null; Notify(); }
    }

    internal void Navigate(DesktopPage page)
    {
        if (IsWizard && page != DesktopPage.Install) return;
        if (RunningApplicationsDialog is not null) return;
        if (Busy && page == DesktopPage.Install && Stage == SetupStage.Progress) { Page = page; Notify(); return; }
        Page = page;
        if (page == DesktopPage.Updates) _ = RefreshUpdaterAvailabilityAsync();
        if (page == DesktopPage.Installed && !Busy) _ = ReloadInstalledAsync();
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
                var package = PackageReader.Open(path);
                try
                {
                    arguments.ValidatePackageIdentity(package);
                    await package.VerifyAsync(cancellationToken: Cancellation!.Token);
                    if (IsWizard && package.Manifest.LegacyExecutable is { SilentInstallArguments: null })
                        throw new NotSupportedException("This application's publisher has not configured unattended setup. Ask the publisher for a package with silent installation support.");
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
                    var artwork = IsWizard ? await ReadWizardArtworkAsync(package, Cancellation!.Token) : null;
                    return (Package: package, Manifest: package.Manifest, License: license, Artwork: artwork);
                }
                catch { package.Dispose(); throw; }
            });
            await game.OnUiAsync(() =>
            {
                var metadata = loaded.Manifest;
                verifiedPlanningPackage?.Dispose();
                verifiedPlanningPackage = loaded.Package;
                PackagePath = Path.GetFullPath(path);
                Manifest = metadata;
                WizardArtworkPath = loaded.Artwork;
                if (IsWizard) game.Window.Title = metadata.Name + " Setup";
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
                Plan = null;
            });
            await ReplanAsync();
            await game.OnUiAsync(() =>
            {
                Stage = SetupStage.Package;
                Page = DesktopPage.Install;
                var metadata = Manifest!;
                End(IsWizard ? "Ready to install" : "Package verified", IsWizard ? $"{metadata.Name} {metadata.Version} · {metadata.Publisher}" : $"{metadata.Name} {metadata.Version} · {metadata.Payload.Count} files");
            });
            if (arguments.Install && (LicenseText is null || LicenseAccepted)) await InstallAsync();
        }
        catch (Exception error) { await ErrorAsync("Could not open package", error); }
    }

    private async Task ReplanAsync()
    {
        PackageReader package = null!; InstallOptions options = null!;
        await game.OnUiAsync(() => { package = verifiedPlanningPackage ?? throw new InvalidOperationException("Open and verify the package before planning it."); options = Options(); });
        var token = Cancellation!.Token;
        var plan = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            // The reader retains its read-only archive handle from the initial
            // whole-package and payload checks. No timestamp-based trust cache.
            // InstallAsync independently opens and validates the actual input again.
            var result = engine.Plan(package, options);
            token.ThrowIfCancellationRequested();
            return result;
        }, token);
        await game.OnUiAsync(() =>
        {
            Plan = plan;
            if (string.IsNullOrWhiteSpace(TargetDirectory) && !OriginalInstallerDestination) TargetDirectory = plan.InstallDirectory;
        });
    }

    internal void NextStage() => _ = NextStageAsync();

    private async Task NextStageAsync()
    {
        if (Manifest is null || Busy) return;
        if (Stage == SetupStage.Review) { await InstallAsync(); return; }
        if (Stage >= SetupStage.Review) return;
        // Features and location do not display the operation plan. Revalidate
        // the chosen configuration when entering Review and again before install.
        if (Stage < SetupStage.Destination) { Stage++; Notify(); return; }
        var next = Stage + 1;
        Begin("Checking installation choices", "Checking the selected features and installation location.");
        try
        {
            await ReplanAsync();
            await game.OnUiAsync(() => { Stage = next; End("Ready to continue", "Your installation choices have been checked."); });
        }
        catch (Exception error) { await ErrorAsync("Review needs attention", error); }
    }

    internal async Task ReplanChoicesAsync()
    {
        if (Manifest is null || Busy) return;
        Begin("Checking installation choices", "Checking the selected installation location.");
        try { await ReplanAsync(); await game.OnUiAsync(() => End("Location checked", "Review your installation choices before continuing.")); }
        catch (Exception error) { await ErrorAsync("Location needs attention", error); }
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
        RegisterProduct = !arguments.Isolated && !arguments.NoRegister && (IsWizard || Settings.RegisterProducts),
        Isolated = arguments.Isolated,
        Silent = IsWizard || arguments.Silent,
        Launch = !IsWizard && arguments.SmokeDirectory is null && (arguments.Launch || Settings.LaunchAfterInstall),
        InstallerPath = Environment.ProcessPath,
        TrustedPublisherKeySha256 = arguments.TrustedPublisherKeySha256,
        RequireSignature = arguments.RequireSignature,
        LegacyArguments = arguments.LegacyArguments,
        LogPath = arguments.LogPath,
        RunningApplicationsPrompt = PromptRunningApplicationsAsync
    };

    internal async Task InstallAsync()
    {
        if (Manifest is null || Busy) return;
        if (LicenseText is not null && !LicenseAccepted) { Stage = SetupStage.Package; Notify(); return; }
        Begin("Checking installation choices", "Checking the selected features and installation location.");
        try { await ReplanAsync(); }
        catch (Exception error) { await ErrorAsync("Installation needs attention", error); return; }
        var useWorker = false; var launchAfterWorker = false; InstallOptions options = null!; string packagePath = "";
        await game.OnUiAsync(() =>
        {
            useWorker = Plan!.RequiresElevation && !InstallerEngine.IsElevated && !arguments.Isolated || arguments.WizardSmokeDirectory is not null;
            Stage = SetupStage.Progress; Page = DesktopPage.Install;
            Begin("Preparing installation", Manifest.Name);
            options = Options(); launchAfterWorker = useWorker && options.Launch;
            if (useWorker) options.Launch = false;
            packagePath = PackagePath;
        });
        try
        {
            var result = await Task.Run(async () =>
            {
                using var reader = PackageReader.Open(packagePath);
                arguments.ValidatePackageIdentity(reader);
                await WaitForRequestedProcessAsync(RunningApplicationTarget.FromPlan(engine.Plan(reader, options), "install", options.Isolated), Cancellation!.Token);
                if (useWorker)
                    return await ElevatedInstallWorker.InstallAsync(packagePath, engine.Plan(reader, options), options,
                        PromptRunningApplicationsAsync, Reporter(), arguments.WizardSmokeDirectory, Cancellation.Token);
                return await engine.InstallAsync(reader, options, Reporter(), Cancellation!.Token);
            });
            await RefreshInstalledAsync();
            await game.OnUiAsync(() =>
            {
                LastReceiptPath = result.ReceiptPath;
                LastInstallation = result;
                Stage = SetupStage.Complete;
                Progress = 1;
                End(result.RebootRequired ? "Installed · restart required" : "Installation complete", $"{Manifest!.Name} {result.Version} is ready to use.");
                CloseCompletedChild(result.RebootRequired);
            });
            if (launchAfterWorker) await LaunchInstalledApplicationAsync();
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { await ErrorAsync("Installation canceled", new OperationCanceledException("Administrator approval was canceled. You can try again when you are ready.", error)); }
        catch (Exception error) { await ErrorAsync("Installation stopped", error); }
    }

    internal async Task RefreshInstalledAsync()
    {
        var productId = IsWizard ? arguments.MaintenanceProductId ?? Manifest?.Id : null;
        if (IsWizard && productId is null) return;
        var installed = await Task.Run(() => InstallerEngine.ListInstalled(arguments.StateRoot));
        if (IsWizard) installed = installed.Where(product => product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase) &&
            (arguments.Scope is null || product.Scope == arguments.Scope)).ToArray();
        await game.OnUiAsync(() => { Installed = installed; SelectedProduct = Installed.FirstOrDefault(p => p.Id == SelectedProduct?.Id) ?? Installed.FirstOrDefault(); Notify(); });
    }

    internal async Task ReloadInstalledAsync()
    {
        if (Busy) return;
        Begin("Refreshing applications", "Reading installed application information.");
        try { await RefreshInstalledAsync(); await game.OnUiAsync(() => End("Applications refreshed", $"{Installed.Count} installed applications.")); }
        catch (Exception error) { await ErrorAsync("Could not read applications", error); }
    }

    internal async Task RepairAsync()
    {
        var product = SelectedProduct;
        if (product is null || Busy) return;
        if (IsMaintenanceWizard)
        {
            Begin("Checking application", "Checking the installed application's maintenance files.");
            try { product = await Task.Run(ValidateMaintenanceIdentity); await game.OnUiAsync(() => { Stage = SetupStage.Progress; Page = DesktopPage.Install; }); }
            catch (Exception error) { await ErrorAsync("The application changed", error); return; }
        }
        if (NeedsMaintenanceElevation(product) || arguments.WizardSmokeDirectory is not null) { await ElevatedMaintenanceAsync(product, "repair"); return; }
        Begin("Repairing application", product.Name);
        try
        {
            var result = await Task.Run(async () =>
            {
                await WaitForProductDependencyAsync(product, "repair", Cancellation!.Token);
                return await engine.RepairAsync(product.Id, OptionsFor(product, "repair"), Reporter(), Cancellation.Token);
            });
            await RefreshInstalledAsync();
            await game.OnUiAsync(() => { LastReceiptPath = result.ReceiptPath; if (IsMaintenanceWizard) { LastInstallation = result; Stage = SetupStage.Complete; Progress = 1; } End("Repair complete", $"Verified and restored {product.Name}."); CloseCompletedChild(result.RebootRequired); });
        }
        catch (Exception error) { await ErrorAsync("Repair stopped", error); }
    }

    internal async Task UninstallAsync()
    {
        var product = SelectedProduct;
        if (product is null || Busy) return;
        if (IsMaintenanceWizard)
        {
            Begin("Checking application", "Checking the installed application's maintenance files.");
            try { product = await Task.Run(ValidateMaintenanceIdentity); await game.OnUiAsync(() => { Stage = SetupStage.Progress; Page = DesktopPage.Install; }); }
            catch (Exception error) { await ErrorAsync("The application changed", error); return; }
        }
        if (NeedsMaintenanceElevation(product) || arguments.WizardSmokeDirectory is not null) { await ElevatedMaintenanceAsync(product, "uninstall"); return; }
        Begin("Uninstalling application", product.Name);
        try
        {
            var result = await Task.Run(async () =>
            {
                await WaitForProductDependencyAsync(product, "uninstall", Cancellation!.Token);
                return await engine.UninstallAsync(product.Id, OptionsFor(product, "uninstall"), Reporter(), Cancellation.Token);
            });
            await RefreshInstalledAsync();
            await game.OnUiAsync(() => { Update = null; if (IsMaintenanceWizard) { LastInstallation = result; Stage = SetupStage.Complete; Progress = 1; } End("Uninstall complete", $"Removed {product.Name} and its recorded installation changes."); CloseCompletedChild(result.RebootRequired); });
        }
        catch (Exception error) { await ErrorAsync("Uninstall stopped", error); }
    }

    private InstallOptions OptionsFor(InstalledProduct product, string? operation = null) => new()
    {
        StateRoot = arguments.StateRoot,
        TargetDirectory = product.UseOriginalInstallDirectory ? null : product.InstallDirectory,
        Scope = product.Scope,
        Features = product.Features.ToList(),
        RegisterProduct = product.RegisteredProduct,
        Isolated = product.Isolated,
        InstallerPath = Environment.ProcessPath,
        Silent = IsWizard || arguments.Silent,
        TrustedPublisherKeySha256 = product.PublisherKeySha256,
        RequireSignature = product.PublisherKeySha256 is not null,
        LogPath = arguments.LogPath,
        LegacyArguments = arguments.LegacyArguments,
        RunningApplicationsPrompt = (request, token) => PromptRunningApplicationsAsync(operation is null ? request : request with { Operation = operation }, token)
    };

    internal static bool NeedsMaintenanceElevation(InstalledProduct product) => !product.Isolated && !InstallerEngine.IsElevated &&
        (product.Scope == "machine" || product.Modules.Any(module => module.Kind == "service" || module.Kind == "registry" && module.Values.GetValueOrDefault("hive") == "machine"));

    private async Task ElevatedMaintenanceAsync(InstalledProduct product, string operation)
    {
        Begin(operation == "repair" ? "Repairing application" : "Uninstalling application", "Windows will request administrator access.");
        try
        {
            var result = await Task.Run(async () =>
            {
                await WaitForProductDependencyAsync(product, operation, Cancellation!.Token);
                using var package = PackageReader.Open(product.PackagePath);
                package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
                var options = OptionsFor(product);
                options.Silent = true;
                return await ElevatedInstallWorker.InstallAsync(product.PackagePath, engine.Plan(package, options), options,
                    PromptRunningApplicationsAsync, Reporter(), arguments.WizardSmokeDirectory, Cancellation.Token, operation);
            });
            await RefreshInstalledAsync();
            await game.OnUiAsync(() => { LastReceiptPath = result.ReceiptPath; if (IsMaintenanceWizard) { LastInstallation = result; Stage = SetupStage.Complete; Progress = 1; } End(operation == "repair" ? "Repair complete" : "Uninstall complete", product.Name); CloseCompletedChild(result.RebootRequired); });
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { await ErrorAsync("Maintenance canceled", new OperationCanceledException("Administrator approval was canceled.", error)); }
        catch (Exception error) { await ErrorAsync("Maintenance stopped", error); }
    }

    internal async Task CheckUpdatesAsync()
    {
        var product = SelectedProduct;
        if (product is null || Busy) return;
        if (string.IsNullOrWhiteSpace(product.UpdateUrl)) { End("No update feed", "This publisher has not supplied an update feed for this application."); return; }
        Begin("Checking for updates", product.Name);
        CheckingUpdates = true;
        try
        {
            using var client = CreateUpdateClient();
            var token = Cancellation!.Token;
            var reporter = Reporter();
            var result = await Task.Run(() => client.CheckAsync(product.UpdateUrl, product.Version, product.Id, token, reporter), token);
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
            await RefreshInstalledAsync();
            await game.OnUiAsync(() => { Update = null; End(result.RebootRequired ? "Update installed · restart required" : "Update installed", $"{product.Name} {update.Feed.Version} is ready."); CloseCompletedChild(result.RebootRequired); });
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
            var legal = await LegalForBuildAsync(manifest);
            PackageManifest result;
            if (output.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var metadata = JsonSerializer.Deserialize<PackageManifest>(await File.ReadAllTextAsync(manifest, Cancellation!.Token), InstallerJson.Options) ?? throw new InvalidDataException("The application manifest is empty.");
                var project = File.Exists(source) && Path.GetExtension(source).ToLowerInvariant() is ".csproj" or ".vbproj" or ".fsproj";
                var updater = metadata.UpdateCompanion is null ? null : await Task.Run(ResolveUpdater);
                var built = await StandaloneInstallerBuilder.BuildAsync(manifest, output, new()
                {
                    SourceDirectory = project ? null : source, ApplicationProjectPath = project ? source : null,
                    LauncherPath = Environment.GetEnvironmentVariable("HEIROWINSTALLER_LAUNCHER_PATH") ?? Environment.ProcessPath,
                    SigningKeyPath = signingKey, UpdateCompanionExecutablePath = string.IsNullOrEmpty(updater) ? null : updater,
                    LegalDocuments = legal
                }, Reporter(), Cancellation.Token);
                result = built.Manifest;
            }
            else result = await Task.Run(() => PackageBuilder.Build(manifest, source, output, new() { SigningKeyPath = signingKey, LegalDocuments = legal }));
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
            await Task.Run(async () =>
            {
                using var reader = PackageReader.Open(path);
                RequireMatchingLegalDraft(reader.Manifest);
                await reader.VerifyAsync(cancellationToken: Cancellation!.Token);
            }, Cancellation!.Token);
            using var publisher = new PublisherClient(new Uri("https://socketjack.com"), bearerToken: token, sessionCookie: cookie);
            var receipt = await publisher.PublishAsync(path, Cancellation!.Token, ReleaseNotes);
            await game.OnUiAsync(() => End(receipt.UpdateActivationPending == true ? "Release staged" : "Release published",
                receipt.UpdateActivationPending == true
                    ? "Signed file updates are missing for existing versions. Publish those updates, then publish this same package again to activate it."
                    : "SocketJack accepted your verified package and generated its update feed."));
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

    public void Dispose() { Cancellation?.Cancel(); runningApplicationsChoice?.TrySetCanceled(); Cancellation?.Dispose(); publisherSession?.Dispose(); publisherSession = null; verifiedPlanningPackage?.Dispose(); verifiedPlanningPackage = null; DisposeWizardArtwork(); }

    internal async void SaveSettings()
    {
        if (Busy) return;
        Begin("Saving preferences", "Writing your desktop preferences.");
        try { await Task.Run(Settings.Save); await game.OnUiAsync(() => End("Settings saved", "Your desktop preferences were saved for the next launch.")); }
        catch (Exception error) { await ErrorAsync("Could not save settings", error); }
    }

    internal async Task RunUpdaterAsync(params string[] command)
    {
        if (Busy) return;
        Begin(command.FirstOrDefault() == "check" ? "Checking for updates" : "heirowUpdater", "Verifying the companion update manager.");
        CheckingUpdates = command.FirstOrDefault() == "check";
        var token = Cancellation!.Token;
        var reporter = Reporter();
        try
        {
            var updater = await Task.Run(ResolveUpdater);
            if (!File.Exists(updater)) throw new FileNotFoundException("Install or repair heirowInstaller with optional updates enabled to restore its verified heirowUpdater.");
            var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(updater)! };
            foreach (var item in command) start.ArgumentList.Add(item);
            start.ArgumentList.Add("--progress-json");
            if (command.FirstOrDefault() == "update")
            {
                start.ArgumentList.Add("--application-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add("--application-start"); start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (arguments.StateRoot is not null) { start.ArgumentList.Add("--state-root"); start.ArgumentList.Add(arguments.StateRoot); }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The companion updater did not start.");
            // Checking cannot mutate installations. Cancel only our owned check process.
            using var stopCheck = CheckingUpdates ? token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }) : default;
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = ReadUpdaterProgressAsync(process, reporter);
            await process.WaitForExitAsync();
            var output = await standardOutput;
            var error = await standardError;
            token.ThrowIfCancellationRequested();
            await RefreshInstalledAsync();
            await game.OnUiAsync(() =>
            {
                UpdaterOutput = FormatUpdaterOutput(output, error);
                if (string.IsNullOrWhiteSpace(UpdaterOutput)) UpdaterOutput = process.ExitCode == 0 ? "Operation completed successfully." : $"The updater returned exit code {process.ExitCode}.";
                if (UpdaterOutput.Length > 128 * 1024) UpdaterOutput = UpdaterOutput[..(128 * 1024)];
                End(process.ExitCode == 0 ? "Updater operation complete" : "Updater needs attention", process.ExitCode == 0 ? "Review the companion updater result below." : $"Exit code {process.ExitCode}. Review the updater result for details.");
                Failed = process.ExitCode != 0;
                Notify();
                if (process.ExitCode == 0 && RequiresExitForStagedUpdate(output)) game.Exit();
            });
        }
        catch (Exception error) { await ErrorAsync("Updater unavailable", error); }
    }

    private static async Task<string> ReadUpdaterProgressAsync(Process process, IProgress<InstallProgress> progress)
    {
        var errors = new System.Text.StringBuilder();
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            try
            {
                if (JsonSerializer.Deserialize<InstallProgress>(line) is { Phase: not null, Message: not null } value)
                { progress.Report(value); continue; }
            }
            catch (JsonException) { }
            if (errors.Length < 128 * 1024) errors.AppendLine(line);
        }
        return errors.ToString();
    }

    internal void Cancel()
    {
        if (RunningApplicationsDialog is not null) CancelRunningApplications();
        else Cancellation?.Cancel();
    }

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

    internal IProgress<InstallProgress> Reporter() => new UiProgress(game.Enqueue, values =>
    {
        var value = values[^1];
        if (values.Any(item => item.Phase.Equals("applications", StringComparison.OrdinalIgnoreCase))) ClearRunningApplicationsDialog();
        Progress = IsWizard && Stage == SetupStage.Progress ? Math.Max(Progress, value.Phase.ToLowerInvariant() switch
        {
            "verify" => value.Fraction * 0.10,
            "extract" => 0.12 + value.Fraction * 0.30,
            "install" => 0.45 + value.Fraction * 0.50,
            "uninstall" => 0.15 + value.Fraction * 0.80,
            "complete" => 1,
            _ => Progress
        }) : value.Fraction;
        Status = IsWizard ? value.Phase.ToLowerInvariant() switch
        {
            "verify" => "Checking application files",
            "extract" => "Preparing application files",
            "applications" => "Applications closed",
            "install" => "Installing application files",
            "uninstall" => "Removing application files",
            "complete" => MaintenanceOperation == "uninstall" ? "Uninstall complete" : MaintenanceOperation == "repair" ? "Repair complete" : "Installation complete",
            _ => "Installing " + Manifest!.Name
        } : value.Phase switch { "check" or "check-complete" => "Checking for updates", _ => value.Phase };
        StatusDetail = IsWizard ? value.Phase.ToLowerInvariant() switch
        {
            "verify" => "Checking your application's installation files.",
            "extract" => "Preparing the selected application files.",
            "install" => "Installing the selected application components.",
            "uninstall" => "Removing the application and its recorded changes.",
            "applications" => "The apps using this installation have closed. Setup is continuing.",
            "complete" => MaintenanceOperation == "uninstall" ? "The application has been removed." : "Your application is ready to use.",
            _ => "Applying this application's installation changes."
        } : value.Total > 0 && value.Phase is "check" or "check-complete" ? $"{value.Completed}/{value.Total} applications checked · {value.Message}" : value.Message;
        foreach (var item in values) AddActivity(item.Phase, item.Message);
        ProgressChanged?.Invoke();
    });

    private void Begin(string title, string detail)
    {
        ClearRunningApplicationsDialog(); runningApplicationsCanceled = false;
        Cancellation?.Dispose(); Cancellation = new CancellationTokenSource();
        Busy = true; CheckingUpdates = false; Failed = false; Progress = 0; Status = title; StatusDetail = detail;
        AddActivity(title, detail); Notify();
    }

    private void End(string title, string detail)
    {
        ClearRunningApplicationsDialog();
        Busy = false; CheckingUpdates = false; Failed = false; Status = title; StatusDetail = detail;
        AddActivity(title, detail); Notify();
    }

    private void CloseCompletedChild(bool rebootRequired = false)
    {
        if (arguments.MaintenanceProductId is not null || arguments.CloseOnComplete) Environment.ExitCode = rebootRequired ? 3010 : 0;
        if (!arguments.CloseOnComplete) return;
        game.Exit();
    }

    private Task ErrorAsync(string title, Exception error) => game.OnUiAsync(() =>
    {
        ClearRunningApplicationsDialog();
        Busy = false; CheckingUpdates = false; Failed = error is not OperationCanceledException;
        Status = error is OperationCanceledException ? "Operation canceled" : title;
        StatusDetail = runningApplicationsCanceled ? "The operation was canceled before changing application files. You can try again when you are ready." : error.Message;
        if (runningApplicationsCanceled && Stage == SetupStage.Progress) Stage = SetupStage.Review;
        AddActivity(Status, StatusDetail, Failed); Notify();
        if (arguments.CloseOnComplete || arguments.MaintenanceProductId is not null) Environment.ExitCode = error is OperationCanceledException ? 1602 : 1;
        if (arguments.CloseOnComplete && error is OperationCanceledException) game.Exit();
    });

    private void AddActivity(string title, string detail, bool error = false)
    {
        Activity.Insert(0, new(DateTimeOffset.Now, title, detail, error));
        if (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
    }

    private sealed class UiProgress(Action<Action> enqueue, Action<InstallProgress[]> callback) : IProgress<InstallProgress>
    {
        private readonly object gate = new();
        private readonly Queue<InstallProgress> pending = new();
        private InstallProgress? applicationsClosed;
        private bool queued;
        public void Report(InstallProgress value)
        {
            lock (gate)
            {
                if (value.Phase.Equals("applications", StringComparison.OrdinalIgnoreCase)) applicationsClosed = value;
                if (pending.Count == 200) pending.Dequeue();
                pending.Enqueue(value);
                if (queued) return;
                queued = true;
            }
            enqueue(Flush);
        }
        private void Flush()
        {
            InstallProgress[] values;
            lock (gate)
            {
                values = pending.ToArray(); pending.Clear(); queued = false;
                if (applicationsClosed is { } closed && !values.Contains(closed)) values = [closed, .. values];
                applicationsClosed = null;
            }
            callback(values);
        }
    }
}
