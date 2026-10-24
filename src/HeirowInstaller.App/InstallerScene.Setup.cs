using Microsoft.Xna.Framework;

namespace HeirowInstaller.App;

internal sealed partial class InstallerScene
{
    private void BuildInstall()
    {
        if (Controller.Manifest is null) { BuildWelcome(); return; }
        var stage = Controller.Stage;
        var stages = new[] { "Package", "Features", "Location", "Review" };
        var stepWidth = BodyWidth / 4f;
        for (var i = 0; i < stages.Length; i++)
        {
            var active = (int)stage == i;
            var done = (int)stage > i;
            Box(Left + i * stepWidth, 147, stepWidth - 10, 43, active ? Theme.GreenSurface : Theme.Surface, 8, active ? Theme.Lime : Theme.Border);
            Text(done ? "✓" : (i + 1).ToString("00"), Left + 13 + i * stepWidth, 160, 30, 20, 9, active || done ? Theme.Lime : Theme.Muted, true);
            Text(stages[i], Left + 48 + i * stepWidth, 158, stepWidth - 62, 25, 10, active ? Theme.Lime : Theme.Muted, active);
        }
        var y = 214;
        switch (stage)
        {
            case SetupStage.Package: BuildPackageSummary(y); break;
            case SetupStage.Features: BuildFeatures(y); break;
            case SetupStage.Destination: BuildDestination(y); break;
            case SetupStage.Review: BuildReview(y); break;
            case SetupStage.Progress: BuildProgress(y); break;
            case SetupStage.Complete: BuildComplete(y); break;
        }
        if (stage < SetupStage.Progress)
        {
            Action("setup.open", "Change package", Left, BodyBottom - 48, 152, BrowsePackage, enabled: !Controller.Busy);
            if (stage != SetupStage.Package) Action("setup.back", "Back", Width - 300, BodyBottom - 48, 100, Controller.PreviousStage, enabled: !Controller.Busy);
            Action("setup.next", stage == SetupStage.Review ? "Install application  →" : "Continue  →", Width - 186, BodyBottom - 48, 152, () =>
            {
                if (Controller.LicenseText is not null && !Controller.LicenseAccepted) { showLicense = true; Controller.Notify(); }
                else Controller.NextStage();
            }, true, !Controller.Busy);
        }
    }

    private void BuildWelcome()
    {
        var y = 148;
        Box(Left, y, BodyWidth, 244, Theme.Surface, 14);
        Pill("THE NEXT STEP FOR YOUR SOFTWARE", Left + 27, y + 25, 278, Theme.Orange);
        Text("From release to ready.", Left + 27, y + 80, BodyWidth - 210, 55, 29, bold: true);
        Text("Install, maintain and ship your applications with a\nfocused native experience, powered by Project-Z.", Left + 29, y + 144, BodyWidth - 225, 66, 12, Theme.Muted, wrap: true);
        Logo(Width - 210, y + 66, 142);
        var gap = 18;
        var cardWidth = (BodyWidth - gap) / 2f;
        y += 264;
        Box(Left, y, cardWidth, 209, Theme.Surface, 12);
        Text("01", Left + 24, y + 20, 40, 30, 10, Theme.Lime, true);
        Text("Install a package", Left + 24, y + 55, cardWidth - 48, 36, 17, bold: true);
        Text("Open a .hinst package or published setup\nexecutable. Review every step before installing.", Left + 24, y + 99, cardWidth - 48, 50, 10, Theme.Muted, wrap: true);
        Action("setup.open", "Open installer  →", Left + 24, y + 155, Math.Min(180, cardWidth - 48), BrowsePackage, true, !Controller.Busy);
        var x = Left + cardWidth + gap;
        Box(x, y, cardWidth, 209, Theme.Surface, 12);
        Text("02", x + 24, y + 20, 40, 30, 10, Theme.Orange, true);
        Text("Build your next release", x + 24, y + 55, cardWidth - 48, 36, 17, bold: true);
        Text("Package a manifest and application files,\nthen publish a release with an update feed.", x + 24, y + 99, cardWidth - 48, 50, 10, Theme.Muted, wrap: true);
        Action("welcome.workspace", "Open workspace  →", x + 24, y + 155, Math.Min(191, cardWidth - 48), () => Controller.Navigate(DesktopPage.Workspace), accent: Theme.Orange);
        var bottom = y + 232;
        Text("CHECKED PAYLOADS", Left + 2, bottom, BodyWidth / 3 - 10, 25, 8.5f, Theme.Muted, true);
        Text("TRANSACTIONAL CHANGES", Left + BodyWidth / 3, bottom, BodyWidth / 3 - 10, 25, 8.5f, Theme.Muted, true);
        Text("PUBLISHED UPDATES", Left + BodyWidth * 2 / 3, bottom, BodyWidth / 3 - 10, 25, 8.5f, Theme.Muted, true);
    }

    private void BuildPackageSummary(float y)
    {
        var package = Controller.Manifest!;
        Box(Left, y, BodyWidth, 182, Theme.Surface, 12);
        Logo(Left + 24, y + 30, 82);
        Pill("PAYLOAD VERIFIED", Width - 213, y + 25, 153);
        Text(package.Name, Left + 128, y + 30, BodyWidth - 330, 44, 24, bold: true);
        Text($"Version {package.Version}  ·  {package.Publisher}", Left + 130, y + 86, BodyWidth - 165, 26, 11, Theme.Muted);
        Text(string.IsNullOrWhiteSpace(package.Description) ? "Your application is ready to install." : package.Description, Left + 27, y + 135, BodyWidth - 54, 32, 10.5f, Theme.Muted);
        var rowY = y + 207;
        var col = BodyWidth / 3f;
        Metric(package.LegacyMsi is not null || package.LegacyExecutable is not null ? "PACKAGE SIZE" : "INSTALL SIZE", SizeLabel(Controller.Plan!.TotalBytes), Left, rowY, col - 12);
        Metric("PACKAGE CONTENTS", $"{Controller.Plan.Files.Count:N0} files", Left + col, rowY, col - 12);
        Metric("INSTALL TYPE", package.LegacyMsi is not null ? "MSI compatibility" : package.LegacyExecutable is not null ? "EXE compatibility" : "Native package", Left + col * 2, rowY, col);
        Box(Left, rowY + 110, BodyWidth, 75, Theme.Field, 10);
        Text(Controller.LicenseText is null ? "A clear installation plan" : Controller.LicenseAccepted ? "License accepted" : "License agreement included", Left + 20, rowY + 124, BodyWidth - 230, 25, 11, bold: true);
        Text(Controller.LicenseText is null ? "Choose features, confirm the destination and review changes before installation begins." : "Read the publisher's license before continuing.", Left + 20, rowY + 158, BodyWidth - 230, 25, 10, Theme.Muted);
        if (Controller.LicenseText is not null) Action("setup.license", "Review license", Width - 212, rowY + 130, 156, () => { showLicense = true; Controller.Notify(); });
    }

    private void Metric(string label, string value, float x, float y, float width)
    {
        Box(x, y, width, 88, Theme.Surface, 10);
        Text(label, x + 17, y + 16, width - 30, 22, 8, Theme.Muted, true);
        Text(value, x + 17, y + 46, width - 30, 31, 17, bold: true);
    }

    private void BuildFeatures(float y)
    {
        Text("Make it fit your workflow.", Left, y, BodyWidth, 40, 21, bold: true);
        Text("Required components are always installed. Optional features can be changed below.", Left, y + 50, BodyWidth, 29, 10.5f, Theme.Muted);
        var features = Controller.Manifest!.Features;
        if (features.Count == 0)
        {
            Box(Left, y + 102, BodyWidth, 134, Theme.Surface, 12);
            Text("✓  Complete application", Left + 23, y + 125, BodyWidth - 46, 35, 16, Theme.Lime, true);
            Text("This package includes all its components. No optional features need to be selected.", Left + 24, y + 174, BodyWidth - 48, 34, 11, Theme.Muted);
            return;
        }
        var available = Math.Max(1, (BodyBottom - (int)y - 150) / 82);
        var offset = Math.Clamp(featurePage * available, 0, Math.Max(0, features.Count - 1));
        foreach (var feature in features.Skip(offset).Take(available))
        {
            var selected = Controller.Features.Contains(feature.Id) || feature.Required;
            var fy = y + 99 + (features.IndexOf(feature) - offset) * 82;
            Box(Left, fy, BodyWidth, 70, selected ? Theme.GreenSurface : Theme.Surface, 9, selected ? new Color(66, 92, 62) : Theme.Border);
            Action("feature." + feature.Id, selected ? "✓" : "+", Left + 16, fy + 14, 42, () =>
            {
                if (!Controller.Features.Add(feature.Id)) Controller.Features.Remove(feature.Id);
                Controller.Notify();
            }, primary: selected, enabled: !feature.Required && !Controller.Busy);
            Text(feature.Name, Left + 75, fy + 10, BodyWidth - 200, 26, 11, bold: true);
            Text(feature.Description, Left + 75, fy + 40, BodyWidth - 100, 24, 9.5f, Theme.Muted);
            if (feature.Required) Text("REQUIRED", Width - 129, fy + 15, 75, 22, 8, Theme.Muted, true);
        }
        if (features.Count > available)
        {
            Action("features.previous", "Previous", Left, BodyBottom - 99, 110, () => { featurePage = Math.Max(0, featurePage - 1); Controller.Notify(); }, enabled: featurePage > 0);
            Action("features.next", "More features", Left + 123, BodyBottom - 99, 144, () => { featurePage++; Controller.Notify(); }, enabled: offset + available < features.Count);
        }
    }

    private int featurePage;

    private void BuildDestination(float y)
    {
        if (Controller.OriginalInstallerDestination)
        {
            Text("The original setup knows the way.", Left, y, BodyWidth, 45, 21, bold: true);
            Text("This package preserves the application's published installation behavior.", Left, y + 57, BodyWidth, 32, 10.5f, Theme.Muted);
            Box(Left, y + 112, BodyWidth, 181, Theme.Surface, 12);
            Pill("ORIGINAL INSTALLER DEFAULT", Left + 23, y + 135, 240, Theme.Orange);
            Text("Application destination", Left + 24, y + 186, BodyWidth - 48, 32, 16, bold: true);
            Text("The original application setup chooses its authored installation folder and scope.\nThis keeps existing installs, shortcuts and application updates compatible.", Left + 24, y + 231, BodyWidth - 48, 56, 11, Theme.Muted, wrap: true);
            return;
        }
        Text("Choose where it belongs.", Left, y, BodyWidth, 40, 21, bold: true);
        Text("Install for yourself or make the application available to everyone on this computer.", Left, y + 50, BodyWidth, 29, 10.5f, Theme.Muted);
        Text("INSTALL LOCATION", Left, y + 104, BodyWidth, 22, 8.5f, Theme.Muted, true);
        Field("setup.target", Controller.TargetDirectory, Left, y + 137, BodyWidth - 121, value => Controller.TargetDirectory = value);
        Action("setup.browseTarget", "Browse", Width - 142, y + 137, 108, () =>
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose installation folder", UseDescriptionForTitle = true, InitialDirectory = Controller.TargetDirectory };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) { Controller.TargetDirectory = picker.SelectedPath; Controller.Notify(); }
        });
        var width = (BodyWidth - 16) / 2f;
        ScopeCard("user", "Just for me", "Uses your local application folder.\nUsually no administrator access is needed.", Left, y + 214, width);
        ScopeCard("machine", "Everyone on this computer", "Installs for every user.\nWindows will request administrator access.", Left + width + 16, y + 214, width);
        Text("The review step shows the final destination and every planned change.", Left, y + 382, BodyWidth, 30, 10, Theme.Muted);
    }

    private void ScopeCard(string scope, string title, string description, float x, float y, float width)
    {
        var selected = Controller.Scope == scope;
        var button = Action("setup.scope." + scope, "", x, y, width, () =>
        {
            Controller.Scope = scope;
            Controller.TargetDirectory = "";
            try { Controller.Replan(); } catch { }
            Controller.Notify();
        });
        button.Size = new(width, 140);
        button.BackgroundColor = selected ? Theme.GreenSurface : Theme.Surface;
        button.BorderColor = selected ? Theme.Lime : Theme.Border;
        Text(selected ? "●  " + title : "○  " + title, x + 19, y + 19, width - 38, 34, 12, selected ? Theme.Lime : Theme.Text, true);
        Text(description, x + 19, y + 70, width - 38, 54, 10, Theme.Muted, wrap: true);
    }

    private void BuildReview(float y)
    {
        Text("Ready to make it yours.", Left, y, BodyWidth, 40, 21, bold: true);
        Text("Review the installation. Changes begin when you choose Install application.", Left, y + 50, BodyWidth, 29, 10.5f, Theme.Muted);
        Box(Left, y + 100, BodyWidth, 135, Theme.Surface, 12);
        Text(Controller.Manifest!.Name + "  " + Controller.Manifest.Version, Left + 24, y + 117, BodyWidth - 48, 35, 16, bold: true);
        Text(Controller.DisplayInstallDirectory, Left + 24, y + 164, BodyWidth - 48, 26, 10.5f, Theme.Muted);
        Text(Controller.Scope == "machine" ? "All users · Administrator access required" : "Current user", Left + 24, y + 201, BodyWidth - 48, 24, 9, Theme.Orange);
        var operations = Field("setup.operations", string.Join(Environment.NewLine, Controller.Plan!.Operations.Select(operation => "✓  " + operation)), Left, y + 258, BodyWidth, _ => { }, true, Math.Max(80, BodyBottom - 63 - y - 258));
        operations.IsReadOnly = true;
    }

    private void BuildProgress(float y)
    {
        Logo(Left + BodyWidth / 2 - 48, y + 14, 96);
        Text(Controller.Failed ? "Installation needs attention" : "Making everything ready.", Left + 24, y + 132, BodyWidth - 48, 48, 25, bold: true);
        Text(Controller.Manifest!.Name, Left + 24, y + 195, BodyWidth - 48, 31, 13, Theme.Muted);
        Box(Left + 24, y + 262, BodyWidth - 48, 8, Theme.SurfaceRaised, 4, Color.Transparent);
        Box(Left + 24, y + 262, Math.Max(8, (BodyWidth - 48) * (float)Controller.Progress), 8, Controller.Failed ? Theme.Danger : Theme.Lime, 4, Color.Transparent);
        Text($"{Controller.Progress:P0}", Left + 24, y + 294, BodyWidth - 48, 30, 14, Theme.Lime, true);
        Text(Controller.StatusDetail, Left + 24, y + 335, BodyWidth - 48, Math.Clamp(BodyBottom - 58 - y - 335, 40, 65), 11, Theme.Muted, wrap: true);
        Action("setup.cancel", Controller.Busy ? "Cancel installation" : "Back to review", Left + 24, BodyBottom - 48, 184, () => { if (Controller.Busy) Controller.Cancel(); else { Controller.Stage = SetupStage.Review; Controller.Notify(); } });
        Action("setup.logs", "View activity", Width - 181, BodyBottom - 48, 147, () => Controller.Navigate(DesktopPage.Activity));
    }

    private void BuildComplete(float y)
    {
        Box(Left, y, BodyWidth, 327, Theme.Surface, 14);
        Pill("✓  INSTALLATION COMPLETE", Left + 26, y + 28, 226);
        Text("You're all set.", Left + 26, y + 94, BodyWidth - 52, 59, 31, bold: true);
        Text(Controller.Manifest!.Name + " " + Controller.Manifest.Version + " is ready to use.", Left + 28, y + 175, BodyWidth - 56, 37, 13, Theme.Muted);
        Text(Controller.DisplayInstallDirectory, Left + 28, y + 229, BodyWidth - 56, 29, 10.5f, Theme.Muted);
        Action("setup.installed", "Manage application  →", Left + 27, y + 269, 208, () => Controller.Navigate(DesktopPage.Installed), true);
        if (!Controller.OriginalInstallerDestination) Action("setup.openFolder", "Open folder", Left + 249, y + 269, 147, () => OpenFolder(Controller.TargetDirectory));
        Text("Updates, repair and uninstall are available in Installed apps.", Left + 4, y + 360, BodyWidth - 8, 34, 11, Theme.Muted);
        Action("setup.another", "Install another package", Left, BodyBottom - 48, 210, BrowsePackage);
    }

    private void BuildLicense()
    {
        Box(0, 0, Width, Height, new Color(0, 0, 0, 200), 0, Color.Transparent).isMouseBypassEnabled = false;
        var width = Math.Min(740, Width - 80); var height = Math.Min(545, Height - 120);
        var x = (Width - width) / 2; var y = (Height - height) / 2;
        Box(x, y, width, height, Theme.Surface, 14);
        Text("License agreement", x + 25, y + 23, width - 50, 42, 21, bold: true);
        Text("Provided by " + Controller.Manifest!.Publisher, x + 27, y + 79, width - 54, 27, 10, Theme.Muted);
        var license = Field("license.text", Controller.LicenseText ?? "", x + 25, y + 123, width - 50, _ => { }, true, height - 212);
        license.IsReadOnly = true;
        Action("license.close", "Close", x + 25, y + height - 65, 116, () => { showLicense = false; Controller.Notify(); });
        Action("license.accept", "Accept license", x + width - 198, y + height - 65, 173, () => { Controller.LicenseAccepted = true; showLicense = false; Controller.Notify(); }, true);
    }
}
