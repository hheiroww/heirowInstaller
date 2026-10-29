#requires -Version 7.0
<#
.SYNOPSIS
Exercises the real Windows Installer adapter with a newly authored benign per-user MSI.
.DESCRIPTION
Uses msi.dll authoring APIs and makecab.exe, without WiX. Installs only a newly
generated ProductCode and one file under this run's unique artifact directory.
The current CLI imports and bundles the MSI with the published Project-Z launcher.
No existing products are selected or modified. A finally block removes only the
fixture ProductCode if an assertion fails after Windows Installer registration.
#>
[CmdletBinding()]
param(
    [string]$CliPath,
    [string]$LauncherPath,
    [string]$OutputDirectory,
    [switch]$UseLauncherForInstall,
    [switch]$SkipExecutableFixture,
    [switch]$TestWaitAliases
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This acceptance test requires Windows and the Windows Installer service.' }
if ($TestWaitAliases -and $SkipExecutableFixture) { throw '-TestWaitAliases requires the benign executable fixture.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $CliPath) { $CliPath = Join-Path $projectRoot 'src/HeirowInstaller.Cli/bin/Release/net10.0/heirowInstaller.Cli.exe' }
if (-not $LauncherPath) { $LauncherPath = Join-Path $projectRoot 'artifacts/ui-published-final/heirowInstaller.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/windows-adapter-smoke' }
$CliPath = [IO.Path]::GetFullPath($CliPath)
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($required in @($CliPath,$LauncherPath)) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required executable is missing: $required" } }
$runId = [Guid]::NewGuid().ToString('N')
$shortId = $runId.Substring(0,12)
$runDirectory = Join-Path $OutputDirectory $shortId
if (Test-Path -LiteralPath $runDirectory) { throw 'The unique fixture directory already exists. Run the test again to generate a new identity.' }
$sandbox = Join-Path $runDirectory 'sandbox'
$authoredTarget = Join-Path $sandbox 'msi-authored-default'
$stateDirectory = Join-Path $sandbox 'installer-state'
$productCode = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$packageCode = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$componentCode = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$upgradeCode = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$productId = "adapter-msi-$shortId"
$exeProductId = "adapter-exe-$shortId"
$exeTarget = Join-Path $sandbox 'external-exe-target'
$checks = [Collections.Generic.List[string]]::new()
$processes = [Collections.Generic.List[object]]::new()
$cleanup = [ordered]@{ msiProductState = $null; msiPayloadAbsent = $false; msiReceiptAbsent = $false; executablePayloadAbsent = $null }
$receipt = [ordered]@{ success = $false; runId = $runId; productCode = $productCode; productId = $productId; scope = 'user'; hostElevated = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); sandbox = $sandbox; authoredTarget = $authoredTarget; launcherInstall = [bool]$UseLauncherForInstall; checks = $checks; processes = $processes; cleanup = $cleanup }
New-Item -ItemType Directory -Path $runDirectory,$sandbox -Force | Out-Null

function Assert-SandboxPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($sandbox).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Refusing a fixture write outside its unique sandbox: $resolved" }
    return $resolved
}
function Assert-Check([bool]$Condition,[string]$Description) {
    if (-not $Condition) { throw $Description }
    $checks.Add($Description)
}
function Invoke-FixtureProcess([string]$Executable,[string[]]$Arguments,[string]$Name,[int[]]$SuccessCodes = @(0,3010)) {
    . (Join-Path $PSScriptRoot 'Test-LegalFixture.ps1')
    $Arguments = Add-TestLegalArguments $Executable $Arguments
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $runDirectory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(180000)) { throw "$Name did not finish within three minutes. Do not terminate an active msiexec transaction; inspect the fixture logs." }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.txt"),$stdout)
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.txt"),$stderr)
    $processes.Add([ordered]@{name=$Name; executable=[IO.Path]::GetFileName($Executable); arguments=$Arguments; exitCode=$exitCode})
    $process.Dispose()
    if ($SuccessCodes -notcontains $exitCode) { throw "$Name failed with exit code $exitCode. $stderr" }
    return $stdout
}
function Invoke-Cli([string[]]$Arguments,[string]$Name,[int[]]$SuccessCodes = @(0,3010)) {
    return Invoke-FixtureProcess -Executable $CliPath -Arguments $Arguments -Name $Name -SuccessCodes $SuccessCodes
}

if (-not ('HeirowInstaller.Acceptance.NativeMsi' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace HeirowInstaller.Acceptance {
    public static class NativeMsi {
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiOpenDatabase(string path,IntPtr persist,out uint database);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiDatabaseOpenView(uint database,string query,out uint view);
        [DllImport("msi.dll")] static extern uint MsiViewExecute(uint view,uint record);
        [DllImport("msi.dll")] static extern uint MsiViewClose(uint view);
        [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
        [DllImport("msi.dll")] static extern uint MsiCreateRecord(uint fields);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiRecordSetStream(uint record,uint field,string path);
        [DllImport("msi.dll")] static extern uint MsiDatabaseCommit(uint database);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiGetSummaryInformation(uint database,string path,uint updateCount,out uint summary);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiSummaryInfoSetProperty(uint summary,uint property,uint type,int number,IntPtr time,string value);
        [DllImport("msi.dll")] static extern uint MsiSummaryInfoPersist(uint summary);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] public static extern int MsiQueryProductState(string product);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiEnumProductsEx(string product,string sid,uint context,uint index,StringBuilder installedProduct,out uint installedContext,StringBuilder installedSid,ref uint sidLength);
        public static uint PerUserContext(string product) {
            var found = new StringBuilder(39);var sid = new StringBuilder(256);uint length=256,context;
            var code=MsiEnumProductsEx(product,null,2,0,found,out context,sid,ref length);
            if(code==259)return 0;Check(code,"MsiEnumProductsEx current-user context");return context;
        }
        static void Check(uint code,string operation) { if(code!=0)throw new InvalidOperationException(operation+" failed with Windows Installer error "+code); }
        public static uint Create(string path) { uint database; Check(MsiOpenDatabase(path,new IntPtr(3),out database),"MsiOpenDatabase(create)"); return database; }
        public static void Sql(uint database,string query) {
            uint view; Check(MsiDatabaseOpenView(database,query,out view),"MsiDatabaseOpenView: "+query);
            try { Check(MsiViewExecute(view,0),"MsiViewExecute: "+query); } finally { MsiViewClose(view);MsiCloseHandle(view); }
        }
        public static void Cabinet(uint database,string name,string path) {
            uint view;Check(MsiDatabaseOpenView(database,"INSERT INTO `_Streams` (`Name`,`Data`) VALUES ('"+name+"',?)",out view),"cabinet stream view");
            var record=MsiCreateRecord(1);
            try { Check(MsiRecordSetStream(record,1,path),"cabinet stream record");Check(MsiViewExecute(view,record),"cabinet stream insert"); }
            finally { MsiViewClose(view);MsiCloseHandle(view);MsiCloseHandle(record); }
        }
        public static void Summary(uint database,string packageCode) {
            uint summary;Check(MsiGetSummaryInformation(database,null,20,out summary),"summary handle");
            try {
                Check(MsiSummaryInfoSetProperty(summary,1,2,1252,IntPtr.Zero,null),"codepage");
                SetString(summary,2,"Installation Database");SetString(summary,3,"heirowInstaller per-user adapter acceptance");SetString(summary,4,"heirowInstaller acceptance");
                SetString(summary,7,"Intel;1033");SetString(summary,9,packageCode);
                Check(MsiSummaryInfoSetProperty(summary,14,3,200,IntPtr.Zero,null),"minimum MSI version");
                Check(MsiSummaryInfoSetProperty(summary,15,3,10,IntPtr.Zero,null),"compressed and least-privileged package flags");
                Check(MsiSummaryInfoSetProperty(summary,19,3,2,IntPtr.Zero,null),"security summary");
                Check(MsiSummaryInfoPersist(summary),"summary persist");
            } finally {MsiCloseHandle(summary);}
        }
        static void SetString(uint summary,uint property,string value) {Check(MsiSummaryInfoSetProperty(summary,property,30,0,IntPtr.Zero,value),"summary string "+property);}
        public static void CommitAndClose(uint database) {try {Check(MsiDatabaseCommit(database),"database commit");}finally {MsiCloseHandle(database);}}
        public static void Close(uint database) {MsiCloseHandle(database);}
    }
}
'@
}

try {
    $payloadText = "heirowInstaller real Windows Installer acceptance $runId`r`n"
    $sourceFile = Join-Path $runDirectory 'AdapterFile'
    [IO.File]::WriteAllText($sourceFile,$payloadText,[Text.Encoding]::ASCII)
    $cabinet = Join-Path $runDirectory 'fixture.cab'
    $null = Invoke-FixtureProcess -Executable (Get-Command makecab.exe -ErrorAction Stop).Source -Arguments @('/D','CompressionType=MSZIP',$sourceFile,$cabinet) -Name 'makecab' -SuccessCodes @(0)
    $msi = Join-Path $runDirectory 'fixture.msi'
    $database = [HeirowInstaller.Acceptance.NativeMsi]::Create($msi)
    try {
        $tables = @(
            'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)',
            'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)',
            'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)',
            'CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)',
            'CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)',
            'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)',
            'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)',
            'CREATE TABLE `CreateFolder` (`Directory_` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Directory_`, `Component_`)',
            'CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT NOT NULL PRIMARY KEY `Action`)'
        )
        foreach ($query in $tables) { [HeirowInstaller.Acceptance.NativeMsi]::Sql($database,$query) }
        $properties = [ordered]@{ProductCode=$productCode;ProductName="heirowInstaller adapter fixture $runId";ProductVersion='1.0.0';Manufacturer='heirowInstaller acceptance';ProductLanguage='1033';UpgradeCode=$upgradeCode;INSTALLLEVEL='1';INSTALLFOLDER=((Assert-SandboxPath $authoredTarget) + [IO.Path]::DirectorySeparatorChar);ARPNOMODIFY='1'}
        foreach ($entry in $properties.GetEnumerator()) {
            $name = $entry.Key.Replace("'","''"); $value = ([string]$entry.Value).Replace("'","''")
            [HeirowInstaller.Acceptance.NativeMsi]::Sql($database,"INSERT INTO ``Property`` (``Property``,``Value``) VALUES ('$name','$value')")
        }
        foreach ($query in @(
            "INSERT INTO ``Directory`` (``Directory``,``DefaultDir``) VALUES ('TARGETDIR','SourceDir')",
            "INSERT INTO ``Directory`` (``Directory``,``Directory_Parent``,``DefaultDir``) VALUES ('INSTALLFOLDER','TARGETDIR','.')",
            "INSERT INTO ``Component`` (``Component``,``ComponentId``,``Directory_``,``Attributes``,``KeyPath``) VALUES ('CoreFiles','$componentCode','INSTALLFOLDER',0,'AdapterFile')",
            "INSERT INTO ``Feature`` (``Feature``,``Title``,``Description``,``Display``,``Level``,``Directory_``,``Attributes``) VALUES ('MainFeature','Fixture files','Benign per-user acceptance file',1,1,'INSTALLFOLDER',0)",
            "INSERT INTO ``FeatureComponents`` (``Feature_``,``Component_``) VALUES ('MainFeature','CoreFiles')",
            "INSERT INTO ``File`` (``File``,``Component_``,``FileName``,``FileSize``,``Attributes``,``Sequence``) VALUES ('AdapterFile','CoreFiles','adapter.txt',$((Get-Item -LiteralPath $sourceFile).Length),16384,1)",
            "INSERT INTO ``Media`` (``DiskId``,``LastSequence``,``Cabinet``) VALUES (1,1,'#fixture.cab')",
            "INSERT INTO ``CreateFolder`` (``Directory_``,``Component_``) VALUES ('INSTALLFOLDER','CoreFiles')"
        )) { [HeirowInstaller.Acceptance.NativeMsi]::Sql($database,$query) }
        $sequence = [ordered]@{CostInitialize=800;FileCost=900;CostFinalize=1000;InstallValidate=1400;InstallInitialize=1500;ProcessComponents=1600;UnpublishFeatures=1800;RemoveFiles=3500;RemoveFolders=3600;CreateFolders=3700;InstallFiles=4000;RegisterUser=6000;RegisterProduct=6100;PublishFeatures=6300;PublishProduct=6400;InstallFinalize=6600}
        foreach ($entry in $sequence.GetEnumerator()) { [HeirowInstaller.Acceptance.NativeMsi]::Sql($database,"INSERT INTO ``InstallExecuteSequence`` (``Action``,``Sequence``) VALUES ('$($entry.Key)',$($entry.Value))") }
        [HeirowInstaller.Acceptance.NativeMsi]::Cabinet($database,'fixture.cab',$cabinet)
        [HeirowInstaller.Acceptance.NativeMsi]::Summary($database,$packageCode)
        [HeirowInstaller.Acceptance.NativeMsi]::CommitAndClose($database)
        $database = 0
    } finally { if ($database) { [HeirowInstaller.Acceptance.NativeMsi]::Close($database) } }
    Assert-Check ([HeirowInstaller.Acceptance.NativeMsi]::MsiQueryProductState($productCode) -eq -1) 'The newly generated MSI ProductCode was not installed before this test'
    $package = Join-Path $runDirectory 'fixture.hinst'
    $null = Invoke-Cli -Arguments @('import-msi',$msi,'--output',$package,'--id',$productId,'--scope','user') -Name 'import-msi'
    $inspection = (Invoke-Cli -Arguments @('inspect',$package) -Name 'inspect-msi') | ConvertFrom-Json
    Assert-Check ($inspection.manifest.legacyMsi.productCode -eq $productCode) 'MSI import preserved the original ProductCode'
    Assert-Check ($inspection.manifest.scope -eq 'user' -and [string]::IsNullOrWhiteSpace($inspection.manifest.installDirectory)) 'MSI import preserved a per-user package with no destination override'
    $plan = (Invoke-Cli -Arguments @('plan',$package,'--state-root',$stateDirectory,'--scope','user') -Name 'plan-msi') | ConvertFrom-Json
    Assert-Check ([bool]$plan.useOriginalInstallDirectory) 'The current Core plan preserves the original MSI authored directory'
    $setup = Join-Path $runDirectory 'fixture-Setup.exe'
    $null = Invoke-Cli -Arguments @('bundle',$package,'--launcher',$LauncherPath,'--output',$setup) -Name 'bundle-msi'
    $verified = (Invoke-Cli -Arguments @('verify',$setup,'--silent') -Name 'verify-bundle') | ConvertFrom-Json
    Assert-Check ([bool]$verified.valid) 'The published Project-Z launcher bundle contains a verified MSI package'
    if ($UseLauncherForInstall) {
        $null = Invoke-FixtureProcess -Executable $setup -Arguments @('--silent','--state-root',$stateDirectory,'--scope','user') -Name 'install-msi-bundle'
    } else {
        $null = Invoke-Cli -Arguments @('install',$setup,'--silent','--state-root',$stateDirectory,'--scope','user','--no-register','--log',(Join-Path $runDirectory 'msi-install.log')) -Name 'install-msi-bundle'
    }
    $installedFile = Assert-SandboxPath (Join-Path $authoredTarget 'adapter.txt')
    Assert-Check (Test-Path -LiteralPath $installedFile -PathType Leaf) 'Real msiexec installed the payload into the original authored default without --target'
    Assert-Check ([IO.File]::ReadAllText($installedFile) -eq $payloadText) 'The actual Windows Installer payload matches the original source'
    Assert-Check ([HeirowInstaller.Acceptance.NativeMsi]::MsiQueryProductState($productCode) -eq 5) 'Windows Installer registered the fixture ProductCode for the current user'
    Assert-Check ([HeirowInstaller.Acceptance.NativeMsi]::PerUserContext($productCode) -eq 2) 'Windows Installer enumerated the fixture in the unmanaged per-user context only'
    $installedReceipt = Join-Path $stateDirectory "$productId/receipt.json"
    Assert-Check (Test-Path -LiteralPath $installedReceipt) 'heirowInstaller recorded the external MSI installation separately'
    $recorded = Get-Content -LiteralPath $installedReceipt -Raw | ConvertFrom-Json
    Assert-Check ([bool]$recorded.useOriginalInstallDirectory) 'The installation receipt retained original MSI directory policy'
    $durableSource = Assert-SandboxPath (Join-Path $recorded.legacySourceDirectory 'fixture.msi')
    Assert-Check (Test-Path -LiteralPath $durableSource -PathType Leaf) 'The verified original MSI media remains durably available after installation'
    [IO.File]::WriteAllText($installedFile,'intentional adapter acceptance corruption',[Text.Encoding]::ASCII)
    $null = Invoke-Cli -Arguments @('repair',$productId,'--silent','--state-root',$stateDirectory,'--scope','user','--log',(Join-Path $runDirectory 'msi-repair.log')) -Name 'repair-msi'
    Assert-Check ([IO.File]::ReadAllText($installedFile) -eq $payloadText) 'Real msiexec repair restored the deliberately corrupted file from the verified original MSI source'
    $null = Invoke-Cli -Arguments @('uninstall',$productId,'--silent','--state-root',$stateDirectory,'--scope','user','--log',(Join-Path $runDirectory 'msi-uninstall.log')) -Name 'uninstall-msi'
    Assert-Check (-not (Test-Path -LiteralPath $installedFile)) 'Real msiexec uninstall removed the fixture payload'
    Assert-Check ([HeirowInstaller.Acceptance.NativeMsi]::MsiQueryProductState($productCode) -eq -1) 'Real msiexec uninstall removed the fixture ProductCode registration'
    Assert-Check (-not (Test-Path -LiteralPath $installedReceipt)) 'heirowInstaller removed the fixture receipt after external uninstall'
    if (-not $SkipExecutableFixture) {
        $fixtureProject = Join-Path $runDirectory 'executable-fixture'
        $fixturePublish = Join-Path $fixtureProject 'publish'
        New-Item -ItemType Directory -Path $fixtureProject -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixtureProject 'AdapterExeFixture.csproj'),@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <AssemblyName>AdapterExeFixture</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@)
        $fixtureSource = @'
using System.Text.Json;
if (args.Length != 3) return 2;
var allowed = Path.GetFullPath("__SANDBOX__").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
var target = Path.GetFullPath(args[1]);
if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || args[2] != "__NONCE__") return 44;
var operation = args[0];
if (operation is not ("install" or "install-interactive" or "install-silent" or "repair" or "uninstall" or "fail" or "hold")) return 43;
var payload = Path.Combine(target, "external-fixture.txt");
var text = "External installer acceptance __NONCE__\n";
if (operation is "install" or "install-interactive" or "install-silent" or "repair") { Directory.CreateDirectory(target); File.WriteAllText(payload, text); }
if (operation == "uninstall") { if (File.Exists(payload)) File.Delete(payload); if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target); }
if (operation == "hold") {
    Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"started.marker"),Environment.ProcessId.ToString());
    var deadline=DateTime.UtcNow.AddSeconds(30);
    while(!File.Exists(Path.Combine(target,"release.flag")) && DateTime.UtcNow<deadline) Thread.Sleep(25);
}
File.AppendAllText(Path.Combine(allowed,"external-exe-events.jsonl"),JsonSerializer.Serialize(new { operation, target, timestamp=DateTimeOffset.UtcNow })+Environment.NewLine);
return operation == "fail" ? 42 : operation.StartsWith("install",StringComparison.Ordinal) ? 3010 : 0;
'@
        $fixtureSource = $fixtureSource.Replace('__SANDBOX__',$sandbox.Replace('\','\\').Replace('"','\"')).Replace('__NONCE__',$runId)
        [IO.File]::WriteAllText((Join-Path $fixtureProject 'Program.cs'),$fixtureSource)
        $null = Invoke-FixtureProcess -Executable (Get-Command dotnet.exe -ErrorAction Stop).Source -Arguments @('publish',(Join-Path $fixtureProject 'AdapterExeFixture.csproj'),'-c','Release','-r','win-x64','--self-contained','false','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:DebugType=None','-o',$fixturePublish,'--nologo','--verbosity','quiet') -Name 'build-exe-fixture' -SuccessCodes @(0)
        $fixtureExecutable = Join-Path $fixturePublish 'AdapterExeFixture.exe'
        $exePackage = Join-Path $runDirectory 'external-fixture.hinst'
        $installArguments = ConvertTo-Json -InputObject @('install-interactive','{InstallDir}',$runId) -Compress
        $silentInstallArguments = ConvertTo-Json -InputObject @('install-silent','{InstallDir}',$runId) -Compress
        $repairArguments = ConvertTo-Json -InputObject @('repair','{InstallDir}',$runId) -Compress
        $uninstallArguments = ConvertTo-Json -InputObject @('uninstall','{InstallDir}',$runId) -Compress
        $null = Invoke-Cli -Arguments @('import-exe',$fixtureExecutable,'--output',$exePackage,'--id',$exeProductId,'--name','Benign external installer fixture','--version','1.0.0','--publisher','heirowInstaller acceptance','--scope','user','--install-args',$installArguments,'--silent-install-args',$silentInstallArguments,'--repair-args',$repairArguments,'--uninstall-args',$uninstallArguments) -Name 'import-exe'
        $exeInstalled = (Invoke-Cli -Arguments @('install',$exePackage,'--silent','--scope','user','--state-root',$stateDirectory,'--target',(Assert-SandboxPath $exeTarget),'--no-register') -Name 'install-exe') | ConvertFrom-Json
        Assert-Check ([bool]$exeInstalled.rebootRequired) 'The external EXE adapter accepted exit 3010 and reported reboot-required without restarting Windows'
        $exeFile = Assert-SandboxPath (Join-Path $exeTarget 'external-fixture.txt')
        $exeText = "External installer acceptance $runId`n"
        Assert-Check ((Test-Path -LiteralPath $exeFile) -and [IO.File]::ReadAllText($exeFile) -eq $exeText) 'The actual imported EXE received the expanded exact sandbox installation directory'
        [IO.File]::WriteAllText($exeFile,'intentional EXE acceptance corruption')
        $null = Invoke-Cli -Arguments @('repair',$exeProductId,'--silent','--scope','user','--state-root',$stateDirectory) -Name 'repair-exe'
        Assert-Check ([IO.File]::ReadAllText($exeFile) -eq $exeText) 'The actual external EXE repair command restored the corrupted fixture'
        $null = Invoke-Cli -Arguments @('uninstall',$exeProductId,'--silent','--scope','user','--state-root',$stateDirectory) -Name 'uninstall-exe'
        Assert-Check (-not (Test-Path -LiteralPath $exeFile)) 'The actual external EXE uninstall command removed its payload'
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $stateDirectory "$exeProductId/receipt.json"))) 'heirowInstaller removed the external EXE receipt after uninstall'
        $failureId = "adapter-fail-$shortId"
        $failureTarget = Assert-SandboxPath (Join-Path $sandbox 'external-failure-target')
        $failurePackage = Join-Path $runDirectory 'external-failure.hinst'
        $failureArguments = ConvertTo-Json -InputObject @('fail','{InstallDir}',$runId) -Compress
        $null = Invoke-Cli -Arguments @('import-exe',$fixtureExecutable,'--output',$failurePackage,'--id',$failureId,'--name','Benign failing external installer fixture','--version','1.0.0','--publisher','heirowInstaller acceptance','--scope','user','--install-args',$failureArguments) -Name 'import-exe-failure'
        $null = Invoke-Cli -Arguments @('install',$failurePackage,'--silent','--scope','user','--state-root',$stateDirectory,'--target',$failureTarget,'--no-register') -Name 'install-exe-failure' -SuccessCodes @(1)
        Assert-Check ((Get-Content -LiteralPath (Join-Path $runDirectory 'install-exe-failure.stderr.txt') -Raw) -match 'exit code 42\b') 'The external EXE adapter rejected an unexpected installer exit code'
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $stateDirectory "$failureId/receipt.json"))) 'A failed external EXE did not create a successful installation receipt'
        $null = Invoke-Cli -Arguments @('install',$exePackage,'--scope','user','--state-root',$stateDirectory,'--target',(Assert-SandboxPath $exeTarget),'--no-register') -Name 'install-exe-interactive-mode'
        Assert-Check (Test-Path -LiteralPath $exeFile) 'The external EXE adapter also supports the authored interactive argument route'
        $null = Invoke-Cli -Arguments @('uninstall',$exeProductId,'--silent','--scope','user','--state-root',$stateDirectory) -Name 'uninstall-exe-interactive-mode'
        $events = @(Get-Content -LiteralPath (Join-Path $sandbox 'external-exe-events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
        Assert-Check (($events.operation -join ',') -eq 'install-silent,repair,uninstall,fail,install-interactive,uninstall') 'External EXE receipts distinguish authored silent and interactive arguments and confirm each command ran exactly once'
        $cleanup.executablePayloadAbsent = -not (Test-Path -LiteralPath $exeFile)
    }
    if ($TestWaitAliases) {
        $waitId = "adapter-wait-$shortId"
        $waitState = Assert-SandboxPath (Join-Path $sandbox 'wait-state')
        $waitTarget = Assert-SandboxPath (Join-Path $sandbox 'wait-target')
        $waitManifestPath = Join-Path $runDirectory 'wait-manifest.json'
        $waitManifest = [ordered]@{formatVersion=1;id=$waitId;name='Benign native wait-alias fixture';version='1.0.0';publisher='heirowInstaller acceptance';scope='user';payload=@(@{source='AdapterFile';path='wait-fixture.txt'})}
        [IO.File]::WriteAllText($waitManifestPath,($waitManifest | ConvertTo-Json -Depth 8))
        $waitPackage = Join-Path $runDirectory 'wait-fixture.hinst'
        $waitSetup = Join-Path $runDirectory 'wait-fixture-Setup.exe'
        $null = Invoke-Cli -Arguments @('pack',$waitManifestPath,'--source',$runDirectory,'--output',$waitPackage) -Name 'pack-wait-native'
        $null = Invoke-Cli -Arguments @('bundle',$waitPackage,'--launcher',$LauncherPath,'--output',$waitSetup) -Name 'bundle-wait-native'
        $waitFile = Assert-SandboxPath (Join-Path $waitTarget 'wait-fixture.txt')
        # Warm the exact published single-file bundle once so extraction latency
        # cannot masquerade as a successful wait in the following assertion.
        $null = Invoke-FixtureProcess -Executable $waitSetup -Arguments @('--silent','--isolated','--target',$waitTarget,'--state-root',$waitState) -Name 'warm-native-bundle'
        $null = Invoke-Cli -Arguments @('uninstall',$waitId,'--silent','--state-root',$waitState,'--scope','user') -Name 'uninstall-warm-native'
        foreach ($identityMatches in @($true,$false)) {
            $name = if ($identityMatches) { 'matching' } else { 'mismatched' }
            $holdTarget = Assert-SandboxPath (Join-Path $sandbox "wait-helper-$name")
            $holdStart = [Diagnostics.ProcessStartInfo]::new($fixtureExecutable)
            $holdStart.UseShellExecute = $false; $holdStart.CreateNoWindow = $true
            foreach ($value in @('hold',$holdTarget,$runId)) { $holdStart.ArgumentList.Add($value) }
            $hold = [Diagnostics.Process]::Start($holdStart)
            $started = Join-Path $holdTarget 'started.marker'
            $until = [DateTime]::UtcNow.AddSeconds(10)
            while (-not (Test-Path -LiteralPath $started)) { if ([DateTime]::UtcNow -ge $until) { throw 'The benign wait helper did not start.' }; Start-Sleep -Milliseconds 25 }
            $startTicks = $hold.StartTime.ToUniversalTime().Ticks
            if (-not $identityMatches) { $startTicks++ }
            $bundleArguments = @('--defer','--wait-pid',[string]$hold.Id,'--wait-start',[string]$startTicks,'--isolated','--target',$waitTarget,'--state-root',$waitState)
            $bundleStart = [Diagnostics.ProcessStartInfo]::new($waitSetup)
            $bundleStart.UseShellExecute = $false; $bundleStart.CreateNoWindow = $true
            foreach ($value in $bundleArguments) { $bundleStart.ArgumentList.Add($value) }
            $bundle = [Diagnostics.Process]::Start($bundleStart)
            try {
                if ($identityMatches) {
                    Assert-Check ($bundle.WaitForExit(5000) -and $bundle.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $waitFile) -and -not $hold.HasExited) 'The silent published bundle rejects a live exact dependency promptly without closing it or writing payload'
                    [IO.File]::WriteAllText((Join-Path $holdTarget 'release.flag'),'release')
                    Assert-Check ($hold.WaitForExit(5000)) 'The owned dependency exits only after its normal release flag'
                    $null = Invoke-FixtureProcess -Executable $waitSetup -Arguments $bundleArguments -Name 'retry-released-native-dependency'
                }
                if (-not $bundle.WaitForExit(15000)) { throw "The $name identity bundle invocation did not complete." }
                $expectedInitialExit = if ($identityMatches) { 1 } else { 0 }
                Assert-Check ($bundle.ExitCode -eq $expectedInitialExit -and (Test-Path -LiteralPath $waitFile)) "The $name identity invocation installs only after its required dependency has exited or no longer matches"
                if (-not $identityMatches) {
                    Assert-Check (-not $hold.HasExited) 'A mismatched --wait-start identity skipped waiting for an unrelated live process'
                    [IO.File]::WriteAllText((Join-Path $holdTarget 'release.flag'),'release')
                }
                $null = $hold.WaitForExit(5000)
                $processes.Add([ordered]@{name="wait-alias-$name";executable=[IO.Path]::GetFileName($waitSetup);arguments=$bundleArguments;exitCode=$bundle.ExitCode})
            } finally {
                # Release only this test's own benign helper. Never terminate an
                # installation process or another application's process.
                [IO.File]::WriteAllText((Join-Path $holdTarget 'release.flag'),'release')
                $null = $hold.WaitForExit(5000)
                $hold.Dispose(); $bundle.Dispose()
            }
            $null = Invoke-Cli -Arguments @('uninstall',$waitId,'--silent','--state-root',$waitState,'--scope','user') -Name "uninstall-wait-$name"
            Assert-Check (-not (Test-Path -LiteralPath $waitFile)) "The $name identity fixture was removed after acceptance"
        }
    }
    $receipt.success = $true
} catch {
    $receipt.error = $_.Exception.ToString()
    throw
} finally {
    $state = [HeirowInstaller.Acceptance.NativeMsi]::MsiQueryProductState($productCode)
    if ($state -ne -1) {
        # Only the fresh GUID created above is eligible for cleanup.
        $cleanup.cleanupProductCode = $productCode
        try { $null = Invoke-FixtureProcess -Executable (Join-Path $env:SystemRoot 'System32/msiexec.exe') -Arguments @('/x',$productCode,'/qn','/norestart','/L*v',(Join-Path $runDirectory 'msi-cleanup.log')) -Name 'cleanup-msi' }
        catch { $cleanup.cleanupError = $_.Exception.Message; $receipt.success = $false }
    }
    $cleanup.msiProductState = [HeirowInstaller.Acceptance.NativeMsi]::MsiQueryProductState($productCode)
    $exeFixtureState = Assert-SandboxPath (Join-Path $stateDirectory $exeProductId)
    if (Test-Path -LiteralPath (Join-Path $exeFixtureState 'receipt.json')) {
        try { $null = Invoke-Cli -Arguments @('uninstall',$exeProductId,'--silent','--scope','user','--state-root',$stateDirectory) -Name 'cleanup-exe' }
        catch { $cleanup.executableCleanupError = $_.Exception.Message; $receipt.success = $false }
    }
    if (-not $SkipExecutableFixture) {
        $cleanup.executablePayloadAbsent = -not (Test-Path -LiteralPath (Join-Path $exeTarget 'external-fixture.txt'))
        $cleanup.executableReceiptAbsent = -not (Test-Path -LiteralPath (Join-Path $exeFixtureState 'receipt.json'))
    }
    if ($TestWaitAliases) {
        $waitCleanupState = Assert-SandboxPath (Join-Path $sandbox 'wait-state')
        $waitCleanupId = "adapter-wait-$shortId"
        if (Test-Path -LiteralPath (Join-Path $waitCleanupState "$waitCleanupId/receipt.json")) {
            try { $null = Invoke-Cli -Arguments @('uninstall',$waitCleanupId,'--silent','--scope','user','--state-root',$waitCleanupState) -Name 'cleanup-wait-native' }
            catch { $cleanup.waitCleanupError = $_.Exception.Message; $receipt.success = $false }
        }
        $cleanup.waitPayloadAbsent = -not (Test-Path -LiteralPath (Join-Path $sandbox 'wait-target/wait-fixture.txt'))
    }
    $cleanup.msiPayloadAbsent = -not (Test-Path -LiteralPath (Join-Path $authoredTarget 'adapter.txt'))
    $cleanup.msiReceiptAbsent = -not (Test-Path -LiteralPath (Join-Path $stateDirectory "$productId/receipt.json"))
    [IO.File]::WriteAllText((Join-Path $runDirectory 'receipt.json'),($receipt | ConvertTo-Json -Depth 12))
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'latest.json'),([ordered]@{runDirectory=$runDirectory;success=$receipt.success;receipt=(Join-Path $runDirectory 'receipt.json')} | ConvertTo-Json))
    Write-Output ([ordered]@{success=$receipt.success;checks=$checks.Count;runDirectory=$runDirectory;receipt=(Join-Path $runDirectory 'receipt.json');cleanup=$cleanup} | ConvertTo-Json -Depth 6)
}
