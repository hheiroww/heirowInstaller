using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Core;

/// <summary>Verified, resumable changed-file stage. No installed application is closed while this stage is prepared.</summary>
public sealed class PreparedFileUpdate : IDisposable
{
    private readonly FileStream lease;
    private readonly PreparedFileUpdateState state;
    public string Root { get; }
    public string MetadataPath => Path.Combine(Root, "target.hmeta");
    public FileUpdateMetadata Metadata { get; }
    public IReadOnlyList<string> RequestedPaths => state.RequestedPaths.AsReadOnly();
    public IReadOnlyList<string> SelectedFeatures => state.Features.AsReadOnly();
    internal IReadOnlyDictionary<string, FileUpdateSnapshot> Before => state.Before;
    internal InstalledProduct Installed { get; }
    internal string BaseManifestSha256 => state.BaseManifestSha256;
    internal string ReceiptSha256 => state.ReceiptSha256;
    internal FileUpdateSnapshot MetadataCacheBefore => state.MetadataCacheBefore;
    internal string Phase => state.Phase;
    public bool IsReady => state.Phase is "ready" or "applying" or "complete";

    private PreparedFileUpdate(string root, FileUpdateMetadata metadata, InstalledProduct installed, PreparedFileUpdateState state, bool create)
    {
        Root = Path.GetFullPath(root); Metadata = metadata; Installed = installed; this.state = state;
        PackageSafety.EnsureNoReparsePoints(Root); Directory.CreateDirectory(Root);
        lease = new(Path.Combine(Root, "stage.lease"), create ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    internal static PreparedFileUpdate Create(FileUpdateMetadata metadata, InstalledProduct installed, string parent, InstallOptions options, UpdateFeed feed)
    {
        parent = Path.GetFullPath(parent); PackageSafety.EnsureNoReparsePoints(parent);
        using var old = PackageReader.Open(installed.PackagePath); old.VerifySignature(installed.PublisherKeySha256, true);
        if (old.Manifest.Id != installed.Id || old.Manifest.Version != installed.Version || old.Manifest.Scope != installed.Scope)
            throw new CryptographicException("The installed receipt differs from its signed metadata.");
        PayloadDestinations.ValidateUpgrade(old.Manifest, metadata.Manifest);
        var root = PackageSafety.UnderRoot(parent, installed.Id + "/" + Guid.NewGuid().ToString("N"));
        var state = new PreparedFileUpdateState
        {
            ProductId = installed.Id, Scope = installed.Scope, InstallDirectory = Path.GetFullPath(installed.InstallDirectory),
            TargetManifestSha256 = metadata.ManifestSha256, BaseManifestSha256 = IncrementalPackageIO.Hash(old.ReadManifestBytes()),
            ReceiptSha256 = PackageSafety.HashFile(installed.ReceiptPath), BaseVersion = installed.Version,
            StateRoot = options.StateRoot!, PublisherKeySha256 = installed.PublisherKeySha256!
        };
        var cacheMetadata = PackageSafety.UnderRoot(options.StateRoot!, installed.Id + "/metadata/" + metadata.ManifestSha256 + ".hmeta");
        state.MetadataCacheBefore = File.Exists(cacheMetadata) ? new() { Sha256 = PackageSafety.HashFile(cacheMetadata), Size = new FileInfo(cacheMetadata).Length } : new();
        var stage = new PreparedFileUpdate(root, metadata, installed, state, true);
        try
        {
            metadata.WriteMetadataPackage(stage.MetadataPath, new() { ProductId = installed.Id, Version = metadata.Manifest.Version,
                TargetManifestSha256 = metadata.ManifestSha256, ManifestUrl = feed.ManifestUrl!, FilesUrl = feed.FilesUrl! });
            File.Copy(installed.ReceiptPath, Path.Combine(root, "base-receipt.json"));
            return stage;
        }
        catch { stage.Dispose(); throw; }
    }

    /// <summary>Reopens only a signed target bound to this exact installed identity. Stored paths never grant destination authority.</summary>
    public static PreparedFileUpdate Open(string root, InstalledProduct installed, InstallOptions? options = null)
    {
        options ??= new(); FileUpdateClient.BindOptions(installed, options);
        root = Path.GetFullPath(root); PackageSafety.EnsureNoReparsePoints(root);
        var statePath = Path.Combine(root, "prepared.json"); PackageSafety.EnsureNoReparsePoints(statePath);
        if (new FileInfo(statePath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Oversized prepared file inventory.");
        var bytes = File.ReadAllBytes(statePath); IncrementalPackageIO.ValidateJson(bytes);
        var state = JsonSerializer.Deserialize<PreparedFileUpdateState>(bytes, InstallerJson.Options) ?? throw new InvalidDataException("Empty prepared file inventory.");
        if (state.Format != "heirowInstaller.files/1" || state.ProductId != installed.Id || state.Scope != installed.Scope ||
            !state.InstallDirectory.Equals(Path.GetFullPath(installed.InstallDirectory), InstallerPlatform.PathComparison) ||
            !state.StateRoot.Equals(Path.GetFullPath(options.StateRoot!), InstallerPlatform.PathComparison) ||
            !PackageSafety.HashEquals(state.PublisherKeySha256, installed.PublisherKeySha256!) ||
            state.Phase is not ("ready" or "applying" or "complete") || state.Before.Count > 100000 || state.RequestedPaths.Count > 100000)
            throw new InvalidDataException("The prepared update belongs to a different installation or is incomplete.");
        using var package = PackageReader.OpenMetadata(Path.Combine(root, "target.hmeta")); package.VerifySignature(installed.PublisherKeySha256, true);
        var metadata = FileUpdateMetadata.FromBytes(package.ReadManifestBytes(), ReadSignature(Path.Combine(root, "target.hmeta")), installed.PublisherKeySha256!, state.TargetManifestSha256);
        if (metadata.Manifest.Id != installed.Id || metadata.Manifest.Scope != installed.Scope ||
            PackageVersion.Parse(metadata.Manifest.Version).CompareTo(PackageVersion.Parse(installed.Version)) < 0)
            throw new InvalidDataException("The prepared target identity is invalid.");
        var declared = metadata.Manifest.Payload.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        if (state.RequestedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.RequestedPaths.Count || state.RequestedPaths.Any(path => !declared.ContainsKey(path)))
            throw new InvalidDataException("The prepared selection contains undeclared or duplicate paths.");
        var originalReceipt = Path.Combine(root, "base-receipt.json"); PackageSafety.EnsureNoReparsePoints(originalReceipt);
        if (new FileInfo(originalReceipt).Length > 16 * 1024 * 1024 || !PackageSafety.HashEquals(PackageSafety.HashFile(originalReceipt), state.ReceiptSha256))
            throw new CryptographicException("The prepared base receipt changed.");
        var original = JsonSerializer.Deserialize<InstalledProduct>(File.ReadAllBytes(originalReceipt), InstallerJson.Options)
            ?? throw new InvalidDataException("Missing prepared base installation.");
        original.ReceiptPath = installed.ReceiptPath;
        var cache = Path.GetDirectoryName(installed.ReceiptPath)!;
        if (original.Id != state.ProductId || original.Version != state.BaseVersion || original.Scope != state.Scope ||
            !Path.GetFullPath(original.InstallDirectory).Equals(state.InstallDirectory, InstallerPlatform.PathComparison) ||
            !Path.GetFullPath(original.PackagePath).StartsWith(cache + Path.DirectorySeparatorChar, InstallerPlatform.PathComparison))
            throw new InvalidDataException("The prepared base receipt escapes this installation.");
        return new(root, metadata, original, state, false);
    }

    private static byte[] ReadSignature(string path)
    {
        using var stream = BundleBuilder.OpenPackageStream(path); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return IncrementalPackageIO.ReadEntry(IncrementalPackageIO.ReadEntries(zip), "signature.json", 64 * 1024);
    }

    internal async Task InspectAsync(InstallPlan plan, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        state.Features = plan.Features.ToList();
        using var old = PackageReader.Open(Installed.PackagePath); old.VerifySignature(Installed.PublisherKeySha256, true);
        var owned = Installed.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var oldDeclared = old.Manifest.Payload.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Installed.Files)
        {
            token.ThrowIfCancellationRequested();
            if (oldDeclared.TryGetValue(file.Path, out var authored))
            {
                if (!PackageSafety.HashEquals(authored.Sha256, file.Sha256) || !string.Equals(authored.TargetRoot, file.TargetRoot, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Installed ownership differs from the signed base payload.");
            }
            else if (old.Manifest.UpdateCompanion?.ConfigurationPath != file.Path && file.Path != SharedPackages.BindingFileName &&
                file.Path != SharedPackages.ProbingConfigurationPath(old.Manifest))
                throw new CryptographicException("The installed receipt contains an undeclared owned path.");
            var path = PayloadDestinations.ResolveInstalled(Installed, file);
            state.Before.Add(file.Path, await ObserveAsync(path, token));
        }
        for (var index = 0; index < plan.Files.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var file = plan.Files[index];
            var source = ExistingSource(file, plan);
            if (!SharedUpdaterRuntime.IsRuntimePayload(plan.Manifest, file.Path) && !owned.ContainsKey(file.Path) && File.Exists(source))
                throw new IOException("An unowned file blocks a new update destination: " + file.Path);
            var actual = await ObserveAsync(source, token);
            if (!Matches(actual, file)) state.RequestedPaths.Add(file.Path);
            progress?.Report(new("diff", file.Path, index + 1, plan.Files.Count));
        }
    }

    internal string ExistingSource(PayloadFile file, InstallPlan plan)
    {
        if (!SharedUpdaterRuntime.IsRuntimePayload(plan.Manifest, file.Path))
            return PayloadDestinations.Resolve(plan.Manifest, Installed.InstallDirectory, Installed.Scope, Installed.Isolated, file);
        using var previous = PackageReader.Open(Installed.PackagePath); previous.VerifySignature(Installed.PublisherKeySha256, true);
        if (previous.Manifest.UpdateCompanion is { RuntimeMode: "shared" })
            return SharedUpdaterRuntime.Resolve(InstallerEngine.ReadBoundCompanionConfiguration(Installed, previous.Manifest));
        return PackageSafety.UnderRoot(Installed.InstallDirectory, file.Path);
    }
    internal string Source(PayloadFile file, InstallPlan plan) => state.RequestedPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase)
        ? PackageSafety.UnderRoot(Path.Combine(Root, "payload"), file.Path) : ExistingSource(file, plan);

    internal async Task ReceiveAsync(HttpResponseMessage response, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        const long maximum = 2L * 1024 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("The selected file ZIP is too large.");
        var archivePath = Path.Combine(Root, "selection.zip");
        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
        {
            var buffer = new byte[131072]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                if (count > maximum - total) throw new InvalidDataException("The selected file ZIP exceeds its supported size.");
                total += count; await output.WriteAsync(buffer.AsMemory(0, count), token);
                var expected = response.Content.Headers.ContentLength;
                progress?.Report(new("download", $"Downloaded {total:N0} bytes", expected is > 0 ? (int)Math.Min(1000, total * 1000 / expected.Value) : 0, expected is > 0 ? 1000 : 0));
            }
            if (response.Content.Headers.ContentLength is { } declared && declared != total) throw new InvalidDataException("Incomplete selected file ZIP.");
        }
        using var zip = ZipFile.OpenRead(archivePath); var entries = IncrementalPackageIO.ReadEntries(zip);
        Metadata.VerifyArchiveMetadata(entries);
        var files = Metadata.Manifest.Payload.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var allowed = state.RequestedPaths.Select(path => "payload/" + path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        allowed.Add("manifest.json"); allowed.Add("signature.json");
        if (entries.Count != allowed.Count || entries.Keys.Any(path => !allowed.Contains(path)))
            throw new InvalidDataException("The selected ZIP contains missing, unrequested or undeclared files.");
        long expanded = 0;
        for (var index = 0; index < state.RequestedPaths.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var file = files[state.RequestedPaths[index]];
            if (file.Size > 16L * 1024 * 1024 * 1024 - expanded) throw new InvalidDataException("The selected files exceed the expanded size limit.");
            expanded += file.Size;
            if (!entries.TryGetValue("payload/" + file.Path, out var entry) || entry.Length != file.Size)
                throw new InvalidDataException("Missing selected file or signed size mismatch: " + file.Path);
            var destination = PackageSafety.UnderRoot(Path.Combine(Root, "payload"), file.Path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open(); await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            await IncrementalPackageIO.CopyVerifiedAsync(input, output, file.Size, file.Sha256, token);
            if (!OperatingSystem.IsWindows() && file.UnixMode is { } mode) File.SetUnixFileMode(destination, (UnixFileMode)mode);
            progress?.Report(new("stage", file.Path, index + 1, state.RequestedPaths.Count));
        }
    }

    internal async Task VerifySourcesAsync(InstallPlan plan, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        for (var index = 0; index < plan.Files.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var file = plan.Files[index];
            if (!Matches(await ObserveAsync(Source(file, plan), token), file)) throw new CryptographicException("A prepared update source changed: " + file.Path);
            progress?.Report(new("verify-stage", file.Path, index + 1, plan.Files.Count));
        }
    }

    internal void ValidateReceipt(InstalledProduct installed)
    {
        if (installed.Version != state.BaseVersion || installed.Scope != state.Scope || !installed.InstallDirectory.Equals(state.InstallDirectory, InstallerPlatform.PathComparison) ||
            !PackageSafety.HashEquals(PackageSafety.HashFile(installed.ReceiptPath), state.ReceiptSha256))
            throw new InvalidDataException("The installed application changed while its file update was being staged.");
        using var package = PackageReader.Open(installed.PackagePath); package.VerifySignature(state.PublisherKeySha256, true);
        if (!PackageSafety.HashEquals(IncrementalPackageIO.Hash(package.ReadManifestBytes()), state.BaseManifestSha256))
            throw new CryptographicException("The installed signed base changed while its update was staged.");
    }
    internal async Task MarkReadyAsync(CancellationToken token) { state.Phase = "ready"; await SaveAsync(token); }
    internal async Task MarkApplyingAsync(CancellationToken token) { state.Phase = "applying"; await SaveAsync(token); }
    internal async Task MarkCompleteAsync() { state.Phase = "complete"; await SaveAsync(CancellationToken.None); }
    private async Task SaveAsync(CancellationToken token)
    {
        var path = Path.Combine(Root, "prepared.json"); var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(state, InstallerJson.Options), token);
        using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
        File.Move(temporary, path, true);
    }
    internal static async Task<FileUpdateSnapshot> ObserveAsync(string path, CancellationToken token)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return new();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new() { Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant(), Size = file.Length,
            UnixMode = OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path) };
    }
    private static bool Matches(FileUpdateSnapshot actual, PayloadFile file) => actual.Size == file.Size && actual.Sha256 is not null &&
        PackageSafety.HashEquals(actual.Sha256, file.Sha256) && (OperatingSystem.IsWindows() || file.UnixMode is null || actual.UnixMode == file.UnixMode);
    public void Dispose() => lease.Dispose();
}

internal sealed class FileUpdateSnapshot
{
    public string? Sha256 { get; set; }
    public long? Size { get; set; }
    public int? UnixMode { get; set; }
}
internal sealed class PreparedFileUpdateState
{
    public string Format { get; set; } = "heirowInstaller.files/1";
    public string Phase { get; set; } = "preparing";
    public string ProductId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string StateRoot { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string PublisherKeySha256 { get; set; } = "";
    public string BaseVersion { get; set; } = "";
    public string BaseManifestSha256 { get; set; } = "";
    public string TargetManifestSha256 { get; set; } = "";
    public string ReceiptSha256 { get; set; } = "";
    public FileUpdateSnapshot MetadataCacheBefore { get; set; } = new();
    public List<string> RequestedPaths { get; set; } = [];
    public List<string> Features { get; set; } = [];
    public Dictionary<string, FileUpdateSnapshot> Before { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
