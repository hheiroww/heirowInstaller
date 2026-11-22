using HeirowUpdater.SocketJack;

internal sealed partial class AcceptanceSuite
{
    private static Task SocketJackDeliveryOrigin()
    {
        var authority = new Uri("https://socketjack.com/");
        var legacy = new Uri(authority, "installers/heirowSnap/update.json?fromVersion=26.11.48&updateProtocol=hfiles1");
        var delivery = InstallerUpdateOrigin.DeliveryUri("GET", legacy);
        Equal("https://update.socketjack.com/installers/heirowSnap/update.json?fromVersion=26.11.48&updateProtocol=hfiles1", delivery.AbsoluteUri);
        Equal<Uri?>(legacy, InstallerUpdateOrigin.LegacyFallback("GET", legacy, delivery));
        True(InstallerUpdateOrigin.UsesAccount(delivery, authority));
        True(InstallerUpdateOrigin.CanSendCredentials("GET", legacy, delivery, authority));
        True(InstallerUpdateOrigin.CanSendCredentials("GET", delivery, legacy, authority));

        var files = new Uri(authority, "installers/heirowSnap/26.11.50/files.zip");
        Equal("update.socketjack.com", InstallerUpdateOrigin.DeliveryUri("POST", files).Host);
        var shared = new Uri(authority, "api/installer/shared/heirowSnap/26.11.50/socketjack.updater.host/2026.11.7.3/" + new string('a', 64) + ".hinst");
        Equal("update.socketjack.com", InstallerUpdateOrigin.DeliveryUri("GET", shared).Host);
        var publish = new Uri(authority, "api/installer/publish");
        Equal(publish, InstallerUpdateOrigin.DeliveryUri("POST", publish));
        var foreign = new Uri("https://example.test/installers/heirowSnap/update.json");
        Equal(foreign, InstallerUpdateOrigin.DeliveryUri("GET", foreign));
        True(!InstallerUpdateOrigin.CanSendCredentials("GET", legacy, foreign, authority));
        True(!InstallerUpdateOrigin.CanSendCredentials("GET", foreign, delivery, foreign));
        True(!InstallerUpdateOrigin.UsesAccount(new Uri("https://update.socketjack.com:8443/installers/heirowSnap/update.json"), authority));
        True(!InstallerUpdateOrigin.UsesAccount(new Uri("https://update.socketjack.com/account"), authority));
        Equal<Uri?>(null, InstallerUpdateOrigin.LegacyFallback("GET", foreign, foreign));
        return Task.CompletedTask;
    }
}
