using System.Text.Json;

namespace HeirowInstaller.Core;

internal sealed partial class NativeMigrationSession
{
    private async Task CleanupAfterCommitAsync(IProgress<InstallProgress>? progress, CancellationToken token)
    {
        var pending = 0;
        try { pending += (await owned!.CleanupCommittedAsync(progress, token)).Pending; }
        catch (Exception) { pending++; } // Commit is irreversible; cleanup errors cannot become a rollback/repair transition.
        try
        {
            await Task.Run(DeleteVerifiedMedia, CancellationToken.None);
            pending += journal.StageFiles.Count;
        }
        catch (Exception) { pending++; }
        ReportCleanup(progress, pending);
    }

    internal static async Task RetryCompletedCleanupAsync(InstalledProduct installed, INativeMigrationBackend backend,
        ProductOperationLease lease, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        if (installed.IsMsi || installed.IsExecutable || installed.Scope != "user") return;
        var stateRoot = Path.GetDirectoryName(Path.GetDirectoryName(installed.ReceiptPath))!;
        var directory = JournalDirectory(stateRoot, installed.Id);
        if (!Directory.Exists(Path.Combine(directory, "owned-footprint"))) return;
        try
        {
            // Reconstruct only from independently verified signed cached manifests and the authenticated commit ledger.
            // The plain package-path hint selects a candidate; it grants no cleanup authority.
            var session = await Task.Run(() => OpenCompletedCleanup(installed, backend, lease, stateRoot, directory), token);
            if (session is null) return;
            using (session) await session.CleanupAfterCommitAsync(progress, token);
        }
        catch (Exception) { ReportCleanup(progress, 1); }
    }

    private static NativeMigrationSession? OpenCompletedCleanup(InstalledProduct installed, INativeMigrationBackend backend,
        ProductOperationLease lease, string stateRoot, string directory)
    {
        lease.Validate(installed, stateRoot);
        var saved = ReadJournal(directory);
        if (saved?.Phase != "complete") return null;
        if (saved.FormatVersion != 1 || saved.ProductId != installed.Id || saved.Scope != installed.Scope ||
            !SameRoot(saved.InstallDirectory, installed.InstallDirectory) ||
            !PackageSafety.HashEquals(saved.PublisherKeySha256, installed.PublisherKeySha256 ?? "") ||
            backend is not IInnoOwnedPlatformProvider provider)
            throw new InvalidDataException("Completed cleanup identity differs from this installed native product.");
        var cache = PackageSafety.UnderRoot(stateRoot, installed.Id);
        static string CacheCandidate(string path, string root)
        {
            var full = Path.GetFullPath(path); PackageSafety.EnsureNoReparsePoints(full);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A completed cleanup manifest escapes its product cache.");
            return full;
        }
        using var current = PackageReader.Open(CacheCandidate(installed.PackagePath, cache));
        current.VerifySignature(installed.PublisherKeySha256, requireSignature: true);
        if (current.Manifest.Id != installed.Id || current.Manifest.Version != installed.Version ||
            current.Manifest.LegacyMsi is not null || current.Manifest.LegacyExecutable is not null)
            throw new InvalidDataException("Completed cleanup requires the current signed native receipt.");
        var snapshot = Path.Combine(directory, ReceiptName); PackageSafety.EnsureNoReparsePoints(snapshot);
        if (!File.Exists(snapshot) || new FileInfo(snapshot).Length > 8 * 1024 * 1024 ||
            !PackageSafety.HashEquals(PackageSafety.HashFile(snapshot), saved.OriginalReceiptSha256))
            throw new InvalidDataException("The original cleanup binding snapshot changed.");
        var originalProduct = JsonSerializer.Deserialize<InstalledProduct>(File.ReadAllText(snapshot), InstallerJson.Options)
            ?? throw new InvalidDataException("The original cleanup binding is empty.");
        if (originalProduct.Id != installed.Id || originalProduct.Scope != installed.Scope ||
            originalProduct.Isolated != installed.Isolated || !SameRoot(originalProduct.InstallDirectory, installed.InstallDirectory) ||
            !PackageSafety.HashEquals(originalProduct.PublisherKeySha256 ?? "", installed.PublisherKeySha256 ?? ""))
            throw new InvalidDataException("The original cleanup product/user/root binding differs.");
        using var original = PackageReader.Open(CacheCandidate(originalProduct.PackagePath, cache));
        original.VerifySignature(installed.PublisherKeySha256, requireSignature: true);
        if (!PackageSafety.HashEquals(NativeMigrationValidation.ManifestDigest(original), saved.OriginalManifestSha256))
            throw new InvalidDataException("The original signed cleanup manifest changed.");
        using var committed = PackageReader.Open(CacheCandidate(saved.NativePackagePath ?? installed.PackagePath, cache));
        committed.VerifySignature(installed.PublisherKeySha256, requireSignature: true);
        if (committed.Manifest.Id != installed.Id || !PackageSafety.HashEquals(NativeMigrationValidation.ManifestDigest(committed), saved.TargetManifestSha256))
            throw new InvalidDataException("The signed migration commit differs from the retained cleanup context.");
        var authorization = committed.Manifest.NativeMigration?.Baselines.SingleOrDefault(x => x.Backend == InnoOwnedPolicy.Backend &&
            x.Scope == originalProduct.Scope && x.FromVersion == originalProduct.Version &&
            PackageSafety.HashEquals(x.FromManifestSha256, saved.OriginalManifestSha256))
            ?? throw new InvalidDataException("The signed complete ownership attestation is missing.");
        InnoOwnedPolicy.Bind(original.Manifest, originalProduct, authorization);
        var baseline = new NativeMigrationBaseline(originalProduct, NativeMigrationValidation.Clone(original.Manifest),
            NativeMigrationValidation.Clone(authorization), saved.TargetManifestSha256, saved.OriginalPackageSha256);
        var leasePath = Path.Combine(directory, "migration-stage.lease"); PackageSafety.EnsureNoReparsePoints(leasePath);
        var stageLease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var session = new NativeMigrationSession(baseline, backend, lease, stateRoot, directory, saved, alreadyNative: true, stageLease);
            session.ValidateJournal();
            session.owned = new(baseline, provider.OwnedPlatform(baseline, stateRoot), Path.Combine(directory, "owned-footprint"), saved.OriginalReceiptSha256);
            if (!session.owned.NativeCommitted) throw new InvalidDataException("Backup cleanup has no authenticated native commit marker.");
            session.completed = true;
            return session;
        }
        catch { stageLease.Dispose(); throw; }
    }

    private static bool SameRoot(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static void ReportCleanup(IProgress<InstallProgress>? progress, int pending)
    {
        try
        {
            progress?.Report(new("migration-cleanup", pending == 0
                ? "Known committed migration staging cleaned. Unrecognized objects are preserved."
                : "Native migration is committed. Some migration staging objects were preserved.", pending == 0 ? 1 : 0, 1));
        }
        catch (Exception) { } // A progress callback cannot turn completed installation into a rollback advertisement.
    }
}
