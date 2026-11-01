using HeirowInstaller.Core;

namespace HeirowInstaller.App;

internal sealed partial class AppController
{
    internal string WindowsSigningStatus { get; private set; } = "When enabled, signing runs after packaging and must pass Windows signature and timestamp verification.";
    internal string WindowsSigningSummary => Settings.WindowsCodeSigning?.Provider?.ToLowerInvariant() switch
    {
        "certificate" => "Windows signing · purchased certificate",
        "microsoft" => "Windows signing · Microsoft Artifact Signing",
        _ => "Windows signing · not configured"
    };

    // Snapshot only the selected provider's nonsecret fields. These settings never become application payload metadata.
    internal static WindowsCodeSigningOptions CopyWindowsSigningSettings(WindowsCodeSigningOptions? value)
    {
        var provider = value?.Provider?.Trim().ToLowerInvariant() ?? "none";
        static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return new()
        {
            Provider = provider,
            CertificateThumbprint = provider == "certificate" ? Optional(value?.CertificateThumbprint) : null,
            UseMachineCertificateStore = provider == "certificate" && value?.UseMachineCertificateStore == true,
            SignToolPath = provider == "none" ? null : Optional(value?.SignToolPath),
            ArtifactSigningDlibPath = provider == "microsoft" ? Optional(value?.ArtifactSigningDlibPath) : null,
            Endpoint = provider == "microsoft" ? Optional(value?.Endpoint) : null,
            AccountName = provider == "microsoft" ? Optional(value?.AccountName) : null,
            CertificateProfileName = provider == "microsoft" ? Optional(value?.CertificateProfileName) : null,
            TimestampUrl = provider == "none" ? null : Optional(value?.TimestampUrl)
        };
    }

    internal void ResetWindowsSigningStatus()
    {
        WindowsSigningStatus = "When enabled, signing runs after packaging and must pass Windows signature and timestamp verification.";
    }

    internal void ShowWindowsSigningError(Exception error)
    {
        WindowsSigningStatus = error.Message;
        Notify();
    }

    internal async Task CheckWindowsSigningAsync(WindowsCodeSigningOptions draft)
    {
        if (Busy) return;
        var snapshot = CopyWindowsSigningSettings(draft);
        Begin("Checking Windows signing", "Checking local tools and certificate availability. No signing request is sent.");
        try
        {
            var available = await Task.Run(() => WindowsCodeSigner.CheckAvailability(snapshot), Cancellation!.Token);
            await game.OnUiAsync(() =>
            {
                WindowsSigningStatus = snapshot.Provider == "none" ? "Windows signing is disabled. New installers will have no Windows publisher signature."
                    : snapshot.Provider == "microsoft" ? "Signing tools are ready. Azure authentication, profile permissions and Windows trust are checked when you build. Use a Public Trust profile."
                    : "Certificate and signing tools are ready: " + available.CertificateSubject + ". Windows trust and timestamp are verified when you build.";
                End("Signing settings checked", WindowsSigningStatus);
            });
        }
        catch (Exception error)
        {
            await game.OnUiAsync(() => WindowsSigningStatus = error.Message);
            await ErrorAsync("Signing settings need attention", error);
        }
    }

    internal async Task<bool> SaveWindowsSigningAsync(WindowsCodeSigningOptions draft)
    {
        if (Busy) return false;
        var snapshot = CopyWindowsSigningSettings(draft);
        Begin("Saving Windows signing", "Saving your provider settings without credentials or private keys.");
        try
        {
            WindowsCodeSigner.Validate(snapshot);
            await Task.Run(() =>
            {
                // Avoid changing the active profile if saving preferences fails.
                var previous = Settings.WindowsCodeSigning;
                try { Settings.WindowsCodeSigning = snapshot; Settings.Save(); }
                catch { Settings.WindowsCodeSigning = previous; throw; }
            }, Cancellation!.Token);
            await game.OnUiAsync(() => End("Windows signing saved", snapshot.Provider == "none"
                ? "New installers will be built without a Windows publisher signature."
                : "Build an EXE setup to apply and verify the Windows publisher signature. Signing failures stop the build."));
            return true;
        }
        catch (Exception error)
        {
            await game.OnUiAsync(() => WindowsSigningStatus = error.Message);
            await ErrorAsync("Signing settings could not save", error);
            return false;
        }
    }
}
