using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

/// <summary>Resolves an application's browser recovery page without forwarding updater credentials.</summary>
public static class SocketJackDownloadRecovery
{
    /// <summary>Uses a known application page or the homepage in its verified installed package.</summary>
    public static Uri? GetDownloadPage(UpdateCompanionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!ValidProductId(configuration.ProductId)) return null;
        if (KnownPage(configuration.ProductId) is { } known) return known;
        try
        {
            var installed = InstallerEngine.ListInstalled(configuration.StateRoot).SingleOrDefault(product =>
                product.Id.Equals(configuration.ProductId, StringComparison.OrdinalIgnoreCase) && product.Scope == configuration.Scope);
            if (installed is null || installed.Isolated != configuration.Isolated ||
                !Path.GetFullPath(installed.InstallDirectory).Equals(Path.GetFullPath(configuration.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
                !PackageSafety.HashEquals(installed.PublisherKeySha256 ?? "", configuration.PublisherKeySha256)) return null;
            using var package = PackageReader.Open(installed.PackagePath);
            package.VerifySignature(configuration.PublisherKeySha256, requireSignature: true);
            if (!package.Manifest.Id.Equals(configuration.ProductId, StringComparison.OrdinalIgnoreCase)) return null;
            return GetDownloadPage(package.Manifest);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or InvalidOperationException)
        {
            // Recovery must not replace the original update failure with a second exception.
            return null;
        }
    }

    /// <summary>Resolves metadata from a package whose publisher signature has already been verified by the caller.</summary>
    public static Uri? GetDownloadPage(PackageManifest verifiedManifest)
    {
        ArgumentNullException.ThrowIfNull(verifiedManifest);
        if (!ValidProductId(verifiedManifest.Id)) return null;
        if (KnownPage(verifiedManifest.Id) is { } known) return known;
        if (!Uri.TryCreate(verifiedManifest.Homepage, UriKind.Absolute, out var homepage) ||
            homepage.Scheme != Uri.UriSchemeHttps || !homepage.IsDefaultPort || homepage.UserInfo.Length != 0 ||
            !homepage.IdnHost.Equals("socketjack.com", StringComparison.OrdinalIgnoreCase)) return null;
        // Session or campaign query values and unknown fragments have no place in a recovery launch.
        return new UriBuilder(homepage) { Query = "", Fragment = homepage.Fragment.Equals("#downloads", StringComparison.OrdinalIgnoreCase) ? "downloads" : "" }.Uri;
    }

    private static bool ValidProductId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        try { PackageSafety.ValidateId(id); return true; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { return false; }
    }

    private static Uri? KnownPage(string id) => id.ToLowerInvariant() switch
    {
        "fruityloopscollab" => new("https://socketjack.com/fruityloops-collab/#downloads"),
        "heirowinstaller" => new("https://socketjack.com/heirowInstaller/"),
        _ => null
    };
}
