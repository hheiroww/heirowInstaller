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
        Box(x, y, width, height, Theme.Surface, 16);
        Box(x + 27, y + 27, 46, 46, Theme.GreenSurface, 12, Color.Transparent);
        Text("Ⅱ", x + 40, y + 36, 28, 29, 17, Theme.Lime, true);
        Text(dialog.Waiting ? "Finish saving your work" : dialog.IsRetry ? "An app is still open" : "Close apps to continue", x + 91, y + 27, width - 118, 42, 23, bold: true);
        Text(dialog.ProductName, x + 92, y + 73, width - 120, 27, 11, Theme.Lime, true);
        Text(dialog.ManualOnly ? "Close this installer dependency yourself, then check again." : "These apps are using files that need to change before installation can continue.",
            x + 29, y + 119, width - 58, 49, 11, Theme.Muted, wrap: true);

        const int rows = 3;
        var pages = Math.Max(1, (dialog.Applications.Count + rows - 1) / rows);
        runningApplicationsPage = Math.Clamp(runningApplicationsPage, 0, pages - 1);
        var listY = y + 181;
        foreach (var (application, index) in dialog.Applications.Skip(runningApplicationsPage * rows).Take(rows).Select((application, index) => (application, index)))
        {
            var rowY = listY + index * 68;
            Box(x + 27, rowY, width - 54, 60, Theme.Field, 9);
            Box(x + 40, rowY + 12, 35, 35, Theme.GreenSurface, 8, Color.Transparent);
            Text(application.DisplayName.Length == 0 ? "A" : application.DisplayName[..1].ToUpperInvariant(), x + 51, rowY + 18, 27, 27, 12, Theme.Lime, true);
            Text(application.DisplayName, x + 89, rowY + 8, width - 133, 25, 11, bold: true);
            Text(application.CanRequestClose ? "Save your work before continuing" : "Close this app yourself, then check again", x + 90, rowY + 35, width - 134, 20, 9, application.CanRequestClose ? Theme.Muted : Theme.Orange);
        }
        var footerY = y + height - 193;
        if (pages > 1)
        {
            Action("running.previous", "Previous", x + 27, footerY - 6, 100, () => { runningApplicationsPage--; Controller.Notify(); }, enabled: runningApplicationsPage > 0);
            Text($"{runningApplicationsPage + 1} / {pages}  ·  {dialog.Applications.Count} apps", x + 143, footerY + 8, width - 290, 24, 9, Theme.Muted);
            Action("running.next", "Next", x + width - 127, footerY - 6, 100, () => { runningApplicationsPage++; Controller.Notify(); }, enabled: runningApplicationsPage + 1 < pages);
        }
        else Text($"{dialog.Applications.Count} APP{(dialog.Applications.Count == 1 ? "" : "S")} TO CLOSE", x + 29, footerY + 7, width - 58, 23, 8, Theme.Muted, true);

        var message = dialog.Waiting
            ? "Save your work in any app asking for confirmation. If an app stays open, you can try again or cancel."
            : dialog.ManualOnly || dialog.Applications.Any(application => !application.CanRequestClose)
                ? "Close any app that cannot accept a close request yourself. Installation stays paused until all affected apps are closed."
                : dialog.IsRetry
                    ? "An app stayed open or restarted. Save your work, then try again. Apps are never forced to close."
                    : "We'll ask these apps to close normally. Save dialogs stay open so you can save your work. Apps are never forced to close.";
        Text(message, x + 29, y + height - 131, width - 58, 49, 10, Theme.Muted, wrap: true);
        var cancelLabel = dialog.Operation.Equals("uninstall", StringComparison.OrdinalIgnoreCase) ? "Cancel uninstall" : dialog.Operation.Equals("repair", StringComparison.OrdinalIgnoreCase) ? "Cancel repair" : "Cancel installation";
        Action("running.cancel", cancelLabel, x + 27, y + height - 65, 207, Controller.CancelRunningApplications);
        var canClose = !dialog.ManualOnly && dialog.Applications.Any(application => application.CanRequestClose);
        Action("running.close", dialog.Waiting ? "Waiting for apps…" : canClose ? "Close apps and continue" : "Check again",
            x + width - 305, y + height - 65, 278, Controller.CloseRunningApplications, true, !dialog.Waiting);
    }
}
