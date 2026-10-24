using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeirowInstaller.Core;

public static class PackageBuilder
{
    public static PackageManifest Build(string manifestPath, string sourceDirectory, string outputPath, PackageBuildOptions? options = null)
    {
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        outputPath = Path.GetFullPath(outputPath);
        var signingKeyPath = options?.SigningKeyPath is { } keyPath ? Path.GetFullPath(keyPath) : null;
        if (outputPath.Equals(signingKeyPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Output cannot overwrite the signing key.");
        PackageSafety.EnsureNoReparsePoints(sourceDirectory);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath), InstallerJson.Options)
                       ?? throw new InvalidDataException("Manifest is empty.");
        if (manifest.Payload.Count == 0)
        {
            foreach (var path in EnumerateSafeFiles(sourceDirectory).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFullPath(path).Equals(outputPath, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFullPath(path).Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase)) continue;
                var relative = PackageSafety.NormalizeRelativePath(Path.GetRelativePath(sourceDirectory, path));
                manifest.Payload.Add(new() { Source = relative, Path = relative });
            }
        }
        foreach (var file in manifest.Payload)
        {
            if (file.Path.Length == 0) file.Path = file.Source;
            if (file.Source.Length == 0) file.Source = file.Path;
            var input = PackageSafety.UnderRoot(sourceDirectory, file.Source);
            if (input.Equals(signingKeyPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The signing key cannot be included as a payload source.");
            if (!File.Exists(input)) throw new FileNotFoundException($"Payload source does not exist: {file.Source}");
            if (input.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Output cannot be a payload source.");
            file.Size = new FileInfo(input).Length;
            file.Sha256 = PackageSafety.HashFile(input);
        }
        PackageSafety.ValidateManifest(manifest, true);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, InstallerJson.Options);
        PackageSafety.EnsureNoReparsePoints(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "manifest.json", manifestBytes);
                foreach (var file in manifest.Payload)
                {
                    var entry = archive.CreateEntry("payload/" + file.Path, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var input = File.OpenRead(PackageSafety.UnderRoot(sourceDirectory, file.Source));
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
                if (signingKeyPath is not null)
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(File.ReadAllText(signingKeyPath));
                    if (rsa.KeySize < 2048) throw new InvalidDataException("RSA signing keys must be at least 2048 bits.");
                    var signature = new PackageSignature
                    {
                        Algorithm = "RSA-PSS-SHA256",
                        PublicKey = rsa.ExportSubjectPublicKeyInfoPem(),
                        Signature = Convert.ToBase64String(rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    };
                    WriteEntry(archive, "signature.json", JsonSerializer.SerializeToUtf8Bytes(signature, InstallerJson.Options));
                }
            }
            using (var verification = PackageReader.Open(temporary)) verification.VerifyAsync().GetAwaiter().GetResult();
            File.Move(temporary, outputPath, true);
            return manifest;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Package sources cannot contain symbolic links or reparse points: " + path);
            if ((attributes & FileAttributes.Directory) != 0)
            { foreach (var child in EnumerateSafeFiles(path)) yield return child; }
            else yield return path;
        }
    }
}

public sealed class PackageSignature
{
    public string Algorithm { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed class PackageReader : IDisposable
{
    private readonly Stream stream;
    private readonly ZipArchive archive;
    private readonly byte[] manifestBytes;
    private readonly Dictionary<string, ZipArchiveEntry> entries;
    public string PackagePath { get; }
    public PackageManifest Manifest { get; }
    public PackageSignature? Signature { get; }
    public string? PublisherKeySha256 { get; }

    private PackageReader(string path)
    {
        PackagePath = Path.GetFullPath(path);
        stream = BundleBuilder.OpenPackageStream(PackagePath);
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            entries = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Duplicate package entry.");
            }
            if (!entries.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Missing or oversized package manifest.");
            using (var source = manifestEntry.Open())
            using (var buffer = new MemoryStream()) { source.CopyTo(buffer); manifestBytes = buffer.ToArray(); }
            Manifest = JsonSerializer.Deserialize<PackageManifest>(manifestBytes, InstallerJson.Options) ?? throw new InvalidDataException("Empty package manifest.");
            PackageSafety.ValidateManifest(Manifest, true);
            var permitted = new HashSet<string>(Manifest.Payload.Select(x => "payload/" + x.Path), StringComparer.OrdinalIgnoreCase) { "manifest.json", "signature.json" };
            if (entries.Keys.Any(x => !permitted.Contains(x))) throw new InvalidDataException("Package contains undeclared entries.");
            foreach (var file in Manifest.Payload)
                if (!entries.TryGetValue("payload/" + file.Path, out var entry) || entry.Length != file.Size)
                    throw new InvalidDataException($"Missing payload or size mismatch: {file.Path}");
            if (entries.TryGetValue("signature.json", out var signatureEntry))
            {
                if (signatureEntry.Length > 64 * 1024) throw new InvalidDataException("Signature entry is too large.");
                using var source = signatureEntry.Open();
                Signature = JsonSerializer.Deserialize<PackageSignature>(source, InstallerJson.Options) ?? throw new InvalidDataException("Invalid signature.");
                using var rsa = RSA.Create();
                rsa.ImportFromPem(Signature.PublicKey);
                PublisherKeySha256 = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
                VerifySignature();
            }
        }
        catch { stream.Dispose(); throw; }
    }

    public static PackageReader Open(string path) => new(path);
    public static bool HasEmbeddedPackage(string path) => BundleBuilder.HasEmbeddedPackage(path);

    public void VerifySignature(string? trustedPublicKeySha256 = null, bool requireSignature = false)
    {
        if (Signature is null)
        {
            if (requireSignature || trustedPublicKeySha256 is not null) throw new CryptographicException("This package is unsigned.");
            return;
        }
        if (Signature.Algorithm != "RSA-PSS-SHA256") throw new CryptographicException("Unsupported package signature algorithm.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Signature.PublicKey);
        if (rsa.KeySize < 2048 || !rsa.VerifyData(manifestBytes, Convert.FromBase64String(Signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Package signature is invalid.");
        if (trustedPublicKeySha256 is not null && !PackageSafety.HashEquals(trustedPublicKeySha256, PublisherKeySha256!))
            throw new CryptographicException("Package publisher key does not match the trusted fingerprint.");
    }

    public async Task VerifyAsync(IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < Manifest.Payload.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Manifest.Payload[i];
            using var input = entries["payload/" + file.Path].Open();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
            if (!PackageSafety.HashEquals(hash, file.Sha256)) throw new CryptographicException($"Payload hash mismatch: {file.Path}");
            progress?.Report(new("verify", file.Path, i + 1, Manifest.Payload.Count));
        }
    }

    public async Task ExtractVerifiedAsync(string destinationDirectory, IEnumerable<PayloadFile>? files = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        VerifySignature();
        var selected = (files ?? Manifest.Payload).ToList();
        for (var i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = selected[i];
            var declared = Manifest.Payload.SingleOrDefault(x => x.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (declared is null || !PackageSafety.HashEquals(file.Sha256, declared.Sha256)) throw new InvalidDataException("Extraction requested an undeclared file.");
            var outputPath = PackageSafety.UnderRoot(destinationDirectory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var input = entries["payload/" + file.Path].Open())
            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellationToken);
            if (!PackageSafety.HashEquals(PackageSafety.HashFile(outputPath), file.Sha256)) throw new CryptographicException($"Payload hash mismatch: {file.Path}");
            progress?.Report(new("extract", file.Path, i + 1, selected.Count));
        }
    }

    public void Dispose() { archive.Dispose(); stream.Dispose(); }
}

public static class BundleBuilder
{
    private const int FooterSize = 64;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HINSTEND");
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("HINST01\0");

    public static void Build(string launcherExePath, string packagePath, string outputExePath)
    {
        var launcher = Path.GetFullPath(launcherExePath);
        var package = Path.GetFullPath(packagePath);
        var output = Path.GetFullPath(outputExePath);
        if (output.Equals(launcher, StringComparison.OrdinalIgnoreCase) || output.Equals(package, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bundle output must differ from its inputs.");
        using (var reader = PackageReader.Open(package)) reader.VerifyAsync().GetAwaiter().GetResult();
        PackageSafety.EnsureNoReparsePoints(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var input = File.OpenRead(launcher)) input.CopyTo(destination);
                var offset = destination.Position;
                using (var input = OpenPackageStream(package))
                {
                    var digest = SHA256.HashData(input);
                    input.Position = 0;
                    input.CopyTo(destination);
                    var length = destination.Position - offset;
                    destination.Write(Header);
                    using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
                    writer.Write(offset);
                    writer.Write(length);
                    writer.Write(digest);
                    writer.Write(Magic);
                }
            }
            using (var verify = PackageReader.Open(temporary)) verify.VerifyAsync().GetAwaiter().GetResult();
            File.Move(temporary, output, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool HasEmbeddedPackage(string path)
    {
        using var file = File.OpenRead(path);
        return TryReadFooter(file, out _, out _, out _);
    }

    internal static Stream OpenPackageStream(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!TryReadFooter(file, out var offset, out var length, out var digest)) { file.Position = 0; return file; }
            var bounded = new BoundedReadStream(file, offset, length);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bounded), digest))
                throw new CryptographicException("Embedded package digest mismatch.");
            bounded.Position = 0;
            return bounded;
        }
        catch { file.Dispose(); throw; }
    }

    private static bool TryReadFooter(Stream file, out long offset, out long length, out byte[] digest)
    {
        offset = length = 0; digest = [];
        if (TryReadFooterAt(file, file.Length, out offset, out length, out digest)) return true;
        // Authenticode appends an aligned WIN_CERTIFICATE table after an existing overlay.
        // Locate the footer before that table, without interpreting certificate bytes as package data.
        if (TryGetCertificateOffset(file, out var certificateOffset))
            for (var padding = 0; padding < 8; padding++)
            {
                var end = certificateOffset - padding;
                if (end < FooterSize) break;
                var zeros = true;
                file.Position = end;
                for (var i = 0; i < padding; i++) if (file.ReadByte() != 0) { zeros = false; break; }
                if (zeros && TryReadFooterAt(file, end, out offset, out length, out digest)) return true;
            }
        return false;
    }

    private static bool TryReadFooterAt(Stream file, long footerEnd, out long offset, out long length, out byte[] digest)
    {
        offset = length = 0; digest = [];
        if (footerEnd < FooterSize || footerEnd > file.Length) return false;
        file.Position = footerEnd - 8;
        Span<byte> magic = stackalloc byte[8]; file.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) return false;
        file.Position = footerEnd - FooterSize;
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(8).SequenceEqual(Header)) throw new InvalidDataException("Invalid embedded package footer.");
        offset = reader.ReadInt64(); length = reader.ReadInt64(); digest = reader.ReadBytes(32);
        if (offset < 0 || length < 1 || offset > footerEnd - FooterSize || length != footerEnd - FooterSize - offset)
            throw new InvalidDataException("Invalid embedded package bounds.");
        return true;
    }

    private static bool TryGetCertificateOffset(Stream file, out long certificateOffset)
    {
        certificateOffset = 0;
        if (file.Length < 64) return false;
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        file.Position = 0;
        if (reader.ReadUInt16() != 0x5a4d) return false;
        file.Position = 0x3c;
        var peOffset = reader.ReadInt32();
        if (peOffset < 64 || peOffset > file.Length - 24) return false;
        file.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) return false;
        file.Position = peOffset + 20;
        var optionalSize = reader.ReadUInt16();
        var optionalStart = (long)peOffset + 24;
        if (optionalSize < 2 || optionalStart > file.Length - optionalSize) return false;
        file.Position = optionalStart;
        var magic = reader.ReadUInt16();
        var directoriesOffset = magic switch { 0x10b => 96, 0x20b => 112, _ => 0 };
        if (directoriesOffset == 0 || optionalSize < directoriesOffset + 40) return false;
        file.Position = optionalStart + directoriesOffset - 4;
        if (reader.ReadUInt32() < 5) return false;
        file.Position = optionalStart + directoriesOffset + 32;
        var offset = reader.ReadUInt32();
        var size = reader.ReadUInt32();
        if (offset < optionalStart + optionalSize || offset % 8 != 0 || size < 8 || (long)offset + size != file.Length) return false;
        certificateOffset = offset;
        return true;
    }

    private sealed class BoundedReadStream(Stream inner, long offset, long length) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set { if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value)); position = value; } }
        public override int Read(byte[] buffer, int bufferOffset, int count) => Read(buffer.AsSpan(bufferOffset, count));
        public override int Read(Span<byte> buffer)
        {
            inner.Position = offset + position;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, length - position)]);
            position += read; return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            inner.Position = offset + position;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - position)], cancellationToken);
            position += read; return read;
        }
        public override long Seek(long value, SeekOrigin origin) { Position = origin switch { SeekOrigin.Begin => value, SeekOrigin.Current => position + value, SeekOrigin.End => length + value, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }; return position; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offsetValue, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
