#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RuntimeFrameworkVersion = '10.0.14',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The legacy bridge updater targets Windows x64.' }
if ($RuntimeFrameworkVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Supply an exact stable .NET runtime version.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh owned output directory; existing bridge outputs are retained.' }
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($OutputDirectory))
if ($drive.AvailableFreeSpace -lt 512MB) { throw 'At least 512 MiB is required for bounded bridge publication and build intermediates.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
& dotnet publish (Join-Path $projectRoot 'src/HeirowUpdater/HeirowUpdater.csproj') -c $Configuration -r win-x64 --self-contained true `
    "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $OutputDirectory --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'The self-contained bridge updater did not publish.' }
$executable = Join-Path $OutputDirectory 'heirowUpdater.exe'
$artifact = Get-Item -LiteralPath $executable
if ($artifact.Length -gt 100000000) { throw 'The one-time bridge updater exceeds the 100 MB transitional artifact budget; outputs were retained.' }
[ordered]@{ purpose='One-time format-4 bridge updater'; runtimeFrameworkVersion=$RuntimeFrameworkVersion; artifact=$executable;
    bytes=$artifact.Length; sha256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant();
    fileVersion=$artifact.VersionInfo.FileVersion; selfContained=$true; retained=$true;
    requiredAcceptance='Run Test-LegacyBridge.ps1 -ExecuteSelfContainedUpdater with the exact prior Core commit and this executable before configuring a compatibility pointer.';
    notes='The final thin native updater remains separate; this transitional executable requires no preinstalled maintenance host or .NET runtime.' } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bridge-updater-build.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $OutputDirectory 'bridge-updater-build.json')
