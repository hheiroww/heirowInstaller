#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][string]$BootstrapPath,
    [Parameter(Mandatory)][string]$ApplicationPackagePath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PublisherKeySha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$OfflineCapableLauncherSha256,
    [string[]]$DependencyPaths = @(),
    [string[]]$BootstrapPackagePaths = @()
)
$ErrorActionPreference = 'Stop'

function Invoke-InstallerCli([string[]]$Arguments) {
    $result = & $CliPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Installer verification failed: $($Arguments[0])" }
    return $result | ConvertFrom-Json
}

function Get-ArchiveIdentity([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file -isnot [IO.FileInfo] -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Setup media requires a regular file at $Path."
    }
    return @{ path = $file.FullName; size = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Get-BoundArchives([string[]]$Paths, $Declarations) {
    $archives = @{}
    foreach ($path in $Paths) {
        $archive = Get-ArchiveIdentity $path
        if ($archives.ContainsKey($archive.sha256)) { throw 'An archive was supplied more than once.' }
        $archives[$archive.sha256] = $archive
    }
    $expected = @{}
    foreach ($declaration in $Declarations) {
        $archive = $archives[$declaration.sha256]
        if ($null -eq $archive -or $archive.size -ne $declaration.size) {
            throw "Missing signed archive $($declaration.id) $($declaration.version)."
        }
        $expected[$archive.sha256] = $archive
    }
    if ($archives.Count -ne $expected.Count) { throw 'An archive was supplied outside the signed declaration list.' }
    return $expected
}

$setup = Get-ArchiveIdentity $BootstrapPath
$binding = Invoke-InstallerCli @('inspect-bootstrap', $setup.path, '--trust-key', $PublisherKeySha256)
$descriptor = $binding.descriptor
if ($binding.launcherSha256 -ine $OfflineCapableLauncherSha256 -or $descriptor.hostKind -cne 'installer' -or
    $descriptor.offlineApplicationBundle -ne $true -or !$descriptor.applicationPackage -or $descriptor.applicationFileUpdate) {
    throw 'Use the approved offline-capable installer launcher with a signed application package binding.'
}
$application = Get-ArchiveIdentity $ApplicationPackagePath
if ($application.sha256 -ine $descriptor.applicationPackage.sha256 -or $application.size -ne $descriptor.applicationPackage.size) {
    throw 'The application archive does not match the signed setup.'
}
$verification = Invoke-InstallerCli @('verify', $application.path, '--require-signature', '--trust-key', $PublisherKeySha256)
if ($verification.productId -cne $descriptor.productId -or $verification.version -cne $descriptor.version) {
    throw 'The signed setup and application identities differ.'
}
$manifest = (Invoke-InstallerCli @('inspect', $application.path)).manifest
$dependencies = Get-BoundArchives $DependencyPaths $manifest.sharedPackages
$bootstrapPackages = if ($BootstrapPackagePaths.Count -gt 0) { Get-BoundArchives $BootstrapPackagePaths $descriptor.packages } else { @{} }

$output = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($output) -ine '.zip') { throw 'The output must be a .zip archive.' }
$file = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $entries = [ordered]@{'Setup.exe' = $setup; 'offline/application.hinst' = $application}
        foreach ($hash in $dependencies.Keys | Sort-Object) { $entries["offline/dependencies/$hash.hinst"] = $dependencies[$hash] }
        foreach ($hash in $bootstrapPackages.Keys | Sort-Object) { $entries["offline/bootstrap/$hash.hinst"] = $bootstrapPackages[$hash] }
        foreach ($entry in $entries.GetEnumerator()) {
            $source = [IO.File]::Open($entry.Value.path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ($source.Length -ne $entry.Value.size -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source)) -ine $entry.Value.sha256) {
                    throw "A verified input changed while preparing $($entry.Key)."
                }
                $source.Position = 0
                $item = $zip.CreateEntry($entry.Key, [IO.Compression.CompressionLevel]::NoCompression)
                $destination = $item.Open()
                try { $source.CopyTo($destination) } finally { $destination.Dispose() }
            } finally { $source.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $file.Dispose() }

$media = Get-ArchiveIdentity $output
[pscustomobject]@{ productId = $manifest.id; version = $manifest.version; path = $media.path; size = $media.size; sha256 = $media.sha256 }
