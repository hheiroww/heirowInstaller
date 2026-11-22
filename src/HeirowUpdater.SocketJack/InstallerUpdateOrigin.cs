namespace HeirowUpdater.SocketJack;

public static class InstallerUpdateOrigin
{
    private const string AccountHost = "socketjack.com";
    private const string UpdateHost = "update.socketjack.com";

    public static bool UsesAccount(Uri feed, Uri accountOrigin) =>
        SameOrigin(feed, accountOrigin) ||
        IsHttpsHost(accountOrigin, AccountHost) && IsHttpsHost(feed, UpdateHost) && IsDeliveryRequest("GET", feed);

    internal static Uri DeliveryUri(string method, Uri logical)
        => IsHttpsHost(logical, AccountHost) && IsDeliveryRequest(method, logical)
            ? WithHost(logical, UpdateHost) : logical;

    internal static Uri? LegacyFallback(string method, Uri logical, Uri delivery)
        => IsHttpsHost(delivery, UpdateHost) && IsDeliveryRequest(method, delivery)
            ? WithHost(logical, AccountHost) : null;

    internal static bool CanSendCredentials(string method, Uri logical, Uri delivery, Uri origin) =>
        SameOrigin(logical, origin) && SameOrigin(delivery, origin) ||
        IsHttpsHost(origin, AccountHost) && IsDeliveryRequest(method, logical) &&
        (IsHttpsHost(logical, AccountHost) || IsHttpsHost(logical, UpdateHost)) &&
        (IsHttpsHost(delivery, AccountHost) || IsHttpsHost(delivery, UpdateHost));

    private static bool IsDeliveryRequest(string method, Uri uri)
    {
        var path = uri.AbsolutePath;
        var releasePath = path.StartsWith("/installers/", StringComparison.OrdinalIgnoreCase);
        var betaPath = path.StartsWith("/api/installer/beta/FruityLoopsCollab/", StringComparison.OrdinalIgnoreCase);
        if (method == "POST")
            return (releasePath || betaPath) && path.EndsWith("/files.zip", StringComparison.OrdinalIgnoreCase);
        if (method != "GET") return false;
        return releasePath || betaPath ||
            path.StartsWith("/api/installer/shared/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/installer/catalog/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/installer/catalog", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/installer/applications", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/installer/downloads", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/installer/releases", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri WithHost(Uri uri, string host) => new UriBuilder(uri) { Host = host, Port = -1 }.Uri;

    private static bool IsHttpsHost(Uri uri, string host) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase);

    private static bool SameOrigin(Uri a, Uri b) => a.IsAbsoluteUri && b.IsAbsoluteUri && a.Scheme == b.Scheme &&
        a.UserInfo.Length == 0 && b.UserInfo.Length == 0 && a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
