#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LauncherPath,
    [Parameter(Mandatory)][string]$CliPath,
    [string]$OutputDirectory,
    [ValidateSet('install-closed','install-open','repair','uninstall')][string[]]$CaseNames = @('install-closed','install-open','repair','uninstall'),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$CliPath = [IO.Path]::GetFullPath($CliPath)
foreach ($path in @($LauncherPath,$CliPath)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required build is missing: $path" } }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/app-wizard-' + [Guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Wizard acceptance requires a fresh output directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$clearEnvironment = @('HEIROWUPDATER_TOKEN','HEIROWUPDATER_COOKIE','SOCKETJACK_DOWNLOAD_TOKEN','SOCKETJACK_DOWNLOAD_COOKIE','SOCKETJACK_PUBLISHER_TOKEN','SOCKETJACK_PUBLISHER_COOKIE','HEIROWINSTALLER_SIGNING_KEY_PATH')
function Invoke-OwnedProcess([string]$image,[string[]]$arguments,[string]$log,[hashtable]$environment=@{}) {
    $start = [Diagnostics.ProcessStartInfo]::new($image)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $image -Parent
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in $clearEnvironment) { $start.Environment.Remove($name) | Out-Null }
    foreach ($pair in $environment.GetEnumerator()) { $start.Environment[$pair.Key] = $pair.Value }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) { throw "Owned acceptance process $($process.Id) did not finish. It was left intact; no process was terminated." }
        [IO.File]::WriteAllText($log + '.stdout.log',$stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText($log + '.stderr.log',$stderr.GetAwaiter().GetResult())
        if ($process.ExitCode -ne 0) { throw "Owned acceptance process returned $($process.ExitCode). Inspect $log and its wizard receipt." }
    } finally { $process.Dispose() }
}
function Global-FileMetadata {
    $paths = @(
        (Join-Path $env:LOCALAPPDATA 'SocketJack/heirowUpdater/last-check.json'),
        (Join-Path $env:LOCALAPPDATA 'heirowInstaller/desktop.json'),
        (Join-Path $env:LOCALAPPDATA 'SocketJack/heirowInstaller/state/heirowinstaller/receipt.json')
    )
    @($paths | ForEach-Object { [ordered]@{ path=$_; exists=(Test-Path -LiteralPath $_); sha256=$(if (Test-Path -LiteralPath $_ -PathType Leaf) { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } else { $null }) } })
}
function Add-InMemoryFixtureSignature([string]$path,[Security.Cryptography.RSA]$signer) {
    $archive = [IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Update)
    try {
        $manifestStream = $archive.GetEntry('manifest.json').Open()
        $memory = [IO.MemoryStream]::new()
        try { $manifestStream.CopyTo($memory); $manifestBytes=$memory.ToArray() } finally { $manifestStream.Dispose(); $memory.Dispose() }
        $signature = [ordered]@{ algorithm='RSA-PSS-SHA256'; publicKey=$signer.ExportSubjectPublicKeyInfoPem(); signature=[Convert]::ToBase64String($signer.SignData($manifestBytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)) }
        $signatureStream = $archive.CreateEntry('signature.json').Open()
        $writer = [IO.StreamWriter]::new($signatureStream,[Text.UTF8Encoding]::new($false))
        try { $writer.Write(($signature | ConvertTo-Json -Depth 5)) } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
}
$globalBefore = Global-FileMetadata
$globalBefore | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'global-before.json') -Encoding utf8
$probe = Join-Path $OutputDirectory 'probe'
& dotnet publish (Join-Path $projectRoot 'tests/HeirowInstaller.Tests/Fixtures/WizardApplicationProbe/WizardApplicationProbe.csproj') -c $Configuration -r win-x64 --self-contained false -o $probe -m:1 -p:UseSharedCompilation=false -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'The owned wizard application probe did not publish.' }
$source = Join-Path $OutputDirectory 'payload'
New-Item -ItemType Directory -Path (Join-Path $source 'application') -Force | Out-Null
foreach ($name in @('WizardApplicationProbe.exe','WizardApplicationProbe.dll','WizardApplicationProbe.deps.json','WizardApplicationProbe.runtimeconfig.json')) { Copy-Item -LiteralPath (Join-Path $probe $name) -Destination (Join-Path $source ('application/' + $name)) }
[IO.File]::WriteAllText((Join-Path $source 'application/.wizard-application-fixture'),"heirowInstaller wizard application acceptance v1`n")
[IO.File]::WriteAllText((Join-Path $source 'notes.txt'),"Wizard application payload.`n")
[IO.File]::WriteAllText((Join-Path $source 'readme.txt'),"Optional application documentation.`n")
[IO.File]::WriteAllText((Join-Path $source 'LICENSE.txt'),"Isolated wizard acceptance license. Permission is granted to install this dedicated test application.`n")
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets/heirowInstaller.png') -Destination (Join-Path $source 'app.png')
$payload = @(
    @{ source='notes.txt'; path='notes.txt'; feature='core' },
    @{ source='readme.txt'; path='readme.txt'; feature='docs' },
    @{ source='LICENSE.txt'; path='LICENSE.txt'; feature='core' },
    @{ source='app.png'; path='app.png'; feature='core' }
)
foreach ($name in @('WizardApplicationProbe.exe','WizardApplicationProbe.dll','WizardApplicationProbe.deps.json','WizardApplicationProbe.runtimeconfig.json','.wizard-application-fixture')) { $payload += @{ source=('application/' + $name); path=('application/' + $name); feature='core' } }
# Enough real, streamed file work to observe the live progress UI without delaying the engine.
New-Item -ItemType Directory -Path (Join-Path $source 'data') | Out-Null
$zeroBlock = [byte[]]::new(65536)
for ($part = 0; $part -lt 16; $part++) {
    $relative = 'data/part-' + $part.ToString('00') + '.bin'
    $stream = [IO.File]::Create((Join-Path $source $relative))
    try { for ($block = 0; $block -lt 32; $block++) { $stream.Write($zeroBlock,0,$zeroBlock.Length) } } finally { $stream.Dispose() }
    $payload += @{ source=$relative; path=$relative; feature='core' }
}
$manifest = [ordered]@{
    id='heirowInstaller.wizard-fixture'; name='SocketJack Notes'; version='1.0.0'; publisher='SocketJack acceptance';
    description='A small, isolated application for verifying the complete setup experience.';
    entryPoint='application/WizardApplicationProbe.exe'; licenseFile='LICENSE.txt';
    features=@(@{ id='core'; name='SocketJack Notes'; description='The required application files.'; required=$true },@{ id='docs'; name='Documentation'; description='Optional application documentation.'; default=$true });
    payload=$payload
}
$manifestPath = Join-Path $OutputDirectory 'installer.json'
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8
$package = Join-Path $OutputDirectory 'wizard.hinst'
$setup = Join-Path $OutputDirectory 'SocketJack-Notes-Setup.exe'
Invoke-OwnedProcess $CliPath @('pack',$manifestPath,'--source',$source,'--output',$package) (Join-Path $OutputDirectory 'pack')
$signatureCases = Join-Path $OutputDirectory 'signature-cases'
New-Item -ItemType Directory -Path $signatureCases | Out-Null
# These test keys exist only as disposable RSA objects. No private PEM is exported or written.
$fixtureSigner = [Security.Cryptography.RSA]::Create(2048)
$otherSigner = [Security.Cryptography.RSA]::Create(2048)
try {
    Add-InMemoryFixtureSignature $package $fixtureSigner
    foreach ($name in @('newer-same-publisher','wrong-product','wrong-publisher')) {
        $smallManifest = [ordered]@{ id=$(if ($name -eq 'wrong-product') {'heirowInstaller.wizard-fixture-other'} else {'heirowInstaller.wizard-fixture'}); name='SocketJack Notes'; version='1.0.1'; publisher='SocketJack acceptance'; payload=@(@{source='notes.txt';path='notes.txt'}) }
        $smallPath = Join-Path $signatureCases ($name + '.json')
        $smallManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $smallPath -Encoding utf8
        $smallPackage = Join-Path $signatureCases ($name + '.hinst')
        Invoke-OwnedProcess $CliPath @('pack',$smallPath,'--source',$source,'--output',$smallPackage) (Join-Path $signatureCases $name)
        Add-InMemoryFixtureSignature $smallPackage $(if ($name -eq 'wrong-publisher') {$otherSigner} else {$fixtureSigner})
    }
} finally { $fixtureSigner.Dispose(); $otherSigner.Dispose() }
Invoke-OwnedProcess $CliPath @('verify',$package,'--require-signature') (Join-Path $OutputDirectory 'verify-signed')
Invoke-OwnedProcess $CliPath @('bundle',$package,'--launcher',$LauncherPath,'--output',$setup) (Join-Path $OutputDirectory 'bundle')
$cases = @()
foreach ($open in @($false,$true)) {
    if ($(if ($open) { 'install-open' } else { 'install-closed' }) -notin $CaseNames) { continue }
    $run = Join-Path $OutputDirectory $(if ($open) { 'finish-open' } else { 'finish-closed' })
    New-Item -ItemType Directory -Path $run | Out-Null
    [IO.File]::WriteAllText((Join-Path $run '.isolated-wizard-fixture'),"heirowInstaller isolated wizard acceptance v1`n")
    Invoke-OwnedProcess $setup @('--wizard-smoke',$run) (Join-Path $run 'setup') @{ HEIROWINSTALLER_WIZARD_SMOKE_OPEN=$(if ($open) { '1' } else { '0' }); HEIROWINSTALLER_WIZARD_BINDING_DIRECTORY=$signatureCases }
    $receiptPath = Join-Path $run 'wizard-smoke.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $finish = Get-Content -LiteralPath (Join-Path $run 'finish-action.json') -Raw | ConvertFrom-Json
    if (-not $receipt.success -or -not $finish.finish -or $finish.openApplication -ne $open) { throw 'The actual native Finish action did not preserve its Open-app choice.' }
    $opened = Join-Path $run 'installed/application/opened.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($open -and -not (Test-Path -LiteralPath $opened) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    if ((Test-Path -LiteralPath $opened) -ne $open) { throw 'Actual installed application launch did not match the Finish checkbox.' }
    if ($open) { $launch = Get-Content -LiteralPath $opened -Raw | ConvertFrom-Json; if (-not $launch.normalLaunch -or [IO.Path]::GetFullPath($launch.image) -ne [IO.Path]::GetFullPath((Join-Path $run 'installed/application/WizardApplicationProbe.exe'))) { throw 'Finish opened a different application image.' } }
    Invoke-OwnedProcess $LauncherPath @('uninstall','heirowInstaller.wizard-fixture','--state-root',(Join-Path $run 'state'),'--isolated','--silent') (Join-Path $run 'cleanup')
    if (Test-Path -LiteralPath (Join-Path $run 'state/heirowInstaller.wizard-fixture/receipt.json')) { throw 'The isolated fixture receipt survived normal cleanup.' }
    $cases += [ordered]@{ kind='install'; open=$open; checks=$receipt.checks.Count; receipt=$receiptPath; receiptSha256=(Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash; finishActionSha256=(Get-FileHash -LiteralPath (Join-Path $run 'finish-action.json') -Algorithm SHA256).Hash; installedAppActuallyOpened=(Test-Path -LiteralPath $opened); isolatedReceiptRemoved=$true }
}
foreach ($operation in @('repair','uninstall')) {
    if ($operation -notin $CaseNames) { continue }
    $run = Join-Path $OutputDirectory ('maintenance-' + $operation)
    New-Item -ItemType Directory -Path $run | Out-Null
    [IO.File]::WriteAllText((Join-Path $run '.isolated-wizard-fixture'),"heirowInstaller isolated wizard acceptance v1`n")
    Invoke-OwnedProcess $setup @('--silent','--isolated','--no-register','--scope','user','--target',(Join-Path $run 'installed'),'--state-root',(Join-Path $run 'state')) (Join-Path $run 'prepare')
    $installedReceipt = Get-Content -LiteralPath (Join-Path $run 'state/heirowInstaller.wizard-fixture/receipt.json') -Raw | ConvertFrom-Json
    $cachedHost = [IO.Path]::GetFullPath($installedReceipt.uninstallerPath)
    $expectedCache = [IO.Path]::GetFullPath((Join-Path $run 'state/heirowInstaller.wizard-fixture/uninstaller')) + [IO.Path]::DirectorySeparatorChar
    $rawHash=(Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash
    $expectedPoolHost=[IO.Path]::GetFullPath((Join-Path $run ('.installer-hosts/' + $rawHash.ToLowerInvariant() + '/heirowInstaller.exe')))
    $hostHash=(Get-FileHash -LiteralPath $cachedHost -Algorithm SHA256).Hash
    $legacyHost=$cachedHost.StartsWith($expectedCache,[StringComparison]::OrdinalIgnoreCase) -and $hostHash -eq (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    $pooledHost=$cachedHost.Equals($expectedPoolHost,[StringComparison]::OrdinalIgnoreCase) -and $hostHash -eq $rawHash
    if (-not ($legacyHost -or $pooledHost)) { throw 'The fixture maintenance host is not the exact known compiled setup or verified shared GUI prefix.' }
    Invoke-OwnedProcess $cachedHost @($operation,'heirowInstaller.wizard-fixture','--scope','user','--wizard-smoke',$run) (Join-Path $run 'setup')
    $receiptPath = Join-Path $run 'wizard-smoke.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $finish = Get-Content -LiteralPath (Join-Path $run 'finish-action.json') -Raw | ConvertFrom-Json
    if (-not $receipt.success -or $receipt.maintenance -ne $operation -or -not $finish.finish -or $finish.openApplication) { throw 'The maintenance wizard did not preserve its app-bound completion choice.' }
    if ($operation -eq 'repair') { Invoke-OwnedProcess $LauncherPath @('uninstall','heirowInstaller.wizard-fixture','--state-root',(Join-Path $run 'state'),'--isolated','--silent') (Join-Path $run 'cleanup') }
    if (Test-Path -LiteralPath (Join-Path $run 'state/heirowInstaller.wizard-fixture/receipt.json')) { throw 'The maintenance fixture receipt survived normal cleanup.' }
    $cases += [ordered]@{ kind=$operation; cachedHost=$true; checks=$receipt.checks.Count; receipt=$receiptPath; receiptSha256=(Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash; finishActionSha256=(Get-FileHash -LiteralPath (Join-Path $run 'finish-action.json') -Algorithm SHA256).Hash; isolatedReceiptRemoved=$true }
}
$globalAfter = Global-FileMetadata
if ($cases.Count -eq 0) { throw 'Select at least one wizard acceptance case.' }
if (($globalBefore | ConvertTo-Json -Depth 8 -Compress) -ne ($globalAfter | ConvertTo-Json -Depth 8 -Compress)) { throw 'A global tool preference, status or receipt changed during the isolated test. It was preserved for review, not overwritten.' }
$result = [ordered]@{ success=$true; sourceAppSha256=(Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash; compiledSetupSha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash; cases=$cases; globalMetadataUnchanged=$true; realUacTested=$false; noRealApplicationsInstalled=$true }
$resultPath = Join-Path $OutputDirectory 'acceptance.json'
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding utf8
[pscustomobject]@{ success=$true; cases=$cases.Count; checks=($cases.checks | Measure-Object -Sum).Sum; receipt=$resultPath; receiptSha256=(Get-FileHash -LiteralPath $resultPath -Algorithm SHA256).Hash } | ConvertTo-Json
