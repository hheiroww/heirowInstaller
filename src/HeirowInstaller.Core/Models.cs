using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeirowInstaller.Core;

public static class InstallerJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public sealed class PackageManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string EntryPoint { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string Scope { get; set; } = "user";
    public string? UpdateUrl { get; set; }
    public string? Homepage { get; set; }
    public string? LicenseFile { get; set; }
    public List<PackageFeature> Features { get; set; } = [];
    public List<PayloadFile> Payload { get; set; } = [];
    public List<RegistryDefinition> Registry { get; set; } = [];
    public List<ShortcutDefinition> Shortcuts { get; set; } = [];
    public List<EnvironmentDefinition> Environment { get; set; } = [];
    public List<ServiceDefinition> Services { get; set; } = [];
    public List<PrerequisiteDefinition> Prerequisites { get; set; } = [];
    public LegacyMsiDefinition? LegacyMsi { get; set; }
    public LegacyExecutableDefinition? LegacyExecutable { get; set; }
    public UpdateCompanionDefinition? UpdateCompanion { get; set; }
    public Dictionary<string, JsonElement> Modules { get; set; } = [];
}

public sealed class UpdateCompanionDefinition
{
    public string Feature { get; set; } = "updates";
    public string ExecutablePath { get; set; } = "heirowUpdater.exe";
    public string ConfigurationPath { get; set; } = "heirowUpdater.json";
    public string Mode { get; set; } = "after-exit";
    public string CredentialOrigin { get; set; } = "https://socketjack.com";
    public string? AuthApplication { get; set; }
    public string? CookieEnvironmentVariable { get; set; }
    public string? TokenEnvironmentVariable { get; set; }
}

/// <summary>Generated app-local updater configuration. Credential values are never stored here.</summary>
public sealed class UpdateCompanionConfiguration
{
    public int FormatVersion { get; set; } = 1;
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string StateRoot { get; set; } = "";
    public string Scope { get; set; } = "user";
    public string PublisherKeySha256 { get; set; } = "";
    public List<string> Features { get; set; } = [];
    public string EntryPoint { get; set; } = "";
    public string Mode { get; set; } = "after-exit";
    public bool Isolated { get; set; }
    public string CredentialOrigin { get; set; } = "https://socketjack.com";
    public string? AuthApplication { get; set; }
    public string? CookieEnvironmentVariable { get; set; }
    public string? TokenEnvironmentVariable { get; set; }
}

public sealed class PackageFeature
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
    public bool Default { get; set; } = true;
}

public sealed class PayloadFile
{
    public string Source { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? Feature { get; set; }
}

public sealed class RegistryDefinition
{
    public string Hive { get; set; } = "user";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public string Value { get; set; } = "";
    public string? Feature { get; set; }
}

public sealed class ShortcutDefinition
{
    public string Name { get; set; } = "";
    public string Location { get; set; } = "startMenu";
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? Icon { get; set; }
    public string? Feature { get; set; }
}

public sealed class EnvironmentDefinition
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Append { get; set; }
    public string? Feature { get; set; }
}

public sealed class ServiceDefinition
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string Start { get; set; } = "auto";
    public bool StartAfterInstall { get; set; } = true;
    public string? Feature { get; set; }
}

public sealed class PrerequisiteDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DetectFile { get; set; }
    public string? Executable { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public List<string> Arguments { get; set; } = [];
    public List<int> SuccessExitCodes { get; set; } = [0, 3010, 1641];
    public string? Feature { get; set; }
}

public sealed class LegacyMsiDefinition
{
    public string PayloadPath { get; set; } = "";
    public string? ProductCode { get; set; }
    public string InstallDirectoryProperty { get; set; } = "INSTALLFOLDER";
    public List<string> Arguments { get; set; } = [];
}

public sealed class LegacyExecutableDefinition
{
    public string PayloadPath { get; set; } = "";
    public List<string> InstallArguments { get; set; } = [];
    public List<string>? SilentInstallArguments { get; set; }
    public string? InstallDirectoryArgument { get; set; }
    public List<string> RepairArguments { get; set; } = [];
    public string? UninstallExecutable { get; set; }
    public List<string> UninstallArguments { get; set; } = [];
    public List<int> SuccessExitCodes { get; set; } = [0, 3010, 1641];
    public BackendRegistrationDefinition? MaintenanceRegistration { get; set; }
}

public sealed class BackendRegistrationDefinition
{
    public string Hive { get; set; } = "user";
    public string View { get; set; } = "registry64";
    public string? UninstallKey { get; set; }
    public string? BundleUpgradeCode { get; set; }
}

public sealed class PackageBuildOptions
{
    public string? SigningKeyPath { get; set; }
}

public sealed class InstallOptions
{
    public string? TargetDirectory { get; set; }
    public string? StateRoot { get; set; }
    public string? Scope { get; set; }
    public List<string>? Features { get; set; }
    public bool AllowDowngrade { get; set; }
    public bool RegisterProduct { get; set; } = true;
    public bool Silent { get; set; } = true;
    public bool Launch { get; set; }
    public string? LogPath { get; set; }
    public string? InstallerPath { get; set; }
    public List<string> LegacyArguments { get; set; } = [];
    public bool RequireSignature { get; set; }
    public string? TrustedPublisherKeySha256 { get; set; }
    /// <summary>A verified staged helper already stopped its owning watcher and still observes its cancellation event.</summary>
    public bool CompanionStopAlreadyHandled { get; set; }
    /// <summary>Disables OS-wide integrations for isolated package validation. Never use for production installation.</summary>
    public bool Isolated { get; set; }
}

public sealed class InstallPlan
{
    public PackageManifest Manifest { get; set; } = new();
    public string InstallDirectory { get; set; } = "";
    public string Scope { get; set; } = "user";
    public List<string> Features { get; set; } = [];
    public List<PayloadFile> Files { get; set; } = [];
    public long TotalBytes { get; set; }
    public bool IsUpgrade { get; set; }
    public string? PreviousVersion { get; set; }
    public bool RequiresElevation { get; set; }
    public bool UseOriginalInstallDirectory { get; set; }
    public bool UpdateCompanionEnabled { get; set; }
    public List<string> Operations { get; set; } = [];
}

public sealed record InstallProgress(string Phase, string Message, int Completed, int Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

public sealed class InstallResult
{
    public bool Success { get; set; } = true;
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public bool RebootRequired { get; set; }
    public string ReceiptPath { get; set; } = "";
}

public sealed class InstalledProduct
{
    public int ReceiptVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string Scope { get; set; } = "user";
    public DateTimeOffset InstalledAt { get; set; }
    public string? UpdateUrl { get; set; }
    public string? PublisherKeySha256 { get; set; }
    public string PackagePath { get; set; } = "";
    public string ReceiptPath { get; set; } = "";
    public string? UninstallerPath { get; set; }
    public bool RegisteredProduct { get; set; }
    public bool Isolated { get; set; }
    public bool IsMsi { get; set; }
    public bool IsExecutable { get; set; }
    public bool UseOriginalInstallDirectory { get; set; }
    public string? MsiProductCode { get; set; }
    public string? LegacySourceDirectory { get; set; }
    public List<string> Features { get; set; } = [];
    public List<InstalledFile> Files { get; set; } = [];
    public List<ModuleSnapshot> Modules { get; set; } = [];
    public List<ModuleSnapshot> BackendRegistrations { get; set; } = [];
}

public sealed class InstalledFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? OriginalBackup { get; set; }
}

public sealed class ModuleSnapshot
{
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public Dictionary<string, string?> Values { get; set; } = [];
}

public sealed class UpdateFeed
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallerUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string PackageType { get; set; } = "hinst";
    public string ReleaseNotes { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
}

public sealed class UpdateCheckResult
{
    public bool Available { get; set; }
    public string CurrentVersion { get; set; } = "";
    public UpdateFeed Feed { get; set; } = new();
}
