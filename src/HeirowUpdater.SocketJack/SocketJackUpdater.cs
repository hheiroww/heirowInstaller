using HeirowInstaller.Core;
using System.Diagnostics;
using System.Security.Cryptography;

namespace HeirowUpdater.SocketJack;

public sealed class UpdaterTarget
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public string? InstallDirectory { get; set; }
    public string Scope { get; set; } = "user";
    public string? StateRoot { get; set; }
    public string? PublisherKeySha256 { get; set; }
    public List<string>? Features { get; set; }
    public string? UpdateMode { get; set; }

    public static UpdaterTarget FromInstalled(InstalledProduct product, string? stateRoot = null)
    {
        using var package = PackageReader.Open(product.PackagePath);
        package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
        if (!package.Manifest.Id.Equals(product.Id, StringComparison.OrdinalIgnoreCase) || package.Manifest.Version != product.Version)
            throw new InvalidDataException("The update target's cached signed package differs from its installed receipt.");
        var mode = package.Manifest.UpdateCompanion?.UpdateMode;
        if (mode == "files") package.VerifySignature(product.PublisherKeySha256, requireSignature: true);
        return new()
        {
            ProductId = product.Id, Name = product.Name, CurrentVersion = product.Version,
            FeedUrl = product.UpdateUrl ?? throw new InvalidOperationException("Product has no update feed."),
            InstallDirectory = product.UseOriginalInstallDirectory ? null : product.InstallDirectory,
            Scope = product.Scope, StateRoot = stateRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(product.ReceiptPath)), PublisherKeySha256 = product.PublisherKeySha256,
            Features = product.Features, UpdateMode = mode
        };
    }
}

public sealed class UpdaterPolicy
{
    /// <summary>Explicit opt-in for products with no pinned signing key. Hash verification remains mandatory.</summary>
    public bool AllowUnsigned { get; set; }
    public string? TrustedPublisherKeySha256 { get; set; }
    public string? DownloadDirectory { get; set; }
    public bool Isolated { get; set; }
    public string? LogPath { get; set; }
    /// <summary>Allows a verified helper to request UAC after the user explicitly chooses to update.</summary>
    public bool StageForElevation { get; set; }
}

public sealed class UpdaterCheck
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string? LatestVersion { get; set; }
    public bool Available { get; set; }
    public string? Error { get; set; }
    public UpdateCheckResult? Update { get; set; }
}

public sealed class UpdateRequiresElevationException(string productId) : UnauthorizedAccessException("Updating this machine-wide application requires administrator approval.")
{
    public string ProductId { get; } = productId;
}

/// <summary>Reusable application updater using actual SocketJack HTTP networking and the heirowInstaller verification engine.</summary>
public sealed class SocketJackUpdater : IDisposable
{
    private readonly UpdateClient client;
    public SocketJackUpdater(bool allowLoopback = false, Uri? credentialOrigin = null, string? cookie = null, string? bearerToken = null, bool requireIncrementalUpdates = false)
    {
        client = new(allowLoopback, transport: new SocketJackUpdateTransport(credentialOrigin, cookie, bearerToken), requireIncrementalUpdates: requireIncrementalUpdates);
    }
    internal SocketJackUpdater(bool allowLoopback, Uri credentialOrigin, Func<ApplicationSessionSnapshot> session, bool requireIncrementalUpdates = false)
    {
        client = new(allowLoopback, transport: new SocketJackUpdateTransport(credentialOrigin, session), requireIncrementalUpdates: requireIncrementalUpdates);
    }
    public async Task<UpdateCheckResult> CheckAsync(UpdaterTarget target, CancellationToken cancellationToken = default)
    {
        var update = await client.CheckAsync(target.FeedUrl, target.CurrentVersion, target.ProductId, cancellationToken);
        RequireFileUpdate(target, update); return update;
    }

    public async Task<IReadOnlyList<UpdaterCheck>> CheckAllAsync(string? stateRoot = null, CancellationToken cancellationToken = default)
    {
        var results = new List<UpdaterCheck>();
        foreach (var installed in InstallerEngine.ListInstalled(stateRoot).Where(x => !string.IsNullOrWhiteSpace(x.UpdateUrl)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new UpdaterCheck { ProductId = installed.Id, Name = installed.Name, CurrentVersion = installed.Version };
            try
            {
                result.Update = await CheckAsync(UpdaterTarget.FromInstalled(installed, stateRoot), cancellationToken);
                result.LatestVersion = result.Update.Feed.Version; result.Available = result.Update.Available;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { result.Error = ex.Message; }
            results.Add(result);
        }
        return results;
    }

    public async Task<InstallResult> ApplyAsync(UpdaterTarget target, UpdateCheckResult update, UpdaterPolicy? policy = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireFileUpdate(target, update);
        policy ??= new();
        if (!update.Feed.ProductId.Equals(target.ProductId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update does not belong to the selected application.");
        if (!update.Available) throw new InvalidOperationException("Application is already current.");
        if (target.Scope == "machine" && !InstallerEngine.IsElevated && !policy.Isolated) throw new UpdateRequiresElevationException(target.ProductId);
        var key = policy.TrustedPublisherKeySha256 ?? target.PublisherKeySha256;
        if (key is null && !policy.AllowUnsigned) throw new InvalidOperationException("Automatic application updates require a trusted publisher key. Configure --trust-key or explicitly opt in with --allow-unsigned.");
        var download = await client.DownloadAsync(update.Feed, policy.DownloadDirectory ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowUpdater", "downloads"), progress, cancellationToken);
        var result = await client.ApplyAsync(download, update.Feed, new()
        {
            TargetDirectory = target.InstallDirectory, StateRoot = target.StateRoot, Scope = target.Scope,
            Features = target.Features, Silent = true, Launch = false, Isolated = policy.Isolated, LogPath = policy.LogPath,
            TrustedPublisherKeySha256 = key, RequireSignature = key is not null || !policy.AllowUnsigned,
            InstallerPath = FindInstaller()
        }, progress, cancellationToken);
        target.CurrentVersion = update.Feed.Version;
        return result;
    }
    /// <summary>Stages the current trusted updater outside the application directory. The downloaded bundle is read only as signed package data; its launcher is never executed.</summary>
    public async Task<ProcessStartInfo> PrepareInstallerHandoffAsync(UpdaterTarget target, UpdateCheckResult update,
        int waitForProcessId, long waitForStartTicks, UpdaterPolicy? policy = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireFileUpdate(target, update);
        policy ??= new();
        if (waitForProcessId <= 0 || waitForStartTicks <= 0) throw new ArgumentException("A process ID and exact start time are required for update handoff.");
        if (!update.Available || !update.Feed.ProductId.Equals(target.ProductId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid handoff update identity.");
        if (target.Scope == "machine" && !InstallerEngine.IsElevated && !policy.Isolated && !policy.StageForElevation) throw new UpdateRequiresElevationException(target.ProductId);
        var key = policy.TrustedPublisherKeySha256 ?? target.PublisherKeySha256;
        if (key is null && !policy.AllowUnsigned) throw new InvalidOperationException("Update handoff requires a trusted publisher key.");
        if (update.Feed.PackageType.Equals("hupd", StringComparison.OrdinalIgnoreCase))
            return await PrepareIncrementalHandoffAsync(target, update, waitForProcessId, waitForStartTicks, policy, key, progress, cancellationToken);
        var downloadDirectory = policy.DownloadDirectory ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowUpdater", "downloads");
        var stageRoot = policy.Isolated ? downloadDirectory : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowUpdater", "staging");
        var stage = TrustedHelperStaging.NewDirectoryPath(stageRoot, "heirowUpdater.exe", "heirowInstaller.Cli.exe");
        var download = await client.DownloadAsync(update.Feed, downloadDirectory, progress, cancellationToken);
        using (var package = PackageReader.Open(download))
        {
            package.VerifySignature(key, key is not null || !policy.AllowUnsigned);
            await package.VerifyAsync(progress, cancellationToken);
            if (!package.Manifest.Id.Equals(target.ProductId, StringComparison.OrdinalIgnoreCase) || package.Manifest.Version != update.Feed.Version)
                throw new InvalidDataException("Handoff package identity does not match the update feed.");
        }
        var host = UpdaterHostResolver.FindExecutable() ?? throw new FileNotFoundException("The currently trusted heirowUpdater apphost is missing.");
        var hostDirectory = Path.GetDirectoryName(host)!;
        PackageSafety.EnsureNoReparsePoints(stage); Directory.CreateDirectory(stage);
        var names = new[] { "heirowUpdater.exe", "heirowUpdater.deps.json", "heirowUpdater.runtimeconfig.json", "heirowInstaller.Cli.exe", "heirowInstaller.Cli.deps.json", "heirowInstaller.Cli.runtimeconfig.json" }
            .Concat(Directory.EnumerateFiles(hostDirectory, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(hostDirectory, name);
            PackageSafety.EnsureNoReparsePoints(source);
            if (File.Exists(source)) File.Copy(source, PackageSafety.UnderRoot(stage, name));
        }
        var start = new ProcessStartInfo(Path.Combine(stage, "heirowUpdater.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage, RedirectStandardOutput = true, RedirectStandardError = true };
        void Argument(string option, string? value) { if (value is not null) { start.ArgumentList.Add(option); start.ArgumentList.Add(value); } }
        start.ArgumentList.Add("apply-bundle"); start.ArgumentList.Add(download);
        Argument("--sha256", update.Feed.Sha256); Argument("--product-id", target.ProductId); Argument("--expected-version", update.Feed.Version);
        Argument("--wait-pid", waitForProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); Argument("--wait-start", waitForStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Argument("--trust-key", key); Argument("--target", target.InstallDirectory); Argument("--state-root", target.StateRoot); Argument("--scope", target.Scope);
        if (File.Exists(Path.Combine(stage, "heirowInstaller.Cli.exe"))) Argument("--maintenance-host", Path.Combine(stage, "heirowInstaller.Cli.exe"));
        Argument("--log", policy.LogPath);
        Argument("--status-file", Path.Combine(stage, "helper-status.json"));
        start.ArgumentList.Add("--background");
        if (policy.Isolated) start.ArgumentList.Add("--isolated");
        if (policy.AllowUnsigned) start.ArgumentList.Add("--allow-unsigned");
        if (target.Scope == "machine" && !InstallerEngine.IsElevated && !policy.Isolated && policy.StageForElevation)
        {
            start.UseShellExecute = true; start.Verb = "runas";
            start.RedirectStandardOutput = false; start.RedirectStandardError = false;
        }
        return start;
    }
    private async Task<ProcessStartInfo> PrepareIncrementalHandoffAsync(UpdaterTarget target, UpdateCheckResult update,
        int sourcePid, long sourceStart, UpdaterPolicy policy, string? key, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (key is null) throw new CryptographicException("Incremental handoff requires the installed publisher pin.");
        var installed = InstallerEngine.ListInstalled(target.StateRoot).SingleOrDefault(x => x.Id.Equals(target.ProductId, StringComparison.OrdinalIgnoreCase) && x.Scope == target.Scope)
            ?? throw new InvalidOperationException("The incremental update requires its exact installed application.");
        if (installed.IsMsi || installed.IsExecutable || !PackageSafety.HashEquals(installed.PublisherKeySha256 ?? "", key) ||
            (target.InstallDirectory is not null && !Path.GetFullPath(target.InstallDirectory).Equals(Path.GetFullPath(installed.InstallDirectory), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The incremental handoff does not match a native installed application.");
        using var installedPackage = PackageReader.Open(installed.PackagePath);
        installedPackage.VerifySignature(key, requireSignature: true);
        var definition = installedPackage.Manifest.UpdateCompanion ?? throw new InvalidDataException("The installed application has no update companion.");
        var profilePath = PackageSafety.UnderRoot(installed.InstallDirectory, definition.ConfigurationPath);
        var profile = ApplicationUpdater.ReadConfiguration(profilePath, requireEntryPoint: false);
        if (profile.SharedRuntime is null) throw new InvalidDataException("Migrate this application to its verified shared updater before using file updates.");
        var helper = ApplicationUpdater.ResolveExecutable(profile);
        var downloadDirectory = policy.DownloadDirectory ?? Path.Combine(ApplicationUpdater.StateDirectory(profile), "downloads");
        var download = await client.DownloadAsync(update.Feed, downloadDirectory, progress, cancellationToken);
        using (var delta = IncrementalUpdateReader.Open(download))
        {
            delta.VerifySignature(key, requireSignature: true);
            await delta.VerifyAsync(progress, cancellationToken);
            var transition = delta.Transition;
            if (transition.ProductId != target.ProductId || transition.FromVersion != installed.Version || transition.FromVersion != update.Feed.BaseVersion ||
                transition.ToVersion != update.Feed.Version || !PackageSafety.HashEquals(transition.BaseManifestSha256, update.Feed.BaseManifestSha256 ?? "") ||
                !PackageSafety.HashEquals(transition.TargetManifestSha256, update.Feed.TargetManifestSha256 ?? ""))
                throw new InvalidDataException("The incremental handoff's signed base or target differs from the feed and installed receipt.");
        }
        var directory = PackageSafety.UnderRoot(ApplicationUpdater.StateDirectory(profile), "handoffs/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installed.InstallDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true };
        void Value(string name, string value) { start.ArgumentList.Add(name); start.ArgumentList.Add(value); }
        start.ArgumentList.Add("apply-incremental"); start.ArgumentList.Add(download);
        Value("--sha256", update.Feed.Sha256); Value("--file-size", update.Feed.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Value("--product-id", target.ProductId); Value("--expected-version", update.Feed.Version); Value("--base-version", update.Feed.BaseVersion!);
        Value("--base-manifest-sha256", update.Feed.BaseManifestSha256!); Value("--target-manifest-sha256", update.Feed.TargetManifestSha256!);
        Value("--target", installed.InstallDirectory); Value("--scope", installed.Scope); Value("--state-root", profile.StateRoot); Value("--trust-key", key);
        Value("--wait-pid", sourcePid.ToString(System.Globalization.CultureInfo.InvariantCulture)); Value("--wait-start", sourceStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Value("--status-file", Path.Combine(directory, "status.json"));
        Value("--lifecycle-config", profilePath); Value("--lifecycle-state", Path.Combine(ApplicationUpdater.StateDirectory(profile), "state.json"));
        Value("--lifecycle-ready", Path.Combine(directory, "ready.json"));
        ApplicationUpdater.SetSessionEnvironment(start, null, null, replace: false); // The local data helper performs no authenticated network request.
        start.ArgumentList.Add("--background");
        if (policy.LogPath is not null) Value("--log", policy.LogPath);
        if (profile.Isolated) start.ArgumentList.Add("--isolated");
        if (target.Scope == "machine" && !InstallerEngine.IsElevated && !profile.Isolated && policy.StageForElevation)
        {
            start.UseShellExecute = true; start.Verb = "runas"; start.RedirectStandardOutput = false; start.RedirectStandardError = false;
        }
        return start;
    }
    private static void RequireFileUpdate(UpdaterTarget target, UpdateCheckResult update)
    {
        if (target.UpdateMode == "files" && update.Available && !update.Feed.PackageType.Equals("hupd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This installed application requires signed file updates; a full installer is not an update fallback.");
    }
    private static string? FindInstaller()
    {
        foreach (var directory in UpdaterHostResolver.HostDirectories())
        {
            var cli = Path.Combine(directory, "heirowInstaller.Cli.exe");
            if (File.Exists(cli)) return cli;
            var app = Path.Combine(directory, "heirowInstaller.exe");
            if (File.Exists(app)) return app;
        }
        return null; // A shared updater is not the application's GUI maintenance host; preserve the verified previous host.
    }
    public void Dispose() => client.Dispose();
}
