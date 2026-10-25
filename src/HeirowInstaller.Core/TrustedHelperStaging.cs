using System.Security.Cryptography;

namespace HeirowInstaller.Core;

/// <summary>Creates compact, unpredictable helper paths inside the caller's existing staging boundary.</summary>
public static class TrustedHelperStaging
{
    /// <summary>
    /// Reserves no files and changes no permissions. Call before downloading or
    /// copying so an unsupported Windows launch path fails with a useful error.
    /// </summary>
    public static string NewDirectoryPath(string stagingRoot, params string[] executableNames)
    {
        var root = Path.GetFullPath(stagingRoot);
        PackageSafety.EnsureNoReparsePoints(root);
        if (executableNames.Length == 0 || executableNames.Any(name => string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny(['/', '\\', ':', '"', '\0']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(char.IsControl) || name is "." or ".." ||
            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose the exact helper executable file names.", nameof(executableNames));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            // All 128 random bits fit in 22 filename-safe characters. Redundant
            // prefixes and hex expansion must not consume the Windows launch budget.
            var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var directory = Path.Combine(root, "h-" + id);
            if (OperatingSystem.IsWindows() && executableNames.Any(name => Path.Combine(directory, name).Length >= 260))
                throw new PathTooLongException("The trusted update helper's staging path is too long for Windows to launch. Choose a shorter state root or download directory.");
            PackageSafety.EnsureNoReparsePoints(directory);
            if (!Directory.Exists(directory) && !File.Exists(directory)) return directory;
        }
        throw new IOException("A unique trusted update helper directory could not be selected.");
    }
}
