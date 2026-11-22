#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][string]$MaintenancePackagePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$PublisherKeySha256,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Native Windows apphost verification requires Windows.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh task-owned apphost fixture directory.' }
$publisherRoot = Join-Path $output "packages/$($PublisherKeySha256.Substring(0, 16).ToLowerInvariant())"

. (Join-Path $PSScriptRoot 'Shared-ToolkitRuntime.ps1')

function Invoke-CachedAppHost([string]$Name, [string[]]$LaunchArguments, [int]$ExpectedExitCode) {
    $tracePath = Join-Path $output "$Name.host-trace.log"
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $maintenanceFiles "$Name.exe"))
    $start.WorkingDirectory = $output
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($variable in @($start.Environment.Keys)) {
        if ($variable.StartsWith('DOTNET_ROOT', [StringComparison]::OrdinalIgnoreCase)) { $null = $start.Environment.Remove($variable) }
    }
    $start.Environment['DOTNET_HOST_TRACE'] = '1'
    $start.Environment['DOTNET_HOST_TRACEFILE'] = $tracePath
    $start.Environment['DOTNET_DISABLE_GUI_ERRORS'] = '1'
    foreach ($argument in $LaunchArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(30000)) {
            $process.Kill()
            throw "Cached apphost did not exit: $Name"
        }
        if ($process.ExitCode -ne $ExpectedExitCode) { throw "Cached apphost $Name exited with code $($process.ExitCode)." }
    } finally { $process.Dispose() }

    $trace = Get-Content -LiteralPath $tracePath -Raw
    if (!$trace.Contains("Using app-relative location [$runtimeFiles]", [StringComparison]::OrdinalIgnoreCase) -or
        !$trace.Contains("Resolved fxr [$resolvedHostFxr]", [StringComparison]::OrdinalIgnoreCase) -or
        $trace -match 'Using (global install location|environment variable)') {
        throw "Cached apphost $Name did not resolve the verified package-local runtime."
    }
}

$inspection = & $CliPath inspect $MaintenancePackagePath | ConvertFrom-Json
if ($LASTEXITCODE -or !$inspection.signed -or $inspection.publisherKeySha256 -ine $PublisherKeySha256 -or
    $inspection.manifest.id -cne 'socketjack.maintenance') { throw 'The fixture requires a signed maintenance package.' }
$runtimeVersion = Get-ToolkitLocalRuntimeVersion $inspection.manifest
if (!$runtimeVersion) { throw 'The signed fixture lacks package-local .NET.' }
$digest = (Get-FileHash -LiteralPath $MaintenancePackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$maintenanceFiles = Join-Path $publisherRoot "$digest/files"
& $CliPath extract $MaintenancePackagePath --require-signature --trust-key $PublisherKeySha256 --output $maintenanceFiles | Out-Null
if ($LASTEXITCODE) { throw 'Could not extract the verified maintenance package.' }
$runtimeFiles = Join-Path $maintenanceFiles 'dotnet'
$resolvedHostFxr = Join-Path $runtimeFiles "host/fxr/$runtimeVersion/hostfxr.dll"
$state = Join-Path $output 'state'
# The missing package reaches managed setup in silent isolated mode and leaves an owned diagnostic.
Invoke-CachedAppHost 'heirowInstaller' @('--silent', '--isolated', '--state-root', $state) 1
$installerLog = Get-Content -LiteralPath (Join-Path $state 'Logs/desktop-error.log') -Raw
if (!$installerLog.Contains('Silent installation requires a .hinst package', [StringComparison]::Ordinal)) {
    throw 'Cached installer did not enter managed setup.'
}
Invoke-CachedAppHost 'heirowUpdater' @('help') 0
'Cached installer and updater reached managed entry points through the signed toolkit runtime with DOTNET_ROOT variables unset.'
