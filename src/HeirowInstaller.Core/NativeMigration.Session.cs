using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

public sealed class NativeMigrationRestartRequiredException : IOException
{
    public NativeMigrationRestartRequiredException() : base("Restart Windows before continuing this native migration, then run the same installer again. The original recovery package and receipt have been preserved; no native application files were installed.") { }
}

public sealed class NativeMigrationRepairRequiredException : IOException
{
    public NativeMigrationRepairRequiredException(string message, Exception? inner = null) : base(message + " The original signed media and migration journal have been preserved for repair.", inner) { }
}

internal sealed partial class NativeMigrationSession : IDisposable
{
    private const string JournalName = "migration.json", ReceiptName = "original-receipt.json";
    private readonly NativeMigrationBaseline baseline;
    private readonly INativeMigrationBackend backend;
    private readonly ProductOperationLease lease;
    private readonly string stateRoot, directory;
    private readonly bool alreadyNative;
    private readonly FileStream stageLease;
    private Journal journal;
    private InnoOwnedRetirement? owned;
    private bool completed;
    internal bool BackendWasRemoved => !alreadyNative && journal.Phase == "removed";
    internal bool NeedsRestoration => !alreadyNative && !completed && (owned?.NeedsRestoration ?? BackendWasRemoved);
    internal bool CanFinalizeNativeCommit => owned is not null && alreadyNative;
    private string Media => Path.Combine(directory, "media");

    private NativeMigrationSession(NativeMigrationBaseline baseline, INativeMigrationBackend backend, ProductOperationLease lease,
        string stateRoot, string directory, Journal journal, bool alreadyNative, FileStream stageLease)
    { this.baseline = baseline; this.backend = backend; this.lease = lease; this.stateRoot = stateRoot; this.directory = directory; this.journal = journal; this.alreadyNative = alreadyNative; this.stageLease = stageLease; }

    internal static async Task<NativeMigrationSession?> OpenAsync(PackageReader target, InstallPlan plan, InstalledProduct? previous,
        InstallOptions options, INativeMigrationBackend backend, ProductOperationLease lease, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        var stateRoot = Path.GetFullPath(options.StateRoot ?? InstallerEngine.DefaultStateRoot(plan.Scope));
        var directory = JournalDirectory(stateRoot, target.Manifest.Id);
        PackageSafety.EnsureNoReparsePoints(directory);
        var existing = ReadJournal(directory);
        if (existing?.Phase == "complete")
        {
            if (previous is { IsMsi: true } or { IsExecutable: true })
                throw new NativeMigrationRepairRequiredException("A completed migration conflicts with a newly recorded legacy baseline.");
            if (previous is not null) await RetryCompletedCleanupAsync(previous, backend, lease, progress, token);
            return null;
        }
        var alreadyNative = previous is not null && !previous.IsMsi && !previous.IsExecutable;
        var original = previous;
        if (alreadyNative && existing is not null)
        {
            if (previous!.Version != target.Manifest.Version || !PackageSafety.HashEquals(NativeMigrationValidation.ManifestDigest(target), existing.TargetManifestSha256))
                throw new NativeMigrationRepairRequiredException("Resume the exact installer that began this native migration before applying another update.");
            var receiptPath = Path.Combine(directory, ReceiptName);
            if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 8 * 1024 * 1024 || !PackageSafety.HashEquals(PackageSafety.HashFile(receiptPath), existing.OriginalReceiptSha256))
                throw new NativeMigrationRepairRequiredException("The migration's original receipt snapshot is missing or changed.");
            original = JsonSerializer.Deserialize<InstalledProduct>(File.ReadAllText(receiptPath), InstallerJson.Options)
                ?? throw new NativeMigrationRepairRequiredException("The migration's original receipt snapshot is invalid.");
            if (!Path.GetFullPath(original.PackagePath).StartsWith(PackageSafety.UnderRoot(stateRoot, original.Id) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new NativeMigrationRepairRequiredException("The migration's original repair package escapes its product cache.");
        }
        var baseline = NativeMigrationValidation.ForPlan(target, plan, original, options, backend);
        if (baseline is null)
        {
            if (existing is not null && existing.Phase != "restored") throw new NativeMigrationRepairRequiredException("A pending native migration does not match this operation.");
            return null;
        }
        lease.Validate(baseline.Product, stateRoot);
        using (var originalInput = new FileStream(baseline.Product.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            baseline = baseline with { OriginalPackageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(originalInput, token)) };
        // Owned retirement never queues reboot-time actions and reconciles its complete ledger, not a boot/ARP inference.
        var bootIdentity = existing?.BootIdentity ?? (baseline.Authorization.Backend == InnoOwnedPolicy.Backend
            ? "owned-footprint:no-delayed-actions" : await backend.BootIdentityAsync(token));
        Directory.CreateDirectory(directory);
        var leasePath = Path.Combine(directory, "migration-stage.lease"); PackageSafety.EnsureNoReparsePoints(leasePath);
        var stageLease = new FileStream(leasePath, existing is null ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        NativeMigrationSession? session = null;
        try
        {
        if (existing is null)
        {
            var receipt = File.ReadAllBytes(baseline.Product.ReceiptPath);
            if (receipt.Length > 8 * 1024 * 1024) throw new InvalidDataException("The recorded legacy receipt is too large.");
            WriteDurable(Path.Combine(directory, ReceiptName), receipt);
            existing = new()
            {
                ProductId = baseline.Product.Id, Scope = baseline.Product.Scope, InstallDirectory = Path.GetFullPath(plan.InstallDirectory),
                PublisherKeySha256 = baseline.Product.PublisherKeySha256!, OriginalManifestSha256 = baseline.Authorization.FromManifestSha256,
                OriginalPackageSha256 = baseline.OriginalPackageSha256, OriginalReceiptSha256 = IncrementalPackageIO.Hash(receipt),
                TargetManifestSha256 = baseline.TargetManifestSha256, BootIdentity = bootIdentity, Phase = "preparing"
            };
            WriteDurable(Path.Combine(directory, JournalName), JsonSerializer.SerializeToUtf8Bytes(existing, InstallerJson.Options));
        }
        session = new NativeMigrationSession(baseline, backend, lease, stateRoot, directory, existing, alreadyNative, stageLease);
        session.ValidateJournal();
        if (baseline.Authorization.Backend == InnoOwnedPolicy.Backend)
        {
            var provider = (IInnoOwnedPlatformProvider)backend;
            session.owned = await Task.Run(() => new InnoOwnedRetirement(baseline, provider.OwnedPlatform(baseline, stateRoot),
                Path.Combine(directory, "owned-footprint"), existing.OriginalReceiptSha256), token).ConfigureAwait(false);
        }
        await session.RequireRestartBoundaryAsync(token);
        if (existing.Phase == "preparing")
        {
            progress?.Report(new("migration", "Preparing verified legacy recovery media.", 0, 1));
            using var package = PackageReader.Open(baseline.Product.PackagePath);
            package.VerifySignature(baseline.Product.PublisherKeySha256, requireSignature: true);
            await package.VerifyAsync(progress, token);
            await NativeMigrationStage.PrepareAsync(session.Media, baseline, existing.StageFiles, () => session.SetPhase("preparing"), progress, token);
            session.SetPhase("prepared");
        }
        return session;
        }
        catch { if (session is null) stageLease.Dispose(); else session.Dispose(); throw; }
    }

    internal async Task RetireAsync(InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        ValidateJournal(); await RequireRestartBoundaryAsync(token);
        await VerifyMediaAsync(token);
        if (owned is not null)
        {
            if (alreadyNative)
            {
                if (backend.Observe(baseline) != NativeBackendState.Absent) throw Repair("The old registration reappeared after native commit.");
                owned.MarkNativeCommitted(); return;
            }
            await EnsureClosedAsync(options, token);
            if (backend.Observe(baseline) == NativeBackendState.Uncertain) throw Repair("The exact user-only backend scope is ambiguous.");
            SetPhase("removal-started");
            try { await owned.RetireAsync(progress, token); }
            catch
            {
                SetPhase(owned.NeedsRestoration ? "repair-required" : owned.Restored ? "restored" : "prepared");
                throw;
            }
            SetPhase("removed");
            progress?.Report(new("migration", "The complete attested footprint is retired. Installing native files.", 1, 1));
            return;
        }
        var state = backend.Observe(baseline);
        if (alreadyNative || journal.Phase == "removed")
        {
            if (state != NativeBackendState.Absent) throw Repair("The legacy backend is still registered after its recorded removal.");
            return;
        }
        if (state != NativeBackendState.Installed) throw Repair("The recorded legacy backend is missing or ambiguous before retirement. Repair it before migrating.");
        await EnsureClosedAsync(options, token);
        journal.BootIdentity = await backend.BootIdentityAsync(token);
        SetPhase("removal-started");
        progress?.Report(new("migration", "Retiring the verified legacy installer; preserving its recovery media.", 0, 1));
        int exit;
        try { exit = await backend.UninstallAsync(baseline, Media, options, token); }
        catch (Exception failure)
        {
            SetPhase("repair-required");
            throw new NativeMigrationRepairRequiredException("The legacy uninstaller did not complete with a verified result. Resume only after a trusted repair or restart.", failure);
        }
        journal.LastExitCode = exit;
        if (exit is 3010 or 1641) { SetPhase("await-restart"); throw new NativeMigrationRestartRequiredException(); }
        if (exit != 0 || backend.Observe(baseline) != NativeBackendState.Absent)
            throw Repair("The legacy uninstaller did not conclusively remove the exact registered backend.");
        SetPhase("removed");
        progress?.Report(new("migration", "Legacy backend retired. Installing the native application.", 1, 1));
    }

    internal async Task RestoreAsync(InstallOptions options, CancellationToken token)
    {
        if (owned is not null)
        {
            if (!NeedsRestoration) return;
            await EnsureClosedAsync(options, token); await VerifyMediaAsync(token);
            await owned.RestoreAsync(null, token);
            SetPhase(owned.Restored ? "restored" : "prepared");
            return;
        }
        if (!BackendWasRemoved || completed) return;
        await EnsureClosedAsync(options, token); await VerifyMediaAsync(token);
        if (backend.Observe(baseline) != NativeBackendState.Absent) throw Repair("The backend changed before rollback; automatic restoration was stopped.");
        journal.BootIdentity = await backend.BootIdentityAsync(token); SetPhase("restore-started");
        int exit;
        try { exit = await backend.RestoreAsync(baseline, Media, options, token); }
        catch (Exception failure) { throw Repair("Restoring the original signed backend requires repair.", failure); }
        journal.LastExitCode = exit;
        if (exit is 3010 or 1641) { SetPhase("restore-restart"); throw new NativeMigrationRestartRequiredException(); }
        if (exit != 0 || backend.Observe(baseline) != NativeBackendState.Installed)
            throw Repair("The original backend was not conclusively restored.");
        SetPhase("restored");
    }

    internal void ConfirmRetired()
    {
        ValidateJournal();
        if (owned is not null && !alreadyNative) owned.ConfirmRetired();
        if (owned is not null && alreadyNative && !owned.NativeCommitted) throw Repair("Native recovery lacks its durable footprint commit marker.");
        if (backend.Observe(baseline) != NativeBackendState.Absent)
            throw Repair("The exact legacy backend reappeared before native file changes.");
    }

    internal async Task CompleteAsync(InstalledProduct installed, IProgress<InstallProgress>? progress = null, CancellationToken token = default)
    {
        if (owned is null) { await CompleteSynchronously(installed); return; }
        await Task.Run(() => Complete(installed));
        await CleanupAfterCommitAsync(progress, token);
    }
    private Task CompleteSynchronously(InstalledProduct installed) { Complete(installed); return Task.CompletedTask; }
    private void Complete(InstalledProduct installed)
    {
        lease.Validate(baseline.Product, stateRoot);
        if (installed.IsMsi || installed.IsExecutable || installed.Id != baseline.Product.Id || installed.Scope != baseline.Product.Scope ||
            !Path.GetFullPath(installed.InstallDirectory).Equals(Path.GetFullPath(baseline.Product.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
            backend.Observe(baseline) != NativeBackendState.Absent)
            throw Repair("The native commit does not match its retired backend identity.");
        using var target = PackageReader.Open(installed.PackagePath);
        target.VerifySignature(baseline.Product.PublisherKeySha256, requireSignature: true);
        if (!PackageSafety.HashEquals(NativeMigrationValidation.ManifestDigest(target), journal.TargetManifestSha256))
            throw Repair("The committed native receipt differs from this migration.");
        owned?.MarkNativeCommitted();
        if (owned is not null) journal.NativePackagePath = installed.PackagePath;
        SetPhase("complete"); completed = true;
        if (owned is null) DeleteVerifiedMedia();
    }

    internal async Task FinalizeNativeCommitAsync(InstalledProduct installed, FileTransactionRecoveryContext context,
        IProgress<InstallProgress>? progress, CancellationToken token)
    {
        if (!CanFinalizeNativeCommit) throw Repair("This is not a late native commit recovery.");
        progress?.Report(new("migration-recover", "Verifying the already committed native application.", 0, 1));
        if (!await Task.Run(() => FileTransactionRecovery.TargetIsInstalled(context), token))
            throw Repair("The recorded native target is incomplete or changed; legacy revival remains forbidden.");
        await CompleteAsync(installed, progress, token);
        progress?.Report(new("complete", "The native migration commit was recovered.", 1, 1));
    }

    internal void RequireRepair(string message) => SetPhase("repair-required");

    internal static void GuardMaintenance(InstalledProduct product)
    {
        var stateRoot = Path.GetDirectoryName(Path.GetDirectoryName(product.ReceiptPath))!;
        var journal = ReadJournal(JournalDirectory(stateRoot, product.Id));
        if (journal is not null && journal.Phase is not ("complete" or "restored"))
            throw new NativeMigrationRepairRequiredException("Resume the exact native installer before performing separate repair or uninstall maintenance.");
    }

    private async Task RequireRestartBoundaryAsync(CancellationToken token)
    {
        if (owned is not null)
        {
            owned.ValidateOuterPhase(journal.Phase, alreadyNative);
            return; // No ARP/boot inference can replace the owned per-resource retirement marker.
        }
        if (journal.Phase is not ("removal-started" or "await-restart" or "restore-started" or "restore-restart" or "repair-required")) return;
        var boot = await backend.BootIdentityAsync(token);
        if (boot == journal.BootIdentity) throw new NativeMigrationRestartRequiredException();
        var state = backend.Observe(baseline);
        if (journal.Phase is "restore-started" or "restore-restart")
        {
            if (state != NativeBackendState.Installed) throw Repair("The restarted system cannot confirm restoration of the original backend.");
            journal.LastExitCode = null; journal.BootIdentity = boot; SetPhase("restored");
        }
        else if (state == NativeBackendState.Absent) { journal.BootIdentity = boot; SetPhase("removed"); }
        else if (state == NativeBackendState.Installed && journal.Phase == "repair-required")
        { journal.LastExitCode = null; journal.BootIdentity = boot; SetPhase("prepared"); }
        else throw Repair("The restarted system cannot conclusively confirm the exact legacy backend's removal.");
    }

    private async Task EnsureClosedAsync(InstallOptions options, CancellationToken token) =>
        await RunningApplications.EnsureClosedAsync(RunningApplicationTarget.FromManifest(baseline.Manifest, baseline.Product.InstallDirectory,
            "migrate", baseline.Product.Features, baseline.Product.Scope, baseline.Product.Isolated), options.RunningApplicationsPrompt, token);

    internal Task PlanNativeMetadataAsync(IReadOnlyDictionary<string, object>? values, CancellationToken token)
    {
        var retirement = owned;
        return retirement is null ? Task.CompletedTask : Task.Run(() => retirement.PlanNativeMetadata(values), token);
    }
    internal Task ValidateWrapperBeforeStopAsync(CancellationToken token)
    {
        var retirement = owned;
        return retirement is null ? Task.CompletedTask : Task.Run(retirement.ValidateWrapperBeforeStop, token);
    }
    internal async Task RecoverNativeTransactionsAsync(FileTransactionRecoveryContext context, InstallOptions options, CancellationToken token)
    {
        if (owned is null)
        { await FileTransactionRecovery.RecoverAsync(context, options.RunningApplicationsPrompt, operationLockHeld: true, cancellationToken: token); return; }
        if (context.TargetManifestSha256 != baseline.TargetManifestSha256 || context.ProductId != baseline.Product.Id ||
            context.Scope != baseline.Product.Scope || !PackageSafety.HashEquals(context.PublisherKeySha256!, baseline.Product.PublisherKeySha256!) ||
            !alreadyNative && !PackageSafety.HashEquals(context.BaseManifestSha256 ?? "", baseline.Authorization.FromManifestSha256) ||
            !Path.GetFullPath(context.InstallDirectory).Equals(Path.GetFullPath(baseline.Product.InstallDirectory), StringComparison.OrdinalIgnoreCase))
            throw Repair("Native recovery differs from the verified owned migration.");
        // Target validation forbids every external integration except this explicitly snapshotted SDK ARP key.
        var metadataRecovered = false;
        await FileTransactionRecovery.RecoverOwnedAsync(context, () => { owned.RecoverNativeMetadata(); metadataRecovered = true; }, options.RunningApplicationsPrompt, token);
        // A crash can happen during the owned metadata action before the ordinary file journal records any external metadata.
        if (!alreadyNative && !owned.NativeCommitted && owned.HasNativeMetadataPlan && !metadataRecovered)
            await Task.Run(owned.RecoverNativeMetadata, token);
    }

    private Task VerifyMediaAsync(CancellationToken token) => Task.Run(() =>
    {
        lease.Validate(baseline.Product, stateRoot); ValidateJournal();
        if (journal.StageFiles is null || journal.StageFiles.Count != baseline.Manifest.Payload.Count ||
            journal.StageFiles.Select(intent => intent.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.StageFiles.Count)
            throw new NativeMigrationRepairRequiredException("The original media creation intents are incomplete or ambiguous.");
        foreach (var file in baseline.Manifest.Payload)
        {
            token.ThrowIfCancellationRequested();
            var path = PackageSafety.UnderRoot(Media, file.Path); PackageSafety.EnsureNoReparsePoints(path);
            var intent = journal.StageFiles.SingleOrDefault(candidate => candidate.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            using var held = LegacyStagingFileGuard.File(path, file.Size, file.Sha256);
            if (intent is null || held.IdentityToken != intent.Identity)
                throw new NativeMigrationRepairRequiredException("The original recovery media changed identity; it was preserved.");
        }
    }, token);

    private void ValidateJournal()
    {
        lease.Validate(baseline.Product, stateRoot); PackageSafety.EnsureNoReparsePoints(directory);
        if (journal.FormatVersion != 1 || journal.ProductId != baseline.Product.Id || journal.Scope != baseline.Product.Scope ||
            !Path.GetFullPath(journal.InstallDirectory).Equals(Path.GetFullPath(baseline.Product.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
            !PackageSafety.HashEquals(journal.PublisherKeySha256, baseline.Product.PublisherKeySha256!) ||
            !PackageSafety.HashEquals(journal.OriginalManifestSha256, baseline.Authorization.FromManifestSha256) ||
            !PackageSafety.HashEquals(journal.TargetManifestSha256, baseline.TargetManifestSha256) ||
            !PackageSafety.HashEquals(journal.OriginalPackageSha256, baseline.OriginalPackageSha256) || string.IsNullOrWhiteSpace(journal.BootIdentity) || journal.BootIdentity.Length > 128 ||
            journal.Phase is not ("preparing" or "prepared" or "restored" or "removal-started" or "await-restart" or "removed" or "restore-started" or "restore-restart" or "repair-required" or "complete"))
            throw new NativeMigrationRepairRequiredException("The migration journal differs from the independently verified signed baseline and target.");
        var snapshot = Path.Combine(directory, ReceiptName);
        if (!File.Exists(snapshot) || new FileInfo(snapshot).Length > 8 * 1024 * 1024 || !PackageSafety.HashEquals(PackageSafety.HashFile(snapshot), journal.OriginalReceiptSha256))
            throw new NativeMigrationRepairRequiredException("The original receipt snapshot changed during migration.");
    }

    private NativeMigrationRepairRequiredException Repair(string message, Exception? inner = null)
    { SetPhase("repair-required"); return new(message, inner); }
    private void SetPhase(string phase)
    {
        lease.Validate(baseline.Product, stateRoot); journal.Phase = phase;
        WriteDurable(Path.Combine(directory, JournalName), JsonSerializer.SerializeToUtf8Bytes(journal, InstallerJson.Options));
    }
    private void DeleteVerifiedMedia()
    {
        NativeMigrationStage.Cleanup(Media, baseline.Manifest, journal.StageFiles, () => SetPhase("complete"));
    }
    private static Journal? ReadJournal(string directory)
    {
        PackageSafety.EnsureNoReparsePoints(directory);
        var path = Path.Combine(directory, JournalName);
        if (!Directory.Exists(directory)) return null;
        if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) throw new NativeMigrationRepairRequiredException("The native migration journal is missing or too large.");
        try { return JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), InstallerJson.Options) ?? throw new InvalidDataException(); }
        catch (JsonException failure) { throw new NativeMigrationRepairRequiredException("The native migration journal cannot be read.", failure); }
    }
    private static string JournalDirectory(string stateRoot, string productId) => PackageSafety.UnderRoot(stateRoot, ".native-migrations/" + productId + "/current");
    private static void WriteDurable(string path, byte[] bytes)
    {
        PackageSafety.EnsureNoReparsePoints(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".incoming-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => stageLease.Dispose();
    private sealed class Journal
    {
        public int FormatVersion { get; set; } = 1;
        public string ProductId { get; set; } = "";
        public string Scope { get; set; } = "";
        public string InstallDirectory { get; set; } = "";
        public string PublisherKeySha256 { get; set; } = "";
        public string OriginalManifestSha256 { get; set; } = "";
        public string OriginalPackageSha256 { get; set; } = "";
        public string OriginalReceiptSha256 { get; set; } = "";
        public string TargetManifestSha256 { get; set; } = "";
        public string? NativePackagePath { get; set; }
        public string BootIdentity { get; set; } = "";
        public string Phase { get; set; } = "prepared";
        public int? LastExitCode { get; set; }
        public List<NativeMigrationStageFile> StageFiles { get; set; } = [];
    }
}
