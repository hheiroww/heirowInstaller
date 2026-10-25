using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;
using Microsoft.Win32.SafeHandles;

namespace HeirowUpdater.SocketJack;

/// <summary>Local, mutually checked process-to-process session delivery. No credential payload is ever persisted.</summary>
internal sealed class ApplicationSessionRelay : IAsyncDisposable
{
    internal const int MaximumMessageBytes = 16 * 1024;
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource stop = new();
    private readonly string binding;
    private readonly string trustedImage;
    private readonly Func<ApplicationSessionSnapshot, string?, CancellationToken, Task<Reply>> accept;
    private readonly Task worker;
    private readonly Action<string>? rejected;
    private readonly HashSet<string> recentRequests = new(StringComparer.Ordinal);
    private readonly Queue<string> requestOrder = new();

    internal sealed record Reply(string RequestId, string? ManualRequestId, bool SessionAccepted);
    internal ApplicationSessionRelay(string binding, string trustedImage,
        Func<ApplicationSessionSnapshot, string?, CancellationToken, Task<Reply>> accept, Action<string>? rejected = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Application session relay requires Windows.");
        this.binding = binding; this.trustedImage = Path.GetFullPath(trustedImage); this.accept = accept; this.rejected = rejected;
        CheckProcess(System.Environment.ProcessId, null, this.trustedImage);
        pipe = new(PipeName(binding), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 4096, 4096);
        worker = ServeAsync();
    }

    internal static string Binding(UpdateCompanionConfiguration configuration)
    {
        var value = configuration.ProductId.ToLowerInvariant() + "\n" + Path.GetFullPath(configuration.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant() +
            "\n" + configuration.Scope + "\n" + configuration.PublisherKeySha256.ToLowerInvariant() + "\n" + new Uri(configuration.CredentialOrigin).GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    internal static async Task<Reply> SendAsync(string binding, string trustedImage, ApplicationSessionSnapshot snapshot, bool manual, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Application session relay requires Windows.");
        CheckProcess(System.Environment.ProcessId, null, trustedImage);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        using var client = new NamedPipeClientStream(".", PipeName(binding), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000, timeout.Token);
        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var serverStart = CheckProcess(checked((int)serverPid), null, trustedImage);
        var hello = await ReadAsync<Hello>(client, timeout.Token);
        using var self = Process.GetCurrentProcess();
        if (hello.FormatVersion != 1 || hello.Binding != binding || hello.ServerPid != serverPid || hello.ServerStart != serverStart ||
            hello.ClientPid != self.Id || hello.ClientStart != self.StartTime.ToUniversalTime().Ticks || !IsChallenge(hello.Challenge))
            throw new InvalidDataException("The updater session relay identity could not be verified.");
        CheckProcess(checked((int)serverPid), hello.ServerStart, trustedImage);
        var requestId = Guid.NewGuid().ToString("N");
        await WriteAsync(client, new Request
        {
            Binding = binding, Challenge = hello.Challenge, RequestId = requestId, Manual = manual,
            ClientPid = self.Id, ClientStart = self.StartTime.ToUniversalTime().Ticks,
            CapturedAt = snapshot.CapturedAtUtcTicks, SnapshotId = snapshot.Id, Cookie = snapshot.Cookie, Bearer = snapshot.BearerToken,
            SourcePid = snapshot.SourceProcessId, SourceStart = snapshot.SourceStartTicks, Sequence = snapshot.Sequence
        }, timeout.Token);
        var reply = await ReadAsync<Reply>(client, timeout.Token);
        if (reply.RequestId != requestId || (reply.ManualRequestId is not null && !Guid.TryParseExact(reply.ManualRequestId, "N", out _)))
            throw new InvalidDataException("The updater session relay acknowledgement was invalid.");
        await WriteAsync(client, new Completion { RequestId = requestId }, timeout.Token);
        return reply;
    }

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var stage = "connect";
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                stage = "peer";
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var clientStart = CheckProcess(checked((int)clientPid), null, trustedImage);
                using var self = Process.GetCurrentProcess();
                var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                stage = "hello";
                await WriteAsync(pipe, new Hello
                {
                    Binding = binding, Challenge = challenge, ServerPid = self.Id, ServerStart = self.StartTime.ToUniversalTime().Ticks,
                    ClientPid = checked((int)clientPid), ClientStart = clientStart
                }, timeout.Token);
                stage = "request";
                var request = await ReadAsync<Request>(pipe, timeout.Token);
                stage = "validate";
                CheckProcess(checked((int)clientPid), clientStart, trustedImage);
                if (request.FormatVersion != 1 || request.Binding != binding || request.Challenge != challenge || request.ClientPid != clientPid ||
                    request.ClientStart != clientStart || !Guid.TryParseExact(request.RequestId, "N", out _) || !Guid.TryParseExact(request.SnapshotId, "N", out _) ||
                    request.CapturedAt < DateTimeOffset.UtcNow.AddMinutes(-10).UtcTicks || request.CapturedAt > DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks ||
                    request.SourcePid <= 0 || request.SourceStart <= 0 || request.Sequence <= 0 || !recentRequests.Add(request.RequestId))
                    throw new InvalidDataException("The updater session relay request was invalid.");
                requestOrder.Enqueue(request.RequestId);
                if (requestOrder.Count > 128) recentRequests.Remove(requestOrder.Dequeue());
                var snapshot = new ApplicationSessionSnapshot(request.Cookie, request.Bearer, request.CapturedAt, request.SnapshotId, request.SourcePid, request.SourceStart, request.Sequence);
                stage = "accept";
                var reply = await accept(snapshot, request.Manual ? request.RequestId : null, timeout.Token);
                stage = "reply";
                await WriteAsync(pipe, reply with { RequestId = request.RequestId }, timeout.Token);
                // DisconnectNamedPipe discards unread data. Wait asynchronously for proof that this client consumed its acknowledgement.
                stage = "receipt";
                var completion = await ReadAsync<Completion>(pipe, timeout.Token);
                if (completion.RequestId != request.RequestId) throw new InvalidDataException("The updater session relay completion was invalid.");
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException or JsonException or Win32Exception or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Neither malformed messages nor credential data are included in diagnostics or persisted state.
                rejected?.Invoke(stage + ":" + error.GetType().Name);
            }
            finally
            {
                // A peer can close immediately after its acknowledgement. Broken instances still need Disconnect before reuse.
                try { pipe.Disconnect(); } catch (InvalidOperationException) { } catch (IOException) { }
            }
        }
    }

    internal static long CheckProcess(int pid, long? start, string trustedImage)
    {
        PackageSafety.EnsureNoReparsePoints(trustedImage);
        using var process = Process.GetProcessById(pid);
        using var self = Process.GetCurrentProcess();
        var actualStart = process.StartTime.ToUniversalTime().Ticks;
        if (process.HasExited || process.SessionId != self.SessionId || (start is not null && start != actualStart) ||
            process.MainModule?.FileName is not { } image || !Path.GetFullPath(image).Equals(Path.GetFullPath(trustedImage), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The updater session relay process identity was invalid.");
        return actualStart;
    }

    internal static string PipeName(string binding)
    {
        if (binding.Length != 64 || !binding.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid updater session relay binding.");
        var user = System.Environment.UserName;
        if (OperatingSystem.IsWindows()) { using var identity = WindowsIdentity.GetCurrent(); user = identity.User?.Value ?? user; }
        using var self = Process.GetCurrentProcess();
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "\n" + self.SessionId))).ToLowerInvariant()[..24];
        return "heirowUpdater.session." + scope + "." + binding;
    }
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Updater session relay message exceeded its bound.");
        try
        {
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
            await stream.WriteAsync(prefix, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Updater session relay message exceeded its bound.");
        var bytes = new byte[length];
        try { await stream.ReadExactlyAsync(bytes, token); return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty updater session relay message."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static bool IsChallenge(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; } catch (OperationCanceledException) { }
        pipe.Dispose(); stop.Dispose();
    }
    private sealed class Hello
    {
        public int FormatVersion { get; set; } = 1;
        public string Binding { get; set; } = "";
        public string Challenge { get; set; } = "";
        public int ServerPid { get; set; }
        public long ServerStart { get; set; }
        public int ClientPid { get; set; }
        public long ClientStart { get; set; }
    }
    private sealed class Request
    {
        public int FormatVersion { get; set; } = 1;
        public string Binding { get; set; } = "";
        public string Challenge { get; set; } = "";
        public string RequestId { get; set; } = "";
        public bool Manual { get; set; }
        public int ClientPid { get; set; }
        public long ClientStart { get; set; }
        public long CapturedAt { get; set; }
        public string SnapshotId { get; set; } = "";
        public int SourcePid { get; set; }
        public long SourceStart { get; set; }
        public long Sequence { get; set; }
        public string? Cookie { get; set; }
        public string? Bearer { get; set; }
    }
    private sealed class Completion { public string RequestId { get; set; } = ""; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
