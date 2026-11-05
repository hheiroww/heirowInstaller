using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>The original signed release metadata. A file selection never grants new destination authority.</summary>
public sealed class FileUpdateMetadata
{
    private readonly byte[] manifest;
    private readonly byte[] signature;
    public PackageManifest Manifest => JsonSerializer.Deserialize<PackageManifest>(manifest, InstallerJson.Options)!;
    public string ManifestSha256 { get; }
    public string PublisherKeySha256 { get; }
    public byte[] ManifestBytes => manifest.ToArray();
    public byte[] SignatureBytes => signature.ToArray();

    private FileUpdateMetadata(byte[] manifest, byte[] signature, string publisher, string? expectedDigest)
    {
        if (manifest.Length is <= 0 or > 4 * 1024 * 1024 || signature.Length is <= 0 or > 64 * 1024)
            throw new InvalidDataException("Signed file metadata exceeds its supported size.");
        IncrementalPackageIO.ValidateJson(manifest); IncrementalPackageIO.ValidateJson(signature);
        PublisherKeySha256 = IncrementalPackageIO.VerifySignature(manifest, signature, publisher);
        ManifestSha256 = IncrementalPackageIO.Hash(manifest);
        if (expectedDigest is not null && !PackageSafety.HashEquals(ManifestSha256, expectedDigest))
            throw new CryptographicException("The target signed manifest differs from its update offer.");
        this.manifest = manifest.ToArray(); this.signature = signature.ToArray();
        var value = Manifest;
        PackageSafety.ValidateManifest(value, true); PackagePlatform.RequireCurrentHost(value);
        if (value.LegacyMsi is not null || value.LegacyExecutable is not null)
            throw new NotSupportedException("Directory file updates require native installed ownership.");
    }

    public static FileUpdateMetadata ReadEnvelope(ReadOnlySpan<byte> json, string publisher, string? expectedDigest = null)
    {
        if (json.Length > 6 * 1024 * 1024) throw new InvalidDataException("File manifest response exceeds its supported size.");
        IncrementalPackageIO.ValidateJson(json.ToArray());
        var envelope = JsonSerializer.Deserialize<MetadataEnvelope>(json, InstallerJson.Options)
            ?? throw new InvalidDataException("Empty signed file manifest response.");
        try { return FromBytes(Convert.FromBase64String(envelope.ManifestBase64), Convert.FromBase64String(envelope.SignatureBase64), publisher, expectedDigest); }
        catch (FormatException error) { throw new InvalidDataException("Signed file metadata is not valid base64.", error); }
    }

    public static FileUpdateMetadata FromBytes(byte[] manifest, byte[] signature, string publisher, string? expectedDigest = null)
        => new(manifest, signature, publisher, expectedDigest);

    public void WriteMetadataPackage(string path, FileUpdateSource? source = null)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        IncrementalPackageIO.WriteEntry(zip, "manifest.json", manifest);
        IncrementalPackageIO.WriteEntry(zip, "signature.json", signature);
        if (source is not null)
        {
            source.Validate(this);
            IncrementalPackageIO.WriteEntry(zip, "file-source.json", JsonSerializer.SerializeToUtf8Bytes(source, InstallerJson.Options));
        }
    }

    internal void VerifyArchiveMetadata(Dictionary<string, ZipArchiveEntry> entries)
    {
        var actualManifest = IncrementalPackageIO.ReadEntry(entries, "manifest.json", 4 * 1024 * 1024);
        var actualSignature = IncrementalPackageIO.ReadEntry(entries, "signature.json", 64 * 1024);
        if (!manifest.AsSpan().SequenceEqual(actualManifest) || !signature.AsSpan().SequenceEqual(actualSignature))
            throw new CryptographicException("The downloaded file selection has different signed target metadata.");
    }

    private sealed class MetadataEnvelope
    {
        public string ManifestBase64 { get; set; } = "";
        public string SignatureBase64 { get; set; } = "";
    }
}

/// <summary>A repair download hint. Signature pins and file hashes remain authoritative; URLs never grant trust.</summary>
public sealed class FileUpdateSource
{
    public string Format { get; set; } = "heirowInstaller.file-source/1";
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string TargetManifestSha256 { get; set; } = "";
    public string ManifestUrl { get; set; } = "";
    public string FilesUrl { get; set; } = "";
    internal void Validate(FileUpdateMetadata metadata)
    {
        if (Format != "heirowInstaller.file-source/1" || ProductId != metadata.Manifest.Id || Version != metadata.Manifest.Version ||
            !PackageSafety.HashEquals(TargetManifestSha256, metadata.ManifestSha256)) throw new InvalidDataException("The cached file repair source differs from its signed target.");
        if (!Uri.TryCreate(metadata.Manifest.UpdateUrl, UriKind.Absolute, out var signedOrigin))
            throw new InvalidDataException("Selected-file repair requires a signed update feed origin.");
        var expectedManifest = new Uri(signedOrigin, Uri.EscapeDataString(Version) + "/manifest.json");
        var expectedFiles = new Uri(signedOrigin, Uri.EscapeDataString(Version) + "/files.zip");
        if (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var manifestEndpoint) || manifestEndpoint != expectedManifest ||
            !Uri.TryCreate(FilesUrl, UriKind.Absolute, out var filesEndpoint) || filesEndpoint != expectedFiles)
            throw new CryptographicException("Cached file repair endpoints must be the exact versioned siblings of the signed update feed.");
        foreach (var value in new[] { ManifestUrl, FilesUrl })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback) || uri.UserInfo.Length != 0)
                throw new InvalidDataException("Cached file repair sources require HTTPS.");
            if (uri.Scheme != signedOrigin.Scheme || uri.Host != signedOrigin.Host || uri.Port != signedOrigin.Port)
                throw new CryptographicException("A cached file repair endpoint must retain the signed update origin.");
        }
    }
    public static FileUpdateSource Read(string metadataPackage, string publisher)
    {
        using var stream = BundleBuilder.OpenPackageStream(metadataPackage); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = IncrementalPackageIO.ReadEntries(zip);
        var metadata = FileUpdateMetadata.FromBytes(IncrementalPackageIO.ReadEntry(entries, "manifest.json", 4 * 1024 * 1024),
            IncrementalPackageIO.ReadEntry(entries, "signature.json", 64 * 1024), publisher);
        var bytes = IncrementalPackageIO.ReadEntry(entries, "file-source.json", 64 * 1024); IncrementalPackageIO.ValidateJson(bytes);
        var source = JsonSerializer.Deserialize<FileUpdateSource>(bytes, InstallerJson.Options) ?? throw new InvalidDataException("Missing online repair source.");
        source.Validate(metadata); return source;
    }
    public UpdateFeed ToFeed() => new() { ProductId = ProductId, Version = Version, PackageType = "hfiles", ManifestUrl = ManifestUrl,
        FilesUrl = FilesUrl, TargetManifestSha256 = TargetManifestSha256 };
}
