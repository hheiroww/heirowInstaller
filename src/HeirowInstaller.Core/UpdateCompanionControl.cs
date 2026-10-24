using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HeirowInstaller.Core;

/// <summary>Requests that an application's own updater stop before installer maintenance.</summary>
public static class UpdateCompanionControl
{
    public static string GetStopEventName(string installDirectory)
    {
        var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0 || Path.GetDirectoryName(root) is null) throw new ArgumentException("An application directory is required.", nameof(installDirectory));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).ToLowerInvariant();
        return @"Local\heirowUpdater.lifecycle.stop." + hash;
    }

    public static async Task<bool> StopAsync(string installDirectory, string relativeExecutablePath = "heirowUpdater.exe",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = PackageSafety.UnderRoot(installDirectory, relativeExecutablePath);
        if (!OperatingSystem.IsWindows()) return false;
        if (!EventWaitHandle.TryOpenExisting(GetStopEventName(installDirectory), out var signal)) return false;
        using (signal) signal.Set();
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Opening for replacement access does not alter the file. The Windows image
                // section keeps this unavailable until the updater process has really exited.
                if (File.Exists(executable)) using (new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return true;
            }
            catch (IOException) when (timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (IOException error)
            {
                throw new IOException("The application updater did not stop in time for maintenance. Close its update window and retry.", error);
            }
        }
    }
}
