using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task NativeSessionRelay()
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = Area("native-session-relay"); var source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
        var native = FindUpdaterCli(); File.Copy(native, Path.Combine(source, "heirowUpdater.exe"));
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "ManualCheckApplicationProbe", "bin", configuration, "net10.0"));
        foreach (var name in new[] { "ManualCheckApplicationProbe.exe", "ManualCheckApplicationProbe.dll", "ManualCheckApplicationProbe.deps.json", "ManualCheckApplicationProbe.runtimeconfig.json" }) File.Copy(Path.Combine(probe, name), Path.Combine(source, name));
        var requests = 0; var currentCookie = false; var noCredentials = false;
        var feed = new UpdateFeed { ProductId = "test.native-session-relay", Name = "Owned native session fixture", Version = "1", Size = 1, Sha256 = new string('a', 64) };
        await using var server = new LoopbackServer((NetworkRequest request) =>
        {
            Interlocked.Increment(ref requests);
            currentCookie = request.Headers.TryGetValue("Cookie", out var cookie) && cookie == "wshare_hash=fixture-current-native";
            noCredentials = !request.Headers.ContainsKey("Cookie") && !request.Headers.ContainsKey("Authorization");
            return currentCookie ? new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options)) : new(401, Encoding.UTF8.GetBytes("fixture authorization required"));
        });
        feed.InstallerUrl = server.Origin + "data.hinst";
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var manifest = Manifest(feed.ProductId, "1"); manifest.EntryPoint = "ManualCheckApplicationProbe.exe"; manifest.UpdateUrl = server.Origin + "feed.json";
        manifest.Features = [new() { Id = "updates", Default = true }];
        manifest.UpdateCompanion = new() { RuntimeMode = "shared", UpdateMode = "files", CredentialOrigin = server.Origin, CookieEnvironmentVariable = "HEIROWUPDATER_COOKIE", TokenEnvironmentVariable = "HEIROWUPDATER_TOKEN" };
        manifest.Payload = Directory.EnumerateFiles(source).Select(path => new PayloadFile { Path = Path.GetFileName(path), Feature = Path.GetFileName(path) == "heirowUpdater.exe" ? "updates" : null }).ToList();
        var json = Path.Combine(area, "manifest.json"); File.WriteAllText(json, JsonSerializer.Serialize(manifest, InstallerJson.Options));
        var packagePath = Path.Combine(area, "fixture.hinst"); PackageBuilder.Build(json, source, packagePath, new() { SigningKeyPath = keyPath });
        var install = Options(area, Path.Combine(area, "app"));
        using (var package = PackageReader.Open(packagePath)) await new InstallerEngine().InstallAsync(package, install);
        var profilePath = Path.Combine(install.TargetDirectory!, "heirowUpdater.json"); var profile = ApplicationUpdater.ReadConfiguration(profilePath, true);
        var helper = ApplicationUpdater.ResolveExecutable(profile);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(native)!, "*", SearchOption.TopDirectoryOnly).Where(path => Path.GetExtension(path) is ".dll" or ".json")) File.Copy(file, Path.Combine(Path.GetDirectoryName(helper)!, Path.GetFileName(file)), overwrite: true);
        for (var phase = 0; phase < 2; phase++)
        {
            var run = Path.Combine(area, "run-" + phase); Directory.CreateDirectory(run);
            File.WriteAllText(Path.Combine(run, ".manual-check-probe"), "heirowInstaller manual feedback acceptance\n");
            var appStart = new ProcessStartInfo(Path.Combine(install.TargetDirectory!, manifest.EntryPoint)) { UseShellExecute = false, CreateNoWindow = true };
            appStart.ArgumentList.Add("--run-directory"); appStart.ArgumentList.Add(run);
            using var app = Process.Start(appStart)!;
            var statusPath = Path.Combine(area, "owner-" + phase + ".json");
            ProcessStartInfo Start(ApplicationSessionSnapshot snapshot)
            {
                var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = install.TargetDirectory! };
                foreach (var value in new[] { "after-exit", "--config", profilePath, "--application-pid", app.Id.ToString(), "--application-start", app.StartTime.ToUniversalTime().Ticks.ToString(), "--status-file", statusPath }) start.ArgumentList.Add(value);
                ApplicationUpdater.SetSessionEnvironment(start, snapshot); return start;
            }
            using var owner = Process.Start(Start(new ApplicationSessionSnapshot(phase == 0 ? "wshare_hash=fixture-old-native" : "wshare_hash=fixture-current-native")))!;
            var ownerOut = owner.StandardOutput.ReadToEndAsync(); var ownerError = owner.StandardError.ReadToEndAsync();
            try
            {
                var deadline = Stopwatch.StartNew(); var waiting = false;
                while (!waiting)
                {
                    if (owner.HasExited) throw new InvalidOperationException("Owned watcher exited before waiting: " + await ownerError);
                    if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Owned native watcher did not wait for its app.");
                    try
                    {
                        using var input = new FileStream(statusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var status = JsonDocument.Parse(input); waiting = status.RootElement.GetProperty("status").GetString() == "waiting-for-exit";
                    }
                    catch (Exception error) when (error is IOException or JsonException) { }
                    if (!waiting) await Task.Delay(30);
                }
                var before = requests;
                using (var relay = Process.Start(Start(phase == 0 ? new ApplicationSessionSnapshot("wshare_hash=fixture-current-native") : new ApplicationSessionSnapshot()))!)
                {
                    var output = relay.StandardOutput.ReadToEndAsync(); var error = relay.StandardError.ReadToEndAsync();
                    await relay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    var diagnostic = await error; var response = await output;
                    if (relay.ExitCode != 0)
                    {
                        await UpdateCompanionControl.StopAsync(profile);
                        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                        throw new InvalidOperationException("Owned native session relay failed: " + diagnostic + "; isolated owner diagnostic: " + await ownerError);
                    }
                    using var result = JsonDocument.Parse(response); Equal("already-watching", result.RootElement.GetProperty("status").GetString());
                }
                Equal(before, requests); True(!owner.HasExited && !app.HasExited);
                File.WriteAllText(Path.Combine(run, "exit.flag"), "exit"); await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); _ = await ownerOut; _ = await ownerError;
                if (phase == 0) { Equal(0, owner.ExitCode); True(currentCookie && !noCredentials); }
                else { True(owner.ExitCode != 0); True(noCredentials && !currentCookie); }
            }
            finally
            {
                if (!app.HasExited) File.WriteAllText(Path.Combine(run, "exit.flag"), "exit");
                if (!owner.HasExited) await UpdateCompanionControl.StopAsync(profile);
                await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        foreach (var path in Directory.EnumerateFiles(area, "*.json", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            True(!content.Contains("fixture-current-native", StringComparison.Ordinal) && !content.Contains("fixture-old-native", StringComparison.Ordinal));
        }
    }
}
