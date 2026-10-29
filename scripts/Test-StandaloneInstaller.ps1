#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LauncherPath,
    [string]$Cli,
    [string]$UpdaterPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Cli) { $Cli = Join-Path $projectRoot 'src/HeirowInstaller.Cli/bin/Release/net10.0/heirowInstaller.Cli.exe' }
$Cli = [IO.Path]::GetFullPath($Cli); $LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$id = 'standalone-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/standalone-authoring/' + $id) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh standalone fixture output directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$project = Join-Path $OutputDirectory 'application'; New-Item -ItemType Directory -Path $project | Out-Null
$recipient = Join-Path $OutputDirectory 'recipient'; New-Item -ItemType Directory -Path $recipient | Out-Null
$state = Join-Path $OutputDirectory 'state'; $target = Join-Path $OutputDirectory 'installed'
$checks = [Collections.Generic.List[string]]::new()
function Assert-Check([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message }; $checks.Add($Message); Write-Host "PASS $Message" }
function Invoke-Child([string]$Executable,[string[]]$Arguments,[switch]$RecipientEnvironment) {
    . (Join-Path $PSScriptRoot 'Test-LegalFixture.ps1')
    $Arguments = Add-TestLegalArguments $Executable $Arguments
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; $start.WorkingDirectory=$recipient
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($RecipientEnvironment) {
        $start.Environment['PATH'] = [Environment]::SystemDirectory
        $start.Environment['DOTNET_ROOT'] = Join-Path $OutputDirectory 'no-installed-dotnet'
        $start.Environment['DOTNET_ROOT_X64'] = Join-Path $OutputDirectory 'no-installed-dotnet'
        $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $OutputDirectory 'runtime-extraction'
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync(); $process.WaitForExit()
        $output=$stdout.GetAwaiter().GetResult(); $errorOutput=$stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -notin @(0,3010)) { throw "Child failed ($($process.ExitCode)): $errorOutput $output" }
        return $output
    } finally { $process.Dispose() }
}
[IO.File]::WriteAllText((Join-Path $project 'Application.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>StandaloneFixture</AssemblyName><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Content Include="repair-sentinel.txt" CopyToPublishDirectory="PreserveNewest" ExcludeFromSingleFile="true" /></ItemGroup></Project>')
[IO.File]::WriteAllText((Join-Path $project 'Program.cs'),'if(args.Length!=1 || !Path.IsPathFullyQualified(args[0])) return 2; File.WriteAllText(args[0],System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription); return 0;')
$sentinelText='Native maintenance payload'; [IO.File]::WriteAllText((Join-Path $project 'repair-sentinel.txt'),$sentinelText)
$rsa=[Security.Cryptography.RSA]::Create(2048)
try {
    $key=Join-Path $OutputDirectory 'publisher.private.pem'; [IO.File]::WriteAllText($key,$rsa.ExportPkcs8PrivateKeyPem())
    $pin=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
} finally { $rsa.Dispose() }
$manifest=[ordered]@{id=$id;name='Standalone application acceptance';version='1.0.0';publisher='heirowInstaller acceptance';scope='user';entryPoint='StandaloneFixture.exe';payload=@()}
$manifestPath=Join-Path $OutputDirectory 'installer.json'; $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$setup=Join-Path $recipient 'Application-Setup.exe'
try {
    $built=Invoke-Child $Cli @('build',$manifestPath,'--project',(Join-Path $project 'Application.csproj'),'--output',$setup,'--launcher',$LauncherPath,'--sign-key',$key,'--runtime','win-x64') | ConvertFrom-Json
    Assert-Check ($built.applicationRuntimePublished -and $built.outputPath -eq $setup) 'The standalone builder published the application with its own runtime'
    Assert-Check ($built.sha256 -eq (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()) 'The generated setup matches its build-result hash'
    $verified=Invoke-Child $Cli @('verify',$setup,'--trust-key',$pin,'--require-signature') | ConvertFrom-Json
    Assert-Check ($verified.valid -and $verified.publisherKeySha256 -eq $pin) 'The standalone setup contains the signed application package'
    Assert-Check (@(Get-ChildItem -LiteralPath $recipient -File).Count -eq 1) 'Only the single setup EXE was transferred to the recipient directory'
    Invoke-Child $setup @('--silent','--isolated','--target',$target,'--state-root',$state,'--trust-key',$pin,'--require-signature') -RecipientEnvironment | Out-Null
    $application=Join-Path $target 'StandaloneFixture.exe'
    Assert-Check (Test-Path -LiteralPath $application) 'The setup installed its application without a globally installed heirowInstaller'
    $report=Join-Path $OutputDirectory 'application-runtime.txt'
    Invoke-Child $application @($report) -RecipientEnvironment | Out-Null
    Assert-Check ([IO.File]::ReadAllText($report).StartsWith('.NET 10.')) 'The installed application ran with tool lookup removed and DOTNET_ROOT set to an absent directory'
    $receiptPath=Join-Path $state ($id + '/receipt.json'); $installed=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    Assert-Check ($installed.publisherKeySha256 -eq $pin -and (Test-Path -LiteralPath $installed.uninstallerPath)) 'The recipient retained its publisher pin and its own self-contained maintenance host'
    $sentinel=Join-Path $target 'repair-sentinel.txt'
    Assert-Check ((Test-Path -LiteralPath $sentinel) -and [IO.File]::ReadAllText($sentinel) -ceq $sentinelText) 'The fixture installed its separate repair payload'
    [IO.File]::Delete($sentinel)
    $repairLog=Join-Path $OutputDirectory 'maintenance logs/repair.log'
    Invoke-Child $installed.uninstallerPath @('repair',$id,'--state-root',$state,'--silent','--log',$repairLog) -RecipientEnvironment | Out-Null
    Assert-Check ((Test-Path -LiteralPath $sentinel) -and [IO.File]::ReadAllText($sentinel) -ceq $sentinelText) 'The cached native host repaired its missing payload without the builder'
    Assert-Check ((Test-Path -LiteralPath $repairLog) -and [IO.File]::ReadAllText($repairLog).Contains("Installed $id 1.0.0")) 'The cached native repair wrote the requested log with the repaired product identity'
    $installed=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($UpdaterPath) {
        $manifest.updateUrl='https://updates.example.invalid/fixture.json'
        $manifest.features=@(@{id='updates';name='Optional app updates';default=$false})
        $manifest.updateCompanion=@{feature='updates';executablePath='heirowUpdater.exe';configurationPath='heirowUpdater.json';mode='after-exit'}
        $manifest.payload=@(@{source='StandaloneFixture.exe';path='StandaloneFixture.exe'},@{source='heirowUpdater.exe';path='heirowUpdater.exe';feature='updates'})
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        $withUpdater=Join-Path $recipient 'Application-With-Updater.exe'
        $injected=Invoke-Child $Cli @('build',$manifestPath,'--source',$target,'--output',$withUpdater,'--launcher',$LauncherPath,'--sign-key',$key,'--updater',([IO.Path]::GetFullPath($UpdaterPath))) | ConvertFrom-Json
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $target 'heirowUpdater.exe'))) 'Companion injection did not require or alter the source application folder'
        $updaterPayload=@($injected.manifest.payload | Where-Object path -eq 'heirowUpdater.exe')
        Assert-Check ($updaterPayload.Count -eq 1 -and $updaterPayload[0].feature -eq 'updates') 'The builder injected the missing declared companion into its optional payload feature'
        $folderBuild=Invoke-Child $Cli @('verify',$withUpdater,'--trust-key',$pin,'--require-signature') | ConvertFrom-Json
        Assert-Check ($folderBuild.valid) 'The folder-based setup with an injected root companion verifies independently'
    }
    $uninstallLog=Join-Path $OutputDirectory 'maintenance logs/uninstall.log'
    Invoke-Child $installed.uninstallerPath @('uninstall',$id,'--state-root',$state,'--silent','--log',$uninstallLog) -RecipientEnvironment | Out-Null
    Assert-Check (-not (Test-Path -LiteralPath $application) -and -not (Test-Path -LiteralPath $receiptPath)) 'The recipient uninstalled through its cached standalone host without the builder'
    Assert-Check ((Test-Path -LiteralPath $uninstallLog) -and [IO.File]::ReadAllText($uninstallLog).Contains("Uninstalled $id")) 'The cached native uninstall wrote the requested log with the removed product identity'
    [ordered]@{passed=$checks.Count;productId=$id;setup=$setup;sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant();checks=$checks} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'receipt.json') -Encoding utf8NoBOM
    Get-Content -LiteralPath (Join-Path $OutputDirectory 'receipt.json')
} finally {
    if (Test-Path -LiteralPath (Join-Path $state ($id + '/receipt.json'))) { Invoke-Child $Cli @('uninstall',$id,'--state-root',$state,'--silent') | Out-Null }
}
