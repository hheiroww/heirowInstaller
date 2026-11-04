#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LauncherPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedLauncherSha256,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PackageCache
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'The windowless graphics handoff requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$phase = 'inputs'
$createdOutput = $false
$launcherLease = $null
function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw [IO.InvalidDataException]::new($Message) } }
function NoLinks([string]$Path) {
    Require ([IO.Path]::IsPathFullyQualified($Path)) 'Use absolute input and output paths.'
    for ($part = [IO.Path]::GetFullPath($Path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if ([IO.File]::Exists($part) -or [IO.Directory]::Exists($part)) {
            Require (([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse-point inputs and outputs are not permitted.'
        }
    }
}
function Save([string]$Name, $Data) {
    $path = Join-Path $OutputDirectory $Name
    Require (!(Test-Path -LiteralPath $path)) 'Preserve existing test evidence.'
    [IO.File]::WriteAllText($path, ($Data | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
function RunOwned([string]$Name, [string]$Executable, [string[]]$Arguments, [Collections.IDictionary]$Environment = @{}, [string]$WorkingDirectory = $OutputDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    Require ($WorkingDirectory -ceq $OutputDirectory -or $WorkingDirectory.StartsWith($OutputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'The owned process working directory escaped its output root.'
    NoLinks $WorkingDirectory
    $start.WorkingDirectory = $WorkingDirectory
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    # Suppress native .NET-host error dialogs before the first instruction runs.
    $start.Environment['DOTNET_DISABLE_GUI_ERRORS'] = '1'
    foreach ($key in $Environment.Keys) {
        Require ($key -ceq 'DOTNET_BUNDLE_EXTRACT_BASE_DIR') 'Only the reviewed bundle extraction override may be supplied.'
        $start.Environment[$key] = [string]$Environment[$key]
    }
    $process = $null
    try {
        $started = [DateTimeOffset]::UtcNow
        $process = [Diagnostics.Process]::Start($start)
        Require ($null -ne $process) 'The owned test process did not start.'
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $timedOut = $false
        while (!$process.WaitForExit(1000)) {
            if ($watch.Elapsed.TotalMinutes -ge 10) {
                $timedOut = $true
                # Only this test's own process tree may be stopped on timeout.
                try { $process.Kill($true) } catch { }
                [void]$process.WaitForExit(30000)
                break
            }
        }
        Require ($process.HasExited) 'The timed-out owned process has not exited; preserve its output for inspection.'
        Require ($stdout.Wait([TimeSpan]::FromSeconds(30)) -and $stderr.Wait([TimeSpan]::FromSeconds(30))) 'The owned process streams did not finish.'
        [IO.File]::WriteAllText((Join-Path $OutputDirectory ($Name + '.stdout.log')), $stdout.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $OutputDirectory ($Name + '.stderr.log')), $stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        $record = [pscustomobject]@{ processId = $process.Id; exitCode = $process.ExitCode; timedOut = $timedOut; startedUtc = $started.ToString('O'); elapsedSeconds = $watch.Elapsed.TotalSeconds; timeoutSeconds = 600; useShellExecute = $false; createNoWindow = $true; windowStyle = 'Hidden'; nativeGuiErrorsDisabled = $true }
        Save ($Name + '-process.json') $record
        Require (!$timedOut -and $record.exitCode -eq 0) 'An owned graphics-handoff operation failed; inspect its retained logs.'
        return $record
    } finally { if ($null -ne $process) { $process.Dispose() } }
}
function Inventory {
    $files = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Force -File | ForEach-Object {
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($OutputDirectory, $_.FullName); bytes = $_.Length }
    } | Sort-Object path)
    Save 'outputs.json' ([ordered]@{ root = $OutputDirectory; files = $files; logicalBytesExcludingThisInventory = ($files | Measure-Object bytes -Sum).Sum; thisInventory = 'outputs.json'; finalReceipt = 'result.json'; retained = $true; cleanupApproved = $false; containsSyntheticFixturePrivateKey = $true; publicArtifactUploadRequiresReceiptAllowlist = $true })
}
try {
    Require ([IO.Path]::IsPathFullyQualified($LauncherPath) -and [IO.Path]::IsPathFullyQualified($OutputDirectory)) 'Use absolute reviewed launcher and fresh owned output paths.'
    $LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
    $OutputDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
    Require ([IO.Path]::GetPathRoot($OutputDirectory).Length -eq 3 -and $OutputDirectory[1] -eq ':') 'Use a local Windows drive for the MAX_PATH regression.'
    NoLinks $LauncherPath; NoLinks $OutputDirectory
    Require (!(Test-Path -LiteralPath $OutputDirectory)) 'Use a fresh owned output directory; this test never removes previous outputs.'
    Require ([IO.Path]::GetExtension($LauncherPath) -ieq '.exe') 'Use the reviewed published primary EXE.'
    if ($PackageCache) { Require ([IO.Path]::IsPathFullyQualified($PackageCache)) 'Use an absolute package cache path.'; $PackageCache = [IO.Path]::GetFullPath($PackageCache); NoLinks $PackageCache }
    # Reproduce the observed root/cache lengths on local runs and CI alike.
    $padding = 93 - $OutputDirectory.Length - 1 - 'fixture-'.Length
    Require ($padding -ge 0) 'Choose an output base of at most 84 characters so the fixture root can retain the observed 93-character case.'
    $fixture = Join-Path $OutputDirectory ('fixture-' + ('x' * $padding))
    $cacheRoot = Join-Path $fixture 'graphics-context\graphics-hosts'
    $fontTail = 'heirowInstaller.Dx11\OqJA6ni2Cxym\ThirdParty\ProjectZSans\OFL.txt'
    $oldFont = Join-Path (Join-Path $cacheRoot (('0' * 64) + '\bundle')) $fontTail
    $newFont = Join-Path (Join-Path $cacheRoot 'bundle') $fontTail
    Require ($fixture.Length -eq 93 -and $cacheRoot.Length -eq 125 -and $oldFont.Length -eq 263 -and $newFont.Length -eq 198 -and $oldFont.Length -gt 260 -and $newFont.Length -lt 260) 'The fixture must reproduce the same long-root font extraction regression.'
    $launcherLease = [IO.File]::Open($LauncherPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Require ($launcherLease.Length -gt 0 -and $launcherLease.Length -le 1GB) 'The reviewed launcher exceeds the bounded fixture input size.'
    $launcherSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($launcherLease)); $launcherLease.Position = 0
    Require ($launcherSha -ieq $ExpectedLauncherSha256) 'The published launcher differs from its reviewed SHA-256.'
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($OutputDirectory))
    $estimatedPeak = 5 * $launcherLease.Length + 512MB
    Require ($drive.AvailableFreeSpace -ge $estimatedPeak) 'Insufficient space for one fixture and its owned runtime/maintenance extraction.'
    [void][IO.Directory]::CreateDirectory($OutputDirectory); $createdOutput = $true
    Save 'inputs.json' ([ordered]@{ launcher = @{ path = $LauncherPath; bytes = $launcherLease.Length; sha256 = $launcherSha }; output = $OutputDirectory; fixture = $fixture; packageCache = $PackageCache; estimatedPeakBytes = $estimatedPeak; freeBytesBefore = $drive.AvailableFreeSpace; oneFixtureSetupCopy = $true; noCleanup = $true })
    Save 'path-regression-plan.json' ([ordered]@{ fixtureRootCharacters = $fixture.Length; cacheRootCharacters = $cacheRoot.Length; previousFontPath = $oldFont; previousFontCharacters = $oldFont.Length; correctedFontPath = $newFont; correctedFontCharacters = $newFont.Length; sameFixtureRoot = $true })

    $phase = 'build-fixture-generator'
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    NoLinks $dotnet
    $build = Join-Path $OutputDirectory 'generator-build'
    $project = Join-Path $projectRoot 'tests\HeirowInstaller.GraphicsHandoff.Fixture\HeirowInstaller.GraphicsHandoff.Fixture.csproj'
    $buildArgs = @('build', $project, '-c', 'Release', '--artifacts-path', $build, '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '--nologo', '-v', 'minimal')
    if ($PackageCache) { $buildArgs += '-p:RestorePackagesPath=' + $PackageCache }
    [void](RunOwned 'build' $dotnet $buildArgs)
    $generator = Join-Path $build 'bin\HeirowInstaller.GraphicsHandoff.Fixture\release\HeirowInstaller.GraphicsHandoff.Fixture.dll'
    NoLinks $generator
    $phase = 'prepare-signed-data-fixture'
    [void](RunOwned 'prepare' $dotnet @($generator, $LauncherPath, $ExpectedLauncherSha256, $fixture))
    $invocationPath = Join-Path $fixture 'fixture-invocation.json'
    $bindingPath = Join-Path $fixture 'graphics-handoff-fixture.json'
    foreach ($path in @($invocationPath, $bindingPath)) { NoLinks $path; Require ((Get-Item -LiteralPath $path).Length -le 16384) 'Fixture metadata exceeds its bounded schema.' }
    $invocation = Get-Content -LiteralPath $invocationPath -Raw | ConvertFrom-Json
    $binding = Get-Content -LiteralPath $bindingPath -Raw | ConvertFrom-Json
    $setup = Join-Path $fixture 'fixture-setup.exe'
    Require (@(Get-ChildItem -LiteralPath $fixture -File -Filter '*.exe').Count -eq 1) 'Preparation must create exactly one fixture setup copy.'
    Require ($invocation.executable -ceq $setup -and $invocation.workingDirectory -ceq $fixture -and $invocation.useShellExecute -eq $false -and $invocation.createNoWindow -eq $true) 'The fixture invocation changed its executable or windowless contract.'
    $envFields = @($invocation.environment.PSObject.Properties)
    Require ($envFields.Count -eq 1 -and $envFields[0].Name -ceq 'DOTNET_BUNDLE_EXTRACT_BASE_DIR' -and $envFields[0].Value -ceq (Join-Path $fixture 'primary-bundle')) 'Only the owned primary bundle extraction environment is allowed.'
    $expectedArgs = @('--graphics-handoff-smoke', $fixture, '--isolated', '--no-register', '--state-root', (Join-Path $fixture 'graphics-context'), '--target', (Join-Path $fixture 'installed'), '--scope', 'machine', '--trust-key', $binding.publisherKeySha256, '--require-signature', '--expected-product-id', 'heirowInstaller.wizard-fixture.graphics-handoff', '--expected-version', '1.0.0', '--expected-sha256', $binding.primarySha256)
    Require (@($invocation.arguments).Count -eq $expectedArgs.Count) 'The fixture argument closure changed.'
    for ($index = 0; $index -lt $expectedArgs.Count; $index++) { Require ($invocation.arguments[$index] -is [string] -and $invocation.arguments[$index] -ceq $expectedArgs[$index]) 'The fixture argument closure changed.' }

    $phase = 'actual-two-image-worker-handoff'
    $execution = RunOwned 'fixture' $setup ([string[]]$invocation.arguments) @{ DOTNET_BUNDLE_EXTRACT_BASE_DIR = [string]$envFields[0].Value } $fixture
    $reportPath = Join-Path $fixture 'graphics-handoff-result.json'
    NoLinks $reportPath
    Require ((Get-Item -LiteralPath $reportPath).Length -le 131072) 'The handoff result exceeds its bounded schema.'
    $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    Require ($result.result -ceq 'PASS' -and $result.product -ceq 'heirowInstaller.wizard-fixture.graphics-handoff' -and $result.version -ceq '1.0.0' -and $result.scope -ceq 'machine' -and $result.isolated -eq $true -and $result.actualWorkerTransactionVerified -eq $true -and $result.installedPayloadFiles -eq 1) 'The actual isolated worker transaction did not pass.'
    Require ($result.originalPrimaryProcessId -eq $execution.processId -and $result.actualCompanionProcessId -ne $execution.processId -and $result.originalPrimaryWorker.completed -eq $true -and $result.originalPrimaryWorker.isolated -eq $true -and $result.originalPrimaryWorker.processId -ne $result.actualCompanionProcessId -and $result.originalPrimaryWorker.imageSha256 -ieq $binding.primarySha256) 'The real companion/original-primary-worker process binding differs.'
    foreach ($name in @('registryWrites', 'productionWrites', 'applicationLaunch', 'nativeGraphicsLoaded', 'inputInjected', 'capture', 'uacRequested')) { Require ($result.$name -is [bool] -and $result.$name -eq $false) 'The handoff exceeded its no-production, no-UI contract.' }
    Require ($result.windowsShown -eq 0 -and $result.outputsRetained -eq $true) 'The handoff opened windows or failed to retain evidence.'
    $cases = @('stale-parent', 'parent-image', 'nonce', 'digest', 'destination', 'state-root', 'oversize')
    Require (@($result.rejectedProtocolCases).Count -eq $cases.Count) 'The seven negative protocol cases were not completed.'
    foreach ($case in $cases) { Require (@($result.rejectedProtocolCases | Where-Object { $_ -ceq ('The actual windowless worker rejected ' + $case + ' input before changing files or its receipt') }).Count -eq 1) 'A required worker protocol rejection is missing or duplicated.' }
    $expectedReceipt = Join-Path $fixture 'state\heirowInstaller.wizard-fixture.graphics-handoff\receipt.json'
    Require ($result.receiptPath -ceq $expectedReceipt -and (Test-Path -LiteralPath $expectedReceipt -PathType Leaf)) 'The worker receipt escaped or did not reach its owned fixture state.'

    $phase = 'actual-long-root-extraction'
    $fontRoot = Join-Path $cacheRoot 'bundle\heirowInstaller.Dx11'
    $fonts = @(Get-ChildItem -LiteralPath $fontRoot -Recurse -File -Filter 'OFL.txt' | Where-Object { $_.FullName.EndsWith('\ThirdParty\ProjectZSans\OFL.txt', [StringComparison]::OrdinalIgnoreCase) })
    Require ($fonts.Count -eq 1) 'The actual companion font extraction was not found exactly once.'
    NoLinks $fonts[0].FullName
    $relativeFont = [IO.Path]::GetRelativePath((Join-Path $cacheRoot 'bundle'), $fonts[0].FullName)
    $cachedHosts = @(Get-ChildItem -LiteralPath $cacheRoot -Directory | Where-Object { $_.Name -match '^[a-fA-F0-9]{64}$' })
    Require ($cachedHosts.Count -eq 1) 'The fixture did not retain one immutable version-keyed companion.'
    $actualOldFont = Join-Path (Join-Path $cachedHosts[0].FullName 'bundle') $relativeFont
    Require ($actualOldFont.Length -gt 260 -and $fonts[0].FullName.Length -lt 260 -and $actualOldFont.Length - $fonts[0].FullName.Length -eq 65) 'The actual long-root run did not exercise the corrected font extraction boundary.'
    Save 'path-regression.json' ([ordered]@{ result = 'PASS'; fixtureRoot = $fixture; fixtureRootCharacters = $fixture.Length; cacheRootCharacters = $cacheRoot.Length; actualFontPath = $fonts[0].FullName; actualFontCharacters = $fonts[0].FullName.Length; previousEquivalentFontPath = $actualOldFont; previousEquivalentFontCharacters = $actualOldFont.Length; sameLongFixture = $true; actualExtractionCompleted = $true })
    $phase = 'complete'
    Inventory
    Save 'result.json' ([ordered]@{ result = 'PASS'; launcherSha256 = $launcherSha; fixtureResult = $reportPath; fixtureResultSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash; actualTwoImageWorkerTransaction = $true; negativeProtocolCases = $cases; longRootExtractionVerified = $true; windowsShown = 0; uacRequested = $false; productionInstall = $false; authentication = $false; httpPayloadUploadedBytes = 0; outputsRetained = $true; cleanupApproved = $false; inventory = 'outputs.json'; finishedUtc = [DateTimeOffset]::UtcNow.ToString('O') })
    Write-Host ('PASS actual windowless graphics handoff, seven protocol rejections, and long-root extraction. Retained output: ' + $OutputDirectory)
} catch {
    if ($createdOutput -and !(Test-Path -LiteralPath (Join-Path $OutputDirectory 'result.json'))) {
        Save 'result.json' ([ordered]@{ result = 'STOPPED'; phase = $phase; exceptionType = $_.Exception.GetType().FullName; verificationComplete = $false; outputsRetained = $true; cleanupApproved = $false })
        try { Inventory } catch { }
    }
    throw
} finally { if ($null -ne $launcherLease) { $launcherLease.Dispose() } }
