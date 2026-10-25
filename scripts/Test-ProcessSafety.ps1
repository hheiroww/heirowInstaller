#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$PackageCache,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Process consent acceptance requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/process-safety/' + [Guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh process acceptance output directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$probe = Join-Path $OutputDirectory 'probe'
$publish = @('publish',(Join-Path $projectRoot 'tests/HeirowInstaller.Tests/Fixtures/RunningApplicationProbe/RunningApplicationProbe.csproj'),'-c',$Configuration,'-r','win-x64','--self-contained','false','-m:1','-nr:false','--nologo','-o',$probe)
if ($PackageCache) { $publish += "-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))" }
& dotnet @publish
if ($LASTEXITCODE -ne 0) { throw 'Owned process fixture publication failed.' }
$run = @('run','--project',(Join-Path $projectRoot 'tests/HeirowInstaller.ProcessSafety.Tests/HeirowInstaller.ProcessSafety.Tests.csproj'),'-c',$Configuration)
if ($PackageCache) { $run += "-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))" }
$run += @('--',$probe)
& dotnet @run 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'acceptance.log')
if ($LASTEXITCODE -ne 0) { throw 'Process consent acceptance failed.' }
