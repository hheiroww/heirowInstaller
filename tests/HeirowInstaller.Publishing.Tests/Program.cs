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

    var redirectServer = ServeOnceAsync(listener, _ => (302, "", "https://other.example/collect"));
    try { await client.PublishAsync(package); throw new Exception("Publisher redirect was followed."); }
    catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Found, "Publisher redirects are refused"); }
    await redirectServer;

    var collisionServer = ServeOnceAsync(listener, _ => (409, "", null));
    try { await client.PublishAsync(package); throw new Exception("Version collision was accepted."); }
    catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.Conflict, "Immutable version conflicts reach the caller"); }
    await collisionServer;
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
