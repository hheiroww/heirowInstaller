using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

// An owned subprocess fixture exercises the actual process runner without contacting a signer.
if (args.Length > 0 && args[0] is "sign" or "verify")
{
    var file = args[^1];
    Console.Error.WriteLine("fixture-child-diagnostic-must-not-be-exposed");
    if (file.Contains("process-cancel", StringComparison.OrdinalIgnoreCase))
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, "child.pid"), Environment.ProcessId.ToString());
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    if (args[0] == "sign") File.AppendAllText(file, "fixture-signer-modification");
    Environment.Exit(file.Contains("verify-failure", StringComparison.OrdinalIgnoreCase) ? (args[0] == "verify" ? 2 : 0) : 7);
    return;
}

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Reject<T>(Action action, string message) where T : Exception
{
    try { action(); } catch (T) { Check(true, message); return; }
    throw new Exception(message);
}
async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); } catch (T) { Check(true, message); return; }
    throw new Exception(message);
}
var root = Path.Combine(Path.GetTempPath(), "heirowInstaller-code-signing-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    WindowsCodeSigner.Validate(new());
    Check(WindowsCodeSigner.CheckAvailability(new()).Provider == "none", "Disabled signing requires no tools, credentials, or network");
    Reject<ArgumentException>(() => WindowsCodeSigner.Validate(new() { CertificateThumbprint = new string('A', 40) }), "Disabled signing rejects supplied active settings");
    Reject<ArgumentException>(() => WindowsCodeSigner.Validate(new() { Provider = "other" }), "Unknown providers fail closed");
    Reject<ArgumentException>(() => WindowsCodeSigner.Validate(new() { Provider = "certificate", CertificateThumbprint = "not-a-thumbprint" }), "Installed certificates require an exact SHA-1 selection");
    var certificateOptions = new WindowsCodeSigningOptions { Provider = "certificate", CertificateThumbprint = new string('A', 40), UseMachineCertificateStore = true };
    WindowsCodeSigner.Validate(certificateOptions);
    certificateOptions.Endpoint = "https://eus.codesigning.azure.net/";
    Reject<ArgumentException>(() => WindowsCodeSigner.Validate(certificateOptions), "Providers cannot mix certificate and Microsoft account settings");
    certificateOptions.Endpoint = null;
    var microsoft = MicrosoftOptions();
    WindowsCodeSigner.Validate(microsoft);
    foreach (var endpoint in new[] { "http://eus.codesigning.azure.net/", "https://eus.codesigning.azure.net.attacker.example/", "https://user@eus.codesigning.azure.net/", "https://eus.codesigning.azure.net/path", "https://eus.codesigning.azure.net/?token=fixture", "https://eus.codesigning.azure.net:444/", "https://127.0.0.1/", "https://codesigning.azure.net/" })
    {
        microsoft.Endpoint = endpoint;
        Reject<ArgumentException>(() => WindowsCodeSigner.Validate(microsoft), "Microsoft endpoint rejects nonregional or credential-bearing origins");
    }
    microsoft = MicrosoftOptions(); microsoft.AccountName = "invalid/account";
    Reject<ArgumentException>(() => WindowsCodeSigner.Validate(microsoft), "Microsoft account metadata cannot carry paths");
    microsoft = MicrosoftOptions(); microsoft.TimestampUrl = "https://user:password@timestamp.example/";
    Reject<ArgumentException>(() => WindowsCodeSigner.Validate(microsoft), "Timestamp configuration rejects embedded credentials");
    microsoft = MicrosoftOptions(); microsoft.SignToolPath = "signtool.exe";
    Reject<ArgumentException>(() => WindowsCodeSigner.Validate(microsoft), "Explicit tool selection requires an absolute path");
    microsoft = MicrosoftOptions();
    using (var metadata = JsonDocument.Parse(WindowsCodeSigner.CreateMicrosoftMetadata(microsoft)))
    {
        Check(metadata.RootElement.GetProperty("CodeSigningAccountName").GetString() == "fixture-account" && metadata.RootElement.GetProperty("ExcludeCredentials")[0].GetString() == "InteractiveBrowserCredential", "Microsoft metadata uses ambient credentials and suppresses interactive browser sign-in");
        Check(metadata.RootElement.EnumerateObject().Count() == 4, "Microsoft metadata contains only the four documented nonsecret settings");
    }
    var availability = new WindowsCodeSigningAvailability { Provider = "microsoft", SignToolPath = "fixture-tool.exe", ArtifactSigningDlibPath = Path.Combine(root, "fixture dlib.dll") };
    var signArguments = WindowsCodeSigner.CreateSignArguments(Path.Combine(root, "setup with spaces.exe"), microsoft, availability, Path.Combine(root, "metadata with spaces.json"));
    Check(signArguments.Contains("SHA256") && signArguments.Contains("/dlib") && signArguments.Contains("/dmdf") && signArguments[^1].EndsWith("setup with spaces.exe"), "Microsoft signing preserves individual arguments and uses SHA-256 file/timestamp digests");
    var certArguments = WindowsCodeSigner.CreateSignArguments("fixture.exe", certificateOptions, new(), "unused");
    Check(certArguments.Contains("/sha1") && certArguments.Contains("/sm") && !certArguments.Contains("/p") && !certArguments.Contains("/f"), "Installed-certificate signing uses the selected store and never a PFX password argument");

    using var rootKey = RSA.Create(2048);
    var rootRequest = new CertificateRequest("CN=Isolated signing test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    using var rootCertificate = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));
    Reject<InvalidOperationException>(() => WindowsCodeSigner.RequireCodeSigningCertificate(rootCertificate), "Self-signed leaf certificates are refused even if locally trusted");
    using var leafKey = RSA.Create(2048);
    var leafRequest = new CertificateRequest("CN=Isolated signing test publisher", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
    using var leaf = leafRequest.Create(rootCertificate, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
    WindowsCodeSigner.RequireCodeSigningCertificate(leaf);
    Check(true, "CA-issued code-signing metadata can be checked without exporting keys or modifying trust");
    var wrongRequest = new CertificateRequest("CN=Isolated non-code-signing certificate", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var wrongCertificate = wrongRequest.Create(rootCertificate, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
    Reject<InvalidOperationException>(() => WindowsCodeSigner.RequireCodeSigningCertificate(wrongCertificate), "A certificate without code-signing EKU is refused");
    using var expiredCertificate = leafRequest.Create(rootCertificate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(-1), RandomNumberGenerator.GetBytes(16));
    Reject<InvalidOperationException>(() => WindowsCodeSigner.RequireCodeSigningCertificate(expiredCertificate), "Expired certificates are refused before signing");

    foreach (var failurePhase in new[] { "sign", "verify" })
    {
        var file = Path.Combine(root, failurePhase + "-transaction.exe"); File.WriteAllText(file, "MZ-original");
        var called = new List<string>();
        await RejectAsync<IOException>(() => WindowsCodeSigner.SignCoreAsync(file, microsoft, availability, (tool, arguments, token) =>
        {
            called.Add(arguments[0]);
            if (arguments[0] == "sign") File.AppendAllText(arguments[^1], "-changed");
            if (arguments[0] == failurePhase) throw new IOException("controlled fixture failure");
            return Task.CompletedTask;
        }, signerCertificateReader: _ => new X509Certificate2(leaf)), "A " + failurePhase + " failure preserves the original artifact");
        Check(File.ReadAllText(file) == "MZ-original" && !Directory.EnumerateFiles(root, "*.signing.exe").Any(), "Failed signing removes its temporary copy and keeps original bytes");
        Check(failurePhase == "verify" ? called.SequenceEqual(new[] { "sign", "verify" }) : called.SequenceEqual(new[] { "sign" }), "Verification never starts after a signing failure");
    }
    var success = Path.Combine(root, "controlled-success.exe"); File.WriteAllText(success, "MZ-original");
    var result = await WindowsCodeSigner.SignCoreAsync(success, microsoft, availability, (tool, arguments, token) =>
    {
        if (arguments[0] == "sign")
        {
            Reject<IOException>(() => File.WriteAllText(success, "concurrent-change"), "The source artifact stays locked against concurrent writers during signing");
            File.AppendAllText(arguments[^1], "-signed-fixture");
        }
        else Check(arguments.SequenceEqual(new[] { "verify", "/pa", "/all", "/tw", arguments[^1] }), "Windows verification requires Authenticode policy, all signatures, and timestamp warnings");
        return Task.CompletedTask;
    }, signerCertificateReader: _ => new X509Certificate2(leaf));
    Check(result.Verified && result.TimestampVerified && result.CertificateThumbprint == leaf.Thumbprint && File.ReadAllText(success).EndsWith("-signed-fixture"), "A controlled successful signing transaction publishes only after all checks");

    var payload = Path.Combine(root, "payload"); Directory.CreateDirectory(payload); File.WriteAllText(Path.Combine(payload, "app.txt"), "fixture application payload");
    var manifestPath = Path.Combine(root, "manifest.json");
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(new PackageManifest { Id = "signing.fixture", Name = "Signing fixture", Version = "1.0.0", Publisher = "Fixture", EntryPoint = "app.txt", LegalDocuments = LegalFixture.For("signing.fixture") }, InstallerJson.Options));
    var package = Path.Combine(root, "fixture.hinst"); PackageBuilder.Build(manifestPath, payload, package);
    var launcher = Path.Combine(root, "launcher.exe"); var signedLauncher = SyntheticPeWithCertificate(); File.WriteAllBytes(launcher, signedLauncher);
    var bundle = Path.Combine(root, "bundle.exe"); BundleBuilder.Build(launcher, package, bundle);
    Check(File.ReadAllBytes(launcher).SequenceEqual(signedLauncher), "Bundling never alters the signed source launcher");
    Check(!BundleBuilder.HasAuthenticodeTable(bundle) && new FileInfo(bundle).Length == signedLauncher.Length - 16 + new FileInfo(package).Length + 64, "Bundling strips a stale trailing launcher certificate before appending the package");
    using (var reader = PackageReader.Open(bundle))
    {
        await reader.VerifyAsync(); Check(reader.Manifest.Id == "signing.fixture", "The package remains readable after removing the launcher signature");
        using var rawPackage = PackageReader.Open(package);
        Check(reader.ComputePackageSha256() == rawPackage.ComputePackageSha256(), "Raw archive and bundled setup bind the same exact package bytes");
    }
    var beforeBundle = File.ReadAllBytes(bundle);
    await RejectAsync<CryptographicException>(() => WindowsCodeSigner.SignCoreAsync(bundle, microsoft, availability, (tool, arguments, token) =>
    {
        if (arguments[0] == "sign")
        {
            using var stream = new FileStream(arguments[^1], FileMode.Open, FileAccess.ReadWrite);
            stream.Position = 520; var value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 1));
        }
        return Task.CompletedTask;
    }, signerCertificateReader: _ => new X509Certificate2(leaf)), "A signer cannot publish a changed embedded payload even if its verification reports success");
    Check(File.ReadAllBytes(bundle).SequenceEqual(beforeBundle), "Embedded payload rejection preserves the original setup exactly");
    var malformed = SyntheticPeWithCertificate(); BitConverter.GetBytes((uint)32).CopyTo(malformed, 296 + 4); File.WriteAllBytes(launcher, malformed);
    await RejectAsync<InvalidDataException>(() => { BundleBuilder.Build(launcher, package, bundle); return Task.CompletedTask; }, "A non-trailing launcher certificate table is rejected without replacing existing setup");
    Check(File.ReadAllBytes(bundle).SequenceEqual(beforeBundle), "Malformed launcher rejection preserves the previous setup");

    if (OperatingSystem.IsWindows())
    {
        var executable = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) executable = Path.Combine(AppContext.BaseDirectory, "HeirowInstaller.CodeSigning.Tests.exe");
        var dlib = Path.Combine(root, "fixture-dlib.dll"); File.WriteAllBytes(dlib, [0]);
        microsoft.SignToolPath = executable; microsoft.ArtifactSigningDlibPath = dlib;
        foreach (var mode in new[] { "sign-failure", "verify-failure" })
        {
            var area = Path.Combine(root, mode); Directory.CreateDirectory(area); var file = Path.Combine(area, "fixture.exe"); File.WriteAllText(file, "MZ-original");
            try { await WindowsCodeSigner.SignAsync(file, microsoft); throw new Exception("Expected controlled tool rejection"); }
            catch (IOException error) { Check(!error.Message.Contains("fixture-child-diagnostic"), "Child signing diagnostics are not exposed in errors"); }
            Check(File.ReadAllText(file) == "MZ-original", "The actual process runner preserves the file after signing or timestamp verification fails");
        }
        var cancelArea = Path.Combine(root, "process-cancel"); Directory.CreateDirectory(cancelArea);
        var cancelFile = Path.Combine(cancelArea, "fixture.exe"); File.WriteAllText(cancelFile, "MZ-original");
        using (var cancellation = new CancellationTokenSource())
        {
            var task = WindowsCodeSigner.SignAsync(cancelFile, microsoft, cancellationToken: cancellation.Token);
            var pidFile = Path.Combine(cancelArea, "child.pid");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(pidFile) && !task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
            Check(File.Exists(pidFile), "The cancellation fixture starts the owned signing child");
            var processId = int.Parse(File.ReadAllText(pidFile)); cancellation.Cancel();
            await RejectAsync<OperationCanceledException>(() => task, "Cancellation terminates signing without publishing output");
            var stillRunning = false; try { using var child = Process.GetProcessById(processId); stillRunning = !child.HasExited; } catch (ArgumentException) { }
            Check(!stillRunning && File.ReadAllText(cancelFile) == "MZ-original", "Cancellation kills the owned child process and preserves original bytes");
        }
        var buildArea = Path.Combine(root, "builder-sign-failure"); Directory.CreateDirectory(buildArea);
        var buildLauncher = Path.Combine(buildArea, "launcher.exe"); File.WriteAllBytes(buildLauncher, SyntheticRuntimeLauncher());
        var existingOutput = Path.Combine(buildArea, "setup.exe"); File.WriteAllText(existingOutput, "previous published installer");
        await RejectAsync<IOException>(() => StandaloneInstallerBuilder.BuildAsync(manifestPath, existingOutput, new() { SourceDirectory = payload, LauncherPath = buildLauncher, WindowsCodeSigning = microsoft }), "Standalone build preserves its previous output when Windows signing fails");
        Check(File.ReadAllText(existingOutput) == "previous published installer" && !Directory.EnumerateFiles(buildArea, "*.building.exe").Any(), "Standalone signing failure removes staged output without replacing the last installer");
        var unsigned = await StandaloneInstallerBuilder.BuildAsync(manifestPath, existingOutput, new() { SourceDirectory = payload, LauncherPath = buildLauncher });
        Check(unsigned.WindowsSignature is null && PackageReader.HasEmbeddedPackage(existingOutput), "Unsigned standalone builds retain their existing explicit behavior");
    }
    SignedMaintenanceHostChecks.Run(Check, root);
    Console.WriteLine($"Passed {checks} Windows code-signing checks. No certificates were installed and no signing service was contacted.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

static WindowsCodeSigningOptions MicrosoftOptions() => new() { Provider = "microsoft", Endpoint = "https://eus.codesigning.azure.net/", AccountName = "fixture-account", CertificateProfileName = "fixture-profile" };
static byte[] SyntheticPeWithCertificate()
{
    var pe = new byte[528];
    using var stream = new MemoryStream(pe); using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0x5a4d); stream.Position = 0x3c; writer.Write(128);
    stream.Position = 128; writer.Write(0x00004550); stream.Position = 148; writer.Write((ushort)240);
    stream.Position = 152; writer.Write((ushort)0x20b); stream.Position = 260; writer.Write(16);
    stream.Position = 296; writer.Write((uint)512); writer.Write((uint)16);
    stream.Position = 504; writer.Write(Encoding.ASCII.GetBytes("HOSTDATA"));
    return pe;
}
static byte[] SyntheticRuntimeLauncher()
{
    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
    stream.SetLength(512); writer.Write((ushort)0x5a4d); stream.Position = 56; writer.Write((long)512);
    writer.Write(Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE"));
    var configuration = Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"includedFrameworks\":[]}}"); stream.Position = 256; writer.Write(configuration);
    stream.Position = 512; writer.Write((uint)6); writer.Write((uint)0); writer.Write(2); writer.Write("fixture");
    writer.Write((long)0); writer.Write((long)0); writer.Write((long)256); writer.Write((long)configuration.Length); writer.Write((ulong)0);
    foreach (var name in new[] { "System.Private.CoreLib.dll", "coreclr.dll" }) { writer.Write((long)400); writer.Write((long)1); writer.Write((long)0); writer.Write((byte)0); writer.Write(name); }
    return stream.ToArray();
}
