using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task CentralUpdateSessions()
    {
        var area = Area("central-update-session");
        using var key = RSA.Create(2048);
        var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        const string approved = "fixture-approved-central-session";
        var requests = new List<(string Path, bool Approved, bool HasCookie)>();
        var packageBytes = Array.Empty<byte>();
        var feed = new UpdateFeed { ProductId = "test.central-session", Name = "Protected update fixture", Version = "2", PackageType = "hinst" };
        await using var server = new LoopbackServer((NetworkRequest request) =>
        {
            var authorized = request.Headers.TryGetValue("Authorization", out var bearer) && bearer == "Bearer " + approved;
            requests.Add((request.Path, authorized, request.Headers.ContainsKey("Cookie")));
            if (!authorized) return new(401, Encoding.UTF8.GetBytes("Fixture sign-in required"));
            return request.Path.StartsWith("/setup.hinst", StringComparison.Ordinal)
                ? new(200, packageBytes) : new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options));
        });
        feed.InstallerUrl = server.Origin + "setup.hinst";
        PackageManifest SignedManifest(string version)
        {
            var manifest = Manifest(feed.ProductId, version);
            manifest.EntryPoint = "application.exe"; manifest.UpdateUrl = server.Origin + "feed.json";
            manifest.Features = [new() { Id = "updates", Default = true }];
            manifest.UpdateCompanion = new() { RuntimeMode = "app-local", CredentialOrigin = server.Origin, AuthApplication = "fixture-central",
                CookieEnvironmentVariable = "CENTRAL_FIXTURE_COOKIE", TokenEnvironmentVariable = "CENTRAL_FIXTURE_TOKEN" };
            manifest.Payload = [new() { Path = "application.exe" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }];
            return manifest;
        }
        var first = Build(area, "first", SignedManifest("1"), new() { ["application.exe"] = "first fixture", ["heirowUpdater.exe"] = "non-executed companion" }, keyPath);
        var next = Build(area, "next", SignedManifest("2"), new() { ["application.exe"] = "updated fixture", ["heirowUpdater.exe"] = "non-executed companion" }, keyPath);
        packageBytes = File.ReadAllBytes(next); feed.Size = packageBytes.Length; feed.Sha256 = PackageSafety.HashFile(next);
        var install = Options(area, Path.Combine(area, "app"));
        using (var package = PackageReader.Open(first)) await new InstallerEngine().InstallAsync(package, install);
        var product = InstallerEngine.ListInstalled(install.StateRoot).Single();
        var target = UpdaterTarget.FromInstalled(product, install.StateRoot);
        Equal(server.Origin, target.CredentialOrigin); Equal("fixture-central", target.AuthApplication);
        Equal("CENTRAL_FIXTURE_COOKIE", target.CookieEnvironmentVariable); Equal("CENTRAL_FIXTURE_TOKEN", target.TokenEnvironmentVariable);
        async Task CheckInstalledCli(bool valid)
        {
            var statusPath = Path.Combine(area, valid ? "cli-authorized.json" : "cli-sign-in-required.json");
            var start = new ProcessStartInfo(FindUpdaterCli()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "check", "--all", "--state-root", install.StateRoot!, "--allow-loopback", "--json", "--status-file", statusPath }) start.ArgumentList.Add(argument);
            if (!valid) foreach (var flag in new[] { "--interactive", "--background" }) start.ArgumentList.Add(flag);
            start.Environment["CENTRAL_FIXTURE_TOKEN"] = valid ? approved : "fixture-expired-central-token";
            start.Environment["CENTRAL_FIXTURE_COOKIE"] = "";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var diagnostics = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var stdout = await output; var stderr = await diagnostics;
            Equal(valid ? 0 : 1, process.ExitCode);
            using var status = JsonDocument.Parse(File.ReadAllText(statusPath));
            var result = status.RootElement.GetProperty("products")[0];
            Equal(valid ? "update-available" : "sign-in-required", result.GetProperty("status").GetString());
            Equal(!valid, result.GetProperty("requiresUserAction").GetBoolean());
            True(!stdout.Contains(approved, StringComparison.Ordinal) && !stderr.Contains(approved, StringComparison.Ordinal));
            if (valid) { using var visible = JsonDocument.Parse(stdout); True(visible.RootElement[0].GetProperty("available").GetBoolean()); }
            else True(string.IsNullOrWhiteSpace(stdout));
            True(!File.ReadAllText(statusPath).Contains(approved, StringComparison.Ordinal));
        }
        await CheckInstalledCli(valid: true); await CheckInstalledCli(valid: false);
        requests.Clear();
        var approvals = 0;
        using var session = new TargetUpdateSession(target, true, new Uri(server.Origin), "FixtureCookie=expired", "fixture-expired-bearer", cancellation =>
        { cancellation.ThrowIfCancellationRequested(); approvals++; return Task.FromResult<string?>(approved); });
        var check = await session.RunAsync(updater => updater.CheckAsync(target));
        True(check.Available); Equal(1, approvals); Equal(2, requests.Count);
        True(!requests[0].Approved && requests[0].HasCookie); True(requests[1].Approved && !requests[1].HasCookie);
        await session.RunAsync(updater => updater.ApplyAsync(target, check, new() { Isolated = true, DownloadDirectory = Path.Combine(area, "downloads") }));
        Equal("updated fixture", File.ReadAllText(Path.Combine(target.InstallDirectory!, "application.exe")));
        Equal("2", InstallerEngine.ListInstalled(install.StateRoot).Single().Version); Equal(1, approvals);
        True(requests.Where(request => request.Path.StartsWith("/setup.hinst", StringComparison.Ordinal)).Any());
        True(requests.Skip(1).All(request => request.Approved && !request.HasCookie));
        True(!(await session.RunAsync(updater => updater.CheckAsync(target))).Available); Equal(1, approvals);

        var foreignCredentials = false;
        await using var foreign = new LoopbackServer((NetworkRequest request) =>
        {
            foreignCredentials |= request.Headers.ContainsKey("Cookie") || request.Headers.ContainsKey("Authorization");
            return new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options));
        });
        var publicTarget = new UpdaterTarget { ProductId = feed.ProductId, Name = "Public fixture", CurrentVersion = "2", FeedUrl = foreign.Origin + "feed.json" };
        True(!(await session.RunAsync(updater => updater.CheckAsync(publicTarget))).Available); True(!foreignCredentials); Equal(1, approvals);
        var sourceSawCredentials = false;
        await using var redirect = new LoopbackServer((NetworkRequest request) =>
        {
            sourceSawCredentials |= request.Headers.ContainsKey("Cookie") || request.Headers.ContainsKey("Authorization");
            return new(302, [], new() { ["Location"] = foreign.Origin + "feed.json" });
        });
        publicTarget.FeedUrl = redirect.Origin + "feed.json";
        using (var redirectSession = new TargetUpdateSession(publicTarget, true, new Uri(redirect.Origin), "FixtureCookie=origin-only", approved))
            True(!(await redirectSession.RunAsync(updater => updater.CheckAsync(publicTarget))).Available);
        True(sourceSawCredentials); True(!foreignCredentials);
        foreach (var path in Directory.EnumerateFiles(area, "*.json", SearchOption.AllDirectories))
            True(!File.ReadAllText(path).Contains(approved, StringComparison.Ordinal));
    }

    private async Task CentralUpdateSessionFailures()
    {
        var requests = 0;
        await using var server = new LoopbackServer((NetworkRequest _) => { requests++; return new(401, []); });
        var target = new UpdaterTarget { ProductId = "test.central-unauthorized", Name = "Protected fixture", CurrentVersion = "1", FeedUrl = server.Origin + "feed.json" };
        using (var unattended = new TargetUpdateSession(target, true, new Uri(server.Origin)))
        {
            try { await unattended.RunAsync(updater => updater.CheckAsync(target)); throw new Exception("An unauthorized check must fail."); }
            catch (HttpRequestException error) { Equal(HttpStatusCode.Unauthorized, error.StatusCode); }
        }
        Equal(1, requests);
        var approvals = 0;
        using (var invalidApproval = new TargetUpdateSession(target, true, new Uri(server.Origin), authenticate: _ =>
            { approvals++; return Task.FromResult<string?>("fixture-still-rejected"); }))
        {
            await Throws<HttpRequestException>(() => invalidApproval.RunAsync(updater => updater.CheckAsync(target)));
            Equal(1, approvals); Equal(3, requests);
            await Throws<HttpRequestException>(() => invalidApproval.RunAsync(updater => updater.CheckAsync(target)));
            Equal(1, approvals); Equal(4, requests);
        }
        using (var postponed = new TargetUpdateSession(target, true, new Uri(server.Origin), authenticate: _ =>
            { approvals++; return Task.FromResult<string?>(null); }))
        {
            await Throws<HttpRequestException>(() => postponed.RunAsync(updater => updater.CheckAsync(target)));
            Equal(2, approvals); Equal(5, requests);
        }
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new TargetUpdateSession(target, true, new Uri(server.Origin), authenticate: async token =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; });
        var checking = canceled.RunAsync(updater => updater.CheckAsync(target, cancellation.Token), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => checking); Equal(6, requests);
        using var preCanceled = new CancellationTokenSource(); preCanceled.Cancel();
        await Throws<OperationCanceledException>(() => canceled.RunAsync(updater => updater.CheckAsync(target), preCanceled.Token));
        Equal(6, requests);
    }

    private async Task CentralUpdateProfileAuthentication()
    {
        var area = Area("central-profile-authentication");
        const string approved = "fixture-signed-profile-bearer";
        var received = new List<(bool Approved, bool HasCookie, string Path)>();
        var feed = new UpdateFeed { ProductId = "test.central-profile", Name = "Signed profile fixture", Version = "1", PackageType = "hinst", Size = 1, Sha256 = new string('a', 64) };
        await using var publisher = new LoopbackServer((NetworkRequest request) =>
        {
            var authorized = request.Headers.TryGetValue("Authorization", out var bearer) && bearer == "Bearer " + approved;
            received.Add((authorized, request.Headers.ContainsKey("Cookie"), request.Path));
            return authorized ? new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options)) : new(401, []);
        });
        feed.InstallerUrl = publisher.Origin + "setup.hinst";
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var manifest = Manifest(feed.ProductId, "1"); manifest.EntryPoint = "application.exe"; manifest.UpdateUrl = publisher.Origin + "feed.json";
        manifest.Features = [new() { Id = "updates", Default = true }];
        manifest.UpdateCompanion = new() { RuntimeMode = "app-local", CredentialOrigin = publisher.Origin, AuthApplication = "fixture-signed-app",
            CookieEnvironmentVariable = "CENTRAL_SIGNED_COOKIE", TokenEnvironmentVariable = "CENTRAL_SIGNED_TOKEN" };
        manifest.Payload = [new() { Path = "application.exe" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }];
        var packagePath = Build(area, "installed", manifest, new() { ["application.exe"] = "non-executed fixture", ["heirowUpdater.exe"] = "non-executed companion" }, keyPath);
        var install = Options(area, Path.Combine(area, "app"));
        using (var package = PackageReader.Open(packagePath)) await new InstallerEngine().InstallAsync(package, install);
        var spoofRequests = 0; var spoofCredentials = false;
        await using var spoof = new LoopbackServer((NetworkRequest request) =>
        {
            spoofRequests++; spoofCredentials |= request.Headers.ContainsKey("Cookie") || request.Headers.ContainsKey("Authorization");
            return new(401, []);
        });
        var profile = UpdaterTarget.FromInstalled(InstallerEngine.ListInstalled(install.StateRoot).Single(), install.StateRoot);
        profile.CredentialOrigin = spoof.Origin; profile.AuthApplication = "fixture-spoofed-app";
        profile.CookieEnvironmentVariable = "CENTRAL_SPOOFED_COOKIE"; profile.TokenEnvironmentVariable = "CENTRAL_SPOOFED_TOKEN";
        var profilePath = Path.Combine(area, "spoofed-profile.json"); File.WriteAllText(profilePath, JsonSerializer.Serialize(profile, InstallerJson.Options));
        async Task<string> CheckCli(string label, string[] selection, bool valid = true, string[]? flags = null)
        {
            var statusPath = Path.Combine(area, label + "-status.json");
            var start = new ProcessStartInfo(FindUpdaterCli()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "check", "--allow-loopback", "--json", "--status-file", statusPath }.Concat(selection).Concat(flags ?? [])) start.ArgumentList.Add(argument);
            start.Environment["CENTRAL_SIGNED_TOKEN"] = valid ? approved : "fixture-expired-signed-token";
            start.Environment["CENTRAL_SIGNED_COOKIE"] = "";
            start.Environment["CENTRAL_SPOOFED_TOKEN"] = "fixture-spoofed-token";
            start.Environment["CENTRAL_SPOOFED_COOKIE"] = "FixtureCookie=spoofed";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var diagnostics = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var stdout = await output; var stderr = await diagnostics;
            Equal(valid ? 0 : 1, process.ExitCode);
            True(!stdout.Contains(approved, StringComparison.Ordinal) && !stderr.Contains(approved, StringComparison.Ordinal));
            using var status = JsonDocument.Parse(File.ReadAllText(statusPath));
            var result = status.RootElement.GetProperty("products")[0];
            Equal(valid ? "current" : "sign-in-required", result.GetProperty("status").GetString());
            Equal(!valid, result.GetProperty("requiresUserAction").GetBoolean());
            return stdout;
        }
        await CheckCli("installed-profile", ["--profile", profilePath]);
        True(received.Last().Approved && !received.Last().HasCookie); Equal(0, spoofRequests);
        await CheckCli("installed-feed", ["--feed", publisher.Origin + "feed.json", "--product-id", feed.ProductId, "--current-version", "1", "--state-root", install.StateRoot!]);
        True(received.Last().Approved && !received.Last().HasCookie); Equal(0, spoofRequests);
        var beforeNoPrompt = received.Count;
        await CheckCli("explicit-no-prompt", ["--all", "--state-root", install.StateRoot!], valid: false, flags: ["--interactive", "--no-prompt"]);
        Equal(beforeNoPrompt + 1, received.Count); True(received.Last().Path.StartsWith("/feed.json", StringComparison.Ordinal)); Equal(0, spoofRequests);

        profile.ProductId = "test.central-uninstalled-profile"; profile.Name = "Untrusted profile fixture";
        profile.StateRoot = Path.Combine(area, "empty-state"); profile.FeedUrl = spoof.Origin + "feed.json";
        File.WriteAllText(profilePath, JsonSerializer.Serialize(profile, InstallerJson.Options));
        await CheckCli("uninstalled-profile", ["--profile", profilePath], valid: false, flags: ["--interactive"]);
        Equal(1, spoofRequests); True(!spoofCredentials);
    }
}
