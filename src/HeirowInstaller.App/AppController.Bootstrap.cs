using System.Diagnostics;
using System.Net;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    internal async Task OpenRemotePackageAsync()
    {
        if (Busy || arguments.RemotePackageUrl is null) return;
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
            var parent = arguments.Isolated ? Path.Combine(arguments.StateRoot!, ".bootstrap-downloads")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "downloads");
            var directory = Path.Combine(parent, feed.Sha256.ToLowerInvariant());
            async Task<string> Download()
            {
                using var client = CreateUpdateClient();
                return await client.DownloadCachedApplicationAsync(feed, directory, Reporter(), token);
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
}
