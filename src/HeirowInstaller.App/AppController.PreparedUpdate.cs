using System.Net;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    private void BindPreparedUpdate(PackageReader package, InstallOptions options)
    {
        package.VerifySignature(arguments.TrustedPublisherKeySha256, requireSignature: true);
        var product = InstallerEngine.ListInstalled(arguments.StateRoot).SingleOrDefault(item =>
            item.Id == package.Manifest.Id && item.Scope == arguments.Scope &&
            string.Equals(item.InstallDirectory, arguments.TargetDirectory, StringComparison.OrdinalIgnoreCase));
        if (product?.PublisherKeySha256 is not { } pin || product.IsMsi || product.IsExecutable ||
            !PackageSafety.HashEquals(pin, package.PublisherKeySha256!) ||
            PackageVersion.Parse(package.Manifest.Version).CompareTo(PackageVersion.Parse(product.Version)) <= 0 ||
            package.Manifest.LegacyMsi is not null || package.Manifest.LegacyExecutable is not null)
            throw new InvalidDataException("The prepared update no longer matches a signed installation. Check for updates again.");

        options.TargetDirectory = product.InstallDirectory;
        options.Scope = product.Scope;
        options.RegisterProduct = product.RegisteredProduct;
        options.Features = product.Features.Where(id => package.Manifest.Features.Any(feature =>
            feature.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // The original user's setup can serve staged dependencies to the existing
    // download broker. Core still checks each archive's signed length and hash.
    internal sealed class PreparedDependencyTransport(PackageManifest manifest, string packagePath) : IUpdateTransport
    {
        public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes,
            IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependency = manifest.SharedPackages?.FirstOrDefault(item =>
                new Uri(item.DownloadUrl).AbsoluteUri == uri.AbsoluteUri);
            if (dependency is null || dependency.Size > maximumResponseBytes)
                throw new InvalidDataException("Setup requested a download outside the prepared shared dependencies.");

            var directory = Path.GetDirectoryName(packagePath)!;
            var path = PackageSafety.UnderRoot(directory, "dependencies/" + dependency.Sha256.ToLowerInvariant() + ".hinst");
            PackageSafety.EnsureNoReparsePoints(path);
            if (!File.Exists(path))
                throw new InvalidDataException("A prepared dependency is missing. Download the update again from the application.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            });
        }
    }
}
