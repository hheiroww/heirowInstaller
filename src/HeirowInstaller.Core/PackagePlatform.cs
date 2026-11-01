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
        if (Normalize(manifest.TargetPlatform) == "windows") return;
        if (Path.GetExtension(manifest.EntryPoint).ToLowerInvariant() is ".exe" or ".msi" ||
            manifest.LegacyMsi is not null || manifest.LegacyExecutable is not null || manifest.NativeMigration is not null ||
            manifest.Registry.Count != 0 || manifest.Services.Count != 0)
            throw new InvalidDataException("A non-Windows target cannot declare a Windows executable entry point or Windows installation operations.");
    }
}
