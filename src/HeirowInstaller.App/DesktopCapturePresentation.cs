namespace HeirowInstaller.App;

/// <summary>Tracks temporary opacity independently of the user's backdrop settings.</summary>
internal sealed class DesktopCapturePresentation
{
    private readonly object gate = new();
    private TaskCompletionSource? ready;
    private int leases;
    private long generation;
    private bool sharing;

    internal (bool Opaque, long PendingGeneration) RenderState
    {
        get
        {
            lock (gate)
                return (sharing || leases > 0, leases > 0 && !ready!.Task.IsCompleted ? generation : 0);
        }
    }

    internal void SetTeamViewerSharing(bool active)
    {
        lock (gate) sharing = active;
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
