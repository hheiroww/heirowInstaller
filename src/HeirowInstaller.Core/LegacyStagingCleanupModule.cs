using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>One-time cleanup of explicitly attested legacy staging; unknown or changed data is always preserved.</summary>
public sealed class LegacyStagingCleanupModule : IInstallerModule
{
    public const string ModuleId = "legacy-staging-cleanup";
    public string Id => ModuleId;

    public IReadOnlyList<string> Plan(JsonElement configuration, InstallerModuleContext context)
    {
        var definition = Authorize(configuration, context);
        if (!context.IsFeatureSelected(definition.Feature)) return [];
        return definition.Trees.Select(tree => tree.Kind == "completed-backup"
            ? "Remove only unchanged, attested legacy backup: " + tree.Leaf
            : "Quarantine only unchanged, unproven legacy staging for review: " + tree.Leaf).ToArray();
    }

    public async Task<ModuleSnapshot> InstallAsync(JsonElement configuration, InstallerModuleContext context, CancellationToken cancellationToken)
    {
        var definition = Authorize(configuration, context);
        var snapshot = new ModuleSnapshot { Key = ModuleId };
        if (!context.IsFeatureSelected(definition.Feature)) return snapshot;
        if (!context.ProductLockHeld) throw new InvalidOperationException("Legacy cleanup requires the engine's product operation lock.");
        var authority = context.Authority!;
        var recovery = context.Transaction.RecoveryContext ?? throw new InvalidOperationException("Legacy cleanup requires a durable file transaction.");
        if (recovery.ProductId != authority.ProductId || recovery.Scope != authority.Scope ||
            !Path.GetFullPath(recovery.StateRoot).Equals(authority.StateRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(recovery.InstallDirectory).Equals(authority.InstallRoot, StringComparison.OrdinalIgnoreCase) ||
            !PackageSafety.HashEquals(recovery.TargetManifestSha256, authority.ManifestSha256) ||
            !PackageSafety.HashEquals(recovery.PublisherKeySha256 ?? "", authority.PublisherPin))
            throw new CryptographicException("Legacy cleanup recovery authority differs from its signed product.");
        foreach (var tree in definition.Trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.Values[tree.Leaf] = await ImportAsync(tree, context, cancellationToken);
        }
        return snapshot;
    }

    // Quarantine deliberately survives normal product uninstall; snapshots never confer deletion authority.
    public Task UninstallAsync(ModuleSnapshot originalState, InstallerModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    private static LegacyStagingCleanupDefinition Authorize(JsonElement configuration, InstallerModuleContext context)
    {
        var authority = context.Authority ?? throw new CryptographicException("Legacy cleanup requires verified signed-package authority.");
        authority.Validate(context, configuration);
        var definition = LegacyStagingDefinition.Read(configuration);
        if (definition.Feature is not null && !context.Plan.Manifest.Features.Any(feature => feature.Id == definition.Feature))
            throw new InvalidDataException("Legacy cleanup references an unknown feature.");
        foreach (var tree in definition.Trees)
        {
            var prefix = tree.Leaf + "/";
            if (context.Plan.Manifest.Payload.Any(file => file.TargetRoot is null && (file.Path.Equals(tree.Leaf, StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("A legacy staging inventory overlaps installed payload.");
        }
        return definition;
    }

    private static async Task<string> ImportAsync(LegacyStagingTree tree, InstallerModuleContext context, CancellationToken cancellationToken)
    {
        var authority = context.Authority!;
        var root = PackageSafety.UnderRoot(authority.InstallRoot, tree.Leaf);
        var persistent = LegacyStagingDefinition.PersistentRoot(authority.StateRoot, authority.ProductId);
        var ledger = PackageSafety.UnderRoot(persistent, LegacyStagingDefinition.LedgerRelative(tree));
        var ledgerBytes = LegacyStagingDefinition.Ledger(tree, authority.ProductId, authority.PublisherPin);
        HeldTree? held = null;
        // No source mutation occurs in this block; a failed inspection preserves the complete tree.
        try
        {
            if (File.Exists(ledger))
                return new FileInfo(ledger).Length == ledgerBytes.Length && PackageSafety.HashEquals(PackageSafety.HashFile(ledger), IncrementalPackageIO.Hash(ledgerBytes))
                    ? Directory.Exists(root) || File.Exists(root) ? "preserved: staging reappeared after its one-time import" : "already imported"
                    : "preserved: import ledger differs";
            if (Directory.Exists(ledger)) return "preserved: import ledger path occupied";
            if (!Directory.Exists(root)) return File.Exists(root) ? "preserved: staging leaf is a file" : "absent";
            if (!OperatingSystem.IsWindows()) return "preserved: guarded Windows cleanup unavailable";
            var quarantine = PackageSafety.UnderRoot(persistent, "quarantine/" + LegacyStagingDefinition.Digest(tree));
            if (Directory.Exists(quarantine) || File.Exists(quarantine)) return "preserved: quarantine destination occupied";
            held = await Task.Run(() => HeldTree.Open(root, tree, cancellationToken), cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception or PlatformNotSupportedException)
        { held?.Dispose(); return "preserved: inventory, activity or exclusive-handle verification failed"; }
        using (held)
        {
            // Every source file is already hash-verified and held, and every operation journals its durable backup first.
            foreach (var file in held.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tree.Kind == "unproven-incoming")
                {
                    var relative = Path.GetRelativePath(root, file.Path).Replace('\\', '/');
                    context.Transaction.CreateFromHeld(PackageSafety.UnderRoot(LegacyStagingDefinition.QuarantineRoot(persistent, tree), relative), file);
                }
                context.Transaction.DeleteHeldFile(file);
            }
            foreach (var directory in held.Directories.OrderByDescending(item => item.Path.Length)) directory.DeleteAndClose();
            context.Transaction.CreateDeclaredFile(ledger, ledgerBytes);
        }
        return tree.Kind == "completed-backup" ? "removed: signed completed backup" : "quarantined: unproven incoming preserved for review";
    }

    internal static IEnumerable<FileRecoveryTarget> RecoveryTargets(PackageManifest signedManifest, string installRoot, string stateRoot, string? publisherPin, IEnumerable<string> features)
    {
        if (!signedManifest.Modules.TryGetValue(ModuleId, out var configuration)) yield break;
        if (publisherPin is null) throw new CryptographicException("Legacy staging recovery requires a signed publisher.");
        var definition = LegacyStagingDefinition.Read(configuration);
        if (!InstallerEngine.IsSelected(definition.Feature, features)) yield break;
        var persistent = LegacyStagingDefinition.PersistentRoot(stateRoot, signedManifest.Id);
        if (persistent.Equals(Path.GetFullPath(installRoot), StringComparison.OrdinalIgnoreCase) || RunningApplicationTarget.IsWithin(persistent, Path.GetFullPath(installRoot)))
            throw new InvalidDataException("Legacy staging recovery requires quarantine outside the application root.");
        foreach (var tree in definition.Trees)
        {
            if (signedManifest.Payload.Any(file => file.TargetRoot is null && (file.Path.Equals(tree.Leaf, StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith(tree.Leaf + "/", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("Legacy staging recovery overlaps installed payload.");
            foreach (var file in tree.Files)
            {
                yield return new() { Path = tree.Leaf + "/" + file.Path, BeforeSha256 = file.Sha256, BeforeSize = file.Size };
                if (tree.Kind == "unproven-incoming")
                {
                    yield return new() { Location = "external", Path = "legacy-staging/" + LegacyStagingDefinition.Digest(tree) + "/files/" + IncrementalPackageIO.Hash(System.Text.Encoding.UTF8.GetBytes(file.Path)),
                        TargetRootDirectory = LegacyStagingDefinition.QuarantineRoot(persistent, tree),
                        TargetRelativePath = file.Path, AfterSha256 = file.Sha256, AfterSize = file.Size };
                }
            }
            var ledger = LegacyStagingDefinition.Ledger(tree, signedManifest.Id, publisherPin);
            var ledgerRelative = LegacyStagingDefinition.LedgerRelative(tree);
            yield return new() { Location = "external", Path = "legacy-staging/" + ledgerRelative, TargetRootDirectory = persistent,
                TargetRelativePath = ledgerRelative, AfterSha256 = IncrementalPackageIO.Hash(ledger), AfterSize = ledger.Length };
        }
    }

    // An older receipt can already match a same-version repair. Only completed imports may discard their crash backups.
    internal static bool RecoveryOperationsComplete(FileTransactionRecoveryContext context, PackageManifest signedManifest, IReadOnlyList<JournalFileOperation> operations)
    {
        if (!signedManifest.Modules.TryGetValue(ModuleId, out var configuration)) return true;
        var definition = LegacyStagingDefinition.Read(configuration);
        var persistent = LegacyStagingDefinition.PersistentRoot(context.StateRoot, context.ProductId);
        foreach (var tree in definition.Trees)
        {
            var source = PackageSafety.UnderRoot(context.InstallDirectory, tree.Leaf);
            var quarantine = LegacyStagingDefinition.QuarantineRoot(persistent, tree);
            var ledger = PackageSafety.UnderRoot(persistent, LegacyStagingDefinition.LedgerRelative(tree));
            var touched = operations.Where(operation => operation.Destination.Equals(ledger, StringComparison.OrdinalIgnoreCase) ||
                operation.Destination.Equals(source, StringComparison.OrdinalIgnoreCase) || operation.Destination.Equals(quarantine, StringComparison.OrdinalIgnoreCase) ||
                RunningApplicationTarget.IsWithin(operation.Destination, source) || RunningApplicationTarget.IsWithin(operation.Destination, quarantine)).ToArray();
            if (touched.Length == 0) continue; // Absent/skipped inventories have no completion requirement.
            if (touched.Any(operation => !FileTransactionJournal.ValidateOperation(context, operation))) return false;
            var bytes = LegacyStagingDefinition.Ledger(tree, context.ProductId, context.PublisherKeySha256!);
            var ledgerHash = IncrementalPackageIO.Hash(bytes);
            if (Directory.Exists(source) || File.Exists(source) || !Matches(ledger, bytes.Length, ledgerHash) ||
                !touched.Any(operation => operation.Destination.Equals(ledger, StringComparison.OrdinalIgnoreCase) && operation.BeforeSha256 is null &&
                    PackageSafety.HashEquals(operation.AfterSha256 ?? "", ledgerHash) && operation.AfterSize == bytes.Length)) return false;
            foreach (var file in tree.Files)
            {
                var path = PackageSafety.UnderRoot(source, file.Path);
                if (!touched.Any(operation => operation.Destination.Equals(path, StringComparison.OrdinalIgnoreCase) && operation.AfterSha256 is null &&
                    PackageSafety.HashEquals(operation.BeforeSha256 ?? "", file.Sha256) && operation.BeforeSize == file.Size)) return false;
                if (tree.Kind == "unproven-incoming")
                {
                    var destination = PackageSafety.UnderRoot(quarantine, file.Path);
                    if (!Matches(destination, file.Size, file.Sha256) || !touched.Any(operation => operation.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase) &&
                        operation.BeforeSha256 is null && PackageSafety.HashEquals(operation.AfterSha256 ?? "", file.Sha256) && operation.AfterSize == file.Size)) return false;
                }
            }
            if (tree.Kind == "unproven-incoming") HeldTree.CheckInventory(quarantine, tree, CancellationToken.None);
        }
        return true;
        static bool Matches(string path, long size, string hash) => File.Exists(path) && new FileInfo(path).Length == size && PackageSafety.HashEquals(PackageSafety.HashFile(path), hash);
    }

    internal static bool IsRecoveryCreation(FileTransactionRecoveryContext context, string path) => context.Files.Any(file =>
        file.Location == "external" && file.Path.StartsWith("legacy-staging/", StringComparison.Ordinal) && context.Resolve(file).Equals(path, StringComparison.OrdinalIgnoreCase));

    internal static bool IsRecoveryDirectory(FileTransactionRecoveryContext context, string path)
    {
        if (!context.Files.Any(file => file.Location == "external" && file.Path.StartsWith("legacy-staging/", StringComparison.Ordinal))) return false;
        var parent = PackageSafety.UnderRoot(context.StateRoot, ".legacy-staging");
        var root = LegacyStagingDefinition.PersistentRoot(context.StateRoot, context.ProductId);
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.Equals(root, StringComparison.OrdinalIgnoreCase) || RunningApplicationTarget.IsWithin(path, root);
    }

    internal static void RemoveRecoveryCreation(string path, string hash, long size)
    {
        if (!File.Exists(path)) return;
        using var file = LegacyStagingFileGuard.File(path, size, hash);
        file.DeleteAndClose();
    }

    internal static void RemoveEmptyRecoveryDirectory(string directory)
    {
        using var held = LegacyStagingFileGuard.Directory(directory);
        // Recheck after taking the handle. Standard disposition also rejects a nonempty directory.
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) held.DeleteAndClose();
    }

    private sealed class HeldTree : IDisposable
    {
        internal List<LegacyStagingFileGuard> Files { get; } = [];
        internal List<LegacyStagingFileGuard> Directories { get; } = [];
        private LegacyStagingFileGuard? parent;
        internal static HeldTree Open(string root, LegacyStagingTree tree, CancellationToken token)
        {
            var held = new HeldTree();
            try
            {
                held.parent = LegacyStagingFileGuard.Directory(Path.GetDirectoryName(root)!);
                CheckInventory(root, tree, token);
                if (RunningApplications.HasResourceUsers(tree.Files.Select(file => PackageSafety.UnderRoot(root, file.Path)).ToArray()))
                    throw new IOException("A process is using declared legacy staging files.");
                foreach (var relative in LegacyStagingDefinition.Directories(tree).OrderBy(path => path.Length))
                {
                    token.ThrowIfCancellationRequested();
                    held.Directories.Add(LegacyStagingFileGuard.Directory(relative.Length == 0 ? root : PackageSafety.UnderRoot(root, relative)));
                }
                foreach (var file in tree.Files)
                {
                    token.ThrowIfCancellationRequested();
                    held.Files.Add(LegacyStagingFileGuard.File(PackageSafety.UnderRoot(root, file.Path), file.Size, file.Sha256));
                }
                CheckInventory(root, tree, token);
                held.parent.Recheck();
                foreach (var item in held.Files.Concat(held.Directories)) item.Recheck();
                return held;
            }
            catch { held.Dispose(); throw; }
        }
        internal static void CheckInventory(string root, LegacyStagingTree tree, CancellationToken token)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
                foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { AttributesToSkip = 0 }))
                {
                    token.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0) throw new IOException("Legacy staging contains a link or read-only item.");
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if ((attributes & FileAttributes.Directory) != 0) { directories.Add(relative); pending.Push(path); }
                    else files.Add(relative);
                    if (files.Count + directories.Count > 12288) throw new IOException("Legacy staging has an unexpected inventory size.");
                }
            if (!files.SetEquals(tree.Files.Select(file => file.Path)) || !directories.SetEquals(LegacyStagingDefinition.Directories(tree)))
                throw new IOException("Legacy staging contains missing or undeclared files or directories.");
        }
        public void Dispose() { foreach (var file in Files) file.Dispose(); foreach (var directory in Directories.AsEnumerable().Reverse()) directory.Dispose(); parent?.Dispose(); }
    }
}
