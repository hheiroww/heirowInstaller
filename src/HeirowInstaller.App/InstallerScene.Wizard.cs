using Microsoft.Xna.Framework;

namespace HeirowInstaller.App;

internal sealed partial class InstallerScene
{
    private void BuildWizardRail()
    {
        Box(0, 0, RailWidth, Height, Theme.Rail, 0, Color.Transparent);
        Box(RailWidth - 1, 0, 1, Height, Theme.Border, 0, Color.Transparent);
        Logo(27, 28, 56);
        var applicationName = Controller.Manifest?.Name ?? "Application setup";
        Text(applicationName, 27, 105, RailWidth - 54, 57, applicationName.Length > 14 ? 17 : 19, bold: true, wrap: true);
        Text(Controller.Manifest is { } manifest ? "Version " + manifest.Version : "Preparing your installation", 28, 174, RailWidth - 56, 26, 9.5f, Theme.Muted);
        var labels = Controller.IsMaintenanceWizard ? new[] { "Review", Controller.MaintenanceOperation == "repair" ? "Repairing" : "Uninstalling", "Finish" }
            : new[] { "Welcome", "Features", "Location", "Ready to install", "Installing", "Finish" };
        var selectedStage = Controller.IsMaintenanceWizard ? Controller.Stage == SetupStage.Complete ? 2 : Controller.Stage == SetupStage.Progress ? 1 : 0 : (int)Controller.Stage;
        for (var i = 0; i < labels.Length; i++)
        {
            var active = Controller.Manifest is not null && selectedStage == i;
            var done = Controller.Manifest is not null && selectedStage > i;
            var y = 232 + i * 51;
            if (active) Box(19, y - 7, RailWidth - 38, 43, Theme.GreenSurface, 8, Color.Transparent);
            Text(done ? "✓" : (i + 1).ToString("00"), 32, y + 2, 27, 24, 9, active || done ? Theme.Lime : Theme.Muted, true);
            Text(labels[i], 73, y + 1, RailWidth - 96, 27, 10.5f, active ? Theme.Text : Theme.Muted, active);
        }
        Box(26, Height - 133, RailWidth - 52, 1, Theme.Border, 0, Color.Transparent);
        Text(Controller.Manifest?.Publisher ?? "", 28, Height - 113, RailWidth - 56, 31, 10, Theme.Muted, wrap: true);
        Text("Powered by heirowInstaller", 28, Height - 63, RailWidth - 56, 22, 8, Theme.Muted);
        Text("SOCKETJACK", 28, Height - 41, RailWidth - 56, 20, 7.5f, Theme.Lime, true);
    }

    private void BuildWizardHeader()
    {
        var name = Controller.Manifest?.Name;
        var title = name is null ? "Preparing setup" : Controller.Stage == SetupStage.Complete ? name + " is ready" : "Install " + name;
        if (Controller.IsMaintenanceWizard && name is not null)
            title = Controller.Stage == SetupStage.Complete && Controller.MaintenanceOperation == "uninstall" ? name + " was removed" :
                Controller.Stage == SetupStage.Complete ? name + " is ready" : (Controller.MaintenanceOperation == "repair" ? "Repair " : "Uninstall ") + name;
        Text(title, Left, 30, BodyWidth, 43, 24, bold: true);
        var subtitle = Controller.Stage switch
        {
            SetupStage.Features => "Choose the components you want to install.",
            SetupStage.Destination => "Choose where the application will be installed.",
            SetupStage.Review => "Confirm your choices before installation begins.",
            SetupStage.Progress => "Keep this window open while setup finishes.",
            SetupStage.Complete => "Setup has finished. You can start using your application.",
            _ => "A few steps, then you're ready to go."
        };
        if (Controller.IsMaintenanceWizard) subtitle = Controller.Stage == SetupStage.Complete ? "Choose Finish to close setup." :
            Controller.Stage == SetupStage.Progress ? "Keep this window open while setup finishes." : "Only this application's installation will be changed.";
        Text(subtitle, Left + 1, 83, BodyWidth - (Controller.Manifest?.LegalDocuments is null ? 2 : 195), 27, 10.5f, Theme.Muted);
        if (Controller.Manifest?.LegalDocuments is not null)
            Action("wizard.legal", "Privacy & terms", Width - 213, 75, 179, () =>
            { showPackageLegal = true; showLegalEditor = false; legalTextPage = 0; Controller.Notify(); });
        Box(Left, 120, BodyWidth, 1, Theme.Border, 0, Color.Transparent);
    }

    private void BuildWizardPreparing()
    {
        Box(Left, 158, BodyWidth, 296, Theme.Surface, 14);
        Text(Controller.Failed ? "Setup needs attention" : "Getting your setup ready", Left + 28, 196, BodyWidth - 56, 46, 24, bold: true);
        Text(Controller.Failed ? Controller.StatusDetail : "Checking the application and its installation files.", Left + 29, 267, BodyWidth - 58, 95, 12, Theme.Muted, wrap: true);
        Action("wizard.cancel", "Close", Width - 186, BodyBottom - 48, 152, game.Exit);
    }

    private void BuildWizardWelcome(float y)
    {
        var app = Controller.Manifest!;
        Box(Left, y, BodyWidth, 250, Theme.Surface, 14);
        Logo(Left + 28, y + 29, 82);
        Text("Welcome to " + app.Name, Left + 134, y + 34, BodyWidth - 162, 78, 24, bold: true, wrap: true);
        Text("Version " + app.Version + "  ·  " + app.Publisher, Left + 29, y + 137, BodyWidth - 58, 30, 11, Theme.Muted);
        Text(string.IsNullOrWhiteSpace(app.Description) ? "Setup will guide you through installing " + app.Name + "." : app.Description,
            Left + 29, y + 183, BodyWidth - 58, 51, 11.5f, Theme.Muted, wrap: true);
        var bottom = y + 278;
        Text("Make it yours", Left + 2, bottom, BodyWidth - 4, 36, 17, bold: true);
        Text("Choose features and a location, then review your choices. Your application files will stay unchanged until you choose Install application.",
            Left + 3, bottom + 51, BodyWidth - 6, 75, 11, Theme.Muted, wrap: true);
        if (Controller.LicenseText is not null)
        {
            Text(Controller.LicenseAccepted ? "✓  License accepted" : "This application includes a license agreement.", Left + 3, bottom + 143, BodyWidth - 190, 29, 10, Controller.LicenseAccepted ? Theme.Lime : Theme.Muted);
            Action("setup.license", "Review license", Width - 207, bottom + 135, 173, () => { showLicense = true; Controller.Notify(); });
        }
    }

    private void BuildWizardComplete(float y)
    {
        var app = Controller.Manifest!;
        var removed = Controller.MaintenanceOperation == "uninstall";
        var repaired = Controller.MaintenanceOperation == "repair";
        Box(Left, y, BodyWidth, 335, Theme.Surface, 14);
        Logo(Left + 29, y + 29, 76);
        Pill(removed ? "✓  REMOVED" : repaired ? "✓  REPAIRED" : "✓  INSTALLED", Left + 129, y + 35, 149);
        Text(removed ? "Uninstall complete." : repaired ? "Ready again." : "You're all set.", Left + 29, y + 131, BodyWidth - 58, 57, 31, bold: true);
        Text(app.Name + " " + app.Version + (removed ? " has been removed." : repaired ? " has been repaired." : " has been installed."), Left + 31, y + 204, BodyWidth - 62, 55, 13, Theme.Muted, wrap: true);
        Text(Controller.LastInstallation?.RebootRequired == true ? "Restart Windows to finish applying all changes." : "Choose Finish to close setup.", Left + 31, y + 281, BodyWidth - 62, 31, 10.5f, Theme.Muted);
        if (Controller.CanOpenInstalledApplication)
        {
            var open = Action("wizard.open", "", Left + 1, y + 366, BodyWidth - 2, () => { Controller.OpenApplicationOnFinish = !Controller.OpenApplicationOnFinish; Controller.Notify(); });
            open.BackgroundColor = Controller.OpenApplicationOnFinish ? Theme.GreenSurface : Theme.Surface;
            open.Size = new(BodyWidth - 2, 62);
            Text(Controller.OpenApplicationOnFinish ? "✓" : "○", Left + 19, y + 386, 29, 27, 12, Theme.Lime, true);
            Text("Open " + app.Name, Left + 56, y + 384, BodyWidth - 78, 29, 11.5f, bold: true);
        }
        Action("wizard.finish", "Finish", Width - 202, BodyBottom - 48, 168, () => _ = Controller.FinishAsync(), true);
    }

    private void BuildWizardDetails()
    {
        Box(0, 0, Width, Height, new Color(0, 0, 0, 200), 0, Color.Transparent).isMouseBypassEnabled = false;
        var width = Math.Min(740, Width - 80); var height = Math.Min(540, Height - 120);
        var x = (Width - width) / 2; var y = (Height - height) / 2;
        Box(x, y, width, height, Theme.Surface, 14);
        Text("Installation details", x + 25, y + 24, width - 50, 41, 21, bold: true);
        Text(Controller.Manifest?.Name ?? "Setup", x + 27, y + 79, width - 54, 28, 11, Theme.Muted);
        var details = Field("wizard.details", string.Join(Environment.NewLine, Controller.Activity.Select(entry => $"{entry.Time:HH:mm:ss}  {entry.Title}: {entry.Detail}")),
            x + 25, y + 127, width - 50, _ => { }, true, height - 217);
        ReadOnlyField(details);
        Action("wizard.details.close", "Close details", x + width - 198, y + height - 65, 173, () => { showWizardDetails = false; Controller.Notify(); }, true);
    }

    private void BuildMaintenanceWizard()
    {
        if (Controller.Manifest is null) { BuildWizardPreparing(); return; }
        if (Controller.Stage == SetupStage.Progress) { BuildProgress(151); return; }
        if (Controller.Stage == SetupStage.Complete) { BuildWizardComplete(151); return; }
        var app = Controller.Manifest;
        var repair = Controller.MaintenanceOperation == "repair";
        Box(Left, 151, BodyWidth, 333, Theme.Surface, 14);
        Logo(Left + 29, 181, 78);
        Text(app.Name, Left + 131, 187, BodyWidth - 160, 70, 24, bold: true, wrap: true);
        Text("Version " + app.Version + "  ·  " + app.Publisher, Left + 30, 289, BodyWidth - 60, 30, 11, Theme.Muted);
        Text(repair ? "Repair restores this application's installed files and configured components." : "Uninstall removes this application and its recorded installation changes.",
            Left + 30, 345, BodyWidth - 60, 60, 12, Theme.Muted, wrap: true);
        var location = Field("maintenance.location", Controller.DisplayInstallDirectory, Left + 30, 421, BodyWidth - 60, _ => { }, height: 42);
        ReadOnlyField(location);
        Text("Save your work before continuing. Setup will ask before closing any application that is using these files.",
            Left + 2, 522, BodyWidth - 4, 65, 11, Theme.Muted, wrap: true);
        Action("wizard.cancel", "Cancel", Left, BodyBottom - 48, 112, game.Exit, enabled: !Controller.Busy);
        Action("maintenance.start", repair ? "Repair application" : "Uninstall application", Width - 243, BodyBottom - 48, 209,
            () => { if (repair) _ = Controller.RepairAsync(); else _ = Controller.UninstallAsync(); }, true, !Controller.Busy);
    }
}
