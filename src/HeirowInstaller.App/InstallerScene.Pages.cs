using System.Diagnostics;
using HeirowInstaller.Core;
using Microsoft.Xna.Framework;
using ProjectZ.Shared.Drawing.UI.Input;

namespace HeirowInstaller.App;

internal sealed partial class InstallerScene
{
    private void BuildInstalled()
    {
        Action("installed.refresh", "Refresh", Width - 149, 145, 115, () => { Controller.RefreshInstalled(); Controller.Notify(); }, enabled: !Controller.Busy);
        Text($"{Controller.Installed.Count} APPLICATION{(Controller.Installed.Count == 1 ? "" : "S")}", Left, 158, BodyWidth - 140, 22, 8.5f, Theme.Muted, true);
        if (Controller.Installed.Count == 0)
        {
            Box(Left, 211, BodyWidth, 341, Theme.Surface, 14);
            Logo(Left + 28, 239, 81);
            Text("A home for your applications.", Left + 28, 352, BodyWidth - 56, 43, 24, bold: true);
            Text("Applications installed with heirowInstaller appear here, with repair,\nuninstall and publisher updates in one place.", Left + 30, 414, BodyWidth - 60, 63, 11.5f, Theme.Muted, wrap: true);
            Action("installed.open", "Install an application  →", Left + 30, 491, 220, BrowsePackage, true, !Controller.Busy);
            return;
        }
        var listWidth = BodyWidth * 0.52f;
        var rows = Math.Max(1, (BodyBottom - 276) / 96);
        installedPage = Math.Clamp(installedPage, 0, (Controller.Installed.Count - 1) / rows);
        var offset = installedPage * rows;
        foreach (var product in Controller.Installed.Skip(offset).Take(rows))
        {
            var y = 211 + (Controller.Installed.ToList().IndexOf(product) - offset) * 96;
            var selected = Controller.SelectedProduct?.Id == product.Id;
            var button = Action("installed.select." + product.Id, "", Left, y, listWidth, () => { Controller.SelectedProduct = product; confirmUninstall = false; Controller.Notify(); });
            button.Size = new(listWidth, 82);
            button.BackgroundColor = selected ? Theme.GreenSurface : Theme.Surface;
            button.BorderColor = selected ? new Color(87, 117, 72) : Theme.Border;
            Box(Left + 16, y + 19, 43, 43, Theme.SurfaceRaised, 9, Color.Transparent);
            Text(product.Name[..1].ToUpperInvariant(), Left + 30, y + 28, 33, 30, 16, Theme.Lime, true);
            Text(product.Name, Left + 76, y + 17, listWidth - 95, 29, 12, selected ? Theme.Lime : Theme.Text, true);
            Text($"{product.Version}  ·  {product.Publisher}", Left + 76, y + 51, listWidth - 95, 24, 9.5f, Theme.Muted);
        }
        if (Controller.Installed.Count > rows)
        {
            Action("installed.previous", "Previous", Left, BodyBottom - 48, 115, () => { installedPage--; Controller.Notify(); }, enabled: installedPage > 0);
            Action("installed.next", "Next", Left + 128, BodyBottom - 48, 115, () => { installedPage++; Controller.Notify(); }, enabled: offset + rows < Controller.Installed.Count);
        }
        var x = Left + listWidth + 20;
        var width = BodyWidth - listWidth - 20;
        var selectedProduct = Controller.SelectedProduct!;
        Box(x, 211, width, BodyBottom - 217, Theme.Surface, 12);
        Pill("INSTALLED", x + 20, 231, 106);
        Text(selectedProduct.Name, x + 20, 281, width - 40, 70, 20, bold: true, wrap: true);
        Text("Version " + selectedProduct.Version, x + 20, 359, width - 40, 25, 11, Theme.Muted);
        Text(selectedProduct.Scope == "machine" ? "Everyone on this computer" : "Installed for you", x + 20, 392, width - 40, 25, 9.5f, Theme.Muted);
        Text(selectedProduct.IsMsi || selectedProduct.IsExecutable ? "Application location is managed by its original setup." : selectedProduct.InstallDirectory, x + 20, 430, width - 40, 45, 9, Theme.Muted, wrap: true);
        var actionWidth = width - 40;
        var ay = Math.Max(493, BodyBottom - 198);
        Action("installed.update", Controller.Update is { Available: true } update && update.Feed.ProductId == selectedProduct.Id ? "Install update " + update.Feed.Version : "Check for updates", x + 20, ay, actionWidth,
            () => { if (Controller.Update is { Available: true } candidate && candidate.Feed.ProductId == selectedProduct.Id) _ = Controller.ApplyUpdateAsync(); else _ = Controller.CheckUpdatesAsync(); }, true, !Controller.Busy);
        var canRepair = true; var canUninstall = true;
        if (selectedProduct.IsExecutable)
        {
            try
            {
                using var reader = PackageReader.Open(selectedProduct.PackagePath);
                canRepair = reader.Manifest.LegacyExecutable?.RepairArguments.Count > 0;
                canUninstall = !string.IsNullOrWhiteSpace(reader.Manifest.LegacyExecutable?.UninstallExecutable) || reader.Manifest.LegacyExecutable?.UninstallArguments.Count > 0;
            }
            catch { canRepair = false; canUninstall = false; }
        }
        Action("installed.repair", canRepair ? "Repair application" : "Repair not provided by publisher", x + 20, ay + 54, actionWidth, () => _ = Controller.RepairAsync(), enabled: !Controller.Busy && canRepair);
        Action("installed.uninstall", canUninstall ? "Uninstall" : "Open Windows Installed apps", x + 20, ay + 108, actionWidth, () =>
        {
            if (canUninstall) { confirmUninstall = true; Controller.Notify(); }
            else Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }, enabled: !Controller.Busy, accent: Theme.Danger);
    }

    private void BuildUninstallConfirmation()
    {
        Box(0, 0, Width, Height, new Color(0, 0, 0, 190), 0, Color.Transparent).isMouseBypassEnabled = false;
        var width = 520; var x = (Width - width) / 2; var y = (Height - 275) / 2;
        Box(x, y, width, 275, Theme.Surface, 14, Theme.Border);
        Text("Uninstall this application?", x + 25, y + 25, width - 50, 45, 20, bold: true);
        Text(Controller.SelectedProduct!.Name, x + 27, y + 92, width - 54, 29, 12, Theme.Orange, true);
        Text(Controller.SelectedProduct.IsMsi || Controller.SelectedProduct.IsExecutable ? "The original application setup will run its published\nuninstall behavior." : "heirowInstaller will remove its recorded application files\nand reverse the recorded installation changes.", x + 27, y + 135, width - 54, 58, 10.5f, Theme.Muted, wrap: true);
        Action("confirm.cancel", "Keep application", x + 25, y + 212, 175, () => { confirmUninstall = false; Controller.Notify(); });
        Action("confirm.uninstall", "Uninstall", x + width - 162, y + 212, 137, () => { confirmUninstall = false; _ = Controller.UninstallAsync(); }, true, accent: Theme.Danger);
    }

    private void BuildWorkspace()
    {
        Box(Left, 146, BodyWidth, 63, Theme.Surface, 10);
        Text("YOUR RELEASE WORKSPACE", Left + 20, 158, BodyWidth - 245, 24, 9, Theme.Lime, true);
        Text("Application → standalone setup → published update", Left + 20, 185, BodyWidth - 245, 20, 9, Theme.Muted);
        Action("workspace.publisher", Controller.SigningIn ? "Cancel sign-in" : Controller.PublisherSignedIn ? "Sign out" : "Sign in to SocketJack  ↗", Width - 252, 157, 199, () => _ = Controller.SignInPublisherAsync(), enabled: !Controller.Busy || Controller.SigningIn, accent: Theme.Orange);
        PathField("workspace.manifest", "PACKAGE MANIFEST", Controller.ManifestPath, 232, value => Controller.ManifestPath = value, () =>
        {
            using var picker = new System.Windows.Forms.OpenFileDialog { Title = "Choose package manifest", Filter = "Package manifest|*.json|All files|*.*", CheckFileExists = true };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) { Controller.ManifestPath = picker.FileName; if (string.IsNullOrWhiteSpace(Controller.SourceDirectory)) Controller.SourceDirectory = Path.GetDirectoryName(picker.FileName)!; Controller.Notify(); }
        });
        PathField("workspace.source", "APPLICATION FILES OR .NET PROJECT", Controller.SourceDirectory, 318, value => Controller.SourceDirectory = value, () =>
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose published application files", UseDescriptionForTitle = true, InitialDirectory = File.Exists(Controller.SourceDirectory) ? Path.GetDirectoryName(Controller.SourceDirectory) ?? "" : Controller.SourceDirectory };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) { Controller.SourceDirectory = picker.SelectedPath; Controller.Notify(); }
        }, () =>
        {
            using var picker = new System.Windows.Forms.OpenFileDialog { Title = "Choose an application project to publish", Filter = ".NET application project|*.csproj;*.vbproj;*.fsproj", CheckFileExists = true };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) { Controller.SourceDirectory = picker.FileName; Controller.Notify(); }
        });
        PathField("workspace.output", "OUTPUT INSTALLER", Controller.OutputPath, 404, value => Controller.OutputPath = value, () =>
        {
            using var picker = new System.Windows.Forms.SaveFileDialog { Title = "Choose output installer", Filter = "Standalone Windows setup|*.exe|heirowInstaller package|*.hinst", FileName = Path.GetFileName(Controller.OutputPath), DefaultExt = "exe", AddExtension = true };
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) { Controller.OutputPath = picker.FileName; Controller.Notify(); }
        });
        Text("RELEASE NOTES", Left, 490, BodyWidth, 22, 8.5f, Theme.Muted, true);
        Field("workspace.notes", Controller.ReleaseNotes, Left, 520, BodyWidth, value => Controller.ReleaseNotes = value, true, 52);
        Text(Controller.PublisherAccount, Left, 581, BodyWidth, 20, 9, Theme.Muted);
        Action("workspace.build", Controller.OutputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "Build setup  →" : "Build package  →", Left, BodyBottom - 48, 192, () => _ = Controller.BuildAsync(), true, !Controller.Busy);
        Action("workspace.publish", "Publish release", Left + 206, BodyBottom - 48, 170, () => _ = Controller.PublishAsync(), enabled: !Controller.Busy, accent: Theme.Orange);
        Action("workspace.test", "Open built installer", Width - 220, BodyBottom - 48, 186, () => _ = Controller.OpenPackageAsync(Controller.BuiltPackagePath ?? Controller.OutputPath), enabled: !Controller.Busy && File.Exists(Controller.BuiltPackagePath ?? Controller.OutputPath));
    }

    private void PathField(string id, string label, string value, float y, Action<string> changed, Action browse, Action? project = null)
    {
        Text(label, Left, y, BodyWidth, 22, 8.5f, Theme.Muted, true);
        Field(id, value, Left, y + 30, BodyWidth - (project is null ? 121 : 242), changed);
        Action(id + ".browse", project is null ? "Browse" : "Folder", Width - (project is null ? 142 : 263), y + 30, 108, browse, enabled: !Controller.Busy);
        if (project is not null) Action(id + ".project", "Project", Width - 142, y + 30, 108, project, enabled: !Controller.Busy);
    }

    private void BuildActivity()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "heirowInstaller", "Logs");
        Text("LATEST OPERATIONS", Left, 158, BodyWidth - 180, 25, 8.5f, Theme.Muted, true);
        Action("activity.logs", "Open log folder", Width - 203, 146, 169, () => OpenFolder(directory));
        var count = Math.Max(1, (BodyBottom - 280) / 78);
        activityPage = Math.Clamp(activityPage, 0, Math.Max(0, (Controller.Activity.Count - 1) / count));
        foreach (var entry in Controller.Activity.Skip(activityPage * count).Take(count))
        {
            var index = Controller.Activity.IndexOf(entry) - activityPage * count;
            var y = 211 + index * 78;
            Box(Left, y, BodyWidth, 67, Theme.Surface, 9);
            Box(Left + 16, y + 18, 6, 6, entry.Error ? Theme.Danger : Theme.Lime, 3, Color.Transparent);
            Text(entry.Title, Left + 37, y + 10, BodyWidth - 135, 26, 10.5f, entry.Error ? Theme.Danger : Theme.Text, true);
            Text(entry.Time.ToLocalTime().ToString("HH:mm:ss"), Width - 112, y + 13, 69, 22, 8.5f, Theme.Muted);
            Text(entry.Detail, Left + 37, y + 40, BodyWidth - 58, 22, 9, Theme.Muted);
        }
        if (Controller.Activity.Count > count)
        {
            Action("activity.previous", "Newer", Left, BodyBottom - 48, 115, () => { activityPage--; Controller.Notify(); }, enabled: activityPage > 0);
            Action("activity.next", "Older", Left + 128, BodyBottom - 48, 115, () => { activityPage++; Controller.Notify(); }, enabled: (activityPage + 1) * count < Controller.Activity.Count);
        }
    }

    private void BuildSettings()
    {
        Box(Left, 147, BodyWidth, 194, Theme.Surface, 12);
        Text("A focused native workspace", Left + 24, 169, BodyWidth - 48, 36, 18, bold: true);
        Text("PROJECT-Z GRAPHICS", Left + 25, 224, BodyWidth - 50, 23, 8, Theme.Muted, true);
        Text("16× anti-aliasing  ·  16× anisotropic filtering", Left + 25, 257, BodyWidth - 50, 31, 13, Theme.Lime, true);
        Text("Native vector text, keyboard navigation and resizable desktop layouts.", Left + 25, 302, BodyWidth - 235, 26, 10, Theme.Muted);
        Action("settings.updater", "Open updater  →", Width - 233, 286, 178, () => Controller.Navigate(DesktopPage.Updates));
        Text("DEFAULT PACKAGE OUTPUT FOLDER", Left, 373, BodyWidth, 23, 8.5f, Theme.Muted, true);
        Field("settings.output", Controller.Settings.DefaultOutputDirectory, Left, 406, BodyWidth, value => Controller.Settings.DefaultOutputDirectory = value);
        Toggle("settings.launch", "Launch applications after installation", "Open the installed entry point when setup completes.", 476, Controller.Settings.LaunchAfterInstall, () => { Controller.Settings.LaunchAfterInstall = !Controller.Settings.LaunchAfterInstall; Controller.Notify(); });
        Toggle("settings.register", "Register applications with Windows", "Show installed products in Windows Installed apps.", 551, Controller.Settings.RegisterProducts, () => { Controller.Settings.RegisterProducts = !Controller.Settings.RegisterProducts; Controller.Notify(); });
        Action("settings.save", "Save preferences", Left, BodyBottom - 48, 184, Controller.SaveSettings, true);
    }

    private void BuildUpdates()
    {
        Box(Left, 147, BodyWidth, 146, Theme.Surface, 12);
        Pill("HEIROWUPDATER", Left + 22, 168, 150, Theme.Orange);
        Text("Your next release, one step away.", Left + 23, 216, BodyWidth - 46, 40, 21, bold: true);
        Text("Check every installed application through its publisher's update feed.", Left + 24, 263, BodyWidth - 48, 26, 10.5f, Theme.Muted);
        var available = File.Exists(Controller.UpdaterPath);
        Action("updates.check", "Check all applications", Left, 317, 218, () => _ = Controller.RunUpdaterAsync("check", "--all", "--json"), true, available && !Controller.Busy);
        Action("updates.apply", "Install available updates", Left + 233, 317, 228, () => _ = Controller.RunUpdaterAsync("update", "--all", "--json"), enabled: available && !Controller.Busy, accent: Theme.Orange);
        Action("updates.enable", "Enable background checks", Left, 375, 232, () => _ = Controller.RunUpdaterAsync("enable", "--interval-minutes", "60"), enabled: available && !Controller.Busy);
        Action("updates.disable", "Disable checks", Left + 247, 375, 168, () => _ = Controller.RunUpdaterAsync("disable"), enabled: available && !Controller.Busy);
        Action("updates.status", "Schedule status", Left + 430, 375, 169, () => _ = Controller.RunUpdaterAsync("status", "--json"), enabled: available && !Controller.Busy);
        Action("updates.auto", "Enable signed auto-updates", Left, 433, 247, () => _ = Controller.RunUpdaterAsync("enable", "--interval-minutes", "60", "--apply"), enabled: available && !Controller.Busy, accent: Theme.Orange);
        Text("Hourly checks run through your Windows user account.", Left + 267, 446, BodyWidth - 267, 26, 9, Theme.Muted);
        Text(available ? "UPDATE RESULTS" : "The companion updater is included in the complete installed release.", Left, 503, BodyWidth, 27, 8.5f, Theme.Muted, available);
        var output = Field("updates.results", Controller.UpdaterOutput, Left, 539, BodyWidth, _ => { }, true, Math.Max(100, BodyBottom - 548));
        ReadOnlyField(output);
    }

    private void Toggle(string id, string title, string description, float y, bool selected, Action changed)
    {
        Text(title, Left, y, BodyWidth - 100, 28, 11, bold: true);
        Text(description, Left, y + 33, BodyWidth - 100, 26, 9.5f, Theme.Muted);
        Action(id, selected ? "ON" : "OFF", Width - 110, y + 4, 76, changed, selected);
    }
}
