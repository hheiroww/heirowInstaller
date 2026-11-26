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

    private TargetUpdateSessions() { }

    // Internal acceptance seams cannot be selected by CLI arguments or untrusted profiles.
    internal static async Task<TargetUpdateSessions> CreateAsync(IReadOnlyList<UpdaterTarget> targets, ScheduleConfiguration configuration,
        bool allowLoopback, CancellationToken cancellationToken = default,
        RememberedPublisherAccount? rememberedAccount = null, Uri? rememberedOrigin = null)
    {
        var result = new TargetUpdateSessions();
        try
        {
            var accountOrigin = rememberedOrigin ?? SocketJackOrigin;
            bool UsesAccount(UpdaterTarget target) => target.CredentialOrigin is not null && SameOrigin(new Uri(target.CredentialOrigin), accountOrigin) &&
                Uri.TryCreate(target.FeedUrl, UriKind.Absolute, out var feed) && InstallerUpdateOrigin.UsesAccount(feed, accountOrigin);
            var rememberedStore = OperatingSystem.IsWindows() && targets.Any(UsesAccount)
                ? rememberedAccount ?? new RememberedPublisherAccount(accountOrigin) : null;
            // Update checks never renew or extend remembered sign-in.
            using var remembered = rememberedStore is null ? null : await rememberedStore.RestoreAsync(renew: false, cancellationToken);
            var configuredOrigin = new Uri(configuration.CredentialOrigin);
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
                    var value = Environment.GetEnvironmentVariable(name);
                    if (required && string.IsNullOrEmpty(value)) throw new InvalidOperationException("A configured updater credential environment variable is empty.");
                    return string.IsNullOrEmpty(value) ? null : value;
                }
                var cookie = Read(cookieName, sameOrigin && configuration.CookieEnvironmentVariable is not null);
                var bearer = Read(tokenName, sameOrigin && configuration.TokenEnvironmentVariable is not null);
                // An explicitly supplied app session owns this request, including a cookie-only session.
                var account = cookie is null && bearer is null && UsesAccount(target) ? rememberedStore : null;
                if (account is not null) bearer = remembered?.AccessToken;
                var owned = new TargetSession(target, allowLoopback, origin, cookie, bearer, account);
                result.sessions.Add(target, owned);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static bool SameOrigin(Uri left, Uri right) => left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    internal TargetSession this[UpdaterTarget target] => sessions[target];

    internal sealed class TargetSession : IDisposable
    {
        private readonly TargetUpdateSession session;
        private readonly RememberedPublisherAccount? account;
        private string? bearer;

        internal TargetSession(UpdaterTarget target, bool allowLoopback, Uri origin, string? cookie,
            string? bearer, RememberedPublisherAccount? account)
        {
            this.account = account;
            this.bearer = bearer;
            session = new(target, allowLoopback, origin, cookie, bearer, account is null ? null : ReadLatestAccountAsync);
        }

        private async Task<string?> ReadLatestAccountAsync(CancellationToken cancellationToken)
        {
            await InvalidateAsync(cancellationToken);
            using var latest = await account!.RestoreAsync(renew: false, cancellationToken);
            if (latest is null) return null;
            bearer = latest.AccessToken;
            return bearer;
        }

        private Task InvalidateAsync(CancellationToken cancellationToken) =>
            account is null || bearer is null ? Task.CompletedTask : account.InvalidateAsync(bearer, cancellationToken);

        internal async Task<T> RunAsync<T>(Func<SocketJackUpdater, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            try { return await session.RunAsync(operation, cancellationToken); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized && account is not null)
            {
                await InvalidateAsync(cancellationToken);
                throw;
            }
        }

        public void Dispose() { bearer = null; session.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
    }
}
