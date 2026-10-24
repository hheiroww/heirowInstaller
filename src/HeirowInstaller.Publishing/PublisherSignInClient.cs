using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeirowInstaller.Publishing;

/// <summary>Starts a browser approval flow without accessing saved browser credentials.</summary>
public static class PublisherSignInClient
{
    public static async Task<PublisherSignInOperation> BeginAsync(Uri publisher,
        CancellationToken cancellationToken = default, bool allowLoopback = false)
    {
        if (!publisher.IsAbsoluteUri || publisher.UserInfo.Length != 0 ||
            (publisher.Scheme != Uri.UriSchemeHttps && !(allowLoopback && publisher.IsLoopback && publisher.Scheme == Uri.UriSchemeHttp)))
            throw new ArgumentException("Publisher sign-in requires HTTPS without credentials in the URL.", nameof(publisher));
        var origin = new Uri(publisher.GetLeftPart(UriPartial.Authority) + "/");
        var operation = new PublisherSignInOperation(origin, allowLoopback);
        try { await operation.BeginAsync(cancellationToken); return operation; }
        catch { operation.Dispose(); throw; }
    }
}

/// <summary>A single PKCE-bound approval, held in memory for at most five minutes.</summary>
public sealed class PublisherSignInOperation : IDisposable
{
    private const string Application = "heirowinstaller";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Uri origin;
    private readonly bool allowLoopback;
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly TcpListener callback = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromMinutes(5));
    private readonly string verifier = RandomSecret();
    private readonly string state = RandomSecret();
    private readonly string callbackSecret = RandomSecret();
    private Uri redirect = null!;
    private string requestId = "";
    private int completed;
    private int disposed;

    internal PublisherSignInOperation(Uri origin, bool allowLoopback) { this.origin = origin; this.allowLoopback = allowLoopback; }
    public Uri BrowserUrl { get; private set; } = null!;

    internal async Task BeginAsync(CancellationToken cancellationToken)
    {
        callback.Start(8);
        var port = ((IPEndPoint)callback.LocalEndpoint).Port;
        if (port < 1024) throw new IOException("The sign-in listener could not select an unprivileged loopback port.");
        redirect = new Uri($"http://127.0.0.1:{port}/socketjack-signin/{callbackSecret}");
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        using var response = await PostAsync("api/app-signin/requests",
            new { application = Application, redirectUri = redirect.AbsoluteUri, state, challenge }, linked.Token);
        var result = await ReadResponseAsync<Approval>(response, linked.Token);
        if (string.IsNullOrWhiteSpace(result.RequestId) || result.RequestId.Length > 128 || result.ExpiresIn is <= 0 or > 300 ||
            !Uri.TryCreate(result.Url, UriKind.Absolute, out var browser) || browser.UserInfo.Length != 0 ||
            !SameOrigin(origin, browser) || (browser.Scheme != Uri.UriSchemeHttps && !(allowLoopback && browser.IsLoopback && browser.Scheme == Uri.UriSchemeHttp)))
            throw new InvalidDataException("The publisher returned an invalid browser approval request.");
        requestId = result.RequestId;
        BrowserUrl = browser;
        lifetime.CancelAfter(TimeSpan.FromSeconds(result.ExpiresIn));
    }

    public async Task<PublisherSession> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref completed, 1) != 0) throw new InvalidOperationException("A browser approval can only be completed once.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            var code = await ReceiveCodeAsync(linked.Token);
            using var response = await PostAsync("api/app-signin/redeem",
                new { requestId, application = Application, redirectUri = redirect.AbsoluteUri, code, verifier }, linked.Token);
            var redeemed = await ReadResponseAsync<Redemption>(response, linked.Token);
            if (!redeemed.Authenticated || string.IsNullOrWhiteSpace(redeemed.AccessToken) || redeemed.AccessToken.Length > 8192 ||
                redeemed.AccessToken.Any(character => character < 33 || character > 126) || redeemed.ExpiresUtc <= DateTimeOffset.UtcNow ||
                string.IsNullOrWhiteSpace(redeemed.Username) || redeemed.Username.Length > 256)
                throw new InvalidDataException("The publisher returned an invalid sign-in result.");
            return new PublisherSession(redeemed.Username, redeemed.AccessToken, redeemed.IsAdministrator, redeemed.ExpiresUtc);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Browser sign-in expired. Start sign-in again."); }
        finally { callback.Stop(); }
    }

    private async Task<string> ReceiveCodeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var connection = await callback.AcceptTcpClientAsync(cancellationToken);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var stream = connection.GetStream();
            try
            {
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(one, requestTimeout.Token) == 0) break;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var firstLine = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", 2)[0].Split(' ');
                string? code = null;
                if (header.Count < 8192 && firstLine.Length == 3 && firstLine[0] == "GET" && firstLine[1].StartsWith('/') &&
                    Uri.TryCreate(redirect, firstLine[1], out var received) && SameOrigin(received, redirect) && received.AbsolutePath == redirect.AbsolutePath && received.Fragment.Length == 0)
                {
                    var query = ParseQuery(received.Query);
                    if (query.TryGetValue("state", out var suppliedState) && FixedEquals(state, suppliedState) &&
                        query.TryGetValue("code", out var suppliedCode) && suppliedCode.Length is >= 16 and <= 256 &&
                        suppliedCode.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) code = suppliedCode;
                }
                await RespondAsync(stream, code is not null, requestTimeout.Token);
                if (code is not null) return code;
            }
            catch (Exception error) when (error is IOException or SocketException or FormatException or UriFormatException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static Dictionary<string, string> ParseQuery(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.Length > 4096) return result;
        foreach (var part in value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !result.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]))) return [];
        }
        return result;
    }

    private static async Task RespondAsync(NetworkStream stream, bool accepted, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(accepted ? "Signed in. You can return to heirowInstaller." : "This sign-in callback was not accepted.");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Publisher sign-in was not accepted. HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is > 65536) throw new InvalidDataException("Publisher sign-in response exceeds the limit.");
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await content.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > 65536) throw new InvalidDataException("Publisher sign-in response exceeds the limit.");
            body.Write(buffer, 0, count);
        }
        try { return JsonSerializer.Deserialize<T>(body.ToArray(), Json) ?? throw new InvalidDataException("Publisher sign-in returned no response."); }
        catch (JsonException) { throw new InvalidDataException("Publisher sign-in returned an invalid response."); }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object value, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.PostAsync(new Uri(origin, path), content, cancellationToken);
    }

    private static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;
    private static bool FixedEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static string RandomSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); callback.Stop(); client.Dispose(); lifetime.Dispose();
    }
    private sealed class Approval { public string RequestId { get; set; } = ""; public string Url { get; set; } = ""; public int ExpiresIn { get; set; } }
    private sealed class Redemption
    {
        public bool Authenticated { get; set; }
        public string Username { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public bool IsAdministrator { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }
}

/// <summary>An in-memory credential. Neither token nor account details are written to installer settings.</summary>
public sealed class PublisherSession : IDisposable
{
    internal PublisherSession(string username, string token, bool administrator, DateTimeOffset expiresUtc)
    { Username = username; AccessToken = token; IsAdministrator = administrator; ExpiresUtc = expiresUtc; }
    public string Username { get; }
    [JsonIgnore] public string AccessToken { get; private set; }
    public bool IsAdministrator { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public override string ToString() => "SocketJack publisher session";
    public void Dispose() => AccessToken = "";
}
