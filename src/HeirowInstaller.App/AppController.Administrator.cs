using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    private readonly CancellationTokenSource administratorLifetime = new();
    internal bool AdministratorPreparationCanceled { get; private set; }
    private async Task PrepareAdministratorOnOpenAsync()
    {
        if (!arguments.RequestAdministratorOnOpen || arguments.Isolated || !OperatingSystem.IsWindows() || InstallerEngine.IsElevated) return;
        Begin("Preparing administrator access", "Windows will request approval for the installation worker. Sign-in and setup stay in your account.");
        try
        {
            await ElevatedInstallWorker.PrepareAdministratorAsync(arguments.TrustedPublisherKeySha256, administratorLifetime.Token, arguments.ExpectedProductId);
            await game.OnUiAsync(() => End("Administrator access ready", "Review setup and select Install. Windows will reuse the prepared worker."));
        }
        catch (OperationCanceledException)
        {
            ElevatedInstallWorker.CancelPreparation(); AdministratorPreparationCanceled = true;
            await game.OnUiAsync(() => { Environment.ExitCode = 1223; game.Exit(); });
        }
        catch
        {
            ElevatedInstallWorker.CancelPreparation(); throw;
        }
    }
}
