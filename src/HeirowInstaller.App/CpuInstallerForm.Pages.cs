#if WINDOWS
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace HeirowInstaller.App;

internal sealed partial class CpuInstallerForm
{
    private void RenderState()
    {
        if (controllerDisposed || IsDisposed) return;
        body.SuspendLayout();
        footer.SuspendLayout();
        try
        {
            AcceptButton = null;
            CancelButton = null;
            ClearOwnedControls(body);
            ClearOwnedControls(footer);
            controls.Clear();
            var manifest = Controller.Manifest;
            heading.Text = Controller.SetupProductName is { } name ? name + " Setup" : arguments.InitialSetupTitle ?? "heirowInstaller";
            subtitle.Text = "Windows compatibility interface · CPU rendering";
            if (Controller.SetupProductVersion is { } version) subtitle.Text += " · " + version;
            if (manifest is not null) subtitle.Text += " · " + manifest.Publisher;
            var page = NewPage();
            if (Controller.RunningApplicationsDialog is not null) BuildRunningApplications(page);
            else if (manifest is null) BuildPreparingOrHub(page);
            else if (Controller.Stage == SetupStage.Progress) BuildProgressPage(page);
            else if (Controller.Stage == SetupStage.Complete) BuildCompletePage(page);
            else if (Controller.IsMaintenanceWizard) BuildMaintenancePage(page);
            else
            {
                switch (Controller.Stage)
                {
                    case SetupStage.Package: BuildPackagePage(page); break;
                    case SetupStage.Features: BuildFeaturesPage(page); break;
                    case SetupStage.Destination: BuildDestinationPage(page); break;
                    case SetupStage.Review: BuildReviewPage(page); break;
                }
                BuildNavigation();
            }
            RefreshProgress();
        }
        finally { footer.ResumeLayout(true); body.ResumeLayout(true); }
    }

    private void BuildPreparingOrHub(Forms.TableLayoutPanel page)
    {
        AddRow(page, Paragraph(Controller.Busy ? "Checking the application and its installation files." : Controller.Failed ? "Setup needs attention" : "Install an application", true));
        AddRow(page, Paragraph(Controller.Failed || Controller.Busy ? Controller.StatusDetail : "Open a .hinst package or a published setup executable. You can review its license, selected features and destination before choosing Install."));
        if (!Controller.Busy && !Controller.IsMaintenanceWizard)
            ActionButton("setup.open", "Open installer…", BrowsePackage, primary: true);
        ActionButton("wizard.cancel", Controller.Busy ? "Cancel setup" : "Close", RequestClose);
    }

    private void BuildPackagePage(Forms.TableLayoutPanel page)
    {
        var app = Controller.Manifest!;
        AddRow(page, Paragraph("Welcome to " + Controller.SetupProductName, true));
        if (!string.IsNullOrWhiteSpace(app.Description)) AddRow(page, Paragraph(app.Description));
        AddRow(page, Paragraph("Choose the features and installation location, then review the planned changes. Installation begins when you choose Install application."));
        if (Controller.Plan is { } plan)
            AddRow(page, Paragraph($"{plan.Files.Count:N0} selected files · {plan.TotalBytes:N0} bytes"));
        if (Controller.LicenseText is not null)
        {
            AddRow(page, InlineButton("setup.license", "Read license agreement", () => ShowDocument(CreateLicenseDialogForTests())));
            var accepted = Remember("setup.license.accept", new Forms.CheckBox
            {
                AutoSize = true, Text = "I accept the publisher's license agreement", Checked = Controller.LicenseAccepted, Enabled = !Controller.Busy
            });
            accepted.CheckedChanged += (_, _) => { Controller.LicenseAccepted = accepted.Checked; ScheduleRefresh(); };
            AddRow(page, accepted);
        }
        AddLegalButtons(page);
    }

    private void BuildFeaturesPage(Forms.TableLayoutPanel page)
    {
        AddRow(page, Paragraph("Choose application components", true));
        AddRow(page, Paragraph("Required components remain selected. Choose any optional features below."));
        var features = Controller.Manifest!.Features;
        if (features.Count == 0) AddRow(page, Paragraph("This package includes the complete application."));
        foreach (var feature in features)
        {
            var selected = feature.Required || Controller.Features.Contains(feature.Id);
            var check = Remember("feature." + feature.Id, new Forms.CheckBox
            {
                AutoSize = true, Text = feature.Name + (feature.Required ? " (required)" : ""), Checked = selected, Enabled = !feature.Required && !Controller.Busy
            });
            check.CheckedChanged += (_, _) =>
            {
                if (feature.Required) return;
                if (check.Checked) Controller.Features.Add(feature.Id); else Controller.Features.Remove(feature.Id);
            };
            AddRow(page, check);
            if (!string.IsNullOrWhiteSpace(feature.Description)) AddRow(page, Paragraph(feature.Description));
        }
    }

    private void BuildDestinationPage(Forms.TableLayoutPanel page)
    {
        AddRow(page, Paragraph("Choose where to install", true));
        AddRow(page, Paragraph(Controller.RequiresMachineScope
            ? "The selected components require installation for everyone on this computer. Windows may request administrator approval."
            : "Install for your account or for everyone on this computer. Machine-wide changes may request administrator approval."));
        var scopePanel = new Forms.FlowLayoutPanel { AutoSize = true, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false };
        foreach (var scope in new[] { "user", "machine" })
        {
            var radio = Remember("setup.scope." + scope, new Forms.RadioButton
            {
                AutoSize = true, Text = scope == "user" ? "Just for me" : "Everyone on this computer", Checked = Controller.Scope == scope,
                Enabled = !Controller.Busy && (scope != "user" || !Controller.RequiresMachineScope)
            });
            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked || Controller.Scope == scope || Controller.Busy || scope == "user" && Controller.RequiresMachineScope) return;
                _ = RunCommandAsync(() => Controller.SelectScopeAsync(scope));
            };
            scopePanel.Controls.Add(radio);
        }
        AddRow(page, scopePanel);
        AddRow(page, Paragraph("Installation folder", true));
        var target = Remember("setup.target", new Forms.TextBox
        {
            Text = Controller.DisplayInstallDirectory, ReadOnly = Controller.OriginalInstallerDestination, Enabled = !Controller.Busy
        });
        target.TextChanged += (_, _) => { if (!target.ReadOnly) Controller.TargetDirectory = target.Text; };
        AddRow(page, target, 42);
        AddRow(page, InlineButton("setup.browseTarget", "Browse folder…", () =>
        {
            using var picker = new Forms.FolderBrowserDialog { Description = "Choose installation folder", UseDescriptionForTitle = true };
            if (Directory.Exists(Controller.TargetDirectory)) picker.SelectedPath = Controller.TargetDirectory;
            if (picker.ShowDialog(this) == Forms.DialogResult.OK)
            {
                Controller.TargetDirectory = picker.SelectedPath;
                target.Text = picker.SelectedPath;
            }
        }, enabled: !Controller.Busy && !Controller.OriginalInstallerDestination));
        if (Controller.OriginalInstallerDestination)
            AddRow(page, Paragraph("This package delegates its destination to the original installer."));
        if (Controller.Failed)
        {
            var error = Remember("setup.destination.error", Paragraph(Controller.StatusDetail));
            error.ForeColor = Drawing.Color.Firebrick;
            AddRow(page, error);
        }
    }

    private void BuildReviewPage(Forms.TableLayoutPanel page)
    {
        AddRow(page, Paragraph("Ready to install " + Controller.SetupProductName, true));
        AddRow(page, Paragraph("Changes begin only when you choose Install application."));
        AddRow(page, Paragraph("Location: " + Controller.DisplayInstallDirectory));
        AddRow(page, Paragraph(Controller.Scope == "machine" ? "Scope: everyone on this computer" : "Scope: current user"));
        if (Controller.Plan is { } plan)
        {
            AddRow(page, Paragraph(plan.RequiresElevation ? "Windows will request administrator approval for the planned changes." : "The current installation plan does not require elevation."));
            AddRow(page, ReadOnlyText("setup.operations", string.Join(Environment.NewLine, plan.Operations)), 185);
        }
        AddLegalButtons(page);
        if (Controller.LicenseText is not null) AddRow(page, InlineButton("setup.license", "Read license agreement", () => ShowDocument(CreateLicenseDialogForTests())));
    }

    private void BuildNavigation()
    {
        var stage = Controller.Stage;
        var licensed = Controller.LicenseText is null || Controller.LicenseAccepted;
        if (stage == SetupStage.Review)
            ActionButton("setup.next", "Install application", () => _ = RunCommandAsync(Controller.InstallAsync), !Controller.Busy && licensed && Controller.Plan is not null, primary: true);
        else
            ActionButton("setup.next", "Continue", () => _ = RunCommandAsync(Controller.NextStageAsync), !Controller.Busy && licensed, primary: true);
        if (stage > SetupStage.Package) ActionButton("setup.back", "Back", Controller.PreviousStage, !Controller.Busy);
        ActionButton("wizard.cancel", "Cancel", RequestClose);
    }

    private void BuildMaintenancePage(Forms.TableLayoutPanel page)
    {
        var repair = Controller.MaintenanceOperation == "repair";
        AddRow(page, Paragraph((repair ? "Repair " : "Uninstall ") + Controller.SetupProductName, true));
        AddRow(page, Paragraph(repair ? "Repair verifies and restores this application's installed files and configured components." : "Uninstall removes this application and its recorded installation changes."));
        AddRow(page, ReadOnlyText("maintenance.location", Controller.DisplayInstallDirectory), 58);
        AddRow(page, Paragraph("Save your work before continuing. Setup asks before closing any application using these files."));
        AddLegalButtons(page);
        ActionButton("maintenance.start", repair ? "Repair application" : "Uninstall application",
            () => _ = RunCommandAsync(repair ? Controller.RepairAsync : Controller.UninstallAsync), !Controller.Busy, primary: true);
        ActionButton("wizard.cancel", "Cancel", RequestClose);
    }

    private void BuildProgressPage(Forms.TableLayoutPanel page)
    {
        AddRow(page, Remember("setup.progress.status", Paragraph(Controller.Status, true)));
        AddRow(page, Remember("setup.progress.detail", Paragraph(Controller.StatusDetail)));
        AddRow(page, ReadOnlyText("setup.activity", ActivityText()), 205);
        if (Controller.Busy) ActionButton("wizard.cancel", "Cancel safely", RequestClose);
        else
        {
            ActionButton("wizard.retry", "Review and retry", () => { Controller.Stage = SetupStage.Review; Controller.Notify(); }, primary: true);
            ActionButton("wizard.cancel", "Close", RequestClose);
        }
    }

    private void BuildCompletePage(Forms.TableLayoutPanel page)
    {
        var removed = Controller.MaintenanceOperation == "uninstall";
        AddRow(page, Paragraph(Controller.SetupProductName + (removed ? " was removed." : " is ready."), true));
        AddRow(page, Paragraph(Controller.LastInstallation?.RebootRequired == true ? "Restart Windows to finish applying all changes." : "Choose Finish to close setup."));
        if (Controller.CanOpenInstalledApplication)
        {
            var launch = Remember("wizard.open", new Forms.CheckBox { AutoSize = true, Text = "Open " + Controller.SetupProductName + " after setup", Checked = Controller.OpenApplicationOnFinish });
            launch.CheckedChanged += (_, _) => Controller.OpenApplicationOnFinish = launch.Checked;
            AddRow(page, launch);
        }
        AddRow(page, ReadOnlyText("setup.activity", ActivityText()), 150);
        ActionButton("wizard.finish", "Finish", () => _ = RunCommandAsync(Controller.FinishAsync), !Controller.Busy, primary: true);
    }

    private string ActivityText() => string.Join(Environment.NewLine, Controller.Activity.Select(item => $"{item.Time:HH:mm:ss}  {item.Title}: {item.Detail}"));

    private Forms.TextBox ReadOnlyText(string id, string text) => Remember(id, new Forms.TextBox
    {
        Text = text, ReadOnly = true, Multiline = true, ScrollBars = Forms.ScrollBars.Vertical, WordWrap = true, BackColor = Drawing.SystemColors.Window
    });

    private Forms.Button InlineButton(string id, string text, Action action, bool enabled = true)
    {
        var button = Remember(id, new Forms.Button { Text = text, AutoSize = true, MinimumSize = new Drawing.Size(115, 32), Enabled = enabled && !closePending, UseVisualStyleBackColor = true });
        button.Click += (_, _) => action();
        return button;
    }

    private void BrowsePackage()
    {
        using var picker = new Forms.OpenFileDialog { Title = "Open application installer", Filter = "Installer packages|*.hinst;*.exe|All files|*.*", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) == Forms.DialogResult.OK) _ = RunCommandAsync(() => OpenPackageAsync(picker.FileName));
    }
}
#endif
