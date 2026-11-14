using System.Diagnostics;
using System.Net;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;
using HeirowUpdater.SocketJack;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    internal async Task OpenRemotePackageAsync()
    {
        if (Busy || arguments.RemotePackageUrl is null) return;
        if (arguments.PreparedPackagePath is { } prepared)
        {
            await OpenPackageAsync(prepared);
            return;
        }
        Begin("Downloading application setup", SetupProductName is { } name
            ? $"Preparing {name} setup. Shared dependencies are downloaded during installation."
            : "Preparing the application files. Shared dependencies are downloaded during installation.");
        try
        {
            var token = Cancellation!.Token;
            await RestoreAccountAsync(renew: false, token);
            var feed = new UpdateFeed
            {
                ProductId = arguments.ExpectedProductId!, Version = arguments.ExpectedVersion!,
                InstallerUrl = arguments.RemotePackageUrl, Sha256 = arguments.ExpectedSha256!, Size = arguments.RemotePackageSize, PackageType = "hinst"
            };
            var endpoint = new Uri(feed.InstallerUrl);
            var useStreaming = SetupPackageTransport.SameOrigin(endpoint, AccountOrigin) || arguments.Isolated && endpoint.IsLoopback;
            var parent = arguments.Isolated ? Path.Combine(arguments.StateRoot!, ".bootstrap-downloads")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "downloads");
            var directory = Path.Combine(parent, feed.Sha256.ToLowerInvariant());
            async Task<string> Download()
            {
                if (!useStreaming)
                {
                    using var existingClient = CreateUpdateClient();
                    return await existingClient.DownloadCachedApplicationAsync(feed, directory, Reporter(), token);
                }
                var (cookie, bearer) = DownloadCredentials();
                var session = new ApplicationSessionSnapshot(sessionCookie: cookie, bearerToken: bearer);
                using var transport = new SetupPackageTransport(AccountOrigin, session);
                using var client = new UpdateClient(allowLoopback: arguments.Isolated && endpoint.IsLoopback, transport: transport);
                return await client.DownloadStreamingApplicationAsync(feed, directory, TimeSpan.FromMinutes(1),
                    new SetupDownloadProgress(value => ReportSetupDownload(value, token)), token);
            }
            string path;
            try { path = await Download(); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized &&
                new Uri(arguments.RemotePackageUrl).GetLeftPart(UriPartial.Authority).Equals(AccountOrigin.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                await game.OnUiAsync(() => { SigningIn = true; Status = "Sign in to download setup"; StatusDetail = "Approve the SocketJack sign-in in your browser."; Notify(); });
                PublisherSession? pending = null;
                try
                {
                    var revision = OperatingSystem.IsWindows() ? await Account.GetRevisionAsync(token) : null;
                    using var approval = await PublisherSignInClient.BeginAsync(AccountOrigin, token);
                    Process.Start(new ProcessStartInfo(approval.BrowserUrl.AbsoluteUri) { UseShellExecute = true });
                    pending = await approval.CompleteAsync(token);
                    if (OperatingSystem.IsWindows() && !await Account.RememberAsync(pending, revision, token))
                        throw new InvalidOperationException("The sign-in changed on this device. Reopen setup to use the current account.");
                    var accepted = pending;
                    await game.OnUiAsync(() => { publisherSession?.Dispose(); publisherSession = accepted; SigningIn = false; Notify(); });
                    pending = null;
                    path = await Download();
                }
                finally { pending?.Dispose(); await game.OnUiAsync(() => { SigningIn = false; Notify(); }); }
            }
            await game.OnUiAsync(() => End("Application files ready", "Opening the verified installation options."));
            await OpenPackageAsync(path);
        }
        catch (Exception error) { await ErrorAsync("Could not download application setup", error); }
    }

    private void ReportSetupDownload(InstallProgress value, CancellationToken token) => game.Enqueue(() =>
    {
        if (!Busy || token.IsCancellationRequested || Cancellation?.Token != token) return;
        Progress = value.CompletedBytes is { } bytes && value.TotalBytes is > 0
            ? Math.Clamp((double)bytes / value.TotalBytes.Value, 0, 1) : value.Fraction;
        Status = value.Phase == "cache" ? "Using downloaded application files" : "Downloading application files";
        StatusDetail = value.Message;
        ProgressChanged?.Invoke();
    });
}
