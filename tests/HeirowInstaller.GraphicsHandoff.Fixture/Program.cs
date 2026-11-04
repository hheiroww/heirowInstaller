using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

const string Product = "heirowInstaller.wizard-fixture.graphics-handoff";
const string Marker = "heirowInstaller isolated wizard acceptance v1\n";
const string Notes = "heirowInstaller graphics handoff fixture v1\n";
const string Privacy = "Synthetic isolated graphics-handoff fixture. No personal data is collected.\n";
const string Terms = "Synthetic isolated graphics-handoff acceptance only. No production product is installed.\n";
try
{
if (args.Length != 3 || !Path.IsPathFullyQualified(args[0]) || args[1].Length != 64 || !args[1].All(Uri.IsHexDigit)
    || !Path.IsPathFullyQualified(args[2]))
    throw new ArgumentException("Use <published-primary.exe> <expected-primary-sha256> <fresh-owned-fixture-directory>.");
string primary = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[2]);
PackageSafety.EnsureNoReparsePoints(primary);
PackageSafety.EnsureNoReparsePoints(root);
if (Path.GetDirectoryName(root) is null || Directory.Exists(root) || File.Exists(root))
    throw new InvalidDataException("Preserve existing evidence; choose a fresh owned fixture directory.");
using var primaryLease = File.Open(primary, FileMode.Open, FileAccess.Read, FileShare.Read);
if (!PackageSafety.HashEquals(PackageSafety.HashFile(primary), args[1]) || primaryLease.Length is <= 0 or > 1073741824
    || PackageReader.HasEmbeddedPackage(primary))
    throw new InvalidDataException("Use the exact reviewed unbundled published primary host.");
Directory.CreateDirectory(root);
string inputs = Path.Combine(root, "build-inputs"), source = Path.Combine(inputs, "source");
Directory.CreateDirectory(source);
string keyPath = Path.Combine(inputs, "synthetic-key.pem"), manifestPath = Path.Combine(inputs, "manifest.json");
string packagePath = Path.Combine(root, "fixture.hinst"), setupPath = Path.Combine(root, "fixture-setup.exe");
using var key = RSA.Create(2048);
File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
string pin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
File.WriteAllText(Path.Combine(source, "notes.txt"), Notes, new UTF8Encoding(false));
var manifest = new PackageManifest
{
    FormatVersion = 3, Id = Product, Name = "Graphics handoff isolated fixture", Version = "1.0.0", Publisher = "Synthetic isolated test fixture",
    Scope = "machine", Payload = [new() { Source = "notes.txt", Path = "notes.txt" }]
};
var legal = LegalDocumentRules.CreateDraft(Product);
legal.PrivacyPolicy!.Content = Privacy; legal.TermsAndConditions!.Content = Terms;
manifest.LegalDocuments = LegalDocumentRules.PrepareForCreation(Product, legal);
File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options), new UTF8Encoding(false));
PackageBuilder.Build(manifestPath, source, packagePath, new() { SigningKeyPath = keyPath });
using (var package = PackageReader.Open(packagePath)) package.VerifySignature(pin, requireSignature: true);
BundleBuilder.Build(primary, packagePath, setupPath);
string packageHash = PackageSafety.HashFile(packagePath), setupHash = PackageSafety.HashFile(setupPath);
using (var bundled = PackageReader.Open(setupPath))
{
    bundled.VerifySignature(pin, requireSignature: true);
    if (bundled.Manifest.Id != Product || !PackageSafety.HashEquals(bundled.ComputePackageSha256(), packageHash))
        throw new InvalidDataException("The synthetic setup did not retain its exact signed footer.");
}
var binding = new
{
    schema = 1, root, productId = Product, version = "1.0.0", packageBytes = new FileInfo(packagePath).Length,
    packageSha256 = packageHash, publisherKeySha256 = pin, primaryBytes = new FileInfo(setupPath).Length, primarySha256 = setupHash
};
File.WriteAllText(Path.Combine(root, ".isolated-wizard-fixture"), Marker, new UTF8Encoding(false));
File.WriteAllText(Path.Combine(root, "graphics-handoff-fixture.json"), JsonSerializer.Serialize(binding, InstallerJson.Options), new UTF8Encoding(false));
string[] invocationArguments = ["--graphics-handoff-smoke", root, "--isolated", "--no-register", "--state-root", Path.Combine(root, "graphics-context"),
    "--target", Path.Combine(root, "installed"), "--scope", "machine", "--trust-key", pin, "--require-signature",
    "--expected-product-id", Product, "--expected-version", "1.0.0", "--expected-sha256", setupHash];
File.WriteAllText(Path.Combine(root, "fixture-invocation.json"), JsonSerializer.Serialize(new { executable = setupPath, arguments = invocationArguments,
    useShellExecute = false, createNoWindow = true, workingDirectory = root,
    environment = new Dictionary<string, string> { ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(root, "primary-bundle") } }, InstallerJson.Options), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(root, "fixture-preparation.json"), JsonSerializer.Serialize(new
{
    result = "PREPARED", primaryInput = new { path = primary, bytes = primaryLease.Length, sha256 = args[1] }, fixture = binding,
    syntheticKeyOnly = true, fixtureExecutableCopies = 1, state = Path.Combine(root, "state"), target = Path.Combine(root, "installed"),
    processesStarted = 0, installationInvoked = false, payloadUploadedBytes = 0, outputsRetained = true
}, InstallerJson.Options), new UTF8Encoding(false));
Console.WriteLine("PREPARED one signed data-only handoff fixture; no process, UI, installation, authentication, or network started.");
return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Fixture preparation stopped: " + error.GetType().Name + ". Preserve existing evidence and review the input paths and hashes.");
    return 1;
}
