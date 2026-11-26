using System.ComponentModel;
using System.Text;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    private sealed record WebSetupUpdate(InstalledProduct Product, string StageRoot, string ManifestSha256);
    private WebSetupUpdate? webSetupUpdate;

    internal bool IsWebSetupFileUpdate => webSetupUpdate is not null;
    internal string? CurrentWebSetupVersion { get; private set; }
    internal bool CanRetryWebSetup => arguments.RemotePackageUrl is not null && !Busy && (Failed || Status == "Operation canceled") && Manifest is null && CurrentWebSetupVersion is null;

    private async Task<bool> TryOpenWebSetupUpdateAsync(WebSetupFileUpdate binding, CancellationToken token)
    {
        var product = await Task.Run(() => InstallerEngine.FindBoundWebSetupInstallation(
            arguments.ExpectedProductId!, arguments.TrustedPublisherKeySha256!, arguments.StateRoot, arguments.Isolated), token);
        if (product is null) return false;

        string targetVersion = arguments.ExpectedVersion!;
        if (PackageVersion.Parse(product.Version).CompareTo(PackageVersion.Parse(targetVersion)) >= 0)
        {
            await game.OnUiAsync(() =>
            {
                CurrentWebSetupVersion = product.Version;
                End("Already installed", $"{product.Name} {product.Version} is installed.");
                CloseCompletedChild();
            });
            return true;
        }

        await game.OnUiAsync(() =>
        {
            Status = "Preparing an update";
            StatusDetail = $"Checking the files you already have for {product.Name} {product.Version}.";
            Notify();
        });
        var offer = new UpdateFeed
        {
            ProductId = product.Id,
            Version = targetVersion,
            PackageType = "hfiles",
            ManifestUrl = binding.ManifestUrl,
            FilesUrl = binding.FilesUrl,
            TargetManifestSha256 = binding.ManifestSha256
        };
        var options = OptionsFor(product, "update");
        // The engine retains installed features that still exist in the new manifest.
        options.Features = null;
        var progress = Reporter();
        var update = await Task.Run(async () =>
        {
            using var client = new FileUpdateClient(allowLoopback: product.Isolated, transport: options.SharedPackageTransport);
            using var stage = await client.PrepareAsync(offer, product, FileUpdateStagingRoot(), options, progress, token);
            using var package = PackageReader.OpenMetadata(stage.MetadataPath);
            var plan = engine.Plan(package, options);
            string? license = null;
            if (!string.IsNullOrWhiteSpace(package.Manifest.LicenseFile))
            {
                var bytes = await stage.ReadVerifiedPayloadAsync(package.Manifest.LicenseFile, plan, 512 * 1024, token);
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                license = await reader.ReadToEndAsync(token);
            }
            return (Stage: new WebSetupUpdate(product, stage.Root, binding.ManifestSha256), Plan: plan, License: license,
                Files: stage.RequestedPaths.Count);
        }, token);

        await game.OnUiAsync(() =>
        {
            webSetupUpdate = update.Stage;
            SelectedProduct = product;
            Manifest = update.Plan.Manifest;
            Plan = update.Plan;
            LicenseText = update.License;
            LicenseAccepted = arguments.AcceptLicense;
            Scope = product.Scope;
            targetDirectory = update.Plan.InstallDirectory;
            Features.Clear();
            foreach (var feature in update.Plan.Features) Features.Add(feature);
            Stage = update.License is null || LicenseAccepted ? SetupStage.Review : SetupStage.Package;
            game.Title = SetupProductName + " Setup";
            End("Ready to update", $"{product.Version} → {targetVersion}. {update.Files:N0} application files downloaded.");
        });
        if (arguments.Install && (LicenseText is null || LicenseAccepted)) await InstallAsync();
        return true;
    }

    private async Task InstallWebSetupUpdateAsync(WebSetupUpdate update)
    {
        Stage = SetupStage.Progress;
        Begin("Updating application", SetupProductName ?? update.Product.Name);
        var token = Cancellation!.Token;
        try
        {
            var progress = Reporter();
            var result = await Task.Run(async () =>
            {
                var options = OptionsFor(update.Product, "update");
                var stage = PreparedFileUpdate.Open(update.StageRoot, update.Product, options);
                using (stage)
                {
                    if (!PackageSafety.HashEquals(stage.Metadata.ManifestSha256, update.ManifestSha256) ||
                        stage.Metadata.Manifest.Version != arguments.ExpectedVersion)
                        throw new InvalidDataException("The prepared update differs from the signed website installer.");
                    options.Features = stage.SelectedFeatures.ToList();
                    using var metadata = PackageReader.OpenMetadata(stage.MetadataPath);
                    var plan = engine.Plan(metadata, options);
                    await WaitForRequestedProcessAsync(RunningApplicationTarget.FromPlan(plan, "update", options.Isolated), token);
                    if (!(plan.RequiresElevation && !InstallerEngine.IsElevated && !options.Isolated || arguments.WizardSmokeDirectory is not null))
                        return await engine.ApplyPreparedFileUpdateAsync(stage, options, progress, token);

                    // The worker must take its own exclusive lease and verify the sparse stage.
                    stage.Dispose();
                    return await ElevatedInstallWorker.InstallAsync(stage.MetadataPath, plan, options,
                        PromptRunningApplicationsAsync, progress, arguments.WizardSmokeDirectory, token, "apply-files", stage.Root);
                }
            }, token);

            await RefreshInstalledAsync();
            await game.OnUiAsync(() =>
            {
                LastReceiptPath = result.ReceiptPath;
                LastInstallation = result;
                Stage = SetupStage.Complete;
                Progress = 1;
                End(result.RebootRequired ? "Updated · restart required" : "Update complete",
                    $"{update.Product.Name} {result.Version} is ready to use.");
                CloseCompletedChild(result.RebootRequired);
            });
        }
        catch (Exception error)
        {
            var reason = error is Win32Exception { NativeErrorCode: 1223 }
                ? new OperationCanceledException("Administrator approval was canceled.", error)
                : error;
            await game.OnUiAsync(() => Stage = SetupStage.Review);
            await ErrorAsync("Update stopped", reason);
        }
    }
}
