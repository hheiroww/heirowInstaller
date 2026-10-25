using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task SessionAuthorizationTransitions()
    {
        var cookieA = "wshare_hash=fixture-authority-first"; var cookieB = "wshare_hash=fixture-authority-next";
        var requests = 0; var block = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new UpdateFeed { ProductId = "test.session-transitions", Name = "Session transition fixture", Version = "1", Size = 1, Sha256 = new string('a', 64) };
        await using var server = new LoopbackServer((NetworkRequest request) =>
        {
            Interlocked.Increment(ref requests);
            if (Interlocked.CompareExchange(ref block, 0, 1) == 1) { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
            var current = request.Headers.TryGetValue("Cookie", out var cookie) && cookie == cookieB && !request.Headers.ContainsKey("Authorization");
            return current ? new(200, JsonSerializer.SerializeToUtf8Bytes(feed, InstallerJson.Options)) : new(401, Encoding.UTF8.GetBytes("fixture authorization required"));
        });
        feed.InstallerUrl = server.Origin + "setup.hinst";
        var target = new UpdaterTarget { ProductId = feed.ProductId, CurrentVersion = "1", FeedUrl = server.Origin + "feed.json" };
        Task<UpdateCheckResult> Request(ApplicationSessionStore sessions) => RequestCore(sessions);
        async Task<UpdateCheckResult> RequestCore(ApplicationSessionStore sessions)
        {
            using var client = new SocketJackUpdater(true, new Uri(server.Origin), () => sessions.Read().Snapshot);
            return await client.CheckAsync(target);
        }
        var statuses = new List<string>(); var browser = 0; var postponed = 0;
        Task Status(string status) { statuses.Add(status); return Task.CompletedTask; }
        Task Postpone() { postponed++; return Task.CompletedTask; }

        // The actual rejected HTTP request is already in flight when a later native login arrives.
        var sessions = new ApplicationSessionStore(new ApplicationSessionSnapshot(cookieA));
        block = 1;
        var retry = ApplicationAuthorization.RunAsync(sessions, () => Request(sessions), _ =>
        { browser++; throw new InvalidOperationException("A newer valid native session must be retried before browser approval."); }, Status, Postpone, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(sessions.Replace(new ApplicationSessionSnapshot(cookieB))); release.TrySetResult();
        True(!(await retry.WaitAsync(TimeSpan.FromSeconds(5))).Available); Equal(0, browser); Equal(0, postponed); Equal(2, requests);

        // Refresh cancels only this obsolete approval wait. It is not a postpone or application-wide stop.
        sessions = new(new ApplicationSessionSnapshot(cookieA));
        var approvalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerCancellation = new CancellationTokenSource();
        var approval = ApplicationAuthorization.RunAsync(sessions, () => Request(sessions), async cancellation =>
        {
            browser++; approvalEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); return null;
        }, Status, Postpone, ownerCancellation.Token);
        await approvalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(sessions.Replace(new ApplicationSessionSnapshot(cookieB)));
        True(!(await approval.WaitAsync(TimeSpan.FromSeconds(5))).Available);
        Equal(1, browser); Equal(0, postponed); True(!ownerCancellation.IsCancellationRequested);
        True(statuses.Contains("sign-in-required") && statuses.Contains("checking"));

        // A provider that finishes despite cancellation cannot install the old account's bearer over the newer login.
        sessions = new(new ApplicationSessionSnapshot(cookieA));
        approvalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldApproval = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = ApplicationAuthorization.RunAsync(sessions, () => Request(sessions), _ =>
        { browser++; approvalEntered.TrySetResult(); return oldApproval.Task; }, Status, Postpone, CancellationToken.None);
        await approvalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(sessions.Replace(new ApplicationSessionSnapshot(cookieB))); oldApproval.TrySetResult("fixture-obsolete-browser");
        True(!(await late.WaitAsync(TimeSpan.FromSeconds(5))).Available);
        True(sessions.Read().Snapshot.BearerToken is null && sessions.Read().Snapshot.Cookie == cookieB); Equal(0, postponed);

        // An already authorized operation is left running; only subsequent requests observe the replacement.
        sessions = new(new ApplicationSessionSnapshot(cookieA));
        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = ApplicationAuthorization.RunAsync(sessions, async () =>
        { operationEntered.TrySetResult(); await operationRelease.Task; return "verified fixture handoff"; }, null, Status, Postpone, ownerCancellation.Token);
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(sessions.Replace(new ApplicationSessionSnapshot(cookieB))); True(!active.IsCompleted && !ownerCancellation.IsCancellationRequested);
        operationRelease.TrySetResult(); Equal("verified fixture handoff", await active);
        True(!(await Request(sessions)).Available); Equal(0, postponed);
    }
}
