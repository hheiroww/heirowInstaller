using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeirowInstaller.Core;

/// <summary>Reads a publisher's accounted version catalog without packages, profiles, installed receipts or helper executables.</summary>
public sealed class VersionCatalogClient : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly Uri publisher;
    private readonly IUpdateTransport? transport;
    private readonly HttpClient? client;
    private readonly TimeSpan timeout;

    public VersionCatalogClient(Uri publisher, IUpdateTransport? transport = null, TimeSpan? timeout = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (!publisher.IsAbsoluteUri || publisher.UserInfo.Length != 0 || publisher.Query.Length != 0 || publisher.Fragment.Length != 0 || publisher.AbsolutePath != "/" ||
            (publisher.Scheme != "https" && !(allowLoopback && publisher.IsLoopback && publisher.Scheme == "http")))
            throw new ArgumentException("Supply an HTTPS publisher origin without a path, query or credentials.", nameof(publisher));
        this.publisher = new(publisher.GetLeftPart(UriPartial.Authority) + "/");
        this.transport = transport;
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (transport is null)
            client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Fetches one small page, comparing against the active published pointer rather than the first history item.</summary>
    public async Task<InstallerVersionCheckResult> CheckAsync(string productId, string currentVersion, string? channel = null,
        CancellationToken cancellationToken = default)
    {
        var current = PackageVersion.Parse(currentVersion);
        var page = await GetVersionsAsync(productId, channel, limit: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
        var comparison = current.CompareTo(PackageVersion.Parse(page.LatestVersion));
        var state = comparison < 0 ? InstallerVersionCheckState.UpdateAvailable : comparison > 0 ? InstallerVersionCheckState.Ahead : InstallerVersionCheckState.Current;
        return new(page.ProductId, currentVersion, page.Channel, page.LatestVersion, state);
    }

    public async Task<InstallerVersionCatalogPage> GetVersionsAsync(string productId, string? channel = null, int limit = 20,
        string? beforeVersion = null, CancellationToken cancellationToken = default)
    {
        PackageSafety.ValidateId(productId);
        channel ??= "public";
        if (channel is not ("public" or "beta")) throw new ArgumentException("Channel must be public or beta.", nameof(channel));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (beforeVersion is not null) _ = PackageVersion.Parse(beforeVersion);
        var query = "channel=" + channel + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (beforeVersion is not null) query += "&beforeVersion=" + Uri.EscapeDataString(beforeVersion);
        var uri = new Uri(publisher, "api/installer/catalog/" + Uri.EscapeDataString(productId) + "/versions?" + query);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            // The deadline starts before dispatch, and native networking setup never runs on the caller's UI thread.
            var request = Task.Run(() => transport is null
                ? client!.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                : transport.GetAsync(uri, MaximumResponseBytes, null, deadline.Token), deadline.Token);
            using var response = await AwaitBoundedAsync(request, deadline.Token, value => value.Dispose()).ConfigureAwait(false);
            response.EnsureSuccessStatusCode(); // Redirects and 304 without a cached, freshly authorized representation are errors.
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Version catalog exceeds its metadata limit.");
            await using var input = await AwaitBoundedAsync(response.Content.ReadAsStreamAsync(deadline.Token), deadline.Token, value => value.Dispose()).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await AwaitBoundedAsync(input.ReadAsync(bytes.AsMemory(), deadline.Token).AsTask(), deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("Version catalog exceeds its metadata limit.");
                buffer.Write(bytes, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Version catalog must be an object.");
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!fields.Add(property.Name)) throw new InvalidDataException("Version catalog contains duplicate fields.");
            var page = document.RootElement.Deserialize<InstallerVersionCatalogPage>(Json) ?? throw new InvalidDataException("Version catalog is empty.");
            Validate(page, productId, channel, limit, beforeVersion);
            deadline.Token.ThrowIfCancellationRequested();
            return page with { Versions = Array.AsReadOnly(page.Versions.ToArray()) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The version catalog took too long to respond. Try again."); }
        catch (JsonException error) { throw new InvalidDataException("The publisher returned invalid version metadata.", error); }
    }

    private static async Task<T> AwaitBoundedAsync<T>(Task<T> pending, CancellationToken token, Action<T>? disposeLate = null)
    {
        try { return await pending.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A third-party transport/stream may ignore cancellation. Its late result still has an owner.
            _ = ObserveLateAsync(pending, disposeLate);
            throw;
        }
    }

    private static async Task ObserveLateAsync<T>(Task<T> pending, Action<T>? dispose)
    {
        try { var result = await pending.ConfigureAwait(false); dispose?.Invoke(result); }
        catch { } // Observe a late failure without replacing the original timeout/cancellation.
    }

    private static void Validate(InstallerVersionCatalogPage page, string productId, string channel, int limit, string? beforeVersion)
    {
        if (page.SchemaVersion != 1 || !string.Equals(page.ProductId, productId, StringComparison.OrdinalIgnoreCase) || page.Channel != channel)
            throw new InvalidDataException("Version catalog identity, channel or schema does not match the request.");
        var latest = PackageVersion.Parse(page.LatestVersion);
        if (page.Versions is null || page.Versions.Count > limit) throw new InvalidDataException("Version catalog exceeds the requested page size.");
        if (beforeVersion is null && (page.Versions.Count == 0 || page.Versions[0] != page.LatestVersion))
            throw new InvalidDataException("The first version page must begin with the active published version.");
        var previous = beforeVersion is null ? null : PackageVersion.Parse(beforeVersion);
        foreach (var version in page.Versions)
        {
            var parsed = PackageVersion.Parse(version);
            if (parsed.CompareTo(latest) > 0) throw new InvalidDataException("Version history contains a version newer than the active published pointer.");
            if (previous is not null && parsed.CompareTo(previous) >= 0) throw new InvalidDataException("Version history must be distinct, descending and before the requested cursor.");
            previous = parsed;
        }
        if (page.NextBeforeVersion is not null && (page.Versions.Count == 0 || page.NextBeforeVersion != page.Versions[^1]))
            throw new InvalidDataException("The next version cursor must identify the final history item.");
    }

    public void Dispose() => client?.Dispose();
}
