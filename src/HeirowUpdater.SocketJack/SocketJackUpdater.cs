using HeirowInstaller.Core;
using System.Diagnostics;

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

    public static UpdaterTarget FromInstalled(InstalledProduct product, string? stateRoot = null) => new()
    {
        ProductId = product.Id, Name = product.Name, CurrentVersion = product.Version,
        FeedUrl = product.UpdateUrl ?? throw new InvalidOperationException("Product has no update feed."),
        InstallDirectory = product.UseOriginalInstallDirectory ? null : product.InstallDirectory,
        Scope = product.Scope, StateRoot = stateRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(product.ReceiptPath)), PublisherKeySha256 = product.PublisherKeySha256, Features = product.Features
    };
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
    public SocketJackUpdater(bool allowLoopback = false, Uri? credentialOrigin = null, string? cookie = null, string? bearerToken = null)
    {
        client = new(allowLoopback, transport: new SocketJackUpdateTransport(credentialOrigin, cookie, bearerToken));
    }
    public Task<UpdateCheckResult> CheckAsync(UpdaterTarget target, CancellationToken cancellationToken = default) =>
        client.CheckAsync(target.FeedUrl, target.CurrentVersion, target.ProductId, cancellationToken);

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
        policy ??= new();
        if (waitForProcessId <= 0 || waitForStartTicks <= 0) throw new ArgumentException("A process ID and exact start time are required for update handoff.");
        if (!update.Available || !update.Feed.ProductId.Equals(target.ProductId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid handoff update identity.");
        if (target.Scope == "machine" && !InstallerEngine.IsElevated && !policy.Isolated && !policy.StageForElevation) throw new UpdateRequiresElevationException(target.ProductId);
        var key = policy.TrustedPublisherKeySha256 ?? target.PublisherKeySha256;
        if (key is null && !policy.AllowUnsigned) throw new InvalidOperationException("Update handoff requires a trusted publisher key.");
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
        Argument("--maintenance-host", File.Exists(Path.Combine(stage, "heirowInstaller.Cli.exe")) ? Path.Combine(stage, "heirowInstaller.Cli.exe") : Path.Combine(stage, "heirowUpdater.exe"));
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
    private static string? FindInstaller()
    {
        foreach (var directory in UpdaterHostResolver.HostDirectories())
        {
            var cli = Path.Combine(directory, "heirowInstaller.Cli.exe");
            if (File.Exists(cli)) return cli;
            var app = Path.Combine(directory, "heirowInstaller.exe");
            if (File.Exists(app)) return app;
        }
        return UpdaterHostResolver.FindExecutable();
    }
    public void Dispose() => client.Dispose();
}
