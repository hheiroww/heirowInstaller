using HeirowInstaller.Core;

namespace HeirowUpdater;

internal sealed class PromptCancellation : IDisposable
{
    private readonly EventWaitHandle? signal;
    private readonly RegisteredWaitHandle? registration;
    private readonly System.Threading.Timer? timer;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string path;
    private readonly bool owner;
    internal CancellationToken Token => cancellation.Token;
    internal PromptCancellation(string requestPath, string eventName, bool owner)
    {
        this.owner = owner; path = requestPath + ".cancel"; PackageSafety.EnsureNoReparsePoints(path);
        if (OperatingSystem.IsWindows())
        {
            if (owner) signal = new(false, EventResetMode.ManualReset, eventName);
            else if (EventWaitHandle.TryOpenExisting(eventName, out var existing)) signal = existing;
            else throw new InvalidDataException("The prompt cancellation owner is missing.");
            registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Cancel(), null, Timeout.Infinite, true);
        }
        else
        {
            if (owner && File.Exists(path)) File.Delete(path);
            timer = new System.Threading.Timer(_ => { if (File.Exists(path)) Cancel(); }, null, 0, 100);
        }
    }
    private void Cancel() { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    internal void Set() { if (signal is not null) signal.Set(); else { PackageSafety.EnsureNoReparsePoints(path); File.WriteAllText(path, "cancel"); } }
    public void Dispose()
    {
        timer?.Dispose(); registration?.Unregister(null); signal?.Dispose(); cancellation.Dispose();
        if (owner && File.Exists(path)) File.Delete(path);
    }
}
