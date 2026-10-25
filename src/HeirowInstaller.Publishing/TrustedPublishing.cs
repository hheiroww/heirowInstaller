using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeirowInstaller.Publishing;

public sealed class TrustedPublishingOptions
{
    public string? ReleaseNotes { get; set; }
    public string? UploadId { get; set; }
    public bool WaitForActivation { get; set; }
    public TimeSpan ActivationTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Optional identity provider; the default requests a fresh GitHub Actions OIDC token for each exchange.</summary>
    public Func<CancellationToken, Task<string>>? OidcTokenProvider { get; set; }
}

public sealed partial class PublisherClient
{
    /// <summary>Publishes using GitHub Actions identity. Failed transfers remain resumable and never persist credentials.</summary>
    public async Task<PublishedRelease> PublishFromGitHubActionsAsync(string packagePath, string productId, string version,
        TrustedPublishingOptions? options = null, CancellationToken cancellationToken = default, IProgress<PublishProgress>? progress = null)
    {
        options ??= new();
        if (client.DefaultRequestHeaders.Authorization is not null || client.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Use a publisher connection without account credentials for GitHub OIDC publishing.");
        if (string.IsNullOrWhiteSpace(productId) || productId.Length > 100 || productId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-') ||
            string.IsNullOrWhiteSpace(version) || version.Length > 128 || version.Any(char.IsControl))
            throw new ArgumentException("Trusted publishing requires the exact package product ID and version.");
        if (options.ActivationTimeout <= TimeSpan.Zero || options.ActivationTimeout > TimeSpan.FromHours(1) || options.PollInterval <= TimeSpan.Zero || options.PollInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Activation wait and polling limits are invalid.");
        if (options.UploadId is not null && !Guid.TryParseExact(options.UploadId, "N", out _)) throw new ArgumentException("The resume upload ID is invalid.");
        var path = Path.GetFullPath(packagePath);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".hinst" or ".hupd" or ".exe")) throw new ArgumentException("Publish a signed .hinst, .hupd update, or standalone Setup.exe.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new InvalidDataException("Package is empty.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
        string? uploadId = options.UploadId;
        PublisherGrant? grant = null;

        async Task RenewAsync(CancellationToken token)
        {
            var identity = await (options.OidcTokenProvider ?? GitHubActionsOidcClient.RequestTokenAsync)(token);
            if (!IsCredential(identity)) throw new InvalidDataException("The OIDC provider returned an invalid credential.");
            using var exchange = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "api/installer/publisher/oidc/token"))
            { Content = JsonContent(new { oidcToken = identity, productId, version, packageSha256 = hash, uploadId }) };
            using var response = await client.SendAsync(exchange, HttpCompletionOption.ResponseHeadersRead, token);
            await EnsureSuccessAsync(response, token);
            var next = JsonSerializer.Deserialize<PublisherGrant>(await ReadBoundedAsync(response, token), Json) ?? throw new InvalidDataException("Publisher returned no OIDC grant.");
            if (!IsCredential(next.AccessToken) || next.ExpiresUtc <= DateTimeOffset.UtcNow || next.ExpiresUtc > DateTimeOffset.UtcNow.AddHours(1) ||
                next.ProductId != productId || next.Version != version || !string.Equals(next.PackageSha256, hash, StringComparison.OrdinalIgnoreCase) ||
                next.Scope != "installer:publish" || string.IsNullOrWhiteSpace(next.PublisherId) ||
                (next.UploadId is not null && !Guid.TryParseExact(next.UploadId, "N", out _)) ||
                (uploadId is not null && next.UploadId != uploadId) ||
                (grant is not null && next.PublisherId != grant.PublisherId)) throw new InvalidDataException("Publisher returned a grant outside the requested package scope.");
            grant = next;
            uploadId ??= next.UploadId;
        }
        async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken token)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (grant is null || grant.ExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(30)) await RenewAsync(token);
                using var request = create();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant!.AccessToken);
                request.Headers.Add("X-HeirowInstaller-SHA256", hash);
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
                response.Dispose();
                await RenewAsync(token);
            }
        }
        void ValidateReceipt(PublishedRelease receipt)
        {
            if (receipt.ProductId != productId || receipt.Version != version || receipt.UploadedSize != file.Length ||
                !string.Equals(receipt.UploadedPackageSha256, hash, StringComparison.OrdinalIgnoreCase) || receipt.Status is not ("pending" or "activated" or "rejected") ||
                (receipt.Status == "activated" && (receipt.Size <= 0 || receipt.Sha256.Length != 64 || !receipt.Sha256.All(Uri.IsHexDigit))) ||
                !Guid.TryParseExact(receipt.ReceiptId, "N", out _)) throw new InvalidDataException("Publisher receipt does not match the exact requested package.");
        }
        async Task<PublishedRelease> FinishAsync(PublishedRelease receipt)
        {
            ValidateReceipt(receipt);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(options.ActivationTimeout);
            try
            {
                while (options.WaitForActivation && receipt.Status == "pending")
                {
                    await Task.Delay(options.PollInterval, wait.Token);
                    using var response = await SendAsync(() => new(HttpMethod.Get, new Uri(origin, "api/installer/publisher/receipts/" + receipt.ReceiptId)), wait.Token);
                    await EnsureSuccessAsync(response, wait.Token);
                    var next = JsonSerializer.Deserialize<PublishedRelease>(await ReadBoundedAsync(response, wait.Token), Json) ?? throw new InvalidDataException("Publisher returned no activation receipt.");
                    ValidateReceipt(next);
                    if (next.ReceiptId != receipt.ReceiptId) throw new InvalidDataException("Publisher changed the activation receipt identity.");
                    receipt = next;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Publication remains pending; activation was not confirmed before the wait deadline."); }
            if (receipt.Status == "rejected") throw new InvalidOperationException("The publisher rejected package activation.");
            return receipt;
        }

        await RenewAsync(cancellationToken);
        if (uploadId is null)
        {
            using var response = await SendAsync(() => new(HttpMethod.Post, new Uri(origin, "api/installer/publisher/uploads"))
            { Content = JsonContent(new { filename = Path.GetFileName(path), length = file.Length, sha256 = hash, productId, version, releaseNotes = options.ReleaseNotes ?? "" }) }, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var begin = JsonSerializer.Deserialize<TrustedUploadState>(await ReadBoundedAsync(response, cancellationToken), Json) ?? throw new InvalidDataException("Publisher returned no upload session.");
            if (begin.Receipt is not null) return await FinishAsync(begin.Receipt);
            if (!Guid.TryParseExact(begin.UploadId, "N", out _)) throw new InvalidDataException("Publisher returned an invalid upload ID.");
            uploadId = begin.UploadId;
        }
        async Task<TrustedUploadState> StateAsync()
        {
            using var response = await SendAsync(() => new(HttpMethod.Get, new Uri(origin, "api/installer/publisher/uploads/" + uploadId)), cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var state = JsonSerializer.Deserialize<TrustedUploadState>(await ReadBoundedAsync(response, cancellationToken), Json) ?? throw new InvalidDataException("Publisher returned no upload state.");
            if (state.UploadId != uploadId || state.Length != file.Length || state.ProductId != productId || state.Version != version ||
                !string.Equals(state.Sha256, hash, StringComparison.OrdinalIgnoreCase) || state.ChunkSize is <= 0 or > 4 * 1024 * 1024 || state.ReceivedBytes < 0 || state.ReceivedBytes > file.Length)
                throw new InvalidDataException("Publisher returned upload state outside the requested package scope.");
            return state;
        }
        var state = await StateAsync();
        if (state.Receipt is not null) return await FinishAsync(state.Receipt);
        var buffer = new byte[state.ChunkSize];
        var retries = 0;
        while (state.ReceivedBytes < file.Length)
        {
            var offset = state.ReceivedBytes;
            file.Position = offset;
            var count = (int)Math.Min(buffer.Length, file.Length - offset);
            await file.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            try
            {
                using var response = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, new Uri(origin, "api/installer/publisher/uploads/" + uploadId + "?offset=" + offset.ToString(CultureInfo.InvariantCulture)));
                    request.Headers.Add("X-HeirowInstaller-Chunk-SHA256", Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))).ToLowerInvariant());
                    request.Content = new ByteArrayContent(buffer, 0, count);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return request;
                }, cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);
                var ack = JsonSerializer.Deserialize<UploadAcknowledgement>(await ReadBoundedAsync(response, cancellationToken), Json);
                if (ack?.ReceivedBytes != offset + count) throw new InvalidDataException("Publisher acknowledged an unexpected upload offset.");
                state.ReceivedBytes = ack.ReceivedBytes;
                retries = 0;
                progress?.Report(new(state.ReceivedBytes, file.Length));
            }
            catch (HttpRequestException error) when (error.StatusCode is null or HttpStatusCode.Conflict && retries++ < 3)
            { state = await StateAsync(); }
        }
        using var complete = await SendAsync(() => new(HttpMethod.Post, new Uri(origin, "api/installer/publisher/uploads/" + uploadId + "/complete")) { Content = JsonContent(new { }) }, cancellationToken);
        await EnsureSuccessAsync(complete, cancellationToken);
        var result = JsonSerializer.Deserialize<PublishedRelease>(await ReadBoundedAsync(complete, cancellationToken), Json) ?? throw new InvalidDataException("Publisher returned no publication receipt.");
        return await FinishAsync(result);
    }

    private static ByteArrayContent JsonContent<T>(T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
    internal static bool IsCredential(string? value) => value is { Length: > 0 and <= 32768 } && value.All(c => c is >= (char)33 and <= (char)126);
    internal static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + count > 65536) throw new InvalidDataException("Publisher response exceeds its metadata limit.");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }
    private sealed class PublisherGrant
    {
        public string AccessToken { get; set; } = "";
        public DateTimeOffset ExpiresUtc { get; set; }
        public string? UploadId { get; set; }
        public string PublisherId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string Version { get; set; } = "";
        public string PackageSha256 { get; set; } = "";
        public string Scope { get; set; } = "";
        public override string ToString() => "PublisherGrant [redacted]";
    }
    private sealed class TrustedUploadState
    {
        public string UploadId { get; set; } = "";
        public int ChunkSize { get; set; }
        public long ReceivedBytes { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string Version { get; set; } = "";
        public PublishedRelease? Receipt { get; set; }
    }
}
