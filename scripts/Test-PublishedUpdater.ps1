#requires -Version 7.0
<#
.SYNOPSIS
Verifies self-update using the actual published self-extracting heirowUpdater.
.DESCRIPTION
Creates signed per-user 1.0.0 and 1.0.1 fixtures containing the supplied published
updater and CLI. A bounded loopback server serves only the feed and new package.
The installed updater must stage a trusted helper outside its install directory,
exit, and let that helper replace its signed payload. Forced bundle extraction
reproduces the difference between Environment.ProcessPath and AppContext.BaseDirectory.
No production product, machine state, registration, service or schedule is used.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedUpdaterPath,
    [Parameter(Mandatory)][string]$CliPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Published updater acceptance requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/windows-adapter-smoke' }
$PublishedUpdaterPath = [IO.Path]::GetFullPath($PublishedUpdaterPath)
$CliPath = [IO.Path]::GetFullPath($CliPath)
foreach ($required in @($PublishedUpdaterPath, $CliPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required published executable is missing: $required" }
}
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$runDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "published-updater-$runId"))
if (Test-Path -LiteralPath $runDirectory) { throw 'The unique acceptance directory already exists.' }
$public = Join-Path $runDirectory 'public'
$sandbox = Join-Path $runDirectory 'sandbox'
$state = Join-Path $sandbox 'state'
$target = Join-Path $sandbox 'application'
$downloads = Join-Path $sandbox 'downloads'
$extraction = Join-Path $sandbox 'bundle-extraction'
$statusPath = Join-Path $sandbox 'parent-status.json'
$installedUpdater = Join-Path $target 'heirowUpdater.exe'
$installedCli = Join-Path $target 'heirowInstaller.Cli.exe'
$productId = "published-updater-$runId"
$installedReceipt = Join-Path $state "$productId/receipt.json"
$receiptPath = Join-Path $runDirectory 'published-updater-receipt.json'
$checks = [Collections.Generic.List[string]]::new()
$receipt = [ordered]@{ success = $false; productId = $productId; scope = 'user'; isolated = $true; runDirectory = $runDirectory; checks = $checks; cleanup = [ordered]@{} }
$server = $null; $runner = $null; $helper = $null
$publishedHash = (Get-FileHash -LiteralPath $PublishedUpdaterPath -Algorithm SHA256).Hash.ToLowerInvariant()
$cliHash = (Get-FileHash -LiteralPath $CliPath -Algorithm SHA256).Hash.ToLowerInvariant()
New-Item -ItemType Directory -Path $runDirectory, $public, $sandbox -Force | Out-Null

function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $checks.Add($Description)
}
function Invoke-Child([string]$Executable, [string[]]$Arguments, [string]$Name) {
    . (Join-Path $PSScriptRoot 'Test-LegalFixture.ps1')
    $Arguments = Add-TestLegalArguments $Executable $Arguments
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.WorkingDirectory = $runDirectory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    $stdoutTask = $child.StandardOutput.ReadToEndAsync(); $stderrTask = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(180000)) { throw "$Name did not finish in three minutes." }
    $stdout = $stdoutTask.GetAwaiter().GetResult(); $stderr = $stderrTask.GetAwaiter().GetResult(); $code = $child.ExitCode
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.txt"), $stdout)
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.txt"), $stderr)
    $child.Dispose()
    if ($code -notin @(0, 3010)) { throw "$Name failed with exit code $code. $stderr" }
    return $stdout
}
function Invoke-Cli([string[]]$Arguments, [string]$Name) { Invoke-Child -Executable $CliPath -Arguments $Arguments -Name $Name }
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 90) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ([DateTime]::UtcNow -ge $until) { throw "Timed out: $Description" }
        Start-Sleep -Milliseconds 100
    }
}

try {
    # The server has no authentication, URL reservation, wildcard listener or
    # arbitrary-file route. Its public root never contains a signing key.
    $serverScript = Join-Path $runDirectory 'LoopbackFeed.ps1'
    [IO.File]::WriteAllText($serverScript, @'
param([string]$Root, [string]$Ready, [string]$Stop, [string]$Log)
$ErrorActionPreference = 'Stop'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
[IO.File]::WriteAllText($Ready, (@{ port = $listener.LocalEndpoint.Port } | ConvertTo-Json -Compress))
$deadline = [DateTime]::UtcNow.AddMinutes(5)
try {
    while (-not [IO.File]::Exists($Stop) -and [DateTime]::UtcNow -lt $deadline) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 30; continue }
        $client = $listener.AcceptTcpClient(); $client.ReceiveTimeout = 5000; $client.SendTimeout = 30000
        $stream = $client.GetStream()
        try {
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 8192, $true)
            try {
                $request = $reader.ReadLine(); $parts = @($request -split ' '); $agent = ''
                for ($count = 0; $count -lt 64; $count++) {
                    $line = $reader.ReadLine(); if ([string]::IsNullOrEmpty($line)) { break }
                    if ($line.StartsWith('User-Agent:', [StringComparison]::OrdinalIgnoreCase)) { $agent = $line.Substring(11).Trim() }
                }
            } finally { $reader.Dispose() }
            $requestPath = if ($parts.Count -gt 1) { $parts[1].Split('?', 2)[0] } else { '' }
            $name = if ($parts.Count -gt 1 -and $parts[0] -eq 'GET') {
                switch ($requestPath) { '/update.json' { 'update.json' }; '/fixture.hinst' { 'fixture.hinst' }; default { $null } }
            } else { $null }
            if (-not $name) {
                $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 404 Not Found`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
                $stream.Write($header, 0, $header.Length); continue
            }
            $inputFile = [IO.File]::OpenRead([IO.Path]::Combine($Root, $name))
            try {
                $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: application/octet-stream`r`nContent-Length: $($inputFile.Length)`r`nConnection: close`r`n`r`n")
                $stream.Write($header, 0, $header.Length); $inputFile.CopyTo($stream); $stream.Flush()
                [IO.File]::AppendAllText($Log, ((@{ path = $requestPath; requestTarget = $parts[1]; size = $inputFile.Length; userAgent = $agent; timestamp = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Compress) + [Environment]::NewLine))
            } finally { $inputFile.Dispose() }
        } catch { [IO.File]::AppendAllText($Log, ((@{ error = $_.Exception.GetType().Name } | ConvertTo-Json -Compress) + [Environment]::NewLine)) }
        finally { $stream.Dispose(); $client.Dispose() }
    }
} finally { $listener.Stop() }
'@)
    $ready = Join-Path $runDirectory 'server-ready.json'; $stop = Join-Path $runDirectory 'server-stop.flag'; $requestLog = Join-Path $runDirectory 'server-requests.jsonl'
    $serverStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $serverStart.UseShellExecute = $false; $serverStart.CreateNoWindow = $true; $serverStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $serverScript, '-Root', $public, '-Ready', $ready, '-Stop', $stop, '-Log', $requestLog)) { $serverStart.ArgumentList.Add($argument) }
    $server = [Diagnostics.Process]::Start($serverStart)
    Wait-For { Test-Path -LiteralPath $ready } 'the bounded loopback server' 15
    $port = (Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json).port
    $origin = "http://127.0.0.1:$port"

    # The ephemeral key is a sibling of both payload roots and public files.
    $keyPath = Join-Path $runDirectory 'acceptance-signing-key.pem'
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try { [IO.File]::WriteAllText($keyPath, $rsa.ExportPkcs8PrivateKeyPem()) } finally { $rsa.Dispose() }
    foreach ($version in @('1.0.0', '1.0.1')) {
        $source = Join-Path $runDirectory "source-$version"
        New-Item -ItemType Directory -Path $source -Force | Out-Null
        $updaterCopy = Join-Path $source 'heirowUpdater.exe'
        Copy-Item -LiteralPath $PublishedUpdaterPath -Destination $updaterCopy
        Copy-Item -LiteralPath $CliPath -Destination (Join-Path $source 'heirowInstaller.Cli.exe')
        if ($version -eq '1.0.0') {
            # This benign overlay changes the running image hash without changing
            # its .NET bundle or application behavior. The update must replace it.
            $overlay = [Text.Encoding]::UTF8.GetBytes("`nheirowUpdater acceptance old image $runId`n")
            $file = [IO.File]::Open($updaterCopy, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $file.Write($overlay, 0, $overlay.Length) } finally { $file.Dispose() }
        }
        [IO.File]::WriteAllText((Join-Path $source 'generation.txt'), $version)
        $manifest = [ordered]@{ formatVersion = 1; id = $productId; name = 'Published updater acceptance'; version = $version; publisher = 'heirowInstaller acceptance'; scope = 'user'; entryPoint = 'heirowUpdater.exe'; updateUrl = "$origin/update.json"; features = @(@{id = 'core'; name = 'Application'; required = $true}); payload = @(@{source = 'heirowUpdater.exe'; path = 'heirowUpdater.exe'; feature = 'core'}, @{source = 'heirowInstaller.Cli.exe'; path = 'heirowInstaller.Cli.exe'; feature = 'core'}, @{source = 'generation.txt'; path = 'generation.txt'; feature = 'core'}) }
        $manifestPath = Join-Path $runDirectory "manifest-$version.json"
        [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8))
        $null = Invoke-Cli @('pack', $manifestPath, '--source', $source, '--output', (Join-Path $runDirectory "fixture-$version.hinst"), '--sign-key', $keyPath) "pack-$version"
    }
    $oldPackage = Join-Path $runDirectory 'fixture-1.0.0.hinst'
    $newPackage = Join-Path $runDirectory 'fixture-1.0.1.hinst'
    $inspection = (Invoke-Cli @('inspect', $oldPackage) 'inspect-old') | ConvertFrom-Json
    $pin = $inspection.publisherKeySha256
    Assert-Check ($inspection.signed -and $pin.Length -eq 64) 'The fixture has a real RSA-PSS publisher identity with its private key outside the payload'
    Copy-Item -LiteralPath $newPackage -Destination (Join-Path $public 'fixture.hinst')
    $feed = [ordered]@{ productId = $productId; name = 'Published updater acceptance'; publisher = 'heirowInstaller acceptance'; version = '1.0.1'; installerUrl = "$origin/fixture.hinst"; sha256 = (Get-FileHash -LiteralPath $newPackage -Algorithm SHA256).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $newPackage).Length; packageType = 'hinst'; releaseNotes = 'Isolated self-extracting updater acceptance'; arguments = @('--silent') }
    [IO.File]::WriteAllText((Join-Path $public 'update.json'), ($feed | ConvertTo-Json -Depth 6))
    $null = Invoke-Cli @('install', $oldPackage, '--silent', '--isolated', '--state-root', $state, '--target', $target, '--scope', 'user', '--no-register', '--trust-key', $pin, '--require-signature', '--features', 'core') 'install-old'
    $before = Get-Content -LiteralPath $installedReceipt -Raw | ConvertFrom-Json
    $oldHash = (Get-FileHash -LiteralPath $installedUpdater -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Check ($before.version -eq '1.0.0' -and $before.publisherKeySha256 -eq $pin -and $before.isolated -and -not $before.registeredProduct -and $oldHash -ne $publishedHash) 'The installed isolated 1.0.0 updater has the intended pin and a distinct old executable image'

    $start = [Diagnostics.ProcessStartInfo]::new($installedUpdater)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.WorkingDirectory = $target
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extraction
    foreach ($argument in @('update', '--all', '--isolated', '--allow-loopback', '--state-root', $state, '--download-directory', $downloads, '--status-file', $statusPath, '--json')) { $start.ArgumentList.Add($argument) }
    $runner = [Diagnostics.Process]::Start($start)
    $receipt.parentProcessId = $runner.Id
    $stdoutTask = $runner.StandardOutput.ReadToEndAsync(); $stderrTask = $runner.StandardError.ReadToEndAsync()
    Wait-For { $runner.HasExited } 'the installed published updater to stage its own update and exit' 120
    $stdout = $stdoutTask.GetAwaiter().GetResult(); $stderr = $stderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $runDirectory 'installed-updater.stdout.json'), $stdout)
    [IO.File]::WriteAllText((Join-Path $runDirectory 'installed-updater.stderr.txt'), $stderr)
    $rows = @($stdout | ConvertFrom-Json)
    $row = $rows | Where-Object { $_.productId -eq $productId } | Select-Object -First 1
    Assert-Check ($runner.ExitCode -eq 0 -and $row.status -eq 'update-staged' -and $row.requiresApplicationExit -and $row.helperProcessId -gt 0) 'The actual installed single-file updater reported update-staged and required its own exit'
    $receipt.helperProcessId = $row.helperProcessId
    try { $helper = [Diagnostics.Process]::GetProcessById($row.helperProcessId) } catch [ArgumentException] { }
    $extractedAssembly = @(Get-ChildItem -LiteralPath $extraction -Filter 'heirowUpdater.dll' -File -Recurse -ErrorAction SilentlyContinue)
    Assert-Check ($extractedAssembly.Count -gt 0 -and -not $extractedAssembly[0].FullName.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Forced .NET bundle extraction occurred outside the installed executable directory'
    $receipt.extractedAssembly = $extractedAssembly[0].FullName
    $parentStatus = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    Assert-Check ($parentStatus.transport -eq 'SocketJack' -and $parentStatus.products[0].status -eq 'update-staged') 'The caller-selected parent status file recorded the real SocketJack staged result'

    Wait-For { @(Get-ChildItem -LiteralPath $downloads -Directory -Filter 'h-*').Count -eq 1 } 'the trusted helper outside the installation' 15
    $helperDirectory = @(Get-ChildItem -LiteralPath $downloads -Directory -Filter 'h-*')[0].FullName
    $helperExe = Join-Path $helperDirectory 'heirowUpdater.exe'
    $helperCli = Join-Path $helperDirectory 'heirowInstaller.Cli.exe'
    Assert-Check (-not $helperDirectory.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Get-FileHash -LiteralPath $helperExe -Algorithm SHA256).Hash.ToLowerInvariant() -eq $oldHash -and (Get-FileHash -LiteralPath $helperCli -Algorithm SHA256).Hash.ToLowerInvariant() -eq $cliHash) 'The staged trusted helper and maintenance CLI were copied from the actual installed host directory'
    $helperStatusPath = Join-Path $helperDirectory 'helper-status.json'
    Wait-For { Test-Path -LiteralPath $helperStatusPath } 'the copied trusted helper to finish applying the signed update' 120
    $helperStatus = Get-Content -LiteralPath $helperStatusPath -Raw | ConvertFrom-Json
    Assert-Check ($helperStatus.status -eq 'updated' -and $helperStatus.result.version -eq '1.0.1') 'The trusted helper completed installation of the signed 1.0.1 release'
    if ($helper) { Wait-For { $helper.HasExited } 'the copied trusted helper to exit' 30 }
    $after = Get-Content -LiteralPath $installedReceipt -Raw | ConvertFrom-Json
    Assert-Check ($after.version -eq '1.0.1' -and $after.publisherKeySha256 -eq $pin -and $after.scope -eq 'user' -and $after.isolated -and -not $after.registeredProduct -and $after.features.Count -eq 1 -and $after.features[0] -eq 'core') 'The replacement receipt preserved publisher pin, scope, isolation and feature choices'
    Assert-Check ((Get-FileHash -LiteralPath $installedUpdater -Algorithm SHA256).Hash.ToLowerInvariant() -eq $publishedHash -and (Get-FileHash -LiteralPath $installedCli -Algorithm SHA256).Hash.ToLowerInvariant() -eq $cliHash -and [IO.File]::ReadAllText((Join-Path $target 'generation.txt')) -eq '1.0.1') 'The helper replaced the old running image with the verified updater, CLI and new payload generation'
    $requests = @(Get-Content -LiteralPath $requestLog | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-Check (@($requests | Where-Object { $_.path -eq '/update.json' -and $_.userAgent -like '*SocketJack*' }).Count -gt 0 -and @($requests | Where-Object { $_.path -eq '/fixture.hinst' -and $_.userAgent -like '*SocketJack*' }).Count -gt 0) 'Both the feed and package traveled through the actual SocketJack transport'
    Assert-Check (@($requests | Where-Object { $_.path -eq '/update.json' -and $_.requestTarget -like '*fromVersion=1.0.0*' -and $_.requestTarget -like '*updateProtocol=hupd1*' }).Count -gt 0) 'The actual published client selects its installed base while the fixture routes the feed path separately from its query'
    Assert-Check ((Get-FileHash -LiteralPath $PublishedUpdaterPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $publishedHash -and (Get-FileHash -LiteralPath $CliPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $cliHash) 'The supplied published updater and CLI remained byte-for-byte unchanged'
    $null = Invoke-Cli @('uninstall', $productId, '--silent', '--state-root', $state, '--scope', 'user') 'uninstall-updated-fixture'
    Assert-Check (-not (Test-Path -LiteralPath $installedUpdater) -and -not (Test-Path -LiteralPath $installedCli) -and -not (Test-Path -LiteralPath $installedReceipt)) 'The unique fixture uninstalled with no active receipt or target executables remaining'
    $receipt.publisherKeySha256 = $pin; $receipt.oldUpdaterSha256 = $oldHash; $receipt.newUpdaterSha256 = $publishedHash; $receipt.cliSha256 = $cliHash; $receipt.helperDirectory = $helperDirectory
    $receipt.success = $true
} catch { $receipt.error = $_.Exception.ToString(); throw }
finally {
    if ($server) {
        [IO.File]::WriteAllText((Join-Path $runDirectory 'server-stop.flag'), 'stop')
        if (-not $server.WaitForExit(10000)) { $server.Kill(); $server.WaitForExit() }
        $receipt.cleanup.loopbackServerExited = $server.HasExited; $server.Dispose()
    }
    # A failing test preserves an in-flight installer and its diagnostics. It
    # never kills a helper transaction or deletes a target behind that helper.
    $receipt.cleanup.installedUpdaterAbsent = -not (Test-Path -LiteralPath $installedUpdater)
    $receipt.cleanup.installedCliAbsent = -not (Test-Path -LiteralPath $installedCli)
    $receipt.cleanup.installedReceiptAbsent = -not (Test-Path -LiteralPath $installedReceipt)
    $receipt.cleanup.activeFixtureProcesses = @(Get-CimInstance Win32_Process -Filter "Name='heirowUpdater.exe' OR Name='heirowInstaller.Cli.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($runDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count
    $receipt.cleanup.machineStateChanged = $false; $receipt.cleanup.registryChanged = $false; $receipt.cleanup.schedulingChanged = $false
    $receipt.cleanup.evidenceAndEphemeralKeyRetained = $true
    $cleanupFailed = $receipt.success -and ($receipt.cleanup.activeFixtureProcesses -ne 0 -or -not $receipt.cleanup.installedReceiptAbsent -or -not $receipt.cleanup.installedUpdaterAbsent -or -not $receipt.cleanup.installedCliAbsent)
    if ($cleanupFailed) { $receipt.success = $false; $receipt.error = 'Fixture cleanup verification failed.' }
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 10))
    Write-Output ([ordered]@{ success = $receipt.success; checks = $checks.Count; receipt = $receiptPath; cleanup = $receipt.cleanup } | ConvertTo-Json -Depth 5)
    if ($cleanupFailed) { throw 'Published updater fixture cleanup verification failed.' }
}
