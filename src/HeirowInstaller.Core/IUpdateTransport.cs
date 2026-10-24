namespace HeirowInstaller.Core;

/// <summary>A one-request update transport. Implementations must not follow redirects; UpdateClient validates each destination before issuing the next request.</summary>
public interface IUpdateTransport
{
    Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken);
}
