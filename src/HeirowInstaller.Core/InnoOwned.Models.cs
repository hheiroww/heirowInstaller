using System.Text.Json.Serialization;

namespace HeirowInstaller.Core;

/// <summary>Explicit publisher ownership attestation, carried inside the signed native target. Not an extraction proof.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InnoOwnedFootprintCatalog
{
    public int FormatVersion { get; set; } = 1;
    public string Authority { get; set; } = "";
    public string Coverage { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string PublisherKeySha256 { get; set; } = "";
    public string OriginalManifestSha256 { get; set; } = "";
    public string SetupPayloadPath { get; set; } = "";
    public string SetupSha256 { get; set; } = "";
    public long SetupSize { get; set; }
    public string AppId { get; set; } = "";
    public BackendRegistrationDefinition Registration { get; set; } = new();
    public string RootRelativeToLocalAppData { get; set; } = "";
    public string ReviewSha256 { get; set; } = "";
    public string WrapperPolicy { get; set; } = "";
    public List<InnoOwnedLineage> Lineage { get; set; } = [];
    public List<InnoOwnedFile> Files { get; set; } = [];
    public List<string> Directories { get; set; } = [];
    public List<InnoOwnedMetadata> GeneratedMetadata { get; set; } = [];
    public List<InnoOwnedRegistryRule> RegistryValues { get; set; } = [];
    public List<InnoOwnedShortcut> Shortcuts { get; set; } = [];
    public List<string> PreservedDirectories { get; set; } = [];
    public List<string> PreservedFiles { get; set; } = [];
    public List<string> UnsupportedBehaviors { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InnoOwnedLineage
{
    public string Version { get; set; } = "";
    public string SetupSha256 { get; set; } = "";
    public string RecipeSha256 { get; set; } = "";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InnoOwnedFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? LineageSetupSha256 { get; set; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InnoOwnedMetadata
{
    public string FileName { get; set; } = "";
    public bool Required { get; set; } = true;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InnoOwnedRegistryRule
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "string";
    public string Match { get; set; } = "literal";
    public string Value { get; set; } = "";
    public bool Optional { get; set; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InnoOwnedShortcut
{
    public string KnownFolder { get; set; } = "Programs";
    public string RelativePath { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Optional { get; set; } = true;
    public string? LineageSetupSha256 { get; set; }
}
