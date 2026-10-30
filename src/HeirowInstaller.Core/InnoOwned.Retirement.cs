using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeirowInstaller.Core;

internal sealed record InnoOwnedCleanupResult(int Removed, int Pending);

/// <summary>One protected, per-resource transaction. A registry absence is never its completion marker.</summary>
internal sealed class InnoOwnedRetirement
{
    private readonly NativeMigrationBaseline baseline;
    private readonly InnoOwnedFootprintCatalog catalog;
    private readonly IInnoOwnedPlatform platform;
    private readonly string root, directory, journalPath;
    private readonly byte[] binding;
    private readonly Dictionary<string, Resource> resources;
    private Ledger? ledger;
    internal Action<string>? Fault { get; set; } // Internal fault fixture only, never an InstallOptions switch.
    internal bool NeedsRestoration => ledger?.Phase is "retiring" or "retired" or "restoring";
    internal bool Restored => ledger?.Phase == "restored";
    internal bool NativeCommitted => ledger?.Phase == "native-committed";
    internal bool HasNativeMetadataPlan => ledger?.NativeMetadataPlanned == true;
    internal void ValidateWrapperBeforeStop()
    {
        if (ledger?.Phase is "retired" or "native-committed") return;
        if (ledger?.Phase is "retiring" or "restoring" && baseline.Manifest.UpdateCompanion is { } companion &&
            !File.Exists(PackageSafety.UnderRoot(root, companion.ConfigurationPath))) return;
        VerifyWrapperProfile();
    }

    internal InnoOwnedRetirement(NativeMigrationBaseline baseline, IInnoOwnedPlatform platform, string directory, string receiptHash)
    {
        this.baseline = baseline; catalog = baseline.Authorization.OwnedFootprint!;
        this.platform = platform; this.directory = Path.GetFullPath(directory); journalPath = Path.Combine(this.directory, "ledger.bin");
        root = PackageSafety.UnderRoot(platform.LocalAppData, catalog.RootRelativeToLocalAppData);
        if (platform.IsIsolated != baseline.Product.Isolated || !root.Equals(Path.GetFullPath(baseline.Product.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(platform.UserSid) || !InnoOwnedPolicy.Hash(receiptHash))
            throw new InvalidDataException("Owned retirement requires this exact receipt, current user and known-folder root.");
        resources = Resources().ToDictionary(x => x.Key, StringComparer.Ordinal);
        binding = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", "inno-owned-footprint-journal-v1",
            InnoOwnedPolicy.Digest(catalog), baseline.TargetManifestSha256, baseline.Authorization.FromManifestSha256,
            baseline.OriginalPackageSha256, receiptHash, platform.UserSid, root.ToUpperInvariant(), platform.StateRoot.ToUpperInvariant())));
        platform.ProtectDirectory(this.directory);
        platform.ProtectDirectory(Path.Combine(this.directory, "backups"));
        PackageSafety.EnsureNoReparsePoints(journalPath);
        if (File.Exists(journalPath))
        {
            if (new FileInfo(journalPath).Length is <= 0 or > 12 * 1024 * 1024) throw Repair("The owned ledger exceeds its bound.");
            var plain = platform.Unseal(File.ReadAllBytes(journalPath), binding);
            try { ledger = JsonSerializer.Deserialize<Ledger>(plain, InstallerJson.Options) ?? throw Repair("The owned ledger is empty."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            ValidateLedger();
        }
    }

    internal void ValidateOuterPhase(string phase, bool alreadyNative)
    {
        ValidateLedger();
        if ((phase is "removed" or "complete" || alreadyNative) && ledger?.Phase is not ("retired" or "native-committed"))
            throw Repair("The outer migration has no complete authenticated per-resource retirement marker.");
        if (NativeCommitted && !alreadyNative) throw Repair("A committed footprint cannot be attached to a legacy receipt.");
    }
    internal async Task RetireAsync(IProgress<InstallProgress>? progress, CancellationToken token)
        => await Task.Run(() => Retire(progress, token), token).ConfigureAwait(false);
    private void Retire(IProgress<InstallProgress>? progress, CancellationToken token)
    {
        if (NativeCommitted) throw Repair("A committed footprint cannot retire legacy resources again.");
        progress?.Report(new("migration-backup", "Validating the complete attested legacy footprint.", 0, resources.Count));
        using var namespaces = HoldNamespaces();
        try
        {
            if (ledger?.Phase is "retiring" or "restoring") RestoreCore(progress, CancellationToken.None);
            if (ledger?.Phase == "retired") { ConfirmRetired(); return; }
            if (ledger?.Phase == "restored") PrepareAgain();
            if (ledger is null) Snapshot(progress, token);
            if (ledger!.Phase == "snapshotting") CompleteBackups(progress, token);
            VerifyBeforeRetirement();
            SetPhase("retiring");
            foreach (var entry in ledger.Files)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Before is null) continue;
                entry.RemovalIntent = true; Save(); Checkpoint("remove-intent:" + entry.Key);
                platform.Delete(resources[entry.Key].Path, entry.Before);
                entry.Removed = true; Save(); Checkpoint("removed:" + entry.Key);
                progress?.Report(new("migration-retire", "Retiring attested legacy files and launch links.", ledger.Files.Count(x => x.Removed), ledger.Files.Count));
            }
            token.ThrowIfCancellationRequested();
            ledger.RegistryIntent = true; Save(); Checkpoint("registration-intent");
            platform.RetireRegistry(ledger.InnoBefore);
            ledger.RegistryRemoved = true; Save(); Checkpoint("registration-removed");
            VerifyAllRetired();
            SetPhase("retired"); Checkpoint("retirement-marker");
        }
        catch (Exception failure)
        {
            if (NeedsRestoration)
                try { RestoreCore(progress, CancellationToken.None); }
                catch (Exception restoreFailure) { throw new AggregateException("Owned retirement failed and exact recovery remains pending.", failure, restoreFailure); }
            throw;
        }
    }
    internal async Task RestoreAsync(IProgress<InstallProgress>? progress, CancellationToken token)
        => await Task.Run(() => { using var namespaces = HoldNamespaces(); RestoreCore(progress, token); }, token).ConfigureAwait(false);
    private void RestoreCore(IProgress<InstallProgress>? progress, CancellationToken token)
    {
        ValidateLedger();
        if (NativeCommitted) throw Repair("The native commit is durable; legacy revival is forbidden.");
        if (ledger is null || ledger.Phase is "snapshotting" or "ready" or "restored") return;
        VerifyBackups();
        SetPhase("restoring"); Checkpoint("restore-started");
        // Restore bytes first. Do not re-advertise a complete Inno backend while a file conflict remains unresolved.
        foreach (var entry in ledger.Files.AsEnumerable().Reverse())
        {
            token.ThrowIfCancellationRequested();
            if (!entry.RemovalIntent || entry.Before is null) continue;
            var destination = resources[entry.Key].Path;
            var temporary = destination + ".hinst-inno-restore-" + entry.Id + ".tmp";
            platform.Restore(destination, BackupPath(entry), entry.Before, temporary, entry.RestoreIdentity,
                identity => { entry.RestoreIdentity = identity; Save(); Checkpoint("restore-created:" + entry.Key); });
            entry.Removed = false; Save(); Checkpoint("restored:" + entry.Key);
            progress?.Report(new("migration-restore", "Restoring exact legacy before-images.", ledger.Files.Count(x => x.RemovalIntent && !x.Removed), ledger.Files.Count));
        }
        RestoreSdkMetadata();
        foreach (var entry in ledger.Files)
        {
            var actual = platform.Describe(resources[entry.Key].Path);
            if (!SameBytes(actual, entry.Before)) throw Repair("Restoration did not reproduce the original complete footprint.");
        }
        if (ledger.RegistryIntent)
        { platform.RestoreRegistry(false, ledger.InnoBefore, null); ledger.RegistryRemoved = false; Save(); Checkpoint("registration-restored"); }
        if (!InnoOwnedRegistry.Equal(platform.ReadRegistry(false), ledger.InnoBefore)) throw Repair("The original Inno registration was not restored.");
        SetPhase("restored"); Checkpoint("restore-marker");
    }
    private void PrepareAgain()
    {
        foreach (var entry in ledger!.Files)
        {
            var actual = platform.Describe(resources[entry.Key].Path);
            if (!SameBytes(actual, entry.Before)) throw Repair("The restored footprint changed before a retry.");
            entry.Before = actual; entry.RemovalIntent = false; entry.Removed = false; entry.RestoreIdentity = null;
        }
        if (!InnoOwnedRegistry.Equal(platform.ReadRegistry(false), ledger.InnoBefore) ||
            !InnoOwnedRegistry.Equal(platform.ReadRegistry(true), ledger.SdkBefore)) throw Repair("Restored metadata changed before a retry.");
        ledger.RegistryIntent = ledger.RegistryRemoved = false; ledger.SdkAfter = null; ledger.NativeMetadataPlanned = false;
        SetPhase("ready");
    }
    private void Snapshot(IProgress<InstallProgress>? progress, CancellationToken token)
    {
        ValidateInventory();
        var files = new List<FileEntry>();
        foreach (var resource in resources.Values)
        {
            token.ThrowIfCancellationRequested();
            var actual = platform.Describe(resource.Path);
            if (actual is null && resource.Required || actual is not null &&
                (resource.Size.HasValue && resource.Size != actual.Size || resource.Hash is not null && !PackageSafety.HashEquals(resource.Hash, actual.Sha256) ||
                    resource.Opaque && (actual.Size is <= 0 or > 64L * 1024 * 1024) || resource.Link is not null && (actual.Size is <= 0 or > 64 * 1024) ||
                    resource.Hash is not null && resource.Size is null && actual.Size > 64 * 1024))
                throw Repair("A required attested program, wrapper file or bounded metadata item is missing or changed.");
            files.Add(new() { Key = resource.Key, Id = Guid.NewGuid().ToString("N"), Before = actual });
            progress?.Report(new("migration-inspect", "Checking the attested legacy footprint.", files.Count, resources.Count));
        }
        var inno = platform.ReadRegistry(false); ValidateInnoProfile(inno);
        var sdk = platform.ReadRegistry(true); ValidateSdkBefore(sdk);
        VerifyWrapperProfile();
        var needed = files.Sum(x => x.Before?.Size ?? 0);
        if (!platform.IsIsolated && new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < needed + 64L * 1024 * 1024)
            throw new IOException("Not enough free space for complete legacy recovery before-images.");
        ledger = new() { Files = files, InnoBefore = inno, SdkBefore = sdk };
        Save(); Checkpoint("snapshot-marker");
    }
    private void CompleteBackups(IProgress<InstallProgress>? progress, CancellationToken token)
    {
        foreach (var entry in ledger!.Files)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Before is null) { entry.BackupReady = true; Save(); continue; }
            if (!entry.BackupReady)
            {
                Checkpoint("backup-intent:" + entry.Key);
                platform.Backup(resources[entry.Key].Path, BackupPath(entry), entry.Before, entry.BackupIdentity,
                    identity => { entry.BackupIdentity = identity; Save(); Checkpoint("backup-created:" + entry.Key); });
                entry.BackupReady = true; Save(); Checkpoint("backed-up:" + entry.Key);
            }
            progress?.Report(new("migration-backup", "Preserving verified legacy recovery before-images.", ledger.Files.Count(x => x.BackupReady), ledger.Files.Count));
        }
        VerifyBackups();
        foreach (var entry in ledger.Files.Where(x => resources[x.Key].Link is not null && x.Before is not null))
        {
            var expected = PackageSafety.UnderRoot(root, resources[entry.Key].Link!.Target);
            var link = platform.ReadLink(BackupPath(entry));
            if (link.Arguments != "" || link.IconIndex != 0 || !ExactPath(link.Target, expected) || !ExactPath(link.Icon, expected) ||
                !ExactPath(link.WorkingDirectory, root)) throw Repair("A user shortcut does not independently match its exact attested launch identity.");
        }
        VerifyBackups(); SetPhase("ready"); Checkpoint("backups-complete");
    }
    private void VerifyBeforeRetirement()
    {
        ValidateInventory(); VerifyBackups();
        foreach (var entry in ledger!.Files)
        {
            var actual = platform.Describe(resources[entry.Key].Path);
            if (!SameBytes(actual, entry.Before) || actual?.Identity != entry.Before?.Identity)
                throw Repair("An owned resource changed after backup and before retirement.");
        }
        if (!InnoOwnedRegistry.Equal(platform.ReadRegistry(false), ledger.InnoBefore) ||
            !InnoOwnedRegistry.Equal(platform.ReadRegistry(true), ledger.SdkBefore))
            throw Repair("An integration changed after its complete backup.");
    }
    internal void ConfirmRetired()
    {
        ValidateLedger();
        if (ledger?.Phase != "retired" || !ledger.RegistryRemoved || ledger.Files.Any(x => x.Before is not null && !x.Removed))
            throw Repair("A complete per-resource retirement marker is required before native writes.");
        VerifyBackups(); VerifyAllRetired();
    }
    private void VerifyAllRetired()
    {
        ValidateInventory();
        foreach (var resource in resources.Values)
            if (platform.Describe(resource.Path) is not null) throw Repair("An old owned resource remains after retirement.");
        if (platform.ReadRegistry(false).Exists) throw Repair("The exact Inno registration remains after complete retirement.");
    }
    internal void PlanNativeMetadata(IReadOnlyDictionary<string, object>? after)
    {
        ValidateLedger();
        if (ledger?.Phase != "retired") throw Repair("Native metadata requires complete uncommitted owned retirement.");
        var current = platform.ReadRegistry(true);
        var expectedCurrent = ledger.SdkBefore;
        if (!InnoOwnedRegistry.Equal(current, expectedCurrent) || ledger.SdkBefore.Exists && after is null)
            throw Repair("The SDK identity changed before native metadata or would be disabled.");
        ledger.SdkAfter = InnoOwnedRegistry.FromValues(after);
        ledger.NativeMetadataPlanned = true; Save(); Checkpoint("native-metadata-intent");
        // This exact-key action also retires obsolete optional SDK values. The ordinary registration loop otherwise retains them.
        platform.RestoreRegistry(true, ledger.SdkAfter, expectedCurrent);
        Checkpoint("native-metadata-applied");
    }
    internal void RestoreSdkMetadata()
    {
        ValidateLedger();
        if (NativeCommitted) throw Repair("Committed native metadata cannot be rolled back to legacy.");
        if (ledger is null) throw Repair("SDK recovery has no authenticated owned before-image.");
        platform.RestoreRegistry(true, ledger.SdkBefore, ledger.NativeMetadataPlanned ? ledger.SdkAfter : null);
        Checkpoint("sdk-metadata-restored");
    }
    internal void RecoverNativeMetadata()
    {
        if (ledger?.NativeMetadataPlanned != true || ledger.Phase != "retired")
            throw Repair("Interrupted SDK metadata has no complete retirement and before/after plan.");
        RestoreSdkMetadata();
    }
    internal void MarkNativeCommitted()
    {
        ValidateLedger();
        if (NativeCommitted)
        {
            if (ledger!.SdkAfter is null || !InnoOwnedRegistry.Equal(platform.ReadRegistry(true), ledger.SdkAfter) || platform.ReadRegistry(false).Exists)
                throw Repair("The committed native metadata changed; old backend revival remains forbidden.");
            return;
        }
        if (ledger?.Phase != "retired" || !ledger.NativeMetadataPlanned || ledger.SdkAfter is null ||
            !InnoOwnedRegistry.Equal(platform.ReadRegistry(true), ledger.SdkAfter) || platform.ReadRegistry(false).Exists)
            throw Repair("The native commit does not match its complete retirement and recorded metadata.");
        SetPhase("native-committed"); Checkpoint("native-commit-marker");
    }
    internal Task<InnoOwnedCleanupResult> CleanupCommittedAsync(IProgress<InstallProgress>? progress, CancellationToken token)
        => Task.Run(() => CleanupCommitted(progress, token), CancellationToken.None);
    private InnoOwnedCleanupResult CleanupCommitted(IProgress<InstallProgress>? progress, CancellationToken token)
    {
        ValidateLedger();
        if (!NativeCommitted) throw new InvalidOperationException("Backup cleanup requires the authenticated native commit marker.");
        var removed = 0; var pending = 0;
        // Cleanup never acquires application/link namespaces, writes integrations or changes the irreversible commit phase.
        using var namespaces = platform.HoldDirectories(new[] { directory, Path.Combine(directory, "backups") });
        if (!ledger!.CleanupStarted) { ledger.CleanupStarted = true; Save(); Checkpoint("cleanup-started"); }
        foreach (var entry in ledger.Files)
        {
            if (token.IsCancellationRequested) { pending += ledger.Files.Count(x => !x.BackupCleaned); break; }
            try
            {
                var path = BackupPath(entry); var actual = platform.Describe(path);
                if (actual is null)
                {
                    entry.BackupCleaned = true; Save(); Checkpoint("cleanup-absent:" + entry.Key);
                }
                else if (entry.Before is null || entry.BackupCleaned || !entry.BackupReady || entry.BackupIdentity is null ||
                    !SameBytes(actual, entry.Before) || actual.Identity != entry.BackupIdentity)
                    pending++; // A new/replaced/changed object is preserved; today's hash never creates deletion authority.
                else
                {
                    Checkpoint("cleanup-intent:" + entry.Key);
                    platform.Delete(path, new(entry.Before.Size, entry.Before.Sha256, entry.BackupIdentity));
                    removed++; Checkpoint("cleanup-deleted:" + entry.Key);
                    entry.BackupCleaned = true; Save(); Checkpoint("cleanup-recorded:" + entry.Key);
                }
            }
            catch (Exception failure) when (CleanupFailure(failure)) { pending++; }
            progress?.Report(new("migration-cleanup", "Removing only authenticated backups of the committed migration.", ledger.Files.Count(x => x.BackupCleaned), ledger.Files.Count));
        }
        ledger.CleanupComplete = pending == 0 && ledger.Files.All(x => x.BackupCleaned);
        Save(); Checkpoint("cleanup-finished");
        return new(removed, pending);
    }
    internal static bool CleanupFailure(Exception failure) => failure is IOException or UnauthorizedAccessException or CryptographicException or
        JsonException or InvalidOperationException or System.Security.SecurityException;
    private void VerifyBackups()
    {
        ValidateLedger();
        foreach (var entry in ledger!.Files)
        {
            if (!entry.BackupReady) throw Repair("The owned backup ledger is incomplete.");
            if (entry.Before is null) continue;
            var actual = platform.Describe(BackupPath(entry));
            if (!SameBytes(actual, entry.Before) || actual?.Identity != entry.BackupIdentity)
                throw Repair("An authenticated owned backup is missing, changed or replaced.");
        }
    }
    private void ValidateLedger()
    {
        if (ledger is null) return;
        if (ledger.FormatVersion != 1 || ledger.Phase is not ("snapshotting" or "ready" or "retiring" or "retired" or "restoring" or "restored" or "native-committed") ||
            ledger.Files is null || ledger.Files.Count != resources.Count || ledger.Files.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != resources.Count ||
            ledger.Files.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != resources.Count ||
            ledger.Files.Any(x => !resources.ContainsKey(x.Key) || !Guid.TryParseExact(x.Id, "N", out _) ||
                x.Before is not null && (x.Before.Size < 0 || !InnoOwnedPolicy.Hash(x.Before.Sha256) || string.IsNullOrWhiteSpace(x.Before.Identity))) ||
            ledger.InnoBefore is null || ledger.SdkBefore is null || !ledger.InnoBefore.Exists)
            throw Repair("The protected per-resource ledger differs from the signed attestation.");
        // Re-derive authority from the signed catalog; journal paths never enter the resource allowlist.
        foreach (var entry in ledger.Files)
        {
            var resource = resources[entry.Key];
            if (entry.Before is null && resource.Required || entry.Before is not null &&
                (resource.Size.HasValue && resource.Size != entry.Before.Size || resource.Hash is not null && !PackageSafety.HashEquals(resource.Hash, entry.Before.Sha256)))
                throw Repair("An owned before-image contradicts signed authority.");
        }
        ValidateInnoProfile(ledger.InnoBefore); ValidateSdkBefore(ledger.SdkBefore);
    }
    private void ValidateInnoProfile(InnoOwnedRegistryImage image)
    {
        if (!image.Exists || image.Values.Count > 64 || image.Values.Any(x => !catalog.RegistryValues.Any(r => r.Name.Equals(x.Key, StringComparison.OrdinalIgnoreCase))) ||
            catalog.RegistryValues.Any(rule => !image.Values.ContainsKey(rule.Name) && !rule.Optional))
            throw Repair("The exact Inno key does not match the complete declared profile.");
        foreach (var rule in catalog.RegistryValues)
        {
            if (!image.Values.TryGetValue(rule.Name, out var actual)) continue;
            if (actual.Kind != rule.Kind) throw Repair("An Inno value changed type.");
            var expected = rule.Value.Replace("{InstallDir}", root, StringComparison.Ordinal);
            var valid = rule.Match switch
            {
                "literal" => actual.Data == expected,
                "date" => actual.Kind == "string" && actual.Data.Length == 8 && DateOnly.TryParseExact(actual.Data, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "nonnegative" => int.TryParse(actual.Data, NumberStyles.None, CultureInfo.InvariantCulture, out _),
                "wrapper-visibility" => actual.Data == (baseline.Product.BackendRegistrations.Count == 1 ? "1" : rule.Value),
                _ => false
            };
            if (!valid) throw Repair("An Inno value differs from its typed publisher predicate.");
        }
    }
    private void ValidateSdkBefore(InnoOwnedRegistryImage image)
    {
        if (image.Exists != baseline.Product.RegisteredProduct) throw Repair("The SDK ARP registration differs from the recorded wrapper feature state.");
        if (!image.Exists) return;
        var expected = InnoOwnedRegistry.FromValues(WindowsIntegration.ProductRegistrationValues(baseline.Product, platform.StateRoot, baseline.Manifest));
        if (image.Values.TryGetValue("InstallDate", out var date) && date.Kind == "string" && date.Data.Length == 8 &&
            DateOnly.TryParseExact(date.Data, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            expected.Values["InstallDate"] = date;
        if (!InnoOwnedRegistry.Equal(image, expected)) throw Repair("The SDK ARP identity or maintenance command differs from its verified cached host.");
    }
    private void VerifyWrapperProfile()
    {
        if (baseline.Product.Files.Count == 0) return;
        var companion = baseline.Manifest.UpdateCompanion!;
        var path = PackageSafety.UnderRoot(root, companion.ConfigurationPath);
        if (new FileInfo(path).Length > 64 * 1024 ||
            !PackageSafety.HashEquals(PackageSafety.HashFile(path), baseline.Product.Files.Single(x => x.Path == companion.ConfigurationPath).Sha256))
            throw Repair("The old updater profile exceeds its generated-profile bound.");
        var config = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllText(path), InstallerJson.Options)
            ?? throw Repair("The old wrapper updater profile is empty.");
        if (config.ProductId != baseline.Product.Id || config.CurrentVersion != baseline.Product.Version || config.Scope != "user" ||
            config.Isolated != platform.IsIsolated || !ExactPath(config.InstallDirectory, root) || !ExactPath(config.StateRoot, platform.StateRoot) ||
            !PackageSafety.HashEquals(config.PublisherKeySha256, catalog.PublisherKeySha256) || config.EntryPoint != baseline.Manifest.EntryPoint ||
            config.FeedUrl != baseline.Manifest.UpdateUrl || config.SharedRuntime is not null || config.Mode != companion.Mode ||
            config.CredentialOrigin != companion.CredentialOrigin || config.AuthApplication != companion.AuthApplication ||
            config.TokenEnvironmentVariable != companion.TokenEnvironmentVariable || config.CookieEnvironmentVariable != companion.CookieEnvironmentVariable ||
            config.UpdateMode != companion.UpdateMode || config.LaunchWorkingDirectory != baseline.Manifest.LaunchWorkingDirectory ||
            !config.AffectedResourceFiles.SequenceEqual(RunningApplicationTarget.FromManifest(baseline.Manifest, root, features: baseline.Product.Features).AffectedResourceFiles, StringComparer.OrdinalIgnoreCase) ||
            !config.Features.Order(StringComparer.Ordinal).SequenceEqual(baseline.Product.Features.Order(StringComparer.Ordinal)))
            throw Repair("The generated old updater profile is not bound to this wrapper, user, root and publisher.");
    }
    private IEnumerable<Resource> Resources()
    {
        foreach (var file in catalog.Files) yield return new("app:" + file.Path, PackageSafety.UnderRoot(root, file.Path), file.Required, file.Size, file.Sha256, false, null);
        foreach (var file in catalog.GeneratedMetadata) yield return new("app:" + file.FileName, PackageSafety.UnderRoot(root, file.FileName), file.Required, null, null, true, null);
        foreach (var file in baseline.Product.Files)
        {
            var signed = baseline.Manifest.Payload.SingleOrDefault(x => x.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (signed is not null && !PackageSafety.HashEquals(signed.Sha256, file.Sha256)) throw Repair("The wrapper executable differs from its original signed bytes.");
            yield return new("app:" + file.Path, PackageSafety.UnderRoot(root, file.Path), true, signed?.Size, file.Sha256, false, null);
        }
        for (var index = 0; index < catalog.Shortcuts.Count; index++)
        {
            var link = catalog.Shortcuts[index];
            yield return new("link:" + index.ToString(CultureInfo.InvariantCulture), PackageSafety.UnderRoot(platform.Folder(link.KnownFolder), link.RelativePath), !link.Optional, null, null, false, link);
        }
    }
    private void ValidateInventory()
    {
        var allowed = resources.Values.Where(x => x.Key.StartsWith("app:", StringComparison.Ordinal)).Select(x => x.Key[4..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = catalog.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in allowed.Concat(catalog.PreservedFiles).Concat(catalog.PreservedDirectories).ToArray())
        {
            var parent = path;
            while (parent.LastIndexOf('/') is var slash && slash >= 0) { parent = parent[..slash]; directories.Add(parent); }
        }
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                PackageSafety.EnsureNoReparsePoints(entry);
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    if (catalog.PreservedDirectories.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!directories.Contains(relative)) throw Repair("An unexplained program directory was preserved.");
                    pending.Push(entry);
                }
                else if (!allowed.Contains(relative) && !catalog.PreservedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    throw Repair("An unexplained file cannot be relabeled user data.");
            }
    }
    private IDisposable HoldNamespaces() => platform.HoldDirectories(resources.Values.Select(x => Path.GetDirectoryName(x.Path)!)
        .Append(root).Append(directory).Append(Path.Combine(directory, "backups")).Where(Directory.Exists));
    private string BackupPath(FileEntry entry) => PackageSafety.UnderRoot(directory, "backups/" + entry.Id + ".bin");
    private void SetPhase(string phase) { ledger!.Phase = phase; Save(); }
    private void Save()
    {
        PackageSafety.EnsureNoReparsePoints(journalPath);
        var plain = JsonSerializer.SerializeToUtf8Bytes(ledger, InstallerJson.Options);
        try
        {
            if (plain.Length > 8 * 1024 * 1024) throw Repair("The owned journal exceeds its bound.");
            var bytes = platform.Seal(plain, binding); var temporary = journalPath + ".incoming-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { output.Write(bytes); output.Flush(true); }
                platform.CommitLedger(temporary, journalPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Checkpoint(string name) => Fault?.Invoke(name);
    private static bool SameBytes(InnoOwnedFileState? actual, InnoOwnedFileState? expected) => actual is null ? expected is null :
        expected is not null && actual.Size == expected.Size && PackageSafety.HashEquals(actual.Sha256, expected.Sha256);
    private static bool ExactPath(string actual, string expected) => Path.IsPathFullyQualified(actual) && !actual.Contains('%') &&
        Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static NativeMigrationRepairRequiredException Repair(string message) => new(message);
    private sealed record Resource(string Key, string Path, bool Required, long? Size, string? Hash, bool Opaque, InnoOwnedShortcut? Link);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class Ledger
    {
        public int FormatVersion { get; set; } = 1;
        public string Phase { get; set; } = "snapshotting";
        public List<FileEntry> Files { get; set; } = [];
        public InnoOwnedRegistryImage InnoBefore { get; set; } = new();
        public InnoOwnedRegistryImage SdkBefore { get; set; } = new();
        public bool RegistryIntent { get; set; }
        public bool RegistryRemoved { get; set; }
        public bool NativeMetadataPlanned { get; set; }
        public InnoOwnedRegistryImage? SdkAfter { get; set; }
        public bool CleanupStarted { get; set; }
        public bool CleanupComplete { get; set; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class FileEntry
    {
        public string Key { get; set; } = "";
        public string Id { get; set; } = "";
        public InnoOwnedFileState? Before { get; set; }
        public string? BackupIdentity { get; set; }
        public bool BackupReady { get; set; }
        public bool RemovalIntent { get; set; }
        public bool Removed { get; set; }
        public string? RestoreIdentity { get; set; }
        public bool BackupCleaned { get; set; }
    }
}
