using System.Net;

namespace HeirowUpdater.SocketJack;

/// <summary>Retries a newer app session before browser approval and never lets an obsolete approval replace a later login.</summary>
internal static class ApplicationAuthorization
{
    internal static async Task<T> RunAsync<T>(ApplicationSessionStore sessions, Func<Task<T>> operation,
        Func<CancellationToken, Task<string?>>? authenticate, Func<string, Task> status, Func<Task> postpone, CancellationToken cancellationToken)
    {
        var retriedNewerSession = false;
        while (true)
        {
            var attempt = sessions.Read();
            try { return await operation(); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
            {
                var latest = sessions.Read();
                if (!retriedNewerSession && latest.Revision != attempt.Revision) { retriedNewerSession = true; continue; }
                if (authenticate is null) throw;
                await status("sign-in-required");
                var authentication = sessions.BeginAuthentication(latest.Revision, cancellationToken);
                string? bearer;
                try { bearer = await authenticate(authentication.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && sessions.Read().Revision != latest.Revision)
                { await status("checking"); retriedNewerSession = false; continue; }
                finally { sessions.EndAuthentication(authentication); }
                if (sessions.Read().Revision != latest.Revision) { await status("checking"); retriedNewerSession = false; continue; }
                if (string.IsNullOrEmpty(bearer))
                {
                    await postpone();
                    throw new OperationCanceledException("Application update sign-in was postponed.", cancellationToken);
                }
                if (!sessions.SetBrowserSession(latest.Revision, bearer)) continue;
                return await operation(); // Exactly one browser-authenticated attempt; a second 401 is reported.
            }
        }
    }
}
