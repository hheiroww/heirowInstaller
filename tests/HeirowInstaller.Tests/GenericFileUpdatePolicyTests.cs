using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task GenericFileUpdatePolicy()
    {
        var area = Area("generic-file-policy"); var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RawMsiUpdateProbe", "bin", configuration, "net10.0", "RawMsiUpdateProbe.exe"));
        File.Copy(probe, Path.Combine(source, "heirowUpdater.exe")); File.WriteAllText(Path.Combine(source, "application.exe"), "non-executed fixture");
        var downloads = 0; var feed = new UpdateFeed { ProductId = "test.generic-files", Name = "Generic policy fixture", Version = "2", PackageType = "exe", Sha256 = new string('a', 64), Size = 1 };
        await using var server = new LoopbackServer((NetworkRequest request) =>
        {
            if (request.Path.StartsWith("/feed", StringComparison.Ordinal)) return new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options));
            Interlocked.Increment(ref downloads); return new(200, new byte[] { 0 });
        });
        feed.InstallerUrl = server.Origin + "forbidden-full-setup.exe";
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var manifest = Manifest(feed.ProductId, "1"); manifest.EntryPoint = "application.exe"; manifest.UpdateUrl = server.Origin + "feed.json";
        manifest.Features = [new() { Id = "updates", Default = true }]; manifest.UpdateCompanion = new() { RuntimeMode = "shared", UpdateMode = "files", CredentialOrigin = server.Origin };
        manifest.Payload = [new() { Path = "application.exe" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }];
        var json = Path.Combine(area, "manifest.json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var packagePath = Path.Combine(area, "fixture.hinst"); PackageBuilder.Build(json, source, packagePath, new() { SigningKeyPath = keyPath });
        var install = Options(area, Path.Combine(area, "app"));
        using (var package = PackageReader.Open(packagePath)) await new InstallerEngine().InstallAsync(package, install);
        var product = InstallerEngine.ListInstalled(install.StateRoot).Single(); var target = UpdaterTarget.FromInstalled(product, install.StateRoot); Equal("files", target.UpdateMode);
        var stale = JsonSerializer.Deserialize<InstalledProduct>(JsonSerializer.Serialize(product, InstallerJson.Options), InstallerJson.Options)!; stale.Version = "0";
        await Throws<InvalidDataException>(() => Task.Run(() => UpdaterTarget.FromInstalled(stale, install.StateRoot)));
        using var updater = new SocketJackUpdater(allowLoopback: true); // Generic/default constructor intentionally has no app-specific policy flag.
        await Throws<InvalidDataException>(() => updater.CheckAsync(target));
        var results = await updater.CheckAllAsync(install.StateRoot); True(results.Single().Error is not null && !results.Single().Available);
        var offered = new UpdateCheckResult { Available = true, CurrentVersion = "1", Feed = feed };
        await Throws<InvalidDataException>(() => updater.ApplyAsync(target, offered, new() { Isolated = true, DownloadDirectory = Path.Combine(area, "downloads") }));
        using var self = Process.GetCurrentProcess();
        await Throws<InvalidDataException>(() => updater.PrepareInstallerHandoffAsync(target, offered, self.Id, self.StartTime.ToUniversalTime().Ticks,
            new() { Isolated = true, DownloadDirectory = Path.Combine(area, "downloads") }));
        var cli = new ProcessStartInfo(FindUpdaterCli()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "check", product.Id, "--state-root", install.StateRoot!, "--allow-loopback", "--json", "--status-file", Path.Combine(area, "cli-status.json") }) cli.ArgumentList.Add(argument);
        using var process = Process.Start(cli)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); _ = await output; _ = await error;
        True(process.ExitCode != 0); Equal(0, downloads); True(!Directory.Exists(Path.Combine(area, "downloads")));
        Equal("1", InstallerEngine.ListInstalled(install.StateRoot).Single().Version);
    }
}
