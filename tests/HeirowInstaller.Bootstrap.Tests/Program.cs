using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Bootstrap;
using HeirowInstaller.BootstrapContract;

var root = Path.GetFullPath(args.FirstOrDefault() ?? throw new ArgumentException("Supply a task-owned test output directory."));
Directory.CreateDirectory(root);
using var key = RSA.Create(2048);
var pin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch (Exception error) when (error is InvalidDataException or CryptographicException or IOException or HttpRequestException or PlatformNotSupportedException) { Check(true, message); return; }
    throw new InvalidOperationException("Expected rejection: " + message);
}
byte[] Package(string id, Action<ZipArchive>? tamper = null)
{
    var file = Encoding.UTF8.GetBytes("owned runtime bytes for " + id);
    var manifest = new BootstrapManifest { FormatVersion = 4, Id = id, Version = "1.0.0", TargetRuntime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
        Payload = [new() { Path = "host.exe", Size = file.Length, Sha256 = Convert.ToHexString(SHA256.HashData(file)) }] };
    var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BootstrapJson.Default.BootstrapManifest);
    var signature = new BootstrapSignature { Algorithm = "RSA-PSS-SHA256", PublicKey = key.ExportSubjectPublicKeyInfoPem(), Signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
    {
        void Entry(string name, byte[] content) { using var entry = zip.CreateEntry(name).Open(); entry.Write(content); }
        Entry("manifest.json", bytes); Entry("signature.json", JsonSerializer.SerializeToUtf8Bytes(signature, BootstrapJson.Default.BootstrapSignature)); Entry("payload/host.exe", file); tamper?.Invoke(zip);
    }
    return output.ToArray();
}
BootstrapDescriptor Descriptor(byte[] bytes, string id = "fixture.host") => new()
{
    ProductId = "fixture.app", Name = "Fixture", Version = "1.0.0", PublisherKeySha256 = pin, HostPackageId = id, HostEntryPoint = "host.exe",
    Packages = [new() { Id = id, Version = "1.0.0", TargetRuntime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
        Url = "https://packages.example.test/host.hinst", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) }]
};
var payload = Package("fixture.host");
var descriptor = Descriptor(payload);
var handler = new FixtureHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
using var client = new HttpClient(handler);
var reports = new List<BootstrapProgress>();
var engine = new BootstrapEngine(descriptor, client, new ImmediateProgress(reports));
var shared = Path.Combine(root, "shared");
var start = await engine.PrepareAsync(shared, Path.Combine(root, "first"), default);
Check(File.ReadAllText(start.FileName) == "owned runtime bytes for fixture.host", "signed runtime is verified and extracted before launch");
Check(start.ArgumentList.SequenceEqual(new[] { "--scope", "user" }), "per-user scope reaches the installer without requiring an administrator launcher");
Check(reports.Any(x => x.Phase == "Downloading") && reports.Any(x => x.Phase == "Preparing shared files"), "download and extraction report bounded byte progress");
var calls = handler.Calls;
await engine.PrepareAsync(shared, Path.Combine(root, "cached"), default);
Check(handler.Calls == calls, "second setup reuses verified shared bytes without a network download");
File.WriteAllText(start.FileName, "modified bytes");
await Reject(async () => await engine.PrepareAsync(shared, Path.Combine(root, "tampered-cache"), default), "a changed immutable runtime is rejected and preserved");
Check(File.ReadAllText(start.FileName) == "modified bytes", "failed cache verification does not overwrite existing files");
foreach (var name in new[] { "../escape.exe", "payload/HOST.exe", "payload/undeclared.exe", "C:/outside.exe", "payload/link/../host.exe" })
{
    var hostile = Package("fixture.host", zip => { using var entry = zip.CreateEntry(name).Open(); entry.WriteByte(1); });
    var binding = Descriptor(hostile);
    using var badClient = new HttpClient(new FixtureHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(hostile) }));
    var badEngine = new BootstrapEngine(binding, badClient, new ImmediateProgress([]));
    var caseRoot = Path.Combine(root, "bad-" + checks);
    await Reject(async () => await badEngine.PrepareAsync(Path.Combine(caseRoot, "shared"), Path.Combine(caseRoot, "stage"), default), "hostile archive rejected: " + name);
}
var wrongPin = Descriptor(payload); wrongPin.PublisherKeySha256 = new string('1', 64);
await Reject(async () => await new BootstrapEngine(wrongPin, client, new ImmediateProgress([])).PrepareAsync(Path.Combine(root, "wrong-pin"), Path.Combine(root, "wrong-pin-stage"), default), "pinned publisher mismatch is rejected");
var wrongHash = Descriptor(payload); wrongHash.Packages[0].Sha256 = new string('0', 64);
await Reject(async () => await new BootstrapEngine(wrongHash, client, new ImmediateProgress([])).PrepareAsync(Path.Combine(root, "wrong-hash"), Path.Combine(root, "wrong-hash-stage"), default), "archive hash mismatch is rejected before extraction");
using (var redirected = new HttpClient(new FixtureHandler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://foreign.example.test/host.hinst"); return response; })))
    await Reject(async () => await new BootstrapEngine(descriptor, redirected, new ImmediateProgress([])).PrepareAsync(Path.Combine(root, "redirect"), Path.Combine(root, "redirect-stage"), default), "undeclared redirect origin receives no follow-up request");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    try { await engine.PrepareAsync(Path.Combine(root, "cancel"), Path.Combine(root, "cancel-stage"), cancel.Token); throw new InvalidOperationException("Cancellation ignored."); }
    catch (OperationCanceledException) { Check(true, "cancellation stops before runtime acquisition or launch"); }
}
var fixtureDescriptor = Path.Combine(root, "fixture-bootstrap.json");
File.WriteAllBytes(fixtureDescriptor, JsonSerializer.SerializeToUtf8Bytes(descriptor, BootstrapJson.Default.BootstrapDescriptor));
// A fixed PE fixture exercises binding/signing without executing arbitrary native code.
var native = new byte[512]; native[0] = (byte)'M'; native[1] = (byte)'Z';
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(native.AsSpan(60), 64);
"PE\0\0"u8.CopyTo(native.AsSpan(64));
System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(64 + 20), 240);
System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(64 + 24), 0x20b);
var generic = Path.Combine(root, "generic-fixture.exe"); await File.WriteAllBytesAsync(generic, native);
var bound = Path.Combine(root, "bound-fixture.exe");
await WebBootstrapBindings.WriteAsync(generic, await File.ReadAllBytesAsync(fixtureDescriptor), bound, key);
var signedBinding = WebBootstrapBindings.Read(bound, pin);
var originalDescriptorBytes = await File.ReadAllBytesAsync(fixtureDescriptor);
Check(signedBinding.LauncherLength == native.Length && signedBinding.DescriptorBytes.SequenceEqual(originalDescriptorBytes), "signed footer preserves the original descriptor and fixed launcher prefix");
Check(WebBootstrapBindings.LauncherSha256(generic) == WebBootstrapBindings.LauncherSha256(bound, signedBinding.LauncherLength), "per-product descriptors retain one approved generic launcher identity");
await Reject(() => { WebBootstrapBindings.Read(bound, new string('2',64)); return Task.CompletedTask; }, "product footer rejects a substituted publisher pin");
var signed = (await File.ReadAllBytesAsync(bound)).ToList();
while (signed.Count % 8 != 0) signed.Add(0);
var certOffset = signed.Count; signed.AddRange(new byte[8]);
var withCertificate = signed.ToArray();
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(withCertificate.AsSpan(64+24+64), 123456);
System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(withCertificate.AsSpan(64+24+112+32), (uint)certOffset);
System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(withCertificate.AsSpan(64+24+112+36), 8);
var authenticodeShape = Path.Combine(root,"certificate-overlay-fixture.exe"); await File.WriteAllBytesAsync(authenticodeShape,withCertificate);
var afterWindowsSigning = WebBootstrapBindings.Read(authenticodeShape,pin);
Check(afterWindowsSigning.DescriptorBytes.SequenceEqual(signedBinding.DescriptorBytes) && WebBootstrapBindings.LauncherSha256(authenticodeShape,afterWindowsSigning.LauncherLength)==WebBootstrapBindings.LauncherSha256(generic), "certificate overlay and alignment preserve product binding and normalized launcher identity");
Console.WriteLine($"PASS {checks} bootstrap checks; fixture binding: {fixtureDescriptor}");

sealed class ImmediateProgress(List<BootstrapProgress> values) : IProgress<BootstrapProgress> { public void Report(BootstrapProgress value) => values.Add(value); }
sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(response(request)); }
}
