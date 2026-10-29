using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;

int checks = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
async Task Reject<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
const string id = "legal.fixture";
PackageLegalDocuments Draft(string privacy = "Fixture privacy policy.\nOnly isolated local test data is used. 😀", string terms = "Fixture terms.\r\nThis isolated test is not a distributed application.")
{
    var value = LegalDocumentRules.CreateDraft(id);
    value.PrivacyPolicy!.Content = privacy; value.TermsAndConditions!.Content = terms;
    return value;
}
var documents = LegalDocumentRules.PrepareForCreation(id, Draft());
LegalDocumentRules.Validate(documents); checks++;
Check(documents.PrivacyPolicy!.Size == Encoding.UTF8.GetByteCount(documents.PrivacyPolicy.Content), "UTF-8 size does not match Unicode content.");
Check(documents.PrivacyPolicy.Sha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documents.PrivacyPolicy.Content))).ToLowerInvariant(), "Content digest differs.");
Check(documents.TermsAndConditions!.Content.Contains("\r\n", StringComparison.Ordinal), "Creation normalized the supplied line endings.");
Check(documents.PrivacyPolicy.Url.EndsWith("/PrivacyPolicy/" + id, StringComparison.Ordinal)
    && documents.TermsAndConditions.Url.EndsWith("/TermsAndConditions/" + id, StringComparison.Ordinal), "Public URLs do not identify this product.");
var blankDraft = LegalDocumentRules.PrepareDraft(id, null);
LegalDocumentRules.ValidateDraft(blankDraft); checks++;
await Reject<InvalidDataException>(() => { LegalDocumentRules.PrepareForCreation(id, blankDraft); return Task.CompletedTask; });
await Reject<InvalidDataException>(() => { LegalDocumentRules.PrepareForCreation(id, null); return Task.CompletedTask; });
foreach (var input in new[] { Draft(""), Draft(" \r\n\t"), Draft("\uFEFF"), Draft(terms: ""), Draft("nul\0text"), Draft("control\u0085text"), Draft("invalid\uD800"), Draft(new string('a', 65537)), Draft(new string('多', 21846)) })
    await Reject<InvalidDataException>(() => { LegalDocumentRules.PrepareForCreation(id, input); return Task.CompletedTask; });
var maximum = LegalDocumentRules.PrepareForCreation(id, Draft(new string('a', 65536)));
Check(maximum.PrivacyPolicy!.Size == 65536, "The exact UTF-8 limit was rejected.");
foreach (var badUrl in new[] { "http://example.invalid/privacy", "https://user:secret@example.invalid/privacy", "javascript:alert(1)", "https://example.invalid/\n" })
{
    var input = Draft(); input.PrivacyPolicy!.Url = badUrl;
    await Reject<InvalidDataException>(() => { LegalDocumentRules.PrepareForCreation(id, input); return Task.CompletedTask; });
}
foreach (var badName in new[] { "../policy.txt", "folder/policy.txt", "policy.html", "CON.txt", "policy.txt " })
{
    var input = Draft(); input.PrivacyPolicy!.FileName = badName;
    await Reject<InvalidDataException>(() => { LegalDocumentRules.PrepareForCreation(id, input); return Task.CompletedTask; });
}
foreach (var mutation in new Action<PackageLegalDocument>[]
{
    doc => doc.Content += "changed", doc => doc.Size++, doc => doc.Sha256 = new string('0', 64),
    doc => doc.Sha256 = doc.Sha256.ToUpperInvariant(), doc => doc.MediaType = "text/html"
})
{
    var value = LegalDocumentRules.PrepareForCreation(id, Draft()); mutation(value.PrivacyPolicy!);
    await Reject<InvalidDataException>(() => { LegalDocumentRules.Validate(value); return Task.CompletedTask; });
}

var area = Path.Combine(Path.GetTempPath(), "heirowInstaller-legal-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(area);
await File.WriteAllTextAsync(Path.Combine(area, "app.txt"), "Fixture payload.");
var import = Path.Combine(area, "privacy.md");
await File.WriteAllTextAsync(import, "# Fixture policy\r\nOriginal line endings.\n", new UTF8Encoding(false));
var imported = await LegalDocumentRules.ImportFileAsync(import, documents.PrivacyPolicy.Url);
Check(imported.MediaType == "text/markdown" && imported.Content.Contains("\r\n", StringComparison.Ordinal), "File-to-text import changed content or media type.");
await File.WriteAllBytesAsync(import, [0xff, 0xfe, 0x00, 0x00]);
await Reject<InvalidDataException>(() => LegalDocumentRules.ImportFileAsync(import, documents.PrivacyPolicy.Url));
var manifest = new PackageManifest { Id = id, Name = "Isolated legal fixture", Publisher = "Fixture", Version = "1.0",
    Payload = [new() { Path = "app.txt" }], LegalDocuments = Draft() };
var manifestPath = Path.Combine(area, "input.json");
await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
using var key = RSA.Create(2048);
var keyPath = Path.Combine(area, "signing.pem"); await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
var packagePath = Path.Combine(area, "signed.hinst");
var built = PackageBuilder.Build(manifestPath, area, packagePath, new() { SigningKeyPath = keyPath });
using (var package = PackageReader.Open(packagePath))
{
    package.VerifySignature(requireSignature: true); await package.VerifyAsync();
    Check(package.Manifest.LegalDocuments!.PrivacyPolicy!.Content == built.LegalDocuments!.PrivacyPolicy!.Content, "The actual signed package does not contain the policy text.");
    Check(package.Manifest.LegalDocuments.TermsAndConditions!.Content == built.LegalDocuments.TermsAndConditions!.Content, "The actual signed package does not contain the terms text.");
}
using (var zip = ZipFile.OpenRead(packagePath))
    Check(zip.Entries.Select(entry => entry.FullName).Order().SequenceEqual(new[] { "manifest.json", "payload/app.txt", "signature.json" }), "Legal metadata changed the archive entry allowlist.");
var rejectedPath = Path.Combine(area, "preserved.hinst"); await File.WriteAllTextAsync(rejectedPath, "Do not replace on validation failure.");
manifest.LegalDocuments = null;
await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
await Reject<InvalidDataException>(() => { PackageBuilder.Build(manifestPath, area, rejectedPath); return Task.CompletedTask; });
Check(await File.ReadAllTextAsync(rejectedPath) == "Do not replace on validation failure.", "Missing policy validation changed an existing output.");

// Construct an old-format package directly: the new builder deliberately cannot create policy-free packages.
var legacyPath = Path.Combine(area, "existing-legacy.hinst");
manifest.Payload[0].Size = new FileInfo(Path.Combine(area, "app.txt")).Length;
manifest.Payload[0].Sha256 = PackageSafety.HashFile(Path.Combine(area, "app.txt"));
using (var zip = ZipFile.Open(legacyPath, ZipArchiveMode.Create))
{
    await using (var writer = zip.CreateEntry("manifest.json").Open())
        await JsonSerializer.SerializeAsync(writer, manifest, InstallerJson.Options);
    zip.CreateEntryFromFile(Path.Combine(area, "app.txt"), "payload/app.txt");
}
using (var legacy = PackageReader.Open(legacyPath))
{
    await legacy.VerifyAsync();
    Check(legacy.Manifest.LegalDocuments is null, "An existing package was given fabricated legal documents.");
}
await Reject<InvalidDataException>(() => { BundleBuilder.Build(Path.Combine(area, "app.txt"), legacyPath, Path.Combine(area, "new-legacy-bundle.exe")); return Task.CompletedTask; });

var requests = new List<FixtureRequest>();
string returnedId = id;
await using (var server = new FixtureServer(request =>
{
    requests.Add(request);
    var response = new PublisherLegalDocuments { ProductId = returnedId, LegalDocuments = documents,
        PrivacyPolicyUrl = documents.PrivacyPolicy.Url, TermsAndConditionsUrl = documents.TermsAndConditions.Url, Status = "draft" };
    return JsonSerializer.SerializeToUtf8Bytes(response, InstallerJson.Options);
}))
using (var publisher = new PublisherClient(server.Origin, bearerToken: "legal-fixture-only", allowLoopback: true))
{
    var saved = await publisher.GetLegalDocumentsAsync(id);
    Check(saved.ProductId == id && saved.LegalDocuments!.PrivacyPolicy!.Content == documents.PrivacyPolicy.Content, "Saved authoring content was not returned.");
    await publisher.SaveLegalDocumentsAsync(id, Draft());
    Check(requests.Count == 2 && requests[0].Method == "GET" && requests[1].Method == "PUT"
        && requests.All(request => request.Path == "/api/installer/publisher/apps/" + id + "/legal"), "Publisher legal endpoint differs from the agreed contract.");
    Check(requests.All(request => request.Headers.GetValueOrDefault("Authorization") == "Bearer legal-fixture-only"), "Normal publisher authentication was not used.");
    Check(requests[1].Headers.GetValueOrDefault("X-HeirowInstaller-SHA256") == Convert.ToHexString(SHA256.HashData(requests[1].Body)).ToLowerInvariant(), "PUT digest does not bind its exact JSON body.");
    using (var body = JsonDocument.Parse(requests[1].Body)) Check(body.RootElement.GetProperty("productId").GetString() == id && body.RootElement.GetProperty("legalDocuments").GetProperty("termsAndConditions").GetProperty("content").GetString() == documents.TermsAndConditions.Content, "Typed text is absent from the saved draft.");
    returnedId = "another.product";
    await Reject<InvalidDataException>(() => publisher.GetLegalDocumentsAsync(id));
    var before = requests.Count;
    await Reject<InvalidDataException>(() => publisher.PublishAsync(legacyPath));
    Check(requests.Count == before, "A missing-policy package reached the upload endpoint.");
}
Console.WriteLine($"Passed {checks} legal document assertions. No user settings, installed apps, services or real account sessions changed. Fixture: {area}");

sealed record FixtureRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
sealed class FixtureServer : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource cancellation = new();
    readonly Task loop;
    public Uri Origin { get; }
    public FixtureServer(Func<FixtureRequest, byte[]> response)
    {
        listener.Start(); Origin = new("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
        loop = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    await using var stream = client.GetStream();
                    using var header = new MemoryStream(); var single = new byte[1];
                    while (header.Length < 16384)
                    {
                        await stream.ReadExactlyAsync(single, cancellation.Token); header.WriteByte(single[0]);
                        if (header.Length >= 4 && Encoding.ASCII.GetString(header.GetBuffer(), (int)header.Length - 4, 4) == "\r\n\r\n") break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                    var first = lines[0].Split(' '); var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines.Skip(1).Where(line => line.Contains(':'))) { int colon = line.IndexOf(':'); headers.Add(line[..colon], line[(colon + 1)..].Trim()); }
                    int length = int.Parse(headers.GetValueOrDefault("Content-Length", "0"));
                    if (length is < 0 or > 1024 * 1024) throw new InvalidDataException("Fixture request too large.");
                    var body = new byte[length]; await stream.ReadExactlyAsync(body, cancellation.Token);
                    var bytes = response(new(first[0], first[1], headers, body));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), cancellation.Token);
                    await stream.WriteAsync(bytes, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
    }
    public async ValueTask DisposeAsync() { cancellation.Cancel(); listener.Stop(); await loop; cancellation.Dispose(); }
}
