using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HeirowInstaller.App;

var state = new DesktopCapturePresentation();
using (var initial = state.BeginOpaque())
{
    var generation = state.RenderState.PendingGeneration;
    Check(state.RenderState.Opaque && generation > 0 && !initial.Presented.IsCompleted, "An opaque lease waits for presentation");
    state.AcknowledgeOpaque(generation - 1);
    Check(!initial.Presented.IsCompleted, "An older presentation cannot acknowledge a new lease");
    state.AcknowledgeOpaque(generation);
    await initial.Presented;
    Check(state.RenderState.PendingGeneration == 0, "An acknowledged generation needs no further native wait");
}
Check(!state.RenderState.Opaque, "Releasing the last lease restores local blur");
state.SetTeamViewerSharing(true);
using (state.BeginOpaque()) { }
Check(state.RenderState.Opaque, "A screenshot ending does not cancel ongoing screen sharing");
Check(state.RenderState.PendingGeneration == 0, "Screen sharing alone needs no screenshot presentation wait");
state.SetTeamViewerSharing(false);
Check(!state.RenderState.Opaque, "Ending sharing clears its opaque override");
Check(DesktopCapturePipe.IsTeamViewerClient("TeamViewerQS", 4, 4), "QuickSupport is recognized in the interactive session");
Check(!DesktopCapturePipe.IsTeamViewerClient("TeamViewer", 0, 4), "A client in another session does not alter appearance");
Check(!DesktopCapturePipe.IsTeamViewerClient("TeamViewer_Service", 4, 4) &&
    !DesktopCapturePipe.IsTeamViewerClient("TeamViewer_Desktop", 4, 4), "Persistent service and helper are excluded");

if (!OperatingSystem.IsWindows()) return;

using var process = Process.GetCurrentProcess();
var name = FormattableString.Invariant($"SocketJack.ProjectZ.DesktopCapture.v1.{process.SessionId}.{process.Id}");
var sample = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
var pendingPng = sample;
using var server = new DesktopCapturePipe(state, _ => Task.FromResult<byte[]?>(pendingPng), watchTeamViewer: false);
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

using (var first = await ConnectAsync((byte)'O'))
using (var second = await ConnectAsync((byte)'O'))
{
    var firstResponse = StatusAsync(first);
    var secondResponse = StatusAsync(second);
    await WaitForAsync(() => state.RenderState.Opaque);
    Check(!firstResponse.IsCompleted && !secondResponse.IsCompleted, "The pipe sends no success before a presentation");
    state.AcknowledgeOpaque(state.RenderState.PendingGeneration);
    Check(await firstResponse == 1 && await secondResponse == 1, "Both requests share the acknowledged opaque frame");
    first.Dispose();
    Check(state.RenderState.Opaque, "A second connected client keeps the renderer opaque");
}
await WaitForAsync(() => !state.RenderState.Opaque);
Check(true, "Pipe disconnect releases the last lease");

using (var abandoned = await ConnectAsync((byte)'O'))
{
    await WaitForAsync(() => state.RenderState.Opaque);
}
await WaitForAsync(() => !state.RenderState.Opaque);
Check(true, "A client can release the lease before presentation");

using (var timedOut = await ConnectAsync((byte)'O'))
    Check(await StatusAsync(timedOut) == 0, "A presentation timeout reports unsupported");
await WaitForAsync(() => !state.RenderState.Opaque);
Check(true, "A presentation timeout releases its lease");

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

static void Check(bool value, string description)
{
    if (!value) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}
