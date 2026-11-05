namespace HeirowInstaller.Core;

/// <summary>An immutable dependency archive, authorized by the application's signed format-5 manifest.</summary>
public sealed class SharedPackageDefinition
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string TargetRuntime { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string PublisherKeySha256 { get; set; } = "";
    public string? Feature { get; set; }
}

/// <summary>Locally generated paths bound to the application's exact signed dependency inventory.</summary>
public sealed class SharedPackageBinding
{
    public int FormatVersion { get; set; } = 1;
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string Scope { get; set; } = "";
    public string PublisherKeySha256 { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public List<SharedPackageReference> Packages { get; set; } = [];
    public ApplicationLaunchDefinition? ApplicationLaunch { get; set; }
}

public sealed class SharedPackageReference
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string TargetRuntime { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Directory { get; set; } = "";
}
