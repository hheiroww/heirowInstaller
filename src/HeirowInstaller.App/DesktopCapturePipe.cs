using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.App;

/// <summary>Same-user, same-session capture protocol for the rendered installer window.</summary>
internal sealed class DesktopCapturePipe : IDisposable
{
    internal const int MaximumPngBytes = 32 * 1024 * 1024;
    private const int Connections = 8;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(5);
    private static readonly string[] TeamViewerClients = ["TeamViewer", "TeamViewerQS", "TeamViewerMeeting"];
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task[] workers;
    private readonly int sessionId;
    private readonly string pipeName;
    private readonly DesktopCapturePresentation presentation;
    private readonly Func<CancellationToken, Task<byte[]?>> capture;
    private int disposed;

    internal DesktopCapturePipe(DesktopCapturePresentation presentation, Func<CancellationToken, Task<byte[]?>> capture,
        bool watchTeamViewer = true)
    {
        this.presentation = presentation;
        this.capture = capture;
        using var current = Process.GetCurrentProcess();
        sessionId = current.SessionId;
        pipeName = FormattableString.Invariant($"SocketJack.ProjectZ.DesktopCapture.v1.{sessionId}.{Environment.ProcessId}");
        if (watchTeamViewer)
            UpdateTeamViewerState();
        var token = shutdown.Token;
        var listeners = Enumerable.Range(0, Connections).Select(_ => Task.Run(() => AcceptAsync(token)));
        workers = (watchTeamViewer ? listeners.Append(Task.Run(() => WatchTeamViewerAsync(token))) : listeners).ToArray();
    }

    private async Task AcceptAsync(CancellationToken stopping)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The capture pipe requires a Windows user.");
        var access = new PipeSecurity();
        access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        access.SetOwner(user);
        access.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        access.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, Connections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, access);
                await pipe.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                if (IsSessionClient(pipe.SafePipeHandle))
                    await HandleAsync(pipe, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (IOException)
            {
                if (!stopping.IsCancellationRequested)
                    await Task.Delay(100, stopping).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        var protocolByte = new byte[1];
        var successStarted = false;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        request.CancelAfter(RequestTimeout);
        try
        {
            if (await pipe.ReadAsync(protocolByte, request.Token).ConfigureAwait(false) == 0) return;
            switch (protocolByte[0])
            {
                case (byte)'O':
                    using (var lease = presentation.BeginOpaque())
                    {
                        var disconnected = pipe.ReadAsync(new byte[1].AsMemory(), request.Token).AsTask();
                        var presented = lease.Presented.WaitAsync(request.Token);
                        if (await Task.WhenAny(presented, disconnected).ConfigureAwait(false) == disconnected)
                        {
                            await disconnected.ConfigureAwait(false);
                            return;
                        }
                        await presented.ConfigureAwait(false);
                        successStarted = true;
                        await pipe.WriteAsync(new byte[] { 1 }, request.Token).ConfigureAwait(false);
                        // EOF, an unexpected extra byte, or the lease deadline restores local mode.
                        request.CancelAfter(MaximumLease);
                        await disconnected.ConfigureAwait(false);
                    }
                    break;
                case (byte)'P':
                    var png = await capture(request.Token).WaitAsync(request.Token).ConfigureAwait(false);
                    if (png is not { Length: > 0 and <= MaximumPngBytes })
                    {
                        await pipe.WriteAsync(new byte[] { 0 }, request.Token).ConfigureAwait(false);
                        break;
                    }
                    var header = new byte[5];
                    header[0] = 1;
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), png.Length);
                    successStarted = true;
                    await pipe.WriteAsync(header, request.Token).ConfigureAwait(false);
                    await pipe.WriteAsync(png, request.Token).ConfigureAwait(false);
                    break;
                default:
                    await pipe.WriteAsync(new byte[] { 0 }, request.Token).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            // A bounded request can fail before its optional success response.
            if (!successStarted && protocolByte[0] is (byte)'O' or (byte)'P')
            {
                using var response = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                response.CancelAfter(TimeSpan.FromMilliseconds(250));
                try { await pipe.WriteAsync(new byte[] { 0 }, response.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
    }

    private bool IsSessionClient(SafePipeHandle handle) =>
        GetNamedPipeClientSessionId(handle, out var clientSession) && clientSession == sessionId;

    internal static bool IsTeamViewerClient(string name, int processSession, int currentSession) =>
        currentSession == processSession && TeamViewerClients.Contains(name, StringComparer.OrdinalIgnoreCase);

    private async Task WatchTeamViewerAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            UpdateTeamViewerState();
    }

    private void UpdateTeamViewerState()
    {
        var active = false;
        foreach (var name in TeamViewerClients)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try { active |= IsTeamViewerClient(process.ProcessName, process.SessionId, sessionId); }
                    catch (InvalidOperationException) { } // The process can leave between listing and inspection.
                    catch (Win32Exception) { }
                }
            }
        }
        presentation.SetTeamViewerSharing(active);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        _ = Task.WhenAll(workers).ContinueWith(completed =>
        {
            _ = completed.Exception;
            shutdown.Dispose();
        }, TaskScheduler.Default);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out int sessionId);
}
