using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeirowInstaller.Core;

/// <summary>Caller-verified ownership and manifest binding for durable application-file recovery.</summary>
public sealed class FileTransactionRecoveryContext
{
    public string ProductId { get; init; } = "";
    public string StateRoot { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string Scope { get; init; } = "user";
    public string? PublisherKeySha256 { get; init; }
    public string? BaseManifestSha256 { get; init; }
    public string TargetManifestSha256 { get; init; } = "";
    public string? BaseVersion { get; init; }
    public string TargetVersion { get; init; } = "";
    public bool RequireExactBase { get; init; }
    public bool Isolated { get; init; }
    public IReadOnlyList<FileRecoveryTarget> Files { get; init; } = [];
    [JsonIgnore]
    public IReadOnlyList<RunningApplicationTarget> Applications { get; init; } = [];

    /// <summary>Derives targets from an already verified package/plan and the engine's validated receipt, never a journal.</summary>
    public static FileTransactionRecoveryContext ForInstall(PackageReader package, InstallPlan plan, InstalledProduct? previous,
        InstallOptions options, bool requireExactBase = false)
    {
        package.VerifySignature(options.TrustedPublisherKeySha256 ?? previous?.PublisherKeySha256,
            options.RequireSignature || previous?.PublisherKeySha256 is not null);
        if (package.Manifest.Id != plan.Manifest.Id || package.Manifest.Version != plan.Manifest.Version)
            throw new InvalidDataException("Recovery plan differs from its verified package.");
        var stateRoot = Path.GetFullPath(options.StateRoot ?? InstallerEngine.DefaultStateRoot(plan.Scope));
        var installRoot = Path.GetFullPath(plan.InstallDirectory);
        byte[] targetManifest = package.ReadManifestBytes();
        string? baseDigest = null;
        PackageManifest? oldManifest = null;
        if (previous is not null)
        {
            if (previous.Id != package.Manifest.Id || previous.Scope != plan.Scope || !Path.GetFullPath(previous.InstallDirectory).Equals(installRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery receipt differs from the verified installation target.");
            using var old = PackageReader.Open(previous.PackagePath);
            old.VerifySignature(previous.PublisherKeySha256, previous.PublisherKeySha256 is not null);
            if (old.Manifest.Id != previous.Id || old.Manifest.Version != previous.Version) throw new InvalidDataException("Prior repair package identity differs from its validated receipt.");
            var bytes = old.ReadManifestBytes();
            baseDigest = IncrementalPackageIO.Hash(bytes);
            oldManifest = JsonSerializer.Deserialize<PackageManifest>(bytes, InstallerJson.Options)!;
        }
        var signedTarget = JsonSerializer.Deserialize<PackageManifest>(targetManifest, InstallerJson.Options)!;
        if (signedTarget.Modules.ContainsKey(LegacyStagingCleanupModule.ModuleId) && options.TrustedPublisherKeySha256 is null && previous?.PublisherKeySha256 is null)
            throw new CryptographicException("Legacy staging recovery requires an explicitly trusted or previously installed publisher pin.");
        return Create(plan, previous, options, oldManifest, baseDigest, IncrementalPackageIO.Hash(targetManifest), package.PublisherKeySha256, requireExactBase, signedTarget);
    }

    /// <summary>Can be built before reconstruction, so a crashed update can restore its base files first.</summary>
    public static FileTransactionRecoveryContext ForIncrementalUpdate(IncrementalUpdateReader update, InstalledProduct installed, InstallOptions options)
    {
        update.VerifySignature(installed.PublisherKeySha256);
        var transition = update.Transition;
        if (installed.PublisherKeySha256 is null || installed.Id != transition.ProductId || installed.Version != transition.FromVersion || installed.IsMsi || installed.IsExecutable)
            throw new InvalidDataException("File recovery requires the installed native incremental base.");
        var target = update.TargetManifest;
        using var cached = PackageReader.Open(installed.PackagePath);
        cached.VerifySignature(installed.PublisherKeySha256, requireSignature: true);
        if (!PackageSafety.HashEquals(IncrementalPackageIO.Hash(ReadManifest(installed.PackagePath)), transition.BaseManifestSha256))
            throw new CryptographicException("Installed repair metadata differs from the signed incremental base.");
        var selected = (options.Features ?? installed.Features.Where(id => target.Features.Any(feature => feature.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in target.Features.Where(feature => feature.Required)) selected.Add(feature.Id);
        if (selected.Any(id => !target.Features.Any(feature => feature.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("Unknown incremental feature selection.");
        var plan = new InstallPlan
        {
            Manifest = target, InstallDirectory = installed.InstallDirectory, Scope = installed.Scope, Features = selected.ToList(),
            Files = target.Payload.Where(file => file.Feature is null || selected.Contains(file.Feature)).ToList(),
            UpdateCompanionEnabled = target.UpdateCompanion is { } companion && selected.Contains(companion.Feature)
        };
        return Create(plan, installed, options, update.BaseManifest, transition.BaseManifestSha256, transition.TargetManifestSha256, update.PublisherKeySha256, true, update.TargetManifest);
    }

    private static FileTransactionRecoveryContext Create(InstallPlan plan, InstalledProduct? previous, InstallOptions options,
        PackageManifest? oldManifest, string? baseDigest, string targetDigest, string? publisherPin, bool requireExactBase, PackageManifest signedTarget)
    {
        var manifest = plan.Manifest;
        if (oldManifest is not null) PayloadDestinations.ValidateUpgrade(oldManifest, manifest);
        var stateRoot = Path.GetFullPath(options.StateRoot ?? InstallerEngine.DefaultStateRoot(plan.Scope));
        var installRoot = Path.GetFullPath(plan.InstallDirectory);
        var files = new Dictionary<string, FileRecoveryTarget>(StringComparer.OrdinalIgnoreCase);
        // Owned retirement durably removes the entire app/wrapper footprint before native file writes.
        // Its separate authenticated ledger restores those bytes after native rollback; a native transaction's before-image is absence.
        var ownedTransition = previous is not null && oldManifest?.LegacyExecutable is not null &&
            signedTarget.NativeMigration?.Baselines.Any(b => b.Backend == InnoOwnedPolicy.Backend && b.Scope == previous.Scope &&
                b.FromVersion == previous.Version && PackageSafety.HashEquals(b.FromManifestSha256, baseDigest!)) == true;
        var oldFiles = ownedTransition ? new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase) :
            previous?.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase) ?? [];
        var newFiles = plan.Files.Where(file => !SharedUpdaterRuntime.IsRuntimePayload(manifest, file.Path)).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var path in oldFiles.Keys.Union(newFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            oldFiles.TryGetValue(path, out var old);
            newFiles.TryGetValue(path, out var next);
            var authoredOld = oldManifest?.Payload.FirstOrDefault(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (old is not null && authoredOld is not null && (!PackageSafety.HashEquals(old.Sha256, authoredOld.Sha256) || !string.Equals(old.TargetRoot, authoredOld.TargetRoot, StringComparison.OrdinalIgnoreCase)))
                throw new CryptographicException("Recovery receipt ownership differs from the signed base manifest.");
            long? oldSize = authoredOld?.Size;
            var rootId = next?.TargetRoot ?? old?.TargetRoot;
            var relativeTarget = rootId is null ? null : path[(rootId.Length + 1)..];
            var resolvedTarget = rootId is null ? null : next is not null ? PayloadDestinations.Resolve(manifest, installRoot, plan.Scope, options.Isolated, next)
                : PayloadDestinations.ResolveInstalled(previous!, old!);
            var rootDirectory = resolvedTarget is null ? null : resolvedTarget[..^(relativeTarget!.Replace('/', Path.DirectorySeparatorChar).Length + 1)];
            files["application:" + path] = new()
            {
                Location = rootId is null ? "application" : "external", TargetRootDirectory = rootDirectory, TargetRelativePath = relativeTarget,
                Path = path, BeforeSha256 = old?.Sha256, BeforeSize = oldSize,
                AfterSha256 = next?.Sha256, AfterSize = next?.Size
            };
        }
        if (plan.UpdateCompanionEnabled && manifest.UpdateCompanion is { } companion)
        {
            oldFiles.TryGetValue(companion.ConfigurationPath, out var old);
            files["application:" + companion.ConfigurationPath] = new()
            {
                Path = companion.ConfigurationPath, BeforeSha256 = old?.Sha256,
                AllowGeneratedAfterContent = true
            };
        }
        var receipt = PackageSafety.UnderRoot(stateRoot, manifest.Id + "/receipt.json");
        files["state:receipt"] = new()
        {
            Location = "state", Path = manifest.Id + "/receipt.json", BeforeSha256 = File.Exists(receipt) ? PackageSafety.HashFile(receipt) : null,
            BeforeSize = File.Exists(receipt) ? new FileInfo(receipt).Length : null, AllowGeneratedAfterContent = true
        };
        if (manifest.UpdateCompanion?.RuntimeMode == "shared" || oldManifest?.UpdateCompanion?.RuntimeMode == "shared")
        {
            var identity = new InstalledProduct { Id = manifest.Id, InstallDirectory = installRoot, Scope = plan.Scope, Isolated = options.Isolated, PublisherKeySha256 = publisherPin };
            var next = SharedUpdaterRuntime.GetReferenceBinding(manifest, identity, stateRoot, plan.UpdateCompanionEnabled);
            var old = oldManifest is not null && previous is not null ? SharedUpdaterRuntime.GetReferenceBinding(oldManifest, previous, stateRoot,
                oldManifest.UpdateCompanion is { } definition && previous.Features.Contains(definition.Feature, StringComparer.OrdinalIgnoreCase)) : new SharedUpdaterReferenceBinding(next.Path, null);
            if (!Path.GetFullPath(old.Path).Equals(Path.GetFullPath(next.Path), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A shared runtime reference cannot relocate during file recovery.");
            files["metadata:shared-reference"] = new()
            {
                Location = "external", Path = "shared-updater/reference.json", TargetRootDirectory = Path.GetDirectoryName(next.Path), TargetRelativePath = Path.GetFileName(next.Path),
                BeforeSha256 = old.Content is null ? null : IncrementalPackageIO.Hash(old.Content), BeforeSize = old.Content?.Length,
                AfterSha256 = next.Content is null ? null : IncrementalPackageIO.Hash(next.Content), AfterSize = next.Content?.Length
            };
        }
        foreach (var target in LegacyStagingCleanupModule.RecoveryTargets(signedTarget, installRoot, stateRoot, publisherPin, plan.Features))
            if (!files.TryAdd(target.Location + ":" + target.Path, target))
                throw new InvalidDataException("Legacy cleanup recovery overlaps an existing owned destination.");
        var result = new FileTransactionRecoveryContext
        {
            ProductId = manifest.Id, StateRoot = stateRoot, InstallDirectory = installRoot, Scope = plan.Scope,
            PublisherKeySha256 = publisherPin, BaseManifestSha256 = baseDigest,
            TargetManifestSha256 = targetDigest, BaseVersion = previous?.Version,
            TargetVersion = manifest.Version, RequireExactBase = requireExactBase, Isolated = options.Isolated,
            Applications = oldManifest is null ? [RunningApplicationTarget.FromPlan(plan, "recover update")]
                : [RunningApplicationTarget.FromManifest(oldManifest, installRoot, "recover update", previous!.Features), RunningApplicationTarget.FromPlan(plan, "recover update")],
            Files = files.Values.OrderBy(file => file.Location + ":" + file.Path, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        result.Validate();
        return result;
    }

    internal static byte[] ReadManifest(string path)
    {
        using var stream = BundleBuilder.OpenPackageStream(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return IncrementalPackageIO.ReadEntry(archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase),
            "manifest.json", IncrementalPackageIO.MaximumManifestBytes);
    }

    /// <summary>Must be rechecked by the engine after it acquires the product operation lock.</summary>
    public void ValidateInstalledBase(InstalledProduct? installed)
    {
        if (!RequireExactBase) return;
        if (installed is null || installed.Id != ProductId || installed.Scope != Scope || installed.Version != BaseVersion ||
            !Path.GetFullPath(installed.InstallDirectory).Equals(Path.GetFullPath(InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
            !IncrementalPackageIO.NullableHashEquals(installed.PublisherKeySha256, PublisherKeySha256))
            throw new InvalidDataException("The installed incremental base changed while the update was being prepared.");
        using var package = PackageReader.Open(installed.PackagePath);
        package.VerifySignature(PublisherKeySha256, requireSignature: true);
        if (BaseManifestSha256 is null || !PackageSafety.HashEquals(IncrementalPackageIO.Hash(package.ReadManifestBytes()), BaseManifestSha256))
            throw new CryptographicException("The incremental repair-cache base changed before installation.");
        var receipt = Files.SingleOrDefault(file => file.Location == "state" && file.Path == ProductId + "/receipt.json");
        if (receipt?.BeforeSha256 is null || !PackageSafety.HashEquals(PackageSafety.HashFile(installed.ReceiptPath), receipt.BeforeSha256))
            throw new CryptographicException("The incremental ownership receipt changed before installation.");
    }

    internal void Validate()
    {
        PackageSafety.ValidateId(ProductId);
        if (Scope is not ("user" or "machine") || !Path.IsPathFullyQualified(StateRoot) || !Path.IsPathFullyQualified(InstallDirectory))
            throw new InvalidDataException("File recovery requires explicit application and state roots and installation scope.");
        PackageSafety.EnsureNoReparsePoints(StateRoot);
        PackageSafety.EnsureNoReparsePoints(InstallDirectory);
        _ = PackageVersion.Parse(TargetVersion);
        if (BaseVersion is not null) _ = PackageVersion.Parse(BaseVersion);
        if (!PackageSafety.HashPattern().IsMatch(TargetManifestSha256) ||
            (BaseManifestSha256 is not null && !PackageSafety.HashPattern().IsMatch(BaseManifestSha256)) ||
            (PublisherKeySha256 is not null && !PackageSafety.HashPattern().IsMatch(PublisherKeySha256)))
            throw new InvalidDataException("File recovery requires verified manifest and publisher digests.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file.Location is not ("application" or "state" or "external") || !paths.Add(file.Location + ":" + PackageSafety.NormalizeRelativePath(file.Path))) throw new InvalidDataException("Duplicate file recovery target.");
            if (file.Location == "external" && (file.TargetRootDirectory is null || !Path.IsPathFullyQualified(file.TargetRootDirectory) || Path.GetDirectoryName(file.TargetRootDirectory) is null || file.TargetRelativePath is null))
                throw new InvalidDataException("A recovery component target must have a caller-verified product root and relative path.");
            _ = Resolve(file);
            if ((file.BeforeSha256 is not null && (!PackageSafety.HashPattern().IsMatch(file.BeforeSha256) || file.BeforeSize is < 0)) ||
                (file.AfterSha256 is not null && (!PackageSafety.HashPattern().IsMatch(file.AfterSha256) || file.AfterSize is null or < 0)))
                throw new InvalidDataException("A file recovery target has invalid metadata.");
        }
    }

    internal string Resolve(FileRecoveryTarget file) => file.Location == "external" ? PackageSafety.UnderRoot(file.TargetRootDirectory!, file.TargetRelativePath!)
        : PackageSafety.UnderRoot(file.Location == "application" ? InstallDirectory : StateRoot, file.Path);
    internal string BindingHash => IncrementalPackageIO.Hash(JsonSerializer.SerializeToUtf8Bytes(this, InstallerJson.Options));
}

public sealed class FileRecoveryTarget
{
    public string Location { get; init; } = "application";
    public string Path { get; init; } = "";
    public string? BeforeSha256 { get; init; }
    public long? BeforeSize { get; init; }
    public string? AfterSha256 { get; init; }
    public long? AfterSize { get; init; }
    public bool AllowGeneratedAfterContent { get; init; }
    public string? TargetRootDirectory { get; init; }
    public string? TargetRelativePath { get; init; }
}
