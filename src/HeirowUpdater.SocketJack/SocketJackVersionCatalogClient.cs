using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

/// <summary>Code-only version checks over SocketJack networking, using one explicit in-memory session per call.</summary>
public sealed class SocketJackVersionCatalogClient
{
    private readonly Uri publisher;
    private readonly TimeSpan timeout;
    private readonly bool allowLoopback;

    public SocketJackVersionCatalogClient(Uri publisher, TimeSpan? timeout = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (!publisher.IsAbsoluteUri || publisher.UserInfo.Length != 0 || publisher.Query.Length != 0 || publisher.Fragment.Length != 0 || publisher.AbsolutePath != "/" ||
            (publisher.Scheme != "https" && !(allowLoopback && publisher.IsLoopback && publisher.Scheme == "http")))
            throw new ArgumentException("Supply an HTTPS publisher origin without a path, query or credentials.", nameof(publisher));
        this.publisher = publisher;
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.allowLoopback = allowLoopback;
    }

    public async Task<InstallerVersionCheckResult> CheckAsync(string productId, string currentVersion,
        ApplicationSessionSnapshot? session = null, string? channel = null, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(session);
        return await client.CheckAsync(productId, currentVersion, channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstallerVersionCatalogPage> GetVersionsAsync(string productId, ApplicationSessionSnapshot? session = null,
        string? channel = null, int limit = 20, string? beforeVersion = null, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(session);
        return await client.GetVersionsAsync(productId, channel, limit, beforeVersion, cancellationToken).ConfigureAwait(false);
    }

    private VersionCatalogClient CreateClient(ApplicationSessionSnapshot? session) => new(publisher,
        transport: new SocketJackUpdateTransport(publisher, cookie: session?.Cookie, bearerToken: session?.BearerToken),
        timeout: timeout, allowLoopback: allowLoopback);
}
