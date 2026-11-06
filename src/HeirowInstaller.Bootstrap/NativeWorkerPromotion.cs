using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HeirowInstaller.Bootstrap;

internal static class NativeWorkerPromotion
{
    internal static int Run(string[] args, BootstrapDescriptor descriptor)
    {
        if (args.Length != 5 || descriptor.HostKind != "installer" || !Program.IsAdministrator() ||
            args[1].Length is < 1 or > 160 || args[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-') ||
            !int.TryParse(args[2], out var parentId) || parentId <= 0 || !long.TryParse(args[3], out var parentStart) || parentStart <= 0 ||
            args[4].Length != 64 || !args[4].All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid protected worker handoff.");
        using var window = new BootstrapWindow(descriptor.Name + " · Administrator access");
        return window.Run(async token =>
        {
            var root = BootstrapEngine.SharedRoot("machine"); BootstrapMachineStore.Prepare(root);
            var staging = Path.Combine(root, "staging", Guid.NewGuid().ToString("N"));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            // The original medium-integrity window owns application downloads and account credentials.
            // This process provisions only descriptor-authorized public host/runtime packages.
            descriptor.ApplicationPackage = null;
            var engine = new BootstrapEngine(descriptor, http, window);
            var start = await engine.PrepareAsync(root, staging, token);
            foreach (var package in descriptor.Packages) BootstrapMachineStore.ValidateTree(engine.PackageRoot(root, package), requireProtectedDacl: false);
            var original = ReadOriginalWindow(parentId);
            if (original.Start != parentStart || !Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original.Image, token))).Equals(
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(start.FileName, token))), StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("The original installation window changed during administrator approval.");
            var pinnedDotnet = start.Environment.TryGetValue("DOTNET_ROOT", out var runtime) ? runtime : null;
            start.Environment.Clear();
            var windows = Path.GetDirectoryName(Environment.SystemDirectory)!;
            var temporary = Path.Combine(staging, "temp"); Directory.CreateDirectory(temporary);
            start.Environment["SystemRoot"] = windows; start.Environment["WINDIR"] = windows;
            start.Environment["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd(Path.DirectorySeparatorChar);
            start.Environment["ComSpec"] = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Environment["PATH"] = Environment.SystemDirectory + ";" + windows;
            start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary;
            start.Environment["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            start.Environment["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            start.Environment["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            start.Environment["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (pinnedDotnet is not null)
            { start.Environment["DOTNET_ROOT"] = pinnedDotnet; start.Environment["DOTNET_ROOT_X64"] = pinnedDotnet; start.Environment["DOTNET_ROOT_ARM64"] = pinnedDotnet; }
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"; start.Environment["DOTNET_EnableDiagnostics"] = "0";
            var promoter = NativeProcessIdentity.Current();
            start.Environment["SOCKETJACK_BOOTSTRAP_LAUNCHER"] = promoter.Image;
            start.Environment["SOCKETJACK_BOOTSTRAP_PROMOTER_PID"] = promoter.Id.ToString(CultureInfo.InvariantCulture);
            start.Environment["SOCKETJACK_BOOTSTRAP_PROMOTER_START"] = promoter.StartTicks.ToString(CultureInfo.InvariantCulture);
            start.ArgumentList.Clear();
            foreach (var argument in new[] { "--install-worker", args[1], args[2], args[3], args[4] }) start.ArgumentList.Add(argument);
            start.CreateNoWindow = true;
            using var worker = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the protected installation worker.");
            window.Hide(); await worker.WaitForExitAsync(CancellationToken.None); return worker.ExitCode;
        });
    }
    private static (string Image, long Start) ReadOriginalWindow(int id)
    {
        using var process = OpenProcess(0x1000, false, id);
        if (process.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var image = new StringBuilder(32768); var size = image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref size) || !GetProcessTimes(process, out var created, out _, out _, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        BootstrapEngine.NoLinks(image.ToString());
        return (image.ToString(), DateTime.FromFileTimeUtc(created).Ticks);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
