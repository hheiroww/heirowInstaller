using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.BootstrapContract;
using HeirowInstaller.Core;

internal static class DetachedCoordinatorChecks
{
    internal static async Task Run(string root, RSA key, string generic, Action<bool, string> check)
    {
        var area = Path.Combine(root, "detached-coordinator");
        var source = Path.Combine(area, "host-source"); Directory.CreateDirectory(source);
        var signingKey = Path.Combine(area, "synthetic-key.pem"); await File.WriteAllTextAsync(signingKey, key.ExportPkcs8PrivateKeyPem());
        var pin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        async Task<string> Build(string id, string payload, string output)
        {
            var legal = LegalDocumentRules.CreateDraft(id);
            legal.PrivacyPolicy!.Content = "Synthetic local test only.";
            legal.TermsAndConditions!.Content = "Synthetic local test only.";
            var manifest = new PackageManifest { FormatVersion = 4, Id = id, Name = id, Publisher = "Fixture", Version = "1.0.0", Scope = "user", TargetRuntime = "win-x64",
                EntryPoint = "heirowInstaller.exe", LegalDocuments = LegalDocumentRules.PrepareForCreation(id, legal) };
            var path = output + ".json"; await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            PackageBuilder.Build(path, payload, output, new() { SigningKeyPath = signingKey }); return output;
        }
        await File.WriteAllTextAsync(Path.Combine(source, "heirowInstaller.exe"), "Exact signed desktop fixture");
        var host = await Build("fixture.maintenance", source, Path.Combine(area, "host.hinst"));
        var digest = PackageSafety.HashFile(host);
        var cache = Path.Combine(area, digest.ToLowerInvariant()); var files = Path.Combine(cache, "files"); Directory.CreateDirectory(files);
        File.Copy(host, Path.Combine(cache, "package.hinst"));
        var image = Path.Combine(files, "heirowInstaller.exe"); File.Copy(Path.Combine(source, "heirowInstaller.exe"), image);
        var descriptor = new { formatVersion = 1, hostKind = "installer", hostEntryPoint = "heirowInstaller.exe", hostPackageId = "fixture.maintenance", publisherKeySha256 = pin,
            packages = new[] { new { id = "fixture.maintenance", version = "1.0.0", targetRuntime = "win-x64", size = new FileInfo(host).Length, sha256 = digest } } };
        var installed = Path.Combine(area, "installed"); Directory.CreateDirectory(installed);
        var native = Path.Combine(installed, "heirowInstaller.exe");
        await WebBootstrapBindings.WriteAsync(generic, JsonSerializer.SerializeToUtf8Bytes(descriptor), native, key);
        var authoring = await Build("heirowinstaller", installed, Path.Combine(area, "authoring.hinst"));
        var product = new InstalledProduct { Id = "heirowinstaller", Version = "1.0.0", Scope = "user", InstallDirectory = installed, PackagePath = authoring, PublisherKeySha256 = pin };
        var method = typeof(BootstrapWorkerPromotion).GetMethod("FindDetachedCoordinator", BindingFlags.NonPublic | BindingFlags.Static)!;
        string? Find(string path, params InstalledProduct[] products) => (string?)method.Invoke(null, [path, products]);
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (TargetInvocationException error) when (error.InnerException is CryptographicException or InvalidDataException) { check(true, message); return; }
            throw new InvalidOperationException(message);
        }
        check(Find(image, product) == native, "a directly opened signed shared desktop host recovers its exact installed native authoring coordinator");
        check(Find(image) is null, "a loose desktop host without an installed signed authoring application has no elevation fallback");
        var foreignImage = Path.Combine(area, "foreign", "heirowInstaller.exe"); Directory.CreateDirectory(Path.GetDirectoryName(foreignImage)!); File.Copy(image, foreignImage);
        check(Find(foreignImage, product) is null, "an unrelated executable path cannot borrow the installed native coordinator");
        File.AppendAllText(image, "changed");
        Reject(() => Find(image, product), "a changed shared desktop executable is rejected before starting a native administrator process");
        File.Copy(Path.Combine(source, "heirowInstaller.exe"), image, true);
        File.AppendAllText(native, "changed");
        Reject(() => Find(image, product), "a changed installed native carrier is rejected against its signed application payload");
    }
}
