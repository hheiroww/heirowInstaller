using System.Diagnostics;
using System.Net;
using System.Text.Json;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private async Task UpdateCheckDeadlines()
    {
        True(typeof(UpdateClient).GetConstructor([typeof(bool), typeof(HttpClient), typeof(IUpdateTransport), typeof(bool)]) is not null);
        True(typeof(UpdateClient).GetMethod(nameof(UpdateClient.CheckAsync), [typeof(string), typeof(string), typeof(string), typeof(CancellationToken)]) is not null);
        True(typeof(SocketJackUpdater).GetMethod(nameof(SocketJackUpdater.CheckAsync), [typeof(UpdaterTarget), typeof(CancellationToken)]) is not null);
        var headers = new StalledFeedTransport();
        using (var client = new UpdateClient(transport: headers, checkTimeout: TimeSpan.FromMilliseconds(150)))
        {
            var watch = Stopwatch.StartNew();
            await Throws<TimeoutException>(() => client.CheckAsync("https://example.invalid/feed", "1", "test.deadline"));
            True(headers.Canceled); True(watch.Elapsed < TimeSpan.FromSeconds(5));
        }
        var body = new StalledFeedStream();
        using (var client = new UpdateClient(transport: new StalledFeedTransport(body), checkTimeout: TimeSpan.FromMilliseconds(150)))
            await Throws<TimeoutException>(() => client.CheckAsync("https://example.invalid/feed", "1", "test.deadline"));
        True(body.Canceled); True(body.Disposed);
        var interrupted = new StalledFeedTransport();
        using var cancel = new CancellationTokenSource();
        using (var client = new UpdateClient(transport: interrupted))
        {
            var checking = client.CheckAsync("https://example.invalid/feed", "1", "test.deadline", cancel.Token);
            await interrupted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => checking);
            True(interrupted.Canceled);
        }
    }

    private async Task ConcurrentUpdateChecks()
    {
        var servers = new List<LoopbackServer>();
        var targets = new List<UpdaterTarget>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximumActive = 0; var entered = 0;
        var sync = new object();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var index = i; var product = "test.concurrent-" + i;
                LoopbackServer? server = null;
                server = new LoopbackServer((NetworkRequest _) =>
                {
                    lock (sync)
                    {
                        active++; entered++; maximumActive = Math.Max(maximumActive, active);
                        if (entered == 4) fourStarted.TrySetResult();
                    }
                    release.Task.GetAwaiter().GetResult();
                    lock (sync) active--;
                    if (index == 2) return new(500, []);
                    return new(200, JsonSerializer.SerializeToUtf8Bytes(new UpdateFeed { ProductId = product, Name = product, Version = "1",
                        PackageType = "hinst", Size = 1, Sha256 = new string('a', 64), InstallerUrl = server!.Origin + "package.hinst" }, InstallerJson.Options));
                });
                servers.Add(server);
                targets.Add(new() { ProductId = product, Name = product, CurrentVersion = "1", FeedUrl = server.Origin + "feed.json" });
            }
            using var updater = new SocketJackUpdater(allowLoopback: true);
            var progress = new List<InstallProgress>();
            var checking = updater.CheckTargetsAsync(targets, new InlineProgress(progress.Add));
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (sync) { Equal(4, entered); Equal(4, active); }
            release.TrySetResult();
            var results = await checking.WaitAsync(TimeSpan.FromSeconds(10));
            Equal(4, maximumActive); Equal(6, results.Count);
            Equal(string.Join(',', targets.Select(x => x.ProductId)), string.Join(',', results.Select(x => x.ProductId)));
            True(results[2].Error is not null); True(results.Where((_, index) => index != 2).All(x => x.Error is null && !x.Available), string.Join("; ", results.Select(x => x.ProductId + ": " + x.Error)));
            Equal("1,2,3,4,5,6", string.Join(',', progress.Where(x => x.Phase == "check-complete").Select(x => x.Completed)));
            True(progress.All(x => x.Total == 6));
            var cli = new ProcessStartInfo(FindUpdaterCli()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var cliArea = Area("update-check-progress");
            foreach (var argument in new[] { "check", "--feed", targets[0].FeedUrl, "--product-id", targets[0].ProductId, "--current-version", "1",
                "--state-root", cliArea, "--status-file", Path.Combine(cliArea, "status.json"), "--allow-loopback", "--json", "--progress-json" }) cli.ArgumentList.Add(argument);
            using var process = Process.Start(cli)!;
            var output = process.StandardOutput.ReadToEndAsync(); var diagnostics = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Equal(0, process.ExitCode);
            using (var json = JsonDocument.Parse(await output)) Equal("current", json.RootElement[0].GetProperty("status").GetString()!);
            var messages = (await diagnostics).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<InstallProgress>(line)!).ToArray();
            True(messages.Any(x => x.Phase == "check-complete" && x.Completed == 1 && x.Total == 1));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Throws<OperationCanceledException>(() => updater.CheckTargetsAsync(targets, cancellationToken: canceled.Token));
        }
        finally
        {
            release.TrySetResult();
            foreach (var server in servers) await server.DisposeAsync();
        }
    }

    private sealed class StalledFeedTransport(StalledFeedStream? body = null) : IUpdateTransport
    {
        internal bool Canceled;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HttpResponseMessage> GetAsync(Uri uri, long maximumResponseBytes, IProgress<InstallProgress>? progress, CancellationToken token)
        {
            Entered.TrySetResult();
            if (body is not null) return new(HttpStatusCode.OK) { Content = new StreamContent(body) };
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
    }

    private sealed class StalledFeedStream : Stream
    {
        internal bool Canceled, Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
