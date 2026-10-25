using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    private sealed record MaintenanceIdentity(string Id, string Version, string Root, string Scope, string Package, string PackageSha256, string Receipt, string ReceiptSha256, string? PublisherPin);
    private MaintenanceIdentity? maintenanceIdentity;

    internal async Task InitializeMaintenanceWizardAsync()
    {
        if (!IsMaintenanceWizard || SelectedProduct is not { } product || Busy) return;
        Stage = SetupStage.Review;
        Begin("Checking " + product.Name, "Preparing this application's maintenance options.");
        try
        {
            var loaded = await Task.Run(async () =>
            {
                PackageSafety.EnsureNoReparsePoints(product.PackagePath);
                PackageSafety.EnsureNoReparsePoints(product.ReceiptPath);
                ValidateMaintenanceHost(product);
                using var input = File.Open(product.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var package = PackageReader.Open(product.PackagePath);
                arguments.ValidatePackageIdentity(package);
                package.VerifySignature(product.PublisherKeySha256, product.PublisherKeySha256 is not null);
                if (package.Manifest.Id != product.Id || package.Manifest.Version != product.Version)
                    throw new InvalidDataException("The cached package does not match this installed application.");
                await package.VerifyAsync(cancellationToken: Cancellation!.Token);
                var identity = new MaintenanceIdentity(product.Id, product.Version, Path.GetFullPath(product.InstallDirectory), product.Scope,
                    Path.GetFullPath(product.PackagePath), PackageSafety.HashFile(product.PackagePath), Path.GetFullPath(product.ReceiptPath),
                    PackageSafety.HashFile(product.ReceiptPath), product.PublisherKeySha256);
                var artwork = await ReadWizardArtworkAsync(package, Cancellation.Token);
                return (package.Manifest, Identity: identity, Artwork: artwork, Plan: engine.Plan(package, OptionsFor(product)));
            });
            await game.OnUiAsync(() =>
            {
                Manifest = loaded.Manifest;
                maintenanceIdentity = loaded.Identity;
                WizardArtworkPath = loaded.Artwork;
                PackagePath = loaded.Identity.Package;
                Scope = product.Scope; TargetDirectory = product.InstallDirectory;
                if (MaintenanceOperation == "uninstall") OpenApplicationOnFinish = false;
                Features.Clear(); foreach (var feature in product.Features) Features.Add(feature);
                Plan = loaded.Plan;
                game.Window.Title = product.Name + " Setup";
                End(MaintenanceOperation == "repair" ? "Ready to repair" : "Ready to uninstall", product.Name + " " + product.Version);
            });
            if (arguments.MaintenanceConfirmed || arguments.CloseOnComplete && MaintenanceOperation == "repair")
            {
                if (MaintenanceOperation == "repair") await RepairAsync(); else await UninstallAsync();
            }
        }
        catch (Exception error) { await ErrorAsync("Could not prepare maintenance", error); }
    }

    private InstalledProduct ValidateMaintenanceIdentity()
    {
        var expected = maintenanceIdentity ?? throw new InvalidOperationException("Wait for setup to verify this application.");
        var actual = InstallerEngine.ListInstalled(arguments.StateRoot).SingleOrDefault(x => x.Id == expected.Id && x.Scope == expected.Scope)
            ?? throw new InvalidDataException("This application is no longer installed. Close setup and open its current installer.");
        ValidateMaintenanceHost(actual);
        if (actual.Version != expected.Version || !Path.GetFullPath(actual.InstallDirectory).Equals(expected.Root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(actual.PackagePath).Equals(expected.Package, StringComparison.OrdinalIgnoreCase) || actual.PublisherKeySha256 != expected.PublisherPin ||
            !Path.GetFullPath(actual.ReceiptPath).Equals(expected.Receipt, StringComparison.OrdinalIgnoreCase) ||
            !PackageSafety.HashEquals(PackageSafety.HashFile(expected.Package), expected.PackageSha256) ||
            !PackageSafety.HashEquals(PackageSafety.HashFile(expected.Receipt), expected.ReceiptSha256))
            throw new InvalidDataException("The installed application changed while setup was open. Close this window and reopen its current installer.");
        return actual;
    }

    private void ValidateMaintenanceHost(InstalledProduct product)
    {
        if (product.UninstallerPath is not { } path) return;
        path = Path.GetFullPath(path);
        var state = arguments.StateRoot ?? InstallerEngine.DefaultStateRoot(product.Scope);
        var pool = SharedInstallerHost.Root(product.Scope, state, product.Isolated) + Path.DirectorySeparatorChar;
        if (path.StartsWith(pool, StringComparison.OrdinalIgnoreCase))
        {
            _ = SharedInstallerHost.Validate(path, product.Scope, state, product.Isolated);
            return;
        }
        var legacy = Path.Combine(Path.GetDirectoryName(product.ReceiptPath)!, "uninstaller") + Path.DirectorySeparatorChar;
        PackageSafety.EnsureNoReparsePoints(path);
        if (!path.StartsWith(legacy, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("The installed application's maintenance host is unavailable. Use its current published installer.");
    }
}
