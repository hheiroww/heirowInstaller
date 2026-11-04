#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Vsix
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content -LiteralPath (Join-Path $root 'build/projectz-dx11-runtime.json') -Raw | ConvertFrom-Json
$frameworkVersion = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.ProjectZPackageVersion.InnerText.Trim([char[]]'[]')
if ($pin.version -cne $frameworkVersion) { throw 'The DX11 runtime pin must match the current Project-Z package version.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
if (!$Vsix) {
    $Vsix = Join-Path $output ('ProjectZ.VisualStudio2026-' + $pin.version + '.vsix')
    if (!(Test-Path -LiteralPath $Vsix)) {
        # Keep an interrupted download for explicit owner review; never prune shared caches.
        $partial = $Vsix + '.partial'
        if (Test-Path -LiteralPath $partial) { throw 'An interrupted VSIX download exists. Supply a verified -Vsix path or use another owned output directory.' }
        Invoke-WebRequest -Uri $pin.url -OutFile $partial -TimeoutSec 180
        if ((Get-Item -LiteralPath $partial).Length -ne $pin.bytes -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine $pin.sha256) { throw 'The downloaded Project-Z VSIX differs from the reviewed official release.' }
        Move-Item -LiteralPath $partial -Destination $Vsix
    }
}
$Vsix = [IO.Path]::GetFullPath($Vsix)
if ((Get-Item -LiteralPath $Vsix).Length -ne $pin.bytes -or (Get-FileHash -LiteralPath $Vsix -Algorithm SHA256).Hash -ine $pin.sha256) { throw 'The Project-Z VSIX differs from the reviewed official release.' }
$runtime = Join-Path $output 'ProjectZ.dll'
if (!(Test-Path -LiteralPath $runtime)) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Vsix)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $pin.entry })
        if ($entries.Count -ne 1 -or $entries[0].Length -ne $pin.runtimeBytes) { throw 'The official VSIX has no unique expected DX11 runtime.' }
        $runtimeInput = $entries[0].Open()
        try {
            $destination = [IO.File]::Open($runtime, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $runtimeInput.CopyTo($destination) } finally { $destination.Dispose() }
        } finally { $runtimeInput.Dispose() }
    } finally { $archive.Dispose() }
}
if ((Get-Item -LiteralPath $runtime).Length -ne $pin.runtimeBytes -or (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ine $pin.runtimeSha256) { throw 'The extracted DX11 runtime differs from the pinned official template.' }
[pscustomobject]@{path=$runtime; bytes=$pin.runtimeBytes; sha256=$pin.runtimeSha256; version=$pin.version; sourceCommit=$pin.sourceCommit; vsix=$Vsix}
