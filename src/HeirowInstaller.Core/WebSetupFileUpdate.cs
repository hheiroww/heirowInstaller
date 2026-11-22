using System.Security.Cryptography;

namespace HeirowInstaller.Core;

/// <summary>The exact signed selected-file target for a native website setup.</summary>
public sealed record WebSetupFileUpdate(string ManifestUrl, string FilesUrl, string ManifestSha256);

public sealed partial class InstallerEngine
{
    /// <summary>Finds an exact signed native installation, without treating an unreadable receipt as a fresh install.</summary>
    public static InstalledProduct? FindBoundWebSetupInstallation(string productId, string publisherPin, string? stateRoot = null,
        bool isolated = false, string? scope = null)
    {
        PackageSafety.ValidateId(productId);
        ArgumentNullException.ThrowIfNull(publisherPin);
        if (!PackageSafety.HashPattern().IsMatch(publisherPin))
            throw new ArgumentException("Website setup discovery requires a signed publisher pin.", nameof(publisherPin));
        if (scope is not (null or "user" or "machine") || isolated && stateRoot is null)
            throw new ArgumentException("Website setup discovery requires a valid installation scope and state directory.");

        string[] scopes = scope is null ? ["user", "machine"] : [scope];
        var selected = new List<InstalledProduct>();
        foreach (var candidate in scopes.Select(value => (Scope: value, Root: Path.GetFullPath(stateRoot ?? DefaultStateRoot(value))))
                     .GroupBy(value => value.Root, InstallerPlatform.PathComparer))
        {
            // An isolated or explicitly selected root can contain either scope. For
            // the default roots, the directory itself binds the receipt's scope.
            var declaredScope = candidate.Count() == 1 ? candidate.First().Scope : null;
            var receipt = PackageSafety.UnderRoot(candidate.Key, productId + "/receipt.json");
            PackageSafety.EnsureNoReparsePoints(receipt);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(receipt); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("The existing application's installation receipt is invalid.");

            var product = ReadReceipt(receipt, declaredScope, isolated)
                ?? throw new InvalidDataException("The existing application's installation receipt could not be verified.");
            if (product.Id != productId || product.Isolated != isolated)
                throw new InvalidDataException("The installation receipt belongs to a different application or setup environment.");
            if (product.IsMsi || product.IsExecutable) continue;
            if (!PackageSafety.HashEquals(publisherPin, product.PublisherKeySha256 ?? ""))
                throw new CryptographicException("The installed application's publisher does not match this website installer.");
            PackageSafety.EnsureNoReparsePoints(product.PackagePath);
            using var package = PackageReader.Open(product.PackagePath);
            package.VerifySignature(publisherPin, requireSignature: true);
            if (package.Manifest.Id != product.Id || package.Manifest.Version != product.Version ||
                package.Manifest.LegacyMsi is not null || package.Manifest.LegacyExecutable is not null)
                throw new InvalidDataException("The installation receipt differs from the signed native package.");
            selected.Add(product);
        }
        if (selected.Count > 1)
            throw new InvalidDataException("More than one signed installation matches this website installer. Update the intended installation from its application.");
        return selected.SingleOrDefault();
    }
}
