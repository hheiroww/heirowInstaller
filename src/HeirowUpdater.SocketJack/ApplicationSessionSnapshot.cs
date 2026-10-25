using System.Text.Json.Serialization;

namespace HeirowUpdater.SocketJack;

/// <summary>A complete in-memory replacement of an application's current session. Null values explicitly clear prior credentials.</summary>
public sealed class ApplicationSessionSnapshot
{
    private static long sequence;
    [JsonIgnore] public string? Cookie { get; }
    [JsonIgnore] public string? BearerToken { get; }
    public long CapturedAtUtcTicks { get; }
    public string Id { get; }
    public int SourceProcessId { get; }
    public long SourceStartTicks { get; }
    public long Sequence { get; }

    public ApplicationSessionSnapshot(string? sessionCookie = null, string? bearerToken = null)
        : this(sessionCookie, bearerToken, DateTimeOffset.UtcNow.UtcTicks, Guid.NewGuid().ToString("N"),
            System.Environment.ProcessId, CurrentStart(), Interlocked.Increment(ref sequence)) { }

    internal ApplicationSessionSnapshot(string? cookie, string? bearer, long capturedAt, string id, int sourcePid = 0, long sourceStart = 0, long sourceSequence = 0)
    {
        ValidateValue(cookie); ValidateValue(bearer);
        if (capturedAt < 0 || !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid application session snapshot metadata.");
        Cookie = string.IsNullOrEmpty(cookie) ? null : cookie;
        BearerToken = string.IsNullOrEmpty(bearer) ? null : bearer;
        CapturedAtUtcTicks = capturedAt; Id = id;
        SourceProcessId = sourcePid; SourceStartTicks = sourceStart; Sequence = sourceSequence;
    }

    internal static void ValidateValue(string? value)
    {
        if (value is not null && (value.Length > 6144 || value.Any(c => c < 32 || c == 127)))
            throw new ArgumentException("Application session contains invalid HTTP header data.");
    }
    public override string ToString() => "Application session snapshot (redacted)";
    private static long CurrentStart() { using var process = System.Diagnostics.Process.GetCurrentProcess(); return process.StartTime.ToUniversalTime().Ticks; }
}

internal sealed class ApplicationSessionStore(ApplicationSessionSnapshot initial)
{
    private readonly object gate = new();
    private ApplicationSessionSnapshot current = initial;
    private long revision;
    private CancellationTokenSource? authentication;
    public (ApplicationSessionSnapshot Snapshot, long Revision) Read() { lock (gate) return (current, revision); }

    public bool Replace(ApplicationSessionSnapshot replacement)
    {
        CancellationTokenSource? obsolete;
        lock (gate)
        {
            var sameSource = replacement.SourceProcessId == current.SourceProcessId && replacement.SourceStartTicks == current.SourceStartTicks && replacement.Sequence > 0 && current.Sequence > 0;
            if (replacement.Id == current.Id || (sameSource ? replacement.Sequence <= current.Sequence : replacement.CapturedAtUtcTicks < current.CapturedAtUtcTicks)) return false;
            current = replacement; revision++; obsolete = authentication; authentication = null;
        }
        try { obsolete?.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    public bool SetBrowserSession(long expectedRevision, string bearer)
    {
        ApplicationSessionSnapshot.ValidateValue(bearer);
        lock (gate)
        {
            if (revision != expectedRevision) return false;
            current = new(null, bearer, current.CapturedAtUtcTicks, Guid.NewGuid().ToString("N"), current.SourceProcessId, current.SourceStartTicks, current.Sequence); revision++;
            return true;
        }
    }
    public CancellationTokenSource BeginAuthentication(long expectedRevision, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (gate)
        {
            if (revision != expectedRevision) source.Cancel();
            else authentication = source;
        }
        return source;
    }
    public void EndAuthentication(CancellationTokenSource source)
    {
        lock (gate) if (ReferenceEquals(authentication, source)) authentication = null;
        source.Dispose();
    }
}
