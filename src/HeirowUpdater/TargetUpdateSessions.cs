using System.Diagnostics;
using System.Net;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;
using HeirowUpdater.SocketJack;

namespace HeirowUpdater;

/// <summary>Shares the remembered installer account only with signed SocketJack update targets.</summary>
internal sealed class TargetUpdateSessions : IDisposable
{
    private static readonly Uri SocketJackOrigin = new("https://socketjack.com/");
    private readonly Dictionary<UpdaterTarget, TargetSession> sessions = [];
    private readonly SemaphoreSlim browserApproval = new(1, 1);
    private RememberedPublisherAccount? account;

    private TargetUpdateSessions() { }

    // Internal acceptance seams cannot be selected by CLI arguments or untrusted profiles.
    internal static async Task<TargetUpdateSessions> CreateAsync(IReadOnlyList<UpdaterTarget> targets, ScheduleConfiguration configuration,
        bool allowLoopback, bool interactive, IProgress<InstallProgress> progress, CancellationToken cancellationToken = default,
        RememberedPublisherAccount? rememberedAccount = null, Uri? rememberedOrigin = null,
        Func<Uri, string, CancellationToken, Task<PublisherSession>>? authenticate = null)
    {
        var result = new TargetUpdateSessions();
        try
        {
            var accountOrigin = rememberedOrigin ?? SocketJackOrigin;
            bool UsesAccount(UpdaterTarget target) => target.CredentialOrigin is not null && SameOrigin(new Uri(target.CredentialOrigin), accountOrigin) &&
                Uri.TryCreate(target.FeedUrl, UriKind.Absolute, out var feed) && SameOrigin(feed, accountOrigin);
            if (OperatingSystem.IsWindows() && targets.Any(UsesAccount)) result.account = rememberedAccount ?? new RememberedPublisherAccount(accountOrigin);
            // Update checks never renew or extend remembered sign-in, even when interactive.
            using var remembered = result.account is null ? null : await result.account.RestoreAsync(renew: false, cancellationToken);
            var configuredOrigin = new Uri(configuration.CredentialOrigin);
            var credentialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HEIROWUPDATER_COOKIE", "HEIROWUPDATER_TOKEN", "SOCKETJACK_DOWNLOAD_COOKIE", "SOCKETJACK_DOWNLOAD_TOKEN",
                "SOCKETJACK_PUBLISHER_COOKIE", "SOCKETJACK_PUBLISHER_TOKEN"
            };
            foreach (var target in targets)
            {
                var origin = new Uri(target.CredentialOrigin ?? configuration.CredentialOrigin);
                var sameOrigin = SameOrigin(origin, configuredOrigin);
                // Explicit schedule credentials retain their configured origin; signed product defaults are optional.
                var cookieName = (sameOrigin ? configuration.CookieEnvironmentVariable : null) ?? target.CookieEnvironmentVariable;
                var tokenName = (sameOrigin ? configuration.TokenEnvironmentVariable : null) ?? target.TokenEnvironmentVariable;
                string? Read(string? name, bool required)
                {
                    if (name is null) return null;
                    credentialNames.Add(name);
                    var value = Environment.GetEnvironmentVariable(name);
                    if (required && string.IsNullOrEmpty(value)) throw new InvalidOperationException("A configured updater credential environment variable is empty.");
                    return string.IsNullOrEmpty(value) ? null : value;
                }
                var cookie = Read(cookieName, sameOrigin && configuration.CookieEnvironmentVariable is not null);
                var bearer = Read(tokenName, sameOrigin && configuration.TokenEnvironmentVariable is not null);
                var usesAccount = UsesAccount(target);
                // Explicit and signed environment credentials take precedence, including cookie-only sessions.
                if (cookie is null && bearer is null && usesAccount) bearer = remembered?.AccessToken;
                var owned = new TargetSession(result, usesAccount, bearer);
                owned.Session = new(target, allowLoopback, origin, cookie, bearer,
                    interactive && (usesAccount || target.AuthApplication is not null) ? async cancellation =>
                    {
                        // Concurrent feed checks must never open overlapping sign-in approvals.
                        await result.browserApproval.WaitAsync(cancellation);
                        try
                        {
                            string? revision = null;
                            if (usesAccount && result.account is not null)
                            {
                                await result.InvalidateAsync(owned.Bearer, cancellation);
                                // Another target or installer process may already have approved a newer account.
                                using var current = await result.account!.RestoreAsync(renew: false, cancellation);
                                if (current is not null) return owned.ReplaceBearer(current.AccessToken);
                                revision = await result.account.GetRevisionAsync(cancellation);
                            }
                            progress.Report(new("sign-in-required", "Approve SocketJack sign-in in your browser for " + target.Name + ".", 0, 0));
                            PublisherSession approved;
                            var application = usesAccount ? "heirowinstaller" : target.AuthApplication!;
                            if (authenticate is not null) approved = await authenticate(origin, application, cancellation);
                            else
                            {
                                using var approval = await PublisherSignInClient.BeginAsync(origin, cancellation, allowLoopback, application);
                                OpenApproval(approval.BrowserUrl, credentialNames);
                                approved = await approval.CompleteAsync(cancellation);
                            }
                            using var session = approved;
                            if (usesAccount && result.account is not null && !await result.account.RememberAsync(session, revision, cancellation))
                            {
                                // A newer login or sign-out wins over an older browser completion.
                                using var latest = await result.account.RestoreAsync(renew: false, cancellation);
                                return latest is null ? null : owned.ReplaceBearer(latest.AccessToken);
                            }
                            progress.Report(new("check", "Sign-in approved. Retrying " + target.Name + ".", 0, 0));
                            return owned.ReplaceBearer(session.AccessToken);
                        }
                        finally { result.browserApproval.Release(); }
                    } : null);
                result.sessions.Add(target, owned);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private Task InvalidateAsync(string? rejectedBearer, CancellationToken cancellationToken)
        => rejectedBearer is null || account is null ? Task.CompletedTask : account.InvalidateAsync(rejectedBearer, cancellationToken);

    private static bool SameOrigin(Uri left, Uri right) => left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static void OpenApproval(Uri url, IEnumerable<string> credentialNames)
    {
        var saved = credentialNames.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase);
        try
        {
            // Shell execution cannot take a custom environment. Strip credentials only during
            // this synchronous launch, then restore them for an owned elevated update child.
            foreach (var name in saved.Keys) Environment.SetEnvironmentVariable(name, null);
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    internal TargetSession this[UpdaterTarget target] => sessions[target];

    internal sealed class TargetSession(TargetUpdateSessions owner, bool usesAccount, string? bearer) : IDisposable
    {
        internal TargetUpdateSession Session { private get; set; } = null!;
        internal string? Bearer { get; private set; } = bearer;
        internal string ReplaceBearer(string bearer) { Bearer = bearer; return bearer; }

        internal async Task<T> RunAsync<T>(Func<SocketJackUpdater, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            try { return await Session.RunAsync(operation, cancellationToken); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized && usesAccount)
            {
                await owner.InvalidateAsync(Bearer, cancellationToken);
                throw;
            }
        }
        public void Dispose() { Bearer = null; Session.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
        browserApproval.Dispose();
    }
}
