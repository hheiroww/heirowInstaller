using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.Core;

public static partial class RunningApplications
{
    private static readonly object InspectionLock = new();
    private static Scan? activeScan;
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CloseAttemptTimeout = TimeSpan.FromSeconds(8);
    private static Func<RunningApplicationTarget, IReadOnlyList<RunningApplication>>? inspectionTestProbe;
    private sealed record Scan(string Key, Task<IReadOnlyList<RunningApplication>> Task);

    private static async Task<IReadOnlyList<RunningApplication>> InspectWithinAsync(RunningApplicationTarget target,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        // RM and module enumeration are native synchronous operations. A deadline bounds
        // the caller, while one shared read-only worker bounds abandoned native work.
        // No worker can send WM_CLOSE or terminate a process after its caller returns.
        var watch = Stopwatch.StartNew();
        var key = string.Join('\n', new[] { target.InstallDirectory, target.ExecutablePath ?? "" }
            .Concat(target.AffectedResourceFiles).Concat(target.WaitOnlyExecutables).Concat(target.ExcludedExecutables)
            .Concat(target.WaitOnlyProcesses.Select(p => $"{p.ProcessId}:{p.StartTimeUtcTicks}:{p.ExecutablePath}")));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Windows application inspection exceeded its deadline.");
            Scan scan;
            lock (InspectionLock)
            {
                if (activeScan is null || activeScan.Task.IsCompleted)
                {
                    var probe = inspectionTestProbe;
                    var task = Task.Run(() => probe is null ? Inspect(target, CancellationToken.None) : probe(target));
                    // Observe errors even when every waiter has canceled or timed out.
                    _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    activeScan = new(key, task);
                }
                scan = activeScan;
            }
            var result = await scan.Task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (scan.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return result;
            // A different target must wait for the single worker, never inherit its inventory.
        }
    }

    private static bool TryForceClose(RunningApplicationTarget target, RunningApplication application, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !application.CanForceClose || application.Kind != "application" ||
            application.ProcessId <= 0 || application.ProcessId == Environment.ProcessId || application.StartTimeUtcTicks <= 0 ||
            !target.OwnedApplicationImages.Contains(application.ExecutablePath) ||
            !application.AffectedPath.Equals(application.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            target.ExcludedExecutables.Contains(application.ExecutablePath) ||
            target.WaitOnlyExecutables.Contains(application.ExecutablePath, StringComparer.OrdinalIgnoreCase) ||
            target.WaitOnlyProcesses.Any(p => SameIdentity(p, application)) ||
            RunningApplicationTarget.IsSystemPath(application.ExecutablePath)) return false;
        try
        {
            PackageSafety.EnsureNoReparsePoints(application.ExecutablePath);
            // Hold the original process object through validation and termination. Opening
            // a second handle by PID after validation would permit a PID-reuse race.
            using var handle = OpenProcess(0x1000 | 0x100000 | 0x0001, false, application.ProcessId);
            if (handle.IsInvalid || !GetProcessTimes(handle, out var creation, out _, out _, out _) ||
                DateTime.FromFileTimeUtc(creation).Ticks != application.StartTimeUtcTicks ||
                !GetExitCodeProcess(handle, out var exitCode) || exitCode != 259) return false;
            var image = new StringBuilder(32768); var length = image.Capacity;
            if (!QueryFullProcessImageName(handle, 0, image, ref length) ||
                !Path.GetFullPath(image.ToString()).Equals(application.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !ProcessIdToSessionId((uint)application.ProcessId, out var session)) return false;
            using var current = Process.GetCurrentProcess();
            if (session == 0 || session != current.SessionId) return false;
            // The exact executable is an authored target resource; no new RM scan is
            // needed (or allowed) on this action path. Unknown identities stay manual.
            token.ThrowIfCancellationRequested();
            return TerminateProcess(handle, 1); // One retained process object, never descendants.
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    // Fixture-only seams exercise a stuck native query and adversarial snapshots with
    // the same production validation. Neither is available to application callers.
    internal static IDisposable OverrideInspectionForTests(Func<RunningApplicationTarget, IReadOnlyList<RunningApplication>> probe)
    {
        lock (InspectionLock)
        {
            if (activeScan is { Task.IsCompleted: false } || inspectionTestProbe is not null)
                throw new InvalidOperationException("An inspection is already active.");
            activeScan = null; inspectionTestProbe = probe;
            return new InspectionTestScope();
        }
    }
    private sealed class InspectionTestScope : IDisposable
    {
        public void Dispose() { lock (InspectionLock) inspectionTestProbe = null; }
    }
    internal static bool ForceCloseForTests(RunningApplicationTarget target, RunningApplication application, CancellationToken token = default) =>
        TryForceClose(target, application, token);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint processId, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
