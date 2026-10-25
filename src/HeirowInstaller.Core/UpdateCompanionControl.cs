using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HeirowInstaller.Core;

/// <summary>Requests that an application's own updater stop before installer maintenance.</summary>
public static class UpdateCompanionControl
{
    public static async Task<bool> StopAsync(UpdateCompanionConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return false;
        EventWaitHandle? signal = null;
        var directory = SharedUpdaterRuntime.ApplicationStateDirectory(configuration);
        var timeout = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (signal is null) EventWaitHandle.TryOpenExisting(GetStopEventName(configuration.InstallDirectory), out signal);
                signal?.Set();
                try
                {
                    var paths = new List<string> { Path.Combine(directory, "lifecycle.lock") };
                    var observers = Path.Combine(directory, "observers");
                    PackageSafety.EnsureNoReparsePoints(observers);
                    if (Directory.Exists(observers)) paths.AddRange(Directory.EnumerateFiles(observers, "*.lock", SearchOption.TopDirectoryOnly));
                    foreach (var path in paths)
                    {
                        PackageSafety.EnsureNoReparsePoints(path);
                        if (File.Exists(path)) using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    }
                    if (configuration.SharedRuntime is null)
                    {
                        var legacy = PackageSafety.UnderRoot(configuration.InstallDirectory, "heirowUpdater.exe");
                        if (File.Exists(legacy)) using (new FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    }
                    return signal is not null; // Other apps may still execute the same immutable shared runtime.
                }
                catch (IOException) when (timeout.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(100, cancellationToken); }
                catch (IOException error) { throw new IOException("This application's updater did not stop in time. Close its update window and retry.", error); }
            }
        }
        finally { signal?.Dispose(); }
    }

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
