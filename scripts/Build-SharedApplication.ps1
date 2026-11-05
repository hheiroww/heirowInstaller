#requires -Version 7.0
<#
Partitions an already reviewed framework-dependent native application payload.
Only explicitly allowed NuGet assets matching their package-cache bytes become
public library packs. Application-specific resource partitions retain their
declared visibility. This script never installs, launches, publishes or deletes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][string]$SigningKey,
    [Parameter(Mandatory)][string]$GenericBootstrapPath,
    [Parameter(Mandatory)][string]$MaintenancePackagePath,
    [Parameter(Mandatory)][uri]$MaintenancePackageUrl,
    [Parameter(Mandatory)][uri]$ApplicationPackageUrl,
    [Parameter(Mandatory)][uri]$SharedPackageBaseUrl,
    [string]$DotNetRuntimeDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$DotNetRuntimeVersion,
    [string]$RuntimePackagePath,
    [uri]$RuntimePackageUrl,
    [string]$MaintenanceRuntimePackagePath,
    [uri]$MaintenanceRuntimePackageUrl,
    [string[]]$AllowedDownloadOrigins=@(),
    [Parameter(Mandatory)][string]$DependencyCacheDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PackageCache = $(if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'}),
    [string[]]$PublicPackageIds = @('Microsoft.*','System.*','ProjectZ','ProjectZ.*','MonoGame.*','SharpDX*','SocketJack','BouncyCastle.*','NAudio*','SSH.NET','Mono.Nat','NSpeex.Reactive'),
    [string]$ResourcePartitionsPath,
    [string]$PrerequisiteDownloadMapPath,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference='Stop'
$scriptRoot=$PSScriptRoot
. (Join-Path $scriptRoot 'Shared-NuGetValidation.ps1')
function Plain([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    for($node=$full;$node;$node=[IO.Path]::GetDirectoryName($node)){
        if((Test-Path -LiteralPath $node) -and ((Get-Item -LiteralPath $node -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Shared authoring paths cannot traverse links.'}
    }
    return $full
}
function Relative([string]$Path){
    $value=$Path.Replace('\','/')
    if([string]::IsNullOrWhiteSpace($value) -or [IO.Path]::IsPathRooted($value) -or @($value.Split('/') | Where-Object {$_ -in @('','.','..')}).Count){throw 'Invalid authoring relative path.'}
    if($value -match '[\x00-\x1f<>:"|?*]' -or $value.Length -gt 240){throw 'Unsafe authoring relative path.'}
    return $value
}
function Files([string]$Directory){
    foreach($item in Get-ChildItem -LiteralPath $Directory -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Payload inputs cannot contain links.'}
        if($item.PSIsContainer){Files $item.FullName}else{$item.FullName}
    }
}
function Hash([string]$Path){return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function SaveJson($Value,[string]$Path){$Value|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $Path -Encoding utf8NoBOM}
function Invoke-SharedCli([string[]]$Arguments){
    $result=& $CliPath @Arguments
    if($LASTEXITCODE){throw ('Installer authoring failed: '+$Arguments[0])}
    return $result
}
function CopyOwned([string]$Source,[string]$Destination){
    Plain $Source|Out-Null;Plain $Destination|Out-Null
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Destination)) -Force|Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination
}
$source=Plain $SourceDirectory;$output=Plain $OutputDirectory;$cache=Plain $DependencyCacheDirectory
if(Test-Path -LiteralPath $output){throw 'Use a new task-owned shared application output directory.'}
if($output.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Application source and owned output must be separate.'}
$manifest=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json -AsHashtable
if($manifest.legacyMsi -or $manifest.legacyExecutable){throw 'Author an exact signed native migration before partitioning a legacy backend.'}
$reviewedSecurityPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$reviewedSecurityModule=$null
if($manifest.modules -and $manifest.modules.SocketJackSecurityLoader){
    $security=$manifest.modules.SocketJackSecurityLoader
    if($security.preparationOnly -or !$security.components){throw 'Supply a final reviewed sealed SecurityLoader manifest before shared packaging.'}
    $reviewedSecurityModule=$security|ConvertTo-Json -Depth 100 -Compress
    foreach($path in @($security.publicPayload)+@($security.components|ForEach-Object{$_.path})+@('SecurityLoader/bootstrap.json','SecurityLoader/bootstrap.sig','SecurityLoader/bootstrap.pub')){
        $path=Relative $path
        if(!(Test-Path -LiteralPath (Join-Path $source $path) -PathType Leaf)){throw ('The sealed reviewed closure requires local '+$path+'. Author a reviewed loose-payload bridge; a bundled-only sealing policy cannot be inferred or weakened.')}
        $reviewedSecurityPaths.Add($path)|Out-Null
    }
    if($reviewedSecurityPaths.Contains($manifest.entryPoint)){throw 'The sealed closure pins the original apphost. Author a reviewed native-launcher bridge before shared packaging.'}
}
$entry=Relative $manifest.entryPoint;$entryDirectory=[IO.Path]::GetDirectoryName($entry.Replace('/',[IO.Path]::DirectorySeparatorChar))
$main=[IO.Path]::GetFileNameWithoutExtension($entry)
$runtimeConfig=Relative $(if($entryDirectory){$entryDirectory+'/'+$main+'.runtimeconfig.json'}else{$main+'.runtimeconfig.json'})
$depsPath=Relative $(if($entryDirectory){$entryDirectory+'/'+$main+'.deps.json'}else{$main+'.deps.json'})
$config=Get-Content -LiteralPath (Join-Path $source $runtimeConfig) -Raw|ConvertFrom-Json -AsHashtable
if($config.runtimeOptions.includedFrameworks -or !$config.runtimeOptions.frameworks){throw 'Supply a loose framework-dependent Windows desktop publish; self-contained bundles cannot be split after signing.'}
if($RuntimePackagePath){
    $providedRuntime=Invoke-SharedCli @('inspect',$RuntimePackagePath)|ConvertFrom-Json -AsHashtable
    if(!$providedRuntime.signed -or $providedRuntime.manifest.targetRuntime -cne $Runtime){throw 'Supply an exact signed redistributable runtime package for this architecture.'}
    if($DotNetRuntimeVersion -and $DotNetRuntimeVersion -cne $providedRuntime.manifest.version){throw 'Runtime package version differs from its declared pin.'}
    $DotNetRuntimeVersion=$providedRuntime.manifest.version
    if('dotnet.exe' -notin $providedRuntime.manifest.payload.path -or !@($providedRuntime.manifest.payload|Where-Object{$_.path -like "host/fxr/$DotNetRuntimeVersion/*"}).Count -or
        !@($providedRuntime.manifest.payload|Where-Object{$_.path -like "shared/Microsoft.NETCore.App/$DotNetRuntimeVersion/*"}).Count -or
        !@($providedRuntime.manifest.payload|Where-Object{$_.path -like "shared/Microsoft.WindowsDesktop.App/$DotNetRuntimeVersion/*"}).Count){throw 'The supplied runtime package must contain the complete standard private .NET Desktop layout.'}
}elseif(!$DotNetRuntimeDirectory -or !$DotNetRuntimeVersion){throw 'Supply RuntimePackagePath or the exact redistributable runtime directory and version.'}
foreach($framework in $config.runtimeOptions.frameworks){
    $minimum=[version]$framework.version;$provided=[version]$DotNetRuntimeVersion
    if($minimum.Major -ne $provided.Major -or $minimum.Minor -ne $provided.Minor -or $provided -lt $minimum){throw 'The private runtime must satisfy each exact application framework family and minimum version.'}
}
$sourceVersion=(Get-Item -LiteralPath (Join-Path $source $entry)).VersionInfo.ProductVersion
if($sourceVersion -and $sourceVersion.Split('+')[0] -ne $manifest.version){throw 'The application file version must match its reviewed native manifest.'}
$origin=[uri]$manifest.updateUrl
foreach($url in @($ApplicationPackageUrl,$SharedPackageBaseUrl)){
    if($url.Scheme -ne 'https' -or $url.UserInfo -or $url.Fragment -or $url.GetLeftPart([UriPartial]::Authority) -ne $origin.GetLeftPart([UriPartial]::Authority)){throw 'Application and dependency URLs must remain on the signed HTTPS update origin.'}
}
$inputFiles=@(Files $source)
$inputBytes=($inputFiles|ForEach-Object{(Get-Item -LiteralPath $_).Length}|Measure-Object -Sum).Sum
$runtimeInputs=@();$runtimeBytes=0
if(!$RuntimePackagePath){
    $runtimeRoot=Plain $DotNetRuntimeDirectory
    $runtimeInputs=@((Join-Path $runtimeRoot 'dotnet.exe'))
    foreach($relative in @("host/fxr/$DotNetRuntimeVersion","shared/Microsoft.NETCore.App/$DotNetRuntimeVersion","shared/Microsoft.WindowsDesktop.App/$DotNetRuntimeVersion")){
        $directory=Join-Path $runtimeRoot $relative
        if(!(Test-Path -LiteralPath $directory -PathType Container)){throw ('Missing exact redistributable runtime directory: '+$relative)}
        $runtimeInputs+=@(Files $directory)
    }
    foreach($notice in @('LICENSE.txt','ThirdPartyNotices.txt')){if(Test-Path -LiteralPath (Join-Path $runtimeRoot $notice)){$runtimeInputs+=Join-Path $runtimeRoot $notice}}
    $runtimeBytes=($runtimeInputs|ForEach-Object{(Get-Item -LiteralPath $_).Length}|Measure-Object -Sum).Sum
}
$need=3L*([long]$inputBytes+[long]$runtimeBytes)+64MB
if((Get-PSDrive -Name ([IO.Path]::GetPathRoot($output).TrimEnd('\').TrimEnd(':'))).Free -lt $need){throw ('Authoring needs up to '+$need+' free bytes for its one staging tree and retained artifacts.')}
New-Item -ItemType Directory -Path $output,$cache -Force|Out-Null
$work=Join-Path $output 'staging';$payload=Join-Path $work 'application';New-Item -ItemType Directory -Path $payload -Force|Out-Null
$maintenance=(Invoke-SharedCli @('inspect',$MaintenancePackagePath)|ConvertFrom-Json -AsHashtable)
if(!$maintenance.signed){throw 'The maintenance dependency must already be signed.'}
$pin=$maintenance.publisherKeySha256.ToLowerInvariant();$legal=$maintenance.manifest.legalDocuments
$excluded=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$definitions=[Collections.Generic.List[object]]::new();$publication=[Collections.Generic.List[object]]::new();$serial=0
function Pack([string]$Id,[string]$Version,[hashtable]$Assets,[string]$Visibility='public',[string]$Feature=''){
    if(!$Assets.Count){return}
    $script:serial++;$area=Join-Path $work ('dependency-'+$script:serial);$filesRoot=Join-Path $area 'files';New-Item -ItemType Directory -Path $filesRoot -Force|Out-Null
    $records=@(foreach($relative in @($Assets.Keys|Sort-Object)){
        $relative=Relative $relative;$file=Plain $Assets[$relative]
        [ordered]@{source=$relative;path=$relative;size=(Get-Item -LiteralPath $file).Length;sha256=Hash $file}
    })
    $packageManifest=[ordered]@{formatVersion=4;id=$Id;name=$Id;version=$Version;publisher=$manifest.publisher;scope='user';targetPlatform='windows';targetRuntime=$Runtime;legalDocuments=$legal;payload=$records}
    $identity=[Text.Encoding]::UTF8.GetBytes(($pin+($packageManifest|ConvertTo-Json -Depth 100 -Compress)))
    $key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identity)).ToLowerInvariant()
    $archive=Join-Path $cache ($key+'.hinst')
    $lock=[IO.File]::Open($archive+'.lock',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        if(!(Test-Path -LiteralPath $archive)){
            foreach($record in $records){CopyOwned $Assets[$record.path] (Join-Path $filesRoot $record.path)}
            $definitionPath=Join-Path $area 'manifest.json';SaveJson $packageManifest $definitionPath
            Invoke-SharedCli @('pack',$definitionPath,'--source',$filesRoot,'--output',$archive,'--sign-key',$SigningKey)|Out-Null
        }
        Invoke-SharedCli @('verify',$archive,'--trust-key',$pin,'--require-signature')|Out-Null
        $actual=(Invoke-SharedCli @('inspect',$archive)|ConvertFrom-Json -AsHashtable)
        if($actual.manifest.id -cne $Id -or $actual.manifest.version -cne $Version -or $actual.manifest.targetRuntime -cne $Runtime -or $actual.manifest.payload.Count -ne $records.Count){throw 'A cached dependency has a different signed identity.'}
        foreach($record in $records){$other=@($actual.manifest.payload|Where-Object path -CEQ $record.path);if($other.Count -ne 1 -or $other[0].sha256 -ine $record.sha256 -or $other[0].size -ne $record.size){throw 'Cached dependency content differs from its reviewed sources.'}}
    }finally{$lock.Dispose()}
    $sha=Hash $archive;$bytes=(Get-Item -LiteralPath $archive).Length
    $url=$SharedPackageBaseUrl.AbsoluteUri.TrimEnd('/')+'/'+[uri]::EscapeDataString($Id)+'/'+[uri]::EscapeDataString($Version)+'/'+$sha+'.hinst'
    $definition=[ordered]@{id=$Id;version=$Version;targetRuntime=$Runtime;downloadUrl=$url;sha256=$sha;size=$bytes;publisherKeySha256=$pin}
    if($Feature){$definition.feature=$Feature}
    $definitions.Add($definition);$publication.Add([ordered]@{id=$Id;version=$Version;path=$archive;url=$url;bytes=$bytes;sha256=$sha;visibility=$Visibility})
}
$runtimeId='socketjack.dotnet.desktop.'+$DotNetRuntimeVersion.Split('.')[0]
if($RuntimePackagePath){
    if($providedRuntime.publisherKeySha256 -ine $pin){throw 'The provided runtime must share the application publisher pin.'}
    Invoke-SharedCli @('verify',$RuntimePackagePath,'--require-signature','--trust-key',$pin)|Out-Null
    $runtimeId=$providedRuntime.manifest.id;$runtimeSha=Hash $RuntimePackagePath;$runtimeSize=(Get-Item -LiteralPath $RuntimePackagePath).Length
    $runtimeDownload=if($RuntimePackageUrl){$RuntimePackageUrl.AbsoluteUri}else{$SharedPackageBaseUrl.AbsoluteUri.TrimEnd('/')+'/'+[uri]::EscapeDataString($runtimeId)+'/'+[uri]::EscapeDataString($DotNetRuntimeVersion)+'/'+$runtimeSha+'.hinst'}
    $definitions.Add([ordered]@{id=$runtimeId;version=$DotNetRuntimeVersion;targetRuntime=$Runtime;downloadUrl=$runtimeDownload;sha256=$runtimeSha;size=$runtimeSize;publisherKeySha256=$pin})
    $publication.Add([ordered]@{id=$runtimeId;version=$DotNetRuntimeVersion;path=[IO.Path]::GetFullPath($RuntimePackagePath);url=$runtimeDownload;bytes=$runtimeSize;sha256=$runtimeSha;visibility='public';reused=$true})
}else{
    $runtimeAssets=@{};foreach($file in $runtimeInputs){$runtimeAssets[[IO.Path]::GetRelativePath($runtimeRoot,$file).Replace('\','/')]=$file}
    Pack $runtimeId $DotNetRuntimeVersion $runtimeAssets
}
if(!$MaintenanceRuntimePackagePath){
    if($runtimeId -cne 'socketjack.dotnet.desktop.10'){throw 'Supply the separate canonical .NET 10 MaintenanceRuntimePackagePath/Url when the application uses a different runtime family.'}
    $mainRuntime=@($publication|Where-Object id -CEQ $runtimeId)[0]
    $MaintenanceRuntimePackagePath=$mainRuntime.path
    $MaintenanceRuntimePackageUrl=[uri]$mainRuntime.url
}
if(!$MaintenanceRuntimePackageUrl){throw 'Supply the exact HTTPS MaintenanceRuntimePackageUrl.'}
$maintenanceRuntime=Invoke-SharedCli @('inspect',$MaintenanceRuntimePackagePath)|ConvertFrom-Json -AsHashtable
if(!$maintenanceRuntime.signed -or $maintenanceRuntime.publisherKeySha256 -ine $pin -or $maintenanceRuntime.manifest.id -cne 'socketjack.dotnet.desktop.10' -or $maintenanceRuntime.manifest.targetRuntime -cne $Runtime){throw 'Maintenance requires the canonical same-publisher .NET 10 Desktop archive.'}
Invoke-SharedCli @('verify',$MaintenanceRuntimePackagePath,'--require-signature','--trust-key',$pin)|Out-Null
foreach($url in @($MaintenancePackageUrl,$MaintenanceRuntimePackageUrl)){
    if($url.Scheme -ne 'https' -or $url.UserInfo -or $url.Fragment -or $url.GetLeftPart([UriPartial]::Authority) -ne $origin.GetLeftPart([UriPartial]::Authority)){throw 'Maintenance dependencies must have immutable HTTPS URLs on the signed application update origin.'}
}
$deps=Get-Content -LiteralPath (Join-Path $source $depsPath) -Raw|ConvertFrom-Json -AsHashtable
$target=$deps.targets[$deps.runtimeTarget.name]
foreach($libraryName in @($target.Keys|Sort-Object)){
    $library=$deps.libraries[$libraryName]
    $name=$libraryName.Substring(0,$libraryName.LastIndexOf('/'));$version=$libraryName.Substring($libraryName.LastIndexOf('/')+1)
    if($library.type -ne 'package' -or !@($PublicPackageIds|Where-Object{$name -like $_}).Count){continue}
    $packagePath=if($library.path){$library.path}else{$libraryName.ToLowerInvariant()}
    $packageDirectory=Join-Path $PackageCache $packagePath
    $provenance=Join-Path $packageDirectory '.nupkg.metadata'
    if(!(Test-Path -LiteralPath $provenance)){continue}
    $packageOrigin=(Get-Content -LiteralPath $provenance -Raw|ConvertFrom-Json).source
    if($packageOrigin -notin @('https://api.nuget.org/v3/index.json','https://www.nuget.org/api/v2/')){continue}
    $nupkg=Join-Path $packageDirectory ($name.ToLowerInvariant()+'.'+$version.ToLowerInvariant()+'.nupkg')
    if(!(Test-Path -LiteralPath $nupkg)){continue}
    if(!$library.sha512){continue}
    $nugetReader=Open-VerifiedPublicNuGetArchive $nupkg $name $version $library.sha512 $provenance
    try{
    $assets=@{};$selected=$target[$libraryName]
    foreach($kind in @('runtime','native','resources','runtimeTargets')){
        if(!$selected.Contains($kind)){continue}
        # Native/plugin loaders may use explicit adjacent paths outside the .NET
        # host's dependency resolver. Keep them local until a reviewed provider
        # supports relocation; known tool/resource families have separate adapters.
        if($kind -eq 'native'){continue}
        foreach($asset in $selected[$kind].Keys){
            if($asset.EndsWith('/_._')){continue}
            $details=$selected[$kind][$asset]
            if($kind -eq 'runtimeTargets' -and $details.assetType -eq 'native'){continue}
            if($kind -eq 'runtimeTargets' -and $details.rid -notin @($Runtime,'win','any')){continue}
            $original=Join-Path (Join-Path $PackageCache $packagePath) $asset
            if(!(Test-Path -LiteralPath $original -PathType Leaf)){continue}
            Assert-VerifiedNuGetAsset $nugetReader $asset $original
            $leaf=[IO.Path]::GetFileName($asset)
            $applicationSource=if($entryDirectory){Join-Path $source $entryDirectory}else{$source}
            $candidates=@((Join-Path $applicationSource $asset),(Join-Path $applicationSource $leaf))
            if($kind -eq 'resources' -and $details.locale){$candidates=@((Join-Path $applicationSource ($details.locale+'/'+$leaf)))+$candidates}
            $published=@($candidates|Where-Object{Test-Path -LiteralPath $_ -PathType Leaf}|Select-Object -Unique)
            foreach($candidate in $published){
                if($reviewedSecurityPaths.Contains([IO.Path]::GetRelativePath($source,$candidate).Replace('\','/'))){continue}
                if((Hash $candidate) -ne (Hash $original)){continue} # Reviewed/private overrides remain in the application payload.
                $assets[($packagePath.ToLowerInvariant()+'/'+$asset)]=$original
                $excluded.Add([IO.Path]::GetRelativePath($source,$candidate).Replace('\','/'))|Out-Null
            }
        }
    }
    if($assets.Count){
        foreach($notice in @(Get-ChildItem -LiteralPath $packageDirectory -File|Where-Object{$_.Name -match '(?i)^(license|notice|third.party)' -or $_.Name -match '(?i)\.nuspec$'})){
            $assets['ThirdParty/'+$name+'/'+$notice.Name]=$notice.FullName
        }
    }
    Pack ('nuget.'+$name) $version $assets
    }finally{$nugetReader.Dispose()}
}
if($ResourcePartitionsPath){
    foreach($partition in @(Get-Content -LiteralPath $ResourcePartitionsPath -Raw|ConvertFrom-Json -AsHashtable)){
        if($partition.visibility -notin @('public','protected')){throw 'Each resource partition must explicitly declare public or protected visibility.'}
        $assets=@{}
        foreach($mapping in $partition.sources){
            $prefix=Relative $mapping.prefix;$directory=Join-Path $source $prefix
            if(!(Test-Path -LiteralPath $directory)){if($mapping.required){throw ('Missing required resource partition '+$prefix)};continue}
            foreach($file in @(Files $directory)){
                if($reviewedSecurityPaths.Contains([IO.Path]::GetRelativePath($source,$file).Replace('\','/'))){throw 'A resource partition overlaps the sealed reviewed SecurityLoader closure; author an explicit bridge before relocating it.'}
                $relative=([IO.Path]::GetRelativePath($directory,$file)).Replace('\','/')
                $targetPath=if($mapping.destination){$mapping.destination.TrimEnd('/')+'/'+$relative}else{$relative}
                if($assets.ContainsKey($targetPath) -and (Hash $assets[$targetPath]) -ne (Hash $file)){throw 'Resource aliases contain different bytes; keep separate versioned families.'}
                $assets[$targetPath]=$file;$excluded.Add([IO.Path]::GetRelativePath($source,$file).Replace('\','/'))|Out-Null
            }
        }
        foreach($mapping in @($partition.equivalentSources)){
            $directory=Join-Path $source (Relative $mapping.prefix);if(!(Test-Path -LiteralPath $directory)){continue}
            foreach($file in @(Files $directory)){
                $relative=([IO.Path]::GetRelativePath($directory,$file)).Replace('\','/')
                $targetPath=if($mapping.destination){$mapping.destination.TrimEnd('/')+'/'+$relative}else{$relative}
                $ownedRelative=[IO.Path]::GetRelativePath($source,$file).Replace('\','/')
                if(!$reviewedSecurityPaths.Contains($ownedRelative) -and $assets.ContainsKey($targetPath) -and (Hash $assets[$targetPath]) -eq (Hash $file)){$excluded.Add($ownedRelative)|Out-Null}
            }
        }
        Pack $partition.id $partition.version $assets $partition.visibility $partition.feature
    }
}
if($manifest.updateCompanion){
    foreach($hostDependency in @(
        [ordered]@{id=$maintenance.manifest.id;version=$maintenance.manifest.version;path=$MaintenancePackagePath;url=$MaintenancePackageUrl.AbsoluteUri;inspection=$maintenance},
        [ordered]@{id=$maintenanceRuntime.manifest.id;version=$maintenanceRuntime.manifest.version;path=$MaintenanceRuntimePackagePath;url=$MaintenanceRuntimePackageUrl.AbsoluteUri;inspection=$maintenanceRuntime}
    )){
        $dependency=[ordered]@{id=$hostDependency.id;version=$hostDependency.version;targetRuntime=$Runtime;downloadUrl=$hostDependency.url;sha256=Hash $hostDependency.path;size=(Get-Item -LiteralPath $hostDependency.path).Length;publisherKeySha256=$pin;feature=$manifest.updateCompanion.feature}
        $existing=@($definitions|Where-Object id -IEQ $dependency.id)
        if($existing.Count){
            if($existing.Count -ne 1 -or $existing[0].sha256 -ine $dependency.sha256 -or $existing[0].version -cne $dependency.version -or $existing[0].size -ne $dependency.size){throw 'Two dependency roles select different archives for the same package ID.'}
            if($existing[0].feature -and $existing[0].feature -cne $dependency.feature){$existing[0].Remove('feature')}
        }else{
            $definitions.Add($dependency)
            $publication.Add([ordered]@{id=$dependency.id;version=$dependency.version;path=[IO.Path]::GetFullPath($hostDependency.path);url=$dependency.downloadUrl;bytes=$dependency.size;sha256=$dependency.sha256;visibility='public';reused=$true})
        }
    }
    $updaterDescriptor=[ordered]@{formatVersion=1;productId='heirowUpdater';name='heirowUpdater';version=$maintenance.manifest.version;scope='user';hostKind='updater';publisherKeySha256=$pin;allowedDownloadOrigins=$AllowedDownloadOrigins;
        offlineOnly=$true;packages=@(
            [ordered]@{id=$maintenance.manifest.id;version=$maintenance.manifest.version;targetRuntime=$Runtime;url=$MaintenancePackageUrl.AbsoluteUri;sha256=Hash $MaintenancePackagePath;size=(Get-Item -LiteralPath $MaintenancePackagePath).Length},
            [ordered]@{id=$maintenanceRuntime.manifest.id;version=$maintenanceRuntime.manifest.version;targetRuntime=$Runtime;url=$MaintenanceRuntimePackageUrl.AbsoluteUri;sha256=Hash $MaintenanceRuntimePackagePath;size=(Get-Item -LiteralPath $MaintenanceRuntimePackagePath).Length});
        hostPackageId=$maintenance.manifest.id;hostEntryPoint='heirowUpdater.exe';dotNetPackageId=$maintenanceRuntime.manifest.id;hostArguments=@()}
    $updaterBytes=[Text.Encoding]::UTF8.GetBytes(($pin+(Hash $GenericBootstrapPath)+($updaterDescriptor|ConvertTo-Json -Depth 50 -Compress)))
    $updaterKey=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($updaterBytes)).ToLowerInvariant()
    $updater=Join-Path $cache ($updaterKey+'.exe')
    if(!(Test-Path -LiteralPath $updater)){
        $updaterDescriptorPath=Join-Path $work 'updater-bootstrap.json';SaveJson $updaterDescriptor $updaterDescriptorPath
        Invoke-SharedCli @('bind-bootstrap',$updaterDescriptorPath,'--launcher',$GenericBootstrapPath,'--output',$updater,'--sign-key',$SigningKey)|Out-Null
    }
    Invoke-SharedCli @('inspect-bootstrap',$updater,'--trust-key',$pin)|Out-Null
    $updaterId='socketjack.updater.host'
    Pack $updaterId $maintenance.manifest.version @{'heirowUpdater.exe'=$updater} 'public' $manifest.updateCompanion.feature
    $excluded.Add($manifest.updateCompanion.executablePath)|Out-Null
    $manifest.updateCompanion.sharedPackageId=$updaterId;$manifest.updateCompanion.executablePath='heirowUpdater.exe'
    $manifest.updateCompanion.runtimeMode='shared';$manifest.updateCompanion.updateMode='files';$manifest.updateCompanion.updateProtocol='hfiles1'
}
$downloadMap=if($PrerequisiteDownloadMapPath){Get-Content -LiteralPath $PrerequisiteDownloadMapPath -Raw|ConvertFrom-Json -AsHashtable}else{@{}}
foreach($prerequisite in @($manifest.prerequisites)){
    if(!$prerequisite.executable){continue}
    $relative=Relative $prerequisite.executable
    if(!$downloadMap.ContainsKey($prerequisite.id)){throw ('Author a pinned HTTPS download map for prerequisite '+$prerequisite.id+'; embedded redistributables are excluded from thin application packages.')}
    $download=$downloadMap[$prerequisite.id];$uri=[uri]$download.url
    if($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Fragment -or (Hash (Join-Path $source $relative)) -ine $prerequisite.sha256){throw 'Prerequisite download mapping does not match the reviewed executable.'}
    $privilege=Invoke-SharedCli @('inspect-execution-level',(Join-Path $source $relative))|ConvertFrom-Json
    if($privilege.requiresAdministrator){$prerequisite.requiresAdministrator=$true}
    $prerequisite.downloadUrl=$uri.AbsoluteUri;$prerequisite.Remove('executable');$excluded.Add($relative)|Out-Null
    $publication.Add([ordered]@{id=$prerequisite.id;path=(Join-Path $source $relative);url=$uri.AbsoluteUri;sha256=$prerequisite.sha256;bytes=(Get-Item -LiteralPath (Join-Path $source $relative)).Length;visibility='public-prerequisite'})
}
foreach($file in $inputFiles){
    $relative=[IO.Path]::GetRelativePath($source,$file).Replace('\','/')
    if($excluded.Contains($relative) -or $relative -match '(?i)(^|/)Tools/Installer/' -or $relative -match '(?i)\.pdb$|\.runtimeconfig\.dev\.json$'){continue}
    if($relative -match '(?i)(^|/)runtimes/([^/]+)/' -and $matches[2] -notin @($Runtime,'win','any')){continue}
    CopyOwned $file (Join-Path $payload $relative)
}
$hostEntry=Relative $(if($entryDirectory){$entryDirectory+'/'+$main+'.App.exe'}else{$main+'.App.exe'})
Move-Item -LiteralPath (Join-Path $payload $entry) -Destination (Join-Path $payload $hostEntry)
$manifest.formatVersion=5;$manifest.targetPlatform='windows';$manifest.targetRuntime=$Runtime
$existing=@($manifest.sharedPackages|Where-Object{$_ -and $_.id -notin @($definitions|ForEach-Object{$_.id})});$manifest.sharedPackages=@($existing+$definitions.ToArray())
$probeIds=@($manifest.sharedPackages|Where-Object{!$_.feature}|ForEach-Object{$_.id})
$privilege=Invoke-SharedCli @('inspect-execution-level',(Join-Path $payload $hostEntry))|ConvertFrom-Json
$applicationAdmin=([bool]$manifest.applicationLaunch.requiresAdministrator -or [bool]$privilege.requiresAdministrator)
$manifest.applicationLaunch=@{executablePath=$hostEntry;runtimeConfigPath=$runtimeConfig;runtimePackageId=$runtimeId;probingPackageIds=$probeIds;requiresAdministrator=$applicationAdmin}
$localRoot=if($entryDirectory){Join-Path $payload $entryDirectory}else{$payload}
$originalPayload=@{};foreach($file in @($manifest.payload|Where-Object{$_})){$originalPayload[$file.path]=$file}
foreach($file in @(Files $localRoot)){
    $relative=[IO.Path]::GetRelativePath($payload,$file).Replace('\','/')
    if($originalPayload.ContainsKey($relative) -and $originalPayload[$relative].feature){
        $feature=@($manifest.features|Where-Object id -IEQ $originalPayload[$relative].feature)
        if($feature.Count -ne 1 -or !$feature[0].required){throw 'Feature-optional app-root files cannot be required by the signed application launcher. Move optional resources to explicit shared providers or make startup files required.'}
    }
}
$descriptor=[ordered]@{formatVersion=1;productId=$manifest.id;name=$manifest.name;version=$manifest.version;scope=$manifest.scope;hostKind='application';publisherKeySha256=$pin;requiresAdministrator=$applicationAdmin;
    packages=@($manifest.sharedPackages|Where-Object{!$_.feature}|ForEach-Object{[ordered]@{id=$_.id;version=$_.version;targetRuntime=$Runtime;url=$_.downloadUrl;sha256=$_.sha256;size=$_.size}});
    hostEntryPoint=[IO.Path]::GetFileName($hostEntry);dotNetPackageId=$runtimeId;runtimeConfigPath=[IO.Path]::GetFileName($runtimeConfig);probingPackageIds=$probeIds;
    applicationFiles=@(Files $localRoot|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($localRoot,$_).Replace('\','/');sha256=Hash $_;size=(Get-Item -LiteralPath $_).Length}})}
$descriptorPath=Join-Path $work 'application-bootstrap.json';SaveJson $descriptor $descriptorPath
Invoke-SharedCli @('bind-bootstrap',$descriptorPath,'--launcher',$GenericBootstrapPath,'--output',(Join-Path $payload $entry),'--sign-key',$SigningKey)|Out-Null
if((Get-Item -LiteralPath (Join-Path $payload $entry)).Length -ge 20000000){throw 'The application launcher exceeds the 20 MB native-host budget.'}
$manifest.payload=@(Files $payload|ForEach-Object{$relative=[IO.Path]::GetRelativePath($payload,$_).Replace('\','/');$record=[ordered]@{source=$relative;path=$relative};
    if($originalPayload.ContainsKey($relative) -and $originalPayload[$relative].feature){$record.feature=$originalPayload[$relative].feature}
    foreach($rootDefinition in @($manifest.installationRoots)){if($relative.StartsWith($rootDefinition.id+'/',[StringComparison]::OrdinalIgnoreCase)){$record.targetRoot=$rootDefinition.id}}
    $record})
if($reviewedSecurityModule){
    if(($manifest.modules.SocketJackSecurityLoader|ConvertTo-Json -Depth 100 -Compress) -cne $reviewedSecurityModule){throw 'Shared packaging changed the sealed SecurityLoader policy.'}
    foreach($path in $reviewedSecurityPaths){
        if(!(Test-Path -LiteralPath (Join-Path $payload $path) -PathType Leaf) -or (Hash (Join-Path $payload $path)) -cne (Hash (Join-Path $source $path))){throw 'Shared packaging changed or relocated a reviewed sealed closure file.'}
    }
}
foreach($shortcut in @($manifest.shortcuts)){if($shortcut.icon -eq $entry){$shortcut.icon=$hostEntry}}
$prepared=Join-Path $output 'installer.json';SaveJson $manifest $prepared
SaveJson $publication.ToArray() (Join-Path $output 'shared-publication-plan.json')
SaveJson ([ordered]@{ownedOutput=$output;staging=$work;dependencyCache=$cache;dependencyCacheIsShared=$true;inputSource=$source;inputBytes=$inputBytes;runtimeInputBytes=$runtimeBytes;peakBudgetBytes=$need;retain=@('installer.json','shared-publication-plan.json','web-installer','verified referenced dependency archives');cleanupRequiresApproval=$true}) (Join-Path $output 'storage-manifest.json')
& (Join-Path $scriptRoot 'Build-WebInstaller.ps1') -ManifestPath $prepared -SourceDirectory $payload -SigningKey $SigningKey -GenericBootstrapPath $GenericBootstrapPath -MaintenancePackagePath $MaintenancePackagePath -MaintenancePackageUrl $MaintenancePackageUrl -RuntimePackagePath $MaintenanceRuntimePackagePath -RuntimePackageUrl $MaintenanceRuntimePackageUrl -ApplicationPackageUrl $ApplicationPackageUrl -OutputDirectory (Join-Path $output 'web-installer') -Runtime $Runtime -CliPath $CliPath -AllowedDownloadOrigins $AllowedDownloadOrigins
if($LASTEXITCODE){throw 'Shared application web installer assembly failed.'}
