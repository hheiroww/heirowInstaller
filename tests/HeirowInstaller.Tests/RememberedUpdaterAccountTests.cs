using System.Net;
using System.Reflection;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;
using HeirowUpdater;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task RememberedUpdaterAccountSharing()
    {
        var area = Area("remembered-updater-sharing");
        var clock = new UpdaterAccountClock(DateTimeOffset.UtcNow);
        const string approved = "fixture-remembered-updater-account";
        var applications = new List<string>();
        var requests = new List<string?>();
        LoopbackServer? publisher = null;
        await using (publisher = new LoopbackServer((NetworkRequest request) =>
        {
            request.Headers.TryGetValue("Authorization", out var bearer);
            lock (requests) requests.Add(bearer);
            if (bearer != "Bearer " + approved && bearer != "Bearer fixture-explicit-account") return new(401, []);
            var product = request.Path.StartsWith("/one", StringComparison.Ordinal) ? "test.remembered-one" : "test.remembered-two";
            return new(200, JsonSerializer.SerializeToUtf8Bytes(RememberedFeed(product, publisher!.Origin), InstallerJson.Options));
        }))
        {
            var origin = new Uri(publisher.Origin);
            var store = new RememberedPublisherAccount(origin, Path.Combine(area, "account"), clock, allowLoopback: true);
            var targets = new[] { RememberedTarget("test.remembered-one", publisher.Origin + "one/feed.json", publisher.Origin),
                RememberedTarget("test.remembered-two", publisher.Origin + "two/feed.json", publisher.Origin) };
            async Task<PublisherSession> Approve(Uri requestedOrigin, string application, CancellationToken cancellation)
            {
                Equal(origin, requestedOrigin); applications.Add(application);
                await Task.Delay(50, cancellation);
                return UpdaterAccountSession(approved, clock.GetUtcNow().AddDays(30));
            }
            using (var sessions = await TargetUpdateSessions.CreateAsync(targets, new(), true, true, new InlineProgress(_ => { }),
                rememberedAccount: store, rememberedOrigin: origin, authenticate: Approve))
            using (var updater = new SocketJackUpdater(allowLoopback: true))
            {
                var results = await updater.CheckTargetsAsync(targets,
                    (target, token) => sessions[target].RunAsync(client => client.CheckAsync(target, token), token), null, default);
                True(results.All(result => result.Error is null));
                Equal(1, applications.Count); Equal("heirowinstaller", applications.Single());
            }
            DateTimeOffset expiry;
            using (var cached = await store.RestoreAsync()) { True(cached is not null); expiry = cached!.ExpiresUtc; }
            var revision = await store.GetRevisionAsync();
            clock.Now = clock.Now.AddDays(10);
            var freshStore = new RememberedPublisherAccount(origin, Path.Combine(area, "account"), clock, allowLoopback: true);
            using (var background = await TargetUpdateSessions.CreateAsync(targets, new(), true, false, new InlineProgress(_ => { }),
                rememberedAccount: freshStore, rememberedOrigin: origin, authenticate: Approve))
                foreach (var target in targets) True(!(await background[target].RunAsync(client => client.CheckAsync(target))).Available);
            Equal(1, applications.Count); Equal(revision, await freshStore.GetRevisionAsync());
            using (var cached = await freshStore.RestoreAsync()) Equal(expiry, cached!.ExpiresUtc);
            True(requests.Count(value => value == "Bearer " + approved) >= 4);

            var variable = "CENTRAL_REMEMBERED_EXPLICIT_TOKEN";
            var previous = Environment.GetEnvironmentVariable(variable);
            try
            {
                Environment.SetEnvironmentVariable(variable, "fixture-explicit-account");
                using var explicitSession = await TargetUpdateSessions.CreateAsync([targets[0]],
                    new() { CredentialOrigin = publisher.Origin, TokenEnvironmentVariable = variable }, true, false, new InlineProgress(_ => { }),
                    rememberedAccount: store, rememberedOrigin: origin, authenticate: Approve);
                await explicitSession[targets[0]].RunAsync(client => client.CheckAsync(targets[0]));
                Equal("Bearer fixture-explicit-account", requests.Last()); Equal(1, applications.Count);
            }
            finally { Environment.SetEnvironmentVariable(variable, previous); }

            var foreignCredentials = false;
            LoopbackServer? foreign = null;
            await using (foreign = new LoopbackServer((NetworkRequest request) =>
            {
                foreignCredentials |= request.Headers.ContainsKey("Authorization") || request.Headers.ContainsKey("Cookie");
                return new(200, JsonSerializer.SerializeToUtf8Bytes(RememberedFeed("test.remembered-foreign", foreign!.Origin), InstallerJson.Options));
            }))
            {
                var foreignTarget = RememberedTarget("test.remembered-foreign", foreign.Origin + "feed.json", foreign.Origin);
                using var foreignSession = await TargetUpdateSessions.CreateAsync([foreignTarget], new(), true, false, new InlineProgress(_ => { }),
                    rememberedAccount: store, rememberedOrigin: origin, authenticate: Approve);
                await foreignSession[foreignTarget].RunAsync(client => client.CheckAsync(foreignTarget));
                True(!foreignCredentials); Equal(1, applications.Count);
                foreignTarget.CredentialOrigin = publisher.Origin;
                using var foreignFeed = await TargetUpdateSessions.CreateAsync([foreignTarget], new(), true, false, new InlineProgress(_ => { }),
                    rememberedAccount: store, rememberedOrigin: origin, authenticate: Approve);
                await foreignFeed[foreignTarget].RunAsync(client => client.CheckAsync(foreignTarget));
                True(!foreignCredentials); Equal(1, applications.Count);
            }
            var unsigned = RememberedTarget(targets[0].ProductId, targets[0].FeedUrl, null);
            using (var unsignedSession = await TargetUpdateSessions.CreateAsync([unsigned], new(), true, false, new InlineProgress(_ => { }),
                rememberedAccount: store, rememberedOrigin: origin, authenticate: Approve))
                await Throws<HttpRequestException>(() => unsignedSession[unsigned].RunAsync(client => client.CheckAsync(unsigned)));
            True(requests.Last() is null); Equal(revision, await store.GetRevisionAsync());
        }
    }

    private async Task RememberedUpdaterAccountRaces()
    {
        var area = Area("remembered-updater-races");
        var clock = new UpdaterAccountClock(DateTimeOffset.UtcNow);
        var expected = "fixture-newer-updater-account";
        LoopbackServer? publisher = null;
        await using (publisher = new LoopbackServer((NetworkRequest request) =>
            request.Headers.TryGetValue("Authorization", out var bearer) && bearer == "Bearer " + expected
                ? new(200, JsonSerializer.SerializeToUtf8Bytes(RememberedFeed("test.remembered-race", publisher!.Origin), InstallerJson.Options)) : new(401, [])))
        {
            var origin = new Uri(publisher.Origin);
            var store = new RememberedPublisherAccount(origin, Path.Combine(area, "account"), clock, allowLoopback: true);
            var target = RememberedTarget("test.remembered-race", publisher.Origin + "feed.json", publisher.Origin);
            async Task Remember(string token)
            { using var session = UpdaterAccountSession(token, clock.Now.AddDays(30)); await store.RememberAsync(session); }
            await Remember("fixture-rejected-updater-account");
            using (var oldRequest = await TargetUpdateSessions.CreateAsync([target], new(), true, false, new InlineProgress(_ => { }),
                rememberedAccount: store, rememberedOrigin: origin))
            {
                await Remember(expected);
                await Throws<HttpRequestException>(() => oldRequest[target].RunAsync(client => client.CheckAsync(target)));
            }
            using (var cached = await store.RestoreAsync()) Equal(expected, cached!.AccessToken);
            await Remember("fixture-rejected-updater-account");
            using (var rejected = await TargetUpdateSessions.CreateAsync([target], new(), true, false, new InlineProgress(_ => { }),
                rememberedAccount: store, rememberedOrigin: origin))
                await Throws<HttpRequestException>(() => rejected[target].RunAsync(client => client.CheckAsync(target)));
            using (var cached = await store.RestoreAsync()) True(cached is null);

            var approvals = 0;
            using (var signingOut = await TargetUpdateSessions.CreateAsync([target], new(), true, true, new InlineProgress(_ => { }),
                rememberedAccount: store, rememberedOrigin: origin, authenticate: async (_, application, _) =>
                {
                    approvals++; Equal("heirowinstaller", application); await store.SignOutAsync();
                    return UpdaterAccountSession("fixture-obsolete-approval", clock.Now.AddDays(30));
                }))
                await Throws<HttpRequestException>(() => signingOut[target].RunAsync(client => client.CheckAsync(target)));
            using (var cached = await store.RestoreAsync()) True(cached is null);
            Equal(1, approvals);
            using (var newerLogin = await TargetUpdateSessions.CreateAsync([target], new(), true, true, new InlineProgress(_ => { }),
                rememberedAccount: store, rememberedOrigin: origin, authenticate: async (_, _, _) =>
                {
                    approvals++; await Remember(expected);
                    return UpdaterAccountSession("fixture-obsolete-approval", clock.Now.AddDays(30));
                }))
                True(!(await newerLogin[target].RunAsync(client => client.CheckAsync(target))).Available);
            Equal(2, approvals);
            using (var cached = await store.RestoreAsync()) Equal(expected, cached!.AccessToken);
        }
    }

    private static UpdaterTarget RememberedTarget(string product, string feed, string? origin) => new()
    { ProductId = product, Name = product, CurrentVersion = "1", FeedUrl = feed, CredentialOrigin = origin, AuthApplication = origin is null ? null : "fruityloops" };
    private static UpdateFeed RememberedFeed(string product, string origin) => new()
    { ProductId = product, Name = product, Version = "1", InstallerUrl = origin + "setup.hinst", PackageType = "hinst", Size = 1, Sha256 = new string('a', 64) };
    private static PublisherSession UpdaterAccountSession(string token, DateTimeOffset expires) => (PublisherSession)Activator.CreateInstance(
        typeof(PublisherSession), BindingFlags.Instance | BindingFlags.NonPublic, null, ["fixture-account", token, false, expires], null)!;
    private sealed class UpdaterAccountClock(DateTimeOffset now) : TimeProvider
    { internal DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
}
