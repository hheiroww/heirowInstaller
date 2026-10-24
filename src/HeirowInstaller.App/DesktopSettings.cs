using System.Text.Json;

namespace HeirowInstaller.App;

internal sealed class DesktopSettings
{
    public string DefaultOutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "heirowInstaller", "Packages");
    public string PublisherBaseUrl { get; set; } = "https://socketjack.com";
    public bool LaunchAfterInstall { get; set; }
    public bool RegisterProducts { get; set; } = true;
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "heirowInstaller", "desktop.json");

    public static DesktopSettings Load()
    {
        try { return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
