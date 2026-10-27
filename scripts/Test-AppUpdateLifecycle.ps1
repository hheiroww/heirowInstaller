#requires -Version 7.0
<#
.SYNOPSIS
Exercises optional app-root, after-exit updates with real published native prompts.
.DESCRIPTION
Uses only generated signed fixtures, a protected loopback feed, ephemeral keys,
and unique isolated targets. The actual bootstrap template is compiled into a
benign application, and the supplied single-file updater contains its own engine.
No globally installed heirowInstaller, service, task, account or app is modified.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishedUpdaterPath, [Parameter(Mandatory)][string]$CliPath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native lifecycle acceptance requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/windows-adapter-smoke' }
$PublishedUpdaterPath = [IO.Path]::GetFullPath($PublishedUpdaterPath); $CliPath = [IO.Path]::GetFullPath($CliPath)
foreach ($path in @($PublishedUpdaterPath, $CliPath)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required binary is missing: $path" } }
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$run = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "app-lifecycle-$runId"))
if (Test-Path -LiteralPath $run) { throw 'Acceptance directory already exists.' }
$public = Join-Path $run 'public'; $sandbox = Join-Path $run 'sandbox'
New-Item -ItemType Directory -Path $run, $public, $sandbox -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$receipt = [ordered]@{ success = $false; runDirectory = $run; isolated = $true; checks = $checks; cleanup = [ordered]@{} }
$server = $null; $apps = [Collections.Generic.List[Diagnostics.Process]]::new()
$fixtures = [Collections.Generic.List[object]]::new(); $uninstalled = [Collections.Generic.HashSet[string]]::new()
$secret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$prefixMarker = Join-Path $run 'untrusted-prefix-executed.txt'
function Assert-Check([bool]$Condition, [string]$Description) { if (-not $Condition) { throw $Description }; $checks.Add($Description) }
function Invoke-Child([string]$Exe, [string[]]$Arguments, [string]$Name, [int[]]$ExpectedCodes = @(0,3010)) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe); $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.WorkingDirectory = $run
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start); $outTask = $child.StandardOutput.ReadToEndAsync(); $errorTask = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(180000)) { throw "$Name exceeded three minutes." }
    $output = $outTask.GetAwaiter().GetResult(); $errors = $errorTask.GetAwaiter().GetResult(); $code = $child.ExitCode; $child.Dispose()
    [IO.File]::WriteAllText((Join-Path $run "$Name.stdout.txt"), $output); [IO.File]::WriteAllText((Join-Path $run "$Name.stderr.txt"), $errors)
    if ($code -notin $ExpectedCodes) { throw "$Name failed ($code). $errors" }; return $output
}
function Invoke-LifecycleCli([string[]]$Arguments, [string]$Name) { Invoke-Child $CliPath $Arguments $Name }
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 90) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (& $Condition)) { if ([DateTime]::UtcNow -ge $until) { throw "Timed out: $Description" }; Start-Sleep -Milliseconds 100 }
}
function Read-Json([string]$Path) { if (Test-Path -LiteralPath $Path) { try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null } }; return $null }
function Request-Manual([object]$Session) { [IO.File]::WriteAllText($Session.manual,[Guid]::NewGuid().ToString('N')) }
function Manual-Events([object]$Session) {
    if(-not(Test-Path -LiteralPath $Session.manualEvents)){return @()}
    $text=[IO.File]::ReadAllText($Session.manualEvents).Trim()
    if($text.Length -eq 0){return @()}
    # Fixture records use the product's indented JSON options. Read complete
    # flat objects rather than treating each formatting line as an event.
    return @(('['+($text -replace '\}\s*\{','},{')+']')|ConvertFrom-Json)
}
function Manual-Renderers([object]$Session) { return @(Manual-Events $Session|Where-Object{$_.kind -eq 'renderer-start'}).Count }
function Resolve-FixtureUpdater([object]$Fixture) {
    $profile=Read-Json (Join-Path $Fixture.target 'heirowUpdater.json')
    if(-not $profile){throw 'The fixture updater profile is missing.'}
    if(-not $profile.sharedRuntime){return Join-Path $Fixture.target 'heirowUpdater.exe'}
    if(-not $profile.isolated){throw 'Lifecycle runtime discovery requires an isolated fixture.'}
    $parent=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($profile.stateRoot))
    $runtime=Join-Path $parent ('.updater-runtime/'+$profile.sharedRuntime.version+'-'+$profile.sharedRuntime.sha256.ToLowerInvariant()+'/heirowUpdater.exe')
    if(-not [IO.Path]::GetFullPath($runtime).StartsWith($run+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'The fixture runtime must stay inside its run.'}
    if((Get-Item -LiteralPath $runtime).Length -ne $profile.sharedRuntime.size -or (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ine $profile.sharedRuntime.sha256){throw 'The fixture shared runtime differs from its installed binding.'}
    return $runtime
}
function Requests([string]$Path) { @((Get-Content -LiteralPath (Join-Path $run 'requests.jsonl') -ErrorAction SilentlyContinue) | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.path -eq $Path }) }
function Start-App([object]$Fixture, [string]$Session, [string]$Choice = 'postpone', [bool]$Hosted = $false, [bool]$WithoutWatcher = $false, [bool]$FaultedFeedback = $false) {
    $ready = Join-Path $run "$Session.ready.json"; $gate = Join-Path $run "$Session.exit.flag"; $manual = Join-Path $run "$Session.manual.flag"
    $status = Join-Path $run "$Session.status.json"; $capture = Join-Path $run "$Session.prompt.png"; $promptReport = Join-Path $run "$Session.prompt.json"
    $choiceFile=Join-Path $run "$Session.choice.txt";[IO.File]::WriteAllText($choiceFile,$Choice)
    $executable = Join-Path $Fixture.target 'FixtureApp.exe'
    if($Hosted){$hostDirectory=Join-Path $run 'external-host';New-Item -ItemType Directory -Path $hostDirectory -Force|Out-Null;$executable=Join-Path $hostDirectory 'FixtureHost.exe';if(-not(Test-Path -LiteralPath $executable)){Copy-Item -LiteralPath (Join-Path $fixturePublish 'FixtureApp.exe') -Destination $executable}}
    $start = [Diagnostics.ProcessStartInfo]::new($executable); $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $Fixture.target
    if($Hosted){$start.Environment['FIXTURE_APP_ROOT']=$Fixture.target}
    foreach ($argument in @('--hold', $ready, $gate, $manual)) { $start.ArgumentList.Add($argument) }
    $start.Environment['FIXTURE_AUTH'] = $secret
    $start.Environment['HEIROWUPDATER_TEST_CHOICE'] = $Choice; $start.Environment['HEIROWUPDATER_TEST_CAPTURE'] = $capture
    $start.Environment['HEIROWUPDATER_TEST_CHOICE_FILE'] = $choiceFile
    $start.Environment['HEIROWUPDATER_TEST_PROMPT_REPORT'] = $promptReport; $start.Environment['HEIROWUPDATER_TEST_STATUS_FILE'] = $status
    $start.Environment['HEIROWUPDATER_TEST_CLOSE_APPS_CHOICE'] = 'close'
    $start.Environment['HEIROWUPDATER_TEST_CLOSE_APPS_CAPTURE'] = Join-Path $run "$Session.close-apps.png"
    $start.Environment['HEIROWUPDATER_TEST_CLOSE_APPS_REPORT'] = Join-Path $run "$Session.close-apps.json"
    $start.Environment['HEIROWUPDATER_TEST_MANUAL_CHOICE'] = 'close'
    $start.Environment['HEIROWUPDATER_TEST_MANUAL_CAPTURE'] = Join-Path $run "$Session.manual.png"
    $start.Environment['HEIROWUPDATER_TEST_MANUAL_REPORT'] = Join-Path $run "$Session.manual.json"
    $start.Environment['HEIROWUPDATER_TEST_MANUAL_EVENTS'] = Join-Path $run "$Session.manual-events.jsonl"
    if($WithoutWatcher){$start.Environment['FIXTURE_DISABLE_UPDATER']='1'}
    if($FaultedFeedback){$start.Environment['HEIROWUPDATER_TEST_MANUAL_FAIL_CHILD']='1'}
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $run 'bundle-extraction'
    $process = [Diagnostics.Process]::Start($start); $apps.Add($process)
    Wait-For { Test-Path -LiteralPath $ready } "the direct $Session application bootstrap" 30
    return [pscustomobject]@{process=$process;ready=$ready;gate=$gate;manual=$manual;status=$status;capture=$capture;promptReport=$promptReport;choiceFile=$choiceFile;manualReport=(Join-Path $run "$Session.manual.json");manualEvents=(Join-Path $run "$Session.manual-events.jsonl")}
}
function Close-App([object]$Session) { [IO.File]::WriteAllText($Session.gate, 'exit'); Wait-For { $Session.process.HasExited } 'the exact fixture application to exit' 15 }
function Confirm-DuplicateWatcher([object]$Fixture, [object]$Session, [string]$Name, [bool]$Hosted = $false) {
    $arguments=@('after-exit','--config',(Join-Path $Fixture.target 'heirowUpdater.json'),'--application-pid',[string]($Session.process.Id),
        '--application-start',[string]($Session.process.StartTime.ToUniversalTime().Ticks),'--json')
    if($Hosted){$arguments+=@('--application-host',$Session.process.Path)}
    $result=Invoke-Child (Resolve-FixtureUpdater $Fixture) $arguments $Name | ConvertFrom-Json
    if($result.status -ne 'already-watching' -or $result.manualCheck -or $Session.process.HasExited){throw 'The duplicate watcher did not return its real coalesced result while preserving the fixture application.'}
}
function Wait-CompanionExit([object]$Fixture) {
    Wait-For { @(Get-CimInstance Win32_Process -Filter "Name='heirowUpdater.exe'" | Where-Object { $_.ExecutablePath -and $_.CommandLine -and ($_.CommandLine.Contains($Fixture.target) -or $_.CommandLine.Contains($Fixture.id)) }).Count -eq 0 } 'the app-specific companion and trusted helper to exit' 30
}
try {
    $serverScript = Join-Path $run 'ProtectedFeed.ps1'
    [IO.File]::WriteAllText($serverScript, @'
param([string]$Root,[string]$Ready,[string]$Stop,[string]$Log)
$ErrorActionPreference='Stop';$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start()
[IO.File]::WriteAllText($Ready,(@{port=$listener.LocalEndpoint.Port}|ConvertTo-Json -Compress));$deadline=[DateTime]::UtcNow.AddMinutes(12)
function Read-SharedText([string]$Path){$sharedStream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete));try{$textReader=[IO.StreamReader]::new($sharedStream,[Text.Encoding]::UTF8,$true,4096,$true);try{return $textReader.ReadToEnd()}finally{$textReader.Dispose()}}finally{$sharedStream.Dispose()}}
try { while(-not [IO.File]::Exists($Stop) -and [DateTime]::UtcNow -lt $deadline) {
 if(-not $listener.Pending()){Start-Sleep -Milliseconds 25;continue};$client=$listener.AcceptTcpClient();$client.ReceiveTimeout=5000;$stream=$client.GetStream()
 try {
  $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::ASCII,$false,8192,$true);$request=$reader.ReadLine();$parts=@($request -split ' ');$auth='';$agent=''
  try {for($i=0;$i -lt 64;$i++){$line=$reader.ReadLine();if([string]::IsNullOrEmpty($line)){break};if($line.StartsWith('Authorization:',[StringComparison]::OrdinalIgnoreCase)){$auth=$line.Substring(14).Trim()};if($line.StartsWith('User-Agent:',[StringComparison]::OrdinalIgnoreCase)){$agent=$line.Substring(11).Trim()}}}finally{$reader.Dispose()}
  $requestPath=if($parts.Count -gt 1){($parts[1] -split '\?',2)[0]}else{''}
  $name=if($parts.Count -gt 1 -and $parts[0] -eq 'GET'){switch($requestPath){'/a.json'{'a.json'};'/b.json'{'b.json'};'/a-Setup.exe'{'a-Setup.exe'};'/b-Setup.exe'{'b-Setup.exe'};default{$null}}}else{$null}
  $authenticated=$auth -ceq ('Bearer '+$env:FIXTURE_AUTH);$code=if(-not $name){404}elseif(-not $authenticated){401}elseif($name -eq 'a.json' -and [IO.File]::Exists([IO.Path]::Combine($Root,'feed-error.flag'))){503}else{200}
  $progressRenderedBeforeRequest=$null
  if($name -eq 'a.json' -and [IO.File]::Exists([IO.Path]::Combine($Root,'feed-delay.flag'))){
   $gate=[IO.File]::ReadAllText([IO.Path]::Combine($Root,'feed-delay.flag'))|ConvertFrom-Json;$rendererEvents=[string]$gate.events;$ownerStatus=[string]$gate.status;$runPrefix=[IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($Root))+[IO.Path]::DirectorySeparatorChar
   if(-not [IO.Path]::GetFullPath($rendererEvents).StartsWith($runPrefix,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFullPath($ownerStatus).StartsWith($runPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture renderer gate must stay inside its own run.'}
   $progressRenderedBeforeRequest=$false
   try{if([IO.File]::Exists($rendererEvents) -and [IO.File]::Exists($ownerStatus)){$requestId=((Read-SharedText $ownerStatus)|ConvertFrom-Json).manualRequestId;$eventText=(Read-SharedText $rendererEvents).Trim();$events=@(('['+($eventText-replace'\}\s*\{','},{')+']')|ConvertFrom-Json);$progressRenderedBeforeRequest=@($events|Where-Object{$_.kind-eq'renderer-start' -and $_.requestId-eq$requestId}).Count-gt0}}catch{$progressRenderedBeforeRequest=$false}
   Start-Sleep -Milliseconds 1200
  }
  [IO.File]::AppendAllText($Log,((@{path=$requestPath;authenticated=$authenticated;status=$code;userAgent=$agent;progressRenderedBeforeRequest=$progressRenderedBeforeRequest;timestamp=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Compress)+[Environment]::NewLine))
  if($code -ne 200){$bytes=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $code Fixture`r`nContent-Length: 0`r`nConnection: close`r`n`r`n");$stream.Write($bytes,0,$bytes.Length);continue}
  $inputFile=[IO.File]::OpenRead([IO.Path]::Combine($Root,$name));try{$bytes=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: application/octet-stream`r`nContent-Length: $($inputFile.Length)`r`nConnection: close`r`n`r`n");$stream.Write($bytes,0,$bytes.Length);$inputFile.CopyTo($stream);$stream.Flush()}finally{$inputFile.Dispose()}
 } catch {[IO.File]::AppendAllText($Log,((@{error=$_.Exception.GetType().Name}|ConvertTo-Json -Compress)+[Environment]::NewLine))} finally {$stream.Dispose();$client.Dispose()}
}}finally{$listener.Stop()}
'@)
    $serverStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe')); $serverStart.UseShellExecute=$false; $serverStart.CreateNoWindow=$true; $serverStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('-NoProfile','-NonInteractive','-File',$serverScript,'-Root',$public,'-Ready',(Join-Path $run 'server-ready.json'),'-Stop',(Join-Path $run 'server-stop.flag'),'-Log',(Join-Path $run 'requests.jsonl'))) { $serverStart.ArgumentList.Add($argument) }
    $serverStart.Environment['FIXTURE_AUTH']=$secret; $server=[Diagnostics.Process]::Start($serverStart)
    Wait-For { Test-Path -LiteralPath (Join-Path $run 'server-ready.json') } 'the protected bounded feed' 15
    $origin='http://127.0.0.1:'+(Read-Json (Join-Path $run 'server-ready.json')).port
    $fixtureProject=Join-Path $run 'fixture-project';New-Item -ItemType Directory -Path $fixtureProject | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixtureProject 'Fixture.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>FixtureApp</AssemblyName></PropertyGroup></Project>')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'templates/bootstrap/HeirowUpdaterBootstrap.cs') -Destination (Join-Path $fixtureProject 'Bootstrap.cs')
    $fixtureSource=@'
using System.Text.Json;
using SocketJack;
if(args.Length==0){File.WriteAllText("__PREFIX__","Untrusted downloaded prefix ran.");return 41;}
var root=Environment.GetEnvironmentVariable("FIXTURE_APP_ROOT")??Path.GetDirectoryName(Environment.ProcessPath!)!;
var credential=Environment.GetEnvironmentVariable("FIXTURE_AUTH");
var started=Environment.GetEnvironmentVariable("FIXTURE_DISABLE_UPDATER")!="1"&&HeirowUpdaterBootstrap.StartAfterExit(bearerToken:credential,appRoot:root);
File.WriteAllText(args[1],JsonSerializer.Serialize(new {updaterStarted=started,generation=File.ReadAllText(Path.Combine(root,"generation.txt"))}));
var deadline=DateTime.UtcNow.AddMinutes(8);var requested="";
while(!File.Exists(args[2])&&DateTime.UtcNow<deadline){if(File.Exists(args[3])){string request="";try{using var input=new FileStream(args[3],FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(input);request=reader.ReadToEnd().Trim();}catch(IOException){}if(Guid.TryParseExact(request,"N",out _)&&request!=requested){requested=request;HeirowUpdaterBootstrap.CheckForUpdates(bearerToken:credential,appRoot:root);}}await Task.Delay(50);}
return 0;
'@
    [IO.File]::WriteAllText((Join-Path $fixtureProject 'Program.cs'),$fixtureSource.Replace('__PREFIX__',$prefixMarker.Replace('\','\\')))
    $fixturePublish=Join-Path $fixtureProject 'publish'
    $null=Invoke-Child (Get-Command dotnet.exe -ErrorAction Stop).Source @('publish',(Join-Path $fixtureProject 'Fixture.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:UseSharedCompilation=false','-m:1','-nodeReuse:false','-p:DebugType=None','-o',$fixturePublish,'--nologo','-v:quiet') 'build-benign-app'
    $keyPath=Join-Path $run 'acceptance-key.pem';$rsa=[Security.Cryptography.RSA]::Create(3072)
    try{[IO.File]::WriteAllText($keyPath,$rsa.ExportPkcs8PrivateKeyPem())}finally{$rsa.Dispose()}
    foreach($tag in @('a','b')){
        $id="lifecycle-$tag-$runId";$target=Join-Path $sandbox "app-$tag";$state=Join-Path $sandbox "state-$tag"
        foreach($version in @('1.0.0','1.0.1')){
            $source=Join-Path $run "source-$tag-$version";New-Item -ItemType Directory -Path $source | Out-Null
            Copy-Item -LiteralPath (Join-Path $fixturePublish 'FixtureApp.exe') -Destination (Join-Path $source 'FixtureApp.exe')
            if($version -eq '1.0.0'){$overlay=[Text.Encoding]::UTF8.GetBytes("`nold lifecycle image $runId`n");$file=[IO.File]::Open((Join-Path $source 'FixtureApp.exe'),[IO.FileMode]::Append,[IO.FileAccess]::Write);try{$file.Write($overlay)}finally{$file.Dispose()}}
            Copy-Item -LiteralPath $PublishedUpdaterPath -Destination (Join-Path $source 'heirowUpdater.exe');[IO.File]::WriteAllText((Join-Path $source 'generation.txt'),$version)
            # This lifecycle fixture deliberately exercises legacy full-package feeds; signed file transitions have their own integration suite.
            $manifest=[ordered]@{formatVersion=1;id=$id;name="Lifecycle app $($tag.ToUpperInvariant())";version=$version;publisher='heirowInstaller acceptance';scope='user';entryPoint='FixtureApp.exe';updateUrl="$origin/$tag.json";features=@(@{id='core';name='Application';required=$true},@{id='updates';name='Optional updates';default=$true});updateCompanion=@{feature='updates';executablePath='heirowUpdater.exe';configurationPath='heirowUpdater.json';mode='after-exit';credentialOrigin=$origin;tokenEnvironmentVariable='HEIROWUPDATER_TOKEN';updateMode='legacy'};payload=@(@{source='FixtureApp.exe';path='FixtureApp.exe';feature='core'},@{source='generation.txt';path='generation.txt';feature='core'},@{source='heirowUpdater.exe';path='heirowUpdater.exe';feature='updates'})}
            $manifestPath=Join-Path $run "$tag-$version.json";[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 9))
            $null=Invoke-LifecycleCli @('pack',$manifestPath,'--source',$source,'--output',(Join-Path $run "$tag-$version.hinst"),'--sign-key',$keyPath) "pack-$tag-$version"
        }
        $old=Join-Path $run "$tag-1.0.0.hinst";$new=Join-Path $run "$tag-1.0.1.hinst";$pin=(Invoke-LifecycleCli @('inspect',$old) "inspect-$tag"|ConvertFrom-Json).publisherKeySha256
        $setup=Join-Path $public "$tag-Setup.exe";$null=Invoke-LifecycleCli @('bundle',$new,'--launcher',(Join-Path $fixturePublish 'FixtureApp.exe'),'--output',$setup) "bundle-$tag"
        $feed=@{productId=$id;name="Lifecycle app $tag";publisher='heirowInstaller acceptance';version='1.0.1';installerUrl="$origin/$tag-Setup.exe";sha256=(Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant();size=(Get-Item -LiteralPath $setup).Length;packageType='exe';releaseNotes='A signed fixture release. The untrusted launcher prefix must never execute.'}
        [IO.File]::WriteAllText((Join-Path $public "$tag.json"),($feed|ConvertTo-Json))
        $null=Invoke-LifecycleCli @('install',$old,'--silent','--isolated','--state-root',$state,'--target',$target,'--scope','user','--no-register','--trust-key',$pin,'--require-signature','--features','core,updates') "install-$tag"
        $fixtures.Add([pscustomobject]@{tag=$tag;id=$id;target=$target;state=$state;pin=$pin;old=$old})
    }
    $a=$fixtures[0];$b=$fixtures[1]
    $off=[pscustomobject]@{tag='off';id=$a.id;target=(Join-Path $sandbox 'app-off');state=(Join-Path $sandbox 'state-off');pin=$a.pin;old=$a.old};$fixtures.Add($off)
    $null=Invoke-LifecycleCli @('install',$off.old,'--silent','--isolated','--state-root',$off.state,'--target',$off.target,'--scope','user','--no-register','--trust-key',$off.pin,'--require-signature','--features','core') 'install-optional-off'
    Assert-Check (-not(Test-Path -LiteralPath (Join-Path $off.target 'heirowUpdater.exe')) -and -not(Test-Path -LiteralPath (Join-Path $off.target 'heirowUpdater.json'))) 'Declining optional updates installs neither the companion nor its profile'
    $offSession=Start-App $off 'optional-off';Assert-Check (-not(Read-Json $offSession.ready).updaterStarted) 'The actual bootstrap lets the app run without an optional updater';Close-App $offSession
    foreach($fixture in @($a,$b)){
        $profileText=Get-Content -LiteralPath (Join-Path $fixture.target 'heirowUpdater.json') -Raw;$profile=$profileText|ConvertFrom-Json
        Assert-Check ($profile.publisherKeySha256 -eq $fixture.pin -and $profile.tokenEnvironmentVariable -eq 'HEIROWUPDATER_TOKEN' -and -not $profileText.Contains($secret) -and -not(Test-Path -LiteralPath (Join-Path $fixture.target 'heirowInstaller.exe')) -and -not(Test-Path -LiteralPath (Join-Path $fixture.target 'heirowInstaller.Cli.exe'))) "App $($fixture.tag) has a generated pinned root profile with env names only and no global installer payload"
    }
    $a1=Start-App $a 'a-first';Wait-For {(Read-Json $a1.status).status -eq 'waiting-for-exit'} 'the first exact-process watcher'
    $a2=Start-App $a 'a-second';Confirm-DuplicateWatcher $a $a2 'confirm-duplicate-watcher'
    Start-Sleep -Milliseconds 1500;Assert-Check ((Requests '/a.json').Count -eq 0) 'Direct app bootstrap performs no update request while application instances are active'
    Close-App $a1;Start-Sleep -Milliseconds 1200;Assert-Check ((Requests '/a.json').Count -eq 0) 'Closing the first instance does not check while another canonical app instance remains'
    Close-App $a2;Wait-For {(Read-Json $a1.status).status -eq 'postponed'} 'the native Postpone choice' 90
    $choice=Read-Json $a1.promptReport
    Assert-Check ($choice.choice -eq 'Postpone' -and $choice.pointerEventsInjected -and $choice.framework -eq 'Project-Z' -and (Requests '/a.json').Count -eq 1 -and (Requests '/a-Setup.exe').Count -eq 0) 'The actual native prompt postponed after one exit check without downloading or installing'
    Assert-Check ([DateTimeOffset](Read-Json $a1.status).postponeUntil -gt [DateTimeOffset]::UtcNow -and [IO.File]::ReadAllText((Join-Path $a.target 'generation.txt')) -eq '1.0.0') 'Postpone persists 24 hours and preserves the installed version'
    Wait-CompanionExit $a
    $a3=Start-App $a 'a-postponed-reopen';Close-App $a3;Wait-For {(Read-Json $a3.status).status -eq 'postponed'} 'persisted postpone on a new session'
    Assert-Check ((Requests '/a.json').Count -eq 1) 'A reopened postponed app does not silently recheck or apply an update'
    Wait-CompanionExit $a
    $b1=Start-App $b 'b-independent';$a4=Start-App $a 'a-manual-update' 'postpone'
    Wait-For {(Read-Json $a4.status).status -eq 'waiting-for-exit'} 'the reopened app watcher'
    Request-Manual $a4
    Wait-For {(Read-Json $a4.promptReport).choice -eq 'Postpone' -and (Read-Json $a4.status).status -eq 'waiting-for-exit'} 'manual Postpone retaining the existing watcher'
    Assert-Check ((Requests '/a-Setup.exe').Count -eq 0 -and -not $a4.process.HasExited) 'Manual Postpone keeps the app and its exit watcher alive without downloading'
    Copy-Item -LiteralPath $a4.capture -Destination (Join-Path $run 'a-manual-postpone.prompt.png')
    [IO.File]::WriteAllText($a4.choiceFile,'update');Request-Manual $a4
    Wait-For {(Test-Path -LiteralPath $a4.promptReport) -and (Requests '/a-Setup.exe').Count -eq 1 -and (Read-Json $a4.status).status -eq 'waiting-for-applications'} 'manual Update now downloading and requesting explicit application closure' 120
    Assert-Check ((Read-Json $a4.promptReport).choice -eq 'UpdateNow') 'A second manual check opens a working native prompt in the same companion process'
    $a5=Start-App $a 'a-reopened-during-update' 'update'
    Wait-For {(Read-Json (Join-Path $run 'a-manual-update.close-apps.json')).choice -eq 'CloseAppsAndContinue'} 'the actual native close-apps consent' 45
    $closePrompt=Read-Json (Join-Path $run 'a-manual-update.close-apps.json')
    Assert-Check ($closePrompt.framework -eq 'Project-Z' -and $closePrompt.pointerEventsInjected -and @($closePrompt.applications|Where-Object {$_.processId -eq $a4.process.Id}).Count -eq 1) 'Manual Update now lists the exact running fixture and requires a separate native close-apps choice'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $a.target 'generation.txt')) -eq '1.0.0' -and (Read-Json $a4.promptReport).choice -eq 'UpdateNow') 'Manual Update now is a real native choice and does not replace files while the app is active'
    Close-App $a4;Start-Sleep -Milliseconds 1200;Assert-Check ([IO.File]::ReadAllText((Join-Path $a.target 'generation.txt')) -eq '1.0.0') 'An instance opened during download also prevents mutation after the original instance exits'
    Close-App $a5
    $aReceipt=Join-Path $a.state "$($a.id)/receipt.json"
    Wait-For {(Read-Json $aReceipt).version -eq '1.0.1'} 'the trusted root updater helper to install the signed release' 120
    Assert-Check ((Read-Json $aReceipt).publisherKeySha256 -eq $a.pin -and [IO.File]::ReadAllText((Join-Path $a.target 'generation.txt')) -eq '1.0.1' -and -not(Test-Path -LiteralPath $prefixMarker)) 'Update now installs the pinned package data through a trusted helper without executing its EXE prefix'
    Wait-CompanionExit $a
    Assert-Check ((Requests '/b.json').Count -eq 0 -and -not $b1.process.HasExited -and [IO.File]::ReadAllText((Join-Path $b.target 'generation.txt')) -eq '1.0.0') 'App A updates independently while app B remains open and untouched'
    $a6=Start-App $a 'a-updated-reopen';Assert-Check ((Read-Json $a6.ready).generation -eq '1.0.1') 'The updated application reopens normally with its optional bootstrap';Close-App $a6
    Wait-For {(Read-Json $a6.status).status -eq 'current'} 'the updated after-exit current-version result'
    Wait-CompanionExit $a
    $beforeManualCurrent=(Requests '/a.json').Count
    $manualCurrent=Start-App $a 'manual-current'
    Wait-For {(Read-Json $manualCurrent.status).status -eq 'waiting-for-exit'} 'the watcher before a manual current-version check'
    Request-Manual $manualCurrent
    Wait-For {(Requests '/a.json').Count -eq $beforeManualCurrent+1 -and (Read-Json $manualCurrent.status).status -eq 'waiting-for-exit'} 'manual current check returning to app-exit monitoring'
    $currentUi=Read-Json $manualCurrent.manualReport
    Assert-Check ($currentUi.status -eq 'current' -and $currentUi.visibleTitle -eq "You're up to date." -and $currentUi.closeButtonText -eq 'Close' -and $currentUi.pointerEventsInjected -and $currentUi.renderedFrames -gt 0) 'A relayed manual-current check renders the real native up-to-date label and usable Close action'
    $currentUiHash=(Get-FileHash -LiteralPath $manualCurrent.manualReport).Hash
    $currentRendererCount=Manual-Renderers $manualCurrent
    Assert-Check (-not $manualCurrent.process.HasExited) 'A manual current-version check leaves the application running and its after-exit watcher active'
    Close-App $manualCurrent;Wait-For {(Read-Json $manualCurrent.status).status -eq 'current'} 'the automatic check after the manually checked app exits'
    Assert-Check ((Requests '/a.json').Count -eq $beforeManualCurrent+2) 'Manual checking does not disable the later after-exit check'
    Wait-CompanionExit $a
    Assert-Check ((Get-FileHash -LiteralPath $manualCurrent.manualReport).Hash -eq $currentUiHash -and (Manual-Renderers $manualCurrent) -eq $currentRendererCount) 'Automatic current after app exit does not open another manual feedback renderer'

    $direct=Start-App $a 'manual-direct-current' 'postpone' $false $true
    $delayFlag=Join-Path $public 'feed-delay.flag';[IO.File]::WriteAllText($delayFlag,(@{events=$direct.manualEvents;status=$direct.status}|ConvertTo-Json -Compress))
    Assert-Check (-not(Read-Json $direct.ready).updaterStarted) 'The direct manual-current fixture has no existing watcher'
    Request-Manual $direct
    Wait-For {(Read-Json $direct.manualReport).status -eq 'current' -and (Read-Json $direct.status).status -eq 'waiting-for-exit'} 'the direct native current result and retained watcher'
    $directUi=Read-Json $direct.manualReport
    Assert-Check ((Requests '/a.json')[-1].progressRenderedBeforeRequest -eq $true) 'The manual progress window presented its first frame before the update feed received any request'
    Assert-Check ($directUi.visibleTitle -eq "You're up to date." -and $directUi.closeButtonText -eq 'Close' -and $directUi.pointerEventsInjected -and $directUi.statusesRendered -contains 'checking' -and -not $direct.process.HasExited) 'A direct manual check renders progress and up-to-date result, and Close preserves the application and watcher'
    [IO.File]::Delete($delayFlag);$directUiHash=(Get-FileHash -LiteralPath $direct.manualReport).Hash;$directRendererCount=Manual-Renderers $direct
    Close-App $direct;Wait-For {(Read-Json $direct.status).status -eq 'current'} 'direct manual owner automatic exit check';Wait-CompanionExit $a
    Assert-Check ((Get-FileHash -LiteralPath $direct.manualReport).Hash -eq $directUiHash -and (Manual-Renderers $direct) -eq $directRendererCount) 'A direct manual owner returns to quiet automatic-current monitoring'

    $errorFlag=Join-Path $public 'feed-error.flag';[IO.File]::WriteAllText($errorFlag,'one isolated service error')
    $manualError=Start-App $a 'manual-network-error';Wait-For {(Read-Json $manualError.status).status -eq 'waiting-for-exit'} 'the watcher before a manual error'
    Request-Manual $manualError
    Wait-For {(Read-Json $manualError.manualReport).status -eq 'error' -and (Read-Json $manualError.status).status -eq 'waiting-for-exit'} 'the visible error result followed by watcher retention'
    $errorUi=Read-Json $manualError.manualReport
    Assert-Check ($errorUi.visibleTitle -eq 'Updates need your attention.' -and $errorUi.closeButtonText -eq 'Close' -and $errorUi.pointerEventsInjected -and -not $manualError.process.HasExited -and -not([IO.File]::ReadAllText($manualError.manualReport).Contains($secret))) 'A manual network error visibly renders a safe native result and Close preserves the application and watcher'
    [IO.File]::Delete($errorFlag);Close-App $manualError;Wait-For {(Read-Json $manualError.status).status -eq 'current'} 'the error owner quiet automatic retry';Wait-CompanionExit $a

    $cold=Start-App $a 'manual-cold-child-error' 'postpone' $false $true $true
    Request-Manual $cold
    Wait-For {(Read-Json $cold.manualReport).status -eq 'error' -and (Read-Json $cold.status).status -eq 'error'} 'a visible startup result after the feedback child cannot initialize'
    Wait-CompanionExit $a;$coldUi=Read-Json $cold.manualReport
    $coldEvents=Manual-Events $cold
    Assert-Check ($coldUi.visibleTitle -eq 'Updates need your attention.' -and $coldUi.closeButtonText -eq 'Close' -and $coldUi.pointerEventsInjected -and @($coldEvents|Where-Object{$_.kind -eq 'child-start'}).Count -eq 1 -and @($coldEvents|Where-Object{$_.kind -eq 'renderer-start'}).Count -eq 1 -and -not $cold.process.HasExited) 'A cold child failure opens one usable generic native result without repeatedly relaunching the failing child'
    Close-App $cold
    $beforeHostChecks=(Requests '/a.json').Count
    $mixedApp=Start-App $a 'mixed-standalone-owner'
    Wait-For {(Read-Json $mixedApp.status).status -eq 'waiting-for-exit'} 'the standalone owner before a plugin host joins'
    $host1=Start-App $a 'host-first' 'postpone' $true;$host2=Start-App $a 'host-second' 'postpone' $true
    Confirm-DuplicateWatcher $a $host1 'confirm-host-duplicate-watcher' $true
    Start-Sleep -Milliseconds 1200
    Assert-Check ((Requests '/a.json').Count -eq $beforeHostChecks) 'A plugin bootstrap with explicit appRoot waits for its external host instead of checking immediately'
    Close-App $mixedApp;Start-Sleep -Milliseconds 1200
    Assert-Check ((Requests '/a.json').Count -eq $beforeHostChecks) 'A plugin joining an existing standalone watcher adds its host wait before the standalone app exits'
    $wrongStart=($host1.process.StartTime.ToUniversalTime().Ticks+1).ToString([Globalization.CultureInfo]::InvariantCulture)
    $null=Invoke-Child (Resolve-FixtureUpdater $a) @('after-exit','--config',(Join-Path $a.target 'heirowUpdater.json'),'--application-pid',$host1.process.Id.ToString(),'--application-start',$wrongStart,'--application-host',$host1.process.Path) 'reject-host-identity' @(1)
    Assert-Check ((Get-Content -LiteralPath (Join-Path $run 'reject-host-identity.stderr.txt') -Raw).Contains('identity changed') -and (Requests '/a.json').Count -eq $beforeHostChecks) 'A mismatched external-host creation time is rejected before any update request'
    Close-App $host1;Start-Sleep -Milliseconds 1200
    Assert-Check ((Requests '/a.json').Count -eq $beforeHostChecks) 'A second instance of the same external host continues to block checking and mutation'
    Close-App $host2;Wait-For {(Read-Json $mixedApp.status).status -eq 'current'} 'the check after all external hosts exit'
    Assert-Check ((Requests '/a.json').Count -eq $beforeHostChecks+1) 'The app-root companion checks once after the last canonical external host exits'
    Wait-CompanionExit $a
    $beforeMaintenance=(Requests '/a.json').Count
    $stopHost=Start-App $a 'maintenance-stop-host' 'postpone' $true
    Wait-For {(Read-Json $stopHost.status).status -eq 'waiting-for-exit'} 'the host-bound watcher before a polite stop'
    $stopResult=Invoke-Child (Resolve-FixtureUpdater $a) @('stop','--config',(Join-Path $a.target 'heirowUpdater.json'),'--json') 'polite-stop' | ConvertFrom-Json
    Wait-For {(Read-Json $stopHost.status).status -eq 'stopped'} 'the watcher to honor its root-specific stop event'
    Wait-CompanionExit $a
    Assert-Check ($stopResult.status -eq 'stop-requested' -and -not $stopHost.process.HasExited -and (Requests '/a.json').Count -eq $beforeMaintenance) 'A polite root-specific stop releases the updater without terminating the application host or contacting its feed'
    Close-App $stopHost
    $repairHost=Start-App $a 'maintenance-repair-host' 'postpone' $true
    Wait-For {(Read-Json $repairHost.status).status -eq 'waiting-for-exit'} 'the host-bound watcher before installer repair'
    $null=Invoke-LifecycleCli @('repair',$a.id,'--silent','--isolated','--state-root',$a.state,'--scope','user') 'repair-watched-app'
    Wait-For {(Read-Json $repairHost.status).status -eq 'stopped'} 'installer repair to stop its old companion'
    Wait-CompanionExit $a
    Assert-Check (-not $repairHost.process.HasExited -and [IO.File]::ReadAllText((Join-Path $a.target 'generation.txt')) -eq '1.0.1' -and (Read-Json $aReceipt).version -eq '1.0.1') 'Real installer repair stops the waiting companion, repairs its owned files, and preserves an external host'
    Close-App $repairHost
    $uninstallHost=Start-App $a 'maintenance-uninstall-host' 'postpone' $true
    Wait-For {(Read-Json $uninstallHost.status).status -eq 'waiting-for-exit'} 'the host-bound watcher before installer uninstall'
    $null=Invoke-LifecycleCli @('uninstall',$a.id,'--silent','--isolated','--state-root',$a.state,'--scope','user') 'uninstall-watched-app';$null=$uninstalled.Add($a.id)
    Wait-For {(Read-Json $uninstallHost.status).status -eq 'stopped'} 'installer uninstall to stop its old companion'
    Wait-CompanionExit $a
    Assert-Check (-not $uninstallHost.process.HasExited -and -not(Test-Path -LiteralPath $aReceipt) -and -not(Test-Path -LiteralPath (Join-Path $a.target 'heirowUpdater.exe'))) 'Real installer uninstall stops and removes its companion while leaving the external host running'
    Close-App $uninstallHost;Start-Sleep -Milliseconds 1200
    Assert-Check ((Requests '/a.json').Count -eq $beforeMaintenance -and -not(Test-Path -LiteralPath $aReceipt)) 'Stopped repair and uninstall watchers do not later contact the feed or recreate the removed application'
    Close-App $b1;Wait-For {(Read-Json $b1.status).status -eq 'postponed'} 'the separate app B Postpone choice'
    Wait-CompanionExit $a;Wait-CompanionExit $b
    $requests=@(Get-Content -LiteralPath (Join-Path $run 'requests.jsonl') | ForEach-Object {$_|ConvertFrom-Json})
    Assert-Check (@($requests|Where-Object {$_.status -notin @(200,503) -or -not $_.authenticated -or $_.userAgent -notlike '*SocketJack*'}).Count -eq 0 -and @($requests|Where-Object{$_.status -eq 503}).Count -eq 1) 'The actual bootstrap forwards protected-feed authorization through SocketJack, including the one intended isolated error response'
    foreach($fixture in $fixtures){if(-not $uninstalled.Contains($fixture.id) -or $fixture.tag -eq 'off'){$null=Invoke-LifecycleCli @('uninstall',$fixture.id,'--silent','--state-root',$fixture.state,'--scope','user') "uninstall-$($fixture.tag)"}}
    Wait-For {@(Get-CimInstance Win32_Process -Filter "Name='heirowUpdater.exe' OR Name='FixtureApp.exe' OR Name='FixtureHost.exe'" | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($run+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}).Count -eq 0} 'all owned fixture processes to exit' 30
    Assert-Check (@($fixtures|Where-Object {(Test-Path -LiteralPath (Join-Path $_.state "$($_.id)/receipt.json")) -or (Test-Path -LiteralPath (Join-Path $_.target 'FixtureApp.exe'))}).Count -eq 0) 'All unique fixtures uninstall without active receipts or target executables'
    $receipt.updaterSha256=(Get-FileHash -LiteralPath $PublishedUpdaterPath).Hash;$receipt.success=$true
} catch {$receipt.error=$_.Exception.ToString();throw}
finally {
    foreach($app in $apps){if(-not $app.HasExited){try{$path=$app.Path;if($path.StartsWith($run+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){$app.Kill();$app.WaitForExit()}}catch{}}}
    foreach($fixture in $fixtures){
        $canonical=[IO.Path]::GetFullPath($fixture.target).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar).ToUpperInvariant()
        $eventName='Local\heirowUpdater.lifecycle.stop.'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
        try{$signal=[Threading.EventWaitHandle]::OpenExisting($eventName);try{$null=$signal.Set()}finally{$signal.Dispose()}}catch [Threading.WaitHandleCannotBeOpenedException]{}
    }
    $cleanupDeadline=[DateTime]::UtcNow.AddSeconds(15)
    do{$remaining=@(Get-CimInstance Win32_Process -Filter "Name='heirowUpdater.exe'"|Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($run+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)});if($remaining.Count -eq 0){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $cleanupDeadline)
    if(-not $receipt.success){
        $cleanupErrors=[Collections.Generic.List[string]]::new()
        foreach($fixture in $fixtures){if(Test-Path -LiteralPath (Join-Path $fixture.state "$($fixture.id)/receipt.json")){try{$null=Invoke-LifecycleCli @('uninstall',$fixture.id,'--silent','--isolated','--state-root',$fixture.state,'--scope','user') "cleanup-$($fixture.tag)"}catch{$cleanupErrors.Add($_.Exception.Message)}}}
        $receipt.cleanup.errors=$cleanupErrors
    }
    if($server){[IO.File]::WriteAllText((Join-Path $run 'server-stop.flag'),'stop');if(-not $server.WaitForExit(10000)){$server.Kill();$server.WaitForExit()};$receipt.cleanup.serverExited=$server.HasExited;$server.Dispose()}
    $receipt.cleanup.activeFixtureProcesses=@(Get-CimInstance Win32_Process -Filter "Name='heirowUpdater.exe' OR Name='FixtureApp.exe' OR Name='FixtureHost.exe'" | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($run+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}).Count
    $receipt.cleanup.remainingReceipts=@(Get-ChildItem -LiteralPath $sandbox -Filter receipt.json -Recurse -ErrorAction SilentlyContinue).Count
    $receipt.cleanup.machineStateChanged=$false;$receipt.cleanup.schedulingChanged=$false;$receipt.cleanup.registryChanged=$false;$receipt.cleanup.ignoredEvidenceAndEphemeralKeyRetained=$true
    [IO.File]::WriteAllText((Join-Path $run 'lifecycle-receipt.json'),($receipt|ConvertTo-Json -Depth 10))
    [ordered]@{success=$receipt.success;checks=$checks.Count;receipt=(Join-Path $run 'lifecycle-receipt.json');cleanup=$receipt.cleanup}|ConvertTo-Json -Depth 5
}
