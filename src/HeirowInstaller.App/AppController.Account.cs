using System.Diagnostics;
using HeirowInstaller.Publishing;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    private static readonly Uri AccountOrigin = new("https://socketjack.com");
    private RememberedPublisherAccount? rememberedAccount;
    private RememberedPublisherAccount Account => rememberedAccount ??= new(AccountOrigin);
    private bool UsesRememberedAccount => !IsWizard && !arguments.Isolated && arguments.SmokeDirectory is null &&
        arguments.SelfUpdateSmokeFeed is null && arguments.CapturePath is null;

    private async Task RestoreAccountAsync(bool renew, CancellationToken cancellationToken)
    {
        if (!UsesRememberedAccount) return;
        PublisherSession? restored;
        try { restored = await Account.RestoreAsync(renew, cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            await game.OnUiAsync(() => AddActivity("Sign-in unavailable", "Your saved SocketJack sign-in could not be opened. Sign in again to continue."));
            return;
        }
        await game.OnUiAsync(() => { publisherSession?.Dispose(); publisherSession = restored; Notify(); });
    }

    private async Task SignInAccountAsync()
    {
        if (Busy) { if (SigningIn) Cancellation?.Cancel(); return; }
        if (PublisherSignedIn)
        {
            Begin("Signing out of SocketJack", "Clearing this device's remembered sign-in.");
            try
            {
                await Account.SignOutAsync(Cancellation!.Token);
                await game.OnUiAsync(() => { publisherSession?.Dispose(); publisherSession = null; End("Signed out", "Your SocketJack sign-in was cleared from this device."); });
            }
            catch (Exception error) { await ErrorAsync("Could not sign out", error); }
            return;
        }
        SigningIn = true;
        Begin("Sign in to SocketJack", "Approve sign-in in your browser. This device remembers it for 30 days, renewed each time heirowInstaller opens.");
        PublisherSession? pending = null;
        try
        {
            var cancellationToken = Cancellation!.Token;
            pending = await Account.RestoreAsync(renew: false, cancellationToken);
            if (pending is null)
            {
                var revision = await Account.GetRevisionAsync(cancellationToken);
                using var approval = await PublisherSignInClient.BeginAsync(AccountOrigin, cancellationToken);
                Process.Start(new ProcessStartInfo(approval.BrowserUrl.AbsoluteUri) { UseShellExecute = true });
                pending = await approval.CompleteAsync(cancellationToken);
                if (!await Account.RememberAsync(pending, revision, cancellationToken))
                {
                    pending.Dispose(); pending = null;
                    await RestoreAccountAsync(renew: false, cancellationToken);
                    await game.OnUiAsync(() => End("Sign-in changed", "The latest sign-in choice on this device was kept."));
                    return;
                }
            }
            var accepted = pending;
            await game.OnUiAsync(() => { publisherSession?.Dispose(); publisherSession = accepted; End("SocketJack sign-in complete", PublisherAccount); });
            pending = null;
        }
        catch (Exception error) { await ErrorAsync("SocketJack sign-in failed", error); }
        finally { pending?.Dispose(); await game.OnUiAsync(() => { SigningIn = false; Notify(); }); }
    }
}
