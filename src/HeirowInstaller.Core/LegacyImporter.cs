using System.Text.Json;

namespace HeirowInstaller.Core;

public static class LegacyImporter
{
    /// <summary>Wraps the unchanged MSI database. Windows Installer continues to implement all authored MSI tables and custom actions.</summary>
    public static PackageManifest ImportMsi(string msiPath, string outputPackage, string? id = null, string? name = null,
        string? version = null, string? publisher = null, string scope = "machine", PackageBuildOptions? buildOptions = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MSI metadata inspection requires Windows.");
        msiPath = Path.GetFullPath(msiPath);
        name ??= WindowsIntegration.GetMsiProperty(msiPath, "ProductName");
        version ??= WindowsIntegration.GetMsiProperty(msiPath, "ProductVersion");
        publisher ??= WindowsIntegration.GetMsiProperty(msiPath, "Manufacturer");
        id ??= System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9._-]", "-").Trim('-', '.');
        var manifest = new PackageManifest
        {
            Id = id, Name = name, Version = version, Publisher = publisher, Scope = scope,
            Description = "Installed through the original Windows Installer database using heirowInstaller.",
            Payload = [new() { Source = Path.GetFileName(msiPath), Path = Path.GetFileName(msiPath) }],
            LegacyMsi = new() { PayloadPath = Path.GetFileName(msiPath), ProductCode = WindowsIntegration.GetMsiProperty(msiPath, "ProductCode") }
        };
        var temporary = Path.Combine(Path.GetTempPath(), "heirowInstaller-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var manifestPath = Path.Combine(temporary, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, InstallerJson.Options));
            return PackageBuilder.Build(manifestPath, Path.GetDirectoryName(msiPath)!, outputPackage, buildOptions);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}
