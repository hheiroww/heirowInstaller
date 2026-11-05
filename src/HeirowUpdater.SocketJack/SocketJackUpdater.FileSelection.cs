using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

public sealed partial class SocketJackUpdater
{
    public Task<InstallResult> RepairAsync(UpdaterTarget target, InstallOptions options,
        IProgress<InstallProgress>? progress = null, CancellationToken token = default)
    {
        options.SharedPackageTransport = transport;
        return new InstallerEngine().RepairAsync(target.ProductId, options, progress, token);
    }

    public async Task<InstallResult> ApplyPreparedFilesAsync(UpdaterTarget target, string stageRoot, InstallOptions options,
        IProgress<InstallProgress>? progress = null, CancellationToken token = default, RunningApplicationTarget? runningApplications = null)
    {
        var installed = InstallerEngine.ListInstalled(target.StateRoot).Single(product => product.Id.Equals(target.ProductId, StringComparison.OrdinalIgnoreCase) && product.Scope == target.Scope);
        options.SharedPackageTransport = transport;
        using var prepared = PreparedFileUpdate.Open(stageRoot, installed, options);
        return await new InstallerEngine().ApplyPreparedFileUpdateAsync(prepared, options, progress, token, runningApplications);
    }

    private async Task<ProcessStartInfo> PrepareFileSelectionHandoffAsync(UpdaterTarget target, UpdateCheckResult update,
        int sourcePid, long sourceStart, UpdaterPolicy policy, string? key, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        if (key is null) throw new CryptographicException("Selected-file handoff requires the installed publisher pin.");
        var installed = InstallerEngine.ListInstalled(target.StateRoot).SingleOrDefault(product => product.Id.Equals(target.ProductId, StringComparison.OrdinalIgnoreCase) && product.Scope == target.Scope)
            ?? throw new InvalidOperationException("The selected update requires one exact installed application.");
        if (installed.IsMsi || installed.IsExecutable || installed.Version != target.CurrentVersion || !PackageSafety.HashEquals(installed.PublisherKeySha256 ?? "", key) ||
            target.InstallDirectory is not null && !Path.GetFullPath(target.InstallDirectory).Equals(installed.InstallDirectory, InstallerPlatform.PathComparison))
            throw new InvalidDataException("The selected-file handoff differs from the installed native application.");
        using var installedPackage = PackageReader.Open(installed.PackagePath); installedPackage.VerifySignature(key, true);
        var definition = installedPackage.Manifest.UpdateCompanion ?? throw new InvalidDataException("The installed application has no update companion.");
        var profilePath = PackageSafety.UnderRoot(installed.InstallDirectory, definition.ConfigurationPath);
        var profile = ApplicationUpdater.ReadConfiguration(profilePath, requireEntryPoint: false);
        if (profile.SharedRuntime is null) throw new InvalidDataException("Selected-file updates require the verified shared updater.");
        var helper = ApplicationUpdater.ResolveExecutable(profile);
        var downloadDirectory = policy.DownloadDirectory ?? Path.Combine(ApplicationUpdater.StateDirectory(profile), "downloads");
        var options = new InstallOptions { TargetDirectory = installed.InstallDirectory, StateRoot = target.StateRoot, Scope = installed.Scope,
            Features = target.Features, Isolated = installed.Isolated, TrustedPublisherKeySha256 = key, RequireSignature = true, SharedPackageTransport = transport };
        using var prepared = await client.PrepareFileUpdateAsync(update.Feed, installed, downloadDirectory, options, progress, token);
        // Machine-scoped dependency activation is deferred to the elevated helper; it still precedes app shutdown.
        if (installed.Scope != "machine" || InstallerEngine.IsElevated || installed.Isolated)
        {
            using var metadata = PackageReader.OpenMetadata(prepared.MetadataPath);
            await SharedPackages.PrepareAsync(metadata, new InstallerEngine().Plan(metadata, options), options, progress, token);
        }
        var directory = PackageSafety.UnderRoot(ApplicationUpdater.StateDirectory(profile), "handoffs/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installed.InstallDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true };
        void Value(string name, string value) { start.ArgumentList.Add(name); start.ArgumentList.Add(value); }
        start.ArgumentList.Add("apply-files"); start.ArgumentList.Add(prepared.Root);
        Value("--product-id", target.ProductId); Value("--expected-version", update.Feed.Version); Value("--target-manifest-sha256", prepared.Metadata.ManifestSha256);
        Value("--target", installed.InstallDirectory); Value("--scope", installed.Scope); Value("--state-root", profile.StateRoot); Value("--trust-key", key);
        Value("--wait-pid", sourcePid.ToString(CultureInfo.InvariantCulture)); Value("--wait-start", sourceStart.ToString(CultureInfo.InvariantCulture));
        Value("--status-file", Path.Combine(directory, "status.json")); Value("--lifecycle-config", profilePath);
        Value("--lifecycle-state", Path.Combine(ApplicationUpdater.StateDirectory(profile), "state.json")); Value("--lifecycle-ready", Path.Combine(directory, "ready.json"));
        ApplicationUpdater.SetSessionEnvironment(start, session());
        start.ArgumentList.Add("--background");
        if (policy.LogPath is not null) Value("--log", policy.LogPath);
        if (profile.Isolated) start.ArgumentList.Add("--isolated");
        if (target.Scope == "machine" && !InstallerEngine.IsElevated && !profile.Isolated && policy.StageForElevation)
        { start.UseShellExecute = true; start.Verb = "runas"; start.RedirectStandardOutput = false; start.RedirectStandardError = false; }
        return start;
    }
}
