using System.IO.Compression;
using System.Security.Cryptography;

namespace HeirowInstaller.Core;

internal sealed class NativeMigrationStageFile
{
    public string Path { get; set; } = "";
    public string Identity { get; set; } = "";
}

/// <summary>Resumable extraction with a durable creation identity before every large write.</summary>
internal static class NativeMigrationStage
{
    internal static async Task PrepareAsync(string root, NativeMigrationBaseline baseline, List<NativeMigrationStageFile> intents,
        Action save, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        var declared = baseline.Manifest.Payload.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        if (declared.Count > 4096 || intents.Count > declared.Count || intents.Select(intent => intent.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != intents.Count ||
            intents.Any(intent => !declared.ContainsKey(intent.Path) || intent.Identity.Length != 42 ||
                !System.Text.RegularExpressions.Regex.IsMatch(intent.Identity, "^[a-f0-9]{8}:[a-f0-9]{16}:[a-f0-9]{16}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new NativeMigrationRepairRequiredException("The media creation intents differ from the signed original payload.");
        PackageSafety.EnsureNoReparsePoints(root); Directory.CreateDirectory(root);
        var directories = Directories(declared.Keys);
        ValidateInventory(root, declared.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), directories, intents);
        var heldDirectories = new List<LegacyStagingFileGuard>();
        using var package = BundleBuilder.OpenPackageStream(baseline.Product.PackagePath);
        using var archive = new ZipArchive(package, ZipArchiveMode.Read);
        try
        {
            foreach (var relative in directories.OrderBy(path => path.Length))
            {
                var path = relative.Length == 0 ? root : PackageSafety.UnderRoot(root, relative);
                Directory.CreateDirectory(path); heldDirectories.Add(LegacyStagingFileGuard.Directory(path));
            }
            var current = 0;
            foreach (var file in declared.Values)
            {
                token.ThrowIfCancellationRequested();
                var path = PackageSafety.UnderRoot(root, file.Path);
                var intent = intents.SingleOrDefault(item => item.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
                if (File.Exists(path))
                {
                    if (intent is null) throw new NativeMigrationRepairRequiredException("An unowned file occupies a migration media destination.");
                    using var held = HoldCurrent(path, intent);
                    if (held.Size == file.Size && PackageSafety.HashEquals(held.Sha256!, file.Sha256))
                    { progress?.Report(new("migration-stage", "Reusing verified original recovery media.", ++current, declared.Count)); token.ThrowIfCancellationRequested(); continue; }
                    if (!PrefixMatches(archive, file, held))
                        throw new NativeMigrationRepairRequiredException("A partial recovery file was changed; it was preserved.");
                    held.DeleteAndClose(); intents.Remove(intent); save(); intent = null;
                }
                else if (intent is not null) { intents.Remove(intent); save(); }
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    intent = new() { Path = file.Path, Identity = LegacyStagingFileGuard.CaptureIdentity(output.SafeFileHandle) };
                    intents.Add(intent); save(); // Persist ownership before copying even the first payload byte.
                    using var input = Entry(archive, file).Open();
                    await input.CopyToAsync(output, token); output.Flush(flushToDisk: true); output.Position = 0;
                    if (output.Length != file.Size || !PackageSafety.HashEquals(Convert.ToHexString(await SHA256.HashDataAsync(output, token)), file.Sha256))
                        throw new CryptographicException("Original recovery media differs from its signed payload.");
                }
                using (var held = LegacyStagingFileGuard.File(path, file.Size, file.Sha256))
                    if (held.IdentityToken != intent.Identity) throw new NativeMigrationRepairRequiredException("The recovery file was replaced after extraction.");
                progress?.Report(new("migration-stage", "Prepared verified original recovery media.", ++current, declared.Count));
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            Cleanup(root, baseline.Manifest, intents, save, archive, allowPartial: true);
            throw;
        }
        finally { foreach (var held in heldDirectories.AsEnumerable().Reverse()) held.Dispose(); }
    }

    internal static void Cleanup(string root, PackageManifest original, List<NativeMigrationStageFile> intents, Action save,
        ZipArchive? archive = null, bool allowPartial = false)
    {
        foreach (var intent in intents.ToArray())
        {
            var file = original.Payload.SingleOrDefault(candidate => candidate.Path.Equals(intent.Path, StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;
            var path = PackageSafety.UnderRoot(root, file.Path);
            try
            {
                if (!File.Exists(path)) { intents.Remove(intent); save(); continue; }
                using var held = HoldCurrent(path, intent);
                if (held.Size == file.Size && PackageSafety.HashEquals(held.Sha256!, file.Sha256) || allowPartial && archive is not null && PrefixMatches(archive, file, held))
                { held.DeleteAndClose(); intents.Remove(intent); save(); }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or CryptographicException) { }
        }
    }

    private static LegacyStagingFileGuard HoldCurrent(string path, NativeMigrationStageFile intent)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        string hash; long size;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024))
        {
            if (LegacyStagingFileGuard.CaptureIdentity(input.SafeFileHandle) != intent.Identity)
                throw new NativeMigrationRepairRequiredException("A recovery media file was replaced; it was preserved.");
            size = input.Length; hash = Convert.ToHexString(SHA256.HashData(input));
        }
        var held = LegacyStagingFileGuard.File(path, size, hash);
        if (held.IdentityToken == intent.Identity) return held;
        held.Dispose(); throw new NativeMigrationRepairRequiredException("A recovery media file changed identity; it was preserved.");
    }

    private static bool PrefixMatches(ZipArchive archive, PayloadFile file, LegacyStagingFileGuard held)
    {
        if (held.Size > file.Size) return false;
        using var input = Entry(archive, file).Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024]; var remaining = held.Size;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) return false; hash.AppendData(buffer, 0, read); remaining -= read;
        }
        return PackageSafety.HashEquals(Convert.ToHexString(hash.GetHashAndReset()), held.Sha256!);
    }
    private static ZipArchiveEntry Entry(ZipArchive archive, PayloadFile file)
    {
        var entries = archive.Entries.Where(entry => entry.FullName.Equals("payload/" + file.Path, StringComparison.Ordinal)).ToArray();
        return entries.Length == 1 && entries[0].Length == file.Size ? entries[0] : throw new CryptographicException("Recovery media is not an exact declared payload entry.");
    }
    private static HashSet<string> Directories(IEnumerable<string> paths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var path in paths)
        {
            var parent = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent)) { directories.Add(parent.Replace(Path.DirectorySeparatorChar, '/')); parent = Path.GetDirectoryName(parent); }
        }
        return directories;
    }
    private static void ValidateInventory(string root, HashSet<string> files, HashSet<string> directories, List<NativeMigrationStageFile> intents)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                PackageSafety.EnsureNoReparsePoints(entry);
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(entry))
                {
                    if (!directories.Contains(relative)) throw new NativeMigrationRepairRequiredException("An unknown recovery media directory was preserved.");
                    pending.Push(entry);
                }
                else if (!files.Contains(relative) || !intents.Any(intent => intent.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                    throw new NativeMigrationRepairRequiredException("An unknown recovery media file was preserved.");
            }
        }
    }
}
