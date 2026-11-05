using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.BootstrapContract;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;

internal static class BootstrapAttachmentChecks
{
    internal static async Task<int> RunStandaloneAsync(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) throw new ArgumentException("Supply an owned attachment test output directory.");
        string root = Path.GetFullPath(args[1]); if (Directory.Exists(root)) throw new IOException("Use fresh attachment test output."); Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); }
        async Task Reject<T>(Func<Task> action, string message) where T : Exception
        { try { await action(); } catch (T) { Check(true, message); return; } throw new InvalidOperationException(message); }
        using var key = RSA.Create(2048); string pin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var launcherBytes = new byte[512]; launcherBytes[0] = (byte)'M'; launcherBytes[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(launcherBytes.AsSpan(60), 128);
        "PE\0\0"u8.CopyTo(launcherBytes.AsSpan(128)); BinaryPrimitives.WriteUInt16LittleEndian(launcherBytes.AsSpan(148), 240); BinaryPrimitives.WriteUInt16LittleEndian(launcherBytes.AsSpan(152), 0x20b);
        string launcher = Path.Combine(root, "generic.exe"), bootstrap = Path.Combine(root, "web.exe"); await File.WriteAllBytesAsync(launcher, launcherBytes);
        await WebBootstrapBindings.WriteAsync(launcher, JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, productId = "fixture.app", version = "1.0.0", hostKind = "installer", publisherKeySha256 = pin }), bootstrap, key);
        byte[] webBytes = await File.ReadAllBytesAsync(bootstrap); string webHash = Convert.ToHexString(SHA256.HashData(webBytes));
        using var server = new AttachmentServer();
        using var publisher = new PublisherClient(server.Origin, bearerToken: "inert-native-fixture", allowLoopback: true);
        using (var noSession = new PublisherClient(server.Origin, allowLoopback: true))
            await Reject<InvalidOperationException>(async () => await noSession.AttachWebBootstrapAsync(bootstrap, "fixture.app", "1.0.0"), "attachment requires native bearer authentication before HTTP");
        using (var cookieSession = new PublisherClient(server.Origin, bearerToken: "inert-native-fixture", sessionCookie: "fixture=only", allowLoopback: true))
            await Reject<InvalidOperationException>(async () => await cookieSession.AttachWebBootstrapAsync(bootstrap, "fixture.app", "1.0.0"), "attachment rejects combined browser cookie and native session");
        await Reject<ArgumentException>(async () => await publisher.AttachWebBootstrapAsync(bootstrap, "../escape", "1.0.0"), "attachment cannot escape product route identity");
        await Reject<InvalidDataException>(async () => await publisher.AttachWebBootstrapAsync(bootstrap, "other.app", "1.0.0"), "local signed descriptor must match requested product before upload");
        await Reject<CryptographicException>(async () => await publisher.AttachWebBootstrapAsync(bootstrap, "fixture.app", "1.0.0", trustedPublisherKeySha256: new string('a', 64)), "caller publisher pin is checked before uploading bootstrap");
        Check(!server.Pending, "local attachment rejections open no publisher request");
        string WebReceipt(string? hash = null, string? url = null) => JsonSerializer.Serialize(new { productId = "fixture.app", version = "1.0.0", sha256 = hash ?? webHash, size = webBytes.Length,
            installerUrl = url ?? new Uri(server.Origin, "installers/fixture.app/1.0.0/fixture.app-1.0.0-WebSetup.exe").AbsoluteUri, immutable = true, sourcePackagePreserved = true });
        var upload = server.OnceAsync(request =>
        {
            Check(request.Line == "POST /api/installer/publisher/bootstrap/fixture.app/1.0.0 HTTP/1.1", "SDK uses exact bootstrap attachment route");
            Check(request.Headers.GetValueOrDefault("Authorization") == "Bearer inert-native-fixture" && request.Headers.GetValueOrDefault("Content-Type") == "application/octet-stream", "native bearer and binary media type use HTTP headers");
            Check(request.Headers.GetValueOrDefault("X-HeirowInstaller-SHA256")!.Equals(webHash, StringComparison.OrdinalIgnoreCase) && request.Body.SequenceEqual(webBytes), "attachment hashes and uploads the same leased file bytes");
            return (201, WebReceipt(), (string?)null);
        });
        var progress = new List<PublishProgress>(); var receipt = await publisher.AttachWebBootstrapAsync(bootstrap, "fixture.app", "1.0.0", progress: new Reports(progress), trustedPublisherKeySha256: pin); await upload;
        Check(receipt.Immutable && receipt.SourcePackagePreserved && receipt.Size == webBytes.Length && progress.First().BytesSent == 0 && progress.Last().BytesSent == webBytes.Length, "typed attachment receipt and asynchronous byte progress are exact");
        foreach (string rejected in new[] { WebReceipt(new string('b', 64)), WebReceipt(url: "https://other.invalid/installer.exe") })
        {
            var wrong = server.OnceAsync(_ => (201, rejected, (string?)null));
            await Reject<InvalidDataException>(async () => await publisher.AttachWebBootstrapAsync(bootstrap, "fixture.app", "1.0.0"), "attachment rejects mismatched digest or unrelated receipt origin"); await wrong;
        }
        var redirected = server.OnceAsync(_ => (307, "private server body", new Uri(server.Origin, "credential-trap").AbsoluteUri));
        await Reject<HttpRequestException>(async () => await publisher.AttachWebBootstrapAsync(bootstrap, "fixture.app", "1.0.0"), "attachment refuses credential-bearing POST redirects"); await redirected;
        Check(!server.Pending, "refused attachment redirect is never replayed");

        string source = Path.Combine(root, "beta-source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "app.bin"), "signed inert beta file");
        string keyPath = Path.Combine(root, "ephemeral-fixture-key.pem"); await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
        string manifestPath = Path.Combine(root, "beta.json"), beta = Path.Combine(root, "application.hinst");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new PackageManifest { FormatVersion = 3, Id = "FruityLoopsCollab", Name = "Fixture", Version = "1.0.0", Publisher = "Fixture", EntryPoint = "app.bin", LegalDocuments = LegalFixture.For("FruityLoopsCollab") }, InstallerJson.Options));
        PackageBuilder.Build(manifestPath, source, beta, new() { SigningKeyPath = keyPath });
        byte[] betaBytes = await File.ReadAllBytesAsync(beta); string betaHash = Convert.ToHexString(SHA256.HashData(betaBytes));
        string BetaReceipt() => JsonSerializer.Serialize(new { version = "1.0.0", sha256 = betaHash, size = betaBytes.Length, installerUrl = new Uri(server.Origin, "api/installer/beta/FruityLoopsCollab/1.0.0/application.hinst").AbsoluteUri, immutable = true });
        var betaUpload = server.OnceAsync(request =>
        {
            Check(request.Line == "POST /api/installer/publisher/beta-application/1.0.0 HTTP/1.1" && request.Body.SequenceEqual(betaBytes), "SDK attaches the exact signed raw beta representation through its own route");
            return (201, BetaReceipt(), (string?)null);
        });
        var betaReceipt = await publisher.AttachBetaApplicationAsync(beta, "1.0.0", trustedPublisherKeySha256: pin); await betaUpload;
        Check(betaReceipt.Immutable && betaReceipt.Size == betaBytes.Length, "beta representation returns a separate immutable typed receipt");
        await Reject<InvalidDataException>(async () => await publisher.AttachBetaApplicationAsync(beta, "2.0.0"), "raw beta version must equal signed local target before upload");
        string tampered = Path.Combine(root, "tampered.hinst"); File.Copy(beta, tampered);
        using (var archive = ZipFile.Open(tampered, ZipArchiveMode.Update)) { var entry = archive.GetEntry("payload/app.bin")!; entry.Delete(); using var changed = archive.CreateEntry("payload/app.bin").Open(); changed.Write("changed"u8); }
        await Reject<InvalidDataException>(async () => await publisher.AttachBetaApplicationAsync(tampered, "1.0.0"), "raw beta payload tampering is rejected before upload");
        Check(!server.Pending, "invalid beta source performs no HTTP operation");
        var cliPath = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cli-fixture-path.txt")).Trim();
        async Task<(int Exit, string Output, string Error)> RunCli(string command, string path, bool cookie = false)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (string value in new[] { cliPath, command, path, "--publisher", server.Origin.AbsoluteUri, "--allow-loopback", "--version", "1.0.0", cookie ? "--cookie-env" : "--token-env", "HEIROW_ATTACHMENT_FIXTURE_CREDENTIAL" }) start.ArgumentList.Add(value);
            if (command == "attach-bootstrap") { start.ArgumentList.Add("--id"); start.ArgumentList.Add("fixture.app"); }
            start.Environment["HEIROW_ATTACHMENT_FIXTURE_CREDENTIAL"] = cookie ? "fixture=only" : "inert-native-fixture";
            foreach (string variable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "ACTIONS_ID_TOKEN_REQUEST_TOKEN" }) start.Environment.Remove(variable);
            start.Environment["NO_PROXY"] = "*";
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); return (process.ExitCode, await output, await error);
        }
        var cliWebUpload = server.OnceAsync(request => { Check(request.Line.StartsWith("POST /api/installer/publisher/bootstrap/fixture.app/1.0.0 "), "actual CLI uses typed web attachment endpoint"); return (201, WebReceipt(), (string?)null); });
        var cliWeb = await RunCli("attach-bootstrap", bootstrap); await cliWebUpload;
        Check(cliWeb.Exit == 0 && cliWeb.Output.Contains("sourcePackagePreserved") && !cliWeb.Output.Contains("inert-native-fixture") && !cliWeb.Error.Contains("inert-native-fixture"), "actual CLI reports immutable web attachment without printing its credential");
        var cliBetaUpload = server.OnceAsync(_ => (201, BetaReceipt(), (string?)null)); var cliBeta = await RunCli("attach-beta-application", beta); await cliBetaUpload;
        Check(cliBeta.Exit == 0 && cliBeta.Output.Contains("application.hinst"), "actual CLI attaches an existing raw beta representation");
        var cliDenied = await RunCli("attach-bootstrap", bootstrap, cookie: true); Check(cliDenied.Exit != 0 && !server.Pending, "actual CLI rejects browser-cookie attachment before any HTTP request");
        await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { passed = true, checks, retained = true, productionRequests = false }));
        Console.WriteLine($"PASS {checks} SDK/CLI attachment checks; retained {root}"); return 0;
    }
    private sealed class Reports(List<PublishProgress> reports) : IProgress<PublishProgress> { public void Report(PublishProgress value) => reports.Add(value); }
    private sealed record Request(string Line, Dictionary<string, string> Headers, byte[] Body);
    private sealed class AttachmentServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        internal Uri Origin { get; }
        internal bool Pending => listener.Pending();
        internal AttachmentServer() { listener.Start(); Origin = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"); }
        internal async Task OnceAsync(Func<Request, (int Status, string Body, string? Location)> respond)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); using var socket = await listener.AcceptTcpClientAsync(timeout.Token); await using var stream = socket.GetStream();
            using var header = new MemoryStream(); var one = new byte[1];
            while (header.Length < 65536)
            {
                if (await stream.ReadAsync(one, timeout.Token) == 0) throw new EndOfStreamException(); header.WriteByte(one[0]);
                if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1)) { int colon = line.IndexOf(':'); if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim(); }
            if (!int.TryParse(headers.GetValueOrDefault("Content-Length"), out int length) || length is < 0 or > 4 * 1024 * 1024) throw new InvalidDataException("Fixture body exceeds limit.");
            var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, timeout.Token); var response = respond(new(lines[0], headers, bytes)); var body = Encoding.UTF8.GetBytes(response.Body);
            string location = response.Location == null ? "" : "Location: " + response.Location + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{location}\r\n"), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        }
        public void Dispose() => listener.Stop();
    }
}
