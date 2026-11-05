using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

if (args.FirstOrDefault() == "--crash")
{
    var fixtureRoot = args[1]; var phase = args[3];
    var installed = InstallerEngine.ListInstalled(Path.Combine(fixtureRoot, "state")).Single();
    var options = new InstallOptions { StateRoot = Path.Combine(fixtureRoot, "state"), TargetDirectory = Path.Combine(fixtureRoot, "app"), Isolated = true };
    using var prepared = PreparedFileUpdate.Open(args[2], installed, options);
    await new InstallerEngine().ApplyPreparedFileUpdateAsync(prepared, options, new InlineProgress(progress =>
    {
        if (phase == "partial" && progress.Phase == "install" && progress.Completed == 1 || phase == "metadata" && progress.Phase == "metadata" || phase == "receipt" && progress.Phase == "complete")
            Environment.Exit(89);
    }));
    throw new Exception("The crash point was not reached.");
}

var root = Path.GetFullPath(args.FirstOrDefault() ?? throw new ArgumentException("Specify one owned output directory."));
if (Directory.Exists(root)) throw new IOException("Use a fresh acceptance output directory; prior outputs remain available for review.");
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
async Task Reject<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); } catch (T) { checks++; return; }
    throw new Exception(message);
}

await using (var fixture = await Fixture.Create(Path.Combine(root, "skip-and-repair")))
{
    using (var negotiated = new UpdateClient(transport: fixture.Transport, requireIncrementalUpdates: true, fileSelectionUpdates: true))
    {
        var offered = await negotiated.CheckAsync("https://fixture.example/update.json?channel=beta", "1.0.0", "files.fixture");
        Check(offered.Available && offered.Feed.PackageType == "hfiles" && fixture.Transport.LastQuery.Contains("updateProtocol=hfiles1") &&
            fixture.Transport.LastQuery.Contains("fromVersion=1.0.0") && fixture.Transport.LastQuery.Contains("channel=beta"), "Protocol negotiation did not retain its exact installed version and channel.");
    }
    var kept = Path.Combine(fixture.App, "keep.bin"); var stamp = File.GetLastWriteTimeUtc(kept);
    File.WriteAllText(Path.Combine(fixture.App, "main.bin"), "locally modified owned bytes");
    File.Delete(Path.Combine(fixture.App, "missing.bin"));
    File.WriteAllText(Path.Combine(fixture.App, "personal.txt"), "user data outside package ownership");
    using var client = new FileUpdateClient(transport: fixture.Transport);
    var progress = new List<string>();
    using var stage = await client.PrepareAsync(fixture.Feed, fixture.Installed, Path.Combine(fixture.Root, "stages"), fixture.Options(), new InlineProgress(value => progress.Add(value.Phase)));
    Check(File.ReadAllText(Path.Combine(fixture.App, "main.bin")) == "locally modified owned bytes", "Preparation changed the running application.");
    Check(stage.RequestedPaths.Order().SequenceEqual(new[] { "added.bin", "main.bin", "missing.bin" }.Order()), "The diff included unchanged bytes or missed corrupt/missing files.");
    Check(fixture.Transport.BytesDownloaded < 8192, "The selected ZIP downloaded the large unchanged file.");
    var result = await new InstallerEngine().ApplyPreparedFileUpdateAsync(stage, fixture.Options());
    Check(result.Version == "3.0.0", "An update could not skip an uninstalled intermediate version.");
    Check(File.ReadAllText(Path.Combine(fixture.App, "main.bin")) == "new application bytes", "Changed bytes were not installed.");
    Check(File.ReadAllText(Path.Combine(fixture.App, "missing.bin")) == "same optional file bytes", "A missing owned file was not repaired.");
    Check(!File.Exists(Path.Combine(fixture.App, "retired.bin")), "A retired owned file was not removed.");
    Check(File.ReadAllText(Path.Combine(fixture.App, "personal.txt")) == "user data outside package ownership", "Unowned customer files changed.");
    Check(File.GetLastWriteTimeUtc(kept) == stamp, "An unchanged file timestamp changed.");
    var installed = fixture.Installed;
    Check(installed.PackagePath.EndsWith(".hmeta") && new FileInfo(installed.PackagePath).Length < 8192, "The update reconstructed a full repair installer.");
    Check(progress.Contains("diff") && progress.Contains("download") && progress.Contains("stage"), "Preparation did not report its asynchronous phases.");
    File.WriteAllText(Path.Combine(fixture.App, "main.bin"), "damaged after update"); File.Delete(Path.Combine(fixture.App, "missing.bin"));
    var repairOptions = fixture.Options(); repairOptions.SharedPackageTransport = fixture.Transport;
    await new InstallerEngine().RepairAsync("files.fixture", repairOptions);
    Check(File.ReadAllText(Path.Combine(fixture.App, "main.bin")) == "new application bytes" && File.ReadAllText(Path.Combine(fixture.App, "missing.bin")) == "same optional file bytes", "Online repair did not reacquire the exact installed version.");
    var featureOptions = fixture.Options(); featureOptions.SharedPackageTransport = fixture.Transport; featureOptions.Features = ["optional"];
    await new InstallerEngine().RepairAsync("files.fixture", featureOptions);
    Check(File.ReadAllText(Path.Combine(fixture.App, "optional.bin")) == "optional feature bytes" && fixture.Installed.Features.Contains("optional"), "Online feature selection did not download its missing payload.");
    var removeFeature = fixture.Options(); removeFeature.SharedPackageTransport = fixture.Transport; removeFeature.Features = [];
    await new InstallerEngine().RepairAsync("files.fixture", removeFeature);
    Check(!File.Exists(Path.Combine(fixture.App, "optional.bin")) && !fixture.Installed.Features.Contains("optional"), "Online feature deselection did not retire owned optional files.");
    Check(File.GetLastWriteTimeUtc(kept) == stamp, "Online repair or feature selection rewrote unchanged application files.");
    var metadataPath = fixture.Installed.PackagePath;
    using (var zip = ZipFile.Open(metadataPath, ZipArchiveMode.Update))
    {
        zip.GetEntry("file-source.json")!.Delete();
        var source = zip.CreateEntry("file-source.json"); using var output = source.Open();
        output.Write(JsonSerializer.SerializeToUtf8Bytes(new FileUpdateSource { ProductId = "files.fixture", Version = "3.0.0", TargetManifestSha256 = fixture.Feed.TargetManifestSha256!,
            ManifestUrl = "https://foreign.example/manifest", FilesUrl = "https://foreign.example/files" }, InstallerJson.Options));
    }
    await Reject<CryptographicException>(() => Task.Run(() => FileUpdateSource.Read(metadataPath, fixture.Installed.PublisherKeySha256!)), "A modified repair hint could redirect application credentials to another origin.");
}

await using (var fixture = await Fixture.Create(Path.Combine(root, "unowned")))
{
    File.WriteAllText(Path.Combine(fixture.App, "added.bin"), "user-owned collision");
    using var client = new FileUpdateClient(transport: fixture.Transport);
    await Reject<IOException>(async () => { using var stage = await client.PrepareAsync(fixture.Feed, fixture.Installed, Path.Combine(fixture.Root, "stages"), fixture.Options()); }, "An unowned update destination was adopted.");
    Check(File.ReadAllText(Path.Combine(fixture.App, "added.bin")) == "user-owned collision" && fixture.Transport.Posts == 0, "A colliding user-owned file changed.");
}

foreach (var attack in new[] { "missing", "extra", "duplicate", "traversal", "symlink", "tamper", "metadata" })
{
    await using var fixture = await Fixture.Create(Path.Combine(root, attack)); fixture.Transport.Attack = attack;
    using var client = new FileUpdateClient(transport: fixture.Transport);
    await Reject<Exception>(async () => { using var stage = await client.PrepareAsync(fixture.Feed, fixture.Installed, Path.Combine(fixture.Root, "stages"), fixture.Options()); }, "A hostile ZIP was accepted: " + attack);
    Check(fixture.Installed.Version == "1.0.0" && File.ReadAllText(Path.Combine(fixture.App, "main.bin")) == "old application bytes", "Rejected ZIP changed the installed application.");
}

await using (var fixture = await Fixture.Create(Path.Combine(root, "cancel")))
{
    using var cancellation = new CancellationTokenSource();
    using var client = new FileUpdateClient(transport: fixture.Transport);
    await Reject<OperationCanceledException>(async () =>
    {
        using var stage = await client.PrepareAsync(fixture.Feed, fixture.Installed, Path.Combine(fixture.Root, "stages"), fixture.Options(),
            new InlineProgress(progress => { if (progress.Phase == "diff") cancellation.Cancel(); }), cancellation.Token);
    }, "Cancelled diff continued installing.");
    Check(fixture.Transport.Posts == 0 && fixture.Installed.Version == "1.0.0", "Cancellation downloaded or installed an update.");
}

await using (var fixture = await Fixture.Create(Path.Combine(root, "rollback")))
{
    using var client = new FileUpdateClient(transport: fixture.Transport);
    using var stage = await client.PrepareAsync(fixture.Feed, fixture.Installed, Path.Combine(fixture.Root, "stages"), fixture.Options());
    await Reject<IOException>(() => new InstallerEngine().ApplyPreparedFileUpdateAsync(stage, fixture.Options(),
        new InlineProgress(progress => { if (progress.Phase == "install" && progress.Completed == 1) throw new IOException("Fixture interruption"); })), "Apply failure did not surface.");
    Check(File.ReadAllText(Path.Combine(fixture.App, "main.bin")) == "old application bytes" && File.Exists(Path.Combine(fixture.App, "retired.bin")) && fixture.Installed.Version == "1.0.0", "Rollback failed to restore original files and receipt.");
    await new InstallerEngine().ApplyPreparedFileUpdateAsync(stage, fixture.Options());
    Check(fixture.Installed.Version == "3.0.0", "A rolled-back stage could not be retried.");
}

foreach (var phase in new[] { "partial", "metadata", "receipt" })
{
    await using var fixture = await Fixture.Create(Path.Combine(root, "crash-" + phase));
    string stageRoot;
    using (var client = new FileUpdateClient(transport: fixture.Transport))
    using (var stage = await client.PrepareAsync(fixture.Feed, fixture.Installed, Path.Combine(fixture.Root, "stages"), fixture.Options())) stageRoot = stage.Root;
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Fixture).Assembly.Location);
    foreach (var argument in new[] { "--crash", fixture.Root, stageRoot, phase }) start.ArgumentList.Add(argument);
    using (var child = Process.Start(start)!) { await child.WaitForExitAsync(); Check(child.ExitCode == 89, "The owned child did not reach its crash point."); }
    using var reopened = PreparedFileUpdate.Open(stageRoot, fixture.Installed, fixture.Options());
    await new InstallerEngine().ApplyPreparedFileUpdateAsync(reopened, fixture.Options());
    Check(fixture.Installed.Version == "3.0.0" && File.ReadAllText(Path.Combine(fixture.App, "main.bin")) == "new application bytes", "Crash recovery did not finish the signed target: " + phase);
}

await using (var fixture = await Fixture.Create(Path.Combine(root, "selected-machine-scope"), "machine"))
{
    using var client = new FileUpdateClient(transport: fixture.Transport);
    string directory;
    using (var stage = await client.PrepareAsync(fixture.Feed, fixture.Installed, Path.Combine(fixture.Root, "stages"), fixture.Options()))
    {
        Check(stage.Metadata.Manifest.Scope == "user" && fixture.Installed.Scope == "machine", "The fixture did not preserve the selected all-users override of its default per-user manifest.");
        directory = stage.Root;
    }
    using var reopened = PreparedFileUpdate.Open(directory, fixture.Installed, fixture.Options());
    await new InstallerEngine().ApplyPreparedFileUpdateAsync(reopened, fixture.Options());
    Check(fixture.Installed.Scope == "machine" && fixture.Installed.Version == "3.0.0", "Selected-file apply changed or rejected the existing all-users scope.");
    var repairOptions = fixture.Options(); repairOptions.SharedPackageTransport = fixture.Transport;
    File.WriteAllText(Path.Combine(fixture.App, "main.bin"), "damaged machine install");
    await new InstallerEngine().RepairAsync(fixture.Installed.Id, repairOptions);
    Check(fixture.Installed.Scope == "machine" && File.ReadAllText(Path.Combine(fixture.App, "main.bin")) == "new application bytes", "Metadata-only repair lost the selected all-users scope.");
}

await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { status = "passed", assertions = checks, root }, InstallerJson.Options));
Console.WriteLine($"PASS: {checks} file-selection assertions. Owned isolated outputs: {root}");

internal sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
{
    public void Report(InstallProgress value) => report(value);
}

internal sealed class Fixture : IAsyncDisposable
{
    public string Root { get; }
    public string App => Path.Combine(Root, "app");
    public string State => Path.Combine(Root, "state");
    public InstalledProduct Installed => InstallerEngine.ListInstalled(State).Single();
    public SelectionTransport Transport { get; }
    public UpdateFeed Feed { get; }
    public string Scope { get; }
    private Fixture(string root, SelectionTransport transport, UpdateFeed feed, string scope) { Root = root; Transport = transport; Feed = feed; Scope = scope; }
    public InstallOptions Options() => new() { TargetDirectory = App, StateRoot = State, Scope = Scope, Isolated = true, RegisterProduct = false };
    public static async Task<Fixture> Create(string root, string scope = "user")
    {
        Directory.CreateDirectory(root); using var key = RSA.Create(2048);
        var keyPath = Path.Combine(root, "fixture-key.pem"); await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
        var kept = RandomNumberGenerator.GetBytes(512 * 1024);
        string Build(string version)
        {
            var directory = Path.Combine(root, "source-" + version); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "keep.bin"), kept);
            File.WriteAllText(Path.Combine(directory, "main.bin"), version == "1.0.0" ? "old application bytes" : "new application bytes");
            File.WriteAllText(Path.Combine(directory, "missing.bin"), "same optional file bytes");
            File.WriteAllText(Path.Combine(directory, "optional.bin"), "optional feature bytes");
            File.WriteAllText(Path.Combine(directory, version == "1.0.0" ? "retired.bin" : "added.bin"), version);
            var manifest = new PackageManifest { Id = "files.fixture", Name = "File selection fixture", Publisher = "Fixture", Version = version,
                EntryPoint = "main.bin", UpdateUrl = "https://fixture.example/update.json", LegalDocuments = LegalFixture.For("files.fixture"),
                Features = [new() { Id = "optional", Name = "Optional feature", Default = false }] };
            manifest.Payload = Directory.EnumerateFiles(directory).Order().Select(file => new PayloadFile { Path = Path.GetFileName(file) }).ToList();
            manifest.Payload.Single(file => file.Path == "optional.bin").Feature = "optional";
            var json = Path.Combine(root, "manifest-" + version + ".json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var package = Path.Combine(root, version + ".hinst"); PackageBuilder.Build(json, directory, package, new() { SigningKeyPath = keyPath }); return package;
        }
        var before = Build("1.0.0"); var after = Build("3.0.0");
        var transport = new SelectionTransport(after);
        var feed = new UpdateFeed { ProductId = "files.fixture", Version = "3.0.0", PackageType = "hfiles", ManifestUrl = "https://fixture.example/3.0.0/manifest.json", FilesUrl = "https://fixture.example/3.0.0/files.zip", TargetManifestSha256 = transport.ManifestSha256 };
        var fixture = new Fixture(root, transport, feed, scope);
        transport.Offer = feed;
        using var package = PackageReader.Open(before); await new InstallerEngine().InstallAsync(package, fixture.Options());
        return fixture;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SelectionTransport : IFileUpdateTransport
{
    private readonly byte[] manifest, signature;
    private readonly Dictionary<string, byte[]> payload;
    public string ManifestSha256 { get; }
    public string? Attack { get; set; }
    public long BytesDownloaded { get; private set; }
    public int Posts { get; private set; }
    public UpdateFeed? Offer { get; set; }
    public string LastQuery { get; private set; } = "";
    public SelectionTransport(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        static byte[] Read(ZipArchiveEntry entry) { using var stream = entry.Open(); using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); }
        manifest = Read(zip.GetEntry("manifest.json")!); signature = Read(zip.GetEntry("signature.json")!);
        payload = zip.Entries.Where(entry => entry.FullName.StartsWith("payload/")).ToDictionary(entry => entry.FullName, Read);
        ManifestSha256 = Convert.ToHexString(SHA256.HashData(manifest));
    }
    public Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        LastQuery = uri.Query;
        var body = uri.AbsolutePath == "/update.json" ? JsonSerializer.SerializeToUtf8Bytes(Offer, InstallerJson.Options)
            : JsonSerializer.SerializeToUtf8Bytes(new { manifestBase64 = Convert.ToBase64String(manifest), signatureBase64 = Convert.ToBase64String(signature) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
    public Task<HttpResponseMessage> PostAsync(Uri uri, long maximumResponseBytes, ReadOnlyMemory<byte> json, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Posts++;
        using var request = JsonDocument.Parse(json); var selected = request.RootElement.GetProperty("paths").EnumerateArray().Select(value => value.GetString()!).ToArray();
        if (request.RootElement.GetProperty("targetManifestSha256").GetString() != ManifestSha256.ToLowerInvariant()) throw new Exception("Request lost target digest.");
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes, bool link = false) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); if (link) entry.ExternalAttributes = 0xA000 << 16; using var target = entry.Open(); target.Write(bytes); }
            Add("manifest.json", Attack == "metadata" ? "{}"u8.ToArray() : manifest); Add("signature.json", signature);
            foreach (var path in selected.Skip(Attack == "missing" ? 1 : 0)) Add("payload/" + path, Attack == "tamper" ? new byte[payload["payload/" + path].Length] : payload["payload/" + path], Attack == "symlink");
            if (Attack == "extra") Add("payload/keep.bin", payload["payload/keep.bin"]);
            if (Attack == "duplicate") Add("payload/" + selected[0], payload["payload/" + selected[0]]);
            if (Attack == "traversal") Add("payload/../outside.bin", "outside"u8.ToArray());
        }
        var bytes = output.ToArray(); BytesDownloaded += bytes.Length;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
