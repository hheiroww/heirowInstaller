using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

/// <summary>Polite per-root maintenance cancellation; it never terminates the application.</summary>
public sealed class ApplicationStopMonitor : IDisposable
{
    private readonly EventWaitHandle? signal;
    private readonly Timer? poller;
    private readonly string? portablePath;
    private readonly CancellationTokenSource cancellation;
    private readonly RegisteredWaitHandle? registration;
    private readonly FileStream? observer;
    private readonly string? observerPath;
    public CancellationToken Token => cancellation.Token;
    public bool StopRequested => signal?.WaitOne(0) ?? cancellation.IsCancellationRequested;
    public void RequestStop() { if (signal is not null) signal.Set(); else cancellation.Cancel(); }

    public ApplicationStopMonitor(string installDirectory, bool resetForOwner, CancellationToken token = default)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!OperatingSystem.IsWindows())
        {
            portablePath = UpdateCompanionControl.PortableStopPath(installDirectory);
            if (resetForOwner && File.Exists(portablePath)) File.Delete(portablePath);
            poller = new Timer(_ => { try { if (File.Exists(portablePath)) cancellation.Cancel(); } catch (ObjectDisposedException) { } }, null, 0, 100);
            return;
        }
        signal = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateCompanionControl.GetStopEventName(installDirectory));
        if (resetForOwner) signal.Reset();
        registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }
    public ApplicationStopMonitor(UpdateCompanionConfiguration configuration, bool resetForOwner, CancellationToken token = default)
        : this(configuration.InstallDirectory, resetForOwner, token)
    {
        if (configuration.SharedRuntime is null) return;
        try
        {
            var directory = Path.Combine(SharedUpdaterRuntime.ApplicationStateDirectory(configuration, create: true), "observers");
            PackageSafety.EnsureNoReparsePoints(directory); Directory.CreateDirectory(directory);
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            observerPath = Path.Combine(directory, self.Id + "-" + self.StartTime.ToUniversalTime().Ticks + "-" + Guid.NewGuid().ToString("N") + ".lock");
            observer = new FileStream(observerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        poller?.Dispose(); registration?.Unregister(null); cancellation.Dispose(); signal?.Dispose(); observer?.Dispose();
        if (observerPath is not null) try { File.Delete(observerPath); } catch (IOException) { }
    }
}
