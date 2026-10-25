using System.Security.Cryptography;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static class DesktopElevatedUpdate
{
    internal static async Task<InstallResult> InstallAsync(string download, UpdateFeed feed, InstalledProduct product,
        InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(product.PublisherKeySha256))
            throw new CryptographicException("Administrator updates require the installed product's pinned publisher key. Reinstall this application from its publisher's signed installer before updating it.");
        if (!feed.ProductId.Equals(product.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update feed belongs to a different installed product.");
        if (!feed.PackageType.Equals("hinst", StringComparison.OrdinalIgnoreCase) &&
            !(feed.PackageType.Equals("exe", StringComparison.OrdinalIgnoreCase) && PackageReader.HasEmbeddedPackage(download)))
            throw new InvalidOperationException("Administrator updates require a signed .hinst package or bundled heirowInstaller executable.");

        // Hold the verified package open through the child install so another
        // download cannot replace the file while Windows requests UAC approval.
        using var package = PackageReader.Open(download);
        if (!PackageSafety.HashEquals(PackageSafety.HashFile(download), feed.Sha256))
            throw new CryptographicException("The downloaded installer changed before administrator approval.");
        package.VerifySignature(product.PublisherKeySha256, requireSignature: true);
        if (!package.Manifest.Id.Equals(product.Id, StringComparison.OrdinalIgnoreCase) || package.Manifest.Version != feed.Version)
            throw new InvalidDataException("The update package does not match the selected product and release.");
        await package.VerifyAsync(progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // The signed suffix never authorizes execution of a downloaded EXE prefix.
        // The current trusted host rechecks this identity, digest and publisher pin
        // before it plans or applies any change with administrator access.
        options.Launch = false;
        options.Silent = true;
        var plan = new InstallerEngine().Plan(package, options);
        return await ElevatedInstallWorker.InstallAsync(download, plan, options,
            options.RunningApplicationsPrompt ?? ((request, _) => Task.FromException<RunningApplicationsChoice>(new RunningApplicationsException(request.Applications))),
            progress, null, cancellationToken);
    }
}
