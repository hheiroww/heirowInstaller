using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeirowInstaller.Core;

namespace HeirowUpdater.SocketJack;

/// <summary>Additional app or plugin hosts can delay updates; they never grant execution authority.</summary>
public static class ApplicationHostRegistry
{
    public static async Task RegisterAsync(UpdateCompanionConfiguration configuration, string executable, int processId, long creationTicks, CancellationToken token = default)
    {
        executable = Path.GetFullPath(executable);
        ApplicationProcesses.ValidateIdentity(executable, processId, creationTicks);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant()))).ToLowerInvariant();
        await ApplicationUpdater.WriteJsonAsync(Path.Combine(ApplicationUpdater.StateDirectory(configuration), "hosts", key + ".json"),
            new Host { Executable = executable, ProcessId = processId, CreationTicks = creationTicks }, token);
    }

    public static IReadOnlyList<string> Paths(UpdateCompanionConfiguration configuration)
    {
        var directory = Path.Combine(ApplicationUpdater.StateDirectory(configuration), "hosts");
        if (!Directory.Exists(directory)) return [];
        PackageSafety.EnsureNoReparsePoints(directory);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Take(64))
        {
            PackageSafety.EnsureNoReparsePoints(file);
            if (new FileInfo(file).Length > 8192) throw new InvalidDataException("Application host registration is too large.");
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var host = JsonSerializer.Deserialize<Host>(input, InstallerJson.Options) ?? throw new InvalidDataException("Application host registration is empty.");
            if (!Path.IsPathFullyQualified(host.Executable) || host.ProcessId <= 0 || host.CreationTicks <= 0) throw new InvalidDataException("Invalid application host registration.");
            var executable = Path.GetFullPath(host.Executable); PackageSafety.EnsureNoReparsePoints(executable);
            paths.Add(executable);
        }
        return paths.ToList();
    }

    private sealed class Host
    {
        public string Executable { get; set; } = "";
        public int ProcessId { get; set; }
        public long CreationTicks { get; set; }
    }
}
