using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.App;
using HeirowInstaller.Core;

internal static partial class Program
{
    static void SparseWebSetupWizard()
    {
        var basePackage = Build("websetup-sparse", includeLicense: false);
        using var listener = Listen();
        string updateUrl = new Uri(Endpoint(listener), "update.json").AbsoluteUri;
        var targetPackage = BuildSparseRelease(basePackage, "2.0.0", "signed sparse upgrade", updateUrl);
        var newerPackage = BuildSparseRelease(basePackage, "3.0.0", "signed newer application", updateUrl);
        using var service = new SparseWebSetupService(listener, basePackage.Package, targetPackage);

        Fixture Destination(string name)
        {
            var directory = Path.Combine(root, "websetup-sparse", name);
            var state = Path.Combine(directory, "state");
            return basePackage with { Target = Path.Combine(directory, "app"), State = state,
                Receipt = Path.Combine(state, basePackage.Id, "receipt.json") };
        }

        AppArguments Remote(Fixture installation) => Arguments(installation) with
        {
            PackagePath = null,
            ExpectedVersion = "2.0.0",
            ExpectedSha256 = PackageSafety.HashFile(targetPackage),
            RemotePackageUrl = service.Url("application.hinst"),
            RemotePackageSize = new FileInfo(targetPackage).Length,
            VerifiedWebSetupFileUpdate = new WebSetupFileUpdate(service.Url("2.0.0/manifest.json"), service.Url("2.0.0/files.zip"), service.ManifestSha256)
        };

        var fresh = Destination("fresh");
        using (var form = NewForm(Remote(fresh)))
        {
            Pump(form, form.InitializeForTestsAsync());
            Check(!form.Controller.Failed && !form.Controller.IsWebSetupFileUpdate && form.Controller.Manifest?.Version == "2.0.0" &&
                !Directory.Exists(fresh.Target) && service.Requests == (1, 0, 0),
                "fresh website setup downloads the signed full package once and leaves installation to the user");
            Hidden(form);
        }

        var old = Destination("upgrade");
        InstallSparseFixture(old, basePackage.Package);
        string changed = Path.Combine(old.Target, "app.txt");
        string retained = Path.Combine(old.Target, "docs.txt");
        string userFile = Path.Combine(old.Target, "user.txt");
        File.WriteAllText(userFile, "fixture user data");
        File.SetLastWriteTimeUtc(retained, new DateTime(2020, 1, 2, 3, 4, 0, DateTimeKind.Utc));
        DateTime retainedTimestamp = File.GetLastWriteTimeUtc(retained);
        string receiptBeforeReview = PackageSafety.HashFile(old.Receipt);

        using (var form = NewForm(Remote(old)))
        {
            Pump(form, form.InitializeForTestsAsync());
            var controller = form.Controller;
            Check(!controller.Failed && controller.IsWebSetupFileUpdate && controller.Stage == SetupStage.Review &&
                controller.Manifest?.Version == "2.0.0" && controller.TargetDirectory == old.Target && controller.Scope == "user" &&
                controller.Features.SetEquals(["core", "docs"]) && form.Control("setup.next").Text == "Install update" &&
                !form.HasControl("setup.back") && !form.HasControl("setup.target"),
                $"an older signed native installation opens the fixed-location sparse review using its installed features ({controller.Stage}, " +
                $"sparse={controller.IsWebSetupFileUpdate}, requests={service.Requests}, {controller.Status}: {controller.StatusDetail})");
            Check(service.Requests == (1, 1, 1) && service.SelectedPaths.SequenceEqual(["app.txt"]) &&
                File.ReadAllText(changed) == "signed synthetic application" &&
                PackageSafety.HashFile(old.Receipt) == receiptBeforeReview && File.GetLastWriteTimeUtc(retained) == retainedTimestamp,
                "website update requests the signed manifest and only the changed file, preserving files and receipt at review");
            Click(form, "setup.next");
            Until(form, () => !controller.Busy);
            Check(!controller.Failed && controller.Stage == SetupStage.Complete &&
                InstallerEngine.ListInstalled(old.State).Single().Version == "2.0.0" &&
                File.ReadAllText(changed) == "signed sparse upgrade" && File.ReadAllText(userFile) == "fixture user data" &&
                File.ReadAllText(retained) == "optional synthetic documentation" && File.GetLastWriteTimeUtc(retained) == retainedTimestamp &&
                service.Requests == (1, 1, 1),
                "the real CPU Install update command applies the prepared files without a second download");
            Hidden(form);
        }

        var newer = Destination("newer");
        InstallSparseFixture(newer, newerPackage);
        foreach (var (installation, version) in new[] { (old, "2.0.0"), (newer, "3.0.0") })
        {
            using var form = NewForm(Remote(installation));
            Pump(form, form.InitializeForTestsAsync());
            Check(!form.Controller.Failed && form.Controller.CurrentWebSetupVersion == version && form.Controller.Manifest is null &&
                form.Control("wizard.cancel").Text == "Close" && !form.HasControl("setup.next") && service.Requests == (1, 1, 1),
                $"a website installer for an existing {version} installation offers Close without requesting any package bytes");
            Hidden(form);
        }

        var rejected = Destination("rejected");
        InstallSparseFixture(rejected, basePackage.Package);
        service.ReturnWrongManifest = true;
        using (var form = NewForm(Remote(rejected)))
        {
            Pump(form, form.InitializeForTestsAsync());
            Check(form.Controller.Failed && form.Controller.Manifest is null && form.Controller.CanRetryWebSetup &&
                form.HasControl("wizard.retry") && service.Requests == (1, 2, 1) &&
                InstallerEngine.ListInstalled(rejected.State).Single().Version == "1.0.0",
                "a signed manifest for the wrong target fails closed without asking for files or the full package");
            service.ReturnWrongManifest = false;
            Click(form, "wizard.retry");
            Until(form, () => !form.Controller.Busy);
            Check(!form.Controller.Failed && form.Controller.Stage == SetupStage.Review && service.Requests == (1, 3, 2),
                "Retry reopens the signed sparse path without falling back to full setup");
            Hidden(form);
        }
    }

    private static string BuildSparseRelease(Fixture original, string version, string contents, string updateUrl)
    {
        string directory = Path.Combine(root, "websetup-sparse", "release-" + version);
        Directory.CreateDirectory(directory);
        using var signed = PackageReader.Open(original.Package);
        var manifest = signed.Manifest;
        manifest.Version = version;
        manifest.UpdateUrl = updateUrl;
        manifest.Features.Single(feature => feature.Id == "docs").Default = false;
        File.WriteAllText(Path.Combine(directory, "app.txt"), contents);
        File.WriteAllText(Path.Combine(directory, "docs.txt"), "optional synthetic documentation");
        string metadata = Path.Combine(directory, "manifest.json");
        string package = Path.Combine(directory, "release.hinst");
        File.WriteAllText(metadata, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        PackageBuilder.Build(metadata, directory, package, new() { SigningKeyPath = signingKey });
        return package;
    }

    private static void InstallSparseFixture(Fixture fixture, string packagePath)
    {
        using var package = PackageReader.Open(packagePath);
        new InstallerEngine().InstallAsync(package, new()
        {
            TargetDirectory = fixture.Target, StateRoot = fixture.State, Scope = "user",
            Features = ["core", "docs"], Isolated = true, RegisterProduct = false,
            TrustedPublisherKeySha256 = signingPin, RequireSignature = true, Launch = false
        }).GetAwaiter().GetResult();
    }

    private sealed class SparseWebSetupService : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task server;
        private readonly byte[] fullPackage, envelope, wrongEnvelope, subset;
        private int fullRequests, manifestRequests, subsetRequests;
        private string[] selected = [];
        public string ManifestSha256 { get; }
        public (int Full, int Manifest, int Subset) Requests => (Volatile.Read(ref fullRequests), Volatile.Read(ref manifestRequests), Volatile.Read(ref subsetRequests));
        public string[] SelectedPaths => Volatile.Read(ref selected);
        public volatile bool ReturnWrongManifest;

        internal SparseWebSetupService(TcpListener listener, string basePackage, string targetPackage)
        {
            this.listener = listener;
            fullPackage = File.ReadAllBytes(targetPackage);
            using var original = ZipFile.OpenRead(basePackage);
            wrongEnvelope = Envelope(ReadEntry(original, "manifest.json"), ReadEntry(original, "signature.json"));
            using var target = ZipFile.OpenRead(targetPackage);
            byte[] manifest = ReadEntry(target, "manifest.json"), signature = ReadEntry(target, "signature.json");
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(manifest));
            envelope = Envelope(manifest, signature);
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add("manifest.json", manifest);
                Add("signature.json", signature);
                Add("payload/app.txt", ReadEntry(target, "payload/app.txt"));

                void Add(string name, byte[] value)
                {
                    using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    entry.Write(value);
                }
            }
            subset = bytes.ToArray();
            server = ServeAsync();
        }

        internal string Url(string path) => new Uri(Endpoint(listener), path).AbsoluteUri;

        private async Task ServeAsync()
        {
            var token = stop.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(token);
                    using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    string methodAndPath = await reader.ReadLineAsync(token) ?? "";
                    int contentLength = 0;
                    while (await reader.ReadLineAsync(token) is { Length: > 0 } header)
                        if (header.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
                            contentLength = int.Parse(header["Content-Length: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    byte[] payload;
                    string mime;
                    switch (methodAndPath.Split(' ').Take(2).ToArray())
                    {
                        case ["GET", "/application.hinst"]:
                            Interlocked.Increment(ref fullRequests);
                            (payload, mime) = (fullPackage, "application/octet-stream");
                            break;
                        case ["GET", "/2.0.0/manifest.json"]:
                            Interlocked.Increment(ref manifestRequests);
                            (payload, mime) = (ReturnWrongManifest ? wrongEnvelope : envelope, "application/json");
                            break;
                        case ["POST", "/2.0.0/files.zip"] when contentLength is > 0 and <= 4096:
                            var body = new char[contentLength];
                            if (await reader.ReadBlockAsync(body, token) != body.Length) throw new InvalidDataException("Incomplete fixture selection request.");
                            using (var json = JsonDocument.Parse(new string(body)))
                            {
                                var query = json.RootElement;
                                if (!string.Equals(query.GetProperty("targetManifestSha256").GetString(), ManifestSha256, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException("Fixture selection did not bind the signed target.");
                                Volatile.Write(ref selected, query.GetProperty("paths").EnumerateArray().Select(item => item.GetString()!).ToArray());
                            }
                            Interlocked.Increment(ref subsetRequests);
                            (payload, mime) = (subset, "application/zip");
                            break;
                        default:
                            await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
                            continue;
                    }
                    await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nContent-Type: {mime}\r\nConnection: close\r\n\r\n", token);
                    await stream.WriteAsync(payload, token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        private static byte[] ReadEntry(ZipArchive package, string name)
        {
            using var entry = package.GetEntry(name)!.Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }

        private static byte[] Envelope(byte[] manifest, byte[] signature) =>
            JsonSerializer.SerializeToUtf8Bytes(new { manifestBase64 = Convert.ToBase64String(manifest),
                signatureBase64 = Convert.ToBase64String(signature) });

        public void Dispose()
        {
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); }
            finally { stop.Dispose(); }
        }
    }
}
