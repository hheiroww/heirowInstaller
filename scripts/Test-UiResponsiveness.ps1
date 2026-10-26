#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LauncherPath,
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$TrustedPublisherKey,
    [string]$OutputDirectory,
    [double]$MaximumUiWorkMilliseconds = 100,
    [double]$MaximumNavigationDispatchMilliseconds = 250
)
$ErrorActionPreference = 'Stop'
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
foreach ($path in @($LauncherPath,$PackagePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Supply an existing launcher and signed package.' }
}
if ($TrustedPublisherKey -notmatch '^[0-9a-fA-F]{64}$') { throw 'Supply the signed package publisher fingerprint.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/ui-performance-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh performance output directory.' }
$before = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
$start = [Diagnostics.ProcessStartInfo]::new($LauncherPath)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = 'Hidden'
$start.WorkingDirectory = Split-Path $LauncherPath -Parent
foreach ($argument in @('--ui-performance',$OutputDirectory,'--trust-key',$TrustedPublisherKey,'--require-signature','--expected-sha256',$before,$PackagePath)) { $start.ArgumentList.Add($argument) }
foreach ($name in @('HEIROWUPDATER_TOKEN','HEIROWUPDATER_COOKIE','SOCKETJACK_DOWNLOAD_TOKEN','SOCKETJACK_DOWNLOAD_COOKIE','SOCKETJACK_PUBLISHER_TOKEN','SOCKETJACK_PUBLISHER_COOKIE','HEIROWINSTALLER_SIGNING_KEY_PATH')) { $null = $start.Environment.Remove($name) }
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit(240000)) { throw "The owned performance process $($process.Id) did not finish. It was left intact for inspection." }
    if ($process.ExitCode -ne 0) { throw "The performance process returned $($process.ExitCode). Inspect its local evidence." }
} finally { $process.Dispose() }
$receipt = Join-Path $OutputDirectory 'ui-performance.json'
$result = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash -cne $before) { throw 'The input package changed.' }
if (-not $result.completed -or -not $result.isolated -or $result.installed -or $result.globalInput -or $result.packageChanged -or $result.aaSamples -ne 16 -or $result.filtering -cne 'Anisotropic16x') { throw 'The isolated rendering boundary was not preserved.' }
if ($result.update.maxMilliseconds -gt $MaximumUiWorkMilliseconds -or ($result.navigationDispatchMilliseconds | Measure-Object -Maximum).Maximum -gt $MaximumNavigationDispatchMilliseconds -or $result.progressRebuilds -ne 0 -or $result.progressArtworkDecodes -ne 0 -or [math]::Abs($result.finalProgress - 0.1) -gt 0.000001) { throw 'The UI responsiveness budget failed; inspect the measured receipt.' }
[ordered]@{result='PASS';receipt=$receipt;receiptSha256=(Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash;packageSha256=$before;maximumUiWorkMilliseconds=$MaximumUiWorkMilliseconds;maximumNavigationDispatchMilliseconds=$MaximumNavigationDispatchMilliseconds}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'acceptance.json') -Encoding utf8NoBOM
Get-Content -LiteralPath (Join-Path $OutputDirectory 'acceptance.json') -Raw
