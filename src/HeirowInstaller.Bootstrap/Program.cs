using System.Diagnostics;
using System.Security.Principal;

namespace HeirowInstaller.Bootstrap;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 50;
        try
        {
            var descriptor = BootstrapEngine.ReadEmbeddedDescriptor(); BootstrapEngine.Validate(descriptor);
            // No blanket requireAdministrator manifest: per-user setup must open without UAC.
            if (descriptor.HostKind == "installer" && (descriptor.Scope == "machine" || descriptor.RequiresAdministrator) && !IsAdministrator())
            {
                if (BootstrapWindow.MessageBoxW(0, "This setup needs administrator access. Continue to restart setup with administrator rights?", descriptor.Name + " Setup", 0x24) != 6) return 1223;
                var elevate = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (var argument in args) elevate.ArgumentList.Add(argument);
                try { using var elevated = Process.Start(elevate) ?? throw new InvalidOperationException("Windows did not start setup."); elevated.WaitForExit(); return elevated.ExitCode; }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { return 1223; }
            }
            using var window = new BootstrapWindow(descriptor.HostKind == "application" ? descriptor.Name : descriptor.Name + " Setup");
            return window.Run(async token =>
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
                var engine = new BootstrapEngine(descriptor, http, window);
                var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "staging", Guid.NewGuid().ToString("N"));
                var start = await engine.PrepareAsync(BootstrapEngine.SharedRoot(descriptor.Scope), staging, token);
                start.CreateNoWindow = true;
                if (descriptor.HostKind == "updater")
                {
                    using var parent = Process.GetCurrentProcess();
                    start.Environment["SOCKETJACK_BOOTSTRAP_PID"] = parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    start.Environment["SOCKETJACK_BOOTSTRAP_START"] = parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                foreach (var argument in args) start.ArgumentList.Add(argument);
                window.Report(new(descriptor.HostKind == "application" ? "Opening application" : "Opening setup", "Shared files are ready."));
                using var child = Process.Start(start) ?? throw new InvalidOperationException("The verified installer did not start.");
                window.Hide(); await child.WaitForExitAsync(CancellationToken.None); return child.ExitCode;
            });
        }
        catch (Exception error) { BootstrapWindow.MessageBoxW(0, error.Message, "heirowInstaller", 0x10); return 1; }
    }
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
