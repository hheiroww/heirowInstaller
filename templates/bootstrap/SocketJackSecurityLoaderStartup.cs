#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack;

/// <summary>Call before protected application functionality. Supply the installed private bootstrap's async entrypoint.</summary>
public static class SocketJackSecurityLoaderStartup
{
    public static Task InitializeAsync(Func<CancellationToken, Task> initializePrivateBootstrap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializePrivateBootstrap);
        cancellationToken.ThrowIfCancellationRequested();
        // Authentication and protected payload handling remain in the private bootstrap.
        // Propagate failures: protected functionality must remain unavailable until initialization succeeds.
        return initializePrivateBootstrap(cancellationToken);
    }
}
