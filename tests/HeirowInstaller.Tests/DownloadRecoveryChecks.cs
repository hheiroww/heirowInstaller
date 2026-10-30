using System.Security.Cryptography;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task DownloadRecoveryPages()
    {
        Equal("https://socketjack.com/fruityloops-collab/#downloads", SocketJackDownloadRecovery.GetDownloadPage(new UpdateCompanionConfiguration { ProductId = "FruityLoopsCollab" })?.AbsoluteUri);
        Equal("https://socketjack.com/heirowInstaller/", SocketJackDownloadRecovery.GetDownloadPage(new PackageManifest { Id = "heirowInstaller", Homepage = "https://invalid.example/" })?.AbsoluteUri);
        foreach (var homepage in new[] { null, "", "relative/download", "http://socketjack.com/app", "https://socketjack.com:444/app", "https://socketjack.com.invalid/app", "https://invalid.example/app", "https://user@socketjack.com/app", "file:///C:/setup.exe", "javascript:alert(1)" })
            True(SocketJackDownloadRecovery.GetDownloadPage(new PackageManifest { Id = "test.recovery", Homepage = homepage }) is null);
        foreach (var id in new[] { "", "../FruityLoopsCollab", "FruityLoopsCollab?token=value", "/heirowInstaller", "FruityLoopsCollab#download" })
            True(SocketJackDownloadRecovery.GetDownloadPage(new PackageManifest { Id = id, Homepage = "https://socketjack.com/app" }) is null);
        True(SocketJackDownloadRecovery.GetDownloadPage(new UpdateCompanionConfiguration { ProductId = null! }) is null);
        True(SocketJackDownloadRecovery.GetDownloadPage(new PackageManifest { Id = null!, Homepage = "https://socketjack.com/app" }) is null);
        Equal("https://socketjack.com/app/", SocketJackDownloadRecovery.GetDownloadPage(new PackageManifest { Id = "test.recovery", Homepage = "https://socketjack.com/app/?token=private-placeholder#private-placeholder" })?.AbsoluteUri);
        Equal("https://socketjack.com/app/#downloads", SocketJackDownloadRecovery.GetDownloadPage(new PackageManifest { Id = "test.recovery", Homepage = "https://socketjack.com/app/?campaign=updater#downloads" })?.AbsoluteUri);

        var area = Area("download-recovery");
        using var key = RSA.Create(2048);
        var keyPath = Path.Combine(area, "fixture.private.pem");
        File.WriteAllText(keyPath, key.ExportRSAPrivateKeyPem());
        var manifest = Manifest("test.recovery.signed", "1");
        manifest.Homepage = "https://socketjack.com/recovery-fixture/#downloads";
        var packagePath = Build(area, "signed", manifest, new() { ["app.txt"] = "recovery fixture" }, keyPath);
        var options = Options(area, Path.Combine(area, "app"));
        using (var package = PackageReader.Open(packagePath)) await new InstallerEngine().InstallAsync(package, options);
        var installed = InstallerEngine.ListInstalled(options.StateRoot).Single();
        var configuration = new UpdateCompanionConfiguration { ProductId = manifest.Id, InstallDirectory = installed.InstallDirectory,
            PublisherKeySha256 = installed.PublisherKeySha256!, StateRoot = options.StateRoot!, Scope = installed.Scope, Isolated = true };
        Equal(manifest.Homepage, SocketJackDownloadRecovery.GetDownloadPage(configuration)?.AbsoluteUri);
        configuration.PublisherKeySha256 = new string('f', 64);
        True(SocketJackDownloadRecovery.GetDownloadPage(configuration) is null);
        configuration.PublisherKeySha256 = installed.PublisherKeySha256!;
        configuration.InstallDirectory = Path.Combine(area, "different-app");
        True(SocketJackDownloadRecovery.GetDownloadPage(configuration) is null);
        configuration.InstallDirectory = installed.InstallDirectory;
        File.WriteAllText(installed.PackagePath, "malformed isolated recovery package");
        True(SocketJackDownloadRecovery.GetDownloadPage(configuration) is null);
    }
}
