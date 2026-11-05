using System.Diagnostics;
using System.Net;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;
using HeirowUpdater.SocketJack;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    private readonly SemaphoreSlim downloadApprovalGate = new(1, 1);
    private string FileUpdateStagingRoot() => arguments.Isolated ? Path.Combine(arguments.StateRoot!, ".file-downloads")
        : Path.Combine(InstallerPlatform.UserData, "SocketJack", "heirowInstaller", "downloads");

    private async Task<InstallResult?> ApplySelectedFileUpdateAsync(InstalledProduct product, UpdateCheckResult update, CancellationToken token)
    {
        if (DesktopUpdateHandoff.IsRunningProduct(product))
        {
            await RestoreAccountAsync(renew: false, token);
            using var updater = new SocketJackUpdater(product.Isolated, AccountOrigin,
                cookie: PublisherSignedIn ? null : Environment.GetEnvironmentVariable("SOCKETJACK_DOWNLOAD_COOKIE"),
                bearerToken: PublisherSignedIn ? publisherSession!.AccessToken : Environment.GetEnvironmentVariable("SOCKETJACK_DOWNLOAD_TOKEN"));
            using var self = Process.GetCurrentProcess();
            var start = await updater.PrepareInstallerHandoffAsync(UpdaterTarget.FromInstalled(product, arguments.StateRoot), update,
                self.Id, self.StartTime.ToUniversalTime().Ticks, new() { Isolated = product.Isolated, StageForElevation = true, DownloadDirectory = FileUpdateStagingRoot() }, Reporter(), token);
            _ = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the verified file updater.");
            await UpdaterOwnerHandshake.WaitForAcceptanceAsync(start, token);
            await game.OnUiAsync(game.Exit); return null;
        }
        var options = OptionsFor(product, "update");
        using var files = new FileUpdateClient(allowLoopback: product.Isolated, transport: options.SharedPackageTransport);
        using var prepared = await WithDownloadAccountAsync(() => files.PrepareAsync(update.Feed, product, FileUpdateStagingRoot(), options, Reporter(), token), token, autoApprove: false);
        using var metadata = PackageReader.OpenMetadata(prepared.MetadataPath);
        var plan = engine.Plan(metadata, options);
        if (!options.Isolated && !InstallerEngine.IsElevated && plan.RequiresElevation)
        {
            return await ElevatedInstallWorker.InstallAsync(prepared.MetadataPath, plan, options,
                PromptRunningApplicationsAsync, Reporter(), arguments.WizardSmokeDirectory, token, "apply-files", prepared.Root);
        }
        return await engine.ApplyPreparedFileUpdateAsync(prepared, options, Reporter(), token);
    }

    private async Task<T> WithDownloadAccountAsync<T>(Func<Task<T>> operation, CancellationToken token, bool autoApprove = true)
    {
        await RestoreAccountAsync(renew: false, token);
        try { return await operation(); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized && autoApprove && !arguments.Isolated && !arguments.Silent &&
            SelectedProduct?.UpdateUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var origin) && origin.Scheme == AccountOrigin.Scheme && origin.Host == AccountOrigin.Host && origin.Port == AccountOrigin.Port)
        {
            await ApproveDownloadAccountAsync(token); return await operation();
        }
    }

    private async Task ApproveDownloadAccountAsync(CancellationToken token)
    {
        await downloadApprovalGate.WaitAsync(token);
        try
        {
            await game.OnUiAsync(() => { SigningIn = true; Status = "Sign in to download application files"; StatusDetail = "Approve SocketJack sign-in in your browser. Setup stays open."; Notify(); });
            PublisherSession? pending = null;
            try
            {
                var revision = OperatingSystem.IsWindows() ? await Account.GetRevisionAsync(token) : null;
                using var approval = await PublisherSignInClient.BeginAsync(AccountOrigin, token);
                Process.Start(new ProcessStartInfo(approval.BrowserUrl.AbsoluteUri) { UseShellExecute = true });
                pending = await approval.CompleteAsync(token);
                if (OperatingSystem.IsWindows() && !await Account.RememberAsync(pending, revision, token))
                    throw new InvalidOperationException("The sign-in changed on this device. Retry with the current SocketJack account.");
                var accepted = pending;
                await game.OnUiAsync(() => { publisherSession?.Dispose(); publisherSession = accepted; SigningIn = false; Notify(); }); pending = null;
            }
            finally { pending?.Dispose(); await game.OnUiAsync(() => { SigningIn = false; Notify(); }); }
        }
        finally { downloadApprovalGate.Release(); }
    }

    private IFileUpdateTransport CreateAuthenticatedDownloadTransport() => new DesktopAuthenticatedTransport(
        CreateDownloadTransport, ApproveDownloadAccountAsync, !arguments.Isolated && !arguments.Silent);

    private sealed class DesktopAuthenticatedTransport(Func<IFileUpdateTransport> create, Func<CancellationToken, Task> approve, bool interactive) : IFileUpdateTransport
    {
        public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
            => SendAsync(uri, client => client.GetAsync(uri, maximumResponseBytes, progress, cancellationToken), cancellationToken);
        public Task<HttpResponseMessage> PostAsync(Uri uri, long maximumResponseBytes, ReadOnlyMemory<byte> json, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
            => SendAsync(uri, client => client.PostAsync(uri, maximumResponseBytes, json, progress, cancellationToken), cancellationToken);
        private async Task<HttpResponseMessage> SendAsync(Uri uri, Func<IFileUpdateTransport, Task<HttpResponseMessage>> send, CancellationToken token)
        {
            var response = await send(create());
            if (response.StatusCode != HttpStatusCode.Unauthorized || !interactive || uri.Scheme != AccountOrigin.Scheme || uri.Host != AccountOrigin.Host || uri.Port != AccountOrigin.Port)
                return response;
            response.Dispose(); await approve(token); return await send(create());
        }
    }
}
