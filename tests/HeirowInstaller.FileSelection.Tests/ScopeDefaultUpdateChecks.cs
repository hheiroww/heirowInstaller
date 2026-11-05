using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Bootstrap;
using HeirowInstaller.Core;

internal static class ScopeDefaultUpdateChecks
{
    internal static async Task<int> RunAsync(string output)
    {
        string root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new IOException("Use a fresh owned scope-default fixture directory; previous outputs are retained.");
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < 64 * 1024 * 1024) throw new IOException("The bounded fixture needs 64 MiB free.");
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); }
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var key = RSA.Create(2048);
        string keyPath = Path.Combine(root, "synthetic-signing.pem"); await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
        string pin = Hash(key.ExportSubjectPublicKeyInfo()), runtime = InstallerPlatform.RuntimeIdentifier;
        const string id = "scope-default.fixture", dependencyId = "scope-default.runtime";
        string target = Path.Combine(root, "machine-app"), state = Path.Combine(root, "machine-state");
        var engine = new InstallerEngine();
        string Build(string name, PackageManifest manifest, Dictionary<string, byte[]> files)
        {
            string source = Path.Combine(root, name + "-source"); Directory.CreateDirectory(source);
            foreach (var file in files) { string path = PackageSafety.UnderRoot(source, file.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, file.Value); }
            manifest.LegalDocuments = LegalFixture.For(manifest.Id);
            manifest.Payload = files.Keys.Select(path => new PayloadFile { Path = path, Feature = path == "heirowUpdater.exe" ? "updates" : null }).ToList();
            string definition = Path.Combine(root, name + ".json"), archive = Path.Combine(root, name + ".hinst");
            File.WriteAllText(definition, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            PackageBuilder.Build(definition, source, archive, new() { SigningKeyPath = keyPath }); return archive;
        }
        var dependencyManifest = new PackageManifest { FormatVersion = 4, Id = dependencyId, Name = "Tiny runtime fixture", Publisher = "Fixture", Version = "1.0.0", TargetPlatform = InstallerPlatform.Current, TargetRuntime = runtime };
        string dependency = Build("dependency", dependencyManifest, new() { ["runtime.marker"] = "synthetic runtime marker; never executed"u8.ToArray() });
        byte[] dependencyBytes = await File.ReadAllBytesAsync(dependency);
        const string dependencyUrl = "https://scope.example/runtime/1.0.0.hinst";
        // A small versioned fixture image supplies the old companion's source binding.
        // It is never started; this test does not assert standalone SCS execution.
        byte[] updater = await File.ReadAllBytesAsync(typeof(ScopeDefaultUpdateChecks).Assembly.Location);
        Check(updater.Length < 1024 * 1024, "The fixture reuses a small versioned test PE instead of copying a runtime or production updater.");
        Dictionary<string, byte[]> Files(string version) => new()
        {
            ["launcher.exe"] = System.Text.Encoding.UTF8.GetBytes("synthetic bootstrap " + version),
            ["app.App.exe"] = System.Text.Encoding.UTF8.GetBytes("synthetic application " + version),
            ["app.runtimeconfig.json"] = "{\"runtimeOptions\":{}}"u8.ToArray(), ["heirowUpdater.exe"] = updater
        };
        PackageManifest Manifest(string version, string scope) => new()
        {
            FormatVersion = 4, Id = id, Name = "Scope-default update fixture", Publisher = "Fixture", Version = version, Scope = scope,
            TargetPlatform = InstallerPlatform.Current, TargetRuntime = runtime, EntryPoint = "launcher.exe", UpdateUrl = "https://scope.example/update.json",
            Features = [new() { Id = "updates", Name = "Updates", Default = true }],
            UpdateCompanion = new() { Feature = "updates", RuntimeMode = "shared", UpdateMode = "files", UpdateProtocol = "hfiles1", ExecutablePath = "heirowUpdater.exe" }
        };
        string bridge = Build("bridge7", Manifest("7.0.0", "machine"), Files("7"));
        var targetManifest = Manifest("8.0.0", "user"); targetManifest.FormatVersion = 5;
        targetManifest.SharedPackages = [new() { Id = dependencyId, Version = "1.0.0", TargetRuntime = runtime, DownloadUrl = dependencyUrl, Size = dependencyBytes.Length, Sha256 = Hash(dependencyBytes), PublisherKeySha256 = pin }];
        targetManifest.ApplicationLaunch = new() { ExecutablePath = "app.App.exe", RuntimeConfigPath = "app.runtimeconfig.json", RuntimePackageId = dependencyId, ProbingPackageIds = [dependencyId] };
        string after = Build("target8", targetManifest, Files("8"));
        var transport = new ScopeTransport(new SelectionTransport(after), dependencyUrl, dependencyBytes);
        using (var package = PackageReader.Open(after))
        {
            var freshUser = new InstallOptions { Isolated = true, RegisterProduct = false, TargetDirectory = Path.Combine(root, "fresh-user-app"), StateRoot = Path.Combine(root, "fresh-user-state"), TrustedPublisherKeySha256 = pin, RequireSignature = true };
            var plan = engine.Plan(package, freshUser);
            Check(plan.Scope == "user" && !plan.RequiresElevation && !Directory.Exists(freshUser.StateRoot) && !Directory.Exists(freshUser.TargetDirectory), "The same signed target defaults a fresh installation to user scope without elevation or plan-time writes.");
        }
        InstallOptions Options() => new() { Isolated = true, RegisterProduct = false, Scope = "machine", TargetDirectory = target, StateRoot = state,
            RequireSignature = true, TrustedPublisherKeySha256 = pin, SharedPackageTransport = transport };
        using (var package = PackageReader.Open(bridge))
        {
            var plan = engine.Plan(package, Options());
            Check(package.Manifest.FormatVersion == 4 && package.Manifest.Scope == "machine" && plan.Scope == "machine" && plan.UpdateCompanionEnabled, "The signed bridge starts as a machine installation with its physical hfiles companion selected.");
            await engine.InstallAsync(package, Options());
        }
        var original = InstallerEngine.ListInstalled(state).Single(); string originalReceipt = original.ReceiptPath;
        var feed = new UpdateFeed { ProductId = id, Version = "8.0.0", PackageType = "hfiles", ManifestUrl = "https://scope.example/8.0.0/manifest.json", FilesUrl = "https://scope.example/8.0.0/files.zip", TargetManifestSha256 = transport.Selection.ManifestSha256 };
        using var client = new FileUpdateClient(transport: transport);
        // Leave Scope/StateRoot/TargetDirectory unset: the client must bind all
        // installation identity from the existing machine receipt, not defaults.
        var prepareOptions = new InstallOptions { Isolated = true, RegisterProduct = false, SharedPackageTransport = transport };
        string preparedRoot;
        using (var prepared = await client.PrepareAsync(feed, original, Path.Combine(root, "stages"), prepareOptions))
        {
            Check(prepared.Metadata.Manifest.Scope == "user" && prepareOptions.Scope == "machine" && prepareOptions.StateRoot == state && prepareOptions.TargetDirectory == target, "File staging binds the installed machine identity despite the target's signed user default.");
            using var metadata = PackageReader.OpenMetadata(prepared.MetadataPath); var plan = engine.Plan(metadata, prepareOptions);
            Check(plan.Scope == "machine" && plan.RequiresElevation && plan.InstallDirectory == target && plan.Manifest.Scope == "user", "The actual target plan retains machine scope and destination while preserving its user-default manifest.");
            Check(transport.SharedDownloads == 1 && File.Exists(Path.Combine(prepared.Root, "dependencies", Hash(dependencyBytes) + ".hinst")), "The signed target shared archive is acquired before apply and owner shutdown.");
            preparedRoot = prepared.Root;
        }
        transport.AllowNetwork = false;
        var applyOptions = new InstallOptions { Isolated = true, RegisterProduct = false };
        using (var reopened = PreparedFileUpdate.Open(preparedRoot, original, applyOptions)) await engine.ApplyPreparedFileUpdateAsync(reopened, applyOptions);
        var products = InstallerEngine.ListInstalled(state); var installed = products.Single();
        Check(installed.Id == id && installed.Version == "8.0.0" && installed.Scope == "machine" && installed.InstallDirectory == target && installed.ReceiptPath == originalReceipt, "Actual engine apply updates the same machine receipt, version, root and state.");
        var receipts = Directory.GetFiles(root, "receipt.json", SearchOption.AllDirectories);
        Check(products.Count == 1 && receipts.Length == 1 && receipts[0] == originalReceipt && applyOptions.Scope == "machine" && applyOptions.StateRoot == state && !installed.RegisteredProduct && installed.Isolated, "Apply creates only the existing machine receipt under the bound state and requests no OS registration.");
        using var installedMetadata = PackageReader.OpenMetadata(installed.PackagePath);
        Check(installedMetadata.Manifest.Scope == "user" && File.ReadAllText(Path.Combine(target, "app.App.exe")) == "synthetic application 8", "The committed signed target retains its user default and changed application bytes.");
        var binding = JsonSerializer.Deserialize<SharedPackageBinding>(File.ReadAllBytes(Path.Combine(target, SharedPackages.BindingFileName)), InstallerJson.Options)!;
        string sharedRoot = SharedPackages.Root("machine", state, isolated: true);
        Check(binding.Scope == "machine" && binding.InstallDirectory == target && binding.Packages.Single().Directory.StartsWith(sharedRoot + Path.DirectorySeparatorChar, InstallerPlatform.PathComparison), "Generated shared bindings use the installed machine scope and task-owned machine state.");
        var references = Directory.GetFiles(Path.Combine(sharedRoot, "references"));
        Check(references.Length == 1 && File.ReadAllBytes(references[0]).SequenceEqual(File.ReadAllBytes(Path.Combine(target, SharedPackages.BindingFileName))), "The only shared owner reference exactly matches the committed machine binding.");
        using (var probes = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target, "app.runtimeconfig.dev.json"))))
            Check(probes.RootElement.GetProperty("runtimeOptions").GetProperty("additionalProbingPaths")[0].GetString() == binding.Packages.Single().Directory, "Generated probing selects the exact verified dependency under the installed machine state.");
        var profile = JsonSerializer.Deserialize<UpdateCompanionConfiguration>(File.ReadAllBytes(Path.Combine(target, "heirowUpdater.json")), InstallerJson.Options)!;
        Check(profile.Scope == "machine" && profile.StateRoot == state && profile.InstallDirectory == target && profile.UpdateProtocol == "hfiles1", "The updater handoff profile retains machine scope, state and target after a user-default release.");
        Check(transport.BlockedNetworkCalls == 0 && transport.SharedDownloads == 1, "Applying the prepared stage needs no new network access after handoff.");

        var descriptor = new BootstrapDescriptor { ProductId = id, Name = "Offline scope fixture", Version = "8.0.0", Scope = "user", HostKind = "application", PublisherKeySha256 = pin,
            HostEntryPoint = "app.App.exe", DotNetPackageId = dependencyId, RuntimeConfigPath = "app.runtimeconfig.json", ProbingPackageIds = [dependencyId],
            Packages = [new() { Id = dependencyId, Version = "1.0.0", TargetRuntime = runtime, Url = dependencyUrl, Sha256 = Hash(dependencyBytes), Size = dependencyBytes.Length }],
            ApplicationFiles = installedMetadata.Manifest.Payload.Where(file => file.Path is "app.App.exe" or "app.runtimeconfig.json").Select(file => new BootstrapPayload { Path = file.Path, Size = file.Size, Sha256 = file.Sha256 }).ToList() };
        var deniedNetwork = new ScopeHttpHandler(_ => throw new InvalidOperationException("Offline startup attempted a download.")); using var offlineHttp = new HttpClient(deniedNetwork);
        var offline = new BootstrapEngine(descriptor, offlineHttp, new BootstrapReports()); string wrongRoot = Path.Combine(root, "wrong-user-shared"), noStage = Path.Combine(root, "offline-must-not-stage");
        // Populate a fully valid second cache. Rejection must come from the
        // actual generated machine probing binding, not a missing-file shortcut.
        var prime = new BootstrapDescriptor { ProductId = id, Name = "Prime tiny alternate cache", Version = "8.0.0", Scope = "user", PublisherKeySha256 = pin, HostPackageId = dependencyId, HostEntryPoint = "runtime.marker", Packages = descriptor.Packages };
        using (var primeHttp = new HttpClient(new ScopeHttpHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(dependencyBytes) })))
            await new BootstrapEngine(prime, primeHttp, new BootstrapReports()).PrepareAsync(wrongRoot, Path.Combine(root, "prime-wrong-cache"), default);
        bool wrongRejected = false; try { await offline.PrepareAsync(wrongRoot, noStage, default, target); } catch (InvalidDataException) { wrongRejected = true; }
        Check(wrongRejected && deniedNetwork.Calls == 0, "A valid user cache is rejected when generated probing binds this installation to its machine cache.");
        var start = await offline.PrepareOfflineAsync([wrongRoot, sharedRoot], noStage, default, target);
        Check(descriptor.Scope == "user" && start.FileName == Path.Combine(target, "app.App.exe") && start.Environment["DOTNET_ROOT"] == binding.Packages.Single().Directory, "The actual native offline engine falls back to the bound machine cache despite the application's user-default descriptor.");
        Check(deniedNetwork.Calls == 0 && !Directory.Exists(noStage), "Offline preparation downloads nothing, creates no staging, and starts no process.");
        await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { passed = true, assertions = checks, baselineManifestScope = "machine", targetManifestScope = "user", installedScope = installed.Scope,
            installedTarget = target, installedState = state, isolated = true, registeredProduct = false, nativeOfflineEngineSourceLinked = true, helperExecuted = false, applicationExecuted = false,
            realMachineAclTested = false, uacTested = false, productionPathsWritten = false, retained = true }, InstallerJson.Options));
        return checks;
    }

    private sealed class ScopeTransport(SelectionTransport selection, string dependencyUrl, byte[] dependency) : IFileUpdateTransport
    {
        internal SelectionTransport Selection => selection;
        internal bool AllowNetwork = true;
        internal int SharedDownloads, BlockedNetworkCalls;
        public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
        {
            if (!AllowNetwork) { BlockedNetworkCalls++; throw new InvalidOperationException("No network is available after prepared handoff."); }
            if (uri.AbsoluteUri == dependencyUrl) { SharedDownloads++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(dependency) }); }
            return selection.GetAsync(uri, maximumResponseBytes, progress, cancellationToken);
        }
        public Task<HttpResponseMessage> PostAsync(Uri uri, long maximumResponseBytes, ReadOnlyMemory<byte> json, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
        {
            if (!AllowNetwork) { BlockedNetworkCalls++; throw new InvalidOperationException("No network is available after prepared handoff."); }
            return selection.PostAsync(uri, maximumResponseBytes, json, progress, cancellationToken);
        }
    }
    private sealed class ScopeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { internal int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(response(request)); } }
    private sealed class BootstrapReports : IProgress<BootstrapProgress> { public void Report(BootstrapProgress value) { } }
}
