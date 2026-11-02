using System.Diagnostics;

namespace HeirowInstaller.Core;

internal static class UnixStateSecurity
{
    internal static void Validate(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        PackageSafety.EnsureNoReparsePoints(root);
        for (string? current = Path.GetFullPath(root); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current)) continue;
            var mode = File.GetUnixFileMode(current);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new UnauthorizedAccessException("Machine state must have root-owned ancestors without shared write access.");
            var start = new ProcessStartInfo("/usr/bin/stat") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in OperatingSystem.IsMacOS() ? new[] { "-f", "%u", current } : new[] { "-c", "%u", "--", current }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Cannot inspect machine state ownership.");
            var owner = process.StandardOutput.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0 || owner.Trim() != "0") throw new UnauthorizedAccessException("Machine state must be owned by root.");
        }
    }
}
