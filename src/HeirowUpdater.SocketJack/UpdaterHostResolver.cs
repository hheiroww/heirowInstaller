namespace HeirowUpdater.SocketJack;

/// <summary>Locates the installed updater beside the actual application host, including self-extracting single-file applications.</summary>
public static class UpdaterHostResolver
{
    public static string? FindExecutable() => FindExecutable(System.Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>Resolves an updater from a trusted host context; the base directory is the fallback for framework-dependent consumers.</summary>
    public static string? FindExecutable(string? processPath, string baseDirectory) =>
        HostDirectories(processPath, baseDirectory).Select(directory => Path.Combine(directory, "heirowUpdater.exe")).FirstOrDefault(File.Exists);

    public static bool IsProcessInsideDirectory(string installDirectory) =>
        IsProcessInsideDirectory(installDirectory, System.Environment.ProcessPath);

    public static bool IsProcessInsideDirectory(string installDirectory, string? processPath)
    {
        if (processPath is null) return false;
        var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(processPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<string> HostDirectories() => HostDirectories(System.Environment.ProcessPath, AppContext.BaseDirectory);

    private static IEnumerable<string> HostDirectories(string? processPath, string baseDirectory)
    {
        string? processDirectory = null;
        // A shared dotnet.exe is not the application's installed host or a source of trusted companion binaries.
        if (processPath is not null && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            processDirectory = Path.GetDirectoryName(Path.GetFullPath(processPath));
            if (processDirectory is not null) yield return processDirectory;
        }
        var fallback = Path.GetFullPath(baseDirectory);
        if (!fallback.Equals(processDirectory, StringComparison.OrdinalIgnoreCase)) yield return fallback;
    }
}
