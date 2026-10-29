#requires -Version 7.0
<#
.SYNOPSIS
Verifies native updater close-apps consent and staged-update races with isolated fixtures.
.DESCRIPTION
Builds only the benign desktop acceptance probe, signs two generated packages,
and uses unique isolated targets. No service, real application, account, machine
registration or scheduled task is changed. Every application close request is
made by the product guard after a separate native Project-Z consent choice.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishedUpdaterPath,
      [Parameter(Mandatory)][string]$CliPath,
      [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native updater acceptance requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($binary in @($PublishedUpdaterPath, $CliPath)) {
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing acceptance binary: $binary" }
}
$PublishedUpdaterPath = [IO.Path]::GetFullPath($PublishedUpdaterPath)
$CliPath = [IO.Path]::GetFullPath($CliPath)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/windows-adapter-smoke' }
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$run = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "updater-running-apps-$runId"))
if (Test-Path -LiteralPath $run) { throw 'Acceptance directory already exists.' }
New-Item -ItemType Directory -Path $run | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$fixtures = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$receipt = [ordered]@{ success = $false; runDirectory = $run; checks = $checks; cleanup = [ordered]@{} }
function Assert-Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $checks.Add($Message) }
function Read-Json([string]$Path) {
    if (Test-Path -LiteralPath $Path) { try { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json } catch { return $null } }
    return $null
}
function Wait-For([scriptblock]$Condition, [string]$Message, [int]$Seconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (& $Condition)) { if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out: $Message" }; Start-Sleep -Milliseconds 100 }
}
function Start-Owned([string]$Executable, [string[]]$Arguments, [string]$Name, [hashtable]$EnvironmentValues = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $run
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $EnvironmentValues.Keys) { $start.Environment[$key] = $EnvironmentValues[$key] }
    $child = [Diagnostics.Process]::Start($start)
    $owned = [pscustomobject]@{ process = $child; executable = [IO.Path]::GetFullPath($Executable);
        started = $child.StartTime.ToUniversalTime().Ticks; name = $Name;
        output = $child.StandardOutput.ReadToEndAsync(); errorOutput = $child.StandardError.ReadToEndAsync() }
    $processes.Add($owned); return $owned
}
function Finish-Owned([object]$Owned, [int[]]$Codes = @(0, 3010)) {
    Wait-For { $Owned.process.HasExited } $Owned.name 90
    $output = $Owned.output.GetAwaiter().GetResult(); $errors = $Owned.errorOutput.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $run "$($Owned.name).stdout.txt"), $output)
    [IO.File]::WriteAllText((Join-Path $run "$($Owned.name).stderr.txt"), $errors)
    if ($Owned.process.ExitCode -notin $Codes) { throw "$($Owned.name) failed ($($Owned.process.ExitCode)): $errors" }
    return $output
}
function Invoke-Owned([string]$Executable, [string[]]$Arguments, [string]$Name) {
    . (Join-Path $PSScriptRoot 'Test-LegalFixture.ps1')
    $Arguments = Add-TestLegalArguments $Executable $Arguments
    $owned = Start-Owned $Executable $Arguments $Name; Finish-Owned $owned
}
function Install-Fixture([string]$Name) {
    $target = Join-Path $run "$Name/app"; $state = Join-Path $run "$Name/state"
    $null = Invoke-Owned $CliPath @('install', $oldPackage, '--silent', '--isolated', '--state-root', $state,
        '--target', $target, '--scope', 'user', '--no-register', '--trust-key', $pin, '--require-signature') "install-$Name"
    $fixture = [pscustomobject]@{ name = $Name; target = $target; state = $state;
        config = Join-Path $target 'heirowUpdater.json'; receipt = Join-Path $state "$productId/receipt.json" }
    $fixtures.Add($fixture); return $fixture
}
function Start-Probe([object]$Fixture, [string]$Name, [switch]$KeepOpen, [switch]$External) {
    $directory = Join-Path $run "probe-$Name"; New-Item -ItemType Directory -Path $directory | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory '.running-application-probe'), "heirowInstaller running-application acceptance`n")
    $executable = Join-Path $Fixture.target 'RunningApplicationProbe.exe'
    if ($External) {
        $externalDirectory = Join-Path $run 'external-host'; New-Item -ItemType Directory -Path $externalDirectory -Force | Out-Null
        $executable = Join-Path $externalDirectory 'RunningApplicationProbe.exe'
        if (-not (Test-Path -LiteralPath $executable)) { Copy-Item -LiteralPath (Join-Path $probePublish 'RunningApplicationProbe.exe') -Destination $executable }
    }
    $arguments = @('--run-directory', $directory); if ($KeepOpen) { $arguments += '--keep-open' }
    $owned = Start-Owned $executable $arguments "probe-$Name"
    Wait-For { Test-Path -LiteralPath (Join-Path $directory 'ready.json') } "ready probe $Name" 20
    $ready = Read-Json (Join-Path $directory 'ready.json')
    Assert-Check ($ready.processId -eq $owned.process.Id -and $ready.startTimeUtcTicks -eq $owned.started -and
        $ready.executablePath.Equals($owned.executable, [StringComparison]::OrdinalIgnoreCase)) "Probe $Name records its exact owned process identity"
    return [pscustomobject]@{ owned = $owned; directory = $directory }
}
function Close-Probe([object]$Probe) {
    [IO.File]::WriteAllText((Join-Path $Probe.directory 'allow-close'), 'close this isolated fixture')
    $null = Finish-Owned $Probe.owned
}
function Start-Apply([object]$Fixture, [string]$Choice, [string]$Name, [string[]]$Extra = @()) {
    $choiceFile = Join-Path $run "$Name.choice.txt"; [IO.File]::WriteAllText($choiceFile, $Choice)
    $report = Join-Path $run "$Name.prompt.json"; $capture = Join-Path $run "$Name.prompt.png"; $status = Join-Path $run "$Name.status.json"
    $arguments = @('apply-bundle', $newPackage, '--product-id', $productId, '--expected-version', '1.0.1',
        '--sha256', $newHash, '--trust-key', $pin, '--scope', 'user', '--target', $Fixture.target,
        '--state-root', $Fixture.state, '--isolated', '--lifecycle-config', $Fixture.config,
        '--maintenance-host', (Join-Path $Fixture.target 'heirowUpdater.exe'), '--status-file', $status) + $Extra
    $owned = Start-Owned $PublishedUpdaterPath $arguments $Name @{
        HEIROWUPDATER_TEST_CLOSE_APPS_CHOICE_FILE = $choiceFile
        HEIROWUPDATER_TEST_CLOSE_APPS_CAPTURE = $capture
        HEIROWUPDATER_TEST_CLOSE_APPS_REPORT = $report
        DOTNET_BUNDLE_EXTRACT_BASE_DIR = (Join-Path $run 'bundle-extraction')
    }
    return [pscustomobject]@{ owned = $owned; choice = $choiceFile; report = $report; capture = $capture; status = $status }
}
function Assert-Unchanged([object]$Fixture, [string]$ReceiptHash, [string]$Message) {
    Assert-Check ((Get-FileHash -LiteralPath $Fixture.receipt).Hash -eq $ReceiptHash -and
        [IO.File]::ReadAllText((Join-Path $Fixture.target 'generation.txt')) -eq '1.0.0') $Message
}
try {
    $probeProject = Join-Path $run 'probe-project'; New-Item -ItemType Directory -Path $probeProject | Out-Null
    foreach ($file in @('Program.cs', 'RunningApplicationProbe.csproj')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "tests/HeirowInstaller.Tests/Fixtures/RunningApplicationProbe/$file") -Destination $probeProject
    }
    $probePublish = Join-Path $run 'probe-publish'
    $null = Invoke-Owned (Get-Command dotnet.exe -ErrorAction Stop).Source @('publish', (Join-Path $probeProject 'RunningApplicationProbe.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:UseSharedCompilation=false',
        '-p:ImplicitUsings=enable', '-p:Nullable=enable', '-p:DebugType=None', '-m:1', '-nodeReuse:false', '-o', $probePublish, '--nologo', '-v:quiet') 'build-desktop-probe'
    $keyPath = Join-Path $run 'fixture.private.pem'; $key = [Security.Cryptography.RSA]::Create(3072)
    try { [IO.File]::WriteAllText($keyPath, $key.ExportPkcs8PrivateKeyPem()) } finally { $key.Dispose() }
    $productId = "updater-closure-$runId"
    foreach ($version in @('1.0.0', '1.0.1')) {
        $source = Join-Path $run "source-$version"; New-Item -ItemType Directory -Path $source | Out-Null
        Copy-Item -LiteralPath (Join-Path $probePublish 'RunningApplicationProbe.exe') -Destination $source
        Copy-Item -LiteralPath $PublishedUpdaterPath -Destination (Join-Path $source 'heirowUpdater.exe')
        [IO.File]::WriteAllText((Join-Path $source 'generation.txt'), $version)
        $manifest = [ordered]@{ formatVersion = 1; id = $productId; name = 'Updater desktop acceptance'; version = $version;
            publisher = 'heirowInstaller acceptance'; scope = 'user'; entryPoint = 'RunningApplicationProbe.exe'; updateUrl = 'https://updates.invalid/fixture.json';
            features = @(@{ id = 'core'; name = 'Application'; required = $true }, @{ id = 'updates'; name = 'Optional updates'; default = $true });
            updateCompanion = @{ feature = 'updates'; executablePath = 'heirowUpdater.exe'; configurationPath = 'heirowUpdater.json'; mode = 'after-exit' };
            payload = @(@{ source = 'RunningApplicationProbe.exe'; path = 'RunningApplicationProbe.exe'; feature = 'core' },
                @{ source = 'generation.txt'; path = 'generation.txt'; feature = 'core' }, @{ source = 'heirowUpdater.exe'; path = 'heirowUpdater.exe'; feature = 'updates' }) }
        $manifestPath = Join-Path $run "manifest-$version.json"; [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8))
        $null = Invoke-Owned $CliPath @('pack', $manifestPath, '--source', $source, '--output', (Join-Path $run "$version.hinst"), '--sign-key', $keyPath) "pack-$version"
    }
    $oldPackage = Join-Path $run '1.0.0.hinst'; $newPackage = Join-Path $run '1.0.1.hinst'
    $pin = (Invoke-Owned $CliPath @('inspect', $oldPackage) 'inspect-old' | ConvertFrom-Json).publisherKeySha256
    $newHash = (Get-FileHash -LiteralPath $newPackage).Hash

    $cancel = Install-Fixture 'cancel'; $app = Start-Probe $cancel 'cancel'
    $before = (Get-FileHash -LiteralPath $cancel.receipt).Hash
    $attempt = Start-Apply $cancel 'cancel' 'cancel-update'; $null = Finish-Owned $attempt.owned @(130)
    $prompt = Read-Json $attempt.report
    Assert-Check ($prompt.choice -eq 'Cancel' -and $prompt.framework -eq 'Project-Z' -and $prompt.pointerEventsInjected -and
        $prompt.applications.Count -eq 1 -and $prompt.applications[0].processId -eq $app.owned.process.Id) 'Cancel is a real native choice listing the exact affected app'
    Assert-Check (-not $app.owned.process.HasExited -and -not (Test-Path -LiteralPath (Join-Path $app.directory 'close-requests.json'))) 'Cancel never requests application closure'
    Assert-Unchanged $cancel $before 'Cancel preserves the application bytes and installed receipt before mutation'
    Close-Probe $app

    $normal = Install-Fixture 'normal'; $app1 = Start-Probe $normal 'normal-first'; $app2 = Start-Probe $normal 'normal-second'
    $attempt = Start-Apply $normal 'close' 'normal-update'; $null = Finish-Owned $attempt.owned
    $prompt = Read-Json $attempt.report
    Assert-Check ($prompt.choice -eq 'CloseAppsAndContinue' -and $prompt.applications.Count -eq 2 -and
        @($prompt.applications | Where-Object { $_.canRequestClose }).Count -eq 2) 'Separate native consent lists all affected app instances'
    Assert-Check ($app1.owned.process.HasExited -and $app2.owned.process.HasExited -and
        (Read-Json (Join-Path $app1.directory 'close-requests.json')).requests -ge 1 -and
        (Read-Json (Join-Path $app2.directory 'close-requests.json')).requests -ge 1) 'Update closes each exact app through normal window closure'
    Assert-Check ((Read-Json $normal.receipt).version -eq '1.0.1' -and
        [IO.File]::ReadAllText((Join-Path $normal.target 'generation.txt')) -eq '1.0.1') 'The signed staged package installs after all apps close'
    $reopened = Start-Probe $normal 'updated-reopen'
    Assert-Check (-not $reopened.owned.process.HasExited) 'The updated installed application reopens successfully'
    Close-Probe $reopened

    $saving = Install-Fixture 'save-prompt'; $app = Start-Probe $saving 'save-prompt' -KeepOpen
    $before = (Get-FileHash -LiteralPath $saving.receipt).Hash
    $attempt = Start-Apply $saving 'close' 'save-prompt-update'
    Wait-For { Test-Path -LiteralPath (Join-Path $app.directory 'close-requests.json') } 'a normal close request reaching the save prompt'
    [IO.File]::WriteAllText($attempt.choice, 'cancel')
    $null = Finish-Owned $attempt.owned @(130); $prompt = Read-Json $attempt.report
    Assert-Check ($prompt.isRetry -and $prompt.choice -eq 'Cancel' -and -not $app.owned.process.HasExited) 'An app refusing closure gets a responsive retry dialog with cancellation'
    Assert-Unchanged $saving $before 'Cancelling a pending save prompt preserves installed bytes and receipt'
    Close-Probe $app

    $manual = Install-Fixture 'manual-host'; $app = Start-Probe $manual 'manual-host' -External
    $before = (Get-FileHash -LiteralPath $manual.receipt).Hash
    $waitIdentity = @('--wait-pid', [string]$app.owned.process.Id, '--wait-start', [string]$app.owned.started)
    $attempt = Start-Apply $manual 'close' 'manual-host-update' $waitIdentity
    Wait-For { (Read-Json $attempt.report).choice -eq 'CloseAppsAndContinue' } 'manual-only host prompt'
    [IO.File]::WriteAllText($attempt.choice, 'cancel'); $null = Finish-Owned $attempt.owned @(130); $prompt = Read-Json $attempt.report
    Assert-Check ($prompt.isRetry -and $prompt.choice -eq 'Cancel' -and $prompt.applications.Count -eq 1 -and
        -not $prompt.applications[0].canRequestClose -and -not $app.owned.process.HasExited -and
        -not (Test-Path -LiteralPath (Join-Path $app.directory 'close-requests.json'))) 'An unrelated exact wait PID remains manual-only and never gains closure authority'
    Assert-Unchanged $manual $before 'Cancelling an unverified external host preserves the installed application'
    $started = [DateTime]::UtcNow
    $silent = Start-Apply $manual 'close' 'manual-host-headless' ($waitIdentity + '--no-prompt')
    $null = Finish-Owned $silent.owned @(1)
    Assert-Check (([DateTime]::UtcNow - $started).TotalSeconds -lt 20 -and
        (Read-Json $silent.status).error.Contains('Close the applications') -and
        -not $app.owned.process.HasExited) 'Headless unrelated-PID dependencies fail promptly with actionable closure instructions'
    Assert-Unchanged $manual $before 'Headless dependency failure preserves application bytes and receipt'
    Close-Probe $app

    $ownerFixture = Install-Fixture 'stalled-owner'; $app = Start-Probe $ownerFixture 'stalled-owner'
    $ownerStatus = Join-Path $run 'stalled-owner.watcher.json'
    $owner = Start-Owned (Join-Path $ownerFixture.target 'heirowUpdater.exe') @('after-exit', '--config', $ownerFixture.config,
        '--application-pid', [string]$app.owned.process.Id, '--application-start', [string]$app.owned.started,
        '--status-file', $ownerStatus) 'stalled-owner-watcher'
    Wait-For { (Read-Json $ownerStatus).status -eq 'waiting-for-exit' } 'the verified updater owner to remain active'
    $before = (Get-FileHash -LiteralPath $ownerFixture.receipt).Hash; $started = [DateTime]::UtcNow
    $attempt = Start-Apply $ownerFixture 'cancel' 'stalled-owner-update' @('--wait-pid', [string]$owner.process.Id, '--wait-start', [string]$owner.started)
    $null = Finish-Owned $attempt.owned @(130); $prompt = Read-Json $attempt.report
    Assert-Check (([DateTime]::UtcNow - $started).TotalSeconds -lt 20 -and
        @($prompt.applications | Where-Object { $_.processId -eq $owner.process.Id -and -not $_.canRequestClose }).Count -eq 1 -and
        -not $owner.process.HasExited) 'A stalled verified updater owner gets a cancellable manual-only dialog after a short grace'
    Assert-Unchanged $ownerFixture $before 'Cancelling the owner dependency preserves the installed application'
    $null = Invoke-Owned $PublishedUpdaterPath @('stop', '--config', $ownerFixture.config) 'stop-owned-watcher'
    $null = Finish-Owned $owner; Close-Probe $app

    $receipt.updaterSha256 = (Get-FileHash -LiteralPath $PublishedUpdaterPath).Hash
    $receipt.success = $true
}
catch { $receipt.error = $_.Exception.ToString(); throw }
finally {
    # Only generated fixture gates are signalled. The bounded probe also exits on
    # its own; cleanup never terminates an application or arbitrary process.
    foreach ($directory in @(Get-ChildItem -LiteralPath $run -Directory -Filter 'probe-*' -ErrorAction SilentlyContinue)) {
        if (Test-Path -LiteralPath (Join-Path $directory.FullName '.running-application-probe')) {
            [IO.File]::WriteAllText((Join-Path $directory.FullName 'allow-close'), 'acceptance cleanup')
        }
    }
    foreach ($fixture in $fixtures) {
        try { $null = Invoke-Owned $PublishedUpdaterPath @('stop', '--config', $fixture.config) "stop-$($fixture.name)" } catch { }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (@($processes | Where-Object { -not $_.process.HasExited }).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    foreach ($fixture in $fixtures) {
        if (Test-Path -LiteralPath $fixture.receipt) {
            try { $null = Invoke-Owned $CliPath @('uninstall', $productId, '--silent', '--isolated', '--state-root', $fixture.state, '--scope', 'user') "uninstall-$($fixture.name)" }
            catch { $cleanupErrors.Add($_.Exception.Message) }
        }
    }
    $receipt.cleanup.errors = $cleanupErrors
    $receipt.cleanup.activeOwnedProcesses = @($processes | Where-Object { -not $_.process.HasExited }).Count
    $receipt.cleanup.remainingReceipts = @($fixtures | Where-Object { Test-Path -LiteralPath $_.receipt }).Count
    $receipt.cleanup.machineStateChanged = $false; $receipt.cleanup.schedulingChanged = $false; $receipt.cleanup.registryChanged = $false
    if ($receipt.cleanup.activeOwnedProcesses -gt 0 -or $receipt.cleanup.remainingReceipts -gt 0 -or $cleanupErrors.Count -gt 0) { $receipt.success = $false }
    [IO.File]::WriteAllText((Join-Path $run 'receipt.json'), ($receipt | ConvertTo-Json -Depth 12))
    [ordered]@{ success = $receipt.success; checks = $checks.Count; receipt = (Join-Path $run 'receipt.json'); cleanup = $receipt.cleanup } | ConvertTo-Json -Depth 5
}
if (-not $receipt.success) { throw 'Updater acceptance cleanup did not complete; inspect the receipt before continuing.' }
