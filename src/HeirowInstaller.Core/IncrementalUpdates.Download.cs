using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>Owns only incremental download scratch, retaining ready packages until their application succeeds.</summary>
internal sealed class IncrementalDownloadWorkspace : IDisposable
{
    private const string MarkerName = "incremental-download-owner.json";
    private const string LeaseName = "incremental-download.lease";
    private readonly FileStream lease;
    private readonly IncrementalDownloadOwner owner;
    private bool ready;
    internal string Root { get; }
    internal string PackagePath => Path.Combine(Root, owner.FileName);

    internal IncrementalDownloadWorkspace(string destinationDirectory, UpdateFeed feed)
    {
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        CleanupIncomplete(destinationDirectory, feed.ProductId);
        owner = new()
        {
            Id = Guid.NewGuid().ToString("N"), ProductId = feed.ProductId, Version = feed.Version, DestinationDirectory = destinationDirectory,
            Sha256 = feed.Sha256, FileName = feed.ProductId + "-" + feed.Version.Replace('+', '_') + ".hupd"
        };
        Root = Path.Combine(destinationDirectory, ".hupd-downloads", owner.ProductId, owner.Id);
        PackageSafety.EnsureNoReparsePoints(Root); Directory.CreateDirectory(Root);
        lease = new(Path.Combine(Root, LeaseName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { Save(Root, owner); } catch { lease.Dispose(); throw; }
    }
    internal void Complete() { owner.Phase = "ready"; Save(Root, owner); ready = true; }

    internal static int CleanupIncomplete(string destinationDirectory, string productId)
    {
        destinationDirectory = Path.GetFullPath(destinationDirectory); PackageSafety.ValidateId(productId);
        var parent = Path.Combine(destinationDirectory, ".hupd-downloads", productId); PackageSafety.EnsureNoReparsePoints(parent);
        if (!Directory.Exists(parent)) return 0;
        var removed = 0;
        foreach (var root in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(root), "N", out _)) continue;
            PackageSafety.EnsureNoReparsePoints(root);
            if (!File.Exists(Path.Combine(root, MarkerName))) continue;
            try
            {
                var candidate = Read(root);
                if (candidate.ProductId != productId || !Path.GetFullPath(candidate.DestinationDirectory).Equals(destinationDirectory, StringComparison.OrdinalIgnoreCase) || candidate.Phase == "ready") continue;
                var leasePath = Path.Combine(root, LeaseName); PackageSafety.EnsureNoReparsePoints(leasePath);
                using var held = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (!OnlyOwnedFiles(root, candidate)) continue;
                held.Dispose(); Directory.Delete(root, recursive: true); removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
        }
        return removed;
    }

    internal static void RemoveApplied(string packagePath, UpdateFeed feed)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(packagePath))!;
        if (!File.Exists(Path.Combine(root, MarkerName))) return;
        try
        {
            var candidate = Read(root);
            if (candidate.ProductId != feed.ProductId || candidate.Version != feed.Version || !PackageSafety.HashEquals(candidate.Sha256, feed.Sha256) ||
                !Path.Combine(root, candidate.FileName).Equals(Path.GetFullPath(packagePath), StringComparison.OrdinalIgnoreCase) ||
                !PackageSafety.HashEquals(PackageSafety.HashFile(packagePath), feed.Sha256)) return;
            var leasePath = Path.Combine(root, LeaseName); PackageSafety.EnsureNoReparsePoints(leasePath);
            using var held = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            candidate.Phase = "applied"; Save(root, candidate);
            if (!OnlyOwnedFiles(root, candidate)) return;
            held.Dispose(); Directory.Delete(root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
    }

    private static IncrementalDownloadOwner Read(string root)
    {
        var path = Path.Combine(root, MarkerName); PackageSafety.EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Oversized download ownership marker.");
        var bytes = File.ReadAllBytes(path); IncrementalPackageIO.ValidateJson(bytes);
        var value = JsonSerializer.Deserialize<IncrementalDownloadOwner>(bytes, InstallerJson.Options) ?? throw new InvalidDataException("Empty download ownership marker.");
        PackageSafety.ValidateId(value.ProductId); _ = PackageVersion.Parse(value.Version);
        var expected = Path.Combine(Path.GetFullPath(value.DestinationDirectory), ".hupd-downloads", value.ProductId, value.Id);
        if (value.Format != "heirowInstaller.incremental-download/1" || !Guid.TryParseExact(value.Id, "N", out _) || value.Id != Path.GetFileName(root) ||
            !Path.GetFullPath(root).Equals(expected, StringComparison.OrdinalIgnoreCase) || !PackageSafety.HashPattern().IsMatch(value.Sha256) ||
            value.FileName != value.ProductId + "-" + value.Version.Replace('+', '_') + ".hupd" || value.Phase is not ("downloading" or "ready" or "applied"))
            throw new InvalidDataException("Invalid download ownership marker.");
        return value;
    }
    private static bool OnlyOwnedFiles(string root, IncrementalDownloadOwner owner)
    {
        PackageSafety.EnsureNoReparsePoints(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
            var name = Path.GetFileName(path);
            if (name == MarkerName || name == LeaseName || name == owner.FileName) continue;
            if (name.Length == owner.FileName.Length + 42 && name.StartsWith(owner.FileName + ".", StringComparison.Ordinal) && name.EndsWith(".download", StringComparison.Ordinal) &&
                Guid.TryParseExact(name[(owner.FileName.Length + 1)..^9], "N", out _)) continue;
            if (name.StartsWith("owner-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) && Guid.TryParseExact(name[6..^4], "N", out _)) continue;
            return false;
        }
        return true;
    }
    private static void Save(string root, IncrementalDownloadOwner owner)
    {
        var temporary = Path.Combine(root, "owner-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(owner, InstallerJson.Options)); output.Flush(flushToDisk: true); }
            File.Move(temporary, Path.Combine(root, MarkerName), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose()
    {
        lease.Dispose();
        if (!ready)
            try { if (OnlyOwnedFiles(Root, owner)) Directory.Delete(Root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class IncrementalDownloadOwner
{
    public string Format { get; set; } = "heirowInstaller.incremental-download/1";
    public string Id { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string DestinationDirectory { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Phase { get; set; } = "downloading";
}
