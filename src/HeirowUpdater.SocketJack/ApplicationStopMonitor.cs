using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

/// <summary>Polite per-root maintenance cancellation; it never terminates the application.</summary>
public sealed class ApplicationStopMonitor : IDisposable
{
    private readonly EventWaitHandle signal;
    private readonly CancellationTokenSource cancellation;
    private readonly RegisteredWaitHandle registration;
    public CancellationToken Token => cancellation.Token;
    public bool StopRequested => signal.WaitOne(0);
    public void RequestStop() => signal.Set();

    public ApplicationStopMonitor(string installDirectory, bool resetForOwner, CancellationToken token = default)
    {
        signal = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateCompanionControl.GetStopEventName(installDirectory));
        if (resetForOwner) signal.Reset();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }
    public void Dispose() { registration.Unregister(null); cancellation.Dispose(); signal.Dispose(); }
}
