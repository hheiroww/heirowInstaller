using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;

var passed = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); passed++; Console.WriteLine("PASS " + description); }
var root = Path.Combine(Path.GetTempPath(), "heirow-security-loader-integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await LifecycleAsync(root, Check);
    await NativeSubmissionAsync(Path.Combine(root, "first.hinst"), Check);
    await ActivationAsync(Check);
    Console.WriteLine($"Passed {passed} SecurityLoader installer/activation checks.");
    return 0;
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("heirow-security-loader-integration-", StringComparison.Ordinal))
        throw new InvalidOperationException("Integration cleanup escaped its dedicated test directory.");
    Directory.Delete(resolved, recursive: true);
}

static async Task LifecycleAsync(string root, Action<bool, string> check)
{
    var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "SecurityLoader"));
    using var signer = RSA.Create(2048);
    using var componentSigner = RSA.Create(2048);
    var keyPath = Path.Combine(root, "fixture-key.pem"); await File.WriteAllTextAsync(keyPath, signer.ExportPkcs8PrivateKeyPem());
    var childKeyPath = Path.Combine(root, "fixture-child-key.pem"); await File.WriteAllTextAsync(childKeyPath, componentSigner.ExportPkcs8PrivateKeyPem());
    var pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    var childPin = Convert.ToHexString(SHA256.HashData(componentSigner.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    check(childPin != pin, "The trusted outer installer and protected component have genuinely different RSA publisher identities");
    var firstCiphertext = RandomNumberGenerator.GetBytes(4096);
    await File.WriteAllBytesAsync(Path.Combine(source, "SecurityLoader", "critical.sjsl"), firstCiphertext);
    var componentDigest = Convert.ToHexString(SHA256.HashData(firstCiphertext)).ToLowerInvariant();
    var componentVersion = "2026.10.25.1";
    void WriteBootstrap(string digest)
    {
        var bootstrap = JsonSerializer.SerializeToUtf8Bytes(new { applicationId = "test.securityloader.delivery", componentVersion, components = new[] { new { componentId = "critical", componentDigest = digest, publisherKeySha256 = childPin } } });
        File.WriteAllBytes(Path.Combine(source, "SecurityLoader", "bootstrap.json"), bootstrap);
        var signature = componentSigner.SignData(bootstrap, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        File.WriteAllBytes(Path.Combine(source, "SecurityLoader", "bootstrap.sig"), signature);
        File.WriteAllText(Path.Combine(source, "SecurityLoader", "bootstrap.pub"), componentSigner.ExportSubjectPublicKeyInfoPem());
        check(componentSigner.VerifyData(bootstrap, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "Fixture protected bootstrap carries an actual signature by the independent child publisher");
    }
    WriteBootstrap(componentDigest);
    JsonElement Descriptor(string digest, bool draft = false, string? manifestPin = null, bool delegated = true, string? outerPin = null) => draft
        ? JsonSerializer.SerializeToElement(new { formatVersion = 1, hostedSigning = true, publisherKeySha256 = childPin, componentVersion, outerPublisherPin = pin, components = new[] { new { id = "critical", sourcePath = "critical.dll", executionMode = "local", runtime = "net10.0", entryType = "Private.Critical" } } })
        : JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["formatVersion"] = 1, ["publisherKeySha256"] = manifestPin ?? childPin, ["componentVersion"] = componentVersion,
            ["components"] = new[] { new { id = "critical", path = "SecurityLoader/critical.sjsl", componentDigest = digest } } }
            .Concat(delegated ? new Dictionary<string, object?> { ["outerPublisherPin"] = outerPin ?? pin } : []).ToDictionary(x => x.Key, x => x.Value));
    var manifest = new PackageManifest
    {
        Id = "test.securityloader.delivery", Name = "Encrypted delivery fixture", Publisher = "Test fixture", Version = "1.0.0", Scope = "user",
        Payload = new[] { "critical.sjsl", "bootstrap.json", "bootstrap.sig", "bootstrap.pub" }.Select(p => new PayloadFile { Source = "SecurityLoader/" + p, Path = "SecurityLoader/" + p }).ToList(),
        Modules = new() { ["SocketJackSecurityLoader"] = Descriptor(componentDigest) }
    };
    var manifestPath = Path.Combine(root, "manifest.json");
    string Build(string name)
    {
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var packagePath = Path.Combine(root, name); PackageBuilder.Build(manifestPath, source, packagePath, new() { SigningKeyPath = keyPath }); return packagePath;
    }
    var firstPackage = Build("first.hinst");
    var target = Path.Combine(root, "installed"); Directory.CreateDirectory(Path.Combine(target, "SecurityLoader"));
    var previousFile = new byte[] { 91, 92, 93, 94 };
    var installedCiphertext = Path.Combine(target, "SecurityLoader", "critical.sjsl"); await File.WriteAllBytesAsync(installedCiphertext, previousFile);
    var options = new InstallOptions { TargetDirectory = target, StateRoot = Path.Combine(root, "state"), Isolated = true, RegisterProduct = false, Launch = false, Silent = true,
        RequireSignature = true, TrustedPublisherKeySha256 = pin };
    var engine = new InstallerEngine();
    using (var package = PackageReader.Open(firstPackage))
    {
        var plan = engine.Plan(package, options);
        check(plan.Operations.Any(x => x.Contains("encrypted SecurityLoader", StringComparison.Ordinal)), "A trusted outer signature explicitly delegates encrypted delivery to the different child publisher");
        await engine.InstallAsync(package, options);
    }
    check(File.ReadAllBytes(installedCiphertext).SequenceEqual(firstCiphertext), "Installation preserves exact ciphertext bytes without unlocking or extracting a DLL");
    check(!Directory.EnumerateFiles(target, "*.dll", SearchOption.AllDirectories).Any(), "Encrypted delivery does not manufacture plaintext application DLLs");
    var receipt = InstallerEngine.ListInstalled(options.StateRoot).Single();
    check(receipt.PublisherKeySha256 == pin && receipt.Modules.Single(m => m.Kind == "extension:SocketJackSecurityLoader").Values["configuration"]!.Contains(childPin, StringComparison.Ordinal), "Delegated installation preserves the existing outer trust pin while recording the separate child pin");
    check(receipt.Version == "1.0.0" && receipt.Modules.Single(m => m.Kind == "extension:SocketJackSecurityLoader").Values["configuration"]!.Contains(componentVersion, StringComparison.Ordinal), "Explicit component release identity may differ from the ordinary application version");
    check(receipt.Modules.Any(m => m.Kind == "extension:SocketJackSecurityLoader" && m.Values.GetValueOrDefault("configuration")!.Contains(componentDigest, StringComparison.Ordinal)), "Installation receipt preserves the declared protected component identity");

    var secondCiphertext = RandomNumberGenerator.GetBytes(5107);
    await File.WriteAllBytesAsync(Path.Combine(source, "SecurityLoader", "critical.sjsl"), secondCiphertext);
    var secondDigest = Convert.ToHexString(SHA256.HashData(secondCiphertext)).ToLowerInvariant();
    componentVersion = "2026.10.25.2"; WriteBootstrap(secondDigest);
    manifest.Version = "2.0.0"; manifest.Modules["SocketJackSecurityLoader"] = Descriptor(secondDigest);
    var secondPackage = Build("second.hinst");
    using (var package = PackageReader.Open(secondPackage))
        await engine.InstallAsync(package, new() { TargetDirectory = target, StateRoot = options.StateRoot, Isolated = true, RegisterProduct = false, Launch = false, Silent = true });
    var updatedReceipt = InstallerEngine.ListInstalled(options.StateRoot).Single();
    check(File.ReadAllBytes(installedCiphertext).SequenceEqual(secondCiphertext) && updatedReceipt.Version == "2.0.0" && updatedReceipt.PublisherKeySha256 == pin, "A signed update reuses the installed publisher pin while replacing ciphertext and recording the new version");
    manifest.Modules["SocketJackSecurityLoader"] = Descriptor(secondDigest, outerPin: childPin);
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
    var attemptedRotation = Path.Combine(root, "attempted-outer-rotation.hinst"); PackageBuilder.Build(manifestPath, source, attemptedRotation, new() { SigningKeyPath = childKeyPath });
    try
    {
        using var package = PackageReader.Open(attemptedRotation);
        engine.Plan(package, new() { TargetDirectory = target, StateRoot = options.StateRoot, Isolated = true, RegisterProduct = false, TrustedPublisherKeySha256 = childPin });
        throw new Exception("Delegation rotated an installed publisher pin.");
    }
    catch (CryptographicException) { check(true, "Explicit new trust options and child delegation cannot rotate an installed outer publisher pin"); }
    await File.WriteAllTextAsync(installedCiphertext, "tampered ciphertext");
    await engine.RepairAsync(manifest.Id, options);
    check(File.ReadAllBytes(installedCiphertext).SequenceEqual(secondCiphertext), "Repair restores the verified encrypted payload from the signed package cache");
    await engine.UninstallAsync(manifest.Id, options);
    check(File.ReadAllBytes(installedCiphertext).SequenceEqual(previousFile), "Uninstall restores the unrelated file that existed before encrypted delivery");
    check(!File.Exists(Path.Combine(target, "SecurityLoader", "bootstrap.json")) && InstallerEngine.ListInstalled(options.StateRoot).Count == 0, "Uninstall removes task-owned trust metadata and installation receipt");
    await File.WriteAllBytesAsync(Path.Combine(source, "SecurityLoader", "critical.sjsl"), firstCiphertext); WriteBootstrap(componentDigest);

    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
    var unsignedPath = Path.Combine(root, "unsigned-sealed.hinst"); PackageBuilder.Build(manifestPath, source, unsignedPath);
    using (var package = PackageReader.Open(unsignedPath))
    {
        try
        {
            engine.Plan(package, new() { TargetDirectory = target, StateRoot = options.StateRoot, Isolated = true, RegisterProduct = false });
            throw new Exception("Unsigned protected delivery accepted under default signature options.");
        }
        catch (CryptographicException) { check(true, "Protected delivery requires an outer package signature even when ordinary install options do not"); }
    }
    using (var package = PackageReader.Open(firstPackage))
    {
        try { engine.Plan(package, new() { TargetDirectory = target, StateRoot = options.StateRoot, Isolated = true, RegisterProduct = false }); throw new Exception("Self-declared publisher trusted on first install."); }
        catch (CryptographicException) { check(true, "A self-signed protected package cannot establish its own first-install trust pin"); }
    }
    manifest.Modules["SocketJackSecurityLoader"] = Descriptor(componentDigest, delegated: false);
    var substitutedPin = Build("substituted-publisher.hinst");
    using (var package = PackageReader.Open(substitutedPin))
    {
        try { engine.Plan(package, options); throw new Exception("Different inner publisher pin accepted."); }
        catch (CryptographicException) { check(true, "A different child key without explicit trusted outer delegation is rejected"); }
    }

    void RejectDescriptor(string name, JsonElement descriptor, string message)
    {
        manifest.Modules["SocketJackSecurityLoader"] = descriptor;
        var path = Build(name + ".hinst");
        try { using var package = PackageReader.Open(path); engine.Plan(package, options); throw new Exception("Invalid protected delegation accepted: " + name); }
        catch (Exception error) when (error is CryptographicException or InvalidDataException) { check(true, message); }
    }
    JsonElement Change(Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var descriptor = System.Text.Json.Nodes.JsonNode.Parse(Descriptor(componentDigest).GetRawText())!.AsObject(); change(descriptor);
        return JsonSerializer.SerializeToElement(descriptor);
    }
    RejectDescriptor("wrong-outer-pin", Descriptor(componentDigest, outerPin: childPin), "A child cannot self-delegate by putting its own key in outerPublisherPin");
    RejectDescriptor("malformed-outer-pin", Descriptor(componentDigest, outerPin: "not-a-key-pin"), "Malformed outer delegation fingerprints are rejected");
    RejectDescriptor("missing-component-version", Change(d => d.Remove("componentVersion")), "A delegated release without an explicit componentVersion is rejected");
    RejectDescriptor("invalid-component-version", Change(d => d["componentVersion"] = "not-a-version"), "An invalid componentVersion cannot bind a protected release");
    RejectDescriptor("duplicate-component-id", Change(d => d["components"]!.AsArray().Add(d["components"]![0]!.DeepClone())), "Duplicate component identities are rejected even inside a trusted signed package");
    RejectDescriptor("duplicate-component-path", Change(d => { var duplicate = d["components"]![0]!.DeepClone(); duplicate["id"] = "another"; duplicate["componentDigest"] = new string('e', 64); d["components"]!.AsArray().Add(duplicate); }), "Two identities cannot alias the same encrypted payload path");
    await File.WriteAllBytesAsync(Path.Combine(source, "SecurityLoader", "another.sjsl"), RandomNumberGenerator.GetBytes(64));
    manifest.Payload.Add(new PayloadFile { Source = "SecurityLoader/another.sjsl", Path = "SecurityLoader/another.sjsl" });
    RejectDescriptor("duplicate-component-digest", Change(d => { var duplicate = d["components"]![0]!.DeepClone(); duplicate["id"] = "another"; duplicate["path"] = "SecurityLoader/another.sjsl"; d["components"]!.AsArray().Add(duplicate); }), "A repeated component digest with a distinct existing payload cannot describe another declared component");
    manifest.Payload.RemoveAll(p => p.Path == "SecurityLoader/another.sjsl");
    RejectDescriptor("missing-component-digest", Change(d => d["components"]![0]!.AsObject().Remove("componentDigest")), "Missing component digests are rejected");
    RejectDescriptor("wrong-component-version", Change(d => d["components"]![0]!["version"] = "0.0.1"), "An optional per-component version cannot contradict the signed component release");
    RejectDescriptor("wrong-component-publisher", Change(d => d["components"]![0]!["publisherKeySha256"] = pin), "An optional per-component publisher cannot contradict the delegated child pin");
    var duplicateField = Descriptor(componentDigest).GetRawText().Replace("\"componentVersion\":", "\"componentVersion\":\"9.9.9\",\"componentVersion\":", StringComparison.Ordinal);
    RejectDescriptor("duplicate-field", JsonDocument.Parse(duplicateField).RootElement.Clone(), "Duplicate protected JSON fields cannot substitute a release binding");

    manifest.Modules["SocketJackSecurityLoader"] = Descriptor(componentDigest, manifestPin: pin, delegated: false);
    using (var package = PackageReader.Open(Build("direct-same-key.hinst")))
        check(engine.Plan(package, options).Operations.Any(x => x.Contains("encrypted SecurityLoader", StringComparison.Ordinal)), "A direct same-key sealed package retains support without a delegation field");
    RejectDescriptor("same-key-wrong-delegation", Descriptor(componentDigest, manifestPin: pin, outerPin: childPin), "A supplied outer delegation must match even when outer and child publishers are the same");

    manifest.Modules["SocketJackSecurityLoader"] = Descriptor(componentDigest);
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
    var untrustedOuter = Path.Combine(root, "untrusted-outer.hinst"); PackageBuilder.Build(manifestPath, source, untrustedOuter, new() { SigningKeyPath = childKeyPath });
    try { using var package = PackageReader.Open(untrustedOuter); engine.Plan(package, options); throw new Exception("A declared delegation bypassed outer trust."); }
    catch (CryptographicException) { check(true, "The actual outer signer is verified against existing trust before any child delegation is considered"); }

    var tampered = Path.Combine(root, "tampered-signed-payload.hinst"); File.Copy(firstPackage, tampered);
    using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
    {
        var entry = zip.GetEntry("payload/SecurityLoader/critical.sjsl")!; entry.Delete();
        using var output = zip.CreateEntry("payload/SecurityLoader/critical.sjsl").Open(); var changed = firstCiphertext.ToArray(); changed[0] ^= 1; output.Write(changed);
    }
    try { using var package = PackageReader.Open(tampered); await engine.InstallAsync(package, options); throw new Exception("Delegated ciphertext tampering was accepted."); }
    catch (CryptographicException) { check(File.ReadAllBytes(installedCiphertext).SequenceEqual(previousFile) && InstallerEngine.ListInstalled(options.StateRoot).Count == 0, "Outer-signed payload hashes reject changed ciphertext before any installation mutation"); }
    var tamperedManifest = Path.Combine(root, "tampered-delegation.hinst"); File.Copy(firstPackage, tamperedManifest);
    using (var zip = ZipFile.Open(tamperedManifest, ZipArchiveMode.Update))
    {
        var entry = zip.GetEntry("manifest.json")!; string json; using (var input = new StreamReader(entry.Open())) json = input.ReadToEnd(); entry.Delete();
        using var output = new StreamWriter(zip.CreateEntry("manifest.json").Open()); output.Write(json.Replace(pin, childPin, StringComparison.Ordinal));
    }
    try { using var package = PackageReader.Open(tamperedManifest); engine.Plan(package, options); throw new Exception("Changed outer delegation accepted."); }
    catch (CryptographicException) { check(true, "Changing delegation metadata invalidates the real outer signature before planning"); }

    manifest.Version = "3.0.0"; manifest.Modules["SocketJackSecurityLoader"] = Descriptor(componentDigest, draft: true);
    var rawDraft = Build("raw-draft.hinst");
    using (var package = PackageReader.Open(rawDraft))
    {
        try { engine.Plan(package, options); throw new Exception("Raw source descriptor installed."); }
        catch (InvalidDataException) { check(true, "A source draft cannot be planned as an installable SecurityLoader release"); }
    }
    manifest.Modules["SocketJackSecurityLoader"] = Descriptor(componentDigest);
    manifest.Payload.RemoveAll(p => p.Path.EndsWith("bootstrap.sig", StringComparison.Ordinal));
    var incomplete = Build("incomplete.hinst");
    using (var package = PackageReader.Open(incomplete))
    {
        try { engine.Plan(package, options); throw new Exception("Incomplete trust metadata accepted."); }
        catch (InvalidDataException) { check(true, "A sealed descriptor with missing bootstrap trust metadata is rejected"); }
    }
}

static async Task NativeSubmissionAsync(string packagePath, Action<bool, string> check)
{
    var bytes = await File.ReadAllBytesAsync(packagePath);
    var inputHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var receiptId = Guid.NewGuid().ToString("N"); var uploadId = Guid.NewGuid().ToString("N");
    var receipt = new PublishedRelease { ProductId = "test.securityloader.delivery", Version = "1.0.0", ReceiptId = receiptId, Status = "pending",
        UploadedPackageSha256 = inputHash, UploadedSize = bytes.Length, Sha256 = new string('d', 64), Size = bytes.Length + 789 };
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var publisher = new PublisherClient(new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"), bearerToken: "fixture-native-account", allowLoopback: true);
    async Task ServeAsync(bool repeat, int resumeOffset = 0, bool recover = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var stage = 0; stage < (repeat ? 1 : recover ? 4 : 3); stage++)
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token); await using var stream = connection.GetStream();
            using var header = new MemoryStream(); var one = new byte[1];
            while (header.Length < 16384)
            {
                await stream.ReadExactlyAsync(one, deadline.Token); header.WriteByte(one[0]); var array = header.GetBuffer(); var count = (int)header.Length;
                if (count >= 4 && array[count - 4] == 13 && array[count - 3] == 10 && array[count - 2] == 13 && array[count - 1] == 10) break;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var fields = lines.Skip(1).Where(x => x.Contains(':')).Select(x => x.Split(':', 2)).ToDictionary(x => x[0], x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var length = int.TryParse(fields.GetValueOrDefault("Content-Length"), out var parsed) ? parsed : 0;
            if (length > 4 * 1024 * 1024) throw new Exception("Unbounded native fixture request.");
            var body = new byte[length]; await stream.ReadExactlyAsync(body, deadline.Token);
            check(fields.GetValueOrDefault("Authorization") == "Bearer fixture-native-account", "Native upload retains its authenticated publisher account");
            object payload;
            if (stage == 0)
            {
                check(lines[0].StartsWith("POST /api/installer/publisher/uploads ", StringComparison.Ordinal), "Native publication begins through the existing upload route");
                payload = repeat ? new { uploadId, chunkSize = 4 * 1024 * 1024, receivedBytes = bytes.Length, receipt } as object
                    : new { uploadId, chunkSize = 4 * 1024 * 1024, receivedBytes = resumeOffset };
            }
            else if (stage == 1)
            {
                check(lines[0].StartsWith("PUT /api/installer/publisher/uploads/" + uploadId + "?offset=" + resumeOffset + " ", StringComparison.Ordinal) && body.AsSpan().SequenceEqual(bytes.AsSpan(resumeOffset)),
                    "Native upload transfers the exact signed input bytes at the acknowledged offset");
                payload = recover ? new { } as object : new { receivedBytes = bytes.Length };
            }
            else if (recover && stage == 2)
            {
                check(lines[0].StartsWith("GET /api/installer/publisher/uploads/" + uploadId + " ", StringComparison.Ordinal), "Native uncertain-write recovery queries authoritative same-origin upload state");
                payload = new { uploadId, chunkSize = 4 * 1024 * 1024, receivedBytes = bytes.Length, length = bytes.Length, sha256 = inputHash };
            }
            else payload = receipt;
            var response = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var status = recover && stage == 1 ? 409 : stage == (recover ? 3 : 2) ? 202 : 200;
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
            await stream.WriteAsync(response, deadline.Token);
        }
    }
    var server = ServeAsync(repeat: false);
    var submission = await publisher.PublishAsync(packagePath); await server;
    check(submission.Status == "pending" && submission.UploadedPackageSha256 == inputHash && submission.Sha256 != inputHash,
        "Native submission accepts a sealed artifact receipt while verifying original upload identity");
    server = ServeAsync(repeat: true);
    var duplicate = await publisher.PublishAsync(packagePath); await server;
    check(duplicate.ReceiptId == receiptId && duplicate.UploadedPackageSha256 == inputHash, "Native exact-digest replay reuses its existing receipt without retransmitting ciphertext");
    server = ServeAsync(repeat: false, resumeOffset: 257);
    var resumed = await publisher.PublishAsync(packagePath); await server;
    check(resumed.ReceiptId == receiptId, "Native partial transfer resumes the same read-locked package instead of starting from zero");
    server = ServeAsync(repeat: false, recover: true);
    var recovered = await publisher.PublishAsync(packagePath); await server;
    check(recovered.ReceiptId == receiptId, "Native transfer recovers a lost write acknowledgement without deleting or duplicating the upload");
}

static async Task ActivationAsync(Action<bool, string> check)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
    using var publisher = new PublisherClient(origin, bearerToken: "fixture-account-token", allowLoopback: true);
    var id = Guid.NewGuid().ToString("N"); var inputHash = new string('a', 64); var outputHash = new string('b', 64);
    PublishedRelease Receipt(string status, bool transformed = false, string? uploadedHash = null) => new()
    {
        ProductId = "test.securityloader.delivery", Version = "2.0.0", ReceiptId = id, Status = status,
        UploadedPackageSha256 = uploadedHash ?? inputHash, UploadedSize = 1234,
        Sha256 = transformed ? outputHash : inputHash, Size = transformed ? 2345 : 1234,
        InstallerUrl = status == "activated" ? "https://publisher.example/final.hinst" : "", UpdateUrl = status == "activated" ? "https://publisher.example/update.json" : ""
    };
    async Task ServeAsync(params PublishedRelease[] responses)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var response in responses)
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token); await using var stream = connection.GetStream();
            using var header = new MemoryStream(); var one = new byte[1];
            while (header.Length < 16384)
            {
                await stream.ReadExactlyAsync(one, deadline.Token); header.WriteByte(one[0]); var bytes = header.GetBuffer(); var n = (int)header.Length;
                if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
            }
            var request = Encoding.ASCII.GetString(header.ToArray());
            check(request.StartsWith("GET /api/installer/publisher/receipts/" + id + " HTTP/1.1", StringComparison.Ordinal) && request.Contains("Authorization: Bearer fixture-account-token\r\n", StringComparison.Ordinal),
                "Native activation polling authenticates on the fixed same-origin receipt route");
            var body = JsonSerializer.SerializeToUtf8Bytes(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
            await stream.WriteAsync(body, deadline.Token);
        }
    }
    var server = ServeAsync(Receipt("pending"), Receipt("activated", transformed: true));
    var activated = await publisher.WaitForActivationAsync(Receipt("pending"), timeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(5));
    await server;
    check(activated.Activated && activated.Sha256 == outputHash && activated.UploadedPackageSha256 == inputHash && activated.Size == 2345,
        "Native wait distinguishes stable uploaded input identity from the transformed activated artifact");

    server = ServeAsync(Receipt("activated", uploadedHash: new string('c', 64)));
    try { await publisher.WaitForActivationAsync(Receipt("pending"), timeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(5)); throw new Exception("Substituted input receipt accepted."); }
    catch (InvalidDataException) { check(true, "Native wait rejects a substituted input digest before claiming activation"); }
    await server;
    server = ServeAsync(Receipt("rejected"));
    try { await publisher.WaitForActivationAsync(Receipt("pending"), timeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(5)); throw new Exception("Rejected activation accepted."); }
    catch (InvalidOperationException) { check(true, "Native wait reports rejection without claiming the submission is active"); }
    await server;
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
    try { await publisher.WaitForActivationAsync(Receipt("pending"), cancellation.Token, timeout: TimeSpan.FromSeconds(1), pollInterval: TimeSpan.FromMilliseconds(100)); throw new Exception("Cancelled activation continued."); }
    catch (OperationCanceledException) { check(true, "Native activation wait preserves explicit caller cancellation"); }
}
