namespace HeirowInstaller.Core;

/// <summary>Signals that the managed window has drawn its first frame.</summary>
public static class BootstrapReadiness
{
    public const string Variable = "SOCKETJACK_BOOTSTRAP_READY";
    private static int signalled;

    public static void Signal()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.Exchange(ref signalled, 1) != 0) return;
        var name = Environment.GetEnvironmentVariable(Variable);
        const string prefix = @"Local\SocketJack.BootstrapReady.";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return;
        try { using var ready = EventWaitHandle.OpenExisting(name); ready.Set(); }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { }
    }
}
