using System.Diagnostics;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

namespace HeirowInstaller.App;

/// <summary>The receipt-bound launch used by Finish and the medium-integrity post-install action.</summary>
internal static class InstalledApplicationLaunch
{
    internal static ProcessStartInfo Create(InstalledProduct product, bool updaterEnabled)
    {
        using var package = PackageReader.Open(product.PackagePath);
        package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
        var manifest = package.Manifest;
        if (manifest.Id != product.Id || manifest.Version != product.Version || manifest.EntryPoint.Length == 0)
            throw new InvalidDataException("The installed application differs from its verified repair package.");
        var executable = PackageSafety.UnderRoot(product.InstallDirectory, manifest.EntryPoint);
        if (!File.Exists(executable)) throw new FileNotFoundException("The installed application executable is missing.", executable);
        if (product.Files.FirstOrDefault(file => file.Path.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase)) is { } owned &&
            !PackageSafety.HashEquals(PackageSafety.HashFile(executable), owned.Sha256))
            throw new InvalidDataException("The installed application changed after installation. Repair it before opening it.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true,
            WorkingDirectory = PackageSafety.ResolveWorkingDirectory(product.InstallDirectory, manifest.LaunchWorkingDirectory) };
        if (updaterEnabled && manifest.UpdateCompanion is { } companion)
        {
            var profilePath = PackageSafety.UnderRoot(product.InstallDirectory, companion.ConfigurationPath);
            _ = ApplicationUpdater.ReadConfiguration(profilePath);
            start.FileName = UpdaterHostResolver.FindExecutable(executable, product.InstallDirectory)
                ?? throw new FileNotFoundException("The optional updater is missing. Repair this application before opening it.");
            start.ArgumentList.Add("launch"); start.ArgumentList.Add("--config"); start.ArgumentList.Add(profilePath);
        }
        return start;
    }
}
