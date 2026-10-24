#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Cli,
    [string]$Wix,
    [string]$Companion,
    [ValidateSet('user','machine')][string]$CompanionScope = 'machine',
    [string]$SdkVersion = '5.0.2',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The actual Windows Installer acceptance test requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Cli) { $Cli = Join-Path $projectRoot 'src/HeirowInstaller.Cli/bin/Release/net10.0/heirowInstaller.Cli.exe' }
if (-not $Wix) { $Wix = Join-Path $projectRoot 'artifacts/tools/wix5/wix.exe' }
$Cli = [IO.Path]::GetFullPath($Cli); $Wix = [IO.Path]::GetFullPath($Wix)
if (-not (Test-Path -LiteralPath $Cli) -or -not (Test-Path -LiteralPath $Wix)) { throw 'Pass the built CLI and an explicitly selected official WiX executable.' }
$id = [Guid]::NewGuid().ToString('N')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/wix-authoring/' + $id) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new acceptance output directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$target = Join-Path $env:LOCALAPPDATA ('Temp/heirowInstaller-wix-' + $id)
$productCode = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$upgradeCode = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$registryRelative = 'Software\SocketJack\heirowInstaller\AuthoringTests\' + $id
$registry = 'HKCU:\' + $registryRelative
$checks = [Collections.Generic.List[string]]::new()
function Assert-Check([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    $checks.Add($Message)
    Write-Host "PASS $Message"
}
function Invoke-Tool([string]$Executable,[string[]]$Arguments,[switch]$AllowFailure) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $result = [pscustomobject]@{ExitCode=$process.ExitCode;Output=$stdout.GetAwaiter().GetResult();Error=$stderr.GetAwaiter().GetResult()}
        if (-not $AllowFailure -and $result.ExitCode -notin @(0,3010)) { throw "Tool failed ($($result.ExitCode)): $($result.Error) $($result.Output)" }
        return $result
    } finally { $process.Dispose() }
}
function Compile([string]$InputPath,[string]$Output,[string[]]$More = @()) {
    $arguments = @('compile',$InputPath,'--output',$Output,'--wix',$Wix,'--arch','x64') + $More
    $result = Invoke-Tool $Cli $arguments
    $build = $result.Output | ConvertFrom-Json
    Assert-Check (Test-Path -LiteralPath $Output) ('Compiled ' + [IO.Path]::GetExtension($Output) + ' from ' + [IO.Path]::GetExtension($InputPath))
    Assert-Check ($build.sha256 -eq (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()) 'Compiler result matches the artifact SHA-256'
}
function Write-Template([string]$Path,[string]$Template,[hashtable]$Values) {
    foreach ($key in $Values.Keys) { $Template = $Template.Replace('@@' + $key + '@@',[Security.SecurityElement]::Escape([string]$Values[$key])) }
    [IO.File]::WriteAllText($Path,$Template)
}
if (-not ('HeirowInstallerAuthoringProductState' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class HeirowInstallerAuthoringProductState {
    [DllImport("msi.dll", EntryPoint="MsiQueryProductStateW", CharSet=CharSet.Unicode)]
    public static extern int Query(string productCode);
    [DllImport("msi.dll", EntryPoint="MsiOpenDatabaseW", CharSet=CharSet.Unicode)] static extern uint Open(string path, IntPtr mode, out uint handle);
    [DllImport("msi.dll", EntryPoint="MsiDatabaseOpenViewW", CharSet=CharSet.Unicode)] static extern uint View(uint database, string sql, out uint handle);
    [DllImport("msi.dll", EntryPoint="MsiViewExecute")] static extern uint Execute(uint view, uint record);
    [DllImport("msi.dll", EntryPoint="MsiViewFetch")] static extern uint Fetch(uint view, out uint record);
    [DllImport("msi.dll", EntryPoint="MsiRecordGetStringW", CharSet=CharSet.Unicode)] static extern uint Read(uint record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll", EntryPoint="MsiCloseHandle")] static extern uint Close(uint handle);
    static void Check(uint error) { if(error != 0) throw new IOException("MSI fixture table inspection failed: " + error); }
    static List<string[]> Rows(uint database, string sql, int columns) {
        uint view; Check(View(database, sql, out view));
        try {
            Check(Execute(view, 0)); var rows = new List<string[]>();
            while(true) {
                uint record; var error = Fetch(view, out record); if(error == 259) break; Check(error);
                try {
                    var row = new string[columns];
                    for(uint field=1; field<=columns; field++) {
                        uint length=0; error=Read(record,field,null,ref length); if(error!=0 && error!=234) Check(error);
                        var value=new StringBuilder((int)length+1); length++; Check(Read(record,field,value,ref length)); row[field-1]=value.ToString();
                    }
                    rows.Add(row);
                } finally { Close(record); }
            }
            return rows;
        } finally { Close(view); }
    }
    public static void AssertContainedComponents(string path) {
        uint database; Check(Open(path,IntPtr.Zero,out database));
        try {
            var parents=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var row in Rows(database,"SELECT `Directory`, `Directory_Parent` FROM `Directory`",2)) parents.Add(row[0],row[1]);
            foreach(var row in Rows(database,"SELECT `Directory_` FROM `Component`",1)) {
                var current=row[0]; var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while(current!="INSTALLFOLDER") {
                    if(!visited.Add(current) || !parents.TryGetValue(current,out current) || String.IsNullOrEmpty(current))
                        throw new InvalidDataException("A compiled fixture component escapes INSTALLFOLDER; installation was blocked.");
                }
            }
        } finally { Close(database); }
    }
}
'@
}
$installed = $false
$outerId = $null; $outerState = $null
function Remove-OwnedMachineState {
    if ($CompanionScope -ne 'machine' -or -not $outerState -or -not (Test-Path -LiteralPath $outerState)) { return }
    $resolved = [IO.Path]::GetFullPath($outerState)
    $parent = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $parent -or [IO.Path]::GetFileName($resolved) -ne ('heirowInstaller-Acceptance-' + $id)) { throw 'Unexpected fixture state directory; cleanup refused.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
try {
    Assert-Check ([HeirowInstallerAuthoringProductState]::Query($productCode) -eq -1) 'The unique fixture ProductCode is not installed'
    Assert-Check (-not (Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $registry)) 'The unique fixture directories and registry key do not exist'
    foreach ($folder in @('module','v1','v2','project','patch')) { New-Item -ItemType Directory -Path (Join-Path $OutputDirectory $folder) | Out-Null }
    $module = Join-Path $OutputDirectory 'module'
    [IO.File]::WriteAllText((Join-Path $module 'module.txt'),'merge-module payload')
    Write-Template (Join-Path $module 'Module.wxs') @'
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Module Id="AuthoringFixtureModule" Guid="@@ModuleGuid@@" Language="1033" Version="1.0.0">
    <SummaryInformation Manufacturer="heirowInstaller acceptance" />
    <StandardDirectory Id="TARGETDIR">
      <Directory Id="MergeRedirectFolder">
      <Component Id="ModulePayload" Guid="@@ComponentGuid@@">
        <File Id="ModuleFile" Source="module.txt" />
        <RegistryValue Root="HKCU" Key="@@Registry@@" Name="Module" Value="merged" Type="string" KeyPath="yes" />
        <RemoveFolder Id="RemoveModuleFixtureFolder" On="uninstall" />
      </Component>
      </Directory>
    </StandardDirectory>
  </Module>
</Wix>
'@ @{ModuleGuid=[Guid]::NewGuid().ToString();ComponentGuid=[Guid]::NewGuid().ToString();Registry=$registryRelative}
    $msm = Join-Path $module 'fixture.msm'; Compile (Join-Path $module 'Module.wxs') $msm

    $packageTemplate = @'
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">
  <Package Name="heirowInstaller authoring fixture @@Id@@" Manufacturer="heirowInstaller acceptance" Version="@@Version@@" ProductCode="@@ProductCode@@" UpgradeCode="@@UpgradeCode@@" Language="1033" Scope="perUser">
    <MediaTemplate EmbedCab="yes" />
    <SetProperty Id="AUTHORING_CUSTOM_ACTION" Value="$(var.FixtureValue)" Before="InstallInitialize" Sequence="execute" />
    <SetProperty Id="ARPINSTALLLOCATION" Value="[INSTALLFOLDER]" After="CostFinalize" Sequence="execute" />
    <Property Id="INSTALLFOLDER"><RegistrySearch Id="ExistingFixtureDirectory" Root="HKCU" Key="@@Registry@@" Name="Directory" Type="raw" /></Property>
    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="INSTALLFOLDER" Name="heirowInstaller-authoring-@@Id@@">
        <Component Id="Payload" Guid="@@PayloadGuid@@">
          <File Id="PayloadFile" Source="payload.txt" />
          <RegistryValue Root="HKCU" Key="@@Registry@@" Name="CustomAction" Value="[AUTHORING_CUSTOM_ACTION]" Type="string" KeyPath="yes" />
          <RegistryValue Root="HKCU" Key="@@Registry@@" Name="Directory" Value="[INSTALLFOLDER]" Type="string" />
          <RemoveFolder Id="RemoveFixtureFolder" On="uninstall" />
        </Component>
        <Component Id="Configuration" Guid="@@ConfigurationGuid@@">
          <File Id="ConfigurationFile" Source="settings.xml" />
          <RegistryValue Root="HKCU" Key="@@Registry@@" Name="Configuration" Value="installed" Type="string" KeyPath="yes" />
          <util:XmlFile Id="ConfigureXml" File="[INSTALLFOLDER]settings.xml" Action="setValue" ElementPath="/settings" Name="source" Value="extension-ran" SelectionLanguage="XPath" />
        </Component>
        <Merge Id="FixtureModule" Language="1033" SourceFile="../module/fixture.msm" DiskId="1" />
      </Directory>
    </StandardDirectory>
    <Feature Id="Core" Level="1"><ComponentRef Id="Payload" /><ComponentRef Id="Configuration" /><MergeRef Id="FixtureModule" /></Feature>
  </Package>
</Wix>
'@
    $values = @{Id=$id;Version='1.0.0';ProductCode=$productCode;UpgradeCode=$upgradeCode;PayloadGuid=[Guid]::NewGuid().ToString();ConfigurationGuid=[Guid]::NewGuid().ToString();Registry=$registryRelative}
    foreach ($generation in @('v1','v2')) {
        $directory = Join-Path $OutputDirectory $generation
        $values.Version = if ($generation -eq 'v1') { '1.0.0' } else { '1.0.1' }
        Write-Template (Join-Path $directory 'Package.wxs') $packageTemplate $values
        [IO.File]::WriteAllText((Join-Path $directory 'payload.txt'),"payload $generation")
        [IO.File]::WriteAllText((Join-Path $directory 'settings.xml'),'<settings source="original" />')
    }
    $project = Join-Path $OutputDirectory 'project'
    Write-Template (Join-Path $project 'Fixture.wixproj') @'
<Project Sdk="WixToolset.Sdk/@@SdkVersion@@">
  <PropertyGroup><DefineConstants>FixtureValue=custom-action-ran</DefineConstants></PropertyGroup>
  <ItemGroup>
    <Compile Include="../v1/Package.wxs" />
    <BindPath Include="../v1" />
    <PackageReference Include="WixToolset.Util.wixext" Version="@@SdkVersion@@" />
  </ItemGroup>
</Project>
'@ @{SdkVersion=$SdkVersion}
    Compile (Join-Path $project 'Fixture.wixproj') (Join-Path $project 'project.msi') @('--compiler-arg','-m:1','--compiler-arg','-nr:false','--compiler-arg','-p:UseSharedCompilation=false')
    # Use the exact extension restored by the authored project, not a globally selected latest extension.
    $assets = Get-Content -LiteralPath (Join-Path $project 'obj/project.assets.json') -Raw | ConvertFrom-Json
    $util = $null
    foreach ($cacheRoot in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $cacheRoot ("wixtoolset.util.wixext/$SdkVersion")
        if (Test-Path -LiteralPath $candidate) {
            $util = Get-ChildItem -LiteralPath $candidate -Filter 'WixToolset.Util.wixext.dll' -File -Recurse | Select-Object -First 1 -ExpandProperty FullName
            if ($util) { break }
        }
    }
    if (-not $util) { throw 'The exact project extension assembly was not restored.' }
    foreach ($generation in @('v1','v2')) {
        $directory = Join-Path $OutputDirectory $generation
        Compile (Join-Path $directory 'Package.wxs') (Join-Path $directory 'fixture.msi') @('--extension',$util,'--define','FixtureValue=custom-action-ran')
    }
    $patch = Join-Path $OutputDirectory 'patch'
    [IO.File]::WriteAllText((Join-Path $patch 'Patch.wxs'),@'
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Patch AllowRemoval="yes" DisplayName="heirowInstaller fixture patch" Description="Isolated patch acceptance" Manufacturer="heirowInstaller acceptance" Classification="Update">
    <Media Id="1" Cabinet="patch.cab"><PatchBaseline Id="RTM" BaselineFile="../v1/fixture.msi" UpdateFile="../v2/fixture.msi" /></Media>
    <PatchFamily Id="FixturePatchFamily" Version="1.0.1" />
  </Patch>
</Wix>
'@)
    $msp = Join-Path $patch 'fixture.msp'; Compile (Join-Path $patch 'Patch.wxs') $msp
    $badInput = Join-Path $OutputDirectory 'invalid.wxs'; [IO.File]::WriteAllText($badInput,'<invalid />')
    $preserved = Join-Path $OutputDirectory 'previous.msi'; [IO.File]::WriteAllText($preserved,'existing artifact')
    $rejected = Invoke-Tool $Cli @('compile',$badInput,'--output',$preserved,'--wix',$Wix) -AllowFailure
    Assert-Check ($rejected.ExitCode -ne 0 -and [IO.File]::ReadAllText($preserved) -eq 'existing artifact') 'Compiler errors preserve the previous output artifact'
    Assert-Check (@(Get-ChildItem -LiteralPath $OutputDirectory -Directory -Filter '.heirow-build-*').Count -eq 0) 'Compiler failures clean only their own temporary build folder'

    $v1 = Join-Path $OutputDirectory 'v1/fixture.msi'
    [HeirowInstallerAuthoringProductState]::AssertContainedComponents($v1)
    Assert-Check $true 'Every compiled MSI/MSM component resolves below the isolated INSTALLFOLDER'
    Invoke-Tool 'msiexec.exe' @('/i',$v1,'/qn','/norestart',"INSTALLFOLDER=$target",'/l*v',(Join-Path $OutputDirectory 'install.log')) | Out-Null
    $installed = $true
    Assert-Check ([HeirowInstallerAuthoringProductState]::Query($productCode) -eq 5) 'The compiled per-user MSI registered its exact ProductCode'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $target 'payload.txt')) -eq 'payload v1') 'The compiled MSI installed its embedded application payload'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $target 'module.txt')) -eq 'merge-module payload') 'The compiled and merged MSM installed its payload'
    Assert-Check ((Get-ItemProperty -LiteralPath $registry).CustomAction -eq 'custom-action-ran') 'The authored custom action executed through Windows Installer'
    Assert-Check (([xml][IO.File]::ReadAllText((Join-Path $target 'settings.xml'))).settings.source -eq 'extension-ran') 'The authored WiX Util extension custom action configured the XML file'
    [IO.File]::Delete((Join-Path $target 'payload.txt'))
    Invoke-Tool 'msiexec.exe' @('/fa',$v1,'/qn','/norestart','/l*v',(Join-Path $OutputDirectory 'repair.log')) | Out-Null
    Assert-Check ([IO.File]::ReadAllText((Join-Path $target 'payload.txt')) -eq 'payload v1') 'Windows Installer repaired the compiled embedded payload'
    Invoke-Tool 'msiexec.exe' @('/p',$msp,'/qn','/norestart','REINSTALL=ALL','REINSTALLMODE=amus','/l*v',(Join-Path $OutputDirectory 'patch.log')) | Out-Null
    Assert-Check ([IO.File]::ReadAllText((Join-Path $target 'payload.txt')) -eq 'payload v2') 'The compiled MSP updated the actual installed application payload'
    Invoke-Tool 'msiexec.exe' @('/x',$productCode,'/qn','/norestart','/l*v',(Join-Path $OutputDirectory 'uninstall.log')) | Out-Null
    $installed = $false
    Assert-Check ([HeirowInstallerAuthoringProductState]::Query($productCode) -eq -1) 'Uninstall removed the exact fixture ProductCode'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $target 'payload.txt')) -and -not (Test-Path -LiteralPath (Join-Path $target 'module.txt'))) 'Uninstall removed the actual compiled application and module payloads'
    Assert-Check (-not (Test-Path -LiteralPath $registry)) 'Uninstall removed the unique fixture registry key'
    if ($Companion) {
        if ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) { Remove-Item -LiteralPath $target }
        $target = Join-Path $env:LOCALAPPDATA ('Temp/heirowInstaller MSI target ' + $id)
        $Companion = [IO.Path]::GetFullPath($Companion)
        if (-not (Test-Path -LiteralPath $Companion)) { throw 'The selected published companion does not exist.' }
        $outer = Join-Path $OutputDirectory 'outer'; New-Item -ItemType Directory -Path $outer | Out-Null
        Copy-Item -LiteralPath $v1 -Destination (Join-Path $outer 'fixture.msi')
        Copy-Item -LiteralPath $Companion -Destination (Join-Path $outer 'heirowUpdater.exe')
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        try {
            $key = Join-Path $OutputDirectory 'fixture.private.pem'; [IO.File]::WriteAllText($key,$rsa.ExportPkcs8PrivateKeyPem())
            $pin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
        } finally { $rsa.Dispose() }
        $outerId = 'test.wix.' + $id
        if ($CompanionScope -eq 'machine') {
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            try { if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Machine maintenance acceptance requires an already elevated administrator process.' } } finally { $identity.Dispose() }
            $outerState = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) ('heirowInstaller-Acceptance-' + $id)
        } else { $outerState = Join-Path $OutputDirectory 'state' }
        Assert-Check (-not (Test-Path -LiteralPath $outerState)) 'The dedicated maintenance fixture state does not exist'
        $manifest = [ordered]@{id=$outerId;name='Isolated compiled MSI companion fixture';version='1.0.0';publisher='heirowInstaller acceptance';scope=$CompanionScope;
            entryPoint='payload.txt';installDirectory=$target;updateUrl='https://updates.example.invalid/fixture.json';
            features=@(@{id='updates';name='Optional companion';default=$false});updateCompanion=@{feature='updates';executablePath='heirowUpdater.exe';configurationPath='heirowUpdater.json';mode='after-exit'};
            legacyMsi=@{payloadPath='fixture.msi';productCode=$productCode;installDirectoryProperty='INSTALLFOLDER';arguments=@('ENCODER_EMPTY=','ENCODER_QUOTES=Acme "Widgets"')};
            payload=@(@{source='fixture.msi';path='fixture.msi'},@{source='heirowUpdater.exe';path='heirowUpdater.exe';feature='updates'})}
        $manifestPath = Join-Path $OutputDirectory 'outer.json'; $package = Join-Path $OutputDirectory 'outer.hinst'
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        Invoke-Tool $Cli @('pack',$manifestPath,'--source',$outer,'--output',$package,'--sign-key',$key) | Out-Null
        $manifest.installDirectory = ''
        $unknownManifest = Join-Path $OutputDirectory 'unknown.json'; $unknownPackage = Join-Path $OutputDirectory 'unknown.hinst'
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $unknownManifest -Encoding utf8NoBOM
        Invoke-Tool $Cli @('pack',$unknownManifest,'--source',$outer,'--output',$unknownPackage,'--sign-key',$key) | Out-Null
        $unknown = Invoke-Tool $Cli @('plan',$unknownPackage,'--features','updates','--state-root',$outerState) -AllowFailure
        Assert-Check ($unknown.ExitCode -ne 0 -and $unknown.Error.Contains('directory is unknown')) 'A fresh MSI with an unknown destination cannot select a guessed companion root'
        $spacedLog = Join-Path $OutputDirectory 'outer install with spaces.log'
        Invoke-Tool $Cli @('install',$package,'--features','updates','--state-root',$outerState,'--silent','--log',$spacedLog) | Out-Null
        $installed = $true
        Assert-Check (Test-Path -LiteralPath (Join-Path $target 'payload.txt')) 'The real MSI installs into the exact directory containing spaces through the adapter'
        Assert-Check ((Test-Path -LiteralPath $spacedLog) -and [IO.File]::ReadAllText($spacedLog).Contains('Acme "Widgets"')) 'The real MSI accepts a spaced log filename and preserves literal property quotes'
        $configurationPath = Join-Path $target 'heirowUpdater.json'
        $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
        Assert-Check ($configuration.installDirectory -eq $target -and $configuration.publisherKeySha256 -eq $pin) 'The MSI companion root and generated configuration match the actual installation and publisher pin'
        Assert-Check (Test-Path -LiteralPath (Join-Path $target 'heirowUpdater.exe')) 'The optional published updater was installed at the actual MSI application root'
        $products = (Invoke-Tool $Cli @('list','--state-root',$outerState)).Output | ConvertFrom-Json
        Assert-Check ($products[0].files.Count -eq 2 -and $products[0].files.path -contains 'heirowUpdater.exe' -and $products[0].files.path -contains 'heirowUpdater.json') 'Outer receipts own only companion files while the MSI keeps ownership of application and module payloads'
        $registryHive = if ($CompanionScope -eq 'machine') { 'HKLM:\' } else { 'HKCU:\' }
        $backendArp = $registryHive + 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $productCode
        $managedArp = $registryHive + 'Software\Microsoft\Windows\CurrentVersion\Uninstall\heirowInstaller.' + $outerId
        Assert-Check ((Get-ItemProperty -LiteralPath $backendArp).SystemComponent -eq 1 -and (Test-Path -LiteralPath $managedArp)) 'The selected companion uses one visible owned maintenance registration and hides only the exact MSI ProductCode'
        Assert-Check ($products[0].registeredProduct -and $products[0].backendRegistrations.Count -eq 1) 'The receipt retains the precise original MSI visibility snapshot'
        $resolved = (Invoke-Tool $Cli @('plan',$unknownPackage,'--features','updates','--state-root',$outerState)).Output | ConvertFrom-Json
        Assert-Check ($resolved.useOriginalInstallDirectory -and $resolved.installDirectory.TrimEnd('\') -eq $target.TrimEnd('\')) 'An existing original-directory MSI resolves its actual registered install location without inventing an override'
        [IO.File]::WriteAllText($configurationPath,'corrupted fixture profile')
        Remove-ItemProperty -LiteralPath $backendArp -Name SystemComponent
        Invoke-Tool $Cli @('repair',$outerId,'--state-root',$outerState,'--silent','--log',(Join-Path $OutputDirectory 'outer-repair.log')) | Out-Null
        $repaired = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
        Assert-Check ($repaired.publisherKeySha256 -eq $pin) 'MSI repair regenerates the pinned optional companion profile'
        Assert-Check ((Get-ItemProperty -LiteralPath $backendArp).SystemComponent -eq 1) 'MSI repair restores maintenance ownership after a backend rewrites its registration'
        Invoke-Tool $Cli @('install',$package,'--features','','--state-root',$outerState,'--silent','--log',(Join-Path $OutputDirectory 'outer-feature-off.log')) | Out-Null
        Assert-Check (-not (Test-Path -LiteralPath $managedArp) -and $null -eq (Get-ItemProperty -LiteralPath $backendArp).SystemComponent -and -not (Test-Path -LiteralPath $configurationPath)) 'Deselecting the companion restores original MSI visibility and removes companion ownership'
        Invoke-Tool $Cli @('install',$package,'--features','updates','--state-root',$outerState,'--silent','--log',(Join-Path $OutputDirectory 'outer-feature-on.log')) | Out-Null
        $ownedReceipt = @((Invoke-Tool $Cli @('list','--state-root',$outerState)).Output | ConvertFrom-Json)[0]
        $registeredCommand = (Get-ItemProperty -LiteralPath $managedArp).QuietUninstallString
        $expectedCommand = '"' + $ownedReceipt.uninstallerPath + '" uninstall "' + $outerId + '" --state-root "' + $outerState + '" --scope ' + $CompanionScope + ' --silent'
        Assert-Check ($registeredCommand -eq $expectedCommand -and (Test-Path -LiteralPath $ownedReceipt.uninstallerPath)) 'Windows Settings has the exact cached maintenance executable and product-specific uninstall command'
        Invoke-Tool $ownedReceipt.uninstallerPath @('uninstall',$outerId,'--state-root',$outerState,'--scope',$CompanionScope,'--silent') | Out-Null
        $installed = $false
        Assert-Check ([HeirowInstallerAuthoringProductState]::Query($productCode) -eq -1 -and -not (Test-Path -LiteralPath $configurationPath) -and -not (Test-Path -LiteralPath (Join-Path $target 'heirowUpdater.exe'))) 'Uninstall removes the compiled MSI product and its outer companion files'
        Assert-Check (@((Invoke-Tool $Cli @('list','--state-root',$outerState)).Output | ConvertFrom-Json).Count -eq 0) 'Companion uninstall removes the outer maintenance receipt'
        Assert-Check (-not (Test-Path -LiteralPath $managedArp) -and -not (Test-Path -LiteralPath $backendArp)) 'The registered Settings uninstall removes both exact maintenance and MSI registrations'
        Invoke-Tool $Cli @('install',$package,'--features','updates','--state-root',$outerState,'--silent','--log',(Join-Path $OutputDirectory 'outer-external-install.log')) | Out-Null
        $installed = $true
        Invoke-Tool 'msiexec.exe' @('/x',$productCode,'/qn','/norestart','/l*v',(Join-Path $OutputDirectory 'outer-external-remove.log')) | Out-Null
        $installed = $false
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $target 'payload.txt')) -and (Test-Path -LiteralPath $configurationPath)) 'An explicit external MSI removal is distinguishable from stale companion state'
        Invoke-Tool $Cli @('uninstall',$outerId,'--state-root',$outerState,'--silent') | Out-Null
        Assert-Check (-not (Test-Path -LiteralPath $configurationPath) -and -not (Test-Path -LiteralPath (Join-Path $target 'heirowUpdater.exe')) -and -not (Test-Path -LiteralPath $managedArp)) 'Maintenance cleans stale companion state after an external backend uninstall without reinstalling the application'
        Remove-OwnedMachineState
        Assert-Check (-not (Test-Path -LiteralPath $outerState)) 'The dedicated protected machine fixture state was removed after all products were uninstalled'
    }
    $receipt = [ordered]@{passed=$checks.Count;productCode=$productCode;target=$target;wix=(Invoke-Tool $Wix @('--version')).Output.Trim();checks=$checks;artifacts=@($msm,$v1,$msp) | ForEach-Object { @{name=[IO.Path]::GetFileName($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()} }}
    $receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'receipt.json') -Encoding utf8NoBOM
    $receipt | ConvertTo-Json -Depth 10
} finally {
    if ($outerId -and $outerState -and (Test-Path -LiteralPath (Join-Path $outerState ($outerId + '/receipt.json')))) {
        Invoke-Tool $Cli @('uninstall',$outerId,'--state-root',$outerState,'--silent','--log',(Join-Path $OutputDirectory 'outer-cleanup.log')) -AllowFailure | Out-Null
    }
    if ($installed -or [HeirowInstallerAuthoringProductState]::Query($productCode) -ne -1) {
        Invoke-Tool 'msiexec.exe' @('/x',$productCode,'/qn','/norestart','/l*v',(Join-Path $OutputDirectory 'cleanup.log')) | Out-Null
    }
    if ([HeirowInstallerAuthoringProductState]::Query($productCode) -eq -1) { Remove-OwnedMachineState }
    # Keep compiler artifacts and logs as evidence. Remove only this unique fixture's now-empty installation directory.
    if ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) { Remove-Item -LiteralPath $target }
}
