using System.Net;

namespace HeirowUpdater.SocketJack;

/// <summary>Keeps one application's update credentials in memory and permits one explicit sign-in retry.</summary>
public sealed class TargetUpdateSession : IDisposable
{
    private readonly SocketJackUpdater updater;
    private readonly Func<CancellationToken, Task<string?>>? authenticate;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private ApplicationSessionSnapshot? session;
    private bool authenticationAttempted;
    private int disposed;

    public TargetUpdateSession(UpdaterTarget target, bool allowLoopback, Uri credentialOrigin,
        string? cookie = null, string? bearerToken = null, Func<CancellationToken, Task<string?>>? authenticate = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(credentialOrigin);
        session = new(cookie, bearerToken);
        updater = new(allowLoopback, credentialOrigin,
            () => Volatile.Read(ref session) ?? throw new ObjectDisposedException(nameof(TargetUpdateSession)),
            requireIncrementalUpdates: target.UpdateMode == "files", fileSelectionUpdates: target.UpdateProtocol == "hfiles1");
        this.authenticate = authenticate;
    }

    public async Task<T> RunAsync<T>(Func<SocketJackUpdater, Task<T>> operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            try { return await operation(updater).ConfigureAwait(false); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized && authenticate is not null && !authenticationAttempted)
            {
                token.ThrowIfCancellationRequested();
                authenticationAttempted = true;
                var bearer = await authenticate(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(bearer)) throw;
                Volatile.Write(ref session, new ApplicationSessionSnapshot(bearerToken: bearer));
                return await operation(updater).ConfigureAwait(false);
            }
        }
        finally { operationGate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Volatile.Write(ref session, null);
        updater.Dispose();
    }
}
