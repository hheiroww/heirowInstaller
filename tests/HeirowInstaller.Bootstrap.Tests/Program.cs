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
var applicationRuntime = Package("fixture.application-runtime");
var runtimeDescriptor = Descriptor(applicationRuntime, "fixture.application-runtime");
using (var populate = new HttpClient(new FixtureHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(applicationRuntime) })))
    await new BootstrapEngine(runtimeDescriptor, populate, new ImmediateProgress([])).PrepareAsync(shared, Path.Combine(root, "application-runtime-stage"), default);
var applicationRoot = Path.Combine(root, "local-application"); Directory.CreateDirectory(applicationRoot);
await File.WriteAllTextAsync(Path.Combine(applicationRoot, "app.App.exe"), "exact local child image");
await File.WriteAllTextAsync(Path.Combine(applicationRoot, "app.runtimeconfig.json"), "{}");
var applicationDescriptor = Descriptor(applicationRuntime, "fixture.application-runtime");
applicationDescriptor.HostKind = "application"; applicationDescriptor.HostPackageId = ""; applicationDescriptor.HostEntryPoint = "app.App.exe";
applicationDescriptor.DotNetPackageId = "fixture.application-runtime"; applicationDescriptor.RuntimeConfigPath = "app.runtimeconfig.json";
applicationDescriptor.ProbingPackageIds = ["fixture.application-runtime"];
applicationDescriptor.ApplicationFiles = new[] { "app.App.exe", "app.runtimeconfig.json" }.Select(relative =>
{
    var path = Path.Combine(applicationRoot, relative); return new BootstrapPayload { Path = relative, Size = new FileInfo(path).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
}).ToList();
var privateRuntime = Path.Combine(new BootstrapEngine(runtimeDescriptor, client, new ImmediateProgress([])).PackageRoot(shared, runtimeDescriptor.Packages[0]), "files");
await File.WriteAllTextAsync(Path.Combine(applicationRoot, "app.runtimeconfig.dev.json"), JsonSerializer.Serialize(new { runtimeOptions = new { additionalProbingPaths = new[] { privateRuntime } } }));
var noNetwork = new FixtureHandler(_ => throw new InvalidOperationException("An application launch cannot download dependencies."));
using (var offline = new HttpClient(noNetwork))
{
    var localEngine = new BootstrapEngine(applicationDescriptor, offline, new ImmediateProgress([]));
    var unused = Path.Combine(root, "application-must-not-stage");
    var localStart = await localEngine.PrepareAsync(shared, unused, default, applicationRoot);
    Check(localStart.FileName == Path.Combine(applicationRoot, "app.App.exe") && localStart.ArgumentList.Count == 0, "application role launches its exact local apphost without installer arguments");
    Check(localStart.Environment["DOTNET_ROOT"] == privateRuntime, "application role binds the child process to the pinned private runtime");
    Check(noNetwork.Calls == 0 && !Directory.Exists(unused), "offline application startup never downloads or creates staging");
    var selectedOffline = await localEngine.PrepareOfflineAsync(new[] { Path.Combine(root, "missing-first-scope"), shared }, unused, default, applicationRoot);
    Check(selectedOffline.FileName == localStart.FileName && noNetwork.Calls == 0 && !Directory.Exists(unused), "offline cache selection skips missing scope then verifies exact installed scope without HTTP or staging");
    await Reject(async () => await localEngine.PrepareAsync(Path.Combine(root, "missing-application-cache"), unused, default, applicationRoot), "missing application dependencies request setup repair without downloading");
    Check(noNetwork.Calls == 0, "missing offline dependencies never contact a provider");
    await File.WriteAllTextAsync(Path.Combine(applicationRoot, "app.runtimeconfig.dev.json"), "{\"runtimeOptions\":{\"additionalProbingPaths\":[\"C:/untrusted\"]}}");
    await Reject(async () => await localEngine.PrepareAsync(shared, unused, default, applicationRoot), "mutable probing metadata cannot select an unverified DLL directory");
}
var updaterDescriptor = Descriptor(applicationRuntime, "fixture.application-runtime"); updaterDescriptor.HostKind = "updater"; updaterDescriptor.OfflineOnly = false;
using (var offlineUpdaterClient = new HttpClient(noNetwork))
{
    var updaterEngine = new BootstrapEngine(updaterDescriptor, offlineUpdaterClient, new ImmediateProgress([]));
    var noUpdaterStage = Path.Combine(root, "updater-must-not-stage");
    await Reject(async () => await updaterEngine.PrepareAsync(Path.Combine(root, "missing-updater-scope"), noUpdaterStage, default), "updater role never provisions missing dependencies even when offline flag is false");
    var updaterStart = await updaterEngine.PrepareOfflineAsync(new[] { Path.Combine(root, "missing-updater-first"), shared }, noUpdaterStage, default);
    Check(updaterStart.FileName == Path.Combine(privateRuntime, "host.exe") && noNetwork.Calls == 0 && !Directory.Exists(noUpdaterStage), "updater selects verified installed cache without creating staging or downloading");
}
// These checks exercise byte and launch policy only. They request no UAC,
// write no protected machine directory, and do not claim credentialed elevation.
var promotedRoot = Path.Combine(root, "promoted-policy");
var protectedRuntime = Path.Combine(root, "protected-runtime-fixture");
var protectedPackages = new Dictionary<string, string> { ["fixture.application-runtime"] = protectedRuntime };
var openedSources = new List<string>();
FileStream OriginalReadOnly(string path)
{
    openedSources.Add(path); BootstrapEngine.NoLinks(path);
    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
}
var machineSharedFixture = Path.Combine(root, "machine-shared-fixture");
var scopeEngine = new BootstrapEngine(applicationDescriptor, client, new ImmediateProgress([]));
var fixedSourceRoots = new[] { shared, machineSharedFixture };
async Task SetSourceProbe(string path) => await File.WriteAllTextAsync(Path.Combine(applicationRoot, "app.runtimeconfig.dev.json"), JsonSerializer.Serialize(new { runtimeOptions = new { additionalProbingPaths = new[] { path } } }));
await SetSourceProbe(Path.Combine(scopeEngine.PackageRoot(machineSharedFixture, applicationDescriptor.Packages[0]), "files"));
var machineSources = await ApplicationPromotionPayloads.SourceCacheCandidatesAsync(applicationDescriptor, applicationRoot, fixedSourceRoots, scopeEngine.PackageRoot, OriginalReadOnly, default);
Check(applicationDescriptor.Scope == "user" && machineSources.SequenceEqual(new[] { machineSharedFixture }), "machine scope override selects exact fixed cache independently of authored user default");
applicationDescriptor.Scope = "machine"; await SetSourceProbe(privateRuntime);
var userSources = await ApplicationPromotionPayloads.SourceCacheCandidatesAsync(applicationDescriptor, applicationRoot, fixedSourceRoots, scopeEngine.PackageRoot, OriginalReadOnly, default);
Check(userSources.SequenceEqual(new[] { shared }), "user cache binding overrides authored machine default without trusting arbitrary paths");
applicationDescriptor.Scope = "user"; await SetSourceProbe("C:/untrusted-cache");
await Reject(async () => await ApplicationPromotionPayloads.SourceCacheCandidatesAsync(applicationDescriptor, applicationRoot, fixedSourceRoots, scopeEngine.PackageRoot, OriginalReadOnly, default), "application promotion rejects probing metadata outside both fixed scope caches");
openedSources.Clear();
await File.WriteAllTextAsync(Path.Combine(applicationRoot, "untrusted-extra.dll"), "never promote this file");
await ApplicationPromotionPayloads.StageApplicationAsync(applicationDescriptor, applicationRoot, promotedRoot, protectedPackages, OriginalReadOnly, new ImmediateProgress([]), default);
ApplicationPromotionPayloads.ValidateInventory(applicationDescriptor, promotedRoot);
Check(openedSources.Count == applicationDescriptor.ApplicationFiles.Count && openedSources.All(path => !path.EndsWith(".dev.json") && !path.EndsWith("untrusted-extra.dll")), "promotion reads only exact signed application sources through readonly handle provider");
Check(!File.Exists(Path.Combine(promotedRoot, "untrusted-extra.dll")) && File.ReadAllText(Path.Combine(promotedRoot, "app.App.exe")) == "exact local child image", "unsigned sibling DLLs are excluded from the promoted application");
using (var generated = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(promotedRoot, "app.runtimeconfig.dev.json"))))
    Check(generated.RootElement.GetProperty("runtimeOptions").GetProperty("additionalProbingPaths")[0].GetString() == protectedRuntime, "administrator probing metadata is regenerated from exact protected package roots");
await File.WriteAllTextAsync(Path.Combine(promotedRoot, "injected.dll"), "foreign protected-cache file");
await Reject(() => { ApplicationPromotionPayloads.ValidateInventory(applicationDescriptor, promotedRoot); return Task.CompletedTask; }, "protected application rejects undeclared files before privileged launch");
var tamperedSource = Path.Combine(root, "promotion-tampered-source"); Directory.CreateDirectory(tamperedSource);
await File.WriteAllTextAsync(Path.Combine(tamperedSource, "app.App.exe"), "changed local app image");
await File.WriteAllTextAsync(Path.Combine(tamperedSource, "app.runtimeconfig.json"), "{}");
await Reject(() => ApplicationPromotionPayloads.StageApplicationAsync(applicationDescriptor, tamperedSource, Path.Combine(root, "promotion-tampered-stage"), protectedPackages, OriginalReadOnly, new ImmediateProgress([]), default), "changed source bytes cannot complete application promotion");
Check(File.ReadAllText(Path.Combine(tamperedSource, "app.App.exe")) == "changed local app image", "failed promotion retains original user files");
var invalidApplication = JsonSerializer.Deserialize(JsonSerializer.Serialize(applicationDescriptor, BootstrapJson.Default.BootstrapDescriptor), BootstrapJson.Default.BootstrapDescriptor)!;
invalidApplication.ApplicationFiles.Add(new() { Path = "app.runtimeconfig.dev.json", Sha256 = new string('1', 64), Size = 1 });
await Reject(() => { BootstrapEngine.Validate(invalidApplication); return Task.CompletedTask; }, "signed payload cannot replace generated privileged probing metadata");
invalidApplication.ApplicationFiles.RemoveAt(invalidApplication.ApplicationFiles.Count - 1); invalidApplication.ApplicationFiles[0].Size = long.MaxValue;
await Reject(() => { BootstrapEngine.Validate(invalidApplication); return Task.CompletedTask; }, "application promotion bounds total signed file sizes before staging");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await ApplicationPromotionPayloads.StageApplicationAsync(applicationDescriptor, applicationRoot, Path.Combine(root, "promotion-cancelled"), protectedPackages, OriginalReadOnly, new ImmediateProgress([]), cancelled.Token); throw new InvalidOperationException("Promotion cancellation ignored."); }
    catch (OperationCanceledException) { Check(true, "cancelled application promotion cannot publish or launch a directory"); }
}
string nonce = new('a', 64), digest = new('b', 64); string[] originalArguments = ["--open", "C:/original user/document.txt", "unicode-\u00e9"];
using var handoff = new MemoryStream(); await ApplicationPromotionPayloads.WriteHandoffAsync(handoff, nonce, digest, originalArguments, default); handoff.Position = 0;
Check((await ApplicationPromotionPayloads.ReadHandoffAsync(handoff, nonce, digest, default)).SequenceEqual(originalArguments), "bounded parent handoff preserves original application arguments");
handoff.Position = 0; await Reject(async () => await ApplicationPromotionPayloads.ReadHandoffAsync(handoff, new string('c', 64), digest, default), "application handoff rejects a substituted nonce");
handoff.Position = 0; await Reject(async () => await ApplicationPromotionPayloads.ReadHandoffAsync(handoff, nonce, new string('c', 64), default), "application handoff rejects another signed descriptor");
byte[] oversized = handoff.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(64), int.MaxValue);
await Reject(async () => await ApplicationPromotionPayloads.ReadHandoffAsync(new MemoryStream(oversized), nonce, digest, default), "application handoff rejects oversized argument count before allocation");
byte[] oversizedArgument = handoff.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(oversizedArgument.AsSpan(68), int.MaxValue);
await Reject(async () => await ApplicationPromotionPayloads.ReadHandoffAsync(new MemoryStream(oversizedArgument), nonce, digest, default), "application handoff rejects oversized argument frame before allocation");
if (OperatingSystem.IsWindows())
{
    var cleanStart = new System.Diagnostics.ProcessStartInfo { WorkingDirectory = promotedRoot };
    foreach (string variable in new[] { "DOTNET_STARTUP_HOOKS", "COREHOST_TRACEFILE", "COMPlus_AltJit", "COR_ENABLE_PROFILING", "PATH", "SOCKETJACK_SESSION_TOKEN" }) cleanStart.Environment[variable] = "untrusted-parent-setting";
    cleanStart.Environment["DOTNET_ROOT"] = protectedRuntime;
    string originalProfile = Path.Combine(root, "original-profile"), originalRoaming = Path.Combine(root, "original-roaming"), originalLocal = Path.Combine(root, "original-local");
    ApplicationPromotionPayloads.ConfigureEnvironment(cleanStart, Path.Combine(root, "protected-temp"), originalProfile, originalRoaming, originalLocal, applicationRoot);
    Check(new[] { "DOTNET_STARTUP_HOOKS", "COREHOST_TRACEFILE", "COMPlus_AltJit", "COR_ENABLE_PROFILING", "SOCKETJACK_SESSION_TOKEN" }.All(variable => !cleanStart.Environment.ContainsKey(variable)), "privileged application launch strips runtime injection and session environment");
    Check(cleanStart.Environment["DOTNET_ROOT"] == protectedRuntime && cleanStart.Environment["PATH"]!.StartsWith(Environment.SystemDirectory + ";"), "privileged launch uses pinned runtime and System32 search path");
    Check(cleanStart.Environment["USERPROFILE"] == originalProfile && cleanStart.Environment["APPDATA"] == originalRoaming && cleanStart.Environment["LOCALAPPDATA"] == originalLocal
        && cleanStart.Environment["APP_INSTALL_ROOT"] == promotedRoot && cleanStart.Environment["SOCKETJACK_ORIGINAL_INSTALL_ROOT"] == applicationRoot, "protected resources and working directory retain original user's private data paths separately");
}
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
await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { passed = true, checks, realUacTested = false, credentialedElevationTested = false, protectedMachineWrites = false, retained = true }));

sealed class ImmediateProgress(List<BootstrapProgress> values) : IProgress<BootstrapProgress> { public void Report(BootstrapProgress value) => values.Add(value); }
sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(response(request)); }
}
