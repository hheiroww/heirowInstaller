using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HeirowInstaller.Core;

public sealed class FileTransactionRecoveryResult
{
    public int RecoveredTransactions { get; internal set; }
    public int RemovedWorkspaces { get; internal set; }
    public int ActiveOrUnrecognizedWorkspaces { get; internal set; }
}

public sealed class FileTransactionRecoveryRequiredException(string message) : IOException(message);

/// <summary>Recovers only a caller-verified product's durable file journal. Journal destinations never grant authority.</summary>
public static class FileTransactionRecovery
{
    public static FileTransactionRecoveryResult Recover(FileTransactionRecoveryContext context, bool operationLockHeld = false)
        => RecoverAsync(context, operationLockHeld: operationLockHeld).GetAwaiter().GetResult();

    public static async Task<FileTransactionRecoveryResult> RecoverAsync(FileTransactionRecoveryContext context,
        Func<RunningApplicationsRequest, CancellationToken, Task<RunningApplicationsChoice>>? prompt = null,
        bool operationLockHeld = false, CancellationToken cancellationToken = default)
        => await RecoverCoreAsync(context, prompt, operationLockHeld, cancellationToken, null);

    internal static Task<FileTransactionRecoveryResult> RecoverOwnedAsync(FileTransactionRecoveryContext context, Action recoverVerifiedSdkMetadata,
        Func<RunningApplicationsRequest, CancellationToken, Task<RunningApplicationsChoice>>? prompt, CancellationToken token)
        => RecoverCoreAsync(context, prompt, operationLockHeld: true, token, recoverVerifiedSdkMetadata);

    private static async Task<FileTransactionRecoveryResult> RecoverCoreAsync(FileTransactionRecoveryContext context,
        Func<RunningApplicationsRequest, CancellationToken, Task<RunningApplicationsChoice>>? prompt,
        bool operationLockHeld, CancellationToken cancellationToken, Action? recoverVerifiedSdkMetadata)
    {
        context.Validate();
        var result = new FileTransactionRecoveryResult();
        var parent = Path.Combine(Path.GetFullPath(context.StateRoot), ".transactions");
        PackageSafety.EnsureNoReparsePoints(parent);
        if (!Directory.Exists(parent)) return result;
        using var machineState = OperatingSystem.IsWindows() && context.Scope == "machine" && !context.Isolated ? MachineStateSecurity.Acquire(context.StateRoot, create: false) : null;
        using var productLock = operationLockHeld ? null : AcquireProductLock(context);
        foreach (var root in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(root), "N", out _)) continue;
            PackageSafety.EnsureNoReparsePoints(root);
            if (!File.Exists(Path.Combine(root, FileTransactionJournal.OwnerName))) continue;
            FileTransactionOwner owner;
            try { owner = FileTransactionJournal.ReadOwner(root); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
            { result.ActiveOrUnrecognizedWorkspaces++; continue; }
            if (!FileTransactionJournal.SameIdentity(owner, context, includeBase: false)) continue;
            using var lease = FileTransactionJournal.TryLease(root);
            if (lease is null) { result.ActiveOrUnrecognizedWorkspaces++; continue; }
            bool remove = false;
            try
            {
                var events = FileTransactionJournal.ReadEvents(root);
                // A durable receipt is written only after all application/module work. Verify the actual signed target and files before discarding a late crash journal.
                if (TargetIsInstalled(context, events.Files)) remove = true;
                else
                {
                    if (!FileTransactionJournal.SameIdentity(owner, context, includeBase: true) || owner.BindingSha256 != context.BindingHash)
                        throw new CryptographicException("Interrupted update ownership differs from the caller's verified base/target file context. Recovery files were preserved.");
                    foreach (var target in context.Applications) await RunningApplications.EnsureClosedAsync(target, prompt, cancellationToken);
                    var uncertain = events.HasExternalMetadata && recoverVerifiedSdkMetadata is null;
                    foreach (var operation in events.Files.AsEnumerable().Reverse())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!FileTransactionJournal.ValidateOperation(context, operation)) { uncertain = true; continue; }
                        FileTransactionJournal.Restore(root, context, operation);
                        FileTransactionJournal.Append(root, new() { Kind = "restored", Id = operation.Id });
                    }
                    foreach (var directory in events.Directories.OrderByDescending(path => path.Length))
                        if (FileTransactionJournal.AllowedDirectory(context, directory) && Directory.Exists(directory))
                        {
                            PackageSafety.EnsureNoReparsePoints(directory);
                            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                            {
                                if (LegacyStagingCleanupModule.IsRecoveryDirectory(context, directory)) LegacyStagingCleanupModule.RemoveEmptyRecoveryDirectory(directory);
                                else Directory.Delete(directory);
                            }
                        }
                    if (events.HasExternalMetadata && recoverVerifiedSdkMetadata is not null)
                        recoverVerifiedSdkMetadata();
                    if (uncertain)
                        throw new FileTransactionRecoveryRequiredException("Application files were recovered where ownership was verified. Interrupted external or unverified metadata requires a trusted repair; recovery files remain in " + root);
                    FileTransactionJournal.Append(root, new() { Kind = "rollback-complete" });
                    result.RecoveredTransactions++;
                    remove = true;
                }
            }
            finally
            {
                if (remove)
                {
                    lease.Dispose();
                    FileTransactionJournal.DeleteOwnedRoot(root, context);
                    result.RemovedWorkspaces++;
                }
            }
        }
        return result;
    }

    private static FileStream AcquireProductLock(FileTransactionRecoveryContext context)
    {
        var folder = Path.Combine(context.StateRoot, ".locks"); PackageSafety.EnsureNoReparsePoints(folder); Directory.CreateDirectory(folder);
        var path = PackageSafety.UnderRoot(folder, context.ProductId + ".lock");
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("Another installer operation is running for " + context.ProductId + ".", error); }
    }

    internal static bool TargetIsInstalled(FileTransactionRecoveryContext context, IReadOnlyList<JournalFileOperation>? operations = null)
    {
        var installed = InstallerEngine.ListInstalled(context.StateRoot).SingleOrDefault(product => product.Id == context.ProductId && product.Scope == context.Scope);
        if (installed is null || installed.Version != context.TargetVersion || !Path.GetFullPath(installed.InstallDirectory).Equals(Path.GetFullPath(context.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
            !IncrementalPackageIO.NullableHashEquals(installed.PublisherKeySha256, context.PublisherKeySha256)) return false;
        try
        {
            using var package = PackageReader.Open(installed.PackagePath);
            package.VerifySignature(context.PublisherKeySha256, context.PublisherKeySha256 is not null);
            if (!PackageSafety.HashEquals(IncrementalPackageIO.Hash(FileTransactionRecoveryContext.ReadManifest(installed.PackagePath)), context.TargetManifestSha256)) return false;
            package.VerifyAsync().GetAwaiter().GetResult();
            var owned = installed.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var file in package.Manifest.Payload.Where(file => !SharedUpdaterRuntime.IsRuntimePayload(package.Manifest, file.Path) && (file.Feature is null || installed.Features.Contains(file.Feature, StringComparer.OrdinalIgnoreCase))))
            {
                if (!owned.TryGetValue(file.Path, out var actual) || !PackageSafety.HashEquals(actual.Sha256, file.Sha256) || !string.Equals(actual.TargetRoot, file.TargetRoot, StringComparison.OrdinalIgnoreCase)) return false;
                IncrementalPackageIO.VerifyFile(PayloadDestinations.ResolveInstalled(installed, actual), file.Size, file.Sha256);
            }
            if (package.Manifest.UpdateCompanion is { RuntimeMode: "shared" } companion)
            {
                var enabled = installed.Features.Contains(companion.Feature, StringComparer.OrdinalIgnoreCase);
                var reference = SharedUpdaterRuntime.GetReferenceBinding(package.Manifest, installed, context.StateRoot, enabled);
                if (reference.Content is null ? File.Exists(reference.Path) : !File.Exists(reference.Path) || !PackageSafety.HashEquals(PackageSafety.HashFile(reference.Path), IncrementalPackageIO.Hash(reference.Content))) return false;
                if (enabled)
                {
                    var configuration = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllText(PackageSafety.UnderRoot(installed.InstallDirectory, companion.ConfigurationPath)), InstallerJson.Options)!;
                    if (configuration.ProductId != installed.Id || configuration.CurrentVersion != installed.Version || configuration.Scope != installed.Scope || configuration.Isolated != installed.Isolated ||
                        !Path.GetFullPath(configuration.StateRoot).Equals(Path.GetFullPath(context.StateRoot), StringComparison.OrdinalIgnoreCase) ||
                        !Path.GetFullPath(configuration.InstallDirectory).Equals(Path.GetFullPath(installed.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
                        !PackageSafety.HashEquals(configuration.PublisherKeySha256, installed.PublisherKeySha256!)) return false;
                    SharedUpdaterRuntime.ValidateBinding(configuration, package.Manifest); _ = SharedUpdaterRuntime.Resolve(configuration);
                }
            }
            return operations is null || LegacyStagingCleanupModule.RecoveryOperationsComplete(context, package.Manifest, operations);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or JsonException) { return false; }
    }
}

internal sealed class FileTransactionJournal : IDisposable
{
    internal const string OwnerName = "file-transaction-owner.json";
    private const string JournalName = "file-transaction.jsonl";
    private const int MaximumJournalBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions CompactJson = new(InstallerJson.Options) { WriteIndented = false };
    private readonly FileStream lease;
    private readonly string root;
    private readonly FileTransactionRecoveryContext context;

    internal FileTransactionJournal(string root, FileTransactionRecoveryContext context)
    {
        this.root = Path.GetFullPath(root);
        this.context = context;
        ValidateRoot(this.root, context);
        lease = new(Path.Combine(root, "file-transaction.lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var owner = new FileTransactionOwner { Id = Path.GetFileName(root), Context = context, BindingSha256 = context.BindingHash };
            WriteNew(Path.Combine(root, OwnerName), JsonSerializer.SerializeToUtf8Bytes(owner, CompactJson));
            WriteNew(Path.Combine(root, JournalName), []);
        }
        catch { lease.Dispose(); throw; }
    }

    internal JournalFileOperation Prepare(string destination, string? backup, string? temporary, string? afterHash, long? afterLength, string? verifiedSource = null)
    {
        var operation = new JournalFileOperation
        {
            Id = Guid.NewGuid().ToString("N"), Destination = Path.GetFullPath(destination),
            Backup = backup is null ? null : Path.GetRelativePath(root, backup).Replace('\\', '/'), Temporary = temporary,
            BeforeSha256 = backup is null ? null : PackageSafety.HashFile(backup), BeforeSize = backup is null ? null : new FileInfo(backup).Length,
            AfterSha256 = afterHash, AfterSize = afterLength
        };
        var target = context.Files.SingleOrDefault(file => context.Resolve(file).Equals(operation.Destination, StringComparison.OrdinalIgnoreCase));
        if (target is null && afterHash is not null && backup is null && verifiedSource is not null && IsTargetPackagePath(context, destination, afterHash) && IsTargetPackage(context, verifiedSource))
            operation.PackageCache = true;
        operation.Recoverable = ValidateOperation(context, operation);
        if (context.RequireExactBase && target is not null && !operation.Recoverable)
            throw new CryptographicException("An incremental file or recovery baseline changed before mutation: " + target.Path);
        Append(root, new() { Kind = "file", File = operation });
        return operation;
    }

    internal void Restored(JournalFileOperation operation) => Append(root, new() { Kind = "restored", Id = operation.Id });
    internal void ExternalMetadata() => Append(root, new() { Kind = "external-metadata" });
    internal void DirectoryCreated(string path) => Append(root, new() { Kind = "directory", Path = Path.GetFullPath(path) });
    internal void Commit() => Append(root, new() { Kind = "committed" });
    internal void RollbackComplete() => Append(root, new() { Kind = "rollback-complete" });

    internal static bool ValidateOperation(FileTransactionRecoveryContext context, JournalFileOperation operation)
    {
        if (!Guid.TryParseExact(operation.Id, "N", out _) || !Path.IsPathFullyQualified(operation.Destination)) return false;
        PackageSafety.EnsureNoReparsePoints(operation.Destination);
        if (operation.Backup is not null && !Regex.IsMatch(operation.Backup, "^rollback/[a-f0-9]{32}$", RegexOptions.CultureInvariant)) return false;
        if (operation.BeforeSha256 is null && (operation.Backup is not null || operation.BeforeSize is not null)) return false;
        if (operation.BeforeSha256 is not null && (!PackageSafety.HashPattern().IsMatch(operation.BeforeSha256) || operation.BeforeSize is null or < 0 || operation.Backup is null)) return false;
        if (operation.AfterSha256 is not null && (!PackageSafety.HashPattern().IsMatch(operation.AfterSha256) || operation.AfterSize is null or < 0)) return false;
        if (operation.Temporary is not null && (!operation.Temporary.StartsWith(operation.Destination + ".hinst-", StringComparison.Ordinal) ||
            !Regex.IsMatch(operation.Temporary[(operation.Destination.Length + 7)..], "^[a-f0-9]{32}\\.tmp$", RegexOptions.CultureInvariant))) return false;
        if (operation.PackageCache)
            return operation.BeforeSha256 is null && operation.AfterSha256 is not null && IsTargetPackagePath(context, operation.Destination, operation.AfterSha256);
        var target = context.Files.SingleOrDefault(file => context.Resolve(file).Equals(operation.Destination, StringComparison.OrdinalIgnoreCase));
        if (target is null || !IncrementalPackageIO.NullableHashEquals(target.BeforeSha256, operation.BeforeSha256) || (target.BeforeSize.HasValue && target.BeforeSize != operation.BeforeSize)) return false;
        return target.AllowGeneratedAfterContent ? operation.AfterSha256 is not null
            : IncrementalPackageIO.NullableHashEquals(target.AfterSha256, operation.AfterSha256) && target.AfterSize == operation.AfterSize;
    }

    internal static bool AllowedDirectory(FileTransactionRecoveryContext context, string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) return false;
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(Path.GetFullPath(context.StateRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        return context.Files.Any(file => context.Resolve(file).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) &&
            (Within(full, context.InstallDirectory) || Within(full, Path.Combine(context.StateRoot, context.ProductId)) ||
             full.Equals(Path.GetFullPath(context.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
             context.Files.Any(file => file.Location == "external" && (Within(full, file.TargetRootDirectory!) || full.Equals(Path.GetFullPath(file.TargetRootDirectory!), StringComparison.OrdinalIgnoreCase))));
    }

    internal static void Restore(string root, FileTransactionRecoveryContext context, JournalFileOperation operation)
    {
        var destination = operation.Destination;
        if (operation.PackageCache && File.Exists(destination) && !IsTargetPackage(context, destination))
            throw new CryptographicException("An interrupted package cache entry no longer matches the signed target.");
        if (operation.BeforeSha256 is not null)
        {
            var backup = PackageSafety.UnderRoot(root, operation.Backup!);
            IncrementalPackageIO.VerifyFile(backup, operation.BeforeSize!.Value, operation.BeforeSha256);
            if (!Matches(destination, operation.BeforeSha256, operation.BeforeSize))
            {
                if (File.Exists(destination) && !Matches(destination, operation.AfterSha256, operation.AfterSize)) throw new CryptographicException("A recovery target was modified after the interrupted update; its backup was preserved.");
                PackageSafety.EnsureNoReparsePoints(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".hinst-recovery-" + operation.Id + ".tmp";
                PackageSafety.EnsureNoReparsePoints(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
                CopyDurable(backup, temporary);
                File.Move(temporary, destination, true);
            }
        }
        else if (File.Exists(destination))
        {
            if (!Matches(destination, operation.AfterSha256, operation.AfterSize)) throw new CryptographicException("A newly created update target changed; automatic removal was refused.");
            if (LegacyStagingCleanupModule.IsRecoveryCreation(context, destination))
                LegacyStagingCleanupModule.RemoveRecoveryCreation(destination, operation.AfterSha256!, operation.AfterSize!.Value);
            else File.Delete(destination);
        }
        if (operation.Temporary is not null && File.Exists(operation.Temporary))
        {
            PackageSafety.EnsureNoReparsePoints(operation.Temporary);
            // The exact reserved temporary name was journaled before CreateNew/copy, so a torn copy is owned staging too.
            File.Delete(operation.Temporary);
        }
    }

    private static bool Matches(string path, string? hash, long? size)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        return hash is not null && File.Exists(path) && (!size.HasValue || new FileInfo(path).Length == size) && PackageSafety.HashEquals(PackageSafety.HashFile(path), hash);
    }
    private static bool IsTargetPackagePath(FileTransactionRecoveryContext context, string path, string digest)
        => Path.GetFullPath(path).Equals(PackageSafety.UnderRoot(context.StateRoot, context.ProductId + "/packages/" + digest.ToLowerInvariant() + ".hinst"), StringComparison.OrdinalIgnoreCase);
    private static bool IsTargetPackage(FileTransactionRecoveryContext context, string path)
    {
        try
        {
            using var package = PackageReader.Open(path);
            package.VerifySignature(context.PublisherKeySha256, context.PublisherKeySha256 is not null);
            return package.Manifest.Id == context.ProductId && package.Manifest.Version == context.TargetVersion &&
                PackageSafety.HashEquals(IncrementalPackageIO.Hash(FileTransactionRecoveryContext.ReadManifest(path)), context.TargetManifestSha256);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or JsonException) { return false; }
    }
    internal static void CopyDurable(string source, string destination)
    {
        PackageSafety.EnsureNoReparsePoints(source); PackageSafety.EnsureNoReparsePoints(destination);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough);
        input.CopyTo(output); output.Flush(flushToDisk: true);
    }
    internal static void Append(string root, FileTransactionEvent value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CompactJson);
        var path = Path.Combine(root, JournalName); PackageSafety.EnsureNoReparsePoints(path);
        using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        if (output.Length + bytes.Length + 1 > MaximumJournalBytes) throw new InvalidDataException("Update recovery journal exceeds its limit.");
        output.Position = output.Length; output.Write(bytes); output.WriteByte((byte)'\n'); output.Flush(flushToDisk: true);
    }
    internal static FileTransactionEvents ReadEvents(string root)
    {
        var path = Path.Combine(root, JournalName); PackageSafety.EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > MaximumJournalBytes) throw new InvalidDataException("Oversized update recovery journal.");
        var bytes = File.ReadAllBytes(path);
        var complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        if (complete != bytes.Length)
        {
            // Mutation begins only after a complete newline and a durable flush. A torn final append cannot authorize a mutation.
            using var truncated = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            truncated.SetLength(complete); truncated.Flush(flushToDisk: true);
        }
        var result = new FileTransactionEvents();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Encoding.UTF8.GetString(bytes, 0, complete).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 1024 * 1024) throw new InvalidDataException("Oversized recovery journal event.");
            var raw = Encoding.UTF8.GetBytes(line); IncrementalPackageIO.ValidateJson(raw);
            var item = JsonSerializer.Deserialize<FileTransactionEvent>(raw, CompactJson) ?? throw new InvalidDataException("Empty recovery event.");
            switch (item.Kind)
            {
                case "file" when item.File is not null && ids.Add(item.File.Id): result.Files.Add(item.File); break;
                case "restored" when item.Id is not null && ids.Contains(item.Id): result.Restored.Add(item.Id); break;
                case "directory" when item.Path is not null: result.Directories.Add(item.Path); break;
                case "external-metadata": result.HasExternalMetadata = true; break;
                case "committed": case "rollback-complete": break;
                default: throw new InvalidDataException("Invalid or duplicate update recovery event.");
            }
        }
        return result;
    }
    internal static FileTransactionOwner ReadOwner(string root)
    {
        var path = Path.Combine(root, OwnerName); PackageSafety.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > MaximumJournalBytes) throw new InvalidDataException("Oversized recovery owner marker.");
        var bytes = File.ReadAllBytes(path); IncrementalPackageIO.ValidateJson(bytes);
        var owner = JsonSerializer.Deserialize<FileTransactionOwner>(bytes, CompactJson) ?? throw new InvalidDataException("Empty recovery owner marker.");
        if (owner.Format != "heirowInstaller.file-transaction/1" || owner.Id != Path.GetFileName(root) || owner.BindingSha256 != owner.Context.BindingHash)
            throw new InvalidDataException("Invalid recovery owner marker binding.");
        owner.Context.Validate(); ValidateRoot(root, owner.Context);
        return owner;
    }
    internal static bool SameIdentity(FileTransactionOwner owner, FileTransactionRecoveryContext context, bool includeBase)
        => owner.Context.ProductId == context.ProductId && owner.Context.Scope == context.Scope &&
           Path.GetFullPath(owner.Context.StateRoot).Equals(Path.GetFullPath(context.StateRoot), StringComparison.OrdinalIgnoreCase) &&
           Path.GetFullPath(owner.Context.InstallDirectory).Equals(Path.GetFullPath(context.InstallDirectory), StringComparison.OrdinalIgnoreCase) &&
           IncrementalPackageIO.NullableHashEquals(owner.Context.PublisherKeySha256, context.PublisherKeySha256) &&
           PackageSafety.HashEquals(owner.Context.TargetManifestSha256, context.TargetManifestSha256) &&
           (!includeBase || IncrementalPackageIO.NullableHashEquals(owner.Context.BaseManifestSha256, context.BaseManifestSha256));
    internal static FileStream? TryLease(string root)
    {
        var path = Path.Combine(root, "file-transaction.lease"); PackageSafety.EnsureNoReparsePoints(path);
        try { return new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
    internal static void DeleteOwnedRoot(string root, FileTransactionRecoveryContext context)
    {
        ValidateRoot(root, context);
        var owner = ReadOwner(root);
        if (!SameIdentity(owner, context, includeBase: false)) throw new InvalidDataException("Refusing cleanup outside the verified update's owned workspace.");
        NoLinks(root); Directory.Delete(root, recursive: true);
    }
    private static void NoLinks(string root)
    {
        PackageSafety.EnsureNoReparsePoints(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Update cleanup cannot follow reparse points.");
            if (Directory.Exists(path)) NoLinks(path);
        }
    }
    private static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void ValidateRoot(string root, FileTransactionRecoveryContext context)
    {
        var expectedParent = Path.Combine(Path.GetFullPath(context.StateRoot), ".transactions");
        if (!Guid.TryParseExact(Path.GetFileName(root), "N", out _) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A durable file workspace must be an exact installer-owned transaction child.");
        PackageSafety.EnsureNoReparsePoints(root);
    }
    private static void WriteNew(string path, byte[] bytes)
    { using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); output.Write(bytes); output.Flush(flushToDisk: true); }
    public void Dispose() => lease.Dispose();
}

internal sealed class FileTransactionOwner
{
    public string Format { get; set; } = "heirowInstaller.file-transaction/1";
    public string Id { get; set; } = "";
    public string BindingSha256 { get; set; } = "";
    public FileTransactionRecoveryContext Context { get; set; } = new();
}
internal sealed class FileTransactionEvent
{
    public string Kind { get; set; } = "";
    public string? Id { get; set; }
    public string? Path { get; set; }
    public JournalFileOperation? File { get; set; }
}
internal sealed class JournalFileOperation
{
    public string Id { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? Backup { get; set; }
    public string? Temporary { get; set; }
    public string? BeforeSha256 { get; set; }
    public long? BeforeSize { get; set; }
    public string? AfterSha256 { get; set; }
    public long? AfterSize { get; set; }
    public bool PackageCache { get; set; }
    public bool Recoverable { get; set; }
}
internal sealed class FileTransactionEvents
{
    public List<JournalFileOperation> Files { get; } = [];
    public HashSet<string> Restored { get; } = new(StringComparer.Ordinal);
    public List<string> Directories { get; } = [];
    public bool HasExternalMetadata { get; set; }
}
