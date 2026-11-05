using System.Security.Cryptography;

namespace HeirowInstaller.Core;

public sealed partial class FileTransactionRecoveryContext
{
    internal static FileTransactionRecoveryContext ForFileUpdate(PreparedFileUpdate stage, InstallPlan plan, InstallOptions options)
    {
        var previous = stage.Installed;
        using var cached = PackageReader.Open(previous.PackagePath); cached.VerifySignature(previous.PublisherKeySha256, true);
        if (!PackageSafety.HashEquals(IncrementalPackageIO.Hash(cached.ReadManifestBytes()), stage.BaseManifestSha256))
            throw new CryptographicException("The file-update base metadata changed.");
        var derived = Create(plan, previous, options, cached.Manifest, stage.BaseManifestSha256, stage.Metadata.ManifestSha256,
            stage.Metadata.PublisherKeySha256, true, stage.Metadata.Manifest);
        var files = new List<FileRecoveryTarget>();
        var owned = previous.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in derived.Files)
        {
            if ((file.Location is "application" or "external") && owned.ContainsKey(file.Path))
            {
                if (!stage.Before.TryGetValue(file.Path, out var snapshot) || snapshot.Sha256 is not null && !PackageSafety.HashPattern().IsMatch(snapshot.Sha256) ||
                    snapshot.Size is < 0 or > 16L * 1024 * 1024 * 1024 || (snapshot.Sha256 is null) != (snapshot.Size is null))
                    throw new InvalidDataException("A prepared before-image differs from the installed ownership inventory.");
                files.Add(new() { Location = file.Location, Path = file.Path, BeforeSha256 = snapshot.Sha256, BeforeSize = snapshot.Size,
                    AfterSha256 = file.AfterSha256, AfterSize = file.AfterSize, AllowGeneratedAfterContent = file.AllowGeneratedAfterContent,
                    TargetRootDirectory = file.TargetRootDirectory, TargetRelativePath = file.TargetRelativePath });
            }
            else if (file.Location == "state" && file.Path == previous.Id + "/receipt.json")
                files.Add(new() { Location = "state", Path = file.Path, BeforeSha256 = stage.ReceiptSha256,
                    AllowGeneratedAfterContent = true });
            else files.Add(file);
        }
        var metadata = previous.Id + "/metadata/" + stage.Metadata.ManifestSha256 + ".hmeta";
        files.Add(new() { Location = "state", Path = metadata, BeforeSha256 = stage.MetadataCacheBefore.Sha256,
            BeforeSize = stage.MetadataCacheBefore.Size,
            AfterSha256 = PackageSafety.HashFile(stage.MetadataPath), AfterSize = new FileInfo(stage.MetadataPath).Length });
        var result = new FileTransactionRecoveryContext
        {
            ProductId = derived.ProductId, StateRoot = derived.StateRoot, InstallDirectory = derived.InstallDirectory,
            Scope = derived.Scope, PublisherKeySha256 = derived.PublisherKeySha256, BaseManifestSha256 = derived.BaseManifestSha256,
            TargetManifestSha256 = derived.TargetManifestSha256, BaseVersion = derived.BaseVersion, TargetVersion = derived.TargetVersion,
            RequireExactBase = true, Isolated = derived.Isolated, Applications = derived.Applications, Files = files
        };
        result.Validate(); return result;
    }
}
