using System.Net;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private static string CatalogJson(string latest = "2.0", string[]? versions = null, string? next = null, string channel = "public")
        => JsonSerializer.Serialize(new { schemaVersion = 1, productId = "test.catalog", channel, latestVersion = latest,
            versions = versions ?? [latest], nextBeforeVersion = next });

    private static HttpResponseMessage CatalogResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private async Task VersionCatalogComparison()
    {
        foreach (var (current, latest, state) in new[]
        {
            ("1.9", "1.10", InstallerVersionCheckState.UpdateAvailable),
            ("1.0.0", "1.0", InstallerVersionCheckState.Current),
            ("2.0", "1.10", InstallerVersionCheckState.Ahead),
            ("1.0-beta.1", "1.0", InstallerVersionCheckState.UpdateAvailable),
            ("1.0+fixture", "1.0", InstallerVersionCheckState.Current)
        })
        {
            var transport = new CatalogTransport((_, _) => Task.FromResult(CatalogResponse(CatalogJson(latest))));
            using var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), transport);
            var result = await client.CheckAsync("test.catalog", current);
            Equal(state, result.State); Equal(current, result.CurrentVersion); Equal(latest, result.LatestVersion);
            Equal(state == InstallerVersionCheckState.UpdateAvailable, result.Available);
            Equal(state == InstallerVersionCheckState.Current, result.UpToDate);
            Equal("/api/installer/catalog/test.catalog/versions", transport.Requests.Single().AbsolutePath);
            Equal("?channel=public&limit=1", transport.Requests.Single().Query);
            Equal(64L * 1024, transport.MaximumBytes);
        }
        // History must not make unactivated/newer metadata look like a published release.
        var rollback = new CatalogTransport((_, _) => Task.FromResult(CatalogResponse(CatalogJson("1.0", ["2.0"]))));
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), rollback))
            await Throws<InvalidDataException>(() => client.CheckAsync("test.catalog", "2.0"));

        var pages = new CatalogTransport((uri, _) => Task.FromResult(CatalogResponse(uri.Query.Contains("beforeVersion=1.9", StringComparison.Ordinal)
            ? CatalogJson("2.1", ["1.8"], channel: "beta")
            : CatalogJson("2.1", ["1.10", "1.9"], "1.9", "beta"))));
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), pages))
        {
            var first = await client.GetVersionsAsync("test.catalog", "beta", 2, "2.0");
            Equal("2.1", first.LatestVersion); Equal("1.10,1.9", string.Join(',', first.Versions)); Equal("1.9", first.NextBeforeVersion!);
            var second = await client.GetVersionsAsync("test.catalog", "beta", 2, first.NextBeforeVersion);
            Equal("2.1", second.LatestVersion); Equal("1.8", second.Versions.Single()); True(second.NextBeforeVersion is null);
        }
        True(!typeof(SocketJackVersionCatalogClient).Assembly.GetReferencedAssemblies().Any(x =>
            x.Name is "heirowUpdater" or "heirowInstaller" or "HeirowInstaller.App"));
    }

    private async Task VersionCatalogValidation()
    {
        var valid = CatalogJson();
        foreach (var invalid in new[]
        {
            "null", "[]", "<html>sign in</html>", "{}",
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            valid.Replace("test.catalog", "test.other", StringComparison.Ordinal),
            valid.Replace("\"public\"", "\"beta\"", StringComparison.Ordinal),
            valid.Replace("\"latestVersion\":\"2.0\"", "\"latestVersion\":null", StringComparison.Ordinal),
            valid.Replace("2.0", "not-a-version", StringComparison.Ordinal),
            valid.Replace(",\"nextBeforeVersion\":null", "", StringComparison.Ordinal),
            CatalogJson(versions: ["2.0", "1.0"]),
            CatalogJson(versions: []),
            CatalogJson("2.0", ["1.9"]),
            CatalogJson(next: "1.0")
        })
        {
            using var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"),
                new CatalogTransport((_, _) => Task.FromResult(CatalogResponse(invalid))));
            await Throws<InvalidDataException>(() => client.CheckAsync("test.catalog", "1.0"));
        }
        foreach (var rows in new[] { new[] { "1.0", "2.0" }, new[] { "1.0", "1.0.0" } })
        {
            using var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"),
                new CatalogTransport((_, _) => Task.FromResult(CatalogResponse(CatalogJson(versions: rows)))));
            await Throws<InvalidDataException>(() => client.GetVersionsAsync("test.catalog", limit: 2));
        }
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"),
            new CatalogTransport((_, _) => Task.FromResult(CatalogResponse(CatalogJson("2.0", ["1.0"]))))))
            await Throws<InvalidDataException>(() => client.GetVersionsAsync("test.catalog", beforeVersion: "1.0"));
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"),
            new CatalogTransport((_, _) => Task.FromResult(CatalogResponse(CatalogJson("2.0", []))))))
            Equal(0, (await client.GetVersionsAsync("test.catalog", beforeVersion: "0")).Versions.Count);

        foreach (var code in new[] { 302, 304, 400, 401, 403, 404, 503 })
        {
            var transport = new CatalogTransport((_, _) =>
            {
                var response = CatalogResponse(valid, (HttpStatusCode)code);
                response.Headers.Location = new Uri("https://other.example.invalid/catalog");
                return Task.FromResult(response);
            });
            using var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), transport);
            try { await client.CheckAsync("test.catalog", "1"); throw new Exception("A failed catalog response must not produce a version result."); }
            catch (HttpRequestException error) { Equal((HttpStatusCode)code, error.StatusCode!.Value); }
            Equal(1, transport.Requests.Count);
        }
        var untouched = new CatalogTransport((_, _) => throw new InvalidOperationException("Invalid selectors must fail before networking."));
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), untouched))
        {
            await Throws<InvalidDataException>(() => client.CheckAsync("test.catalog", "invalid"));
            await Throws<InvalidDataException>(() => client.CheckAsync("../wrong", "1"));
            await Throws<ArgumentException>(() => client.GetVersionsAsync("test.catalog", "unknown"));
            await Throws<ArgumentOutOfRangeException>(() => client.GetVersionsAsync("test.catalog", limit: 0));
            await Throws<ArgumentOutOfRangeException>(() => client.GetVersionsAsync("test.catalog", limit: 101));
            await Throws<InvalidDataException>(() => client.GetVersionsAsync("test.catalog", beforeVersion: "wrong"));
        }
        Equal(0, untouched.Requests.Count);
        foreach (var knownLength in new[] { false, true })
        {
            var transport = new CatalogTransport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = knownLength ? new ByteArrayContent(new byte[65537]) : new StreamContent(new CatalogBodyStream(new byte[65537])) }));
            using var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), transport);
            await Throws<InvalidDataException>(() => client.CheckAsync("test.catalog", "1"));
        }
    }

    private async Task VersionCatalogCancellation()
    {
        var headers = new StalledFeedTransport();
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), headers, TimeSpan.FromMilliseconds(150)))
            await Throws<TimeoutException>(() => client.CheckAsync("test.catalog", "1"));
        await CatalogEventually(() => headers.Canceled);
        var body = new StalledFeedStream();
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), new StalledFeedTransport(body), TimeSpan.FromMilliseconds(150)))
            await Throws<TimeoutException>(() => client.CheckAsync("test.catalog", "1"));
        await CatalogEventually(() => body.Canceled && body.Disposed);
        var pending = new StalledFeedTransport();
        using var cancellation = new CancellationTokenSource();
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), pending))
        {
            var checking = client.CheckAsync("test.catalog", "1", cancellationToken: cancellation.Token);
            await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Throws<OperationCanceledException>(() => checking);
            await CatalogEventually(() => pending.Canceled);
        }

        var lateHeaders = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateBody = new CatalogBodyStream(Encoding.UTF8.GetBytes(CatalogJson()));
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), new CatalogTransport((_, _) => lateHeaders.Task)))
        {
            var checking = client.CheckAsync("test.catalog", "1");
            try
            {
                await Throws<TimeoutException>(() => checking.WaitAsync(TimeSpan.FromSeconds(8)));
                True(checking.IsCompleted, "The default five-second deadline must bound a transport that ignores cancellation.");
            }
            finally { lateHeaders.TrySetResult(new(HttpStatusCode.OK) { Content = new StreamContent(lateBody) }); }
            await lateBody.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        var ignoredRead = new NoncancelableCatalogStream();
        using (var client = new VersionCatalogClient(new Uri("https://catalog.example.invalid"), new CatalogTransport((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(ignoredRead) })), TimeSpan.FromMilliseconds(150)))
        {
            var checking = client.CheckAsync("test.catalog", "1");
            try
            {
                await ignoredRead.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Throws<TimeoutException>(() => checking.WaitAsync(TimeSpan.FromSeconds(2)));
                True(checking.IsCompleted, "The deadline must also bound a response stream that ignores cancellation.");
                await ignoredRead.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { ignoredRead.Completion.TrySetResult(0); }
        }
    }

    private static async Task CatalogEventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception("The canceled catalog operation did not release its owned work.");
            await Task.Delay(10);
        }
    }

    private async Task VersionCatalogSocketJack()
    {
        var requests = new List<NetworkRequest>();
        await using var server = new LoopbackServer((NetworkRequest request) =>
        {
            requests.Add(request);
            var authorized = request.Headers.GetValueOrDefault("Cookie") == "catalog-fixture=session" &&
                request.Headers.GetValueOrDefault("Authorization") == "Bearer catalog-fixture-bearer";
            return new(authorized ? 200 : 401, Encoding.UTF8.GetBytes(CatalogJson(channel: "beta")));
        });
        var client = new SocketJackVersionCatalogClient(new Uri(server.Origin), allowLoopback: true);
        var session = new ApplicationSessionSnapshot(sessionCookie: "catalog-fixture=session", bearerToken: "catalog-fixture-bearer");
        var result = await client.CheckAsync("test.catalog", "1.0", session, "beta");
        True(result.Available); Equal("2.0", result.LatestVersion);
        await Throws<HttpRequestException>(() => client.CheckAsync("test.catalog", "1.0", channel: "beta"));
        Equal(2, requests.Count);
        True(!requests[1].Headers.ContainsKey("Cookie") && !requests[1].Headers.ContainsKey("Authorization"));
        True(requests.All(request => request.Path == "/api/installer/catalog/test.catalog/versions?channel=beta&limit=1"));

        var destinationRequests = 0;
        await using var destination = new LoopbackServer((NetworkRequest _) => { destinationRequests++; return new(200, Encoding.UTF8.GetBytes(CatalogJson())); });
        await using var redirect = new LoopbackServer((NetworkRequest _) => new(302, [], new() { ["Location"] = destination.Origin }));
        var redirectClient = new SocketJackVersionCatalogClient(new Uri(redirect.Origin), allowLoopback: true);
        await Throws<HttpRequestException>(() => redirectClient.CheckAsync("test.catalog", "1.0", session));
        Equal(0, destinationRequests);
    }

    private sealed class CatalogTransport(Func<Uri, CancellationToken, Task<HttpResponseMessage>> send) : IUpdateTransport
    {
        internal List<Uri> Requests { get; } = [];
        internal long MaximumBytes;
        public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken token)
        { Requests.Add(uri); MaximumBytes = maximumResponseBytes; return send(uri, token); }
    }

    private sealed class CatalogBodyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); Disposed.TrySetResult(); } base.Dispose(disposing); }
    }

    private sealed class NoncancelableCatalogStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); return new(Completion.Task); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Disposed.TrySetResult(); base.Dispose(disposing); }
    }
}
