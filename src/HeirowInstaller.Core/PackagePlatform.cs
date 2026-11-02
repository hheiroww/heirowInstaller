namespace HeirowInstaller.Core;

public static class PackagePlatform
{
    public static string Normalize(string? platform) => platform switch
    {
        "windows" or "linux" or "macos" or "freebsd" or "android" or "ios" or "other" => platform,
        _ => throw new InvalidDataException("Target platform must be windows, linux, macos, freebsd, android, ios, or other.")
    };

    public static void Validate(PackageManifest manifest)
    {
        if (manifest.TargetRuntime is { } runtime && (!SupportedRuntimes.Contains(runtime) || PlatformForRuntime(runtime) != manifest.TargetPlatform))
            throw new InvalidDataException("targetRuntime must be a supported runtime identifier matching targetPlatform.");
        if (Normalize(manifest.TargetPlatform) == "windows") return;
        if (Path.GetExtension(manifest.EntryPoint).ToLowerInvariant() is ".exe" or ".msi" ||
            manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null || manifest.NativeMigration is not null ||
            manifest.Registry.Count != 0 || manifest.Services.Count != 0 || manifest.Environment.Count != 0 || manifest.InstallationRoots?.Count > 0 ||
            manifest.Modules.Count != 0 || manifest.AffectedResources.Count != 0 || manifest.Prerequisites.Count != 0)
            throw new InvalidDataException("A non-Windows target cannot declare a Windows executable entry point or Windows installation operations.");
        foreach (var shortcut in manifest.Shortcuts)
            if (shortcut.Arguments.Length != 0 || shortcut.Location is not ("startMenu" or "desktop"))
                throw new InvalidDataException("Portable shortcuts use argumentList and a startMenu or desktop location.");
    }

    public static IReadOnlyList<string> SupportedRuntimes { get; } = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
    public static string PlatformForRuntime(string runtime) => runtime.StartsWith("win-", StringComparison.Ordinal) ? "windows"
        : runtime.StartsWith("linux-", StringComparison.Ordinal) ? "linux" : runtime.StartsWith("osx-", StringComparison.Ordinal) ? "macos"
        : throw new ArgumentException("Unsupported runtime identifier: " + runtime);

    public static void RequireCurrentHost(PackageManifest manifest)
    {
        if (manifest.TargetPlatform != InstallerPlatform.Current || manifest.TargetRuntime is { } runtime && runtime != InstallerPlatform.RuntimeIdentifier)
            throw new PlatformNotSupportedException($"This package targets {manifest.TargetRuntime ?? manifest.TargetPlatform}; the running installer is {InstallerPlatform.RuntimeIdentifier}.");
    }
}
