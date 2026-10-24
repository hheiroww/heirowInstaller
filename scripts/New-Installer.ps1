#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$Output,
    [string]$Launcher,
    [string]$SigningKey,
    [string]$Cli
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Cli) { $Cli = Join-Path $projectRoot 'src/HeirowInstaller.Cli/bin/Release/net10.0/heirowInstaller.Cli.exe' }
if (-not (Test-Path -LiteralPath $Cli)) { throw 'Build the CLI first or pass -Cli with the published executable.' }
$Output = [IO.Path]::GetFullPath($Output)
$package = if ([IO.Path]::GetExtension($Output) -eq '.exe') { [IO.Path]::ChangeExtension($Output,'.hinst') } else { $Output }
if ([IO.Path]::GetExtension($package) -ne '.hinst') { throw 'Output must end in .hinst or .exe.' }
$arguments = @('pack',[IO.Path]::GetFullPath($Manifest),'--source',[IO.Path]::GetFullPath($SourceDirectory),'--output',$package)
if ($SigningKey) { $arguments += @('--sign-key',[IO.Path]::GetFullPath($SigningKey)) }
& $Cli @arguments
if ($LASTEXITCODE -ne 0) { throw 'Package build failed.' }
if ($package -ne $Output) {
    if (-not $Launcher) { throw 'An EXE setup requires -Launcher with the published heirowInstaller application.' }
    & $Cli bundle $package --launcher ([IO.Path]::GetFullPath($Launcher)) --output $Output
    if ($LASTEXITCODE -ne 0) { throw 'Executable bundle build failed.' }
}
& $Cli verify $Output
if ($LASTEXITCODE -ne 0) { throw 'Installer verification failed.' }
Get-FileHash -LiteralPath $Output -Algorithm SHA256
