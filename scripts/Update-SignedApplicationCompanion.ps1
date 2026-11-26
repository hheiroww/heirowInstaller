#requires -Version 7.0
<#
.SYNOPSIS
Builds a signed updater-only successor to a publisher-signed HINST package.
.DESCRIPTION
Preserves the existing application and updater settings. Verifies the baseline,
replacement and successor before handing off the archive for separate delivery.
The caller must authorize the release inputs and supply a fresh staging directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$')][string]$ProductId,
    [Parameter(Mandatory)][string]$BaselinePackage,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$BaselineSha256,
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][ValidateSet('NativeFiles','LegacyExecutable')][string]$Backend,
    [Parameter(Mandatory)][string]$UpdaterPath,
    [Parameter(Mandatory)][version]$UpdaterVersion,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$UpdaterSha256,
    [Parameter(Mandatory)][string]$InstallerCli,
    [Parameter(Mandatory)][string]$SigningKey,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$PublisherKeySha256,
    [Parameter(Mandatory)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a fresh maintenance staging directory.' }
$null = New-Item -ItemType Directory -Path $output

function Invoke-Authoring([string]$Log, [string[]]$Command) {
    $PSNativeCommandUseErrorActionPreference = $false
    $lines = @(& $InstallerCli @Command 2>&1 | ForEach-Object {
        $_.ToString().Replace($SigningKey, '[signing input]', [StringComparison]::OrdinalIgnoreCase)
    })
    $exitCode = $LASTEXITCODE
    $lines | Set-Content -LiteralPath (Join-Path $output $Log) -Encoding utf8
    if ($exitCode -ne 0) { throw "Installer CLI failed during $($Command[0]). Review $Log." }
}

function Read-PackageManifest([IO.Compression.ZipArchive]$Package) {
    $entry = $Package.GetEntry('manifest.json')
    if ($null -eq $entry -or $entry.Length -gt 4MB) { throw 'Package manifest is missing or too large.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable -Depth 100 }
    finally { $reader.Dispose() }
}

$signingPin = @('--require-signature','--trust-key',$PublisherKeySha256)
$updaterFile = Get-Item -LiteralPath $UpdaterPath
if ($updaterFile.PSIsContainer -or $updaterFile.Name -cne 'heirowUpdater.exe' -or
    $updaterFile.VersionInfo.FileVersion -cne $UpdaterVersion.ToString()) {
    throw 'The replacement companion must be the exact versioned heirowUpdater executable.'
}
$packageStream = [IO.File]::OpenRead((Get-Item -LiteralPath $BaselinePackage).FullName)
try {
    if ((Get-FileHash -InputStream $packageStream).Hash -ine $BaselineSha256) { throw 'The active package digest differs from its release pin.' }
    $packageStream.Position = 0
    Invoke-Authoring 'verify-baseline.json' (@('verify',$BaselinePackage) + $signingPin)
    $archive = [IO.Compression.ZipArchive]::new($packageStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $manifest = Read-PackageManifest $archive
        $previousVersion = [version]$manifest.version
        if ($manifest.id -cne $ProductId -or $manifest.targetPlatform -cne 'windows' -or $manifest['legacyMsi'] -or
            $Version -le $previousVersion -or $Version.Build -lt 0 -or
            !$manifest['updateCompanion'] -or
            ($Backend -eq 'NativeFiles' -and ($manifest['legacyExecutable'] -or $manifest.formatVersion -ne 4 -or
                $manifest.updateCompanion.updateMode -cne 'files' -or $manifest.updateCompanion.updateProtocol -cne 'hupd1')) -or
            ($Backend -eq 'LegacyExecutable' -and (-not $manifest['legacyExecutable'] -or $manifest.formatVersion -ne 2)) -or
            @($manifest['sharedPackages'] | Where-Object { $_ }).Count -ne 0 -or
            $manifest.updateCompanion.runtimeMode -cne 'shared' -or $manifest.updateCompanion.executablePath -cne 'heirowUpdater.exe') {
            throw 'The baseline must be a signed package for the exact product and supported updater contract.'
        }
        $oldUpdater = @($manifest.payload | Where-Object path -CEQ 'heirowUpdater.exe')
        if ($oldUpdater.Count -ne 1 -or $oldUpdater[0].feature -cne $manifest.updateCompanion.feature -or
            $oldUpdater[0].sha256 -ieq $UpdaterSha256) { throw 'The baseline does not pin one different updater payload.' }
        $payload = Join-Path $output 'payload'
        $null = New-Item -ItemType Directory -Path $payload
        $updaterSource = if ($oldUpdater[0]['source']) { $oldUpdater[0].source } else { $oldUpdater[0].path }
        $updaterTarget = [IO.Path]::GetFullPath((Join-Path $payload $updaterSource))
        foreach ($file in $manifest.payload) {
            $sourcePath = if ($file['source']) { $file.source } else { $file.path }
            $target = [IO.Path]::GetFullPath((Join-Path $payload $sourcePath))
            if (-not $target.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'A baseline payload source escapes its staging directory.'
            }
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
            if ($file.path -ceq 'heirowUpdater.exe') {
                if (Test-Path -LiteralPath $target) { throw 'The updater source must belong to the updater alone.' }
                [IO.File]::Copy($updaterFile.FullName, $target)
                if ((Get-FileHash -LiteralPath $target).Hash -ine $UpdaterSha256) { throw 'The replacement updater digest differs from its reviewed pin.' }
                $file.sha256 = $UpdaterSha256.ToLowerInvariant()
                $file.size = (Get-Item -LiteralPath $target).Length
                continue
            }
            if ($target -ieq $updaterTarget) { throw 'The updater source must belong to the updater alone.' }
            if (Test-Path -LiteralPath $target) {
                if ((Get-Item -LiteralPath $target).Length -ne $file.size -or (Get-FileHash -LiteralPath $target).Hash -ine $file.sha256) {
                    throw 'Different payloads share the same package source path.'
                }
                continue
            }
            $entry = $archive.GetEntry('payload/' + $file.path)
            if ($null -eq $entry -or $entry.Length -ne $file.size) { throw "Missing signed payload entry $($file.path)." }
            $source = $entry.Open()
            try {
                $destination = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
                try { $source.CopyTo($destination) }
                finally { $destination.Dispose() }
            }
            finally { $source.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
finally { $packageStream.Dispose() }

$manifest.version = $Version.ToString()
$expected = ConvertTo-Json -InputObject $manifest -Depth 100 -Compress
$manifestPath = Join-Path $output 'maintenance-manifest.json'
$expected | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$packagePath = Join-Path $output "$ProductId-$Version.hinst"
Invoke-Authoring 'pack.json' @('pack',$manifestPath,'--source',$payload,'--output',$packagePath,'--sign-key',$SigningKey)
Invoke-Authoring 'verify.json' (@('verify',$packagePath) + $signingPin)
$packedArchive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $actual = Read-PackageManifest $packedArchive
    if ((ConvertTo-Json -InputObject $actual -Depth 100 -Compress) -cne $expected) {
        throw 'The successor signed manifest changed more than the version and the companion payload.'
    }
}
finally { $packedArchive.Dispose() }

$receipt = [ordered]@{
    productId = $ProductId
    baselineVersion = $previousVersion.ToString()
    baselineSha256 = $BaselineSha256.ToLowerInvariant()
    version = $Version.ToString()
    publisherKeySha256 = $PublisherKeySha256.ToLowerInvariant()
    updaterVersion = $UpdaterVersion.ToString()
    updaterSha256 = $UpdaterSha256.ToLowerInvariant()
    unchangedPayloadFiles = $manifest.payload.Count - 1
    path = $packagePath
    size = (Get-Item -LiteralPath $packagePath).Length
    sha256 = (Get-FileHash -LiteralPath $packagePath).Hash.ToLowerInvariant()
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'maintenance-receipt.json') -Encoding utf8
[pscustomobject]$receipt
