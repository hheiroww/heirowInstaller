using System.Diagnostics;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    private string FileUpdateStagingRoot() => arguments.Isolated ? Path.Combine(arguments.StateRoot!, ".file-downloads")
        : Path.Combine(InstallerPlatform.UserData, "SocketJack", "heirowInstaller", "downloads");

    private async Task<InstallResult?> ApplySelectedFileUpdateAsync(InstalledProduct product, UpdateCheckResult update, CancellationToken token)
    {
        await RestoreAccountAsync(renew: false, token);
        if (DesktopUpdateHandoff.IsRunningProduct(product))
        {
            var (cookie, bearer) = DownloadCredentials();
            using var updater = new SocketJackUpdater(product.Isolated, AccountOrigin, cookie: cookie, bearerToken: bearer);
            using var self = Process.GetCurrentProcess();
            var start = await updater.PrepareInstallerHandoffAsync(UpdaterTarget.FromInstalled(product, arguments.StateRoot), update,
                self.Id, self.StartTime.ToUniversalTime().Ticks, new() { Isolated = product.Isolated, StageForElevation = true, DownloadDirectory = FileUpdateStagingRoot() }, Reporter(), token);
            _ = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the verified file updater.");
            await UpdaterOwnerHandshake.WaitForAcceptanceAsync(start, token);
            await game.OnUiAsync(game.Exit); return null;
        }
        var options = OptionsFor(product, "update");
        using var files = new FileUpdateClient(allowLoopback: product.Isolated, transport: options.SharedPackageTransport);
        using var prepared = await files.PrepareAsync(update.Feed, product, FileUpdateStagingRoot(), options, Reporter(), token);
        using var metadata = PackageReader.OpenMetadata(prepared.MetadataPath);
        var plan = engine.Plan(metadata, options);
        if (!options.Isolated && !InstallerEngine.IsElevated && plan.RequiresElevation)
        {
            return await ElevatedInstallWorker.InstallAsync(prepared.MetadataPath, plan, options,
                PromptRunningApplicationsAsync, Reporter(), arguments.WizardSmokeDirectory, token, "apply-files", prepared.Root);
        }
        return await engine.ApplyPreparedFileUpdateAsync(prepared, options, Reporter(), token);
    }

}
