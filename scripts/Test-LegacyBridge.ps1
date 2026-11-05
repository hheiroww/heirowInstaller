#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativeUpdaterPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$LegacyCommit = 'f34d328ccfa9c402d964896e4fd3b39d93c7c1dc'
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The exact native updater bridge acceptance requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$NativeUpdaterPath = [IO.Path]::GetFullPath($NativeUpdaterPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $NativeUpdaterPath -PathType Leaf)) { throw 'Supply the built native updater bootstrap.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh owned output directory; prior fixtures are retained.' }
$resolvedCommit = (& git -C $projectRoot rev-parse --verify --end-of-options ($LegacyCommit + '^{commit}')).Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedCommit -notmatch '^[0-9a-f]{40}$') { throw 'The exact legacy commit is not available locally.' }
$requiredSpace = (Get-Item -LiteralPath $NativeUpdaterPath).Length * 5 + 128MB
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($OutputDirectory))
if ($drive.AvailableFreeSpace -lt $requiredSpace) { throw 'Insufficient space for the bounded legacy Core source and native fixture copies.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$archive = Join-Path $OutputDirectory 'legacy-core-source.zip'
& git -C $projectRoot archive --format=zip "--output=$archive" $resolvedCommit 'src/HeirowInstaller.Core' 'Directory.Build.props'
if ($LASTEXITCODE -ne 0) { throw 'The allowlisted public legacy Core source could not be archived.' }
$source = Join-Path $OutputDirectory 'legacy-source'
Expand-Archive -LiteralPath $archive -DestinationPath $source
$legacyProject = Join-Path $source 'src/HeirowInstaller.Core/HeirowInstaller.Core.csproj'
& dotnet build $legacyProject -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'The exact legacy Core did not compile.' }
$legacyCore = Join-Path $source 'src/HeirowInstaller.Core/bin/Release/net10.0/HeirowInstaller.Core.dll'
$fixture = Join-Path $OutputDirectory 'fixture'
& dotnet run --project (Join-Path $projectRoot 'tests/HeirowInstaller.LegacyBridge.Tests') -c Release -- $legacyCore $NativeUpdaterPath $resolvedCommit $fixture
if ($LASTEXITCODE -ne 0) { throw 'The exact legacy installer bridge failed acceptance.' }
[ordered]@{ purpose='Exact legacy-reader bridge acceptance'; legacyCommit=$resolvedCommit; root=$OutputDirectory; retained=$true;
    cleanup='Review exact source/build/fixture outputs after retaining acceptance receipts; no deletion was performed.' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'ownership.json') -Encoding utf8
