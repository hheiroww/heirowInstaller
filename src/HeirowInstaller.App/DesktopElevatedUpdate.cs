using System.Diagnostics;
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
        var start = CreateStartInfo(download, feed, product, options);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the administrator update.");
        // A native MSI/EXE transaction must finish before the parent refreshes.
        // Cancellation is observed after the child exits; it never kills an installer.
        await child.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        if (child.ExitCode is not (0 or 3010)) throw new InvalidOperationException($"The administrator update returned exit code {child.ExitCode}.");
        return new() { ProductId = product.Id, Version = feed.Version, InstallDirectory = product.InstallDirectory, RebootRequired = child.ExitCode == 3010 };
    }

    internal static ProcessStartInfo CreateStartInfo(string download, UpdateFeed feed, InstalledProduct product, InstallOptions options)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The trusted desktop installer host is unavailable.");
        var start = new ProcessStartInfo(host) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(host)! };
        void Value(string key, string value) { start.ArgumentList.Add(key); start.ArgumentList.Add(value); }
        start.ArgumentList.Add("--silent");
        Value("--install", Path.GetFullPath(download));
        Value("--expected-product-id", product.Id);
        Value("--expected-version", feed.Version);
        Value("--expected-sha256", feed.Sha256);
        Value("--scope", product.Scope);
        if (!product.UseOriginalInstallDirectory) Value("--target", product.InstallDirectory);
        if (options.StateRoot is not null) Value("--state-root", options.StateRoot);
        Value("--features", string.Join(',', product.Features));
        if (product.PublisherKeySha256 is { } key) Value("--trust-key", key);
        start.ArgumentList.Add("--require-signature");
        if (!options.RegisterProduct) start.ArgumentList.Add("--no-register");
        if (options.Isolated) start.ArgumentList.Add("--isolated");
        if (options.LogPath is not null) Value("--log", options.LogPath);
        return start;
    }
}
