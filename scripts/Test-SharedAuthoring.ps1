#requires -Version 7.0
<#
Exercises package partitioning and cross-product signed dependency reuse using
synthetic inputs. It does not install or execute the fixture payload/launchers.
Outputs (including the synthetic signing key) remain in the explicit owned root.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][string]$GenericBootstrapPath,
    [Parameter(Mandatory)][string]$AppHostPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PackageCache=$(if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'})
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$taskRoot=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $taskRoot){throw 'Use a new owned fixture directory; existing outputs are retained.'}
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($taskRoot)).AvailableFreeSpace -lt 256MB){throw 'Authoring acceptance needs at least 256 MiB free.'}
New-Item -ItemType Directory -Path $taskRoot|Out-Null
function Save($Value,[string]$Path){$Value|ConvertTo-Json -Depth 60|Set-Content -LiteralPath $Path -Encoding utf8NoBOM}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
$taskKey=Join-Path $taskRoot 'fixture.private.pem';$taskRsa=[Security.Cryptography.RSA]::Create(2048)
try{[IO.File]::WriteAllText($taskKey,$taskRsa.ExportRSAPrivateKeyPem())}finally{$taskRsa.Dispose()}
$taskLegal=(Get-Content -LiteralPath (Join-Path $projectRoot 'legal/heirowinstaller/legal-documents.json') -Raw|ConvertFrom-Json -AsHashtable).legalDocuments
$taskMaintenanceSource=Join-Path $taskRoot 'maintenance-source';New-Item -ItemType Directory -Path $taskMaintenanceSource|Out-Null
foreach($taskName in @('heirowInstaller','heirowUpdater','heirowInstaller.Cli')){
    foreach($taskExtension in @('.exe','.dll','.deps.json')){Set-Content -LiteralPath (Join-Path $taskMaintenanceSource ($taskName+$taskExtension)) -Value 'isolated synthetic host bytes'}
    Save @{runtimeOptions=@{tfm='net10.0';framework=@{name='Microsoft.NETCore.App';version='10.0.0'}}} (Join-Path $taskMaintenanceSource ($taskName+'.runtimeconfig.json'))
}
$taskMaintenanceManifest=Join-Path $taskRoot 'maintenance.json'
Save @{id='fixture.maintenance';name='Isolated authoring fixture';version='1.0.0';publisher='Fixture';targetRuntime='win-x64';legalDocuments=$taskLegal;payload=@()} $taskMaintenanceManifest
$taskMaintenance=Join-Path $taskRoot 'maintenance.hinst'
& $CliPath pack $taskMaintenanceManifest --source $taskMaintenanceSource --output $taskMaintenance --sign-key $taskKey|Out-Null
if($LASTEXITCODE){throw 'Fixture maintenance creation failed.'}
$taskSource=Join-Path $taskRoot 'source';New-Item -ItemType Directory -Path $taskSource|Out-Null
Copy-Item -LiteralPath $AppHostPath -Destination (Join-Path $taskSource 'fixture.exe')
$taskVersion=(Get-Item -LiteralPath $AppHostPath).VersionInfo.ProductVersion.Split('+')[0]
Save @{runtimeOptions=@{tfm='net10.0';frameworks=@(@{name='Microsoft.NETCore.App';version='10.0.0'},@{name='Microsoft.WindowsDesktop.App';version='10.0.0'})}} (Join-Path $taskSource 'fixture.runtimeconfig.json')
$taskNuget=Join-Path $PackageCache 'system.io.hashing/10.0.1'
$taskSha512=[IO.File]::OpenRead((Join-Path $taskNuget 'system.io.hashing.10.0.1.nupkg'))
try{$taskContentHash='sha512-'+[Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($taskSha512))}finally{$taskSha512.Dispose()}
Copy-Item -LiteralPath (Join-Path $taskNuget 'lib/net10.0/System.IO.Hashing.dll') -Destination (Join-Path $taskSource 'System.IO.Hashing.dll')
Save @{runtimeTarget=@{name='.NETCoreApp,Version=v10.0/win-x64'};targets=@{'.NETCoreApp,Version=v10.0/win-x64'=@{'System.IO.Hashing/10.0.1'=@{runtime=@{'lib/net10.0/System.IO.Hashing.dll'=@{}}}}};libraries=@{'System.IO.Hashing/10.0.1'=@{type='package';path='system.io.hashing/10.0.1';sha512=$taskContentHash}}} (Join-Path $taskSource 'fixture.deps.json')
Set-Content -LiteralPath (Join-Path $taskSource 'private-data.dll') -Value 'isolated private application bytes'
$taskRuntime=Join-Path $taskRoot 'runtime'
foreach($taskDirectory in @('host/fxr/10.0.14','shared/Microsoft.NETCore.App/10.0.14','shared/Microsoft.WindowsDesktop.App/10.0.14')){
    New-Item -ItemType Directory -Path (Join-Path $taskRuntime $taskDirectory) -Force|Out-Null
    $taskRuntimeName=if($taskDirectory.StartsWith('host/')){'hostfxr.dll'}elseif($taskDirectory.Contains('NETCore')){'coreclr.dll'}else{'PresentationFramework.dll'}
    Set-Content -LiteralPath (Join-Path (Join-Path $taskRuntime $taskDirectory) $taskRuntimeName) -Value 'synthetic runtime asset'
}
Set-Content -LiteralPath (Join-Path $taskRuntime 'dotnet.exe') -Value 'synthetic runtime host'
# Deliberately duplicate a resource family; only matching aliases may leave the app.
foreach($taskDirectory in @('resources','duplicate-resources')){
    New-Item -ItemType Directory -Path (Join-Path $taskSource $taskDirectory)|Out-Null
    Set-Content -LiteralPath (Join-Path $taskSource ($taskDirectory+'/model.fixture')) -Value 'synthetic protected resource'
}
$taskPartitions=Join-Path $taskRoot 'partitions.json'
Save @(@{id='fixture.protected-resources';version='1.0.0';visibility='protected';sources=@(@{prefix='resources';required=$true});equivalentSources=@(@{prefix='duplicate-resources'})}) $taskPartitions
$taskManifest=Join-Path $taskRoot 'app.json'
$taskApp=@{id='fixture.shared-authoring';name='Shared authoring fixture';version=$taskVersion;publisher='Fixture';scope='user';entryPoint='fixture.exe';updateUrl='https://packages.invalid/app/update.json';legalDocuments=$taskLegal;features=@(@{id='updates';name='Updates';default=$true});updateCompanion=@{feature='updates';executablePath='heirowUpdater.exe'}}
Save $taskApp $taskManifest
$taskCommon=@{ManifestPath=$taskManifest;SourceDirectory=$taskSource;CliPath=$CliPath;SigningKey=$taskKey;GenericBootstrapPath=$GenericBootstrapPath;MaintenancePackagePath=$taskMaintenance;MaintenancePackageUrl='https://packages.invalid/shared/maintenance.hinst';ApplicationPackageUrl='https://packages.invalid/app/application.hinst';SharedPackageBaseUrl='https://packages.invalid/shared';DependencyCacheDirectory=(Join-Path $taskRoot 'dependency-cache');PackageCache=$PackageCache;ResourcePartitionsPath=$taskPartitions}
& (Join-Path $PSScriptRoot 'Build-SharedApplication.ps1') @taskCommon -DotNetRuntimeDirectory $taskRuntime -DotNetRuntimeVersion 10.0.14 -OutputDirectory (Join-Path $taskRoot 'first')
$taskResult=Get-Content -LiteralPath (Join-Path $taskRoot 'first/installer.json') -Raw|ConvertFrom-Json
$taskPlan=Get-Content -LiteralPath (Join-Path $taskRoot 'first/shared-publication-plan.json') -Raw|ConvertFrom-Json
if($taskResult.sharedPackages.id -notcontains 'nuget.System.IO.Hashing' -or $taskResult.sharedPackages.id -notcontains 'socketjack.updater.host' -or $taskResult.applicationLaunch.executablePath -ne 'fixture.App.exe'){throw 'Missing exact library/runtime/updater/application bindings.'}
if(Test-Path -LiteralPath (Join-Path $taskRoot 'first/staging/application/System.IO.Hashing.dll')){throw 'Public library stayed duplicated in the application.'}
foreach($taskPath in @('resources/model.fixture','duplicate-resources/model.fixture')){if(Test-Path -LiteralPath (Join-Path $taskRoot ('first/staging/application/'+$taskPath))){throw 'An identical shared resource alias stayed duplicated.'}}
if(!(Test-Path -LiteralPath (Join-Path $taskRoot 'first/staging/application/private-data.dll'))){throw 'Private application content was lost.'}
if(@($taskPlan|Where-Object {$_.id -eq 'fixture.protected-resources' -and $_.visibility -eq 'protected'}).Count -ne 1){throw 'Protected resource publication authority was lost.'}
$taskCount=@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'dependency-cache') -Filter '*.hinst').Count
$taskRuntimeArchive=($taskPlan|Where-Object id -EQ 'socketjack.dotnet.desktop.10').path
# A different product must use the identical existing archives, despite RSA-PSS randomness.
$taskApp.id='fixture.shared-authoring.second';$taskApp.name='Second application fixture';Save $taskApp $taskManifest
& (Join-Path $PSScriptRoot 'Build-SharedApplication.ps1') @taskCommon -RuntimePackagePath $taskRuntimeArchive -OutputDirectory (Join-Path $taskRoot 'second')
$taskSecond=Get-Content -LiteralPath (Join-Path $taskRoot 'second/installer.json') -Raw|ConvertFrom-Json
if(@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'dependency-cache') -Filter '*.hinst').Count -ne $taskCount){throw 'Another product duplicated verified dependency archives.'}
foreach($taskDependency in $taskResult.sharedPackages){
    $taskOther=@($taskSecond.sharedPackages|Where-Object id -CEQ $taskDependency.id)
    if($taskOther.Count -ne 1 -or $taskOther[0].sha256 -cne $taskDependency.sha256){throw 'Cross-product dependency identities differ.'}
}
foreach($taskRequired in @('fixture.maintenance','socketjack.dotnet.desktop.10')){
    if($taskResult.sharedPackages.id -cnotcontains $taskRequired){throw 'The signed parent omitted a transitive updater dependency.'}
}
$taskUpdater=Get-Content -LiteralPath (Join-Path $taskRoot 'first/staging/updater-bootstrap.json') -Raw|ConvertFrom-Json
if(!$taskUpdater.offlineOnly -or $taskUpdater.dotNetPackageId -cne 'socketjack.dotnet.desktop.10' -or $taskUpdater.packages.Count -ne 2){throw 'Updater was not bound to its complete offline-only native apphost/private-runtime closure.'}
$taskWeb=Get-Content -LiteralPath (Join-Path $taskRoot 'first/web-installer/bootstrap-descriptor.json') -Raw|ConvertFrom-Json
if($taskWeb.packages.Count -ne 2 -or $taskWeb.dotNetPackageId -cne 'socketjack.dotnet.desktop.10'){throw 'Setup did not bind the separate canonical runtime.'}
# The reviewed public closure stays local and unchanged; private DLLs are never
# added to its policy merely because the application layout contains them.
New-Item -ItemType Directory -Path (Join-Path $taskSource 'SecurityLoader')|Out-Null
foreach($taskName in @('fixture.sjsl','bootstrap.json','bootstrap.sig','bootstrap.pub')){Set-Content -LiteralPath (Join-Path $taskSource ('SecurityLoader/'+$taskName)) -Value 'synthetic sealed bytes; never loaded'}
$taskApp.modules=@{SocketJackSecurityLoader=@{formatVersion=1;preparationOnly=$false;publicPayload=@('System.IO.Hashing.dll','duplicate-resources/model.fixture');components=@(@{id='fixture';path='SecurityLoader/fixture.sjsl'})}}
$taskExpectedPolicy=$taskApp.modules.SocketJackSecurityLoader|ConvertTo-Json -Depth 30 -Compress
Save $taskApp $taskManifest
& (Join-Path $PSScriptRoot 'Build-SharedApplication.ps1') @taskCommon -RuntimePackagePath $taskRuntimeArchive -OutputDirectory (Join-Path $taskRoot 'sealed-closure')
$taskSealed=Get-Content -LiteralPath (Join-Path $taskRoot 'sealed-closure/installer.json') -Raw|ConvertFrom-Json -AsHashtable
if(($taskSealed.modules.SocketJackSecurityLoader|ConvertTo-Json -Depth 30 -Compress) -cne $taskExpectedPolicy -or !(Test-Path -LiteralPath (Join-Path $taskRoot 'sealed-closure/staging/application/System.IO.Hashing.dll')) -or
    !(Test-Path -LiteralPath (Join-Path $taskRoot 'sealed-closure/staging/application/duplicate-resources/model.fixture'))){throw 'The reviewed SecurityLoader closure was weakened or relocated.'}
$taskApp.modules.SocketJackSecurityLoader.publicPayload+=@('missing-reviewed.dll');Save $taskApp $taskManifest
$taskBlocked=$false
try{& (Join-Path $PSScriptRoot 'Build-SharedApplication.ps1') @taskCommon -RuntimePackagePath $taskRuntimeArchive -OutputDirectory (Join-Path $taskRoot 'blocked-sealed-closure')}catch{if($_.Exception.Message -like '*sealed reviewed closure requires local*'){$taskBlocked=$true}else{throw}}
if(!$taskBlocked -or (Test-Path -LiteralPath (Join-Path $taskRoot 'blocked-sealed-closure'))){throw 'Bundled-only/missing reviewed closure was not blocked before creating output.'}
# An app on .NET 8 still provisions the separate .NET 10 updater/toolkit closure.
$taskRuntime8=Join-Path $taskRoot 'runtime-eight';New-Item -ItemType Directory -Path $taskRuntime8|Out-Null
foreach($taskPair in @(@('dotnet.exe','synthetic host'),@('host/fxr/8.0.33/hostfxr.dll','synthetic hostfxr'),@('shared/Microsoft.NETCore.App/8.0.33/coreclr.dll','synthetic core'),@('shared/Microsoft.WindowsDesktop.App/8.0.33/PresentationFramework.dll','synthetic desktop'))){
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName((Join-Path $taskRuntime8 $taskPair[0]))) -Force|Out-Null
    Set-Content -LiteralPath (Join-Path $taskRuntime8 $taskPair[0]) -Value $taskPair[1]
}
$taskRuntime8Manifest=Join-Path $taskRoot 'runtime-eight.json';$taskRuntime8Package=Join-Path $taskRoot 'runtime-eight.hinst'
Save @{formatVersion=4;id='socketjack.dotnet.desktop.8';name='Synthetic desktop eight';version='8.0.33';publisher='Fixture';targetRuntime='win-x64';legalDocuments=$taskLegal;payload=@()} $taskRuntime8Manifest
& $CliPath pack $taskRuntime8Manifest --source $taskRuntime8 --output $taskRuntime8Package --sign-key $taskKey|Out-Null
if($LASTEXITCODE){throw 'Synthetic runtime eight creation failed.'}
$taskApp.Remove('modules');$taskApp.id='fixture.framework-eight';$taskApp.requiresAdministrator=$true;Save $taskApp $taskManifest
Save @{runtimeOptions=@{tfm='net8.0';frameworks=@(@{name='Microsoft.NETCore.App';version='8.0.0'},@{name='Microsoft.WindowsDesktop.App';version='8.0.0'})}} (Join-Path $taskSource 'fixture.runtimeconfig.json')
Save @{runtimeTarget=@{name='.NETCoreApp,Version=v8.0/win-x64'};targets=@{'.NETCoreApp,Version=v8.0/win-x64'=@{}};libraries=@{}} (Join-Path $taskSource 'fixture.deps.json')
$taskRuntime10Url=($taskResult.sharedPackages|Where-Object id -CEQ 'socketjack.dotnet.desktop.10').downloadUrl
& (Join-Path $PSScriptRoot 'Build-SharedApplication.ps1') @taskCommon -RuntimePackagePath $taskRuntime8Package -MaintenanceRuntimePackagePath $taskRuntimeArchive -MaintenanceRuntimePackageUrl $taskRuntime10Url -OutputDirectory (Join-Path $taskRoot 'framework-eight')
$taskEight=Get-Content -LiteralPath (Join-Path $taskRoot 'framework-eight/installer.json') -Raw|ConvertFrom-Json
$taskEightWeb=Get-Content -LiteralPath (Join-Path $taskRoot 'framework-eight/web-installer/bootstrap-descriptor.json') -Raw|ConvertFrom-Json
$taskEightLaunch=Get-Content -LiteralPath (Join-Path $taskRoot 'framework-eight/staging/application-bootstrap.json') -Raw|ConvertFrom-Json
if($taskEight.applicationLaunch.runtimePackageId -cne 'socketjack.dotnet.desktop.8' -or ($taskEight.sharedPackages|Where-Object id -CEQ 'socketjack.dotnet.desktop.10').feature -cne 'updates' -or
    !$taskEightWeb.requiresAdministrator -or $taskEightWeb.dotNetPackageId -cne 'socketjack.dotnet.desktop.10' -or $taskEightLaunch.requiresAdministrator -or $taskEightLaunch.packages.id -contains 'socketjack.dotnet.desktop.10'){
    throw 'App/runtime scope and signed setup-only admin requirements were confused with the updater runtime.'
}
Save @{ownedRoot=$taskRoot;purpose='Synthetic authoring acceptance, no payload execution';retain='All outputs until explicit cleanup approval';syntheticPrivateKey=(Join-Path $taskRoot 'fixture.private.pem');sourceInputs=@($CliPath,$GenericBootstrapPath,$AppHostPath)} (Join-Path $taskRoot 'storage-manifest.json')
Write-Output ('PASS shared authoring: cross-product runtime reuse, complete offline updater/setup closure, exact sealed SecurityLoader policy preservation/block, NuGet relocation, protected aliases/private files; outputs '+$taskRoot)
