using System.Net;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

namespace HeirowInstaller.App;

/// <summary>Streams the signed remote setup directly into the verified package cache.</summary>
internal sealed class SetupPackageTransport(Uri credentialOrigin, ApplicationSessionSnapshot session) : IUpdateTransport, IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(30)
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    internal static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme &&
        first.IdnHost.Equals(second.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;

    public async Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (SameOrigin(uri, credentialOrigin))
        {
            if (!string.IsNullOrWhiteSpace(session.Cookie)) request.Headers.Add("Cookie", session.Cookie);
            if (!string.IsNullOrWhiteSpace(session.BearerToken)) request.Headers.Authorization = new("Bearer", session.BearerToken);
        }
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && error.InnerException is TimeoutException)
        { throw new TimeoutException("The application download server took too long to respond. Check your connection and try again.", error); }
    }

    public void Dispose() => client.Dispose();
}
