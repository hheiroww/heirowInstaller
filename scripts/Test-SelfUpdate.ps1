#requires -Version 7.0
<#
.SYNOPSIS
Exercises the actual native App -> SocketJack download -> trusted helper -> reopen self-update route.
.DESCRIPTION
Creates a unique signed per-user fixture in an ignored artifact directory. The
test uses only a loopback HTTP server, its own signing identity, an isolated
state directory and a unique target. It does not publish or mutate a real app.
#>
[CmdletBinding()]
param([string]$CliPath,[Parameter(Mandatory)][string]$LauncherPath,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native self-update acceptance requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $CliPath) { $CliPath = Join-Path $projectRoot 'src/HeirowInstaller.Cli/bin/Release/net10.0/heirowInstaller.Cli.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/windows-adapter-smoke' }
$CliPath = [IO.Path]::GetFullPath($CliPath)
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
foreach ($required in @($CliPath,$LauncherPath)) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required executable is missing: $required" } }
$runId = [Guid]::NewGuid().ToString('N').Substring(0,12)
$runDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "self-update-$runId"))
if (Test-Path -LiteralPath $runDirectory) { throw 'The unique self-update fixture directory already exists.' }
$sandbox = Join-Path $runDirectory 'sandbox'
$state = Join-Path $sandbox 'state'
$target = Join-Path $sandbox 'application'
$public = Join-Path $runDirectory 'public'
$productId = "self-update-$runId"
$installedExe = Join-Path $target 'heirowInstaller.exe'
$installedReceipt = Join-Path $state "$productId/receipt.json"
$hookReport = Join-Path $runDirectory 'native-handoff.json'
$checks = [Collections.Generic.List[string]]::new()
$receipt = [ordered]@{success=$false;productId=$productId;scope='user';isolated=$true;runDirectory=$runDirectory;checks=$checks;cleanup=[ordered]@{}}
$server = $null; $oldApp = $null; $newApp = $null
New-Item -ItemType Directory -Path $runDirectory,$sandbox,$public -Force | Out-Null

function Assert-Check([bool]$Condition,[string]$Description) { if (-not $Condition) { throw $Description }; $checks.Add($Description) }
function Invoke-Child([string]$Executable,[string[]]$Arguments,[string]$Name,[int[]]$SuccessCodes=@(0,3010)) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; $start.WorkingDirectory=$runDirectory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child=[Diagnostics.Process]::Start($start)
    $stdoutTask=$child.StandardOutput.ReadToEndAsync();$stderrTask=$child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(180000)) { throw "$Name did not complete within three minutes." }
    $stdout=$stdoutTask.GetAwaiter().GetResult();$stderr=$stderrTask.GetAwaiter().GetResult();$exitCode=$child.ExitCode
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.txt"),$stdout);[IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.txt"),$stderr)
    $child.Dispose()
    if ($SuccessCodes -notcontains $exitCode) { throw "$Name failed with exit code $exitCode. $stderr" }
    return $stdout
}
function Invoke-Cli([string[]]$Arguments,[string]$Name) { return Invoke-Child -Executable $CliPath -Arguments $Arguments -Name $Name }
function Wait-For([scriptblock]$Condition,[string]$Description,[int]$Seconds=90) {
    $until=[DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if (Test-Path -LiteralPath $hookReport) {
            try { $hook=Get-Content -LiteralPath $hookReport -Raw | ConvertFrom-Json; if ($hook.phase -eq 'failed') { throw "Native self-update hook failed: $($hook.error)" } }
            catch [System.Management.Automation.RuntimeException] { if ($_.Exception.Message -like 'Native self-update hook failed:*') { throw } }
        }
        if ([DateTime]::UtcNow -ge $until) { throw "Timed out: $Description" }
        Start-Sleep -Milliseconds 100
    }
}
function Stop-OwnedApp([Diagnostics.Process]$Process) {
    if ($null -eq $Process -or $Process.HasExited) { return }
    if (-not $Process.Path.Equals($installedExe,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to close a process outside the unique fixture target.' }
    $null=$Process.CloseMainWindow()
    if (-not $Process.WaitForExit(10000)) { $Process.Kill();$Process.WaitForExit() }
}

try {
    # This ephemeral private key signs only the ignored acceptance packages.
    $keyPath=Join-Path $runDirectory 'acceptance-key.pem'
    $rsa=[Security.Cryptography.RSA]::Create(3072)
    try { [IO.File]::WriteAllText($keyPath,$rsa.ExportPkcs8PrivateKeyPem()) } finally { $rsa.Dispose() }
    foreach ($version in @('1.0.0','1.0.1')) {
        $source=Join-Path $runDirectory "source-$version"
        New-Item -ItemType Directory -Path $source -Force | Out-Null
        $appCopy=Join-Path $source 'heirowInstaller.exe'
        Copy-Item -LiteralPath $LauncherPath -Destination $appCopy
        if ($version -eq '1.0.0') {
            # A benign non-package overlay changes the old image hash while
            # preserving the same runnable .NET single-file application.
            $overlay=[Text.Encoding]::UTF8.GetBytes("`nheirowInstaller self-update acceptance old image $runId`n")
            $file=[IO.File]::Open($appCopy,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $file.Write($overlay,0,$overlay.Length) } finally { $file.Dispose() }
        }
        [IO.File]::WriteAllText((Join-Path $source 'generation.txt'),$version)
        $manifest=[ordered]@{formatVersion=1;id=$productId;name='Native self-update acceptance';version=$version;publisher='heirowInstaller acceptance';scope='user';entryPoint='heirowInstaller.exe';payload=@(@{source='heirowInstaller.exe';path='heirowInstaller.exe'},@{source='generation.txt';path='generation.txt'})}
        $manifestPath=Join-Path $runDirectory "manifest-$version.json"
        [IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json -Depth 8))
        $package=Join-Path $runDirectory "fixture-$version.hinst"
        $null=Invoke-Cli -Arguments @('pack',$manifestPath,'--source',$source,'--output',$package,'--sign-key',$keyPath) -Name "pack-$version"
    }
    $oldPackage=Join-Path $runDirectory 'fixture-1.0.0.hinst'
    $newPackage=Join-Path $runDirectory 'fixture-1.0.1.hinst'
    $inspection=(Invoke-Cli -Arguments @('inspect',$oldPackage) -Name 'inspect-old') | ConvertFrom-Json
    $pin=$inspection.publisherKeySha256
    Assert-Check ([bool]$inspection.signed -and $pin.Length -eq 64) 'The initial native fixture package has a real RSA-PSS publisher identity'
    $receipt.publisherKeySha256=$pin
    # The signed suffix authenticates the native App payload, not an arbitrary
    # executable prefix. This benign trap proves the download remains data.
    $prefixProject=Join-Path $runDirectory 'untrusted-prefix-fixture'
    New-Item -ItemType Directory -Path $prefixProject -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $prefixProject 'PrefixTrap.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>PrefixTrap</AssemblyName></PropertyGroup></Project>')
    $prefixMarker=Join-Path $runDirectory 'prefix-executed.txt'
    $prefixSource='File.WriteAllText("__MARKER__", "The untrusted downloaded prefix was executed."); return 41;'
    [IO.File]::WriteAllText((Join-Path $prefixProject 'Program.cs'),$prefixSource.Replace('__MARKER__',$prefixMarker.Replace('\','\\').Replace('"','\"')))
    $prefixPublish=Join-Path $prefixProject 'publish'
    $null=Invoke-Child -Executable (Get-Command dotnet.exe -ErrorAction Stop).Source -Arguments @('publish',(Join-Path $prefixProject 'PrefixTrap.csproj'),'-c','Release','-r','win-x64','--self-contained','false','-p:PublishSingleFile=true','-p:DebugType=None','-o',$prefixPublish,'--nologo','--verbosity','quiet') -Name 'build-prefix-trap' -SuccessCodes @(0)
    $setup=Join-Path $public 'fixture-Setup.exe'
    $null=Invoke-Cli -Arguments @('bundle',$newPackage,'--launcher',(Join-Path $prefixPublish 'PrefixTrap.exe'),'--output',$setup) -Name 'bundle-new'
    $null=Invoke-Cli -Arguments @('verify',$setup,'--silent','--trust-key',$pin,'--require-signature') -Name 'verify-new-bundle'
    $null=Invoke-Cli -Arguments @('install',$oldPackage,'--silent','--isolated','--state-root',$state,'--target',$target,'--scope','user','--no-register','--trust-key',$pin,'--require-signature') -Name 'install-old'
    $before=Get-Content -LiteralPath $installedReceipt -Raw | ConvertFrom-Json
    $oldHash=(Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $newHash=(Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Check ($before.version -eq '1.0.0' -and $before.publisherKeySha256 -eq $pin -and $before.isolated) 'The initial isolated installation persisted the intended version and publisher pin'
    Assert-Check ($oldHash -ne $newHash) 'The old running image differs from the expected new executable hash'

    $serverProject=Join-Path $runDirectory 'loopback-server'
    New-Item -ItemType Directory -Path $serverProject -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $serverProject 'LoopbackServer.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>LoopbackServer</AssemblyName></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $serverProject 'Program.cs'),@'
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
var root=Path.GetFullPath(args[0]);var ready=Path.GetFullPath(args[1]);var stop=Path.GetFullPath(args[2]);var log=Path.GetFullPath(args[3]);
var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
File.WriteAllText(ready,JsonSerializer.Serialize(new {port=((IPEndPoint)listener.LocalEndpoint).Port}));
var deadline=DateTime.UtcNow.AddMinutes(5);
try {
    while(!File.Exists(stop)&&DateTime.UtcNow<deadline) {
        if(!listener.Pending()){await Task.Delay(30);continue;}
        using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();
        try {
            using var reader=new StreamReader(stream,Encoding.ASCII,false,8192,true);
            var request=await reader.ReadLineAsync()??"";var parts=request.Split(' ');var path=parts.Length>1?parts[1]:"";var agent="";
            for(var count=0;count<64;count++){var line=await reader.ReadLineAsync();if(string.IsNullOrEmpty(line))break;if(line.StartsWith("User-Agent:",StringComparison.OrdinalIgnoreCase))agent=line[11..].Trim();}
            var name=path switch {"/update.json"=>"update.json","/fixture-Setup.exe"=>"fixture-Setup.exe",_=>null};
            if(parts.Length<2||parts[0]!="GET"||name is null){await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));continue;}
            await using var input=File.OpenRead(Path.Combine(root,name));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {input.Length}\r\nConnection: close\r\n\r\n"));
            await input.CopyToAsync(stream);await stream.FlushAsync();
            File.AppendAllText(log,JsonSerializer.Serialize(new {path,size=input.Length,userAgent=agent,timestamp=DateTimeOffset.UtcNow})+Environment.NewLine);
        } catch(Exception error) {File.AppendAllText(log,JsonSerializer.Serialize(new {error=error.GetType().Name})+Environment.NewLine);}
    }
} finally {listener.Stop();}
'@)
    $serverPublish=Join-Path $serverProject 'publish'
    $null=Invoke-Child -Executable (Get-Command dotnet.exe -ErrorAction Stop).Source -Arguments @('publish',(Join-Path $serverProject 'LoopbackServer.csproj'),'-c','Release','-r','win-x64','--self-contained','false','-p:PublishSingleFile=true','-p:DebugType=None','-o',$serverPublish,'--nologo','--verbosity','quiet') -Name 'build-loopback-server' -SuccessCodes @(0)
    $ready=Join-Path $runDirectory 'server-ready.json';$stop=Join-Path $runDirectory 'server-stop.flag';$requestLog=Join-Path $runDirectory 'server-requests.jsonl'
    $serverStart=[Diagnostics.ProcessStartInfo]::new((Join-Path $serverPublish 'LoopbackServer.exe'))
    $serverStart.UseShellExecute=$false;$serverStart.CreateNoWindow=$true
    foreach($argument in @($public,$ready,$stop,$requestLog)){$serverStart.ArgumentList.Add($argument)}
    $server=[Diagnostics.Process]::Start($serverStart)
    Wait-For -Condition {Test-Path -LiteralPath $ready} -Description 'the bounded loopback server' -Seconds 15
    $port=(Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json).port
    $origin="http://127.0.0.1:$port"
    $feed=[ordered]@{productId=$productId;name='Native self-update acceptance';publisher='heirowInstaller acceptance';version='1.0.1';installerUrl="$origin/fixture-Setup.exe";sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant();size=(Get-Item -LiteralPath $setup).Length;packageType='exe';releaseNotes='Bounded isolated native self-update acceptance';arguments=@('--silent')}
    [IO.File]::WriteAllText((Join-Path $public 'update.json'),($feed | ConvertTo-Json -Depth 6))
    $start=[Diagnostics.ProcessStartInfo]::new($installedExe)
    $start.UseShellExecute=$false;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$start.WorkingDirectory=$target
    foreach($argument in @('--isolated','--target',$target,'--state-root',$state,'--self-update-smoke-feed',"$origin/update.json",'--self-update-smoke-report',$hookReport,'--capture',(Join-Path $runDirectory 'old-native-window.png'))){$start.ArgumentList.Add($argument)}
    $oldApp=[Diagnostics.Process]::Start($start)
    $receipt.oldPid=$oldApp.Id
    Wait-For -Condition {Test-Path -LiteralPath $hookReport} -Description 'the actual native controller update check and handoff' -Seconds 90
    $hook=Get-Content -LiteralPath $hookReport -Raw | ConvertFrom-Json
    Assert-Check ($hook.phase -eq 'handoff' -and $hook.oldPid -eq $oldApp.Id -and $hook.publisherKeySha256 -eq $pin) 'The running native controller checked the real feed and initiated its actual trusted self-update handoff'
    Wait-For -Condition {$oldApp.HasExited} -Description 'the original native application to exit' -Seconds 30
    Assert-Check $oldApp.HasExited 'The old native application exited before its replacement completed'
    Wait-For -Condition {
        if(-not (Test-Path -LiteralPath $installedReceipt)){return $false}
        try { (Get-Content -LiteralPath $installedReceipt -Raw | ConvertFrom-Json).version -eq '1.0.1' } catch { $false }
    } -Description 'the signed replacement installation receipt' -Seconds 90
    $after=Get-Content -LiteralPath $installedReceipt -Raw | ConvertFrom-Json
    Assert-Check ($after.publisherKeySha256 -eq $pin -and $after.isolated) 'The replacement receipt retained the pinned publisher and isolated scope'
    Assert-Check ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant() -eq $newHash) 'The actual installed executable changed from the old image to the verified new payload hash'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $target 'generation.txt')) -eq '1.0.1') 'The signed update installed the new payload generation'
    Assert-Check (-not (Test-Path -LiteralPath $prefixMarker)) 'The untrusted downloaded EXE prefix was never executed; the trusted installed helper consumed its signed package as data'
    Wait-For -Condition {
        $script:selfUpdateReopened=@(Get-Process -Name heirowInstaller -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $oldApp.Id -and $_.Path -eq $installedExe -and $_.MainWindowHandle -ne 0 }) | Select-Object -First 1
        $null -ne $script:selfUpdateReopened
    } -Description 'the updated native application to reopen its actual desktop window' -Seconds 45
    $newApp=$script:selfUpdateReopened
    Assert-Check ($newApp.Id -ne $oldApp.Id -and $newApp.Responding -and $newApp.MainWindowTitle.Contains('heirowInstaller')) 'The updated executable reopened as a responsive native desktop window at the same installed path'
    $requests=@(Get-Content -LiteralPath $requestLog | ForEach-Object {$_ | ConvertFrom-Json})
    Assert-Check (@($requests | Where-Object {$_.path -eq '/update.json' -and $_.userAgent -like '*SocketJack*'}).Count -gt 0 -and @($requests | Where-Object {$_.path -eq '/fixture-Setup.exe' -and $_.userAgent -like '*SocketJack*'}).Count -gt 0) 'Loopback server receipts confirm both feed and bundled installer traveled through the real SocketJack transport'
    $receipt.newPid=$newApp.Id;$receipt.oldExecutableSha256=$oldHash;$receipt.newExecutableSha256=$newHash;$receipt.newVersion=$after.version
    Stop-OwnedApp $newApp
    $null=Invoke-Cli -Arguments @('uninstall',$productId,'--silent','--state-root',$state,'--scope','user') -Name 'uninstall-updated-fixture'
    Assert-Check (-not (Test-Path -LiteralPath $installedExe) -and -not (Test-Path -LiteralPath $installedReceipt)) 'The updated native fixture was uninstalled with no target executable or active receipt remaining'
    $receipt.success=$true
} catch {$receipt.error=$_.Exception.ToString();throw}
finally {
    if($newApp){try{Stop-OwnedApp $newApp}catch{$receipt.cleanup.appCloseError=$_.Exception.Message}}
    if($server){
        [IO.File]::WriteAllText((Join-Path $runDirectory 'server-stop.flag'),'stop')
        if(-not $server.WaitForExit(10000)){$server.Kill();$server.WaitForExit()}
        $receipt.cleanup.loopbackServerExited=$server.HasExited;$server.Dispose()
    }
    $receipt.cleanup.installedExecutableAbsent=-not (Test-Path -LiteralPath $installedExe)
    $receipt.cleanup.installedReceiptAbsent=-not (Test-Path -LiteralPath $installedReceipt)
    [IO.File]::WriteAllText((Join-Path $runDirectory 'self-update-receipt.json'),($receipt | ConvertTo-Json -Depth 10))
    Write-Output ([ordered]@{success=$receipt.success;checks=$checks.Count;runDirectory=$runDirectory;receipt=(Join-Path $runDirectory 'self-update-receipt.json');cleanup=$receipt.cleanup} | ConvertTo-Json -Depth 5)
}
