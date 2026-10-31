using Microsoft.Xna.Framework;

namespace HeirowInstaller.App;

internal sealed partial class InstallerScene
{
    private int runningApplicationsPage;

    private void BuildRunningApplicationsDialog()
    {
        var dialog = Controller.RunningApplicationsDialog!;
        Box(0, 0, Width, Height, new Color(0, 0, 0, 205), 0, Color.Transparent).isMouseBypassEnabled = false;
        var width = Math.Min(784, Width - 80);
        var height = Math.Min(590, Height - 90);
        var x = (Width - width) / 2;
        var y = (Height - height) / 2;
        var applications = dialog.ConfirmForceClose ? dialog.Applications.Where(application => application.CanForceClose).ToArray() : dialog.Applications;
        Box(x, y, width, height, Theme.Surface, 16);
        Box(x + 27, y + 27, 46, 46, Theme.GreenSurface, 12, Color.Transparent);
        Text("Ⅱ", x + 40, y + 36, 28, 29, 17, Theme.Lime, true);
        Text(dialog.ConfirmForceClose ? $"Force close {applications.Count} app{(applications.Count == 1 ? "" : "s")}?" : dialog.InspectionTimedOut ? "Application check took too long" : dialog.Waiting ? "Finish saving your work" : dialog.IsRetry ? "An app is still open" : "Close apps to continue", x + 91, y + 27, width - 118, 42, 23, bold: true);
        Text(dialog.ProductName, x + 92, y + 73, width - 120, 27, 11, Theme.Lime, true);
        Text(dialog.ConfirmForceClose ? "Only the apps listed below will be forced to quit. Unsaved work in these apps will be lost. Save your work before confirming."
            : dialog.InspectionTimedOut ? "The application check did not finish in time. Installation is paused. You can check again or cancel."
            : dialog.ManualOnly ? "Close this installer dependency yourself, then check again." : "These apps are using files that need to change before installation can continue.",
            x + 29, y + 119, width - 58, 49, 11, Theme.Muted, wrap: true);

        const int rows = 3;
        var pages = Math.Max(1, (applications.Count + rows - 1) / rows);
        runningApplicationsPage = Math.Clamp(runningApplicationsPage, 0, pages - 1);
        var listY = y + 181;
        foreach (var (application, index) in applications.Skip(runningApplicationsPage * rows).Take(rows).Select((application, index) => (application, index)))
        {
            var rowY = listY + index * 68;
            Box(x + 27, rowY, width - 54, 60, Theme.Field, 9);
            Box(x + 40, rowY + 12, 35, 35, Theme.GreenSurface, 8, Color.Transparent);
            Text(application.DisplayName.Length == 0 ? "A" : application.DisplayName[..1].ToUpperInvariant(), x + 51, rowY + 18, 27, 27, 12, Theme.Lime, true);
            Text(application.DisplayName + "  ·  PID " + application.ProcessId, x + 89, rowY + 8, width - 133, 25, 11, bold: true);
            Text(dialog.ConfirmForceClose ? application.ExecutablePath : application.CanRequestClose ? "Save your work before continuing" : application.CanForceClose ? "Close manually or use Force close… after saving" : "Close this app yourself, then check again", x + 90, rowY + 35, width - 134, 20, 9, dialog.ConfirmForceClose || !application.CanRequestClose ? Theme.Orange : Theme.Muted);
        }
        var footerY = y + height - 193;
        if (pages > 1)
        {
            Action("running.previous", "Previous", x + 27, footerY - 6, 100, () => { runningApplicationsPage--; Controller.Notify(); }, enabled: runningApplicationsPage > 0);
            Text($"{runningApplicationsPage + 1} / {pages}  ·  {applications.Count} apps", x + 143, footerY + 8, width - 290, 24, 9, Theme.Muted);
            Action("running.next", "Next", x + width - 127, footerY - 6, 100, () => { runningApplicationsPage++; Controller.Notify(); }, enabled: runningApplicationsPage + 1 < pages);
        }
        else Text($"{applications.Count} LISTED APP{(applications.Count == 1 ? "" : "S")}", x + 29, footerY + 7, width - 58, 23, 8, Theme.Muted, true);

        var message = dialog.ConfirmForceClose ? "Confirm only after saving your work. Other apps and processes will not be forced to close."
            : dialog.InspectionTimedOut ? dialog.Message
            : dialog.Waiting
            ? "Waiting is time limited. Save your work, then check again, explicitly force close eligible listed apps, or cancel."
            : dialog.ManualOnly || dialog.Applications.Any(application => !application.CanRequestClose)
                ? "Close any app that cannot accept a close request yourself. Installation stays paused until all affected apps are closed."
                : dialog.IsRetry
                    ? "An app stayed open or restarted. Save your work, then check again or explicitly choose Force close… for eligible apps."
                    : "Close apps requests a normal exit and preserves save prompts. Force close… requires a separate confirmation.";
        Text(message, x + 29, y + height - 131, width - 58, 49, 10, Theme.Muted, wrap: true);
        var cancelLabel = dialog.Operation.Equals("uninstall", StringComparison.OrdinalIgnoreCase) ? "Cancel uninstall" : dialog.Operation.Equals("repair", StringComparison.OrdinalIgnoreCase) ? "Cancel repair" : "Cancel setup";
        if (dialog.ConfirmForceClose)
        {
            Action("running.cancel", cancelLabel, x + 27, y + height - 65, 207, Controller.CancelRunningApplications);
            Action("running.force.back", "Back", x + 246, y + height - 65, 130, Controller.BackFromForceCloseRunningApplications);
            Action("running.force.confirm", $"Force close {applications.Count} app{(applications.Count == 1 ? "" : "s")}", x + width - 305, y + height - 65, 278, Controller.ForceCloseRunningApplications, true, accent: Theme.Orange);
            return;
        }
        var canClose = !dialog.ManualOnly && dialog.Applications.Any(application => application.CanRequestClose);
        var canForce = !dialog.ManualOnly && dialog.Applications.Any(application => application.CanForceClose);
        var buttonCount = 2 + (canClose ? 1 : 0) + (canForce ? 1 : 0);
        var buttonWidth = (width - 54 - (buttonCount - 1) * 12) / buttonCount;
        var buttonX = x + 27;
        Action("running.cancel", cancelLabel, buttonX, y + height - 65, buttonWidth, Controller.CancelRunningApplications); buttonX += buttonWidth + 12;
        Action("running.check", "Check again", buttonX, y + height - 65, buttonWidth, Controller.CheckRunningApplications, !canClose); buttonX += buttonWidth + 12;
        if (canClose) { Action("running.close", "Close apps", buttonX, y + height - 65, buttonWidth, Controller.CloseRunningApplications, true, !dialog.Waiting); buttonX += buttonWidth + 12; }
        if (canForce) Action("running.force", "Force close…", buttonX, y + height - 65, buttonWidth, Controller.ConfirmForceCloseRunningApplications, accent: Theme.Orange);
    }
}
