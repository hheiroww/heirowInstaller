#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Manifest,
    [string]$SourceDirectory,
    [string]$ApplicationProject,
    [Parameter(Mandatory)][string]$Output,
    [string]$Launcher,
    [string]$SigningKey,
    [string]$Updater,
    [switch]$UpdaterDefaultSelected,
    [ValidateSet('win-x64','win-x86','win-arm64')][string]$Runtime = 'win-x64',
    [string]$Framework,
    [string]$Cli
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Cli) { $Cli = Join-Path $projectRoot 'src/HeirowInstaller.Cli/bin/Release/net10.0/heirowInstaller.Cli.exe' }
if (-not (Test-Path -LiteralPath $Cli)) { throw 'Build the CLI first or pass -Cli with the published executable.' }
$Output = [IO.Path]::GetFullPath($Output)
if ([bool]$SourceDirectory -eq [bool]$ApplicationProject) { throw 'Select either -SourceDirectory with complete published files or -ApplicationProject to publish its runtime.' }
if ([IO.Path]::GetExtension($Output) -eq '.exe') {
    $arguments = @('build',[IO.Path]::GetFullPath($Manifest),'--output',$Output,'--runtime',$Runtime)
    if ($SourceDirectory) { $arguments += @('--source',[IO.Path]::GetFullPath($SourceDirectory)) }
    if ($ApplicationProject) { $arguments += @('--project',[IO.Path]::GetFullPath($ApplicationProject)) }
    if ($Launcher) { $arguments += @('--launcher',[IO.Path]::GetFullPath($Launcher)) }
    if ($SigningKey) { $arguments += @('--sign-key',[IO.Path]::GetFullPath($SigningKey)) }
    if ($Updater) { $arguments += @('--updater',[IO.Path]::GetFullPath($Updater)) }
    if ($UpdaterDefaultSelected) { $arguments += '--updater-default' }
    if ($Framework) { $arguments += @('--framework',$Framework) }
    & $Cli @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Standalone installer build failed.' }
    & $Cli verify $Output
    if ($LASTEXITCODE -ne 0) { throw 'Standalone installer verification failed.' }
    Get-FileHash -LiteralPath $Output -Algorithm SHA256
    return
}
if ($ApplicationProject -or $Updater) { throw 'Application publication and updater injection require an .exe standalone output.' }
$package = if ([IO.Path]::GetExtension($Output) -eq '.exe') { [IO.Path]::ChangeExtension($Output,'.hinst') } else { $Output }
if ([IO.Path]::GetExtension($package) -ne '.hinst') { throw 'Output must end in .hinst or .exe.' }
$arguments = @('pack',[IO.Path]::GetFullPath($Manifest),'--source',[IO.Path]::GetFullPath($SourceDirectory),'--output',$package)
if ($SigningKey) { $arguments += @('--sign-key',[IO.Path]::GetFullPath($SigningKey)) }
& $Cli @arguments
if ($LASTEXITCODE -ne 0) { throw 'Package build failed.' }
& $Cli verify $Output
if ($LASTEXITCODE -ne 0) { throw 'Installer verification failed.' }
Get-FileHash -LiteralPath $Output -Algorithm SHA256
