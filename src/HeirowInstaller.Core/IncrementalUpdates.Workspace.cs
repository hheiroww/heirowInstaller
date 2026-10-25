using System.Text.Json;

namespace HeirowInstaller.Core;

public sealed class IncrementalWorkspaceCleanupResult
{
    public int Removed { get; internal set; }
    public int Preserved { get; internal set; }
}

/// <summary>Owns one update's reconstruction files. Cleanup never enumerates application backup names.</summary>
public sealed class IncrementalUpdateWorkspace : IDisposable
{
    private const string OwnerName = "incremental-update-owner.json";
    private const string LeaseName = "incremental-update.lease";
    private readonly FileStream lease;
    private readonly FileTransactionRecoveryContext context;
    private readonly IncrementalWorkspaceOwner owner;
    private bool preserve;
    public string Root { get; }
    public string PackagePath => Path.Combine(Root, "reconstructed.hinst");

    private IncrementalUpdateWorkspace(FileTransactionRecoveryContext context)
    {
        context.Validate(); this.context = context;
        var id = Guid.NewGuid().ToString("N");
        Root = Path.Combine(Path.GetFullPath(context.StateRoot), ".updates", context.ProductId, id);
        PackageSafety.EnsureNoReparsePoints(Root);
        Directory.CreateDirectory(Root);
        lease = new(Path.Combine(Root, LeaseName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        owner = new()
        {
            Id = id, ProductId = context.ProductId, StateRoot = Path.GetFullPath(context.StateRoot), InstallDirectory = Path.GetFullPath(context.InstallDirectory),
            PublisherKeySha256 = context.PublisherKeySha256, BaseManifestSha256 = context.BaseManifestSha256, TargetManifestSha256 = context.TargetManifestSha256
        };
        try { Save(); } catch { lease.Dispose(); throw; }
    }
    public static IncrementalUpdateWorkspace Create(FileTransactionRecoveryContext context) => new(context);
    public void MarkApplying() { owner.Phase = "applying"; preserve = true; Save(); }
    public void Complete() { owner.Phase = "complete"; Save(); preserve = false; }
    public void KeepForRecovery() => preserve = true;
    public void AbortAfterRollback()
    {
        if (HasPendingTransaction(context, owner.TargetManifestSha256)) { preserve = true; return; }
        owner.Phase = "rolled-back"; Save(); preserve = false;
    }

    public static IncrementalWorkspaceCleanupResult CleanupAbandoned(FileTransactionRecoveryContext context)
    {
        context.Validate();
        var result = new IncrementalWorkspaceCleanupResult();
        var parent = Path.Combine(Path.GetFullPath(context.StateRoot), ".updates", context.ProductId);
        PackageSafety.EnsureNoReparsePoints(parent);
        if (!Directory.Exists(parent)) return result;
        foreach (var root in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(root), "N", out _)) continue;
            PackageSafety.EnsureNoReparsePoints(root);
            if (!File.Exists(Path.Combine(root, OwnerName))) continue;
            IncrementalWorkspaceOwner candidate;
            try { candidate = ReadOwner(root); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { result.Preserved++; continue; }
            if (!Matches(candidate, context)) { result.Preserved++; continue; }
            FileStream held;
            var leasePath = Path.Combine(root, LeaseName); PackageSafety.EnsureNoReparsePoints(leasePath);
            try { held = new(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { result.Preserved++; continue; }
            using (held)
            {
                // A marker's phase is not proof that application mutation never began. Retain the full target while any matching/unknown file journal needs recovery.
                if (HasPendingTransaction(context, candidate.TargetManifestSha256)) { result.Preserved++; continue; }
                NoLinks(root);
                held.Dispose();
                Directory.Delete(root, recursive: true); result.Removed++;
            }
        }
        return result;
    }

    private void Save()
    {
        var path = Path.Combine(Root, OwnerName);
        var temporary = Path.Combine(Root, "owner-" + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(owner, InstallerJson.Options);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static IncrementalWorkspaceOwner ReadOwner(string root)
    {
        var path = Path.Combine(root, OwnerName); PackageSafety.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Oversized update workspace marker.");
        var bytes = File.ReadAllBytes(path); IncrementalPackageIO.ValidateJson(bytes);
        var value = JsonSerializer.Deserialize<IncrementalWorkspaceOwner>(bytes, InstallerJson.Options) ?? throw new InvalidDataException("Empty update workspace marker.");
        if (value.Format != "heirowInstaller.incremental-workspace/1" || value.Id != Path.GetFileName(root) || !Guid.TryParseExact(value.Id, "N", out _) ||
            !PackageSafety.HashPattern().IsMatch(value.TargetManifestSha256) || value.Phase is not ("preparing" or "applying" or "complete" or "rolled-back"))
            throw new InvalidDataException("Invalid update workspace marker.");
        return value;
    }
    private static bool Matches(IncrementalWorkspaceOwner owner, FileTransactionRecoveryContext context)
        => owner.ProductId == context.ProductId && Path.GetFullPath(owner.StateRoot).Equals(Path.GetFullPath(context.StateRoot), StringComparison.OrdinalIgnoreCase) &&
           Path.GetFullPath(owner.InstallDirectory).Equals(Path.GetFullPath(context.InstallDirectory), StringComparison.OrdinalIgnoreCase) &&
           IncrementalPackageIO.NullableHashEquals(owner.PublisherKeySha256, context.PublisherKeySha256);

    private static bool HasPendingTransaction(FileTransactionRecoveryContext context, string targetManifestSha256)
    {
        var parent = Path.Combine(context.StateRoot, ".transactions"); PackageSafety.EnsureNoReparsePoints(parent);
        if (!Directory.Exists(parent)) return false;
        foreach (var root in Directory.EnumerateDirectories(parent))
        {
            PackageSafety.EnsureNoReparsePoints(root);
            if (!File.Exists(Path.Combine(root, FileTransactionJournal.OwnerName))) continue;
            try
            {
                var transaction = FileTransactionJournal.ReadOwner(root);
                if (transaction.Context.ProductId == context.ProductId && PackageSafety.HashEquals(transaction.Context.TargetManifestSha256, targetManifestSha256)) return true;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { return true; }
        }
        return false;
    }
    private static void NoLinks(string root)
    {
        PackageSafety.EnsureNoReparsePoints(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned update cleanup cannot follow reparse points.");
            if (Directory.Exists(path)) NoLinks(path);
        }
    }
    public void Dispose()
    {
        lease.Dispose();
        if (preserve) return;
        if (!Matches(ReadOwner(Root), context)) throw new InvalidDataException("Update workspace ownership changed before cleanup.");
        NoLinks(Root); Directory.Delete(Root, recursive: true);
    }
}

internal sealed class IncrementalWorkspaceOwner
{
    public string Format { get; set; } = "heirowInstaller.incremental-workspace/1";
    public string Id { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string StateRoot { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string? PublisherKeySha256 { get; set; }
    public string? BaseManifestSha256 { get; set; }
    public string TargetManifestSha256 { get; set; } = "";
    public string Phase { get; set; } = "preparing";
}
