using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task NativeFileSelectionHandoff()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Keep the owned fixture apphost below CreateProcess's legacy path limit,
        // including when the caller supplies a long task-owned TEMP root.
        var area = Area("nfs"); var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        var native = FindUpdaterCli(); File.Copy(native, Path.Combine(source, "heirowUpdater.exe"));
        File.WriteAllText(Path.Combine(source, "application.exe"), "non-executed owned fixture");
        File.WriteAllText(Path.Combine(source, "value.txt"), "before"); File.WriteAllText(Path.Combine(source, "keep.txt"), "unchanged owned bytes");
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        byte[] envelope = [], selection = []; var posted = false; var negotiated = false;
        var feed = new UpdateFeed { ProductId = "test.native-files", Version = "3", PackageType = "hfiles" };
        await using var server = new LoopbackServer((NetworkRequest request) =>
        {
            if (request.Path.StartsWith("/feed", StringComparison.Ordinal))
            {
                negotiated = request.Path.Contains("updateProtocol=hfiles1") && request.Path.Contains("fromVersion=1");
                return new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options));
            }
            if (request.Path.EndsWith("/manifest.json", StringComparison.Ordinal)) return new(200, envelope);
            if (request.Path.EndsWith("/files.zip", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(request.Body);
                posted = request.Method == "POST" && body.RootElement.GetProperty("paths").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "value.txt" }) &&
                    body.RootElement.GetProperty("targetManifestSha256").GetString() == feed.TargetManifestSha256;
                return new(200, selection);
            }
            return new(404, []);
        });
        string Build(string version)
        {
            var manifest = Manifest(feed.ProductId, version); manifest.EntryPoint = "application.exe"; manifest.UpdateUrl = server.Origin + "feed.json";
            manifest.Features = [new() { Id = "updates", Default = true }];
            manifest.UpdateCompanion = new() { RuntimeMode = "shared", UpdateMode = "files", UpdateProtocol = "hfiles1", CredentialOrigin = server.Origin };
            manifest.Payload = Directory.EnumerateFiles(source).Order().Select(path => new PayloadFile { Path = Path.GetFileName(path), Feature = Path.GetFileName(path) == "heirowUpdater.exe" ? "updates" : null }).ToList();
            var json = Path.Combine(area, "manifest-" + version + ".json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var output = Path.Combine(area, version + ".hinst"); PackageBuilder.Build(json, source, output, new() { SigningKeyPath = keyPath }); return output;
        }
        var before = Build("1"); File.WriteAllText(Path.Combine(source, "value.txt"), "after"); var after = Build("3");
        using (var zip = ZipFile.OpenRead(after))
        {
            byte[] Read(string name) { using var input = zip.GetEntry(name)!.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
            var manifest = Read("manifest.json"); var signature = Read("signature.json");
            envelope = JsonSerializer.SerializeToUtf8Bytes(new { manifestBase64 = Convert.ToBase64String(manifest), signatureBase64 = Convert.ToBase64String(signature) });
            feed.TargetManifestSha256 = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant(); feed.ManifestUrl = server.Origin + "3/manifest.json"; feed.FilesUrl = server.Origin + "3/files.zip";
            using var output = new MemoryStream();
            using (var selected = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var name in new[] { "manifest.json", "signature.json", "payload/value.txt" }) { var entry = selected.CreateEntry(name, CompressionLevel.Optimal); using var targetStream = entry.Open(); targetStream.Write(Read(name)); }
            selection = output.ToArray();
        }
        var options = Options(area, Path.Combine(area, "app"));
        using (var package = PackageReader.Open(before)) await new InstallerEngine().InstallAsync(package, options);
        var installed = InstallerEngine.ListInstalled(options.StateRoot).Single(); var target = UpdaterTarget.FromInstalled(installed, options.StateRoot);
        var profilePath = Path.Combine(installed.InstallDirectory, "heirowUpdater.json"); var profile = ApplicationUpdater.ReadConfiguration(profilePath, true);
        var helper = ApplicationUpdater.ResolveExecutable(profile);
        foreach (var dependency in Directory.EnumerateFiles(Path.GetDirectoryName(native)!, "*", SearchOption.TopDirectoryOnly).Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            File.Copy(dependency, Path.Combine(Path.GetDirectoryName(helper)!, Path.GetFileName(dependency)), true);
        using var owner = Process.Start(new ProcessStartInfo(helper, "help") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var ownerPid = owner.Id; var ownerStart = owner.StartTime.ToUniversalTime().Ticks;
        var ownerOutput = owner.StandardOutput.ReadToEndAsync(); var ownerError = owner.StandardError.ReadToEndAsync(); await owner.WaitForExitAsync(); _ = await ownerOutput;
        True(owner.ExitCode == 0, await ownerError);
        using var updater = new SocketJackUpdater(true, new Uri(server.Origin));
        var update = await updater.CheckAsync(target); True(negotiated && update.Available && target.UpdateProtocol == "hfiles1");
        var launch = await updater.PrepareInstallerHandoffAsync(target, update, ownerPid, ownerStart, new() { Isolated = true, DownloadDirectory = Path.Combine(area, "downloads") });
        Equal("apply-files", launch.ArgumentList[0]); Equal(helper, launch.FileName); True(posted);
        var receiptBefore = PackageSafety.HashFile(installed.ReceiptPath); var stamp = File.GetLastWriteTimeUtc(Path.Combine(installed.InstallDirectory, "keep.txt"));
        async Task<int> Execute(string? option = null, string? value = null)
        {
            var start = new ProcessStartInfo(launch.FileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = launch.WorkingDirectory };
            foreach (var argument in launch.ArgumentList) start.ArgumentList.Add(argument);
            if (option is not null) start.ArgumentList[start.ArgumentList.IndexOf(option) + 1] = value!;
            start.ArgumentList.Add("--no-prompt");
            using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); _ = await output; var diagnostic = await error;
            if (child.ExitCode != 0 && option is null) throw new Exception("Selected-file helper failed: " + diagnostic);
            return child.ExitCode;
        }
        True(await Execute("--target", Path.Combine(area, "unrelated")) != 0); Equal(receiptBefore, PackageSafety.HashFile(installed.ReceiptPath));
        True(await Execute("--target-manifest-sha256", new string('0', 64)) != 0); Equal(receiptBefore, PackageSafety.HashFile(installed.ReceiptPath));
        Equal(0, await Execute());
        Equal("after", File.ReadAllText(Path.Combine(installed.InstallDirectory, "value.txt"))); Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(installed.InstallDirectory, "keep.txt")));
        var updated = InstallerEngine.ListInstalled(options.StateRoot).Single(); Equal("3", updated.Version); True(updated.PackagePath.EndsWith(".hmeta"));
        var configuration = ApplicationUpdater.ReadConfiguration(profilePath, true); Equal("hfiles1", configuration.UpdateProtocol); Equal(helper, ApplicationUpdater.ResolveExecutable(configuration));
    }
}
