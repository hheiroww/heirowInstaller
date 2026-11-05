using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HeirowInstaller.Core;

/// <summary>Recognizes the verified native updater bootstrap while its downloaded host runs as a direct child.</summary>
public static class BootstrapProcessTrust
{
    public const string ParentIdVariable = "SOCKETJACK_BOOTSTRAP_PID";
    public const string ParentStartVariable = "SOCKETJACK_BOOTSTRAP_START";

    public static bool IsTrustedUpdaterProcess(UpdateCompanionConfiguration configuration, string verifiedRuntime)
    {
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFullPath(current).Equals(Path.GetFullPath(verifiedRuntime), InstallerPlatform.PathComparison)) return true;
        if (!OperatingSystem.IsWindows() || configuration.SharedRuntime is not { } binding ||
            !int.TryParse(Environment.GetEnvironmentVariable(ParentIdVariable), out var parentId) || parentId <= 0 || parentId == Environment.ProcessId ||
            !long.TryParse(Environment.GetEnvironmentVariable(ParentStartVariable), out var start) || start <= 0) return false;
        try
        {
            using var self = Process.GetCurrentProcess();
            if (NtQueryInformationProcess(self.Handle, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0 || basic.Parent.ToInt64() != parentId) return false;
            using var parent = Process.GetProcessById(parentId);
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != start || parent.MainModule?.FileName is not { } image ||
                !Path.GetFullPath(image).Equals(Path.GetFullPath(verifiedRuntime), InstallerPlatform.PathComparison)) return false;
            PackageSafety.EnsureNoReparsePoints(image);
            return new FileInfo(image).Length == binding.Size && PackageSafety.HashEquals(PackageSafety.HashFile(image), binding.Sha256);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation
    { public nint Reserved1, Peb, Reserved2, Reserved3, UniqueId, Parent; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int informationClass, out ProcessBasicInformation information, int length, out int returned);
}
