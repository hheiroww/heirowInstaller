namespace HeirowInstaller.App;

/// <summary>Tracks which opaque frame the capture clients are waiting to see.</summary>
internal sealed class DesktopCapturePresentation
{
    private readonly object gate = new();
    private TaskCompletionSource? ready;
    private int leases;
    private long generation;
    internal long PendingGeneration
    {
        get
        {
            lock (gate)
                return ready is { Task.IsCompleted: false } ? generation : 0;
        }
    }

    internal OpaqueLease BeginOpaque()
    {
        lock (gate)
        {
            if (leases++ == 0)
            {
                generation++;
                ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return new(this, ready!.Task);
        }
    }

    internal void AcknowledgeOpaque(long presentedGeneration)
    {
        lock (gate)
        {
            if (leases > 0 && generation == presentedGeneration)
                ready!.TrySetResult();
        }
    }

    private void EndOpaque()
    {
        lock (gate)
        {
            if (--leases == 0)
            {
                ready!.TrySetCanceled();
                ready = null;
            }
        }
    }

    internal sealed class OpaqueLease(DesktopCapturePresentation owner, Task presented) : IDisposable
    {
        private DesktopCapturePresentation? presentation = owner;
        internal Task Presented => presented;

        public void Dispose() => Interlocked.Exchange(ref presentation, null)?.EndOpaque();
    }
}
