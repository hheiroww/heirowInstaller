using System.Text.Json.Serialization;

namespace HeirowInstaller.Core;

/// <summary>Published version metadata only; it grants no download or installation authority.</summary>
public sealed record InstallerVersionCatalogPage
{
    [JsonRequired] public int SchemaVersion { get; init; }
    [JsonRequired] public string ProductId { get; init; } = "";
    [JsonRequired] public string Channel { get; init; } = "";
    [JsonRequired] public string LatestVersion { get; init; } = "";
    [JsonRequired] public IReadOnlyList<string> Versions { get; init; } = [];
    [JsonRequired] public string? NextBeforeVersion { get; init; }
}

public enum InstallerVersionCheckState { Current, UpdateAvailable, Ahead }

public sealed record InstallerVersionCheckResult(
    string ProductId, string CurrentVersion, string Channel, string LatestVersion, InstallerVersionCheckState State)
{
    public bool Available => State == InstallerVersionCheckState.UpdateAvailable;
    public bool UpToDate => State == InstallerVersionCheckState.Current;
}
