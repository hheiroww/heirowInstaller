using System.Diagnostics;
using HeirowInstaller.Core;
using HeirowInstaller.Publishing;
using HeirowUpdater.SocketJack;

namespace HeirowUpdater;

/// <summary>Keeps each product's approved credentials in memory through checking and installation.</summary>
internal sealed class TargetUpdateSessions : IDisposable
{
    private readonly Dictionary<UpdaterTarget, TargetUpdateSession> sessions = [];
    private readonly SemaphoreSlim browserApproval = new(1, 1);

    internal TargetUpdateSessions(IReadOnlyList<UpdaterTarget> targets, ScheduleConfiguration configuration,
        bool allowLoopback, bool interactive, IProgress<InstallProgress> progress)
    {
        try
        {
            var configuredOrigin = new Uri(configuration.CredentialOrigin);
            var credentialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HEIROWUPDATER_COOKIE", "HEIROWUPDATER_TOKEN", "SOCKETJACK_DOWNLOAD_COOKIE", "SOCKETJACK_DOWNLOAD_TOKEN",
                "SOCKETJACK_PUBLISHER_COOKIE", "SOCKETJACK_PUBLISHER_TOKEN"
            };
            foreach (var target in targets)
            {
                var origin = new Uri(target.CredentialOrigin ?? configuration.CredentialOrigin);
                var sameOrigin = origin.GetLeftPart(UriPartial.Authority).Equals(configuredOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
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
                sessions.Add(target, new(target, allowLoopback, origin,
                    Read(cookieName, sameOrigin && configuration.CookieEnvironmentVariable is not null),
                    Read(tokenName, sameOrigin && configuration.TokenEnvironmentVariable is not null),
                    interactive && target.AuthApplication is not null ? async cancellation =>
                    {
                        // Concurrent feed checks must never open overlapping sign-in approvals.
                        await browserApproval.WaitAsync(cancellation);
                        try
                        {
                            progress.Report(new("sign-in-required", "Approve SocketJack sign-in in your browser for " + target.Name + ".", 0, 0));
                            using var approval = await PublisherSignInClient.BeginAsync(origin, cancellation, allowLoopback, target.AuthApplication);
                            OpenApproval(approval.BrowserUrl, credentialNames);
                            using var session = await approval.CompleteAsync(cancellation);
                            progress.Report(new("check", "Sign-in approved. Retrying " + target.Name + ".", 0, 0));
                            return session.AccessToken;
                        }
                        finally { browserApproval.Release(); }
                    } : null));
            }
        }
        catch { Dispose(); throw; }
    }

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

    internal TargetUpdateSession this[UpdaterTarget target] => sessions[target];

    public void Dispose()
    {
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
        browserApproval.Dispose();
    }
}
