using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Publishing;

internal static class RememberedPublisherAccountChecks
{
    private const string Username = "remembered-fixture-account";
    private const string Token = "fixture-retained-account-token";

    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, "remembered-account");
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using var server = new Server();
        var account = new RememberedPublisherAccount(server.Origin, directory, clock, allowLoopback: true);
        var revision = await account.GetRevisionAsync();
        using var initial = Session(Token, clock.GetUtcNow().AddDays(30));
        check(await account.RememberAsync(initial, revision), "A fresh approved account is saved against its captured revision");
        var savedRevision = await account.GetRevisionAsync();
        check(savedRevision != revision, "Saving an approved account advances its revision");
        var protectedPath = Directory.GetFiles(directory, "*.bin").Single();
        var bytes = await File.ReadAllBytesAsync(protectedPath);
        check(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Token)) < 0 && bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Username)) < 0, "Actual current-user DPAPI persists neither account name nor bearer in plaintext");
        using (var restored = await new RememberedPublisherAccount(server.Origin, directory, clock, true).RestoreAsync())
            check(restored?.AccessToken == Token && restored.Username == Username && restored.ExpiresUtc == initial.ExpiresUtc, "Another store instance restores the exact protected account without extending expiry");
        check(await account.GetRevisionAsync() == savedRevision && server.Requests == 0, "Update-only restoration neither writes a new lifetime nor contacts the renewal endpoint");
        await ChildAsync(server.Origin, directory, "restore", root, check);
        await CrossProcessLockAsync(account, server.Origin, directory, root, check);

        clock.Now = clock.Now.AddDays(29);
        server.Enqueue(request =>
        {
            VerifyRequest(request, "/api/app-signin/renew", Token, check);
            return (200, Redemption("fixture-renewed-account-token", clock.GetUtcNow().AddDays(30)));
        });
        using (var renewed = await account.RestoreAsync(renew: true))
            check(renewed?.AccessToken == "fixture-renewed-account-token" && renewed.ExpiresUtc == clock.GetUtcNow().AddDays(30), "Opening the installer renews on the server and starts a new thirty-day lifetime");
        clock.Now = clock.Now.AddDays(2);
        using (var retained = await account.RestoreAsync())
            check(retained?.AccessToken == "fixture-renewed-account-token", "A renewed account remains usable beyond its original thirty-day expiry");
        var beforeOffline = await account.GetRevisionAsync();
        server.Enqueue(_ => (503, new { privateDetails = "fixture-server-details" }));
        using (var offline = await account.RestoreAsync(renew: true))
            check(offline?.AccessToken == "fixture-renewed-account-token" && offline.ExpiresUtc == clock.GetUtcNow().AddDays(28), "Transient renewal failure preserves the valid cached token and original expiry");
        check(await account.GetRevisionAsync() == beforeOffline, "Offline renewal cannot extend or replace the protected session");
        server.Enqueue(_ => (401, new { privateDetails = "fixture-private-rejection" }));
        check(await account.RestoreAsync(renew: true) is null && await account.RestoreAsync() is null, "Server-revoked credentials are cleared instead of being reused for updates");

        using var fresh = Session("fixture-new-account", clock.GetUtcNow().AddDays(30));
        check(await account.RememberAsync(fresh), "A later explicit sign-in can replace a rejected remembered account");
        var browserRevision = await account.GetRevisionAsync();
        server.Enqueue(request =>
        {
            VerifyRequest(request, "/api/app-signin/signout", fresh.AccessToken, check);
            return (200, new { ok = true, loggedOut = true });
        });
        await account.SignOutAsync();
        check(await account.RestoreAsync() is null && await account.GetRevisionAsync() != browserRevision, "Sign-out revokes the account and leaves a token-free revision");
        check(!await account.RememberAsync(fresh, browserRevision), "Browser approval completed after sign-out cannot resurrect the removed account");
        check(await account.RememberAsync(fresh), "A new sign-in still works after the old approval is rejected");
        await account.InvalidateAsync(Token);
        using (var retained = await account.RestoreAsync()) check(retained?.AccessToken == fresh.AccessToken, "An older update rejection cannot clear a later account login");
        await account.InvalidateAsync(fresh.AccessToken);
        check(await account.RestoreAsync() is null, "The currently rejected token is conditionally invalidated");

        check(await account.RememberAsync(fresh), "Expiration fixture is remembered");
        clock.Now = fresh.ExpiresUtc;
        check(await account.RestoreAsync() is null, "The exact expiry boundary clears the cached credentials");
        using var longSession = Session("fixture-long-server-session", clock.GetUtcNow().AddDays(45));
        check(await account.RememberAsync(longSession), "Long server lifetime fixture is accepted under the local retention bound");
        using (var capped = await account.RestoreAsync()) check(capped?.ExpiresUtc == clock.GetUtcNow().AddDays(30), "Local retained account expiry never exceeds thirty days from a successful save");
        server.Enqueue(_ => (503, new { ok = false }));
        await account.SignOutAsync();
        check(await account.RestoreAsync() is null, "Local sign-out removes credentials even when server revocation is temporarily unavailable");

        using var corruptFixture = Session(Token, clock.GetUtcNow().AddDays(30));
        await account.RememberAsync(corruptFixture);
        var originalCiphertext = await File.ReadAllBytesAsync(protectedPath);
        originalCiphertext[originalCiphertext.Length / 2] ^= 0x7f;
        await File.WriteAllBytesAsync(protectedPath, originalCiphertext);
        check(await account.RestoreAsync() is null, "Tampered DPAPI ciphertext fails closed without returning a credential");
        await File.WriteAllBytesAsync(protectedPath, new byte[128 * 1024 + 1]);
        check(await account.RestoreAsync() is null, "Oversized protected account state is rejected before unbounded allocation");
        await account.RememberAsync(corruptFixture);
        var otherOrigin = new RememberedPublisherAccount(new Uri("https://other-publisher.example/"), directory, clock);
        await otherOrigin.GetRevisionAsync();
        var otherPath = Directory.GetFiles(directory, "*.bin").Single(path => path != protectedPath);
        File.Copy(protectedPath, otherPath, overwrite: true);
        check(await otherOrigin.RestoreAsync() is null, "Protected accounts copied to another publisher origin cannot be decrypted or reused");
        await ConcurrentRenewalsAsync(account, server, clock, check);
        foreach (var path in Directory.GetFiles(directory))
        {
            var content = await File.ReadAllBytesAsync(path);
            check(content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Token)) < 0, "Retained-account files and lock state contain no plaintext fixture bearer");
        }
        check(!Directory.GetFiles(directory, "*.tmp").Any(), "Protected saves leave no intermediate credential files");
    }

    private static async Task ConcurrentRenewalsAsync(RememberedPublisherAccount account, Server server, Clock clock, Action<bool, string> check)
    {
        using var initial = Session(Token, clock.GetUtcNow().AddDays(30)); await account.RememberAsync(initial);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Enqueue(async request =>
        {
            VerifyRequest(request, "/api/app-signin/renew", Token, check);
            entered.TrySetResult(); await release.Task;
            return (200, Redemption("fixture-concurrent-first", clock.GetUtcNow().AddDays(30)));
        });
        server.Enqueue(request =>
        {
            VerifyRequest(request, "/api/app-signin/renew", "fixture-concurrent-first", check);
            return (200, Redemption("fixture-concurrent-second", clock.GetUtcNow().AddDays(30)));
        });
        var first = account.RestoreAsync(renew: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = account.RestoreAsync(renew: true);
        try
        {
            await Task.Delay(100); check(!second.IsCompleted, "Concurrent account renewal waits for the held cross-process account lease");
        }
        finally { release.TrySetResult(); }
        using var a = await first; using var b = await second;
        check(a?.AccessToken == "fixture-concurrent-first" && b?.AccessToken == "fixture-concurrent-second", "Serialized renewal reads the latest rotated token rather than reusing the old bearer");
        using var final = await account.RestoreAsync();
        check(final?.AccessToken == "fixture-concurrent-second", "The newest completed renewal remains durably protected");
    }

    internal static async Task<int> RunChildAsync(string[] args)
    {
        if (args is not ["--remembered-account-child", var originText, var directory, var mode, var control]) return 2;
        var absolute = Path.GetFullPath(directory);
        if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !absolute.Contains("heirowinstaller-publishing-tests-", StringComparison.Ordinal)) return 2;
        var controlPath = Path.GetFullPath(control);
        if (!controlPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !controlPath.Contains("heirowinstaller-publishing-tests-", StringComparison.Ordinal)) return 2;
        var account = new RememberedPublisherAccount(new Uri(originText), absolute, allowLoopback: true);
        if (mode == "restore")
        {
            using var session = await account.RestoreAsync();
            return session?.AccessToken == Token && session.Username == Username ? 0 : 1;
        }
        if (mode != "lock") return 2;
        var lockPath = Directory.GetFiles(absolute, "*.lock").Single();
        using var lease = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await File.WriteAllTextAsync(control + ".held", "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(control + ".release")) await Task.Delay(25, timeout.Token);
        return 0;
    }

    private static Process Child(Uri origin, string directory, string mode, string control)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(RememberedPublisherAccountChecks).Assembly.Location);
        foreach (var argument in new[] { "--remembered-account-child", origin.AbsoluteUri, directory, mode, control }) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }
    private static async Task ChildAsync(Uri origin, string directory, string mode, string root, Action<bool, string> check)
    {
        using var child = Child(origin, directory, mode, Path.Combine(root, "child-restore"));
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); _ = await output; _ = await error;
        check(child.ExitCode == 0, "A separate process restores the current user's protected account without a browser");
    }
    private static async Task CrossProcessLockAsync(RememberedPublisherAccount account, Uri origin, string directory, string root, Action<bool, string> check)
    {
        var control = Path.Combine(root, "account-lock");
        using var child = Child(origin, directory, "lock", control);
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(control + ".held")) { if (child.HasExited) throw new Exception("Account lock fixture exited early."); await Task.Delay(25, deadline.Token); }
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { await account.GetRevisionAsync(cancellation.Token); throw new Exception("A concurrent account reader ignored the cross-process lock."); }
            catch (OperationCanceledException) { check(true, "An account lease held by another process honors caller cancellation"); }
        }
        finally { await File.WriteAllTextAsync(control + ".release", "release"); }
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); _ = await output; _ = await error;
        check(child.ExitCode == 0 && !string.IsNullOrEmpty(await account.GetRevisionAsync()), "Releasing the other process's account lease restores normal access");
    }

    private static PublisherSession Session(string token, DateTimeOffset expires) => (PublisherSession)Activator.CreateInstance(typeof(PublisherSession), BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null, args: [Username, token, false, expires], culture: null)!;
    private static object Redemption(string token, DateTimeOffset expires) => new { authenticated = true, username = Username, accessToken = token, isAdministrator = false, expiresUtc = expires };
    private static void VerifyRequest(Request request, string path, string token, Action<bool, string> check)
    {
        using var body = JsonDocument.Parse(request.Body);
        check(request.Method == "POST" && request.Path == path && request.Authorization == "Bearer " + token && !request.HasCookie && body.RootElement.GetProperty("application").GetString() == "heirowinstaller",
            "Account lifecycle request is bound to the installer application and only its current bearer");
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { internal DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed record Request(string Method, string Path, string? Authorization, bool HasCookie, byte[] Body);
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Func<Request, Task<(int Code, object Body)>>> responses = new();
        private readonly Task run;
        internal Uri Origin { get; }
        internal int Requests;
        internal Server()
        {
            listener.Start(); Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            run = RunAsync();
        }
        internal void Enqueue(Func<Request, (int Code, object Body)> response) => responses.Enqueue(request => Task.FromResult(response(request)));
        internal void Enqueue(Func<Request, Task<(int Code, object Body)>> response) => responses.Enqueue(response);
        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = connection.GetStream();
                    using var header = new MemoryStream(); var one = new byte[1];
                    while (header.Length < 65536)
                    {
                        if (await stream.ReadAsync(one, stop.Token) == 0) throw new EndOfStreamException();
                        header.WriteByte(one[0]); var bytes = header.GetBuffer(); var count = (int)header.Length;
                        if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
                    var fields = lines.Skip(1).Where(line => line.Contains(':')).Select(line => line.Split(':', 2)).ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    var length = int.TryParse(fields.GetValueOrDefault("Content-Length"), out var size) ? size : 0;
                    if (length is < 0 or > 65536) throw new InvalidDataException("Unbounded account fixture request.");
                    var body = new byte[length]; await stream.ReadExactlyAsync(body, stop.Token);
                    Interlocked.Increment(ref Requests);
                    if (!responses.TryDequeue(out var respond)) throw new InvalidOperationException("Unexpected account lifecycle request.");
                    var response = await respond(new(first[0], first[1], fields.GetValueOrDefault("Authorization"), fields.ContainsKey("Cookie"), body));
                    var json = JsonSerializer.SerializeToUtf8Bytes(response.Body);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Code} Fixture\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(json, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await run; stop.Dispose(); }
    }
}
