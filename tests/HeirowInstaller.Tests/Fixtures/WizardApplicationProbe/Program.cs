using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        const string markerText = "heirowInstaller wizard application acceptance v1\n";
        var root = Path.GetDirectoryName(Environment.ProcessPath!)!;
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return 3;
        var marker = Path.Combine(root, ".wizard-application-fixture");
        if (!File.Exists(marker) || File.ReadAllText(marker) != markerText) return 4;
        if (args.Length == 0)
        {
            File.WriteAllText(Path.Combine(root, "opened.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId, image = Environment.ProcessPath, normalLaunch = true }));
            return 0;
        }
        if (args.Length is not (2 or 3) || args[0] != "--run-directory" || !Path.IsPathFullyQualified(args[1]) || args.Length == 3 && args[2] != "--keep-open") return 2;
        var run = Path.GetFullPath(args[1]);
        for (var directory = new DirectoryInfo(run); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return 3;
        if (!File.Exists(Path.Combine(run, ".wizard-application-fixture")) || File.ReadAllText(Path.Combine(run, ".wizard-application-fixture")) != markerText) return 4;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var form = new ProbeForm { Text = "Wizard acceptance app", Width = 450, Height = 170, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-20000, -20000) };
        form.Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(20), Text = "Owned wizard acceptance app. Save confirmation remains available." });
        var keepOpen = args.Length == 3;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        form.Shown += (_, _) => File.WriteAllText(Path.Combine(run, "ready.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId }));
        form.FormClosing += (_, e) =>
        {
            File.WriteAllText(Path.Combine(run, "close-requested.json"), "A graceful close was requested.");
            e.Cancel = keepOpen && !File.Exists(Path.Combine(run, "allow-close")) && DateTimeOffset.UtcNow < deadline;
        };
        timer.Tick += (_, _) => { if (File.Exists(Path.Combine(run, "allow-close")) || DateTimeOffset.UtcNow >= deadline) form.Close(); };
        timer.Start(); Application.Run(form); return 0;
    }

    private sealed class ProbeForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; }
        }
    }
}
