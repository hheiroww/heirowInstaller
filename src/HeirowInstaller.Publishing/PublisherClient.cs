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

    public async Task<PublishedRelease> PublishAsync(string packagePath, CancellationToken cancellationToken = default,
        string? releaseNotes = null, IProgress<PublishProgress>? progress = null)
    {
        var path = Path.GetFullPath(packagePath);
        if (!string.Equals(Path.GetExtension(path), ".hinst", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Publish a signed .hinst package or standalone Setup.exe.", nameof(packagePath));
        // Hash and upload the same open file, denying writers while it is read.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new InvalidDataException("Package is empty.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
        file.Position = 0;
        string? uploadId = null;
        try
        {
            using var begin = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "api/installer/publisher/uploads"));
            begin.Headers.Add("X-HeirowInstaller-SHA256", hash);
            begin.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { filename = Path.GetFileName(path), length = file.Length, sha256 = hash, releaseNotes = releaseNotes ?? "" }, Json));
            begin.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var opened = await client.SendAsync(begin, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(opened, cancellationToken);
            var session = await opened.Content.ReadFromJsonAsync<UploadSession>(Json, cancellationToken) ?? throw new InvalidDataException("Publisher returned no upload session.");
            if (!Guid.TryParseExact(session.UploadId, "N", out _) || session.ChunkSize is <= 0 or > 4 * 1024 * 1024 || session.ReceivedBytes != 0)
                throw new InvalidDataException("Publisher returned an invalid upload session.");
            uploadId = session.UploadId;
            var uploadPath = "api/installer/publisher/uploads/" + uploadId;
            var buffer = new byte[session.ChunkSize];
            long sent = 0;
            while (sent < file.Length)
            {
                var count = (int)Math.Min(buffer.Length, file.Length - sent);
                await file.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                using var chunk = new HttpRequestMessage(HttpMethod.Put, new Uri(origin, uploadPath + "?offset=" + sent.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                chunk.Headers.Add("X-HeirowInstaller-SHA256", hash);
                chunk.Headers.Add("X-HeirowInstaller-Chunk-SHA256", Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))).ToLowerInvariant());
                chunk.Content = new ByteArrayContent(buffer, 0, count);
                chunk.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var accepted = await client.SendAsync(chunk, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await EnsureSuccessAsync(accepted, cancellationToken);
                var acknowledgement = await accepted.Content.ReadFromJsonAsync<UploadAcknowledgement>(Json, cancellationToken);
                sent += count;
                if (acknowledgement?.ReceivedBytes != sent) throw new InvalidDataException("Publisher acknowledged an unexpected upload offset.");
                progress?.Report(new PublishProgress(sent, file.Length));
            }
            using var complete = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, uploadPath + "/complete"));
            complete.Headers.Add("X-HeirowInstaller-SHA256", hash);
            complete.Content = new ByteArrayContent("{}"u8.ToArray());
            complete.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(complete, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var receipt = await response.Content.ReadFromJsonAsync<PublishedRelease>(Json, cancellationToken) ?? throw new InvalidDataException("Publisher returned no release receipt.");
            if (!string.Equals(receipt.Sha256, hash, StringComparison.OrdinalIgnoreCase) || receipt.Size != file.Length)
                throw new InvalidDataException("Publisher receipt does not match the uploaded package.");
            uploadId = null;
            return receipt;
        }
        catch
        {
            if (uploadId is not null)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    using var cleanup = new HttpRequestMessage(HttpMethod.Delete, new Uri(origin, "api/installer/publisher/uploads/" + uploadId));
                    cleanup.Headers.Add("X-HeirowInstaller-SHA256", hash);
                    using var ignored = await client.SendAsync(cleanup, cleanupTimeout.Token);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
            throw;
        }
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

    private sealed class UploadSession
    {
        public string UploadId { get; set; } = "";
        public int ChunkSize { get; set; }
        public long ReceivedBytes { get; set; }
    }
    private sealed class UploadAcknowledgement { public long ReceivedBytes { get; set; } }
}

public sealed record PublishProgress(long BytesSent, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesSent / TotalBytes, 0, 1);
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
