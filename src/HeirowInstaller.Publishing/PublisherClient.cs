using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Publishing;

/// <summary>A publisher connection owns its own HttpClient; credentials never enter manifests or logs.</summary>
public sealed class PublisherClient : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri origin;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PublisherClient(Uri publisher, string? bearerToken = null, string? sessionCookie = null, bool allowLoopback = false)
    {
        if (!publisher.IsAbsoluteUri || publisher.UserInfo.Length != 0 ||
            (publisher.Scheme != Uri.UriSchemeHttps && !(allowLoopback && publisher.IsLoopback && publisher.Scheme == Uri.UriSchemeHttp)))
            throw new ArgumentException("Publisher must use HTTPS without credentials in the URL.", nameof(publisher));
        origin = new Uri(publisher.GetLeftPart(UriPartial.Authority) + "/");
        // Do not forward a publisher credential through an HTTP redirect.
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("heirowInstaller/2026.10.24.1");
        if (!string.IsNullOrWhiteSpace(bearerToken)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (!string.IsNullOrWhiteSpace(sessionCookie)) client.DefaultRequestHeaders.Add("Cookie", sessionCookie);
    }

    public async Task<PublisherStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(new Uri(origin, "api/installer/publisher"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PublisherStatus>(Json, cancellationToken) ?? throw new InvalidDataException("Publisher returned no status.");
    }

    public async Task<JsonElement> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(new Uri(origin, "api/installer/catalog"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
    }

    public async Task<PublishedRelease> PublishAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(packagePath);
        if (!string.Equals(Path.GetExtension(path), ".hinst", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Publish a verified .hinst package.", nameof(packagePath));
        // Hash and upload the same open file, denying writers while it is read.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new InvalidDataException("Package is empty.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
        file.Position = 0;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "api/installer/publisher/releases"));
        request.Headers.Add("X-HeirowInstaller-SHA256", hash);
        request.Headers.Add("X-HeirowInstaller-Filename", Path.GetFileName(path));
        request.Content = new StreamContent(file, 1024 * 1024);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = file.Length;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PublishedRelease>(Json, cancellationToken) ?? throw new InvalidDataException("Publisher returned no release receipt.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        // Server response bodies can contain internal details. Keep errors bounded and free of credentials.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        var description = (int)response.StatusCode switch
        {
            401 => "Sign in with an authorized SocketJack publisher account.",
            403 => "This account does not have publisher permission.",
            409 => "This product version is already published. Increment the version.",
            413 => "The package exceeds the publisher size limit.",
            >= 300 and < 400 => "The publisher redirected the request. Use its canonical HTTPS origin.",
            _ => "The publisher rejected the request. Check the package and server status."
        };
        throw new HttpRequestException($"{description} HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public void Dispose() => client.Dispose();
}

public sealed class PublisherStatus
{
    public string PublisherId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool CanPublish { get; set; }
    public long MaxPackageBytes { get; set; }
}

public sealed class PublishedRelease
{
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallerUrl { get; set; } = "";
    public string UpdateUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}
