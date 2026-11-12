using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace HeirowInstaller.Bootstrap;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 50;
        if (!SetDefaultDllDirectories(0x800)) return 1; // Native bootstrap imports come only from Windows' system directory.
        var title = "heirowInstaller";
        try
        {
            var descriptor = BootstrapEngine.ReadEmbeddedDescriptor(); BootstrapEngine.Validate(descriptor);
            title = descriptor.HostKind switch
            {
                "application" => descriptor.Name,
                "updater" => descriptor.Name + " Update",
                _ => descriptor.Name + " Setup"
            };
            if (args.FirstOrDefault() == "--bootstrap-worker") return NativeWorkerPromotion.Run(args, descriptor);
            if (args.FirstOrDefault() == "--bootstrap-application") return NativeApplicationPromotion.Run(args, descriptor);
            using var window = new BootstrapWindow(title);
            return window.Run(async token =>
            {
                if (descriptor.HostKind == "application" && IsAdministrator())
                    return await NativeApplicationPromotion.RunCurrentAsync(descriptor, args, window, token);
                if (descriptor.HostKind == "application" && descriptor.RequiresAdministrator)
                    return await NativeApplicationPromotion.RequestAsync(descriptor, args, window, token);
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
                var engine = new BootstrapEngine(descriptor, http, window);
                var protectedUpdater = descriptor.HostKind == "updater" && IsAdministrator();
                var staging = protectedUpdater ? Path.Combine(BootstrapEngine.SharedRoot("machine"), "host-temp")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "heirowInstaller", "staging", Guid.NewGuid().ToString("N"));
                var hostScope = descriptor.HostKind == "installer" ? "user" : descriptor.Scope;
                ProcessStartInfo start;
                if (protectedUpdater)
                {
                    var root = BootstrapEngine.SharedRoot("machine"); BootstrapMachineStore.Prepare(root);
                    foreach (var package in descriptor.Packages)
                    {
                        var packageRoot = engine.PackageRoot(root, package);
                        BootstrapMachineStore.ValidateNamespace(root, packageRoot);
                        BootstrapMachineStore.ValidateTree(packageRoot, requireProtectedDacl: false);
                    }
                    start = await engine.PrepareAsync(root, staging, token, allowProvision: false);
                    BootstrapMachineStore.ValidateNamespace(root, staging);
                    Directory.CreateDirectory(staging); BootstrapMachineStore.ValidateNamespace(root, staging);
                    ApplicationPromotionPayloads.ConfigureEnvironment(start, staging,
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), start.WorkingDirectory);
                }
                else if (descriptor.HostKind is "updater" or "application" || descriptor.OfflineOnly)
                    start = await engine.PrepareOfflineAsync([BootstrapEngine.SharedRoot(hostScope), BootstrapEngine.SharedRoot(hostScope == "user" ? "machine" : "user")], staging, token);
                else start = await engine.PrepareAsync(BootstrapEngine.SharedRoot(hostScope), staging, token);
                start.CreateNoWindow = true;
                if (descriptor.HostKind is "updater" or "installer")
                {
                    var parent = NativeProcessIdentity.Current();
                    start.Environment["SOCKETJACK_BOOTSTRAP_PID"] = parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    start.Environment["SOCKETJACK_BOOTSTRAP_START"] = parent.StartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    start.Environment["SOCKETJACK_BOOTSTRAP_LAUNCHER"] = parent.Image;
                }
                engine.AppendStartupArguments(start, args);
                window.Report(new(descriptor.HostKind == "application" ? "Opening application" : "Opening setup", "Shared files are ready."));
                var readyName = @"Local\SocketJack.BootstrapReady." + Guid.NewGuid().ToString("N");
                using var ready = descriptor.HostKind is "installer" or "updater"
                    ? new EventWaitHandle(false, EventResetMode.ManualReset, readyName) : null;
                if (ready is not null)
                {
                    // Event names are only a first-frame UX notification. They do
                    // not authorize installation, elevation, or process closure.
                    start.Environment["SOCKETJACK_BOOTSTRAP_READY"] = readyName;
                }
                Process child;
                token.ThrowIfCancellationRequested();
                try { child = Process.Start(start) ?? throw new InvalidOperationException("The verified application host did not start."); }
                catch (System.ComponentModel.Win32Exception error) when (descriptor.HostKind == "application" && error.NativeErrorCode == 740)
                { return await NativeApplicationPromotion.RequestAsync(descriptor, args, window, token); }
                using var childLease = child;
                var opening = Stopwatch.StartNew();
                while (ready is not null && !ready.WaitOne(0) && !child.HasExited && !token.IsCancellationRequested && opening.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(25, CancellationToken.None);
                window.Hide(); await child.WaitForExitAsync(CancellationToken.None); return child.ExitCode;
            });
        }
        catch (Exception error) { BootstrapWindow.MessageBoxW(0, error.Message, title, 0x10); return 1; }
    }
    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetDefaultDllDirectories(uint flags);
}
