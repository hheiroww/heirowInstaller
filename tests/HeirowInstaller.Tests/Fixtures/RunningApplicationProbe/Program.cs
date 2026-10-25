using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class Program
{
    internal const string Marker = "heirowInstaller running-application acceptance\n";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "--run-directory" || !Path.IsPathFullyQualified(args[1])) return 2;
        var keepOpen = false;
        var nonactivating = false;
        string? module = null;
        for (var i = 2; i < args.Length; i++)
            if (args[i] == "--keep-open" && !keepOpen) keepOpen = true;
            else if (args[i] == "--nonactivating" && !nonactivating) nonactivating = true;
            else if (args[i] == "--module" && module is null && ++i < args.Length) module = args[i];
            else return 2;
        var directory = Path.GetFullPath(args[1]);
        var marker = Path.Combine(directory, ".running-application-probe");
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return 3;
        if (!File.Exists(marker) || new FileInfo(marker).Length != Marker.Length || File.ReadAllText(marker) != Marker) return 4;
        IntPtr moduleHandle = IntPtr.Zero;
        if (module is not null)
        {
            if (!Path.IsPathFullyQualified(module)) return 5;
            module = Path.GetFullPath(module);
            for (var parent = new FileInfo(module).Directory; parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return 5;
            // The only loadable test plugin is a byte-identical copy of this freshly compiled, harmless probe assembly.
            var ownAssembly = typeof(Program).Assembly.Location;
            if (!File.Exists(module) || (File.GetAttributes(module) & FileAttributes.ReparsePoint) != 0 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(module)), SHA256.HashData(File.ReadAllBytes(ownAssembly)))) return 5;
            moduleHandle = NativeLibrary.Load(module);
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var form = new ProbeForm(nonactivating) { Text = "Desktop acceptance app", Width = 460, Height = 190, StartPosition = nonactivating ? FormStartPosition.Manual : FormStartPosition.CenterScreen };
        if (nonactivating) form.Location = new System.Drawing.Point(-20000, -20000);
        var label = new Label { Dock = DockStyle.Fill, Padding = new Padding(22), Text = keepOpen ? "This isolated app keeps its save dialog open until the test allows it to close." : "This isolated app accepts a normal close request.", AutoSize = false };
        form.Controls.Add(label);
        var requests = 0;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        using var current = Process.GetCurrentProcess();
        void Report(string file, object value) => File.WriteAllText(Path.Combine(directory, file), JsonSerializer.Serialize(value));
        form.Shown += (_, _) => Report("ready.json", new { processId = Environment.ProcessId, startTimeUtcTicks = current.StartTime.ToUniversalTime().Ticks, executablePath = Environment.ProcessPath, keepOpen });
        form.FormClosing += (_, eventArgs) =>
        {
            requests++;
            Report("close-requests.json", new { processId = Environment.ProcessId, requests, keepOpen, at = DateTimeOffset.UtcNow });
            if (keepOpen && !File.Exists(Path.Combine(directory, "allow-close")) && DateTimeOffset.UtcNow < deadline)
            {
                eventArgs.Cancel = true;
                label.Text = "Save confirmation is still open. The installer must wait or allow cancellation.";
            }
        };
        timer.Tick += (_, _) =>
        {
            if (File.Exists(Path.Combine(directory, "allow-close")) || DateTimeOffset.UtcNow >= deadline) form.Close();
        };
        timer.Start();
        Application.Run(form);
        if (moduleHandle != IntPtr.Zero) NativeLibrary.Free(moduleHandle);
        Report("exited.json", new { processId = Environment.ProcessId, requests, at = DateTimeOffset.UtcNow });
        return 0;
    }

    private sealed class ProbeForm(bool nonactivating) : Form
    {
        protected override bool ShowWithoutActivation => nonactivating;
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; if (nonactivating) parameters.ExStyle |= 0x08000000; return parameters; }
        }
    }
}
