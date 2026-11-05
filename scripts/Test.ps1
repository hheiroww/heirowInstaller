#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$PackageCache, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($project in @(
    'tests/HeirowInstaller.CrossPlatform.Tests/HeirowInstaller.CrossPlatform.Tests.csproj',
    'tests/HeirowInstaller.CodeSigning.Tests/HeirowInstaller.CodeSigning.Tests.csproj',
    'tests/HeirowInstaller.Legal.Tests/HeirowInstaller.Legal.Tests.csproj',
    'tests/HeirowInstaller.Tests/HeirowInstaller.Tests.csproj',
    'tests/HeirowInstaller.Incremental.Tests/HeirowInstaller.Incremental.Tests.csproj',
    'tests/HeirowInstaller.FileSelection.Tests/HeirowInstaller.FileSelection.Tests.csproj',
    'tests/HeirowInstaller.Bootstrap.Tests/HeirowInstaller.Bootstrap.Tests.csproj',
    'tests/HeirowInstaller.Publishing.Tests/HeirowInstaller.Publishing.Tests.csproj',
    'tests/HeirowInstaller.SecurityLoader.Integration/HeirowInstaller.SecurityLoader.Integration.csproj',
    'tests/HeirowInstaller.Prerequisites.Tests/HeirowInstaller.Prerequisites.Tests.csproj',
    'tests/HeirowInstaller.SharedPackages.Tests/HeirowInstaller.SharedPackages.Tests.csproj',
    'tests/HeirowInstaller.NativeMigration.Tests/HeirowInstaller.NativeMigration.Tests.csproj',
    'tests/HeirowInstaller.LegacyCleanup.Tests/HeirowInstaller.LegacyCleanup.Tests.csproj',
    'tests/HeirowInstaller.WorkingDirectory.Tests/HeirowInstaller.WorkingDirectory.Tests.csproj',
    'tests/HeirowInstaller.RunningApplicationsUi.Tests/HeirowInstaller.RunningApplicationsUi.Tests.csproj',
    'tests/HeirowInstaller.GraphicsFallback.Tests/HeirowInstaller.GraphicsFallback.Tests.csproj'
)) {
    $arguments = @('run','--project',(Join-Path $projectRoot $project),'-c',$Configuration)
    if ($PackageCache) { $arguments += "-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))" }
    if ($project -like '*GraphicsFallback.Tests*') {
        $testOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts/tests/graphics-fallback' }
        $arguments += @('--', $testOutput)
    }
    if ($project -like '*SharedPackages.Tests*') {
        $sharedFixtures = if ($OutputDirectory) { Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'shared-packages-fixtures' }
            else { Join-Path ([IO.Path]::GetTempPath()) ('heirowinstaller-shared-' + [Guid]::NewGuid().ToString('N')) }
        $arguments += @('--', '--root', $sharedFixtures)
    }
    if ($project -like '*FileSelection.Tests*' -or $project -like '*Bootstrap.Tests*') {
        $fixtureName = if ($project -like '*FileSelection.Tests*') { 'file-selection-fixtures' } else { 'bootstrap-fixtures' }
        $ownedFixtures = if ($OutputDirectory) { Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $fixtureName }
            else { Join-Path ([IO.Path]::GetTempPath()) ('heirowinstaller-' + $fixtureName + '-' + [Guid]::NewGuid().ToString('N')) }
        $arguments += @('--', $ownedFixtures)
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
}
