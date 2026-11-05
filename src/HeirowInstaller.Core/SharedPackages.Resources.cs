namespace HeirowInstaller.Core;

public static class SharedPackageResources
{
    /// <summary>Resolves an installed dependency from signed receipt/package authority, never from a writable path hint. Legacy installs return null.</summary>
    public static async Task<string?> ResolveDirectoryAsync(string productId, string packageId, string publisherKeySha256,
        string? expectedInstallDirectory = null, string? scope = null, string? stateRoot = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var context = await Task.Run(() => Find(productId, packageId, publisherKeySha256, expectedInstallDirectory, scope, stateRoot), cancellationToken).ConfigureAwait(false);
        if (context is null) return null;
        using var package = PackageReader.Open(context.Archive);
        await VerifyArchiveAsync(package, context, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < package.Manifest.Payload.Count; index++)
        {
            var file = package.Manifest.Payload[index];
            await VerifyFileAsync(PackageSafety.UnderRoot(context.Directory, file.Path), file, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("dependencies", "Verifying " + packageId, index + 1, package.Manifest.Payload.Count));
        }
        return context.Directory;
    }

    public static async Task<string?> ResolveFileAsync(string productId, string packageId, string relativePath, string publisherKeySha256,
        string? expectedInstallDirectory = null, string? scope = null, string? stateRoot = null, CancellationToken cancellationToken = default)
    {
        relativePath = PackageSafety.NormalizeRelativePath(relativePath);
        var context = await Task.Run(() => Find(productId, packageId, publisherKeySha256, expectedInstallDirectory, scope, stateRoot), cancellationToken).ConfigureAwait(false);
        if (context is null) return null;
        using var package = PackageReader.Open(context.Archive);
        await VerifyArchiveAsync(package, context, cancellationToken).ConfigureAwait(false);
        var file = package.Manifest.Payload.SingleOrDefault(file => file.Path.Equals(relativePath, InstallerPlatform.PathComparison))
            ?? throw new InvalidDataException("The requested resource is absent from the signed shared package.");
        var path = PackageSafety.UnderRoot(context.Directory, file.Path);
        await VerifyFileAsync(path, file, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static ResourceContext? Find(string productId, string packageId, string publisherPin, string? expectedRoot, string? scope, string? stateRoot)
    {
        PackageSafety.ValidateId(productId); PackageSafety.ValidateId(packageId);
        if (!PackageSafety.HashPattern().IsMatch(publisherPin)) throw new ArgumentException("An explicitly trusted publisher pin is required.");
        var products = InstallerEngine.ListInstalled(stateRoot).Where(product => product.Id.Equals(productId, StringComparison.OrdinalIgnoreCase) &&
            (scope is null || scope == product.Scope) && (expectedRoot is null || Path.GetFullPath(expectedRoot).Equals(Path.GetFullPath(product.InstallDirectory), InstallerPlatform.PathComparison))).ToArray();
        if (products.Length == 0) return null;
        if (products.Length != 1) throw new InvalidDataException("Select the exact installation scope/root before resolving shared resources.");
        var product = products[0];
        if (!PackageSafety.HashEquals(product.PublisherKeySha256 ?? "", publisherPin)) throw new System.Security.Cryptography.CryptographicException("The installed resource owner has a different publisher.");
        using var parent = PackageReader.Open(product.PackagePath); parent.VerifySignature(publisherPin, requireSignature: true);
        if (parent.Manifest.Id != product.Id || parent.Manifest.Version != product.Version) throw new InvalidDataException("The installed shared resource owner differs from its signed package.");
        var definition = parent.Manifest.SharedPackages?.SingleOrDefault(package => package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        if (definition is null) return null;
        if (definition.Feature is not null && !product.Features.Contains(definition.Feature, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("This feature was not selected during setup. Modify the installation before using it.");
        if (!PackageSafety.HashEquals(definition.PublisherKeySha256, publisherPin)) throw new System.Security.Cryptography.CryptographicException("The shared resource package has a different publisher.");
        var actualState = Path.GetDirectoryName(Path.GetDirectoryName(product.ReceiptPath))!;
        var packageRoot = PackageSafety.UnderRoot(SharedPackages.Root(product.Scope, actualState, product.Isolated),
            "packages/" + publisherPin[..16].ToLowerInvariant() + "/" + definition.Sha256.ToLowerInvariant());
        return new(definition, PackageSafety.UnderRoot(packageRoot, "package.hinst"), PackageSafety.UnderRoot(packageRoot, "files"));
    }

    private static async Task VerifyArchiveAsync(PackageReader package, ResourceContext context, CancellationToken token)
    {
        var expected = context.Definition;
        package.VerifySignature(expected.PublisherKeySha256, requireSignature: true);
        if (new FileInfo(context.Archive).Length != expected.Size || !PackageSafety.HashEquals(await package.ComputePackageSha256Async(token).ConfigureAwait(false), expected.Sha256) ||
            package.Manifest.Id != expected.Id || package.Manifest.Version != expected.Version || package.Manifest.TargetRuntime != expected.TargetRuntime)
            throw new System.Security.Cryptography.CryptographicException("The installed shared resource archive differs from its signed declaration. Repair the installation.");
        PackagePlatform.RequireCurrentHost(package.Manifest);
    }

    private static async Task VerifyFileAsync(string path, PayloadFile expected, CancellationToken token)
    {
        PackageSafety.EnsureNoReparsePoints(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expected.Size || !PackageSafety.HashEquals(Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token).ConfigureAwait(false)), expected.Sha256))
            throw new System.Security.Cryptography.CryptographicException("An installed shared resource is missing or changed. Repair the installation.");
    }

    private sealed record ResourceContext(SharedPackageDefinition Definition, string Archive, string Directory);
}
