using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

if (args is not ["--root", var requestedRoot]) throw new ArgumentException("Supply an owned --root for retained test outputs.");
var root = Path.GetFullPath(requestedRoot);
if (Directory.Exists(root)) throw new IOException("Use a new owned fixture directory; prior test outputs are preserved.");
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
async Task Reject<T>(Func<Task> action, string message) where T : Exception
{ try { await action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name + ": " + message); }
using var rsa = RSA.Create(2048);
var key = Path.Combine(root, "fixture-signing.pem");
await File.WriteAllTextAsync(key, rsa.ExportRSAPrivateKeyPem());
var pin = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
var transport = new FixtureTransport();
var build = 0;
string Package(PackageManifest manifest, string content, string? signingKey = null, byte[]? binary = null)
{
    var area = Path.Combine(root, "package-" + (++build)); Directory.CreateDirectory(area);
    var payload = Path.Combine(area, "payload"); Directory.CreateDirectory(payload);
    var payloadFile = Path.Combine(payload, manifest.EntryPoint.Length == 0 ? manifest.Payload.FirstOrDefault()?.Path ?? "dependency.dll" : manifest.EntryPoint);
    if (binary is null) File.WriteAllText(payloadFile, content); else File.WriteAllBytes(payloadFile, binary);
    foreach (var file in manifest.Payload)
    {
        var path = Path.Combine(payload, file.Source.Length > 0 ? file.Source : file.Path);
        if (File.Exists(path)) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.Path.EndsWith(".json", StringComparison.Ordinal) ? "{}" : "synthetic local apphost");
    }
    manifest.LegalDocuments = LegalFixture.For(manifest.Id);
    var definition = Path.Combine(area, "manifest.json");
    File.WriteAllText(definition, JsonSerializer.Serialize(manifest, InstallerJson.Options));
    var output = Path.Combine(area, "package.hinst");
    PackageBuilder.Build(definition, payload, output, new() { SigningKeyPath = signingKey ?? key });
    return output;
}
SharedPackageDefinition Dependency(string version, string content)
{
    var path = Package(new() { Id = "fixture.runtime", Name = "Runtime fixture", Publisher = "Fixture", Version = version,
        TargetRuntime = InstallerPlatform.RuntimeIdentifier, TargetPlatform = InstallerPlatform.Current }, content);
    var url = "https://packages.invalid/runtime/" + version + ".hinst";
    transport.Content[url] = File.ReadAllBytes(path);
    return new() { Id = "fixture.runtime", Version = version, TargetRuntime = InstallerPlatform.RuntimeIdentifier,
        DownloadUrl = url, Sha256 = PackageSafety.HashFile(path), Size = new FileInfo(path).Length, PublisherKeySha256 = pin };
}
PackageManifest App(string id, string version, SharedPackageDefinition dependency) => new()
{
    Id = id, Name = id, Publisher = "Fixture", Version = version, EntryPoint = OperatingSystem.IsWindows() ? "app.exe" : "app",
    TargetRuntime = InstallerPlatform.RuntimeIdentifier, TargetPlatform = InstallerPlatform.Current,
    UpdateUrl = "https://packages.invalid/" + id + "/update.json", SharedPackages = [dependency]
};
var state = Path.Combine(root, "state");
InstallOptions Options(string id) => new() { Isolated = true, StateRoot = state, TargetDirectory = Path.Combine(root, id),
    RegisterProduct = false, RequireSignature = true, TrustedPublisherKeySha256 = pin, SharedPackageTransport = transport };
SharedPackageBinding Binding(string id) => JsonSerializer.Deserialize<SharedPackageBinding>(File.ReadAllText(Path.Combine(root, id, SharedPackages.BindingFileName)), InstallerJson.Options)!;
var engine = new InstallerEngine();
var dependency1 = Dependency("1.0.0", "same shared runtime bytes");
var app1 = Package(App("fixture.one", "1.0.0", dependency1), "first app");
using (var package = PackageReader.Open(app1))
{
    Check(package.Manifest.FormatVersion == 5, "Shared package authoring promotes to format 5.");
    var phases = new List<string>();
    await engine.InstallAsync(package, Options("fixture.one"), new InlineProgress(value => phases.Add(value.Phase)));
    Check(phases.IndexOf("dependencies") >= 0 && phases.IndexOf("dependencies") < phases.IndexOf("install"), "Shared downloads complete before application replacement.");
}
var app2 = Package(App("fixture.two", "1.0.0", dependency1), "second app");
using (var package = PackageReader.Open(app2)) await engine.InstallAsync(package, Options("fixture.two"));
var one = Binding("fixture.one"); var two = Binding("fixture.two");
Check(transport.Requests == 1, "Identical dependencies download only once across two installed apps.");
Check(one.Packages[0].Directory == two.Packages[0].Directory, "Applications share exact immutable dependency files.");
var sharedRoot = SharedPackages.Root("user", state, isolated: true);
Check(Directory.GetFiles(Path.Combine(sharedRoot, "references")).Length == 2, "Each app has a distinct shared-package owner reference.");
Check(File.ReadAllText(Path.Combine(one.Packages[0].Directory, "dependency.dll")) == "same shared runtime bytes", "Shared archive contents were extracted and verified.");
Check(one.ManifestSha256.Length == 64 && one.PublisherKeySha256 == pin, "Bindings retain signed manifest and publisher identity.");
using (var mutated = PackageReader.Open(app2))
{
    mutated.Manifest.SharedPackages!.Clear();
    await Reject<CryptographicException>(() => engine.InstallAsync(mutated, Options("fixture.two")), "Changing the in-memory manifest cannot remove signed shared dependencies.");
}
await Reject<CryptographicException>(() =>
{
    var area = Path.GetDirectoryName(app1)!;
    PackageBuilder.Build(Path.Combine(area, "manifest.json"), Path.Combine(area, "payload"), Path.Combine(root, "unsigned-shared.hinst"));
    return Task.CompletedTask;
}, "Unsigned parents cannot declare shared dependencies.");

var dependency2 = Dependency("2.0.0", "new side-by-side runtime");
var upgrade = Package(App("fixture.one", "2.0.0", dependency2), "updated app");
using (var package = PackageReader.Open(upgrade)) await engine.InstallAsync(package, Options("fixture.one"));
var upgraded = Binding("fixture.one");
Check(upgraded.Packages[0].Directory != one.Packages[0].Directory && Directory.Exists(one.Packages[0].Directory), "A new dependency never overwrites an older shared version.");
Check(Binding("fixture.two").Packages[0].Version == "1.0.0", "Updating one app preserves the other app's pinned runtime.");
Check(transport.Requests == 2, "Only the new dependency is downloaded on upgrade.");

var failing = App("fixture.one", "3.0.0", dependency1);
failing.Modules["fixture.failure"] = JsonSerializer.SerializeToElement(new { });
var failedPackage = Package(failing, "must roll back");
var beforeBinding = File.ReadAllBytes(Path.Combine(root, "fixture.one", SharedPackages.BindingFileName));
using (var package = PackageReader.Open(failedPackage))
    await Reject<InvalidOperationException>(() => new InstallerEngine([new FailureModule()]).InstallAsync(package, Options("fixture.one")), "Module failure must roll back shared references.");
Check(File.ReadAllBytes(Path.Combine(root, "fixture.one", SharedPackages.BindingFileName)).SequenceEqual(beforeBinding), "Failed activation restores the prior application binding.");
Check(File.ReadAllText(Path.Combine(root, "fixture.one", OperatingSystem.IsWindows() ? "app.exe" : "app")) == "updated app", "Failed installation restores prior application bytes.");
Check(Directory.GetFiles(Path.Combine(sharedRoot, "references")).Any(path => File.ReadAllBytes(path).SequenceEqual(beforeBinding)), "Failed activation restores the scope-wide dependency reference.");

await engine.UninstallAsync("fixture.one", Options("fixture.one"));
Check(Directory.GetFiles(Path.Combine(sharedRoot, "references")).Length == 1, "Uninstall removes only its own dependency reference.");
Check(Directory.Exists(one.Packages[0].Directory) && Directory.Exists(upgraded.Packages[0].Directory), "Uninstall retains shared files and unreferenced versions for approved cleanup.");

if (OperatingSystem.IsWindows())
{
    // The versioned fixture apphost exercises signed source binding only. Actual standalone NativeAOT launch has separate acceptance.
    var nativeSource = File.ReadAllBytes(Environment.ProcessPath!);
    var updaterArchive = Package(new() { Id = "fixture.updater", Name = "Native updater fixture", Publisher = "Fixture", Version = "1.0.0",
        TargetRuntime = InstallerPlatform.RuntimeIdentifier, Payload = [new() { Source = "heirowUpdater.exe", Path = "heirowUpdater.exe" }] }, "", binary: nativeSource);
    var updaterUrl = "https://packages.invalid/updater/1.0.0.hinst"; transport.Content[updaterUrl] = File.ReadAllBytes(updaterArchive);
    var updaterDefinition = new SharedPackageDefinition { Id = "fixture.updater", Version = "1.0.0", TargetRuntime = InstallerPlatform.RuntimeIdentifier,
        DownloadUrl = updaterUrl, Sha256 = PackageSafety.HashFile(updaterArchive), Size = new FileInfo(updaterArchive).Length, PublisherKeySha256 = pin, Feature = "updates" };
    var downloaded = App("fixture.downloaded-updater", "1.0.0", dependency1);
    downloaded.SharedPackages!.Add(updaterDefinition); downloaded.Features.Add(new() { Id = "updates", Name = "Updates", Default = true });
    downloaded.UpdateCompanion = new() { SharedPackageId = "fixture.updater", ExecutablePath = "heirowUpdater.exe" };
    var downloadedApp = Package(downloaded, "thin application");
    using var package = PackageReader.Open(downloadedApp);
    Check(package.Manifest.Payload.All(file => file.Path != "heirowUpdater.exe"), "Downloaded updater is excluded from application payload.");
    Check(package.Manifest.UpdateCompanion!.UpdateProtocol == "hfiles1", "Downloaded updater authoring selects the file-selection protocol.");
    await engine.InstallAsync(package, Options("fixture.downloaded-updater"));
    var configuration = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllText(Path.Combine(root, "fixture.downloaded-updater", "heirowUpdater.json")), InstallerJson.Options)!;
    SharedUpdaterRuntime.ValidateBinding(configuration, package.Manifest);
    Check(configuration.UpdateProtocol == "hfiles1" && configuration.SharedRuntime!.Size == nativeSource.LongLength, "Generated profile retains protocol and exact inner signed updater bytes.");
    Check(PackageSafety.HashFile(SharedUpdaterRuntime.Resolve(configuration)) == Convert.ToHexString(SHA256.HashData(nativeSource)).ToLowerInvariant(), "Downloaded host resolves only the verified scope-wide executable.");
    Check(Binding("fixture.downloaded-updater").Packages.Count == 2, "All selected target-platform feature packs complete during setup.");
    Check(!File.Exists(Path.Combine(root, "fixture.downloaded-updater", "heirowUpdater.exe")), "No duplicate updater executable is installed into the application root.");
    configuration.UpdateProtocol = "hupd1";
    await Reject<InvalidDataException>(() => { SharedUpdaterRuntime.ValidateBinding(configuration, package.Manifest); return Task.CompletedTask; }, "Mutable profiles cannot change the signed update protocol.");
}

var localLaunch = App("fixture.application-launch", "1.0.0", dependency1);
localLaunch.Payload = [new() { Source = localLaunch.EntryPoint, Path = localLaunch.EntryPoint },
    new() { Source = "app.App.exe", Path = "app.App.exe" }, new() { Source = "app.runtimeconfig.json", Path = "app.runtimeconfig.json" }];
localLaunch.ApplicationLaunch = new() { ExecutablePath = "app.App.exe", RuntimeConfigPath = "app.runtimeconfig.json", RuntimePackageId = dependency1.Id,
    ProbingPackageIds = [dependency1.Id] };
using (var package = PackageReader.Open(Package(localLaunch, "native launch fixture")))
{
    await engine.InstallAsync(package, Options("fixture.application-launch"));
    var path = SharedPackages.ProbingConfigurationPath(package.Manifest)!;
    using var probing = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fixture.application-launch", path)));
    Check(probing.RootElement.GetProperty("runtimeOptions").GetProperty("additionalProbingPaths")[0].GetString() == one.Packages[0].Directory,
        "Setup creates exact .NET probing paths from signed shared package identities.");
    var target = RunningApplicationTarget.FromPlan(engine.Plan(package, Options("fixture.application-launch")));
    Check(target.OwnedApplicationImages.Contains(Path.Combine(root, "fixture.application-launch", "app.App.exe")),
        "The actual renamed application host has exact process-closure authority.");
    var installed = InstallerEngine.ListInstalled(state).Single(product => product.Id == "fixture.application-launch");
    Check(installed.Files.Any(file => file.Path == path && file.Sha256 == PackageSafety.HashFile(Path.Combine(root, "fixture.application-launch", path))),
        "Generated probing metadata participates in receipt ownership.");
}
var resolved = await SharedPackageResources.ResolveFileAsync("fixture.two", dependency1.Id, "dependency.dll", pin,
    expectedInstallDirectory: Path.Combine(root, "fixture.two"), stateRoot: state);
Check(resolved == Path.Combine(one.Packages[0].Directory, "dependency.dll"), "Resource resolution derives exact paths from signed installed package authority.");
await Reject<CryptographicException>(() => SharedPackageResources.ResolveFileAsync("fixture.two", dependency1.Id, "dependency.dll", new string('c', 64),
    expectedInstallDirectory: Path.Combine(root, "fixture.two"), stateRoot: state), "A different expected publisher cannot select installed shared resources.");

var broken = Dependency("4.0.0", "bad-download-test");
transport.Content[broken.DownloadUrl] = new byte[checked((int)broken.Size)];
var badDownload = Package(App("fixture.bad-download", "1.0.0", broken), "not installed");
using (var package = PackageReader.Open(badDownload))
    await Reject<CryptographicException>(() => engine.InstallAsync(package, Options("fixture.bad-download")), "Corrupt download must fail before application changes.");
Check(!Directory.Exists(Path.Combine(root, "fixture.bad-download")), "A corrupt dependency cannot create the application root.");

var redirect = Dependency("5.0.0", "redirect-test");
transport.Redirects[redirect.DownloadUrl] = "https://foreign.invalid/steal";
var redirectApp = Package(App("fixture.redirect", "1.0.0", redirect), "not installed");
using (var package = PackageReader.Open(redirectApp))
    await Reject<InvalidDataException>(() => engine.InstallAsync(package, Options("fixture.redirect")), "Cross-origin redirect must fail closed.");
Check(!transport.RequestedUris.Any(uri => uri.Host == "foreign.invalid"), "Foreign redirects never receive a transport request.");

var oldFormat = App("fixture.old-format", "1.0.0", dependency1);
oldFormat.Payload = [new() { Path = "app.exe", Sha256 = new string('a', 64), Size = 1 }]; oldFormat.FormatVersion = 4;
await Reject<InvalidDataException>(() => { PackageSafety.ValidateManifest(oldFormat, true); return Task.CompletedTask; }, "Shared metadata cannot be silently accepted by older formats.");
var collision = App("fixture.collision", "1.0.0", dependency1); collision.FormatVersion = 5;
collision.Payload = [new() { Path = SharedPackages.BindingFileName, Sha256 = new string('a', 64), Size = 1 }];
await Reject<InvalidDataException>(() => { PackageSafety.ValidateManifest(collision, true); return Task.CompletedTask; }, "Application payload cannot replace generated binding metadata.");

var sharedFile = Path.Combine(one.Packages[0].Directory, "dependency.dll");
await File.WriteAllTextAsync(sharedFile, "tampered immutable runtime");
using (var package = PackageReader.Open(app2))
    await Reject<CryptographicException>(() => engine.InstallAsync(package, Options("fixture.two")), "An activated shared runtime must never be rewritten in place after corruption.");
Check(File.ReadAllText(sharedFile) == "tampered immutable runtime", "Tampered immutable dependency is preserved for explicit repair.");
Check(SharedPackages.Root("user", state) != SharedPackages.Root("machine", state), "Machine and user dependency stores are distinct.");
using (var package = PackageReader.Open(app2))
{
    var cachedPrefetch = Path.Combine(root, "cached-prefetch"); var requests = transport.Requests;
    await SharedPackages.PrefetchAsync(package, engine.Plan(package, Options("fixture.two")), cachedPrefetch, Options("fixture.two"));
    Check(transport.Requests == requests && !Directory.Exists(cachedPrefetch), "Prefetch reuses a fully verified store archive without another download or staging copy.");
}
var stagedDefinition = Dependency("9.0.0", "prefetch-only verified bytes");
var stagedApplication = Package(App("fixture.prefetch", "1.0.0", stagedDefinition), "not yet installed");
using (var package = PackageReader.Open(stagedApplication))
{
    var stagedOptions = Options("fixture.prefetch"); var stagedRoot = Path.Combine(root, "prepared-only");
    await SharedPackages.PrefetchAsync(package, engine.Plan(package, stagedOptions), stagedRoot, stagedOptions);
    var stagedArchive = Path.Combine(stagedRoot, "dependencies", stagedDefinition.Sha256.ToLowerInvariant() + ".hinst");
    Check(File.Exists(stagedArchive) && !Directory.Exists(stagedOptions.TargetDirectory), "Prefetch retains the verified archive without installing the app or extracting a shared runtime.");
    var requests = transport.Requests;
    await SharedPackages.PrefetchAsync(package, engine.Plan(package, stagedOptions), stagedRoot, stagedOptions);
    Check(transport.Requests == requests, "Repeated prefetch verifies and reuses its exact staged archive.");
    var bytes = await File.ReadAllBytesAsync(stagedArchive); bytes[^1] ^= 1; await File.WriteAllBytesAsync(stagedArchive, bytes);
    await Reject<CryptographicException>(() => SharedPackages.PrefetchAsync(package, engine.Plan(package, stagedOptions), stagedRoot, stagedOptions), "Corrupt staged archives fail without redownload or activation.");
}
Console.WriteLine($"Passed {checks} shared-package checks. Retained owned fixtures: {root}");

sealed class FixtureTransport : IUpdateTransport
{
    public Dictionary<string, byte[]> Content { get; } = [];
    public Dictionary<string, string> Redirects { get; } = [];
    public List<Uri> RequestedUris { get; } = [];
    public int Requests => RequestedUris.Count;
    public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RequestedUris.Add(uri);
        var response = new HttpResponseMessage(Redirects.ContainsKey(uri.AbsoluteUri) ? HttpStatusCode.Redirect : HttpStatusCode.OK)
        { RequestMessage = new(HttpMethod.Get, uri), Content = new ByteArrayContent(Redirects.ContainsKey(uri.AbsoluteUri) ? [] : Content[uri.AbsoluteUri]) };
        if (Redirects.TryGetValue(uri.AbsoluteUri, out var redirect)) response.Headers.Location = new Uri(redirect);
        return Task.FromResult(response);
    }
}
sealed class InlineProgress(Action<InstallProgress> callback) : IProgress<InstallProgress> { public void Report(InstallProgress value) => callback(value); }
sealed class FailureModule : IInstallerModule
{
    public string Id => "fixture.failure";
    public IReadOnlyList<string> Plan(JsonElement configuration, InstallerModuleContext context) => ["Synthetic failure after shared activation"];
    public Task<ModuleSnapshot> InstallAsync(JsonElement configuration, InstallerModuleContext context, CancellationToken token) => throw new InvalidOperationException("Synthetic acceptance failure");
    public Task UninstallAsync(ModuleSnapshot original, InstallerModuleContext context, CancellationToken token) => Task.CompletedTask;
}
