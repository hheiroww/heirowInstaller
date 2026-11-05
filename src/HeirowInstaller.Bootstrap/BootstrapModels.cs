using System.Text.Json.Serialization;

namespace HeirowInstaller.Bootstrap;

internal sealed class BootstrapDescriptor
{
    public int FormatVersion { get; set; } = 1;
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Scope { get; set; } = "user";
    public string HostKind { get; set; } = "installer";
    public string PublisherKeySha256 { get; set; } = "";
    public bool RequiresAdministrator { get; set; }
    public List<string> AllowedDownloadOrigins { get; set; } = [];
    public List<BootstrapPackage> Packages { get; set; } = [];
    public string HostPackageId { get; set; } = "";
    public string HostEntryPoint { get; set; } = "";
    public string? DotNetPackageId { get; set; }
    public string? DotNetEntryPoint { get; set; }
    public List<string> HostArguments { get; set; } = [];
    public BootstrapDownload? ApplicationPackage { get; set; }
    public bool ApplicationPackageRequiresAuthentication { get; set; }
    public string? RuntimeConfigPath { get; set; }
    public List<string> ProbingPackageIds { get; set; } = [];
    public List<BootstrapPayload> ApplicationFiles { get; set; } = [];
}

internal class BootstrapDownload
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class BootstrapPackage : BootstrapDownload
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string TargetRuntime { get; set; } = "win-x64";
}

internal sealed class BootstrapManifest
{
    public int FormatVersion { get; set; }
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string? TargetRuntime { get; set; }
    public string Scope { get; set; } = "user";
    public List<BootstrapPayload> Payload { get; set; } = [];
}

internal sealed class BootstrapPayload
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class BootstrapSignature
{
    public string Algorithm { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Signature { get; set; } = "";
}

internal sealed record BootstrapProgress(string Phase, string Detail, long Completed = 0, long Total = 0);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BootstrapDescriptor))]
[JsonSerializable(typeof(BootstrapManifest))]
[JsonSerializable(typeof(BootstrapSignature))]
internal partial class BootstrapJson : JsonSerializerContext;
