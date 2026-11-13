using System.Net;
using System.Net.Sockets;
using System.Text;
using HeirowInstaller.App;
using HeirowInstaller.Core;
using HeirowUpdater.SocketJack;

internal static partial class Program
{
    static void StreamingRemoteSetup()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        StreamingSetupTransportAsync(deadline.Token).GetAwaiter().GetResult();
        StreamingSetupWizard(deadline.Token);
    }

    static async Task StreamingSetupTransportAsync(CancellationToken token)
    {
        using var origin = Listen();
        using var redirect = Listen();
        var payload = Encoding.ASCII.GetBytes("synthetic signed package body fixture");
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originTask = RespondAsync(origin, async (headers, stream) =>
        {
            Check(headers.Contains("Authorization: Bearer synthetic-token", StringComparer.OrdinalIgnoreCase) &&
                headers.Contains("Cookie: fixture=synthetic-cookie", StringComparer.OrdinalIgnoreCase),
                "the exact credential origin receives both configured fixture headers");
            await WriteAsync(stream, $"HTTP/1.1 302 Found\r\nLocation: {Endpoint(redirect)}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
        }, token);
        var redirectTask = RespondAsync(redirect, async (headers, stream) =>
        {
            Check(!headers.Any(header => header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) ||
                header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)), "a cross-origin redirect receives no credential headers");
            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n", token);
            await stream.WriteAsync(payload.AsMemory(0, 6), token);
            await firstRead.Task.WaitAsync(token);
            await stream.WriteAsync(payload.AsMemory(6), token);
        }, token);
        using var transport = new SetupPackageTransport(Endpoint(origin), new("fixture=synthetic-cookie", "synthetic-token"));
        using var client = new UpdateClient(allowLoopback: true, transport: transport);
        var output = Path.Combine(root, "streaming-transport");
        var feed = new UpdateFeed
        {
            ProductId = "setup-fixture", Version = "1.0.0", PackageType = "hinst", InstallerUrl = Endpoint(origin).AbsoluteUri,
            Size = payload.Length, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))
        };
        var reports = new List<InstallProgress>();
        var progress = new SetupDownloadProgress(value =>
        {
            reports.Add(value);
            if (value.CompletedBytes == 6) firstRead.TrySetResult();
        });
        var file = await client.DownloadStreamingApplicationAsync(feed, output, TimeSpan.FromSeconds(2), progress, token);
        await Task.WhenAll(originTask, redirectTask);
        Check(reports.Count == 3 && reports[0].CompletedBytes == 0 && reports[1].CompletedBytes == 6 && reports[1].TotalBytes == payload.Length &&
            reports[^1].CompletedBytes == payload.Length && (await File.ReadAllBytesAsync(file, token)).SequenceEqual(payload),
            "download reports actual bytes while the network body is still arriving and commits only verified bytes");

        using var stalled = Listen();
        var stalledTask = RespondAsync(stalled, async (_, stream) =>
        {
            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n", token);
            await stream.WriteAsync(payload.AsMemory(0, 6), token);
            var buffer = new byte[1];
            Check(await stream.ReadAsync(buffer, token) == 0, "the timed-out request closes its network connection");
        }, token);
        feed.InstallerUrl = Endpoint(stalled).AbsoluteUri;
        var stalledOutput = Path.Combine(root, "streaming-idle");
        try
        {
            await client.DownloadStreamingApplicationAsync(feed, stalledOutput, TimeSpan.FromMilliseconds(200), cancellationToken: token);
            throw new InvalidOperationException("The stalled setup body unexpectedly completed.");
        }
        catch (TimeoutException)
        {
            Check(!Directory.EnumerateFiles(stalledOutput).Any(), "idle timeout fails distinctly and never promotes incomplete setup bytes");
        }
        await stalledTask.WaitAsync(token);
    }

    static void StreamingSetupWizard(CancellationToken token)
    {
        var fixture = Build("remote-progress", includeLicense: false);
        var bytes = File.ReadAllBytes(fixture.Package);
        using var listener = Listen();
        var continueBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = RespondAsync(listener, async (_, stream) =>
        {
            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n", token);
            await stream.WriteAsync(bytes.AsMemory(0, 64), token);
            await continueBody.Task.WaitAsync(token);
            await Task.Delay(TimeSpan.FromMilliseconds(1100), token);
            await stream.WriteAsync(bytes.AsMemory(64, 128), token);
            await finishBody.Task.WaitAsync(token);
            await stream.WriteAsync(bytes.AsMemory(192), token);
        }, token);
        var arguments = Arguments(fixture) with
        {
            PackagePath = null,
            RemotePackageUrl = Endpoint(listener).AbsoluteUri,
            RemotePackageSize = bytes.Length
        };
        using var form = NewForm(arguments);
        var load = form.InitializeForTestsAsync();
        Until(form, () => form.Controller.StatusDetail.Contains($"64 of {bytes.Length:N0} bytes", StringComparison.Ordinal));
        Check(form.Controller.Manifest is null && form.Controller.Progress > 0 && form.Controller.Progress < 1 &&
            form.Controller.StatusDetail.Contains($"{64d / bytes.Length:P0}", StringComparison.Ordinal),
            "hidden CPU wizard displays exact live byte progress before setup is verified");
        continueBody.TrySetResult();
        Until(form, () => form.Controller.StatusDetail.Contains($"192 of {bytes.Length:N0} bytes", StringComparison.Ordinal));
        Check(form.Controller.StatusDetail.Contains("remaining", StringComparison.Ordinal) && !form.Controller.StatusDetail.Contains("Estimating", StringComparison.Ordinal),
            "a second timed receive sample produces a smoothed time estimate");
        finishBody.TrySetResult();
        Pump(form, load);
        transfer.GetAwaiter().GetResult();
        Check(!form.Controller.Failed && form.Controller.Manifest?.Id == fixture.Id && form.Controller.Scope == "user" &&
            !Directory.Exists(fixture.Target), "streamed package opens verified setup review without installing or changing its scope");
        Hidden(form);
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static Uri Endpoint(TcpListener listener) => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/app.hinst");

    private static async Task RespondAsync(TcpListener listener, Func<List<string>, NetworkStream, Task> respond, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new List<string>();
        while (await reader.ReadLineAsync(token) is { Length: > 0 } header) headers.Add(header);
        await respond(headers, stream);
    }

    private static ValueTask WriteAsync(NetworkStream stream, string value, CancellationToken token)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(value), token);
}
