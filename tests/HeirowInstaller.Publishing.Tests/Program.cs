using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Publishing;

var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description);
    passed++;
}

try
{
    using var insecure = new PublisherClient(new Uri("http://publisher.example"));
    throw new Exception("Non-HTTPS publisher accepted.");
}
catch (ArgumentException) { Check(true, "HTTPS required for public publishers"); }
try
{
    using var embedded = new PublisherClient(new Uri("https://user:password@publisher.example"));
    throw new Exception("Credential-bearing URL accepted.");
}
catch (ArgumentException) { Check(true, "Credentials in publisher URLs rejected"); }

var root = Path.Combine(Path.GetTempPath(), "heirowinstaller-publishing-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await TrustedPublishingChecks.RunAsync(Check, root);
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    var origin = new Uri($"http://127.0.0.1:{endpoint.Port}/");
    using var client = new PublisherClient(origin, bearerToken: "test-publisher-credential", allowLoopback: true);
    var body = new byte[4 * 1024 * 1024 + 513];
    new Random(17).NextBytes(body);
    var package = Path.Combine(root, "test.hinst");
    await File.WriteAllBytesAsync(package, body);
    var expectedHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    var uploadId = Guid.NewGuid().ToString("N");
    var uploadServer = Task.Run(async () =>
    {
        await ServeOnceAsync(listener, request =>
        {
            Check(request.RequestLine == "POST /api/installer/publisher/uploads HTTP/1.1", "Upload opens a bounded publisher session");
            Check(request.Headers.GetValueOrDefault("Authorization") == "Bearer test-publisher-credential", "Publisher credential supplied only as an HTTP header");
            Check(request.Headers.GetValueOrDefault("X-HeirowInstaller-SHA256") == expectedHash, "Upload declares the exact package SHA-256");
            using var metadata = JsonDocument.Parse(request.Body);
            Check(metadata.RootElement.GetProperty("length").GetInt64() == body.Length && metadata.RootElement.GetProperty("sha256").GetString() == expectedHash, "Session metadata declares the complete upload length and hash");
            Check(metadata.RootElement.GetProperty("releaseNotes").ValueKind == JsonValueKind.String && metadata.RootElement.GetProperty("releaseNotes").GetString() == "", "Omitted release notes use the publisher's required empty-string representation");
            return (200, "{\"uploadId\":\"" + uploadId + "\",\"chunkSize\":4194304,\"receivedBytes\":0}", null);
        });
        long offset = 0;
        while (offset < body.Length)
        {
            await ServeOnceAsync(listener, request =>
            {
                Check(request.RequestLine == $"PUT /api/installer/publisher/uploads/{uploadId}?offset={offset} HTTP/1.1", "Chunk uses the acknowledged sequential offset");
                Check(request.Body.Length <= 4 * 1024 * 1024 && request.Body.AsSpan().SequenceEqual(body.AsSpan((int)offset, request.Body.Length)), "Chunk body preserves exact package bytes within the 4 MiB limit");
                Check(request.Headers.GetValueOrDefault("X-HeirowInstaller-Chunk-SHA256") == Convert.ToHexString(SHA256.HashData(request.Body)).ToLowerInvariant(), "Each chunk has its own SHA-256");
                offset += request.Body.Length;
                return (200, "{\"receivedBytes\":" + offset + "}", null);
            });
        }
        await ServeOnceAsync(listener, request =>
        {
            Check(request.RequestLine == $"POST /api/installer/publisher/uploads/{uploadId}/complete HTTP/1.1", "Publication commits only after all chunks are accepted");
            return (200, "{\"productId\":\"test.product\",\"version\":\"1.0.0\",\"installerUrl\":\"https://publisher.example/test.hinst\",\"updateUrl\":\"https://publisher.example/update.json\",\"sha256\":\"" + expectedHash + "\",\"size\":" + body.Length + "}", null);
        });
    });
    var published = await client.PublishAsync(package);
    await uploadServer;
    Check(published.ProductId == "test.product" && published.Sha256 == expectedHash && published.Size == body.Length, "Publication receipt is parsed");

    var statusServer = ServeOnceAsync(listener, request =>
    {
        Check(request.RequestLine.StartsWith("GET /api/installer/publisher ", StringComparison.Ordinal), "Publisher status endpoint is used");
        return (200, "{\"publisherId\":\"socketjack.com\",\"name\":\"SocketJack\",\"canPublish\":true,\"maxPackageBytes\":1073741824}", null);
    });
    var status = await client.GetStatusAsync();
    await statusServer;
    Check(status.CanPublish && status.PublisherId == "socketjack.com", "Publisher permission status is parsed");

    var deniedServer = ServeOnceAsync(listener, _ => (403, "private server detail must not appear in an error", null));
    try { await client.PublishAsync(package); throw new Exception("Denied publication was accepted."); }
    catch (HttpRequestException error)
    {
        Check(error.StatusCode == HttpStatusCode.Forbidden && !error.Message.Contains("private server detail", StringComparison.Ordinal), "Permission errors redact server response details");
    }
    await deniedServer;

    var standalone = Path.Combine(root, "application-Setup.exe");
    File.Copy(package, standalone);
    var standaloneServer = ServeOnceAsync(listener, request =>
    {
        using var metadata = JsonDocument.Parse(request.Body);
        Check(metadata.RootElement.GetProperty("filename").GetString() == "application-Setup.exe", "Standalone setup executables use the same verified publisher upload protocol");
        return (403, "", null);
    });
    try { await client.PublishAsync(standalone); throw new Exception("Denied standalone upload accepted."); }
    catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Forbidden, "Standalone publication still requires real publisher authorization"); }
    await standaloneServer;

    var delta = Path.Combine(root, "application-update.hupd");
    File.Copy(package, delta);
    var deltaServer = ServeOnceAsync(listener, request =>
    {
        using var metadata = JsonDocument.Parse(request.Body);
        Check(metadata.RootElement.GetProperty("filename").GetString() == "application-update.hupd", "Signed incremental updates use the same bounded publisher upload protocol");
        return (403, "", null);
    });
    try { await client.PublishAsync(delta); throw new Exception("Denied incremental upload accepted."); }
    catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Forbidden, "Incremental publication retains real publisher authorization"); }
    await deltaServer;

    var redirectServer = ServeOnceAsync(listener, _ => (302, "", "https://other.example/collect"));
    try { await client.PublishAsync(package); throw new Exception("Publisher redirect was followed."); }
    catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Found, "Publisher redirects are refused"); }
    await redirectServer;

    var collisionServer = ServeOnceAsync(listener, _ => (409, "", null));
    try { await client.PublishAsync(package); throw new Exception("Version collision was accepted."); }
    catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Conflict, "Immutable version conflicts reach the caller"); }
    await collisionServer;

    var sealedServer = ServeOnceAsync(listener, request =>
    {
        using var metadata = JsonDocument.Parse(request.Body);
        Check(request.Headers.GetValueOrDefault("X-HeirowInstaller-Sealed-Delivery") == "true", "Finished sealed delivery explicitly selects its dedicated native lane");
        Check(metadata.RootElement.GetProperty("productId").GetString() == "test.product" && metadata.RootElement.GetProperty("version").GetString() == "1.0.0", "Sealed delivery binds the exact product and version before upload");
        return (403, "private denial must stay private", null);
    });
    try { await client.PublishSealedDeliveryAsync(package, "test.product", "1.0.0"); throw new Exception("Unauthorized sealed delivery accepted."); }
    catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Forbidden && !error.Message.Contains("private denial", StringComparison.Ordinal), "Sealed delivery retains server authorization and redacted failures"); }
    await sealedServer;
    using (var cookieClient = new PublisherClient(origin, sessionCookie: "fixture-cookie", allowLoopback: true))
    {
        try { await cookieClient.PublishSealedDeliveryAsync(package, "test.product", "1.0.0"); throw new Exception("Cookie credential accepted for native sealed delivery."); }
        catch (InvalidOperationException) { Check(true, "Native sealed delivery refuses browser-cookie authentication before any request"); }
    }

    await BrowserSignInChecksAsync(listener, origin, Check);
    listener.Stop();
    Console.WriteLine($"Passed {passed} publishing checks.");
    return 0;
}
finally
{
    // The exact test-created directory is removed after it has been verified.
    var absolute = Path.GetFullPath(root);
    if (!Path.GetFileName(absolute).StartsWith("heirowinstaller-publishing-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path.");
    Directory.Delete(absolute, true);
}

static async Task BrowserSignInChecksAsync(TcpListener listener, Uri origin, Action<bool, string> check)
{
    try { using var ignored = await PublisherSignInClient.BeginAsync(new Uri("http://publisher.example")); throw new Exception("Insecure sign-in accepted."); }
    catch (ArgumentException) { check(true, "Browser sign-in requires HTTPS for public origins"); }
    try { using var ignored = await PublisherSignInClient.BeginAsync(new Uri("https://user:private@publisher.example")); throw new Exception("Credential URL accepted."); }
    catch (ArgumentException) { check(true, "Browser sign-in refuses credentials embedded in URLs"); }

    string redirect = "", state = "", challenge = "";
    var approvalId = Guid.NewGuid().ToString("N");
    var requestServer = ServeOnceAsync(listener, request =>
    {
        check(request.RequestLine == "POST /api/app-signin/requests HTTP/1.1" && request.Headers.ContainsKey("Content-Length"), "Approval uses the real bounded POST protocol with explicit content length");
        using var document = JsonDocument.Parse(request.Body);
        var metadata = document.RootElement;
        check(metadata.GetProperty("application").GetString() == "heirowinstaller" && !metadata.TryGetProperty("verifier", out _), "Dedicated installer application sends its PKCE challenge without the verifier");
        redirect = metadata.GetProperty("redirectUri").GetString()!;
        state = metadata.GetProperty("state").GetString()!;
        challenge = metadata.GetProperty("challenge").GetString()!;
        var callback = new Uri(redirect);
        check(callback.Host == "127.0.0.1" && callback.Port >= 1024 && callback.AbsolutePath.StartsWith("/socketjack-signin/", StringComparison.Ordinal) && state.Length == 43 && challenge.Length == 43,
            "Approval binds random state and a secret path to an unprivileged loopback listener");
        return (200, JsonSerializer.Serialize(new { requestId = approvalId, url = new Uri(origin, "app-signin/?request=" + approvalId).AbsoluteUri, expiresIn = 300 }), null);
    });
    using var operation = await PublisherSignInClient.BeginAsync(origin, allowLoopback: true);
    await requestServer;
    check(operation.BrowserUrl.AbsoluteUri.StartsWith(origin.AbsoluteUri, StringComparison.Ordinal), "Browser approval stays on the selected publisher origin");
    using var callbackClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var complete = operation.CompleteAsync();
    using (var rejected = await callbackClient.GetAsync(redirect + "?code=" + new string('a', 43) + "&state=" + new string('b', 43)))
        check(rejected.StatusCode == HttpStatusCode.BadRequest && !complete.IsCompleted, "A forged callback is rejected without cancelling the legitimate approval");
    var grantCode = new string('g', 43);
    var redemptionServer = ServeOnceAsync(listener, request =>
    {
        check(request.RequestLine == "POST /api/app-signin/redeem HTTP/1.1" && !request.Headers.ContainsKey("Cookie") && !request.Headers.ContainsKey("Authorization"),
            "Redemption uses the approval grant without reading or forwarding browser cookies");
        using var document = JsonDocument.Parse(request.Body);
        var body = document.RootElement;
        var verifier = body.GetProperty("verifier").GetString()!;
        var actualChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        check(actualChallenge == challenge && body.GetProperty("requestId").GetString() == approvalId && body.GetProperty("redirectUri").GetString() == redirect &&
            body.GetProperty("application").GetString() == "heirowinstaller" && body.GetProperty("code").GetString() == grantCode,
            "Redemption binds request, application, callback, one-use code and PKCE verifier");
        return (200, JsonSerializer.Serialize(new { authenticated = true, username = "fixture-publisher", accessToken = "test-disposable-credential", isAdministrator = true, expiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }), null);
    });
    using (var accepted = await callbackClient.GetAsync(redirect + "?code=" + grantCode + "&state=" + state))
        check(accepted.StatusCode == HttpStatusCode.OK && accepted.Headers.CacheControl?.NoStore == true, "Accepted loopback responses prohibit caching and disclose no account credential");
    using var session = await complete;
    await redemptionServer;
    check(session.AccessToken == "test-disposable-credential" && session.IsAdministrator && !JsonSerializer.Serialize(session).Contains("test-disposable-credential", StringComparison.Ordinal) &&
        !session.ToString().Contains("fixture-publisher", StringComparison.Ordinal), "Publisher credential stays in memory and is excluded from JSON and diagnostic text");
    try { using var duplicate = await operation.CompleteAsync(); throw new Exception("Approval reused."); }
    catch (InvalidOperationException) { check(true, "A completed browser approval cannot be redeemed again"); }
    session.Dispose();
    check(session.AccessToken.Length == 0, "Signing out clears the usable in-memory publisher credential");

    var wrongOriginServer = ServeOnceAsync(listener, _ => (200, JsonSerializer.Serialize(new { requestId = "fixture", url = "https://different.example/app-signin/", expiresIn = 300 }), null));
    try { using var ignored = await PublisherSignInClient.BeginAsync(origin, allowLoopback: true); throw new Exception("Unrelated approval origin accepted."); }
    catch (InvalidDataException) { check(true, "A publisher cannot redirect browser approval to another origin"); }
    await wrongOriginServer;

    var applicationServer = ServeOnceAsync(listener, request =>
    {
        using var metadata = JsonDocument.Parse(request.Body);
        check(metadata.RootElement.GetProperty("application").GetString() == "snap", "Application companions request their own registered identity for protected updates");
        return (200, JsonSerializer.Serialize(new { requestId = "application-fixture", url = new Uri(origin, "app-signin/").AbsoluteUri, expiresIn = 300 }), null);
    });
    using (var applicationApproval = await PublisherSignInClient.BeginAsync(origin, allowLoopback: true, applicationId: "snap")) { }
    await applicationServer;

    var deniedServer = ServeOnceAsync(listener, _ => (401, "private-response-that-must-not-be-printed", null));
    try { using var ignored = await PublisherSignInClient.BeginAsync(origin, allowLoopback: true); throw new Exception("Denied sign-in accepted."); }
    catch (HttpRequestException error) { check(error.StatusCode == HttpStatusCode.Unauthorized && !error.Message.Contains("private-response", StringComparison.Ordinal), "Sign-in errors preserve status and redact response bodies"); }
    await deniedServer;
}

static async Task ServeOnceAsync(TcpListener listener, Func<HttpRequest, (int Code, string Body, string? Location)> responder)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
    await using var stream = connection.GetStream();
    using var header = new MemoryStream();
    var one = new byte[1];
    while (header.Length < 65536)
    {
        if (await stream.ReadAsync(one, timeout.Token) == 0) throw new EndOfStreamException();
        header.WriteByte(one[0]);
        var bytes = header.GetBuffer();
        var n = (int)header.Length;
        if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
    }
    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in lines.Skip(1))
    {
        var colon = line.IndexOf(':');
        if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
    }
    var length = int.TryParse(headers.GetValueOrDefault("Content-Length"), out var parsed) ? parsed : 0;
    if (length > 4 * 1024 * 1024) throw new InvalidDataException("Test upload exceeds its chunk limit.");
    var body = new byte[length];
    await stream.ReadExactlyAsync(body, timeout.Token);
    var result = responder(new HttpRequest(lines[0], headers, body));
    var responseBody = Encoding.UTF8.GetBytes(result.Body);
    var location = result.Location is null ? "" : "Location: " + result.Location + "\r\n";
    var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {result.Code} Test\r\nContent-Type: application/json\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n{location}\r\n");
    await stream.WriteAsync(responseHeader, timeout.Token);
    await stream.WriteAsync(responseBody, timeout.Token);
}

sealed record HttpRequest(string RequestLine, Dictionary<string, string> Headers, byte[] Body);
