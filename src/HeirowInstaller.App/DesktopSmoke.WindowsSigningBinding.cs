using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal static partial class DesktopSmoke
{
    private static void VerifyWindowsSigningPackageBinding(string directory, Action<bool, string> require)
    {
        var root = Path.Combine(directory, "unsigned-host-binding");
        Directory.CreateDirectory(root);
        const string productId = "heirowInstaller.desktop-fixture";

        string Build(string label, string id, string content, string? key = null)
        {
            var source = Path.Combine(root, label);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "application.txt"), content);
            var manifest = new PackageManifest
            {
                Id = id, Name = "Unsigned host binding fixture", Version = "1.0", Publisher = "Fixture",
                LegalDocuments = LegalSmokeDocuments()
            };
            var definition = Path.Combine(root, label + ".json");
            File.WriteAllText(definition, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var package = Path.Combine(root, label + ".hinst");
            PackageBuilder.Build(definition, source, package, new() { SigningKeyPath = key });
            return package;
        }

        var original = Build("original", productId, "original application payload");
        var launcher = Path.Combine(root, "launcher.exe");
        File.WriteAllBytes(launcher, new byte[512]);
        var setup = Path.Combine(root, "Setup.exe");
        BundleBuilder.Build(launcher, original, setup);
        using var own = PackageReader.Open(setup);
        var expectedPackageHash = own.ComputePackageSha256();
        using (var raw = PackageReader.Open(original))
        {
            require(raw.ComputePackageSha256() == expectedPackageHash && PackageSafety.HashFile(setup) != expectedPackageHash,
                "Worker package binding hashes exact archive bytes independently of the enclosing Setup executable");
            ElevatedInstallWorker.VerifyUnsignedHostPackage(productId, expectedPackageHash, raw);
        }
        require(true, "Unsigned bundled hosts accept their unchanged raw cached package for maintenance");
        ElevatedInstallWorker.VerifyUnsignedHostPackage(productId, expectedPackageHash, own);
        require(true, "Unsigned bundled hosts continue to accept their original embedded package for initial installation");

        void Reject<T>(string package, string message) where T : Exception
        {
            using var reader = PackageReader.Open(package);
            var rejected = false;
            try { ElevatedInstallWorker.VerifyUnsignedHostPackage(productId, expectedPackageHash, reader); }
            catch (T) { rejected = true; }
            require(rejected, message);
        }

        Reject<CryptographicException>(Build("changed", productId, "changed application payload"),
            "Unsigned bundled hosts reject a different payload for the same application");
        Reject<InvalidDataException>(Build("foreign", "heirowInstaller.foreign-binding-fixture", "original application payload"),
            "Unsigned bundled hosts reject a different application identity");
        using var key = RSA.Create(2048);
        var keyPath = Path.Combine(root, "fixture.private.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        Reject<CryptographicException>(Build("signed", productId, "original application payload", keyPath),
            "Unsigned bundled hosts reject substitution of a signed package instead of their exact original archive");

        var repacked = Path.Combine(root, "repacked.hinst");
        using (var source = ZipFile.OpenRead(original))
        using (var destination = ZipFile.Open(repacked, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var copy = destination.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                copy.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = entry.Open();
                using var output = copy.Open();
                input.CopyTo(output);
            }
        }
        Reject<CryptographicException>(repacked,
            "Unsigned bundled hosts reject repacked ZIP bytes even when all parsed package content remains equivalent");
    }
}
