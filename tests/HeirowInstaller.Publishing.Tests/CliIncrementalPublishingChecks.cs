using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

internal static class CliIncrementalPublishingChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        var cli = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cli-fixture-path.txt")).Trim();
        if (!Path.IsPathFullyQualified(cli) || !File.Exists(cli) || !File.Exists(Path.ChangeExtension(cli, ".runtimeconfig.json")))
            throw new InvalidOperationException("The actual CLI project must be built before publishing regression checks.");
        var directory = Path.Combine(root, "cli-incremental"); Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "ephemeral-test-key.pem");
        using var rsa = RSA.Create(2048); File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        var pin = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        const string product = "cli.incremental.fixture", from = "1.0.0", target = "2.0.0";
        var packages = new Dictionary<string, string>();
        var unchanged = RandomNumberGenerator.GetBytes(128 * 1024);
        foreach (var version in new[] { from, target })
        {
            var source = Path.Combine(directory, "source-" + version); Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "common.bin"), unchanged); File.WriteAllText(Path.Combine(source, "app.bin"), "version " + version);
            var manifest = new PackageManifest { FormatVersion = 2, Id = product, Name = "CLI incremental fixture", Publisher = "Fixture", Version = version, EntryPoint = "app.bin" };
            var path = Path.Combine(directory, version + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            packages[version] = Path.Combine(directory, version + ".hinst");
            PackageBuilder.Build(path, source, packages[version], new() { SigningKeyPath = keyPath });
        }
        var delta = Path.Combine(directory, "signed-transition.HUPD");
        IncrementalUpdateBuilder.Build(packages[from], packages[target], delta, new() { SigningKeyPath = keyPath });
        await using var server = new FixturePublisher(check);

        async Task<CommandResult> Run(string package, string[]? extra = null, bool trust = true, bool cookie = true)
        {
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath! : "dotnet");
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            foreach (var value in new[] { cli, "publish", package, "--publisher", server.Origin.AbsoluteUri, "--allow-loopback" }) start.ArgumentList.Add(value);
            if (trust) foreach (var value in new[] { "--trust-key", pin, "--require-signature" }) start.ArgumentList.Add(value);
            if (cookie) foreach (var value in new[] { "--cookie-env", "HEIROWINSTALLER_TEST_PUBLISH_COOKIE" }) start.ArgumentList.Add(value);
            foreach (var value in extra ?? []) start.ArgumentList.Add(value);
            // Isolate every subprocess from a real CI identity, account grant or
            // outbound proxy. The only credential is this inert loopback fixture.
            foreach (var variable in new[] { "ACTIONS_ID_TOKEN_REQUEST_URL", "ACTIONS_ID_TOKEN_REQUEST_TOKEN", "ACTIONS_RUNTIME_TOKEN", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) start.Environment.Remove(variable);
            start.Environment["GITHUB_ACTIONS"] = "false"; start.Environment["NO_PROXY"] = "*";
            start.Environment["HEIROWINSTALLER_TEST_PUBLISH_COOKIE"] = "SocketJackAuth=cli-loopback-fixture-only";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the owned CLI fixture.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            // The fixture responds even to unexpected requests, so validation
            // failures do not leave a child blocked on an unanswered upload.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                // This exact child is an inert publish-only fixture. Never
                // terminate an application, service or unowned process here.
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                throw;
            }
            return new(process.ExitCode, await stdout, await stderr);
        }
        async Task RejectedBeforeHttp(string package, string message, string[]? extra = null, bool trust = true)
        {
            var before = server.Requests.Count; var result = await Run(package, extra, trust);
            check(result.ExitCode != 0 && server.Requests.Count == before, message);
            check(!result.Error.Contains("Unknown option", StringComparison.Ordinal), "CLI validation failures are not hidden by missing option parser support");
        }

        server.ExpectUpload(delta, product, target);
        var published = await Run(delta, ["--wait-for-activation"]);
        check(published.ExitCode == 0, "Actual CLI publishes a signed .hupd through the authenticated chunk protocol");
        using (var receipt = JsonDocument.Parse(published.Output))
            check(receipt.RootElement.GetProperty("productId").GetString() == product && receipt.RootElement.GetProperty("version").GetString() == target,
                "CLI incremental publication returns the exact signed product and target version");
        check(!published.Output.Contains("cli-loopback-fixture-only", StringComparison.Ordinal) && !published.Error.Contains("cli-loopback-fixture-only", StringComparison.Ordinal), "CLI publishing output never includes its fixture credential");
        check(server.ExpectedRemaining == 0, "Signed incremental publication completed every bounded upload stage");

        var tampered = Path.Combine(directory, "tampered-payload.hupd"); File.Copy(delta, tampered);
        using (var archive = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            var entry = archive.Entries.First(value => value.FullName.StartsWith("payload/", StringComparison.Ordinal)); var name = entry.FullName; var length = checked((int)entry.Length); entry.Delete();
            using var output = archive.CreateEntry(name).Open(); output.Write(new byte[length]);
        }
        await RejectedBeforeHttp(tampered, "Actual CLI refuses tampered changed-file payload before opening any upload");
        var unsigned = Path.Combine(directory, "missing-transition-signature.hupd"); File.Copy(delta, unsigned);
        using (var archive = ZipFile.Open(unsigned, ZipArchiveMode.Update)) archive.GetEntry("update-signature.json")!.Delete();
        await RejectedBeforeHttp(unsigned, "Actual CLI requires the transition signature even without --require-signature", trust: false);
        var invalidBase = Path.Combine(directory, "invalid-base-signature.hupd"); File.Copy(delta, invalidBase);
        using (var archive = ZipFile.Open(invalidBase, ZipArchiveMode.Update))
        {
            archive.GetEntry("base/signature.json")!.Delete(); using var output = archive.CreateEntry("base/signature.json").Open(); output.Write("{}"u8);
        }
        await RejectedBeforeHttp(invalidBase, "Actual CLI verifies the original signed base metadata before HTTP");
        var beforeWrongKey = server.Requests.Count;
        var wrongKey = await Run(delta, ["--trust-key", new string('A', 64)]);
        check(wrongKey.ExitCode != 0 && server.Requests.Count == beforeWrongKey, "Actual CLI enforces the configured publisher-key pin before upload");

        server.ExpectUpload(packages[target], product, target);
        var full = await Run(packages[target]);
        check(full.ExitCode == 0 && server.ExpectedRemaining == 0, "Signed full-package CLI publication retains its existing validation and upload flow");

        await RejectedBeforeHttp(packages[target], "Actual CLI refuses browser-cookie authentication for sealed delivery before HTTP", ["--sealed-delivery"]);
        var beforeSealed = server.Requests.Count;
        var missingBearer = await Run(packages[target], ["--sealed-delivery"], cookie: false);
        check(missingBearer.ExitCode != 0 && !missingBearer.Error.Contains("Unknown option", StringComparison.Ordinal)
            && missingBearer.Error.Contains("in-memory bearer session", StringComparison.Ordinal) && server.Requests.Count == beforeSealed,
            "Actual CLI parses sealed delivery and refuses a missing native bearer session without HTTP");

        var beforeFlags = server.Requests.Count;
        var flags = await Run(delta, ["--github-oidc", "--wait-for-activation", "--resume-upload", Guid.NewGuid().ToString("N")], cookie: false);
        check(flags.ExitCode == 1 && flags.Error.Contains("GitHub Actions OIDC requires", StringComparison.Ordinal) && server.Requests.Count == beforeFlags,
            "Actual CLI parses GitHub OIDC, activation wait and resume flags while refusing absent CI identity without HTTP");
        var resume = await Run(delta, ["--resume-upload", Guid.NewGuid().ToString("N")]);
        check(resume.ExitCode == 2 && resume.Error.Contains("requires --github-oidc", StringComparison.Ordinal) && server.Requests.Count == beforeFlags,
            "Actual CLI rejects resume outside the explicit GitHub OIDC flow before HTTP");

        var protectedSource = Path.Combine(directory, "protected-source"); Directory.CreateDirectory(Path.Combine(protectedSource, "private"));
        File.WriteAllText(Path.Combine(protectedSource, "private", "worker.dll"), "inert remote fixture component, never loaded");
        File.WriteAllText(Path.Combine(protectedSource, "bootstrap.dll"), "inert public bootstrap fixture, never loaded");
        var protectedManifest = new PackageManifest { Id = "cli.protected.fixture", Name = "Protected draft fixture", Version = "1.0.0", Publisher = "Fixture" };
        protectedManifest.Modules["SocketJackSecurityLoader"] = JsonSerializer.SerializeToElement(new
        {
            formatVersion = 1, hostedSigning = true,
            components = new[] { new { id = "worker", sourcePath = "private/worker.dll", executionMode = "remote", entryType = "Private.Worker", runtime = "net10.0", dependencies = Array.Empty<string>() } },
            operations = new[] { new { id = "calculate", componentId = "worker", argumentsSchema = new { type = "object" }, resultSchema = new { type = "object" } } },
            publicPayload = new[] { "bootstrap.dll" }, bootstrapPath = "bootstrap.dll", bootstrapSha256 = PackageSafety.HashFile(Path.Combine(protectedSource, "bootstrap.dll"))
        });
        var protectedJson = Path.Combine(directory, "protected.json"); File.WriteAllText(protectedJson, JsonSerializer.Serialize(protectedManifest, InstallerJson.Options));
        var protectedPackage = Path.Combine(directory, "unsigned-hosted-draft.hinst"); PackageBuilder.Build(protectedJson, protectedSource, protectedPackage);
        var draft = await Run(protectedPackage, ["--github-oidc"], trust: false, cookie: false);
        check(draft.ExitCode == 1 && draft.Error.Contains("GitHub Actions OIDC requires", StringComparison.Ordinal) && server.Requests.Count == beforeFlags,
            "Unsigned hosted-signing full drafts retain their explicit OIDC preflight exception");
        await RejectedBeforeHttp(protectedPackage, "Actual CLI refuses browser-cookie credentials for native preparation", ["--protected-preparation"], trust: false);
        var ordinaryDraft = await Run(packages[target], ["--protected-preparation"], trust: false, cookie: false);
        check(ordinaryDraft.ExitCode == 2 && ordinaryDraft.Error.Contains("protected hosted-signing source draft", StringComparison.Ordinal)
            && server.Requests.Count == beforeFlags, "Actual CLI refuses a non-hosted source in native preparation before HTTP");
        var nativeDraft = await Run(protectedPackage, ["--protected-preparation"], trust: false, cookie: false);
        check(nativeDraft.ExitCode != 0 && nativeDraft.Error.Contains("in-memory bearer session", StringComparison.Ordinal)
            && !nativeDraft.Error.Contains("Unknown option", StringComparison.Ordinal) && server.Requests.Count == beforeFlags,
            "Actual CLI parses native preparation and requires its explicit in-memory bearer session before HTTP");
        var wrongLane = await Run(delta, ["--protected-preparation"], cookie: false);
        check(wrongLane.ExitCode == 2 && wrongLane.Error.Contains("requires a .hinst source draft", StringComparison.Ordinal) && server.Requests.Count == beforeFlags,
            "Actual CLI keeps file transitions out of the full protected preparation lane");
        var executableDraft = await Run(Path.Combine(directory, "not-a-preparation.exe"), ["--protected-preparation"], cookie: false);
        check(executableDraft.ExitCode == 2 && executableDraft.Error.Contains("requires a .hinst source draft", StringComparison.Ordinal)
            && server.Requests.Count == beforeFlags, "Actual CLI refuses executable preparation inputs before opening or uploading them");
        check(server.UnexpectedRequests == 0, "CLI regression fixtures made only the expected local publisher requests");
        if (server.Failure is not null) throw new Exception("CLI loopback publisher fixture failed.", server.Failure);
    }

    sealed record CommandResult(int ExitCode, string Output, string Error);
    sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
    sealed class FixturePublisher : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new(); readonly Task loop;
        readonly ConcurrentQueue<Func<Request, object>> expected = new(); readonly Action<bool, string> check;
        public Uri Origin { get; }
        public ConcurrentQueue<string> Requests { get; } = new();
        public int ExpectedRemaining => expected.Count;
        public int UnexpectedRequests { get; private set; }
        public Exception? Failure { get; private set; }
        public FixturePublisher(Action<bool, string> check)
        {
            this.check = check; listener.Start(); Origin = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"); loop = Serve();
        }
        public void ExpectUpload(string package, string product, string version)
        {
            var body = File.ReadAllBytes(package); var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(); var id = Guid.NewGuid().ToString("N");
            expected.Enqueue(request =>
            {
                check(request.Method == "POST" && request.Path == "/api/installer/publisher/uploads", "CLI opens the existing publisher upload endpoint");
                check(request.Headers.GetValueOrDefault("Cookie") == "SocketJackAuth=cli-loopback-fixture-only" && !request.Headers.ContainsKey("Authorization"), "CLI sends only the explicitly selected fixture cookie credential");
                using var metadata = JsonDocument.Parse(request.Body); var value = metadata.RootElement;
                check(value.GetProperty("filename").GetString() == Path.GetFileName(package) && value.GetProperty("sha256").GetString() == hash && value.GetProperty("length").GetInt64() == body.Length, "CLI upload metadata binds the original filename and exact signed file bytes");
                return new { uploadId = id, chunkSize = 4 * 1024 * 1024, receivedBytes = 0 };
            });
            long received = 0;
            for (long offset = 0; offset < body.Length; offset += 4 * 1024 * 1024)
            {
                var expectedOffset = offset;
                expected.Enqueue(request =>
                {
                    check(request.Method == "PUT" && request.Path == $"/api/installer/publisher/uploads/{id}?offset={expectedOffset}" && request.Body.Length <= 4 * 1024 * 1024 && request.Body.AsSpan().SequenceEqual(body.AsSpan((int)expectedOffset, request.Body.Length)), "Actual CLI transfers only the selected package's exact bounded chunk bytes");
                    received += request.Body.Length; return new { receivedBytes = received };
                });
            }
            expected.Enqueue(request =>
            {
                check(request.Method == "POST" && request.Path == $"/api/installer/publisher/uploads/{id}/complete" && received == body.Length, "Actual CLI commits only after all signed package bytes arrive");
                return new { productId = product, version, installerUrl = new Uri(Origin, Path.GetFileName(package)).AbsoluteUri, updateUrl = new Uri(Origin, "update.json").AbsoluteUri, sha256 = hash, size = body.Length };
            });
        }
        async Task Serve()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token); await using var stream = connection.GetStream();
                    using var header = new MemoryStream(); var one = new byte[1];
                    while (header.Length < 65536)
                    {
                        if (await stream.ReadAsync(one, stop.Token) == 0) throw new EndOfStreamException(); header.WriteByte(one[0]); var bytes = header.GetBuffer(); var size = (int)header.Length;
                        if (size >= 4 && bytes[size - 4] == 13 && bytes[size - 3] == 10 && bytes[size - 2] == 13 && bytes[size - 1] == 10) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
                    var fields = lines.Skip(1).Where(line => line.Contains(':')).Select(line => line.Split(':', 2)).ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    int length = int.TryParse(fields.GetValueOrDefault("Content-Length"), out var count) ? count : 0;
                    if (length < 0 || length > 4 * 1024 * 1024) throw new InvalidDataException("Unbounded CLI fixture request.");
                    var body = new byte[length]; await stream.ReadExactlyAsync(body, stop.Token); Requests.Enqueue(first[0] + " " + first[1]);
                    object response; int code;
                    try
                    {
                        if (expected.TryDequeue(out var respond)) { response = respond(new(first[0], first[1], fields, body)); code = 200; }
                        else { UnexpectedRequests++; response = new { error = "Unexpected fixture request" }; code = 503; }
                    }
                    catch (Exception error) { Failure = error; response = new { error = "Fixture assertion failed" }; code = 503; }
                    var json = JsonSerializer.SerializeToUtf8Bytes(response);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Fixture\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(json, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { Failure = error; }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; stop.Dispose(); }
    }
}
