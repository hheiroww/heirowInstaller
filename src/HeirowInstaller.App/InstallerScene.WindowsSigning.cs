using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using HeirowInstaller.Core;
using Microsoft.Xna.Framework;

namespace HeirowInstaller.App;

internal sealed partial class InstallerScene
{
    private WindowsCodeSigningOptions windowsSigningDraft = new();
    private bool windowsSigningAdvanced;
    private string windowsSigningCertificateName = "";

    private void OpenWindowsSigning()
    {
        windowsSigningDraft = AppController.CopyWindowsSigningSettings(Controller.Settings.WindowsCodeSigning);
        windowsSigningAdvanced = false;
        windowsSigningCertificateName = "";
        Controller.ResetWindowsSigningStatus();
        showWindowsSigning = true;
        Controller.Notify();
    }

    private void BuildWindowsSigning()
    {
        Box(0, 0, Width, Height, new Color(0, 0, 0, 205), 0, Color.Transparent).isMouseBypassEnabled = false;
        int width = Math.Min(850, Width - 48), height = Math.Min(700, Height - 64);
        int x = (Width - width) / 2, y = (Height - height) / 2;
        Box(x, y, width, height, Theme.Surface, 14);
        Text("Windows publisher signing", x + 24, y + 20, width - 48, 34, 19, bold: true);
        Text("Let Windows verify who published your setup. Signing uses your own certificate or Microsoft account.",
            x + 25, y + 65, width - 50, 37, 10, Theme.Muted, wrap: true);
        float choiceWidth = (width - 68) / 3f;
        void ChooseProvider(string provider)
        {
            windowsSigningDraft.Provider = provider;
            if (provider == "none") windowsSigningAdvanced = false;
            Controller.ResetWindowsSigningStatus();
            Controller.Notify();
        }
        Action("signing.none", "Not signed", x + 24, y + 112, choiceWidth, () => ChooseProvider("none"),
            primary: windowsSigningDraft.Provider == "none", enabled: !Controller.Busy);
        Action("signing.certificate", "Purchased certificate", x + 34 + choiceWidth, y + 112, choiceWidth, () => ChooseProvider("certificate"),
            primary: windowsSigningDraft.Provider == "certificate", enabled: !Controller.Busy);
        Action("signing.microsoft", "Microsoft Artifact Signing", x + 44 + 2 * choiceWidth, y + 112, choiceWidth, () => ChooseProvider("microsoft"),
            primary: windowsSigningDraft.Provider == "microsoft", enabled: !Controller.Busy);
        Action("signing.settings", "Signing settings", x + 24, y + 174, 170, () => { windowsSigningAdvanced = false; Controller.Notify(); },
            primary: !windowsSigningAdvanced, enabled: !Controller.Busy);
        Action("signing.advanced", "Tools & timestamp", x + 204, y + 174, 181, () => { windowsSigningAdvanced = true; Controller.Notify(); },
            primary: windowsSigningAdvanced, enabled: !Controller.Busy && windowsSigningDraft.Provider != "none");

        if (windowsSigningAdvanced) BuildWindowsSigningTools(x, y, width);
        else if (windowsSigningDraft.Provider == "certificate") BuildWindowsSigningCertificate(x, y, width);
        else if (windowsSigningDraft.Provider == "microsoft") BuildWindowsSigningMicrosoft(x, y, width);
        else
        {
            Text("Your setup will have no Windows publisher signature.", x + 25, y + 246, width - 50, 31, 13, bold: true);
            Text("Choose Purchased certificate if you already have a code-signing certificate from a certificate authority.\n\nChoose Microsoft Artifact Signing to use an existing Microsoft signing account and public-trust profile.\n\nYour package publisher signature continues to protect installation and updates separately.",
                x + 25, y + 298, width - 50, 188, 11, Theme.Muted, wrap: true);
        }
        Text(Controller.Busy ? Controller.Status + "…" : Controller.WindowsSigningStatus,
            x + 25, y + height - 116, width - 50, 42, 9.5f, Theme.Muted, wrap: true);
        Action("signing.cancel", "Cancel", x + 24, y + height - 64, 108,
            () => { showWindowsSigning = false; Controller.Notify(); }, enabled: !Controller.Busy);
        Action("signing.check", "Check settings", x + 142, y + height - 64, 164,
            () => _ = Controller.CheckWindowsSigningAsync(windowsSigningDraft), enabled: !Controller.Busy);
        Action("signing.save", "Save & use", x + width - 178, y + height - 64, 154,
            () => _ = SaveWindowsSigningAndCloseAsync(), primary: true, enabled: !Controller.Busy);
    }

    private void BuildWindowsSigningCertificate(int x, int y, int width)
    {
        Text("PERSONAL CERTIFICATE STORE", x + 25, y + 238, width - 50, 20, 8.5f, Theme.Muted, true);
        Action("signing.current-user", "Current user", x + 24, y + 263, 161,
            () => { windowsSigningDraft.UseMachineCertificateStore = false; windowsSigningCertificateName = ""; Controller.Notify(); },
            primary: !windowsSigningDraft.UseMachineCertificateStore, enabled: !Controller.Busy);
        Action("signing.machine", "Local computer", x + 195, y + 263, 170,
            () => { windowsSigningDraft.UseMachineCertificateStore = true; windowsSigningCertificateName = ""; Controller.Notify(); },
            primary: windowsSigningDraft.UseMachineCertificateStore, enabled: !Controller.Busy);
        Text("CERTIFICATE THUMBPRINT", x + 25, y + 321, width - 50, 20, 8.5f, Theme.Muted, true);
        Field("signing.thumbprint", windowsSigningDraft.CertificateThumbprint ?? "", x + 24, y + 346, width - 48,
            text => { windowsSigningDraft.CertificateThumbprint = text; windowsSigningCertificateName = ""; });
        Action("signing.choose-certificate", "Choose certificate", x + 24, y + 405, 192, ChooseWindowsSigningCertificate, enabled: !Controller.Busy);
        Action("signing.import-certificate", "Import certificate", x + 226, y + 405, 185, ImportWindowsSigningCertificate, enabled: !Controller.Busy);
        Text(windowsSigningCertificateName.Length == 0
            ? "Select a valid code-signing certificate with its private key. Your CA's driver can expose a USB token or cloud key in this store."
            : windowsSigningCertificateName, x + 25, y + 466, width - 50, 50, 10, Theme.Muted, wrap: true);
        Text("PFX/P12 import opens the Windows certificate wizard. Passwords and private keys are handled by Windows, never saved here.",
            x + 25, y + 527, width - 50, 40, 9.5f, Theme.Muted, wrap: true);
    }

    private void BuildWindowsSigningMicrosoft(int x, int y, int width)
    {
        Text("SERVICE ENDPOINT · copy the endpoint for your Azure region", x + 25, y + 238, width - 50, 20, 8.5f, Theme.Muted, true);
        Field("signing.endpoint", windowsSigningDraft.Endpoint ?? "", x + 24, y + 263, width - 48, text => windowsSigningDraft.Endpoint = text);
        Text("ARTIFACT SIGNING ACCOUNT NAME", x + 25, y + 319, width - 50, 20, 8.5f, Theme.Muted, true);
        Field("signing.account", windowsSigningDraft.AccountName ?? "", x + 24, y + 344, width - 48, text => windowsSigningDraft.AccountName = text);
        Text("PUBLIC-TRUST CERTIFICATE PROFILE NAME", x + 25, y + 400, width - 50, 20, 8.5f, Theme.Muted, true);
        Field("signing.profile", windowsSigningDraft.CertificateProfileName ?? "", x + 24, y + 425, width - 48, text => windowsSigningDraft.CertificateProfileName = text);
        Text("Requires Microsoft's paid service and identity validation. Sign in to Azure CLI once, or use environment credentials; subsequent builds use that identity.",
            x + 25, y + 481, width - 50, 40, 9.5f, Theme.Muted, wrap: true);
        Action("signing.microsoft-setup", "Microsoft setup ↗", x + 24, y + 530, 188,
            () => OpenWindowsSigningHelp("https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart"), enabled: !Controller.Busy);
        Action("signing.microsoft-tools", "Get signing tools ↗", x + 222, y + 530, 194,
            () => OpenWindowsSigningHelp("https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations"), enabled: !Controller.Busy);
        Action("signing.microsoft-login-help", "Azure sign-in help ↗", x + 426, y + 530, 204,
            () => OpenWindowsSigningHelp("https://learn.microsoft.com/en-us/cli/azure/authenticate-azure-cli-interactively"), enabled: !Controller.Busy);
    }

    private void BuildWindowsSigningTools(int x, int y, int width)
    {
        WindowsSigningToolField("signing.signtool", "SIGNTOOL.EXE · leave empty to find installed tools", windowsSigningDraft.SignToolPath,
            x, y + 238, width, value => windowsSigningDraft.SignToolPath = value, "Windows signing tool|signtool.exe");
        if (windowsSigningDraft.Provider == "microsoft")
            WindowsSigningToolField("signing.dlib", "MICROSOFT SIGNING DLIB · leave empty to find installed tools", windowsSigningDraft.ArtifactSigningDlibPath,
                x, y + 319, width, value => windowsSigningDraft.ArtifactSigningDlibPath = value, "Microsoft signing library|Azure.CodeSigning.Dlib.dll");
        Text("TIMESTAMP SERVICE URL · leave empty for the provider's default", x + 25, y + 400, width - 50, 20, 8.5f, Theme.Muted, true);
        Field("signing.timestamp", windowsSigningDraft.TimestampUrl ?? "", x + 24, y + 425, width - 48, text => windowsSigningDraft.TimestampUrl = text);
        Text("SHA-256 signing and timestamping are required. Check settings checks local tools only; a build must also pass Windows signature and timestamp verification.",
            x + 25, y + 481, width - 50, 40, 9.5f, Theme.Muted, wrap: true);
        Action("signing.tools-help", "Get signing tools ↗", x + 24, y + 530, 194,
            () => OpenWindowsSigningHelp("https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations"), enabled: !Controller.Busy);
    }

    private void WindowsSigningToolField(string id, string label, string? value, int x, int top, int width, Action<string> changed, string filter)
    {
        Text(label, x + 25, top, width - 50, 20, 8.5f, Theme.Muted, true);
        Field(id, value ?? "", x + 24, top + 25, width - 168, changed);
        Action(id + ".browse", "Browse", x + width - 132, top + 25, 108, () =>
        {
            using var picker = new DesktopOpenFileDialog { Title = label.Split('·')[0].Trim(), Filter = filter, CheckFileExists = true };
            if (picker.ShowDialog() == PickerResult.OK) { changed(picker.FileName); Controller.Notify(); }
        }, enabled: !Controller.Busy);
    }

    private void ChooseWindowsSigningCertificate()
    {
#if WINDOWS
        try
        {
            using var store = new X509Store(StoreName.My, windowsSigningDraft.UseMachineCertificateStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var all = store.Certificates;
            try
            {
                var available = new X509Certificate2Collection();
                foreach (var certificate in all)
                {
                    var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
                    if (certificate.HasPrivateKey && certificate.NotBefore <= DateTime.Now && certificate.NotAfter >= DateTime.Now
                        && !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
                        && !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)
                        && usages.Any(extension => extension.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()
                            .Any(usage => usage.Value == "1.3.6.1.5.5.7.3.3")))
                        available.Add(certificate);
                }
                if (available.Count == 0) throw new InvalidOperationException("No valid code-signing certificate with a private key was found in this Personal store. Import your purchased certificate or install your CA's token/provider software, then try again.");
                var selected = X509Certificate2UI.SelectFromCollection(available, "Choose Windows publisher certificate",
                    "Choose the code-signing certificate used to sign your finished application setup.", X509SelectionFlag.SingleSelection, game.Window.Handle);
                if (selected.Count == 1)
                {
                    windowsSigningDraft.CertificateThumbprint = selected[0].Thumbprint;
                    windowsSigningCertificateName = selected[0].GetNameInfo(X509NameType.SimpleName, false);
                    Controller.ResetWindowsSigningStatus();
                    Controller.Notify();
                }
            }
            finally { foreach (var certificate in all) certificate.Dispose(); }
        }
        catch (Exception error) { Controller.ShowWindowsSigningError(error); }
#else
        Controller.ShowWindowsSigningError(new PlatformNotSupportedException("Windows certificate selection requires Windows."));
#endif
    }

    private void ImportWindowsSigningCertificate()
    {
        try
        {
            using var picker = new DesktopOpenFileDialog
            {
                Title = "Open your purchased certificate in the Windows import wizard",
                Filter = "Certificate or certificate bundle|*.pfx;*.p12;*.cer;*.crt", CheckFileExists = true
            };
            if (picker.ShowDialog() != PickerResult.OK) return;
            if (Path.GetExtension(picker.FileName).ToLowerInvariant() is not (".pfx" or ".p12" or ".cer" or ".crt"))
                throw new InvalidDataException("Choose a PFX, P12, CER or CRT certificate file.");
            Process.Start(new ProcessStartInfo(picker.FileName) { UseShellExecute = true });
        }
        catch (Exception error) { Controller.ShowWindowsSigningError(error); }
    }

    private void OpenWindowsSigningHelp(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { Controller.ShowWindowsSigningError(error); }
    }

    private async Task SaveWindowsSigningAndCloseAsync()
    {
        if (await Controller.SaveWindowsSigningAsync(windowsSigningDraft))
            await game.OnUiAsync(() => { showWindowsSigning = false; Controller.Notify(); });
    }
}
