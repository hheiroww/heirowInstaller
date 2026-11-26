using System.Net;
using HeirowInstaller.Core;
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
        Begin("Checking this PC", "Looking for an existing installation before downloading application files.");
        try
        {
            var token = Cancellation!.Token;
            await RestoreAccountAsync(renew: false, token);
            if (arguments.VerifiedWebSetupFileUpdate is { } fileUpdate && await TryOpenWebSetupUpdateAsync(fileUpdate, token)) return;
            await game.OnUiAsync(() =>
            {
                Status = "Downloading application setup";
                StatusDetail = "Preparing the complete application files.";
                Notify();
            });
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
            var path = await Download();
            await game.OnUiAsync(() => End("Application files ready", "Opening the verified installation options."));
            await OpenPackageAsync(path);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await ErrorAsync("Could not prepare application setup", new InvalidOperationException(
                "This download could not be authorized. Use the application’s updater or download the full setup from its website.", error));
        }
        catch (Exception error) { await ErrorAsync("Could not prepare application setup", error); }
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
