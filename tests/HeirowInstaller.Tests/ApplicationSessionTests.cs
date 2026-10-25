using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task SessionSnapshots()
    {
        var first = new ApplicationSessionSnapshot("wshare_hash=fixture-native-first");
        var next = new ApplicationSessionSnapshot("wshare_hash=fixture-native-next");
        var store = new ApplicationSessionStore(first);
        var attempted = store.Read();
        True(store.SetBrowserSession(attempted.Revision, "fixture-old-browser"));
        True(store.Replace(next)); True(store.Read().Snapshot.BearerToken is null);
        True(!store.Replace(first)); True(!store.SetBrowserSession(attempted.Revision, "fixture-late-browser"));
        var clockSeed = new ApplicationSessionSnapshot(next.Cookie);
        var clockAdjusted = new ApplicationSessionSnapshot(next.Cookie, null, next.CapturedAtUtcTicks - 1, clockSeed.Id, clockSeed.SourceProcessId, clockSeed.SourceStartTicks, clockSeed.Sequence);
        True(store.Replace(clockAdjusted)); // Monotonic sender ordering survives a local clock correction.
        var current = store.Read();
        using (var authentication = store.BeginAuthentication(current.Revision, CancellationToken.None))
        {
            var cleared = new ApplicationSessionSnapshot();
            True(store.Replace(cleared)); True(authentication.IsCancellationRequested);
            True(store.Read().Snapshot.Cookie is null && store.Read().Snapshot.BearerToken is null);
            store.EndAuthentication(authentication);
        }
        True(!JsonSerializer.Serialize(next).Contains("fixture-native-next", StringComparison.Ordinal));
        True(!next.ToString().Contains("fixture-native-next", StringComparison.Ordinal));
        await Throws<ArgumentException>(() => Task.Run(() => new ApplicationSessionSnapshot("invalid\r\nheader")));
        await Throws<ArgumentException>(() => Task.Run(() => new ApplicationSessionSnapshot(new string('x', 6145))));
        var start = new ProcessStartInfo("non-executed-fixture.exe");
        start.Environment["HEIROWUPDATER_TOKEN"] = "fixture-inherited-browser";
        ApplicationUpdater.SetSessionEnvironment(start, next);
        True(!start.Environment.ContainsKey("HEIROWUPDATER_TOKEN"));
        Equal(next.Cookie, start.Environment["HEIROWUPDATER_COOKIE"]);
        Equal(next.CapturedAtUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), start.Environment["HEIROWUPDATER_SESSION_CAPTURED_AT"]);
        True(!start.ArgumentList.Any(x => x.Contains("fixture-native", StringComparison.Ordinal)));
        ApplicationUpdater.SetSessionEnvironment(start, null, null, replace: true);
        True(!start.Environment.ContainsKey("HEIROWUPDATER_TOKEN") && !start.Environment.ContainsKey("HEIROWUPDATER_COOKIE"));
        Equal("1", start.Environment["HEIROWUPDATER_SESSION_SUPPLIED"]);
        ApplicationUpdater.SetSessionEnvironment(start, null, null, replace: false);
        True(!start.Environment.ContainsKey("HEIROWUPDATER_SESSION_SUPPLIED"));

        var area = Area("session-snapshots");
        var received = new List<(bool CurrentCookie, bool OldBearer)>();
        var feed = new UpdateFeed { ProductId = "test.native-session", Name = "Native session fixture", Version = "1", Size = 1, Sha256 = new string('a', 64) };
        await using var server = new LoopbackServer((NetworkRequest request) =>
        {
            var native = request.Headers.TryGetValue("Cookie", out var cookie) && cookie == next.Cookie;
            var browser = request.Headers.TryGetValue("Authorization", out var authorization);
            lock (received) received.Add((native, browser));
            return native ? new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options)) : new(401, Encoding.UTF8.GetBytes("fixture authentication required"));
        });
        feed.InstallerUrl = server.Origin + "setup.hinst";
        var transportStore = new ApplicationSessionStore(first);
        using (var updater = new SocketJackUpdater(true, new Uri(server.Origin), () => transportStore.Read().Snapshot))
        {
            var target = new UpdaterTarget { ProductId = feed.ProductId, CurrentVersion = "1", FeedUrl = server.Origin + "feed.json" };
            await Throws<HttpRequestException>(() => updater.CheckAsync(target));
            True(transportStore.Replace(next));
            True(!(await updater.CheckAsync(target)).Available);
            True(received.Last().CurrentCookie && !received.Last().OldBearer);
            True(transportStore.Replace(new ApplicationSessionSnapshot()));
            await Throws<HttpRequestException>(() => updater.CheckAsync(target));
        }
        using var key = RSA.Create(2048); var keyPath = Path.Combine(area, "fixture-signing.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var manifest = Manifest(feed.ProductId, "1"); manifest.EntryPoint = "application.exe"; manifest.UpdateUrl = server.Origin + "feed.json";
        manifest.Features = [new() { Id = "updates", Default = true }];
        manifest.UpdateCompanion = new() { RuntimeMode = "app-local", CredentialOrigin = server.Origin, CookieEnvironmentVariable = "HEIROWUPDATER_COOKIE", AuthApplication = "example" };
        manifest.Payload = [new() { Path = "application.exe" }, new() { Path = "heirowUpdater.exe", Feature = "updates" }];
        var packagePath = Build(area, "native-session", manifest, new() { ["application.exe"] = "non-executed app", ["heirowUpdater.exe"] = "non-executed companion" }, keyPath);
        var install = Options(area, Path.Combine(area, "app"));
        using (var package = PackageReader.Open(packagePath)) await new InstallerEngine().InstallAsync(package, install);
        var profile = Path.Combine(install.TargetDirectory!, "heirowUpdater.json"); var browserCalls = 0;
        var options = new ApplicationLifecycleOptions { AllowLoopback = true, SessionSnapshot = next,
            AuthenticateAsync = (_, _) => { browserCalls++; throw new InvalidOperationException("A valid native session must not request browser approval."); } };
        using (var result = await ApplicationUpdater.RunAsync(profile, null, null, true, options, (_, _) => throw new InvalidOperationException("No update is available.")))
            Equal("current", result.Status);
        Equal(0, browserCalls);
        foreach (var path in Directory.EnumerateFiles(area, "*.json", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            True(!content.Contains("fixture-native-next", StringComparison.Ordinal) && !content.Contains("fixture-old-browser", StringComparison.Ordinal));
        }
        var foreignReceivedCredentials = false;
        await using var foreign = new LoopbackServer((NetworkRequest request) =>
        { foreignReceivedCredentials = request.Headers.ContainsKey("Cookie") || request.Headers.ContainsKey("Authorization"); return new(200, Encoding.UTF8.GetBytes("{}")); });
        using (var response = await new SocketJackUpdateTransport(new Uri(server.Origin), () => next).GetAsync(new Uri(foreign.Origin), 1024, null, CancellationToken.None)) Equal(HttpStatusCode.OK, response.StatusCode);
        True(!foreignReceivedCredentials);
    }
}
