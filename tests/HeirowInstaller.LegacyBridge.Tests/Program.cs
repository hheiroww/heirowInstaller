using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

if (!OperatingSystem.IsWindows()) { Console.WriteLine("Legacy native-updater bridge acceptance requires Windows."); return; }
if (args.Length is not (4 or 5) || args.Length == 5 && args[4] != "--execute-self-contained") throw new ArgumentException("Supply legacy Core DLL, bridge updater EXE, exact legacy commit and one new owned output directory; optionally --execute-self-contained.");
var legacyPath = Path.GetFullPath(args[0]); var native = Path.GetFullPath(args[1]); var commit = args[2]; var root = Path.GetFullPath(args[3]);
var execute = args.Length == 5; int ownerPid = 0; long ownerStart = 0;
if (Directory.Exists(root)) throw new IOException("A bridge fixture requires a fresh owned output directory.");
Directory.CreateDirectory(root); int assertions = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); assertions++; Console.WriteLine("PASS " + message); }
using var key = RSA.Create(2048); var keyPath = Path.Combine(root, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
var pin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
var source = Path.Combine(root, "source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "app.exe"), "bridge application");
File.Copy(native, Path.Combine(source, "heirowUpdater.exe"));
PackageManifest App(string version) => new() { Id = "fixture.legacy-bridge", Name = "Legacy bridge acceptance", Publisher = "Fixture", Version = version,
    TargetRuntime = InstallerPlatform.RuntimeIdentifier, TargetPlatform = InstallerPlatform.Current, EntryPoint = "app.exe", UpdateUrl = "https://bridge.invalid/feed.json",
    Features = [new() { Id = "updates", Default = true }], LegalDocuments = LegalFixture.For("fixture.legacy-bridge"),
    UpdateCompanion = new() { RuntimeMode = "shared", UpdateMode = "files", UpdateProtocol = "hfiles1", CredentialOrigin = "https://bridge.invalid/" },
    Payload = [new() { Path = "app.exe" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }] };
string Build(PackageManifest manifest, string directory, string name)
{
    var json = Path.Combine(root, name + ".json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
    var archive = Path.Combine(root, name + ".hinst"); PackageBuilder.Build(json, directory, archive, new() { SigningKeyPath = keyPath }); return archive;
}
var bridgePath = Build(App("2"), source, "bridge");
using (var bridge = PackageReader.Open(bridgePath)) Check(bridge.Manifest.FormatVersion == 4, "Physical updater bridge uses old-reader-compatible format 4");
var load = new AssemblyLoadContext("exact-legacy-installer-core", isCollectible: true); var assembly = load.LoadFromAssemblyPath(legacyPath);
var reader = assembly.GetType("HeirowInstaller.Core.PackageReader", true)!;
dynamic oldPackage = reader.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [bridgePath])!;
var optionsType = assembly.GetType("HeirowInstaller.Core.InstallOptions", true)!;
dynamic oldOptions = Activator.CreateInstance(optionsType)!;
var state = Path.Combine(root, "state"); var installedRoot = Path.Combine(root, "installed");
oldOptions.StateRoot = state; oldOptions.TargetDirectory = installedRoot; oldOptions.Isolated = true; oldOptions.RegisterProduct = false;
oldOptions.TrustedPublisherKeySha256 = pin; oldOptions.RequireSignature = true;
try
{
    oldPackage.VerifySignature(pin, true);
    await (Task)oldPackage.VerifyAsync(null, CancellationToken.None);
    Check((int)oldPackage.Manifest.FormatVersion == 4 && oldPackage.Manifest.UpdateCompanion.GetType().GetProperty("UpdateProtocol") is null,
        "Exact legacy reader verifies original signed bytes while ignoring its unknown additive protocol field");
    var wrongPinRejected = false;
    try { oldPackage.VerifySignature(new string('0', 64), true); } catch (CryptographicException) { wrongPinRejected = true; }
    Check(wrongPinRejected, "Exact legacy reader still enforces the original publisher pin");
    dynamic oldEngine = Activator.CreateInstance(assembly.GetType("HeirowInstaller.Core.InstallerEngine", true)!, [null])!;
    await (Task)oldEngine.InstallAsync(oldPackage, oldOptions, null, CancellationToken.None);
}
finally { ((IDisposable)oldPackage).Dispose(); }
var product = InstallerEngine.ListInstalled(state).Single();
var profilePath = Path.Combine(installedRoot, "heirowUpdater.json");
using (var raw = JsonDocument.Parse(File.ReadAllBytes(profilePath))) Check(!raw.RootElement.TryGetProperty("updateProtocol", out _), "Actual legacy installer generated a profile without the new capability field");
var profile = ApplicationUpdater.ReadConfiguration(profilePath);
Check(profile.UpdateProtocol == "hfiles1", "New updater rehydrates the missing capability only from the pinned signed bridge package");
var updaterPath = ApplicationUpdater.ResolveExecutable(profile);
Check(PackageSafety.HashFile(updaterPath) == PackageSafety.HashFile(native) && !File.Exists(Path.Combine(installedRoot, "heirowUpdater.exe")),
    "Actual legacy engine installed exact native updater bytes in its old shared runtime slot");
Check(UpdaterTarget.FromInstalled(product, state).UpdateProtocol == "hfiles1", "Current SDK selects file selection from the signed installed bridge");
ProcessStartInfo ColdStart()
{
    var start = new ProcessStartInfo(updaterPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = installedRoot };
    foreach (var name in new[] { "HEIROWUPDATER_TOKEN", "HEIROWUPDATER_COOKIE", "SOCKETJACK_DOWNLOAD_TOKEN", "SOCKETJACK_DOWNLOAD_COOKIE", "SOCKETJACK_BOOTSTRAP_LAUNCHER", "SOCKETJACK_BOOTSTRAP_PID", "SOCKETJACK_BOOTSTRAP_START" }) start.Environment.Remove(name);
    start.Environment["DOTNET_ROOT"] = Path.Combine(root, "absent-global-runtime"); start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
    start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"; start.Environment["DOTNET_ROLL_FORWARD"] = "Disable";
    start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(root, "bundle-cache");
    return start;
}
if (execute)
{
    StandaloneRuntimeInspector.RequireEmbeddedRuntime(updaterPath);
    var start = ColdStart(); start.ArgumentList.Add("help");
    using var child = Process.Start(start)!; ownerPid = child.Id; ownerStart = child.StartTime.ToUniversalTime().Ticks;
    var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
    await File.WriteAllTextAsync(Path.Combine(root, "cold-start.stdout.log"), await stdout); await File.WriteAllTextAsync(Path.Combine(root, "cold-start.stderr.log"), await stderr);
    Check(child.ExitCode == 0 && (await File.ReadAllTextAsync(Path.Combine(root, "cold-start.stdout.log"))).Contains("heirowUpdater") &&
        !File.Exists(Path.ChangeExtension(updaterPath, ".runtimeconfig.json")),
        "Actual old-installed self-contained updater starts cold with global runtime lookup disabled and its inspected embedded runtime");
}
var dependencySource = Path.Combine(root, "dependency-source"); Directory.CreateDirectory(dependencySource); File.WriteAllText(Path.Combine(dependencySource, "runtime.bin"), "modern shared bytes");
var dependencyManifest = new PackageManifest { Id = "fixture.legacy-bridge.runtime", Name = "Runtime fixture", Publisher = "Fixture", Version = "1",
    TargetRuntime = InstallerPlatform.RuntimeIdentifier, TargetPlatform = InstallerPlatform.Current, Payload = [new() { Path = "runtime.bin" }], LegalDocuments = LegalFixture.For("fixture.legacy-bridge.runtime") };
var dependency = Build(dependencyManifest, dependencySource, "dependency");
var targetManifest = App("3"); targetManifest.SharedPackages = [new() { Id = dependencyManifest.Id, Version = "1", TargetRuntime = InstallerPlatform.RuntimeIdentifier,
    DownloadUrl = "https://bridge.invalid/dependency.hinst", Sha256 = PackageSafety.HashFile(dependency), Size = new FileInfo(dependency).Length, PublisherKeySha256 = pin }];
File.WriteAllText(Path.Combine(source, "app.exe"), "modern application"); var targetPath = Build(targetManifest, source, "modern");
var legacyRejectedModern = false;
try { using var unexpected = (IDisposable)reader.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [targetPath])!; }
catch (TargetInvocationException rejected) when (rejected.InnerException is InvalidDataException) { legacyRejectedModern = true; }
Check(legacyRejectedModern, "Exact old reader rejects format 5, proving direct publication would strand it");
using var modern = PackageReader.Open(targetPath); Check(modern.Manifest.FormatVersion == 5, "Shared-dependency target retains format-5 disk semantics");
using var transport = new BridgeTransport(targetPath, dependency);
using var client = new FileUpdateClient(transport: transport);
var feed = new UpdateFeed { ProductId = product.Id, Version = "3", PackageType = "hfiles", ManifestUrl = "https://bridge.invalid/3/manifest.json",
    FilesUrl = "https://bridge.invalid/3/files.zip", TargetManifestSha256 = transport.Digest };
var options = new InstallOptions { StateRoot = state, TargetDirectory = installedRoot, Isolated = true, RegisterProduct = false, SharedPackageTransport = transport };
using var prepared = await client.PrepareAsync(feed, product, Path.Combine(root, "stages"), options);
Check(prepared.RequestedPaths.SequenceEqual(["app.exe"]), "Bridge-to-modern diff requests only changed application bytes");
transport.Online = false;
if (execute)
{
    var stageRoot = prepared.Root; prepared.Dispose();
    var lifecycle = ApplicationUpdater.StateDirectory(profile); var handoff = Path.Combine(lifecycle, "handoffs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(handoff);
    var start = ColdStart();
    foreach (var value in new[] { "apply-files", stageRoot, "--product-id", product.Id, "--expected-version", "3", "--target-manifest-sha256", transport.Digest,
        "--target", installedRoot, "--scope", "user", "--state-root", state, "--trust-key", pin, "--wait-pid", ownerPid.ToString(), "--wait-start", ownerStart.ToString(),
        "--lifecycle-config", profilePath, "--lifecycle-state", Path.Combine(lifecycle, "state.json"), "--lifecycle-ready", Path.Combine(handoff, "ready.json"),
        "--status-file", Path.Combine(handoff, "status.json"), "--background", "--isolated", "--no-prompt" }) start.ArgumentList.Add(value);
    UpdaterOwnerHandshake.Attach(start, ownerPid, ownerStart, new(product.Id, installedRoot, state, "user", transport.Digest, stageRoot));
    using var child = Process.Start(start)!; var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); var diagnostic = await stderr;
    await File.WriteAllTextAsync(Path.Combine(root, "apply.stdout.log"), await stdout); await File.WriteAllTextAsync(Path.Combine(root, "apply.stderr.log"), diagnostic);
    Check(child.ExitCode == 0, "Actual old-installed self-contained updater applies signed hfiles5 using its own runtime: " + diagnostic);
    await UpdaterOwnerHandshake.WaitForAcceptanceAsync(start);
}
else await new InstallerEngine().ApplyPreparedFileUpdateAsync(prepared, options);
var result = InstallerEngine.ListInstalled(state).Single();
Check(result.Version == "3" && result.PackagePath.EndsWith(".hmeta") && File.ReadAllText(Path.Combine(installedRoot, "app.exe")) == "modern application",
    "Legacy-installed bridge transitions into a signed format-5 metadata-only file update");
Check(ApplicationUpdater.ReadConfiguration(profilePath).UpdateProtocol == "hfiles1" && transport.RequestsAfterOffline == 0,
    "Modern shared dependency activates from staged bytes without legacy-session HTTP");
await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { passed = true, assertions, legacyCommit = commit,
    legacyCoreSha256 = PackageSafety.HashFile(legacyPath), bridgeUpdaterSha256 = PackageSafety.HashFile(native), bridgeSha256 = PackageSafety.HashFile(bridgePath), selfContainedUpdaterExecuted = execute,
    realUacTested = false, isolated = true, retained = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {assertions} exact legacy bridge checks. Retained outputs: {root}");

sealed class BridgeTransport : IFileUpdateTransport, IDisposable
{
    private readonly ZipArchive zip;
    private readonly byte[] dependency;
    public string Digest { get; }
    public bool Online { get; set; } = true;
    public int RequestsAfterOffline { get; private set; }
    public BridgeTransport(string target, string dependency) { zip = ZipFile.OpenRead(target); this.dependency = File.ReadAllBytes(dependency); Digest = Convert.ToHexString(SHA256.HashData(Read("manifest.json"))).ToLowerInvariant(); }
    private byte[] Read(string name) { using var input = zip.GetEntry(name)!.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
    private void CheckOnline() { if (!Online) { RequestsAfterOffline++; throw new HttpRequestException("Original fixture session ended."); } }
    public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        CheckOnline(); var body = uri.AbsolutePath == "/dependency.hinst" ? dependency : JsonSerializer.SerializeToUtf8Bytes(new { manifestBase64 = Convert.ToBase64String(Read("manifest.json")), signatureBase64 = Convert.ToBase64String(Read("signature.json")) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new(HttpMethod.Get, uri), Content = new ByteArrayContent(body) });
    }
    public Task<HttpResponseMessage> PostAsync(Uri uri, long maximumResponseBytes, ReadOnlyMemory<byte> json, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        CheckOnline(); using var request = JsonDocument.Parse(json); using var output = new MemoryStream();
        using (var selected = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var name in new[] { "manifest.json", "signature.json" }.Concat(request.RootElement.GetProperty("paths").EnumerateArray().Select(path => "payload/" + path.GetString())))
            { using var entry = selected.CreateEntry(name).Open(); entry.Write(Read(name)); }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) });
    }
    public void Dispose() => zip.Dispose();
}
