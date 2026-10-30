using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Publishing;

internal static class TrustedPublishingChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        check(GitHubActionsOidcClient.Audience == "https://socketjack.com/installer-publisher", "GitHub identity uses only the registered installer publisher audience");
        var descriptor = JsonSerializer.SerializeToElement(new
        {
            formatVersion = 1, hostedSigning = true, preparationOnly = true,
            components = new[] { new { id = "worker", sourcePath = "private/worker.dll", executionMode = "remote", entryType = "Private.Worker", runtime = "net10.0", dependencies = Array.Empty<string>() } },
            operations = new[] { new { id = "calculate", componentId = "worker", argumentsSchema = new { type = "object" }, resultSchema = new { type = "object" } } },
            publicPayload = new[] { "bootstrap.dll" }, bootstrapPath = "bootstrap.dll", bootstrapSha256 = new string('a', 64)
        });
        var payload = new Dictionary<string, string> { ["private/worker.dll"] = new string('b', 64), ["bootstrap.dll"] = new string('a', 64) };
        check(SecurityLoaderManifest.Validate(descriptor, payload).HostedSigning, "Protected descriptor validates exact hashed payload and explicit hosted signing without loading application code");
        check(SecurityLoaderManifest.Validate(descriptor, payload).PreparationOnly, "Preparation-only source descriptors remain valid publishing inputs without becoming installable releases");
        foreach (var invalidFlag in new[] { "null", "1", "\"true\"" })
        {
            using var invalidFlagDocument = JsonDocument.Parse(descriptor.GetRawText().Replace("\"preparationOnly\":true", "\"preparationOnly\":" + invalidFlag, StringComparison.Ordinal));
            try { SecurityLoaderManifest.Validate(invalidFlagDocument.RootElement, payload); throw new Exception("A malformed preparation flag was accepted."); }
            catch (JsonException) { check(true, "Preparation-only publishing flags must be JSON booleans"); }
        }
        foreach (var invalidDescriptor in new[]
        {
            descriptor.GetRawText().Replace("\"publicPayload\":[\"bootstrap.dll\"]", "\"publicPayload\":[\"private/worker.dll\"]", StringComparison.Ordinal),
            descriptor.GetRawText().Replace("\"sourcePath\":\"private/worker.dll\"", "\"sourcePath\":\"../private/worker.dll\"", StringComparison.Ordinal),
            descriptor.GetRawText().Replace("\"formatVersion\":1", "\"formatVersion\":1,\"credential\":\"never-allowed\"", StringComparison.Ordinal),
            descriptor.GetRawText().Replace("\"argumentsSchema\":{\"type\":\"object\"}", "\"argumentsSchema\":null", StringComparison.Ordinal),
            descriptor.GetRawText().Replace("\"resultSchema\":{\"type\":\"object\"}", "\"resultSchema\":[]", StringComparison.Ordinal)
        })
        {
            try { using var json = JsonDocument.Parse(invalidDescriptor); SecurityLoaderManifest.Validate(json.RootElement, payload); throw new Exception("Unsafe descriptor accepted."); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { check(true, "Protected descriptor rejects public/private overlap, traversal or undeclared credential fields"); }
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        using var client = new PublisherClient(origin, allowLoopback: true);
        var bytes = new byte[4099];
        new Random(81).NextBytes(bytes);
        var path = Path.Combine(root, "trusted.hinst");
        await File.WriteAllBytesAsync(path, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var uploadId = Guid.NewGuid().ToString("N");
        var receiptId = Guid.NewGuid().ToString("N");
        const string product = "test.trusted";
        const string version = "2026.10.25.1";
        object Receipt(string status, string? digest = null, bool? updateActivationPending = null) => new { productId = product, version, uploadedPackageSha256 = digest ?? hash, uploadedSize = bytes.Length,
            sha256 = new string('a', 64), size = bytes.Length + 999, receiptId, status, updateActivationPending };
        var credentials = 0;
        var offset = 257;
        var stages = new Queue<Func<Request, (int, object)>>();
        (int, object) Grant(Request request, bool renewal)
        {
            check(request.Path == "/api/installer/publisher/oidc/token" && request.Authorization is null, "OIDC exchange receives no unrelated publisher credential");
            using var body = JsonDocument.Parse(request.Body);
            var value = body.RootElement;
            check(value.GetProperty("productId").GetString() == product && value.GetProperty("version").GetString() == version && value.GetProperty("packageSha256").GetString() == hash &&
                value.GetProperty("oidcToken").GetString() == "fixture-identity-" + (renewal ? "2" : "1"), "Fresh OIDC identity is bound to exact product, version and locked-file digest");
            if (renewal) check(value.GetProperty("uploadId").GetString() == uploadId, "Expired publisher credential renews only the same upload");
            return (200, new { accessToken = renewal ? "sjpub_second" : "sjpub_first", expiresUtc = DateTimeOffset.UtcNow.AddMinutes(15), uploadId = renewal ? uploadId : null,
                publisherId = "fixture", productId = product, version, packageSha256 = hash, scope = "installer:publish" });
        }
        object State() => new { uploadId, chunkSize = 1024, receivedBytes = offset, length = bytes.Length, sha256 = hash, productId = product, version };
        stages.Enqueue(request => Grant(request, false));
        stages.Enqueue(request => { check(request.Method == "POST" && request.Path == "/api/installer/publisher/uploads" && request.Authorization == "Bearer sjpub_first", "Trusted publisher starts an authenticated idempotent upload"); return (200, new { uploadId }); });
        stages.Enqueue(request => { check(request.Method == "GET" && request.Path.EndsWith(uploadId), "Resume state is retrieved before sending package chunks"); return (200, State()); });
        stages.Enqueue(_ => (401, new { error = "sensitive server text must stay private" }));
        stages.Enqueue(request => Grant(request, true));
        stages.Enqueue(request =>
        {
            check(request.Authorization == "Bearer sjpub_second" && request.Body.AsSpan().SequenceEqual(bytes.AsSpan(offset, 1024)), "Renewal retries the same exact bounded chunk");
            offset += request.Body.Length; // Simulate the server accepting bytes before an uncertain/conflicting acknowledgement.
            return (409, new { });
        });
        stages.Enqueue(_ => (200, State()));
        for (var i = offset + 1024; i < bytes.Length; i += 1024) // offset is updated by the fixture while running; queue enough remaining chunks.
        {
            stages.Enqueue(request =>
            {
                check(request.Path.EndsWith("?offset=" + offset) && request.Body.AsSpan().SequenceEqual(bytes.AsSpan(offset, request.Body.Length)), "Recovery seeks to the server's verified offset without restarting the upload");
                offset += request.Body.Length;
                return (200, new { receivedBytes = offset });
            });
        }
        stages.Enqueue(request => { check(request.Path.EndsWith("/complete") && offset == bytes.Length, "Trusted upload completes after the full package digest is transferred"); return (202, Receipt("pending")); });
        stages.Enqueue(request => { check(request.Path == "/api/installer/publisher/receipts/" + receiptId, "Activation wait uses the bound receipt instead of claiming submission is active"); return (200, Receipt("activated")); });
        var server = ServeAsync(listener, stages);
        var result = await client.PublishFromGitHubActionsAsync(path, product, version, new()
        {
            OidcTokenProvider = _ => Task.FromResult("fixture-identity-" + Interlocked.Increment(ref credentials)),
            WaitForActivation = true, PollInterval = TimeSpan.FromMilliseconds(5)
        });
        await server;
        check(result.Activated && result.ReceiptId == receiptId && credentials == 2 && result.Sha256 != hash && result.UploadedPackageSha256 == hash,
            "Activation verifies original input identity independently from the sealed installable artifact digest");

        var duplicate = new Queue<Func<Request, (int, object)>>();
        credentials = 0;
        duplicate.Enqueue(request => Grant(request, false));
        duplicate.Enqueue(_ => (200, new { receipt = Receipt("activated") }));
        server = ServeAsync(listener, duplicate);
        result = await client.PublishFromGitHubActionsAsync(path, product, version, new() { OidcTokenProvider = _ => Task.FromResult("fixture-identity-" + Interlocked.Increment(ref credentials)) });
        await server;
        check(result.Activated, "Exact version and digest replay reuses the existing receipt without uploading bytes");

        foreach (var discoveredDuringPolling in new[] { false, true })
        {
            var incomplete = new Queue<Func<Request, (int, object)>>();
            credentials = 0;
            incomplete.Enqueue(request => Grant(request, false));
            incomplete.Enqueue(_ => (200, new { receipt = Receipt("pending", updateActivationPending: discoveredDuringPolling ? null : true) }));
            if (discoveredDuringPolling)
                incomplete.Enqueue(request =>
                {
                    check(request.Path == "/api/installer/publisher/receipts/" + receiptId, "Trusted activation polls only its bound receipt before learning missing update coverage");
                    return (200, Receipt("activated", updateActivationPending: true));
                });
            server = ServeAsync(listener, incomplete);
            try
            {
                await client.PublishFromGitHubActionsAsync(path, product, version, new()
                {
                    OidcTokenProvider = _ => Task.FromResult("fixture-identity-" + Interlocked.Increment(ref credentials)),
                    WaitForActivation = true, PollInterval = TimeSpan.FromMilliseconds(5), ActivationTimeout = TimeSpan.FromSeconds(1)
                });
                throw new Exception("Incomplete update coverage was reported as active.");
            }
            catch (InvalidOperationException error)
            {
                check(error.Message.Contains("signed file updates", StringComparison.Ordinal) && error.Message.Contains("same full package", StringComparison.Ordinal),
                    discoveredDuringPolling ? "Trusted activation stops with an actionable coverage error learned during polling" : "Trusted activation stops with an actionable initial coverage error");
            }
            await server;
        }
        var incompleteActivation = JsonSerializer.Deserialize<PublishedRelease>(JsonSerializer.Serialize(Receipt("activated", updateActivationPending: true)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        check(!incompleteActivation.Activated, "A receipt with incomplete update coverage cannot claim activation even when its legacy status says activated");

        var invalid = new Queue<Func<Request, (int, object)>>();
        credentials = 0;
        invalid.Enqueue(request => Grant(request, false));
        invalid.Enqueue(_ => (200, new { receipt = Receipt("activated", new string('f', 64)) }));
        server = ServeAsync(listener, invalid);
        try { await client.PublishFromGitHubActionsAsync(path, product, version, new() { OidcTokenProvider = _ => Task.FromResult("fixture-identity-" + Interlocked.Increment(ref credentials)) }); throw new Exception("Mismatched receipt accepted."); }
        catch (InvalidDataException) { check(true, "Trusted receipt cannot substitute a different package digest"); }
        await server;

        using var mixed = new PublisherClient(origin, bearerToken: "unrelated", allowLoopback: true);
        try { await mixed.PublishFromGitHubActionsAsync(path, product, version); throw new Exception("Mixed credentials accepted."); }
        catch (InvalidOperationException) { check(true, "GitHub OIDC refuses mixed browser/account publisher credentials"); }

        var wrongGrant = new Queue<Func<Request, (int, object)>>();
        wrongGrant.Enqueue(_ => (200, new { accessToken = "secret-grant-never-printed", expiresUtc = DateTimeOffset.UtcNow.AddMinutes(15), publisherId = "fixture", productId = "other.product",
            version, packageSha256 = hash, scope = "installer:publish" }));
        server = ServeAsync(listener, wrongGrant);
        try { await client.PublishFromGitHubActionsAsync(path, product, version, new() { OidcTokenProvider = _ => Task.FromResult("test-oidc") }); throw new Exception("Wrong product grant accepted."); }
        catch (InvalidDataException error) { check(!error.Message.Contains("secret-grant", StringComparison.Ordinal), "Wrong-product grants fail before upload and never expose the returned credential"); }
        await server;
    }

    private static async Task ServeAsync(TcpListener listener, Queue<Func<Request, (int Code, object Body)>> responders)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (responders.TryDequeue(out var respond))
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            using var header = new MemoryStream();
            var one = new byte[1];
            while (header.Length < 65536)
            {
                if (await stream.ReadAsync(one, timeout.Token) == 0) throw new EndOfStreamException();
                header.WriteByte(one[0]);
                var a = header.GetBuffer(); var n = (int)header.Length;
                if (n >= 4 && a[n - 4] == 13 && a[n - 3] == 10 && a[n - 2] == 13 && a[n - 1] == 10) break;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var first = lines[0].Split(' ');
            var fields = lines.Skip(1).Where(x => x.Contains(':')).Select(x => x.Split(':', 2)).ToDictionary(x => x[0], x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var count = int.TryParse(fields.GetValueOrDefault("Content-Length"), out var length) ? length : 0;
            if (count > 4 * 1024 * 1024) throw new InvalidDataException("Unbounded fixture upload.");
            var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, timeout.Token);
            var response = respond(new(first[0], first[1], fields.GetValueOrDefault("Authorization"), bytes));
            var body = JsonSerializer.SerializeToUtf8Bytes(response.Body);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Code} Fixture\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        }
    }
    private sealed record Request(string Method, string Path, string? Authorization, byte[] Body);
}
