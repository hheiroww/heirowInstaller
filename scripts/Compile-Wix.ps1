#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputPath,
    [Parameter(Mandatory)][string]$Output,
    [string]$Wix = 'wix',
    [string]$MsBuild,
    [string]$ProductId,
    [string]$LegalDocuments,
    [ValidateSet('x86','x64','arm64')][string]$Architecture = 'x64',
    [string[]]$Extension = @(),
    [string[]]$Define = @(),
    [string[]]$BindPath = @(),
    [string[]]$CompilerArgument = @(),
    [string]$Cli
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Cli) { $Cli = Join-Path $projectRoot 'src/HeirowInstaller.Cli/bin/Release/net10.0/heirowInstaller.Cli.exe' }
if (-not (Test-Path -LiteralPath $Cli)) { throw 'Build the CLI or pass the portable published CLI with -Cli.' }
$arguments = @('compile',[IO.Path]::GetFullPath($InputPath),'--output',[IO.Path]::GetFullPath($Output),'--wix',$Wix,'--arch',$Architecture)
if ($MsBuild) { $arguments += @('--msbuild',$MsBuild) }
if ($ProductId) { $arguments += @('--id',$ProductId) }
if ($LegalDocuments) { $arguments += @('--legal-documents',[IO.Path]::GetFullPath($LegalDocuments)) }
foreach ($value in $Extension) { $arguments += @('--extension',$value) }
foreach ($value in $Define) { $arguments += @('--define',$value) }
foreach ($value in $BindPath) { $arguments += @('--bind-path',$value) }
foreach ($value in $CompilerArgument) { $arguments += @('--compiler-arg',$value) }
& $Cli @arguments
if ($LASTEXITCODE -ne 0) { throw "WiX authoring failed with exit code $LASTEXITCODE. The original project and its license requirements remain authoritative." }
