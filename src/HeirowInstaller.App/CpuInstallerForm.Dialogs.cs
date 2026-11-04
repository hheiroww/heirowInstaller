#if WINDOWS
using System.Diagnostics;
using HeirowInstaller.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace HeirowInstaller.App;

internal sealed partial class CpuInstallerForm
{
    private void AddLegalButtons(Forms.TableLayoutPanel page)
    {
        if (Controller.Manifest?.LegalDocuments is not { } legal) return;
        AddRow(page, Paragraph("Publisher-provided privacy and terms are included in this verified package."));
        var buttons = new Forms.FlowLayoutPanel { AutoSize = true, WrapContents = true };
        buttons.Controls.Add(InlineButton("wizard.legal.privacy", "Privacy Policy", () => ShowDocument(CreateLegalDialogForTests(privacy: true))));
        buttons.Controls.Add(InlineButton("wizard.legal.terms", "Terms and Conditions", () => ShowDocument(CreateLegalDialogForTests(privacy: false))));
        AddRow(page, buttons);
    }

    internal Forms.Form CreateLicenseDialogForTests() => CreateDocumentDialog(
        "License agreement", Controller.LicenseText ?? "This package does not include a license agreement.", null);

    internal Forms.Form CreateLegalDialogForTests(bool privacy)
    {
        var legal = Controller.Manifest?.LegalDocuments;
        var document = privacy ? legal?.PrivacyPolicy : legal?.TermsAndConditions;
        return CreateDocumentDialog(privacy ? "Privacy Policy" : "Terms and Conditions",
            document?.Content ?? "This package does not include this document.", document?.Url);
    }

    private Forms.Form CreateDocumentDialog(string title, string text, string? url)
    {
        var dialog = new Forms.Form
        {
            Text = title, Name = "cpu.document", ClientSize = new Drawing.Size(720, 540),
            MinimumSize = new Drawing.Size(480, 340), StartPosition = Forms.FormStartPosition.CenterParent,
            ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = true, Font = Font,
            BackColor = Drawing.SystemColors.Window
        };
        var close = new Forms.Button { Name = "document.close", Text = "Close", Dock = Forms.DockStyle.Right, Width = 100, DialogResult = Forms.DialogResult.Cancel };
        var footerPanel = new Forms.Panel { Dock = Forms.DockStyle.Bottom, Height = 50, Padding = new Forms.Padding(12, 8, 12, 8) };
        footerPanel.Controls.Add(close);
        var content = new Forms.TextBox { Name = "document.content", Dock = Forms.DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = Forms.ScrollBars.Vertical, Text = text, WordWrap = true, BackColor = Drawing.SystemColors.Window };
        var label = new Forms.Label { Text = title + " · " + (Controller.Manifest?.Publisher ?? "Publisher"), Dock = Forms.DockStyle.Top, Height = 42, Padding = new Forms.Padding(12), AutoEllipsis = true };
        dialog.Controls.Add(content);
        if (!string.IsNullOrWhiteSpace(url))
        {
            var link = new Forms.LinkLabel { Name = "document.url", Text = url, Dock = Forms.DockStyle.Bottom, Height = 36, Padding = new Forms.Padding(12, 8, 12, 4), AutoEllipsis = true };
            link.LinkClicked += (_, _) =>
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var target) && target.Scheme == "https" && target.UserInfo.Length == 0)
                {
                    try { Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }); }
                    catch (Exception error) { status.Text = "Could not open document URL: " + error.Message; }
                }
            };
            dialog.Controls.Add(link);
        }
        dialog.Controls.Add(label);
        dialog.Controls.Add(footerPanel);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        return dialog;
    }

    private void ShowDocument(Forms.Form dialog)
    {
        using (dialog) { if (!closePending && !IsDisposed) dialog.ShowDialog(this); }
    }

    private void BuildRunningApplications(Forms.TableLayoutPanel page)
    {
        var dialog = Controller.RunningApplicationsDialog!;
        var rows = dialog.ConfirmForceClose ? dialog.Applications.Where(item => item.CanForceClose).ToArray() : dialog.Applications;
        AddRow(page, Paragraph(dialog.ConfirmForceClose ? "Confirm force close of the listed apps" :
            dialog.InspectionTimedOut ? "Application check took too long" : dialog.Waiting ? "Finish saving your work" : "Close apps to continue", true));
        AddRow(page, Paragraph(dialog.ProductName));
        AddRow(page, Paragraph(dialog.ConfirmForceClose
            ? "Unsaved work in the listed apps will be lost. Confirm only after saving your work. Other apps will not be forced to close."
            : dialog.InspectionTimedOut ? dialog.Message
            : dialog.ManualOnly ? "Close this installer dependency yourself, then check again."
            : "Save your work in these applications. Close apps requests a normal exit and preserves save prompts. Force close requires a separate confirmation."));
        var list = Remember("running.apps", new Forms.ListView { View = Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, HeaderStyle = Forms.ColumnHeaderStyle.Nonclickable });
        list.Columns.Add("Application", 190);
        list.Columns.Add("PID", 65);
        list.Columns.Add("Executable", 400);
        foreach (var app in rows)
        {
            var item = new Forms.ListViewItem(app.DisplayName);
            item.SubItems.Add(app.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            item.SubItems.Add(app.ExecutablePath);
            item.ToolTipText = app.AffectedPath;
            list.Items.Add(item);
        }
        AddRow(page, list, 170);
        if (!string.IsNullOrWhiteSpace(dialog.Message)) AddRow(page, Paragraph(dialog.Message));
        AddRow(page, Paragraph(dialog.ManualOnly || !dialog.Applications.Any(item => item.CanForceClose)
            ? "This attempt is time limited. Check again or cancel if an app stays open."
            : "Installation stays paused until affected apps close. You can check again, explicitly force close eligible listed apps, or cancel."));

        if (dialog.ConfirmForceClose)
        {
            ActionButton("running.force.confirm", "Force close listed apps", Controller.ForceCloseRunningApplications, primary: true);
            ActionButton("running.force.back", "Back", Controller.BackFromForceCloseRunningApplications);
        }
        else
        {
            var canClose = !dialog.ManualOnly && dialog.Applications.Any(item => item.CanRequestClose);
            var canForce = !dialog.ManualOnly && dialog.Applications.Any(item => item.CanForceClose);
            if (canForce) ActionButton("running.force", "Force close…", Controller.ConfirmForceCloseRunningApplications);
            if (canClose) ActionButton("running.close", "Close apps", Controller.CloseRunningApplications, !dialog.Waiting, primary: true);
            ActionButton("running.check", "Check again", Controller.CheckRunningApplications, primary: !canClose);
        }
        ActionButton("running.cancel", "Cancel setup", RequestClose);
    }
}
#endif
