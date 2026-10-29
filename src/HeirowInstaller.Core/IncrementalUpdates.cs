using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>A signed transition between two signed native packages. Payload entries contain changed files only.</summary>
public sealed class IncrementalUpdateManifest
{
    public int FormatVersion { get; set; } = 1;
    public string ProductId { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string BaseManifestSha256 { get; set; } = "";
    public string TargetManifestSha256 { get; set; } = "";
    public string PublisherKeySha256 { get; set; } = "";
    public List<IncrementalFileChange> Changes { get; set; } = [];
}

public sealed class IncrementalFileChange
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? OldSha256 { get; set; }
    public long? OldSize { get; set; }
    public string? NewSha256 { get; set; }
    public long? NewSize { get; set; }
    public string? OldTargetRoot { get; set; }
    public string? NewTargetRoot { get; set; }
}

public static class IncrementalUpdateBuilder
{
    public static IncrementalUpdateManifest Build(string oldSignedPackage, string newSignedPackage, string outputPath,
        PackageBuildOptions options) => BuildAsync(oldSignedPackage, newSignedPackage, outputPath, options).GetAwaiter().GetResult();

    public static async Task<IncrementalUpdateManifest> BuildAsync(string oldSignedPackage, string newSignedPackage,
        string outputPath, PackageBuildOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SigningKeyPath is not { Length: > 0 } signingKey) throw new CryptographicException("Incremental updates require the package publisher's signing key.");
        outputPath = Path.GetFullPath(outputPath);
        if (new[] { oldSignedPackage, newSignedPackage, signingKey }.Any(path => Path.GetFullPath(path).Equals(outputPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Incremental output cannot overwrite an input or signing key.");
        using var oldPackage = PackageReader.Open(oldSignedPackage);
        using var newPackage = PackageReader.Open(newSignedPackage);
        LegalDocumentRules.RequireForCreation(newPackage.Manifest);
        oldPackage.VerifySignature(requireSignature: true);
        newPackage.VerifySignature(oldPackage.PublisherKeySha256, requireSignature: true);
        await oldPackage.VerifyAsync(cancellationToken: cancellationToken);
        await newPackage.VerifyAsync(cancellationToken: cancellationToken);
        var before = IncrementalPackageIO.ReadPackageMetadata(oldSignedPackage, oldPackage.PublisherKeySha256);
        var after = IncrementalPackageIO.ReadPackageMetadata(newSignedPackage, oldPackage.PublisherKeySha256);
        IncrementalPackageIO.ValidatePair(before.Manifest, after.Manifest);
        using var rsa = RSA.Create();
        PackageSafety.EnsureNoReparsePoints(signingKey);
        rsa.ImportFromPem(await File.ReadAllTextAsync(signingKey, cancellationToken));
        var key = IncrementalPackageIO.KeyHash(rsa);
        if (rsa.KeySize < 2048 || !PackageSafety.HashEquals(key, before.PublisherKeySha256))
            throw new CryptographicException("The incremental signing key must match both original package publishers.");
        var transition = new IncrementalUpdateManifest
        {
            ProductId = after.Manifest.Id, FromVersion = before.Manifest.Version, ToVersion = after.Manifest.Version,
            BaseManifestSha256 = IncrementalPackageIO.Hash(before.ManifestBytes),
            TargetManifestSha256 = IncrementalPackageIO.Hash(after.ManifestBytes), PublisherKeySha256 = key,
            Changes = IncrementalPackageIO.Changes(before.Manifest, after.Manifest)
        };
        var transitionBytes = JsonSerializer.SerializeToUtf8Bytes(transition, InstallerJson.Options);
        if (transitionBytes.Length > IncrementalPackageIO.MaximumTransitionBytes) throw new InvalidDataException("Incremental metadata exceeds the supported limit.");
        var signature = JsonSerializer.SerializeToUtf8Bytes(new PackageSignature
        {
            Algorithm = "RSA-PSS-SHA256", PublicKey = rsa.ExportSubjectPublicKeyInfoPem(),
            Signature = Convert.ToBase64String(rsa.SignData(transitionBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        }, InstallerJson.Options);
        PackageSafety.EnsureNoReparsePoints(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = BundleBuilder.OpenPackageStream(newSignedPackage))
            using (var sourceArchive = new ZipArchive(input, ZipArchiveMode.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var sourceEntries = sourceArchive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
                IncrementalPackageIO.WriteEntry(archive, "update.json", transitionBytes);
                IncrementalPackageIO.WriteEntry(archive, "update-signature.json", signature);
                IncrementalPackageIO.WriteEntry(archive, "base/manifest.json", before.ManifestBytes);
                IncrementalPackageIO.WriteEntry(archive, "base/signature.json", before.SignatureBytes);
                IncrementalPackageIO.WriteEntry(archive, "target/manifest.json", after.ManifestBytes);
                IncrementalPackageIO.WriteEntry(archive, "target/signature.json", after.SignatureBytes);
                foreach (var change in transition.Changes.Where(change => change.Kind is "create" or "change"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var source = sourceEntries["payload/" + change.Path].Open();
                    using var destination = IncrementalPackageIO.CreateEntry(archive, "payload/" + change.Path).Open();
                    await IncrementalPackageIO.CopyVerifiedAsync(source, destination, change.NewSize!.Value, change.NewSha256!, cancellationToken);
                }
            }
            using (var verify = IncrementalUpdateReader.Open(temporary))
            {
                verify.VerifySignature(key);
                await verify.VerifyAsync(cancellationToken: cancellationToken);
            }
            File.Move(temporary, outputPath, true);
            return transition;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class IncrementalUpdateReader : IDisposable
{
    private readonly FileStream stream;
    private readonly ZipArchive archive;
    private readonly Dictionary<string, ZipArchiveEntry> entries;
    private readonly byte[] transitionBytes;
    private readonly byte[] transitionSignature;
    private readonly IncrementalUpdateManifest transition;
    private readonly IncrementalPackageMetadata before;
    private readonly IncrementalPackageMetadata after;
    public string PackagePath { get; }
    public string PublisherKeySha256 => after.PublisherKeySha256;
    public IncrementalUpdateManifest Transition => JsonSerializer.Deserialize<IncrementalUpdateManifest>(transitionBytes, InstallerJson.Options)!;
    public PackageManifest BaseManifest => JsonSerializer.Deserialize<PackageManifest>(before.ManifestBytes, InstallerJson.Options)!;
    public PackageManifest TargetManifest => JsonSerializer.Deserialize<PackageManifest>(after.ManifestBytes, InstallerJson.Options)!;

    private IncrementalUpdateReader(string path)
    {
        PackagePath = Path.GetFullPath(path);
        PackageSafety.EnsureNoReparsePoints(PackagePath);
        stream = new(PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);
            entries = IncrementalPackageIO.ReadEntries(archive);
            transitionBytes = IncrementalPackageIO.ReadEntry(entries, "update.json", IncrementalPackageIO.MaximumTransitionBytes);
            transitionSignature = IncrementalPackageIO.ReadEntry(entries, "update-signature.json", IncrementalPackageIO.MaximumSignatureBytes);
            IncrementalPackageIO.ValidateJson(transitionBytes);
            transition = JsonSerializer.Deserialize<IncrementalUpdateManifest>(transitionBytes, InstallerJson.Options) ?? throw new InvalidDataException("Empty incremental transition.");
            before = IncrementalPackageIO.Metadata(entries, "base/");
            after = IncrementalPackageIO.Metadata(entries, "target/");
            IncrementalPackageIO.ValidatePair(before.Manifest, after.Manifest);
            ValidateTransition();
            VerifySignature();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "update.json", "update-signature.json", "base/manifest.json", "base/signature.json", "target/manifest.json", "target/signature.json" };
            foreach (var change in transition.Changes.Where(change => change.Kind is "create" or "change"))
            {
                var name = "payload/" + change.Path;
                allowed.Add(name);
                if (!entries.TryGetValue(name, out var entry) || entry.Length != change.NewSize)
                    throw new InvalidDataException("Incremental payload is missing or its size differs: " + change.Path);
            }
            if (entries.Keys.Any(name => !allowed.Contains(name))) throw new InvalidDataException("Incremental package contains undeclared or unchanged payload entries.");
        }
        catch { stream.Dispose(); throw; }
    }

    public static IncrementalUpdateReader Open(string path) => new(path);

    public void VerifySignature(string? trustedPublisherKeySha256 = null, bool requireSignature = true)
    {
        // Every incremental transition is signed, including both original complete manifests.
        _ = requireSignature;
        var key = IncrementalPackageIO.VerifySignature(transitionBytes, transitionSignature, trustedPublisherKeySha256);
        if (!PackageSafety.HashEquals(key, transition.PublisherKeySha256) ||
            !PackageSafety.HashEquals(key, before.PublisherKeySha256) || !PackageSafety.HashEquals(key, after.PublisherKeySha256))
            throw new CryptographicException("Incremental transition and complete manifests must share the same publisher key.");
    }

    public async Task VerifyAsync(IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        VerifySignature();
        var changes = transition.Changes.Where(change => change.Kind is "create" or "change").ToArray();
        for (var index = 0; index < changes.Length; index++)
        {
            var change = changes[index];
            using var source = entries["payload/" + change.Path].Open();
            await IncrementalPackageIO.CopyVerifiedAsync(source, Stream.Null, change.NewSize!.Value, change.NewSha256!, cancellationToken);
            progress?.Report(new("verify-update", change.Path, index + 1, changes.Length));
        }
    }

    /// <summary>Builds a repairable full package using original signed metadata, installed unchanged files, and changed payload.</summary>
    public async Task<string> ReconstructAsync(InstalledProduct installed, string outputPath, string? trustedPublisherKeySha256 = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        VerifySignature(trustedPublisherKeySha256 ?? installed.PublisherKeySha256);
        if (installed.PublisherKeySha256 is null || !PackageSafety.HashEquals(installed.PublisherKeySha256, PublisherKeySha256))
            throw new CryptographicException("Incremental updates require the installed product's pinned publisher key.");
        if (installed.IsMsi || installed.IsExecutable) throw new NotSupportedException("Opaque MSI and executable backends cannot receive native file updates. Migrate the product to an authored native file package first.");
        if (!installed.Id.Equals(transition.ProductId, StringComparison.OrdinalIgnoreCase) || installed.Version != transition.FromVersion || installed.Scope is not ("user" or "machine"))
            throw new InvalidDataException("Incremental update does not match the installed base version, product, or scope.");
        PackageSafety.EnsureNoReparsePoints(installed.InstallDirectory);
        var owned = installed.Files.ToDictionary(file => PackageSafety.NormalizeRelativePath(file.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var file in before.Manifest.Payload.Where(file => !SharedUpdaterRuntime.IsRuntimePayload(before.Manifest, file.Path) && (file.Feature is null || installed.Features.Contains(file.Feature, StringComparer.OrdinalIgnoreCase))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!owned.TryGetValue(file.Path, out var ownership) || !PackageSafety.HashEquals(ownership.Sha256, file.Sha256) || !string.Equals(ownership.TargetRoot, file.TargetRoot, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Installed receipt does not own the expected base file: " + file.Path);
            IncrementalPackageIO.VerifyFile(PayloadDestinations.ResolveInstalled(installed, ownership), file.Size, file.Sha256);
        }
        using var basePackage = PackageReader.Open(installed.PackagePath);
        basePackage.VerifySignature(PublisherKeySha256, requireSignature: true);
        var cached = IncrementalPackageIO.ReadPackageMetadata(installed.PackagePath, PublisherKeySha256);
        if (!PackageSafety.HashEquals(IncrementalPackageIO.Hash(cached.ManifestBytes), transition.BaseManifestSha256))
            throw new CryptographicException("The installed repair package does not match the signed incremental base manifest.");
        await basePackage.VerifyAsync(cancellationToken: cancellationToken);
        var selected = installed.Features.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in after.Manifest.Features.Where(feature => feature.Required)) selected.Add(feature.Id);
        foreach (var file in after.Manifest.Payload.Where(file => !SharedUpdaterRuntime.IsRuntimePayload(after.Manifest, file.Path) && (file.Feature is null || selected.Contains(file.Feature))))
            if (!owned.ContainsKey(file.Path) && File.Exists(PayloadDestinations.Resolve(after.Manifest, installed.InstallDirectory, installed.Scope, installed.Isolated, file)))
                throw new IOException("An unowned file blocks a new update destination: " + file.Path);
        outputPath = Path.GetFullPath(outputPath);
        if (outputPath.Equals(PackagePath, StringComparison.OrdinalIgnoreCase) || outputPath.Equals(Path.GetFullPath(installed.PackagePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reconstruction cannot overwrite its signed source packages.");
        PackageSafety.EnsureNoReparsePoints(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var baseStream = BundleBuilder.OpenPackageStream(installed.PackagePath))
            using (var baseArchive = new ZipArchive(baseStream, ZipArchiveMode.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var reconstructed = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var oldEntries = baseArchive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
                var changed = transition.Changes.Where(change => change.Kind is "create" or "change").ToDictionary(change => change.Path, StringComparer.OrdinalIgnoreCase);
                IncrementalPackageIO.WriteEntry(reconstructed, "manifest.json", after.ManifestBytes);
                for (var index = 0; index < after.Manifest.Payload.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = after.Manifest.Payload[index];
                    using var source = changed.ContainsKey(file.Path) ? entries["payload/" + file.Path].Open()
                        : owned.TryGetValue(file.Path, out var installedFile) ? new FileStream(PayloadDestinations.ResolveInstalled(installed, installedFile), FileMode.Open, FileAccess.Read, FileShare.Read)
                        // An optional feature never installed on this machine still needs its verified bytes in the repair cache.
                        : oldEntries["payload/" + file.Path].Open();
                    using var destination = IncrementalPackageIO.CreateEntry(reconstructed, "payload/" + file.Path).Open();
                    await IncrementalPackageIO.CopyVerifiedAsync(source, destination, file.Size, file.Sha256, cancellationToken);
                    progress?.Report(new("reconstruct-update", file.Path, index + 1, after.Manifest.Payload.Count));
                }
                IncrementalPackageIO.WriteEntry(reconstructed, "signature.json", after.SignatureBytes);
            }
            using (var verification = PackageReader.Open(temporary))
            {
                verification.VerifySignature(PublisherKeySha256, requireSignature: true);
                await verification.VerifyAsync(cancellationToken: cancellationToken);
            }
            File.Move(temporary, outputPath, false);
            return outputPath;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ValidateTransition()
    {
        if (transition.FormatVersion != 1 || transition.ProductId != after.Manifest.Id || transition.FromVersion != before.Manifest.Version || transition.ToVersion != after.Manifest.Version ||
            !PackageSafety.HashEquals(transition.BaseManifestSha256, IncrementalPackageIO.Hash(before.ManifestBytes)) ||
            !PackageSafety.HashEquals(transition.TargetManifestSha256, IncrementalPackageIO.Hash(after.ManifestBytes)) ||
            !PackageSafety.HashPattern().IsMatch(transition.PublisherKeySha256))
            throw new InvalidDataException("Incremental transition is not bound to its original complete package metadata.");
        var expected = IncrementalPackageIO.Changes(before.Manifest, after.Manifest);
        if (expected.Count != transition.Changes.Count) throw new InvalidDataException("Incremental file changes are incomplete or duplicated.");
        var actual = new Dictionary<string, IncrementalFileChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in transition.Changes)
            if (PackageSafety.NormalizeRelativePath(change.Path) != change.Path || !actual.TryAdd(change.Path, change)) throw new InvalidDataException("Duplicate or unsafe incremental destination.");
        foreach (var change in expected)
            if (!actual.TryGetValue(change.Path, out var value) || value.Kind != change.Kind || value.Path != change.Path ||
                value.OldSize != change.OldSize || value.NewSize != change.NewSize || value.OldTargetRoot != change.OldTargetRoot || value.NewTargetRoot != change.NewTargetRoot || !IncrementalPackageIO.NullableHashEquals(value.OldSha256, change.OldSha256) ||
                !IncrementalPackageIO.NullableHashEquals(value.NewSha256, change.NewSha256)) throw new InvalidDataException("Incremental old/new file metadata differs from its signed complete manifests.");
    }

    public void Dispose() { archive.Dispose(); stream.Dispose(); }
}

internal sealed record IncrementalPackageMetadata(byte[] ManifestBytes, byte[] SignatureBytes, PackageManifest Manifest, string PublisherKeySha256);

internal static class IncrementalPackageIO
{
    internal const int MaximumManifestBytes = 4 * 1024 * 1024;
    internal const int MaximumSignatureBytes = 64 * 1024;
    internal const int MaximumTransitionBytes = 8 * 1024 * 1024;
    internal const long MaximumPayloadBytes = 16L * 1024 * 1024 * 1024;
    internal const int MaximumEntries = 100000;

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string KeyHash(RSA rsa) => Hash(rsa.ExportSubjectPublicKeyInfo());
    internal static bool NullableHashEquals(string? left, string? right) => left is null ? right is null : right is not null && PackageSafety.HashEquals(left, right);

    internal static void ValidatePair(PackageManifest before, PackageManifest after)
    {
        PayloadDestinations.ValidateUpgrade(before, after);
        if (before.LegacyMsi is not null || before.LegacyExecutable is not null || after.LegacyMsi is not null || after.LegacyExecutable is not null)
            throw new NotSupportedException("Incremental packages require native file manifests; opaque MSI/executable installers cannot be diffed or used as repair registration.");
        if (before.Id != after.Id || before.Scope != after.Scope || PackageVersion.Parse(after.Version).CompareTo(PackageVersion.Parse(before.Version)) <= 0)
            throw new InvalidDataException("An incremental update requires the same product/scope and a newer target version.");
        foreach (var manifest in new[] { before, after })
        {
            if (manifest.Payload.Count > MaximumEntries - 6) throw new InvalidDataException("Too many incremental payload files.");
            long total = 0;
            foreach (var file in manifest.Payload)
            {
                if (file.Size > MaximumPayloadBytes - total) throw new InvalidDataException("Incremental payload exceeds the supported expanded size.");
                total += file.Size;
            }
        }
    }

    internal static List<IncrementalFileChange> Changes(PackageManifest before, PackageManifest after)
    {
        var oldFiles = before.Payload.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var newFiles = after.Payload.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var changes = new List<IncrementalFileChange>();
        foreach (var file in after.Payload)
        {
            oldFiles.TryGetValue(file.Path, out var old);
            if (old is not null && old.Size == file.Size && PackageSafety.HashEquals(old.Sha256, file.Sha256) && old.Path == file.Path) continue;
            changes.Add(new() { Path = file.Path, Kind = old is null ? "create" : "change", OldSha256 = old?.Sha256, OldSize = old?.Size, NewSha256 = file.Sha256, NewSize = file.Size, OldTargetRoot = old?.TargetRoot, NewTargetRoot = file.TargetRoot });
        }
        foreach (var file in before.Payload.Where(file => !newFiles.ContainsKey(file.Path)))
            changes.Add(new() { Path = file.Path, Kind = "remove", OldSha256 = file.Sha256, OldSize = file.Size, OldTargetRoot = file.TargetRoot });
        return changes.OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static Dictionary<string, ZipArchiveEntry> ReadEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("Too many incremental ZIP entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.StartsWith("payload/", StringComparison.Ordinal)
                ? "payload/" + PackageSafety.NormalizeRelativePath(entry.FullName[8..]) : PackageSafety.NormalizeRelativePath(entry.FullName);
            if (name != entry.FullName || entry.Length < 0 || entry.Length > MaximumPayloadBytes - expanded ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                !entries.TryAdd(name, entry)) throw new InvalidDataException("Unsafe, duplicate, linked, or oversized incremental ZIP entry.");
            expanded += entry.Length;
        }
        return entries;
    }

    internal static byte[] ReadEntry(Dictionary<string, ZipArchiveEntry> entries, string name, int maximumBytes)
    {
        if (!entries.TryGetValue(name, out var entry) || entry.Length > maximumBytes) throw new InvalidDataException("Missing or oversized incremental metadata: " + name);
        using var source = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = source.Read(buffer)) > 0)
        {
            if (output.Length + count > maximumBytes) throw new InvalidDataException("Incremental metadata exceeds its limit.");
            output.Write(buffer, 0, count);
        }
        if (output.Length != entry.Length) throw new InvalidDataException("Incremental metadata length differs.");
        return output.ToArray();
    }

    internal static IncrementalPackageMetadata Metadata(Dictionary<string, ZipArchiveEntry> entries, string prefix)
    {
        var manifest = ReadEntry(entries, prefix + "manifest.json", MaximumManifestBytes);
        var signature = ReadEntry(entries, prefix + "signature.json", MaximumSignatureBytes);
        ValidateJson(manifest);
        var parsed = JsonSerializer.Deserialize<PackageManifest>(manifest, InstallerJson.Options) ?? throw new InvalidDataException("Empty complete manifest.");
        PackageSafety.ValidateManifest(parsed, true);
        return new(manifest, signature, parsed, VerifySignature(manifest, signature));
    }

    internal static IncrementalPackageMetadata ReadPackageMetadata(string path, string? publisherPin)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        using var source = BundleBuilder.OpenPackageStream(path);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        var result = Metadata(entries, "");
        if (publisherPin is not null && !PackageSafety.HashEquals(publisherPin, result.PublisherKeySha256)) throw new CryptographicException("Complete package publisher changed.");
        return result;
    }

    internal static string VerifySignature(byte[] data, byte[] signatureBytes, string? publisherPin = null)
    {
        ValidateJson(signatureBytes);
        var signature = JsonSerializer.Deserialize<PackageSignature>(signatureBytes, InstallerJson.Options) ?? throw new InvalidDataException("Missing incremental signature.");
        if (signature.Algorithm != "RSA-PSS-SHA256") throw new CryptographicException("Unsupported incremental signing algorithm.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(signature.PublicKey);
        if (rsa.KeySize < 2048 || !rsa.VerifyData(data, Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Incremental or original complete manifest signature is invalid.");
        var key = KeyHash(rsa);
        if (publisherPin is not null && !PackageSafety.HashEquals(publisherPin, key)) throw new CryptographicException("Incremental publisher does not match the trusted key.");
        return key;
    }

    internal static void ValidateJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        Check(document.RootElement);
        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON metadata property.");
                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Check(value);
        }
    }

    internal static ZipArchiveEntry CreateEntry(ZipArchive archive, string name)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return entry;
    }
    internal static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    { using var output = CreateEntry(archive, name).Open(); output.Write(bytes); }

    internal static async Task CopyVerifiedAsync(Stream source, Stream destination, long expectedLength, string expectedHash, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; long total = 0; int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (count > expectedLength - total) throw new InvalidDataException("Incremental file exceeds its declared size.");
            total += count; hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        if (total != expectedLength || !PackageSafety.HashEquals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash))
            throw new CryptographicException("Incremental file size or SHA256 differs from its signed metadata.");
    }

    internal static void VerifyFile(string path, long length, string hash)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        if (!File.Exists(path) || new FileInfo(path).Length != length || !PackageSafety.HashEquals(PackageSafety.HashFile(path), hash))
            throw new CryptographicException("Installed base file changed or is missing: " + Path.GetFileName(path));
    }
}
