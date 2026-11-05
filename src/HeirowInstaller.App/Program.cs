using System.Text.Json;
using HeirowInstaller.Core;
using System.Diagnostics;

namespace HeirowInstaller.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (ElevatedInstallWorker.IsWorker(args)) return ElevatedInstallWorker.RunAsync(args).GetAwaiter().GetResult();
        AppArguments? options = null;
        try
        {
            var maintenance = args.Length >= 2 && args[0] is "uninstall" or "repair";
            options = maintenance ? AppArguments.Parse(args.Skip(2).ToArray(), maintenanceInvocation: true) with { MaintenanceProductId = args[1], MaintenanceOperation = args[0] } : AppArguments.Parse(args);
            using var graphicsContext = DirectX11Fallback.EnterChildContext(options);
#if WINDOWS
            if (options.GraphicsHandoffSmokeRoot is { } handoffSmoke)
            {
                GraphicsHandoffSmoke.ValidateArguments(options);
                if (options.DirectX11Child) return GraphicsHandoffSmoke.RunAsync(handoffSmoke).GetAwaiter().GetResult();
                return DirectX11Fallback.Run(options, args) ?? throw new InvalidOperationException("The graphics handoff fixture requires the embedded DX11 host.");
            }
#else
            if (options.GraphicsHandoffSmokeRoot is not null) throw new PlatformNotSupportedException("Graphics handoff diagnostics require Windows.");
#endif
            if (options.Silent)
            {
                BootstrapReadiness.Signal();
                EnsureRequestedDependencyExited(options);
                if (maintenance) return Maintain(args, options);
                if (options.PackagePath is null) throw new ArgumentException("Silent installation requires a .hinst package or bundled installer.");
                using var package = PackageReader.Open(options.PackagePath);
                options.ValidatePackageIdentity(package);
                var engine = new InstallerEngine();
                var install = new InstallOptions { TargetDirectory = options.TargetDirectory, StateRoot = options.StateRoot, Scope = options.Scope, Features = options.Features, Silent = true, InstallerPath = DirectX11Fallback.CurrentPrimaryHostPath, Isolated = options.Isolated, RegisterProduct = !options.Isolated && !options.NoRegister, Launch = options.Launch, TrustedPublisherKeySha256 = options.TrustedPublisherKeySha256, RequireSignature = options.RequireSignature, LegacyArguments = options.LegacyArguments, LogPath = options.LogPath };
                var plan = engine.Plan(package, install);
                if (plan.RequiresElevation && !InstallerEngine.IsElevated && !options.Isolated)
                {
                    install.Launch = false;
                    var elevated = ElevatedInstallWorker.InstallAsync(options.PackagePath, plan, install, SilentApplicationsAsync,
                        null, null, CancellationToken.None).GetAwaiter().GetResult();
                    if (options.Launch && package.Manifest.EntryPoint.Length > 0)
                    {
                        var product = InstallerEngine.ListInstalled(options.StateRoot).Single(value => value.Id == package.Manifest.Id && value.Scope == plan.Scope);
                        _ = Process.Start(InstalledApplicationLaunch.Create(product, plan.UpdateCompanionEnabled))
                            ?? throw new InvalidOperationException("Windows did not open the installed application.");
                    }
                    return elevated.RebootRequired ? 3010 : 0;
                }
                var result = engine.InstallAsync(package, install).GetAwaiter().GetResult();
                return result.RebootRequired ? 3010 : 0;
            }
            DesktopPlatform.Initialize();
            Task? startupCompanion = null;
            InstallerGame? game = null;
            void Hardware()
            {
                using var candidate = new InstallerGame(options);
                game = candidate;
                candidate.Run();
                startupCompanion = candidate.StartupCompanion;
            }
            if (options.DirectX11Child && options.SoftwareRendering) RunSoftwareDesktop(options);
            else if (options.DirectX11Child)
            {
                try { Hardware(); }
                catch (Exception error) when (GraphicsStartup.ShouldUseSoftwareFallback(error, game?.ContentLoadingStarted == true, OperatingSystem.IsWindows()))
                {
                    return DirectX11Fallback.GraphicsStartupFailed;
                }
            }
            else
            {
                var rendererExit = GraphicsStartup.Run(Hardware, () => DirectX11Fallback.Run(options, args), () => RunSoftwareDesktop(options),
                    () => game?.ContentLoadingStarted == true, OperatingSystem.IsWindows(), options.SoftwareRendering);
                if (rendererExit is { } code) return code;
            }
            // The window is disposed before this bounded shutdown grace period.
            // Startup verification never delays creating or drawing the UI.
            startupCompanion?.Wait(TimeSpan.FromSeconds(3));
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            var directory = options is { Isolated: true, StateRoot: { } stateRoot } ? Path.Combine(stateRoot, "Logs") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "heirowInstaller", "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "desktop-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}\n{error}\n\n");
            if (!args.Any(value => new[] { "--silent", "--quiet", "/quiet", "/qn", "/silent", "/verysilent", "--defer", "--wizard-smoke", "--ui-smoke", "--ui-performance", "--graphics-handoff-smoke" }.Contains(value, StringComparer.OrdinalIgnoreCase)))
                DesktopPlatform.ShowError($"heirowInstaller could not start.\n\n{error.Message}\n\nDetails: {path}");
            return 1;
        }
    }

    private static void RunSoftwareDesktop(AppArguments options)
    {
#if WINDOWS
        using var form = new CpuInstallerForm(options);
        System.Windows.Forms.Application.Run(form);
#else
        throw new PlatformNotSupportedException("The CPU-rendered compatibility wizard requires Windows.");
#endif
    }

    private static int Maintain(string[] args, AppArguments options)
    {
        var install = new InstallOptions { StateRoot = options.StateRoot, Scope = options.Scope, Isolated = options.Isolated, Silent = options.Silent, InstallerPath = DirectX11Fallback.CurrentPrimaryHostPath, LegacyArguments = options.LegacyArguments, LogPath = options.LogPath };
        var product = InstallerEngine.ListInstalled(options.StateRoot).FirstOrDefault(value => value.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The application is not installed.");
        install.Scope = product.Scope; install.TargetDirectory = product.InstallDirectory; install.Features = options.Features ?? product.Features;
        install.Isolated = product.Isolated; install.RegisterProduct = product.RegisteredProduct;
        install.TrustedPublisherKeySha256 = product.PublisherKeySha256; install.RequireSignature = product.PublisherKeySha256 is not null;
        var engine = new InstallerEngine();
        if (AppController.NeedsMaintenanceElevation(product))
        {
            using var package = PackageReader.Open(product.PackagePath);
            if (args[0] == "repair" && package.IsMetadataOnly)
                return RepairElevatedFilesAsync(product, engine, install).GetAwaiter().GetResult();
            var elevated = ElevatedInstallWorker.InstallAsync(product.PackagePath, engine.Plan(package, install), install,
                SilentApplicationsAsync, null, null, CancellationToken.None, args[0]).GetAwaiter().GetResult();
            return elevated.RebootRequired ? 3010 : 0;
        }
        var result = args[0] == "uninstall" ? engine.UninstallAsync(args[1], install).GetAwaiter().GetResult() : engine.RepairAsync(args[1], install).GetAwaiter().GetResult();
        return result.RebootRequired ? 3010 : 0;
    }

    private static void EnsureRequestedDependencyExited(AppArguments options)
    {
        if (options.WaitForPid is not { } pid) return;
        var running = false;
        try
        {
            using var previous = Process.GetProcessById(pid);
            running = !previous.HasExited && (options.WaitForStartTicks is null || previous.StartTime.ToUniversalTime().Ticks == options.WaitForStartTicks);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        if (running) throw new InvalidOperationException("The requested application is still running. Close it and retry, or run the installer without --silent to review the running applications. Silent installation never closes applications automatically.");
    }

    private static Task<RunningApplicationsChoice> SilentApplicationsAsync(RunningApplicationsRequest request, CancellationToken token)
        => Task.FromException<RunningApplicationsChoice>(new RunningApplicationsException(request.Applications));

    private static async Task<int> RepairElevatedFilesAsync(InstalledProduct product, InstallerEngine engine, InstallOptions options)
    {
        var source = FileUpdateSource.Read(product.PackagePath, product.PublisherKeySha256!);
        var accountOrigin = new Uri("https://socketjack.com/");
        using var session = OperatingSystem.IsWindows() && !product.Isolated
            ? await new HeirowInstaller.Publishing.RememberedPublisherAccount(accountOrigin).RestoreAsync() : null;
        // Staging and authentication stay in this original user's process before approval.
        options.AllowDowngrade = true;
        options.SharedPackageTransport = new HeirowUpdater.SocketJack.SocketJackUpdateTransport(accountOrigin,
            bearerToken: session?.AccessToken ?? Environment.GetEnvironmentVariable("SOCKETJACK_DOWNLOAD_TOKEN"),
            cookie: session is null ? Environment.GetEnvironmentVariable("SOCKETJACK_DOWNLOAD_COOKIE") : null);
        using var client = new FileUpdateClient(allowLoopback: product.Isolated, transport: options.SharedPackageTransport);
        using var stage = await client.PrepareAsync(source.ToFeed(), product, Path.Combine(InstallerPlatform.UserData, "SocketJack", "heirowInstaller", "downloads"), options);
        using var metadata = PackageReader.OpenMetadata(stage.MetadataPath);
        var result = await ElevatedInstallWorker.InstallAsync(stage.MetadataPath, engine.Plan(metadata, options), options,
            SilentApplicationsAsync, null, null, CancellationToken.None, "apply-files", stage.Root);
        return result.RebootRequired ? 3010 : 0;
    }
}

internal sealed record AppArguments(string? PackagePath, string? CapturePath, string? SmokeDirectory, bool Install, string? StateRoot, string? TargetDirectory, string? Scope, List<string>? Features, bool Silent, int? WaitForPid, bool Isolated, bool Launch, bool AcceptLicense, long? WaitForStartTicks, string? TrustedPublisherKeySha256, bool RequireSignature, string? SelfUpdateSmokeFeed, string? SelfUpdateSmokeReport, List<string> LegacyArguments, string? LogPath, string? ExpectedProductId, string? ExpectedVersion, string? ExpectedSha256, bool NoRegister)
{
    internal string? MaintenanceProductId { get; init; }
    internal string? MaintenanceOperation { get; init; }
    internal bool MaintenanceConfirmed { get; init; }
    internal bool CloseOnComplete { get; init; }
    internal bool RenderSmoke { get; init; }
    internal string? WizardSmokeDirectory { get; init; }
    internal string? PerformanceSmokeDirectory { get; init; }
    internal bool SoftwareRendering { get; init; }
    internal bool DirectX11Child { get; init; }
    internal string? GraphicsPrimaryHost { get; init; }
    internal string? GraphicsPrimarySha256 { get; init; }
    internal int? GraphicsParentPid { get; init; }
    internal long? GraphicsParentStart { get; init; }
    internal string? GraphicsPackageSha256 { get; init; }
    internal string? GraphicsHandoffSmokeRoot { get; init; }
    internal bool RequestAdministratorOnOpen { get; init; }
    internal string? RemotePackageUrl { get; init; }
    internal long RemotePackageSize { get; init; }
    internal void ValidatePackageIdentity(PackageReader package)
    {
        if (ExpectedProductId is not null && !package.Manifest.Id.Equals(ExpectedProductId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer package belongs to a different product than the requested update.");
        if (ExpectedVersion is not null && !package.Manifest.Version.Equals(ExpectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The installer package version does not match the requested update.");
        if (ExpectedSha256 is not null && !PackageSafety.HashEquals(PackageSafety.HashFile(package.PackagePath), ExpectedSha256))
            throw new System.Security.Cryptography.CryptographicException("The installer package changed after the update was downloaded.");
    }

    public static AppArguments Parse(string[] args, bool maintenanceInvocation = false)
    {
        string? package = null, capture = null, smoke = null, state = null, target = null, scope = null;
        List<string>? features = null;
        var install = false; var silent = false; var isolated = false; var launch = false; var acceptLicense = false; int? waitForPid = null; long? waitForStartTicks = null;
        string? trustedKey = null, selfUpdateFeed = null, selfUpdateReport = null; var requireSignature = false;
        List<string> legacyArguments = []; string? logPath = null;
        string? expectedProduct = null, expectedVersion = null, expectedSha256 = null; var noRegister = false;
        var renderSmoke = false; var closeOnComplete = false; var maintenanceConfirmed = false; var softwareRendering = false; string? wizardSmoke = null, performanceSmoke = null;
        bool directX11Child = false; string? primaryHost = null, primaryHash = null, graphicsPackageHash = null, handoffSmoke = null; int? graphicsParent = null; long? graphicsStart = null;
        string? remotePackage = null; long remotePackageSize = 0; var requestAdmin = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} requires a value.");
            switch (args[i].ToLowerInvariant())
            {
                case "--request-admin-on-open": requestAdmin = true; break;
                case "--software-rendering": softwareRendering = true; break;
                case "--graphics-directx11-child": directX11Child = true; break;
                case "--graphics-primary-host": primaryHost = Path.GetFullPath(Value()); break;
                case "--graphics-primary-sha256": primaryHash = Value(); break;
                case "--graphics-parent-pid": graphicsParent = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--graphics-parent-start": graphicsStart = long.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--graphics-package": package = Path.GetFullPath(Value()); break;
                case "--graphics-package-sha256": graphicsPackageHash = Value(); break;
                case "--graphics-handoff-smoke": handoffSmoke = Path.GetFullPath(Value()); break;
                case "--render-smoke": capture = Path.GetFullPath(Value()); renderSmoke = true; break;
                case "--capture": capture = Path.GetFullPath(Value()); break;
                case "--ui-smoke": smoke = Path.GetFullPath(Value()); break;
                case "--wizard-smoke": wizardSmoke = Path.GetFullPath(Value()); break;
                case "--ui-performance": performanceSmoke = Path.GetFullPath(Value()); break;
                case "--state-root": state = Path.GetFullPath(Value()); break;
                case "--remote-package": remotePackage = Value(); break;
                case "--remote-package-size": remotePackageSize = long.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--install": install = true; package = Path.GetFullPath(Value()); break;
                case "--target": target = Path.GetFullPath(Value()); break;
                case "--scope": scope = Value(); break;
                case "--features": features = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--wait-for-pid": case "--wait-pid": waitForPid = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--wait-start": waitForStartTicks = long.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--defer": silent = true; break;
                case "--trust-key": trustedKey = Value(); break;
                case "--require-signature": requireSignature = true; break;
                case "--expected-product-id": expectedProduct = Value(); break;
                case "--expected-version": expectedVersion = Value(); break;
                case "--expected-sha256": expectedSha256 = Value(); break;
                case "--no-register": noRegister = true; break;
                case "--close-on-complete": closeOnComplete = true; break;
                case "--maintenance-confirmed": maintenanceConfirmed = true; break;
                case "--self-update-smoke-feed": selfUpdateFeed = Value(); break;
                case "--self-update-smoke-report": selfUpdateReport = Path.GetFullPath(Value()); break;
                case "--backend-arg": legacyArguments.Add(Value()); break;
                case "--log": logPath = Path.GetFullPath(Value()); break;
                case "--isolated": isolated = true; break;
                case "--launch": launch = true; break;
                case "--accept-license": acceptLicense = true; break;
                case "--silent": case "--quiet": case "/quiet": case "/qn": case "/silent": case "/verysilent": silent = true; break;
                case "/suppressmsgboxes": case "/norestart": case "/sp-": break;
                default:
                    if (args[i].StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase)) { target = Path.GetFullPath(args[i][5..].Trim('"')); break; }
                    if (args[i].StartsWith("/LOG=", StringComparison.OrdinalIgnoreCase)) { logPath = Path.GetFullPath(args[i][5..].Trim('"')); legacyArguments.Add("/LOG=" + logPath); break; }
                    if (args[i].StartsWith('-')) throw new ArgumentException($"Unknown desktop option: {args[i]}");
                    package = Path.GetFullPath(args[i]);
                    break;
            }
        }
        if (!maintenanceInvocation && package is null && Environment.ProcessPath is { } self && PackageReader.HasEmbeddedPackage(self)) package = self;
        if (!maintenanceInvocation && package is null && Environment.ProcessPath is { } host)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(host))!;
            var sibling = Path.Combine(directory, "Application.hinst");
            var resource = Path.GetFullPath(Path.Combine(directory, "../Resources/Application.hinst"));
            if (File.Exists(sibling)) package = sibling;
            else if (OperatingSystem.IsMacOS() && directory.EndsWith(".app/Contents/MacOS", StringComparison.Ordinal) && File.Exists(resource)) package = resource;
        }
        if (scope is not null && scope is not ("user" or "machine")) throw new ArgumentException("Scope must be user or machine.");
        if (smoke is not null) { state = Path.Combine(smoke, "state"); target = Path.Combine(smoke, "installed"); isolated = true; }
        if (wizardSmoke is not null)
        {
            if (smoke is not null || package is null && !maintenanceInvocation) throw new ArgumentException("Wizard acceptance requires a package and its own isolated output directory.");
            state = Path.Combine(wizardSmoke, "state"); target = Path.Combine(wizardSmoke, "installed"); isolated = true; noRegister = true;
        }
        if (performanceSmoke is not null)
        {
            if (smoke is not null || wizardSmoke is not null || maintenanceInvocation || package is null || install || silent)
                throw new ArgumentException("UI performance checks require one package and a fresh isolated output directory; they never install it.");
            state = Path.Combine(performanceSmoke, "state"); target = Path.Combine(performanceSmoke, "installed"); isolated = true; noRegister = true;
        }
        if (isolated && state is null) throw new ArgumentException("Isolated installation requires an explicit --state-root.");
        if (waitForPid is <= 0 || waitForPid == Environment.ProcessId) throw new ArgumentException("Choose a different valid process to wait for.");
        if (waitForStartTicks is <= 0 || (waitForStartTicks is not null && waitForPid is null)) throw new ArgumentException("--wait-start requires a valid --wait-pid.");
        if (trustedKey is not null && (trustedKey.Length != 64 || !trustedKey.All(Uri.IsHexDigit))) throw new ArgumentException("--trust-key requires a SHA-256 public-key fingerprint.");
        if ((expectedProduct is null) != (expectedVersion is null)) throw new ArgumentException("--expected-product-id and --expected-version must be supplied together.");
        if (expectedProduct is not null) PackageSafety.ValidateId(expectedProduct);
        if (expectedVersion is not null) _ = PackageVersion.Parse(expectedVersion);
        if (expectedSha256 is not null && (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))) throw new ArgumentException("--expected-sha256 requires a SHA-256 digest.");
        if (remotePackage is not null && (package is not null || maintenanceInvocation || silent || !requireSignature || trustedKey is null ||
            expectedProduct is null || expectedVersion is null || expectedSha256 is null || remotePackageSize is <= 0 or > 16L * 1024 * 1024 * 1024 ||
            !Uri.TryCreate(remotePackage, UriKind.Absolute, out var remoteUri) || remoteUri.UserInfo.Length != 0 || remoteUri.Fragment.Length != 0 ||
            remoteUri.Scheme != "https" && !(isolated && remoteUri.Scheme == "http" && remoteUri.IsLoopback)))
            throw new ArgumentException("Remote setup requires one HTTPS package with an exact publisher, product, version, size and SHA-256 binding, and an interactive wizard.");
        if (selfUpdateFeed is not null && (!isolated || state is null || target is null || selfUpdateReport is null ||
            !Uri.TryCreate(selfUpdateFeed, UriKind.Absolute, out var smokeUri) || !smokeUri.IsLoopback || smokeUri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Self-update acceptance requires an explicit isolated state, target, report, and loopback feed.");
        return new(package, capture, smoke, install, state, target, scope, features, silent, waitForPid, isolated, launch, acceptLicense, waitForStartTicks, trustedKey, requireSignature, selfUpdateFeed, selfUpdateReport, legacyArguments, logPath, expectedProduct, expectedVersion, expectedSha256, noRegister)
        { RenderSmoke = renderSmoke, CloseOnComplete = closeOnComplete, MaintenanceConfirmed = maintenanceConfirmed, WizardSmokeDirectory = wizardSmoke, PerformanceSmokeDirectory = performanceSmoke, SoftwareRendering = softwareRendering,
          DirectX11Child = directX11Child, GraphicsPrimaryHost = primaryHost, GraphicsPrimarySha256 = primaryHash, GraphicsParentPid = graphicsParent, GraphicsParentStart = graphicsStart, GraphicsPackageSha256 = graphicsPackageHash, GraphicsHandoffSmokeRoot = handoffSmoke,
          RemotePackageUrl = remotePackage, RemotePackageSize = remotePackageSize, RequestAdministratorOnOpen = requestAdmin };
    }
}
