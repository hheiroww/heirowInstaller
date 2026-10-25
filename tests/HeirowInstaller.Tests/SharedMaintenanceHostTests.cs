using System.Security.Cryptography;
using HeirowInstaller.Core;

internal sealed partial class AcceptanceSuite
{
    private async Task SharedMaintenanceHosts()
    {
        var area = Area("shared-maintenance-hosts"); var state = Path.Combine(area, "state");
        True(SharedInstallerHost.Provision(FindUpdaterCli(), "user", state, isolated: true) is null);
        True(!Directory.Exists(SharedInstallerHost.Root("user", state, isolated: true)));
        True(SharedInstallerHost.Root("user", state, false) != SharedInstallerHost.Root("machine", state, false));
        var productionPoolExisted = Directory.Exists(SharedInstallerHost.Root("user", state, false));
        await Throws<InvalidDataException>(() => Task.Run(() => SharedInstallerHost.Provision(FindUpdaterCli(), "user", state, isolated: false)));
        Equal(productionPoolExisted, Directory.Exists(SharedInstallerHost.Root("user", state, false)));
        var runtime = Environment.GetEnvironmentVariable("HEIROWINSTALLER_TEST_EMBEDDED_GUI_RUNTIME");
        if (string.IsNullOrEmpty(runtime)) return; // Release acceptance supplies the actual immutable self-contained GUI build.
        runtime = Path.GetFullPath(runtime); StandaloneRuntimeInspector.RequireEmbeddedRuntime(runtime);
        var hash = PackageSafety.HashFile(runtime); var length = new FileInfo(runtime).Length;
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var one = Manifest("test.maintenance-one", "1"); one.Payload = [new() { Path = "one.txt" }];
        var two = Manifest("test.maintenance-two", "1"); two.Payload = [new() { Path = "two.txt" }];
        var packageOne = Build(area, "one", one, new() { ["one.txt"] = "first application payload must stay out of the pool" }, keyPath);
        var packageTwo = Build(area, "two", two, new() { ["two.txt"] = "different application payload must stay out of the pool" }, keyPath);
        var bundleOne = Path.Combine(area, "Setup-one.exe"); var bundleTwo = Path.Combine(area, "Setup-two.exe");
        BundleBuilder.Build(runtime, packageOne, bundleOne); BundleBuilder.Build(runtime, packageTwo, bundleTwo);
        var pooledOne = SharedInstallerHost.Provision(bundleOne, "user", state, true)!;
        var pooledTwo = SharedInstallerHost.Provision(bundleTwo, "user", state, true)!;
        Equal(pooledOne, pooledTwo); Equal(length, new FileInfo(pooledOne).Length); Equal(hash, PackageSafety.HashFile(pooledOne));
        Equal(1, Directory.EnumerateFiles(SharedInstallerHost.Root("user", state, true), "heirowInstaller.exe", SearchOption.AllDirectories).Count());
        True(!BundleBuilder.HasEmbeddedPackage(pooledOne)); Equal(pooledOne, SharedInstallerHost.Validate(pooledOne, "user", state, true));
        File.AppendAllText(pooledOne, "tampering");
        await Throws<CryptographicException>(() => Task.Run(() => SharedInstallerHost.Validate(pooledOne, "user", state, true)));
        File.Copy(runtime, pooledOne, overwrite: true); Equal(hash, PackageSafety.HashFile(pooledOne));
        await Throws<InvalidDataException>(() => Task.Run(() => SharedInstallerHost.Validate(bundleOne, "user", state, true)));
        Equal(hash, PackageSafety.HashFile(runtime)); // The frozen GUI input is read-only throughout.
    }
}
