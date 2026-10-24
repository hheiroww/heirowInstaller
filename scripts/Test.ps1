#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$PackageCache)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($project in @('tests/HeirowInstaller.Tests/HeirowInstaller.Tests.csproj','tests/HeirowInstaller.Publishing.Tests/HeirowInstaller.Publishing.Tests.csproj')) {
    $arguments = @('run','--project',(Join-Path $projectRoot $project),'-c',$Configuration)
    if ($PackageCache) { $arguments += "-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
}
