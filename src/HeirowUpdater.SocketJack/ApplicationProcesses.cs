using System.Diagnostics;
using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

/// <summary>Matches application instances by their canonical executable path, never by PID alone.</summary>
public static class ApplicationProcesses
{
    public static void ValidateIdentity(string executable, int processId, long startTicks)
    {
        if (processId <= 0 || startTicks <= 0 || processId == System.Environment.ProcessId)
            throw new ArgumentException("A different application process and exact creation time are required.");
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks != startTicks)
                throw new InvalidDataException("The application process identity changed before updater startup.");
            var image = process.MainModule?.FileName ?? throw new InvalidDataException("The application process executable could not be verified.");
            if (!Path.GetFullPath(image).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The application process does not match the configured executable.");
        }
        catch (ArgumentException) { } // A short-lived application may already have exited.
        catch (InvalidOperationException) { }
    }

    public static IReadOnlyList<int> Running(string executable)
    {
        executable = Path.GetFullPath(executable);
        PackageSafety.EnsureNoReparsePoints(executable);
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && (process.MainModule?.FileName is not { } image || Path.GetFullPath(image).Equals(executable, StringComparison.OrdinalIgnoreCase))) found.Add(process.Id);
                }
                catch (InvalidOperationException) { }
                // If another user's same-named host cannot be inspected, keep
                // waiting rather than risk replacing a plugin it has loaded.
                catch (System.ComponentModel.Win32Exception) { found.Add(process.Id); }
            }
        }
        return found;
    }

    public static async Task WaitForExitAsync(string executable, CancellationToken cancellationToken = default, string? additionalHost = null, Func<IEnumerable<string>>? additionalHosts = null)
    {
        DateTimeOffset? quietSince = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AnyRunning(executable, additionalHost, additionalHosts?.Invoke()))
            {
                quietSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - quietSince >= TimeSpan.FromMilliseconds(750)) return;
            }
            else quietSince = null;
            await Task.Delay(200, cancellationToken);
        }
    }
    public static bool AnyRunning(string executable, string? additionalHost = null, IEnumerable<string>? additionalHosts = null) =>
        Running(executable).Count > 0 || additionalHost is not null && Running(additionalHost).Count > 0 || additionalHosts?.Any(path => Running(path).Count > 0) == true;
}
