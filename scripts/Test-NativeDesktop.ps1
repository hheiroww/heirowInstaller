#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LauncherPath,
    [string]$OutputDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) { throw 'Supply the newly built native heirowInstaller executable.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/native-desktop-' + [Guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Native acceptance requires a fresh output directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$probeOutput = Join-Path $OutputDirectory 'probe'
$hostDirectory = Join-Path $OutputDirectory 'host'
New-Item -ItemType Directory -Path $hostDirectory | Out-Null
$sourceLauncherSha256 = (Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash
$copiedLauncher = Join-Path $hostDirectory 'heirowInstaller.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $copiedLauncher
if ((Get-FileHash -LiteralPath $copiedLauncher -Algorithm SHA256).Hash -ne $sourceLauncherSha256) { throw 'The isolated toolkit host copy changed.' }
# Invalid own-root metadata must disable updater actions without escaping to a sibling/global host.
[IO.File]::WriteAllText((Join-Path $hostDirectory 'heirowUpdater.json'),'{"formatVersion":')
$LauncherPath = $copiedLauncher
$probeProject = Join-Path $projectRoot 'tests/HeirowInstaller.Tests/Fixtures/RunningApplicationProbe/RunningApplicationProbe.csproj'
& dotnet publish $probeProject -c $Configuration -r win-x64 --self-contained false -o $probeOutput -m:1 -p:UseSharedCompilation=false -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'The owned running-application probe did not publish.' }
$uiDirectory = Join-Path $OutputDirectory 'ui'
$start = [Diagnostics.ProcessStartInfo]::new($LauncherPath)
$start.UseShellExecute = $false
$start.WorkingDirectory = Split-Path $LauncherPath -Parent
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('--ui-smoke')
$start.ArgumentList.Add($uiDirectory)
$start.Environment['HEIROWINSTALLER_SMOKE_APPLICATION'] = Join-Path $probeOutput 'RunningApplicationProbe.exe'
$start.Environment['HEIROWINSTALLER_SMOKE_MALFORMED_UPDATER_PROFILE'] = '1'
foreach ($name in @('HEIROWUPDATER_TOKEN','HEIROWUPDATER_COOKIE','SOCKETJACK_DOWNLOAD_TOKEN','SOCKETJACK_DOWNLOAD_COOKIE','SOCKETJACK_PUBLISHER_TOKEN','SOCKETJACK_PUBLISHER_COOKIE','HEIROWINSTALLER_SIGNING_KEY_PATH')) { $start.Environment.Remove($name) | Out-Null }
$process = [Diagnostics.Process]::Start($start)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(180000)) { throw "Native acceptance did not finish within three minutes. Its owned process $($process.Id) was left intact for inspection; no process was terminated." }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'stdout.log'),$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'stderr.log'),$stderr.GetAwaiter().GetResult())
    if ($process.ExitCode -ne 0) { throw "Native acceptance returned exit code $($process.ExitCode). Inspect ui/ui-smoke.json." }
    $receiptPath = Join-Path $uiDirectory 'ui-smoke.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if (-not $receipt.success) { throw 'The native acceptance receipt did not report success.' }
    [pscustomobject]@{ success = $true; checks = $receipt.checks.Count; receipt = $receiptPath; receiptSha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash } | ConvertTo-Json
} finally { $process.Dispose() }
