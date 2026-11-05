using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Supply one fresh owned output directory.");
var root = Path.GetFullPath(args[0]); if (Directory.Exists(root)) throw new IOException("Use a fresh trust fixture root."); Directory.CreateDirectory(root);
int assertions = 0, build = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); assertions++; Console.WriteLine("PASS " + message); }
void Reject(Action action, string message) { try { action(); } catch (CryptographicException) { Check(true, message); return; } throw new InvalidOperationException(message); }
using var publisher = RSA.Create(2048); using var attacker = RSA.Create(2048);
string Pin(RSA key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
var trusted = Pin(publisher); var forged = Pin(attacker);
string Package(string id, string version, RSA key, string? outerPin = null, string? componentPin = null, string moduleId = "SocketJackSecurityLoader", bool duplicateModule = false)
{
    string area = Path.Combine(root, "package-" + ++build), source = Path.Combine(area, "source"), protectedFiles = Path.Combine(source, "SecurityLoader"); Directory.CreateDirectory(protectedFiles);
    File.WriteAllText(Path.Combine(source, "app.exe"), "Synthetic protected application " + version);
    File.WriteAllText(Path.Combine(protectedFiles, "component.sjsl"), "Synthetic sealed bytes; never loaded");
    foreach (var file in new[] { "bootstrap.json", "bootstrap.sig", "bootstrap.pub" }) File.WriteAllText(Path.Combine(protectedFiles, file), "Synthetic metadata; never activated");
    var configuration = JsonSerializer.SerializeToNode(new { formatVersion = 1, publisherKeySha256 = Pin(key), componentVersion = version,
        components = new[] { new { id = "fixture.component", path = "SecurityLoader/component.sjsl", componentDigest = PackageSafety.HashFile(Path.Combine(protectedFiles, "component.sjsl")) } } })!.AsObject();
    if (outerPin is not null) configuration["outerPublisherPin"] = outerPin;
    if (componentPin is not null) configuration["publisherKeySha256"] = componentPin;
    var manifest = new PackageManifest { Id = id, Name = "Protected trust fixture", Publisher = "SocketJack", Version = version, EntryPoint = "app.exe",
        TargetRuntime = InstallerPlatform.RuntimeIdentifier, TargetPlatform = InstallerPlatform.Current, UpdateUrl = "https://socketjack.com/installers/" + id + "/update.json", LegalDocuments = LegalFixture.For(id) };
    manifest.Modules[moduleId] = JsonSerializer.SerializeToElement(configuration);
    if (duplicateModule) manifest.Modules["socketjacksecurityloader"] = JsonSerializer.SerializeToElement(configuration);
    var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    var json = Path.Combine(area, "manifest.json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
    var output = Path.Combine(area, "package.hinst"); PackageBuilder.Build(json, source, output, new() { SigningKeyPath = keyPath }); return output;
}
InstallOptions Options(string name) => new() { Isolated = true, StateRoot = Path.Combine(root, name, "state"), TargetDirectory = Path.Combine(root, name, "application"), RegisterProduct = false };
void NoEffects(InstallOptions options, string message) => Check(!Directory.Exists(options.StateRoot) && !Directory.Exists(options.TargetDirectory), message);
var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["heirowSnap"] = trusted };
var registry = new RegisteredPublisherTrust(map); map["heirowSnap"] = forged;
var engine = new InstallerEngine(null, new WindowsNativeMigrationBackend(), registry);
var official = Package("HEIROWSNAP", "1", publisher, trusted);
using (var package = PackageReader.Open(official))
{
    var options = Options("fresh"); var plan = engine.Plan(package, options);
    Check(plan.Manifest.Id == "HEIROWSNAP" && options.TrustedPublisherKeySha256 == trusted && options.RequireSignature, "fresh protected setup resolves immutable case-insensitive registered trust and retains the verified pin for handoff");
    NoEffects(options, "fresh trust resolution plans without creating installation or state directories");
    var explicitWrong = Options("explicit-wrong"); explicitWrong.TrustedPublisherKeySha256 = forged;
    Reject(() => engine.Plan(package, explicitWrong), "a wrong explicit publisher pin cannot be replaced by a registered fallback");
    Check(explicitWrong.TrustedPublisherKeySha256 == forged, "failed explicit trust remains unchanged"); NoEffects(explicitWrong, "wrong explicit trust fails before filesystem effects");
}
var fakeOfficial = Package("heirowSnap", "2", attacker, forged);
using (var package = PackageReader.Open(fakeOfficial))
{
    var options = Options("forged-official"); Reject(() => engine.Plan(package, options), "official product identity and a self-declared outer pin cannot authorize an attacker signing key");
    Check(options.TrustedPublisherKeySha256 is null && !options.RequireSignature, "rejected signatures cannot promote an unverified effective pin"); NoEffects(options, "forged official setup fails before filesystem effects");
}
using (var package = PackageReader.Open(Package("heirowSnap", "2", attacker, trusted)))
{
    var options = Options("forged-delegation"); Reject(() => engine.Plan(package, options), "claiming the trusted outer pin inside attacker-signed metadata grants no trust"); NoEffects(options, "forged delegation causes no filesystem effects");
}
using (var package = PackageReader.Open(Package("heirowSnap", "2", publisher, trusted, forged)))
{
    var options = Options("delegated-component"); Check(engine.Plan(package, options).Manifest.Version == "2" && options.TrustedPublisherKeySha256 == trusted,
        "verified registered outer publisher can retain explicit signed delegation to a distinct protected component key"); NoEffects(options, "delegated component planning has no filesystem effects");
}
using (var package = PackageReader.Open(Package("customer.custom", "1", publisher, trusted)))
{
    var options = Options("unknown"); Reject(() => engine.Plan(package, options), "unknown protected publishers still require their own independent pin despite SocketJack labels and update URLs"); NoEffects(options, "unknown publisher fallback causes no filesystem effects");
    options.TrustedPublisherKeySha256 = trusted; Check(engine.Plan(package, options).Manifest.Id == "customer.custom", "custom publishers retain explicit-key support without using SocketJack's key");
}
foreach (var moduleId in new[] { "socketjacksecurityloader", "sOcKeTjAcKsEcUrItYlOaDeR" })
{
    using (var package = PackageReader.Open(Package("heirowSnap", "2", publisher, trusted, moduleId: moduleId)))
    {
        var options = Options("cased-trusted-" + build); Check(engine.Plan(package, options).Manifest.Version == "2" && options.TrustedPublisherKeySha256 == trusted,
            "protected module casing uses the same independent registered trust: " + moduleId); NoEffects(options, "cased trusted planning has no filesystem effects");
    }
    using (var package = PackageReader.Open(Package("heirowSnap", "2", attacker, forged, moduleId: moduleId)))
    {
        var options = Options("cased-forged-" + build); Reject(() => engine.Plan(package, options), "protected module casing cannot authorize a forged official publisher: " + moduleId);
        Check(options.TrustedPublisherKeySha256 is null && !options.RequireSignature, "cased forged metadata cannot promote an effective pin"); NoEffects(options, "cased forged setup has no filesystem effects");
    }
    using (var package = PackageReader.Open(Package("customer.custom", "1", attacker, forged, moduleId: moduleId)))
    {
        var options = Options("cased-unknown-" + build); Reject(() => engine.Plan(package, options), "protected module casing cannot bypass the required independent custom publisher pin: " + moduleId);
        NoEffects(options, "cased unknown publisher setup has no filesystem effects");
    }
}
try { _ = Package("heirowSnap", "2", publisher, trusted, duplicateModule: true); throw new InvalidOperationException("Case-insensitive duplicate module IDs were accepted."); }
catch (InvalidDataException exception) when (exception.Message.StartsWith("Duplicate installer module:", StringComparison.Ordinal))
{ Check(true, "case-insensitive duplicate installer module IDs are rejected during package validation"); }
Check(RegisteredPublisherTrust.Default.TryResolve("HEIROWSNAP", out var officialPin) && officialPin == "eab5ba01a94557088fd7b23d9569b2f289368964e9d4922a79970ecb13409e22", "embedded reviewed identity and public PEM agree on the real SocketJack anchor");
Check(!RegisteredPublisherTrust.Default.TryResolve("customer.custom", out _), "embedded official identity does not claim arbitrary customer products");
using (var package = PackageReader.Open(official))
{
    var differentRegistry = new InstallerEngine(null, new WindowsNativeMigrationBackend(), new RegisteredPublisherTrust(new Dictionary<string, string> { ["heirowSnap"] = forged }));
    var options = Options("explicit-wins"); options.TrustedPublisherKeySha256 = trusted;
    Check(differentRegistry.Plan(package, options).Manifest.Version == "1", "a correct explicit publisher pin retains precedence over a different registered key");
}
string previousDirectory = Environment.CurrentDirectory;
try
{
    var adjacent = Path.Combine(root, "adjacent", "publishers"); Directory.CreateDirectory(adjacent); File.WriteAllText(Path.Combine(adjacent, "socketjack.com.json"), JsonSerializer.Serialize(new { publicKeySha256 = forged }));
    Environment.CurrentDirectory = Path.GetDirectoryName(adjacent)!;
    using var package = PackageReader.Open(fakeOfficial); var options = Options("adjacent-registry");
    Reject(() => new InstallerEngine().Plan(package, options), "editable adjacent and working-directory publisher files cannot alter installer-owned trust"); NoEffects(options, "adjacent forged registry causes no filesystem effects");
}
finally { Environment.CurrentDirectory = previousDirectory; }
using (var package = PackageReader.Open(Package("heirowSnap", "1", publisher, trusted)))
{
    var installed = Options("installed"); installed.TrustedPublisherKeySha256 = trusted; installed.RequireSignature = true;
    await engine.InstallAsync(package, installed);
    var product = InstallerEngine.ListInstalled(installed.StateRoot).Single(); var receiptHash = PackageSafety.HashFile(product.ReceiptPath); var appHash = PackageSafety.HashFile(Path.Combine(product.InstallDirectory, "app.exe"));
    using var next = PackageReader.Open(fakeOfficial); var options = Options("installed");
    var changedRegistry = new InstallerEngine(null, new WindowsNativeMigrationBackend(), new RegisteredPublisherTrust(new Dictionary<string, string> { ["heirowSnap"] = forged }));
    Reject(() => changedRegistry.Plan(next, options), "an installed receipt's publisher pin wins over a different registered fallback");
    Check(PackageSafety.HashFile(product.ReceiptPath) == receiptHash && PackageSafety.HashFile(Path.Combine(product.InstallDirectory, "app.exe")) == appHash, "rejected publisher rotation leaves the trusted receipt and owned application unchanged");
}
Check(typeof(InstallerEngine).GetConstructor([typeof(IEnumerable<IInstallerModule>)]) is not null && typeof(InstallerEngine).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(IEnumerable<IInstallerModule>), typeof(INativeMigrationBackend)], null) is not null,
    "existing public and internal installer constructor signatures remain available");
await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { passed = true, assertions, isolated = true, registeredTrustVerified = true, usedProductionPrivateKey = false, credentialedElevationTested = false, retained = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {assertions} registered publisher trust checks. Retained outputs: {root}");
