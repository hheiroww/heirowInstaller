using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowInstaller.Publishing;

/// <summary>A publisher connection owns its own HttpClient; credentials never enter manifests or logs.</summary>
public sealed partial class PublisherClient : IDisposable
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
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(45) };
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

    public Task<PublishedRelease> PublishAsync(string packagePath, CancellationToken cancellationToken = default,
        string? releaseNotes = null, IProgress<PublishProgress>? progress = null)
        => PublishCoreAsync(packagePath, null, null, cancellationToken, releaseNotes, progress);

    /// <summary>Publishes a finished sealed installer through the native administrator delivery lane, retaining the existing outer publisher trust.</summary>
    public Task<PublishedRelease> PublishSealedDeliveryAsync(string packagePath, string productId, string version,
        CancellationToken cancellationToken = default, string? releaseNotes = null, IProgress<PublishProgress>? progress = null)
    {
        RequireFullInput(packagePath);
        if (productId is not { Length: > 0 and <= 100 } || !productId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') || !Version.TryParse(version, out _))
            throw new ArgumentException("Supply the exact sealed installer's product and numeric version.");
        if (client.DefaultRequestHeaders.Authorization?.Scheme != "Bearer" || client.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Sealed installer delivery requires the native publisher's in-memory bearer session.");
        return PublishCoreAsync(packagePath, productId, version, cancellationToken, releaseNotes, progress);
    }

    /// <summary>Explicitly selects encrypted source preparation for a native administrator; it never installs the returned preparation bundle.</summary>
    public Task<PublishedRelease> PublishProtectedPreparationAsync(string packagePath, CancellationToken cancellationToken = default,
        string? releaseNotes = null, IProgress<PublishProgress>? progress = null)
    {
        if (!Path.GetExtension(packagePath).Equals(".hinst", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Native protected preparation requires a .hinst source draft.", nameof(packagePath));
        if (client.DefaultRequestHeaders.Authorization?.Scheme != "Bearer" || client.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Native protected preparation requires the publisher's in-memory bearer session.");
        return PublishCoreAsync(packagePath, null, null, cancellationToken, releaseNotes, progress, protectedPreparation: true);
    }

    private static void RequireFullInput(string packagePath)
    {
        if (Path.GetExtension(packagePath).ToLowerInvariant() is not (".hinst" or ".exe"))
            throw new ArgumentException("Protected preparation and sealed delivery require a full .hinst or standalone .exe input.", nameof(packagePath));
    }

    private async Task<PublishedRelease> PublishCoreAsync(string packagePath, string? sealedProduct, string? sealedVersion,
        CancellationToken cancellationToken, string? releaseNotes, IProgress<PublishProgress>? progress, bool protectedPreparation = false)
    {
        var path = Path.GetFullPath(packagePath);
        if (!string.Equals(Path.GetExtension(path), ".hinst", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(path), ".hupd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Publish a signed .hinst, .hupd update, or standalone Setup.exe.", nameof(packagePath));
        // Hash and upload the same open file, denying writers while it is read.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new InvalidDataException("Package is empty.");
        // The server independently validates opaque sealed delivery after its existing trusted preparation flow.
        // Ordinary packages are checked before any hash/upload, on a worker rather than the publisher UI thread.
        if (sealedProduct is null)
            await Task.Run(() =>
            {
                if (Path.GetExtension(path).Equals(".hupd", StringComparison.OrdinalIgnoreCase))
                {
                    using var update = IncrementalUpdateReader.Open(path);
                    LegalDocumentRules.RequireForCreation(update.TargetManifest);
                }
                else
                {
                    using var package = PackageReader.Open(path);
                    LegalDocumentRules.RequireForCreation(package.Manifest);
                }
            }, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
        file.Position = 0;
        // Keep failed/cancelled uploads resumable until the server's bounded expiry.
        using var begin = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "api/installer/publisher/uploads"));
        begin.Headers.Add("X-HeirowInstaller-SHA256", hash);
        if (protectedPreparation) begin.Headers.Add("X-HeirowInstaller-Protected-Preparation", "true");
        object metadata = new { filename = Path.GetFileName(path), length = file.Length, sha256 = hash, releaseNotes = releaseNotes ?? "" };
        if (sealedProduct != null)
        {
            begin.Headers.Add("X-HeirowInstaller-Sealed-Delivery", "true");
            metadata = new { filename = Path.GetFileName(path), length = file.Length, sha256 = hash, releaseNotes = releaseNotes ?? "", productId = sealedProduct, version = sealedVersion };
        }
        begin.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(metadata, Json));
        begin.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var opened = await client.SendAsync(begin, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(opened, cancellationToken);
        var session = JsonSerializer.Deserialize<UploadSession>(await ReadBoundedAsync(opened, cancellationToken), Json) ?? throw new InvalidDataException("Publisher returned no upload session.");
        if (session.Receipt is not null) { ValidateReceipt(session.Receipt); return session.Receipt; }
        if (!Guid.TryParseExact(session.UploadId, "N", out _) || session.ChunkSize is <= 0 or > 4 * 1024 * 1024 || session.ReceivedBytes < 0 || session.ReceivedBytes > file.Length)
            throw new InvalidDataException("Publisher returned an invalid upload session.");
        var uploadId = session.UploadId;
        var uploadPath = "api/installer/publisher/uploads/" + uploadId;
        var buffer = new byte[session.ChunkSize];
        long sent = session.ReceivedBytes;
        var recoveries = 0;
        while (sent < file.Length)
        {
            file.Position = sent;
            var count = (int)Math.Min(buffer.Length, file.Length - sent);
            await file.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            try
            {
                using var chunk = new HttpRequestMessage(HttpMethod.Put, new Uri(origin, uploadPath + "?offset=" + sent.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                chunk.Headers.Add("X-HeirowInstaller-SHA256", hash);
                chunk.Headers.Add("X-HeirowInstaller-Chunk-SHA256", Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))).ToLowerInvariant());
                chunk.Content = new ByteArrayContent(buffer, 0, count);
                chunk.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var accepted = await client.SendAsync(chunk, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await EnsureSuccessAsync(accepted, cancellationToken);
                var acknowledgement = JsonSerializer.Deserialize<UploadAcknowledgement>(await ReadBoundedAsync(accepted, cancellationToken), Json);
                sent += count;
                if (acknowledgement?.ReceivedBytes != sent) throw new InvalidDataException("Publisher acknowledged an unexpected upload offset.");
                progress?.Report(new PublishProgress(sent, file.Length));
                recoveries = 0;
            }
            catch (HttpRequestException error) when (error.StatusCode is null or System.Net.HttpStatusCode.Conflict && recoveries++ < 3)
            {
                // A lost response may follow a successful write. Query the authoritative offset before retrying.
                using var query = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, uploadPath));
                query.Headers.Add("X-HeirowInstaller-SHA256", hash);
                using var status = await client.SendAsync(query, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await EnsureSuccessAsync(status, cancellationToken);
                var state = JsonSerializer.Deserialize<UploadSession>(await ReadBoundedAsync(status, cancellationToken), Json) ?? throw new InvalidDataException("Publisher returned no resume state.");
                if (state.UploadId != uploadId || state.Length != file.Length || !string.Equals(state.Sha256, hash, StringComparison.OrdinalIgnoreCase) ||
                    state.ReceivedBytes < 0 || state.ReceivedBytes > file.Length || state.ChunkSize is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("Publisher resume state does not match this package.");
                if (state.Receipt is not null) { ValidateReceipt(state.Receipt); return state.Receipt; }
                sent = state.ReceivedBytes;
                if (buffer.Length != state.ChunkSize) buffer = new byte[state.ChunkSize];
            }
        }
        using var complete = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, uploadPath + "/complete"));
        complete.Headers.Add("X-HeirowInstaller-SHA256", hash);
        complete.Content = new ByteArrayContent("{}"u8.ToArray());
        complete.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(complete, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var receipt = JsonSerializer.Deserialize<PublishedRelease>(await ReadBoundedAsync(response, cancellationToken), Json) ?? throw new InvalidDataException("Publisher returned no release receipt.");
        ValidateReceipt(receipt);
        return receipt;
        void ValidateReceipt(PublishedRelease receipt)
        {
            if ((receipt.UploadedPackageSha256 is null) != (receipt.UploadedSize is null) ||
                !string.Equals(receipt.UploadedPackageSha256 ?? receipt.Sha256, hash, StringComparison.OrdinalIgnoreCase) ||
                (receipt.UploadedSize ?? receipt.Size) != file.Length)
                throw new InvalidDataException("Publisher receipt does not match the uploaded package.");
            if (receipt.Status is not null && (receipt.Status is not ("pending" or "activated" or "rejected") || !Guid.TryParseExact(receipt.ReceiptId, "N", out _) ||
                receipt.Status == "activated" && (receipt.Size <= 0 || receipt.Sha256.Length != 64 || !receipt.Sha256.All(Uri.IsHexDigit))))
                throw new InvalidDataException("Publisher returned an invalid publication receipt.");
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
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
        public PublishedRelease? Receipt { get; set; }
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
    public string? UploadedPackageSha256 { get; set; }
    public long? UploadedSize { get; set; }
    public string? ReceiptId { get; set; }
    public string? Status { get; set; }
    public string? PrivacyPolicyUrl { get; set; }
    public string? TermsAndConditionsUrl { get; set; }
    public bool Activated => string.Equals(Status, "activated", StringComparison.Ordinal);
}
