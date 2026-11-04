#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Project, [Parameter(Mandatory)][string]$PublishedExecutable, [long]$MaximumBytes = 90MB, [string]$AssetsPath)
$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$executable = Get-Item -LiteralPath $PublishedExecutable
if (!$AssetsPath) { $assetsPath = Join-Path (Split-Path $projectPath -Parent) 'obj/project.assets.json' }
else { $assetsPath = [IO.Path]::GetFullPath($AssetsPath) }
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
foreach ($dependency in $assets.libraries.PSObject.Properties.Name) {
    if ($dependency -match '^(Silk\.NET(?:\.[^/]+)?|Ultz\.Native\.Assimp|Assimp|Microsoft\.Web\.WebView2|ProjectZ\.(Diagnostics|ModelImport|WebView2|XamlTools))/') {
        throw "Installer UI includes an unused optional runtime dependency: $dependency"
    }
}
if ($executable.Length -gt $MaximumBytes) { throw "Published $($executable.Name) exceeds the runtime size budget: $($executable.Length) > $MaximumBytes bytes." }
Write-Host "RUNTIME_SIZE_OK $($executable.Name)=$($executable.Length) bytes; optional runtime packages absent"
