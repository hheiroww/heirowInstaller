using System.Net;
using System.Security.Cryptography;
using HeirowInstaller.Core;

internal sealed partial class AcceptanceSuite
{
    private async Task CachedRemoteSetup()
    {
        var area = Area("remote-cache");
        byte[] bytes = "Signed-descriptor-bound synthetic package bytes"u8.ToArray();
        var transport = new CachedSetupTransport(bytes);
        using var client = new UpdateClient(transport: transport);
        var feed = new UpdateFeed { ProductId = "fixture.remote", Version = "1", InstallerUrl = "https://example.invalid/app.hinst", PackageType = "hinst", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        var path = await client.DownloadCachedApplicationAsync(feed, area);
        Equal(1, transport.Requests);
        Equal(path, await client.DownloadCachedApplicationAsync(feed, area));
        Equal(1, transport.Requests);
        await File.WriteAllBytesAsync(path, new byte[bytes.Length]);
        await client.DownloadCachedApplicationAsync(feed, area);
        Equal(2, transport.Requests);
        True(File.ReadAllBytes(path).SequenceEqual(bytes));
        await File.WriteAllTextAsync(path, "truncated");
        await client.DownloadCachedApplicationAsync(feed, area);
        Equal(3, transport.Requests);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => client.DownloadCachedApplicationAsync(feed, area, cancellationToken: canceled.Token));
        Equal(3, transport.Requests);
        feed.InstallerUrl = "http://example.invalid/app.hinst";
        await Throws<InvalidDataException>(() => client.DownloadCachedApplicationAsync(feed, area));
        Equal(3, transport.Requests);
    }

    private sealed class CachedSetupTransport(byte[] bytes) : IUpdateTransport
    {
        internal int Requests;
        public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
