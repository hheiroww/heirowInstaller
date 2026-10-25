using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task NativeIncrementalHandoff()
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = Area("native-incremental-handoff"); var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        var native = FindUpdaterCli(); File.Copy(native, Path.Combine(source, "heirowUpdater.exe"));
        File.WriteAllText(Path.Combine(source, "application.exe"), "non-executed app fixture");
        File.WriteAllText(Path.Combine(source, "value.txt"), "before"); File.WriteAllText(Path.Combine(source, "unchanged.txt"), "preserve bytes and timestamp");
        File.WriteAllText(Path.Combine(source, "obsolete.txt"), "owned obsolete file");
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        byte[] data = [];
        var feed = new UpdateFeed { ProductId = "test.native-incremental", Name = "Owned native file-update fixture", Version = "2", PackageType = "hupd" };
        await using var server = new LoopbackServer((NetworkRequest request) => request.Path.StartsWith("/feed", StringComparison.Ordinal)
            ? new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options)) : new(200, data));
        string Package(string version)
        {
            var manifest = Manifest(feed.ProductId, version); manifest.EntryPoint = "application.exe"; manifest.UpdateUrl = server.Origin + "feed.json";
            manifest.Features = [new() { Id = "updates", Default = true }];
            manifest.UpdateCompanion = new() { RuntimeMode = "shared", UpdateMode = "files", CredentialOrigin = server.Origin };
            manifest.Payload = Directory.EnumerateFiles(source).Select(path => new PayloadFile { Path = Path.GetFileName(path), Feature = Path.GetFileName(path) == "heirowUpdater.exe" ? "updates" : null }).ToList();
            var json = Path.Combine(area, "manifest-" + version + ".json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            var package = Path.Combine(area, version + ".hinst"); PackageBuilder.Build(json, source, package, new() { SigningKeyPath = keyPath }); return package;
        }
        var before = Package("1");
        File.WriteAllText(Path.Combine(source, "value.txt"), "after"); File.Delete(Path.Combine(source, "obsolete.txt")); File.WriteAllText(Path.Combine(source, "created.txt"), "new owned file");
        var after = Package("2"); var deltaPath = Path.Combine(area, "1-to-2.hupd");
        var transition = IncrementalUpdateBuilder.Build(before, after, deltaPath, new() { SigningKeyPath = keyPath }); data = File.ReadAllBytes(deltaPath);
        feed.BaseVersion = transition.FromVersion; feed.BaseManifestSha256 = transition.BaseManifestSha256; feed.TargetManifestSha256 = transition.TargetManifestSha256;
        feed.Sha256 = PackageSafety.HashFile(deltaPath); feed.Size = data.Length; feed.InstallerUrl = server.Origin + "delta.hupd";
        True(!transition.Changes.Any(x => x.Path == "heirowUpdater.exe"));
        var target = Path.Combine(area, "app"); var options = Options(area, target);
        using (var package = PackageReader.Open(before)) await new InstallerEngine().InstallAsync(package, options);
        var profilePath = Path.Combine(target, "heirowUpdater.json"); var profile = ApplicationUpdater.ReadConfiguration(profilePath, true);
        var helper = ApplicationUpdater.ResolveExecutable(profile); var helperDirectory = Path.GetDirectoryName(helper)!;
        // The production runtime is one self-contained EXE. This framework-dependent compiled test host needs only its own known build dependencies.
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(native)!, "*", SearchOption.TopDirectoryOnly).Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            File.Copy(file, Path.Combine(helperDirectory, Path.GetFileName(file)), overwrite: true);
        using var sourceOwner = Process.Start(new ProcessStartInfo(helper, "help") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var ownerPid = sourceOwner.Id; var ownerStart = sourceOwner.StartTime.ToUniversalTime().Ticks;
        var sourceOut = sourceOwner.StandardOutput.ReadToEndAsync(); var sourceError = sourceOwner.StandardError.ReadToEndAsync();
        await sourceOwner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); _ = await sourceOut;
        var sourceDiagnostic = await sourceError;
        True(sourceOwner.ExitCode == 0, "Owned shared host help failed: " + sourceDiagnostic);
        using var updater = new SocketJackUpdater(true, new Uri(server.Origin), requireIncrementalUpdates: true);
        var installed = InstallerEngine.ListInstalled(options.StateRoot).Single(); var updaterTarget = UpdaterTarget.FromInstalled(installed, options.StateRoot);
        var update = await updater.CheckAsync(updaterTarget); True(update.Available && update.Feed.PackageType == "hupd");
        var start = await updater.PrepareInstallerHandoffAsync(updaterTarget, update, ownerPid, ownerStart,
            new() { Isolated = true, DownloadDirectory = Path.Combine(area, "downloads") });
        Equal(helper, start.FileName); Equal("apply-incremental", start.ArgumentList[0]); start.ArgumentList.Add("--no-prompt");
        var feedback = new List<string>();
        using (var lifecycle = await ApplicationUpdater.RunAsync(profilePath, null, null, true, new()
        {
            AllowLoopback = true, DownloadDirectory = Path.Combine(area, "lifecycle-downloads"),
            ManualStatusChangedAsync = (result, _) => { feedback.Add(result.Status); return Task.CompletedTask; }
        }, (_, _) => Task.FromResult(ApplicationUpdateChoice.UpdateNow)))
        {
            True(lifecycle.Handoff is not null && lifecycle.Status == "update-staged");
            True(!feedback.Contains("update-staged"), "A fresh handoff must launch without a second already-prepared information dialog.");
        }
        var unchangedTicks = File.GetLastWriteTimeUtc(Path.Combine(target, "unchanged.txt")).Ticks;
        var receiptHash = PackageSafety.HashFile(installed.ReceiptPath);
        ProcessStartInfo Clone(string? option = null, string? value = null)
        {
            var copy = new ProcessStartInfo(start.FileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = start.WorkingDirectory };
            foreach (var argument in start.ArgumentList) copy.ArgumentList.Add(argument);
            if (option is not null) { var index = copy.ArgumentList.IndexOf(option); copy.ArgumentList[index + 1] = value!; }
            ApplicationUpdater.SetSessionEnvironment(copy, null, null, replace: false); return copy;
        }
        var lastDiagnostic = "";
        async Task<int> Execute(ProcessStartInfo launch)
        {
            using var process = Process.Start(launch) ?? throw new InvalidOperationException("Could not start owned incremental helper.");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); _ = await output; lastDiagnostic = await error;
            var statusIndex = launch.ArgumentList.IndexOf("--status-file");
            if (process.ExitCode != 0 && statusIndex >= 0 && File.Exists(launch.ArgumentList[statusIndex + 1]))
            {
                using var status = JsonDocument.Parse(File.ReadAllText(launch.ArgumentList[statusIndex + 1]));
                if (status.RootElement.TryGetProperty("error", out var message)) lastDiagnostic = message.GetString() ?? "";
            }
            return process.ExitCode;
        }
        void StillBefore()
        {
            Equal("before", File.ReadAllText(Path.Combine(target, "value.txt"))); Equal(receiptHash, PackageSafety.HashFile(installed.ReceiptPath));
            Equal(unchangedTicks, File.GetLastWriteTimeUtc(Path.Combine(target, "unchanged.txt")).Ticks); True(!File.Exists(Path.Combine(target, "created.txt")));
        }
        True(await Execute(Clone("--target", Path.Combine(area, "unrelated"))) != 0); StillBefore();
        True(await Execute(Clone("--sha256", new string('0', 64))) != 0); StillBefore();
        True(await Execute(Clone("--base-version", "0")) != 0); StillBefore();
        True(await Execute(Clone("--base-manifest-sha256", new string('0', 64))) != 0); StillBefore();
        var download = start.ArgumentList[1]; var original = File.ReadAllBytes(download); File.AppendAllText(download, "tampering");
        True(await Execute(Clone()) != 0); StillBefore(); File.WriteAllBytes(download, original);
        True(await Execute(Clone()) == 0, "Owned file-update helper failed: " + lastDiagnostic);
        Equal("after", File.ReadAllText(Path.Combine(target, "value.txt"))); Equal("new owned file", File.ReadAllText(Path.Combine(target, "created.txt")));
        True(!File.Exists(Path.Combine(target, "obsolete.txt"))); Equal(unchangedTicks, File.GetLastWriteTimeUtc(Path.Combine(target, "unchanged.txt")).Ticks);
        Equal("2", InstallerEngine.ListInstalled(options.StateRoot).Single().Version);
        var updated = ApplicationUpdater.ReadConfiguration(profilePath, true); Equal(helper, ApplicationUpdater.ResolveExecutable(updated));
        True(!File.Exists(Path.Combine(target, "heirowUpdater.exe")));
        var status = start.ArgumentList[start.ArgumentList.IndexOf("--status-file") + 1];
        using var result = JsonDocument.Parse(File.ReadAllText(status)); Equal("updated", result.RootElement.GetProperty("status").GetString());
    }
}
