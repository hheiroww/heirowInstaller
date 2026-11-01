using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

internal static class SignedMaintenanceHostChecks
{
    // Structural fixtures exercise byte preservation without issuing a signing request or
    // claiming the synthetic WIN_CERTIFICATE table establishes Windows publisher trust.
    internal static void Run(Action<bool, string> check, string root)
    {
        var directory = Path.Combine(root, "signed-maintenance-hosts");
        Directory.CreateDirectory(directory);
        var state = Path.Combine(directory, "state");
        var launcher = Path.Combine(directory, "launcher.exe");
        CreateRuntimeFixture(launcher);
        StandaloneRuntimeInspector.RequireEmbeddedRuntime(launcher);
        var launcherHash = PackageSafety.HashFile(launcher);

        string Bundle(string id, string content)
        {
            var source = Path.Combine(directory, id);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "application.txt"), content);
            var manifest = new PackageManifest
            {
                Id = id, Name = "Maintenance signing fixture", Version = "1.0", Publisher = "Fixture",
                LegalDocuments = new()
                {
                    PrivacyPolicy = new() { Url = "https://example.invalid/privacy", Content = "This isolated fixture does not transmit user information." },
                    TermsAndConditions = new() { Url = "https://example.invalid/terms", Content = "This package is an isolated automated verification fixture." }
                }
            };
            var manifestPath = Path.Combine(directory, id + ".json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var package = Path.Combine(directory, id + ".hinst");
            PackageBuilder.Build(manifestPath, source, package);
            var setup = Path.Combine(directory, id + ".exe");
            BundleBuilder.Build(launcher, package, setup);
            return setup;
        }

        var unsignedOne = Bundle("maintenance.one", "first application payload");
        var unsignedTwo = Bundle("maintenance.two", "second application's different payload");
        var pooledUnsignedOne = SharedInstallerHost.Provision(unsignedOne, "user", state, isolated: true)!;
        var pooledUnsignedTwo = SharedInstallerHost.Provision(unsignedTwo, "user", state, isolated: true)!;
        check(pooledUnsignedOne == pooledUnsignedTwo && PackageSafety.HashFile(pooledUnsignedOne) == launcherHash,
            "Unsigned installers retain common-prefix pooling across different application payloads");
        check(!PackageReader.HasEmbeddedPackage(pooledUnsignedOne), "Unsigned shared maintenance hosts omit the application overlay");

        var signedOne = Path.Combine(directory, "signed-one.exe");
        var signedTwo = Path.Combine(directory, "signed-two.exe");
        AddCertificateTable(unsignedOne, signedOne);
        AddCertificateTable(unsignedTwo, signedTwo);
        check(BundleBuilder.HasAuthenticodeTable(signedOne) && PackageReader.HasEmbeddedPackage(signedOne),
            "The maintenance fixture has a trailing certificate table and a separately readable embedded package");
        var signedHash = PackageSafety.HashFile(signedOne);
        var pooledSignedOne = SharedInstallerHost.Provision(signedOne, "user", state, isolated: true)!;
        var pooledSignedTwo = SharedInstallerHost.Provision(signedTwo, "user", state, isolated: true)!;
        check(File.ReadAllBytes(pooledSignedOne).SequenceEqual(File.ReadAllBytes(signedOne)),
            "Maintenance caching preserves every signed installer byte, including payload, certificate and PE directory");
        check(BundleBuilder.HasAuthenticodeTable(pooledSignedOne) && PackageReader.HasEmbeddedPackage(pooledSignedOne),
            "Signed maintenance media keeps its certificate table and package overlay");
        check(pooledSignedOne != pooledSignedTwo && pooledSignedOne != pooledUnsignedOne,
            "Different signed installers receive distinct immutable hosts rather than an invalid shared prefix");
        check(SharedInstallerHost.Provision(signedOne, "user", state, isolated: true) == pooledSignedOne,
            "Repeated use of identical signed media reuses its full immutable maintenance host");
        check(SharedInstallerHost.Validate(pooledSignedOne, "user", state, isolated: true) == pooledSignedOne,
            "Full signed maintenance media satisfies the existing runtime and receipt content binding");
        check(PackageSafety.HashFile(signedOne) == signedHash && PackageSafety.HashFile(launcher) == launcherHash,
            "Provisioning leaves both original signed media and the clean launcher unchanged");

        using (var cached = new FileStream(pooledSignedOne, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            cached.Position = cached.Length - 1;
            var original = cached.ReadByte();
            cached.Position--;
            cached.WriteByte((byte)(original ^ 1));
        }
        var rejected = false;
        try { SharedInstallerHost.Validate(pooledSignedOne, "user", state, isolated: true); }
        catch (CryptographicException) { rejected = true; }
        check(rejected, "Changing cached certificate bytes fails the immutable maintenance binding");
    }

    private static void CreateRuntimeFixture(string path)
    {
        var bytes = new byte[4096];
        using var stream = new MemoryStream(bytes, writable: true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0x5a4d);
        stream.Position = 0x3c; writer.Write(0x80);
        stream.Position = 0x80; writer.Write(0x00004550);
        stream.Position = 0x80 + 20; writer.Write((ushort)240);
        stream.Position = 0x80 + 24; writer.Write((ushort)0x20b);
        stream.Position = 0x80 + 24 + 108; writer.Write(16);
        stream.Position = 504; writer.Write(2048L);
        writer.Write(Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE"));
        var configuration = Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"includedFrameworks\":[]}}");
        stream.Position = 1024; writer.Write(configuration);
        stream.Position = 2048;
        writer.Write(6u); writer.Write(0u); writer.Write(2); writer.Write("maintenance-fixture");
        writer.Write(0L); writer.Write(0L); writer.Write(1024L); writer.Write((long)configuration.Length); writer.Write(0UL);
        foreach (var name in new[] { "System.Private.CoreLib.dll", "coreclr.dll" })
        {
            writer.Write(800L); writer.Write(1L); writer.Write(0L); writer.Write((byte)0); writer.Write(name);
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void AddCertificateTable(string source, string output)
    {
        var original = File.ReadAllBytes(source);
        var certificateOffset = (original.Length + 7) & ~7;
        var bytes = new byte[certificateOffset + 32];
        original.CopyTo(bytes, 0);
        using var stream = new MemoryStream(bytes, writable: true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 0x80 + 24 + 112 + 32;
        writer.Write((uint)certificateOffset); writer.Write(32u);
        stream.Position = certificateOffset;
        writer.Write(32u); writer.Write((ushort)0x200); writer.Write((ushort)2);
        writer.Write(Encoding.ASCII.GetBytes("STRUCTURAL-TEST-CERT"));
        File.WriteAllBytes(output, bytes);
    }
}
