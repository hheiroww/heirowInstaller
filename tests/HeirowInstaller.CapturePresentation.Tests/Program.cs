using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HeirowInstaller.App;

if (args is ["--renderer", var rendererPid, "--png", var screenshotPath])
{
    await VerifyOpaqueRendererAsync(int.Parse(rendererPid), screenshotPath);
    return;
}

var state = new DesktopCapturePresentation();
long firstGeneration;
using (var initial = state.BeginOpaque())
{
    firstGeneration = state.PendingGeneration;
    Check(firstGeneration > 0 && !initial.Presented.IsCompleted, "An already opaque renderer still waits for a presented frame");
    state.AcknowledgeOpaque(firstGeneration - 1);
    Check(!initial.Presented.IsCompleted, "An older presentation cannot acknowledge a new lease");
    state.AcknowledgeOpaque(firstGeneration);
    await initial.Presented;
    Check(state.PendingGeneration == 0, "An acknowledged generation needs no further native wait");
}
using (var next = state.BeginOpaque())
{
    Check(state.PendingGeneration > firstGeneration && !next.Presented.IsCompleted, "The next capture needs its own presented frame");
}
Check(state.PendingGeneration == 0, "Releasing an unacknowledged lease clears its pending frame");

if (!OperatingSystem.IsWindows()) return;

using var process = Process.GetCurrentProcess();
var name = CapturePipeName(process);
var sample = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
var pendingPng = sample;
using var server = new DesktopCapturePipe(state, _ => Task.FromResult<byte[]?>(pendingPng));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

async Task<NamedPipeClientStream> ConnectAsync(byte action)
{
    var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(timeout.Token);
    await pipe.WriteAsync(new[] { action }, timeout.Token);
    return pipe;
}

async Task<byte> StatusAsync(Stream pipe)
{
    var buffer = new byte[1];
    await pipe.ReadExactlyAsync(buffer, timeout.Token);
    return buffer[0];
}

async Task WaitForAsync(Func<bool> condition)
{
    while (!condition()) await Task.Delay(15, timeout.Token);
}

using (var abandoned = await ConnectAsync((byte)'O'))
    await WaitForAsync(() => state.PendingGeneration != 0);
await WaitForAsync(() => state.PendingGeneration == 0);
Check(true, "A client can release the lease before presentation");

using (var timedOut = await ConnectAsync((byte)'O'))
    Check(await StatusAsync(timedOut) == 0, "A presentation timeout reports unsupported");
await WaitForAsync(() => state.PendingGeneration == 0);
Check(true, "A presentation timeout releases its lease");

using (var first = await ConnectAsync((byte)'O'))
using (var second = await ConnectAsync((byte)'O'))
{
    var firstResponse = StatusAsync(first);
    var secondResponse = StatusAsync(second);
    await WaitForAsync(() => state.PendingGeneration != 0);
    Check(!firstResponse.IsCompleted && !secondResponse.IsCompleted, "The pipe sends no success before a presentation");
    state.AcknowledgeOpaque(state.PendingGeneration);
    Check(await firstResponse == 1 && await secondResponse == 1, "Both requests share the acknowledged opaque frame");
}

using (var screenshot = await ConnectAsync((byte)'P'))
{
    using var identity = WindowsIdentity.GetCurrent();
    var access = screenshot.GetAccessControl();
    var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
    var rules = access.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).OfType<PipeAccessRule>();
    Check(access.GetOwner(typeof(SecurityIdentifier)) == identity.User && rules.Any(rule =>
        rule.IdentityReference.Equals(network) && rule.AccessControlType == AccessControlType.Deny),
        "The pipe is owned by the current user and denies network clients");
    var header = new byte[5];
    await screenshot.ReadExactlyAsync(header, timeout.Token);
    var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
    Check(header[0] == 1 && length == sample.Length, "Composed PNG uses a status byte and little-endian length");
    var delivered = new byte[length];
    await screenshot.ReadExactlyAsync(delivered, timeout.Token);
    Check(delivered.AsSpan().SequenceEqual(sample), "Composed PNG bytes arrive unchanged");
}
pendingPng = [];
using (var noFrame = await ConnectAsync((byte)'P'))
    Check(await StatusAsync(noFrame) == 0, "An unavailable composed frame returns unsupported");
using (var invalid = await ConnectAsync((byte)'?'))
    Check(await StatusAsync(invalid) == 0, "Unknown commands are rejected");

static string CapturePipeName(Process process) =>
    FormattableString.Invariant($"SocketJack.ProjectZ.DesktopCapture.v1.{process.SessionId}.{process.Id}");

static async Task VerifyOpaqueRendererAsync(int rendererPid, string screenshotPath)
{
    using var renderer = Process.GetProcessById(rendererPid);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var lease = new NamedPipeClientStream(".", CapturePipeName(renderer), PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await lease.ConnectAsync(deadline.Token);
    await lease.WriteAsync(new[] { (byte)'O' }, deadline.Token);
    var status = new byte[1];
    await lease.ReadExactlyAsync(status, deadline.Token);
    Check(status[0] == 1, "The real opaque installer acknowledged a presented frame");

    using var screenshot = new NamedPipeClientStream(".", CapturePipeName(renderer), PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await screenshot.ConnectAsync(deadline.Token);
    await screenshot.WriteAsync(new[] { (byte)'P' }, deadline.Token);
    var header = new byte[5];
    await screenshot.ReadExactlyAsync(header, deadline.Token);
    var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
    Check(header[0] == 1 && length is > 24 and <= DesktopCapturePipe.MaximumPngBytes,
        "The real opaque installer returned a bounded composed frame");
    var png = new byte[length];
    await screenshot.ReadExactlyAsync(png, deadline.Token);
    Check(png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "The composed frame has a PNG signature");
    await File.WriteAllBytesAsync(screenshotPath, png, deadline.Token);
}

static void Check(bool value, string description)
{
    if (!value) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}
