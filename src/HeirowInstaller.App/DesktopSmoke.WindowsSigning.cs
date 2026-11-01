using HeirowInstaller.Core;
using ProjectZ.Shared.Drawing.UI.Input;

namespace HeirowInstaller.App;

internal static partial class DesktopSmoke
{
    private static async Task VerifyWindowsSigningUiAsync(InstallerGame game, string directory, string package, List<string> checks)
    {
        await Task.Run(() => VerifyWindowsSigningPackageBinding(directory, Require));
        await Click("workspace.signing");
        Require(await Ui(() => game.MainScene.HasControl("signing.none") && game.MainScene.HasControl("signing.certificate") && game.MainScene.HasControl("signing.microsoft")),
            "Workspace exposed all three Windows publisher signing choices");
        await Click("signing.certificate");
        Require(await Ui(() => game.MainScene.HasControl("signing.choose-certificate") && game.MainScene.HasControl("signing.import-certificate")
            && game.MainScene.HasControl("signing.current-user") && game.MainScene.HasControl("signing.machine")),
            "Purchased certificate signing offered both Personal stores, a certificate picker and the Windows import wizard");
        await game.CaptureAsync(Path.Combine(directory, "02a-purchased-certificate-signing.png"));
        await Click("signing.microsoft");
        await game.OnUiAsync(() =>
        {
            ((Textbox)game.MainScene.Control("signing.endpoint")).Text = "https://eus.codesigning.azure.net";
            ((Textbox)game.MainScene.Control("signing.account")).Text = "synthetic-signing-account";
            ((Textbox)game.MainScene.Control("signing.profile")).Text = "synthetic-public-profile";
        });
        await Click("signing.advanced");
        Require(await Ui(() => game.MainScene.HasControl("signing.signtool") && game.MainScene.HasControl("signing.dlib") && game.MainScene.HasControl("signing.timestamp")),
            "Microsoft signing offered optional tool discovery overrides and timestamp configuration without credential fields");
        await game.CaptureAsync(Path.Combine(directory, "02b-microsoft-signing-tools.png"));
        await Click("signing.settings");
        Require(await Ui(() => ((Textbox)game.MainScene.Control("signing.account")).Text == "synthetic-signing-account"
            && ((Textbox)game.MainScene.Control("signing.profile")).Text == "synthetic-public-profile"),
            "Switching signing pages retained the draft Microsoft profile");
        await game.OnUiAsync(() => game.ResizeForAcceptance(980, 760));
        await Task.Delay(200);
        await game.CaptureAsync(Path.Combine(directory, "02c-compact-microsoft-signing.png"));
        Require(await Ui(() => game.MainScene.Control("signing.profile").Rectangle.Bottom < game.MainScene.Control("signing.check").Rectangle.Top
            && game.MainScene.Control("signing.save").Rectangle.Right < 980 && game.MainScene.Control("signing.save").Rectangle.Bottom < 760),
            "Microsoft signing fields and footer remained separate and usable at 980 by 760");
        await Click("signing.cancel");
        Require(await Ui(() => game.MainScene.Controller.Settings.WindowsCodeSigning.Provider == "none"),
            "Cancel discarded the signing draft without changing the active provider");
        Require(await Ui(() => game.MainScene.Control("workspace.signing").Rectangle.Bottom < game.MainScene.Control("workspace.build").Rectangle.Top),
            "Compact workspace kept Windows signing controls above the build actions");

        // A package-only build cannot silently ignore a selected Windows signature.
        var originalPackageHash = File.Exists(package) ? PackageSafety.HashFile(package) : null;
        await game.OnUiAsync(() => game.MainScene.Controller.Settings.WindowsCodeSigning = new()
        {
            Provider = "microsoft", Endpoint = "https://eus.codesigning.azure.net", AccountName = "synthetic-signing-account", CertificateProfileName = "synthetic-public-profile"
        });
        try
        {
            await Click("workspace.build");
            var until = DateTime.UtcNow.AddSeconds(5);
            while (await Ui(() => game.MainScene.Controller.Busy))
            {
                if (DateTime.UtcNow >= until) throw new TimeoutException("Package-only signing validation did not complete.");
                await Task.Delay(40);
            }
            var packageUnchanged = originalPackageHash is null ? !File.Exists(package) : File.Exists(package) && PackageSafety.HashFile(package) == originalPackageHash;
            Require(await Ui(() => game.MainScene.Controller.Failed && game.MainScene.Controller.StatusDetail.Contains("EXE setup", StringComparison.Ordinal)) && packageUnchanged,
                "Package-only output rejected selected Windows signing without changing the output or contacting the signing service");
        }
        finally
        {
            await game.OnUiAsync(() => { game.MainScene.Controller.Settings.WindowsCodeSigning = new(); game.ResizeForAcceptance(1180, 800); });
            await Task.Delay(200);
        }

        async Task Click(string id) { await game.OnUiAsync(() => game.Activate(id)); await Task.Delay(100); }
        async Task<T> Ui<T>(Func<T> get) { T value = default!; await game.OnUiAsync(() => value = get()); return value; }
        void Require(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
    }
}
