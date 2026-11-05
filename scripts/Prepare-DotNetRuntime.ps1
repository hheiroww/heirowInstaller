#requires -Version 7.0
<# Downloads official, SHA-512-pinned .NET Core/Desktop ZIPs into one retained runtime layout. No installation or deletion. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.0\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64'
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
function Plain([string]$Path){
    for($node=[IO.Path]::GetFullPath($Path);$node;$node=[IO.Path]::GetDirectoryName($node)){
        if((Test-Path -LiteralPath $node) -and ((Get-Item -LiteralPath $node -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Runtime preparation paths cannot traverse links.'}
    }
}
Plain $output
if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace -lt 2GB){throw 'Runtime preparation requires at least 2 GiB free for retained archives and one merged layout.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$lock=[IO.File]::Open((Join-Path $output 'prepare.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
$http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromMinutes(15)
function Official([uri]$Url){
    if($Url.Scheme -cne 'https' -or $Url.Host -cne 'builds.dotnet.microsoft.com' -or $Url.UserInfo -or $Url.Fragment){throw 'Only the exact official Microsoft build download origin is permitted.'}
}
function Download([uri]$Url,[string]$Path,[long]$Maximum){
    Official $Url
    $reply=$http.GetAsync($Url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try{
        $reply.EnsureSuccessStatusCode()|Out-Null
        if($reply.Content.Headers.ContentLength -gt $Maximum){throw 'Official download exceeds the bounded archive size.'}
        $input=$reply.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $stream=[IO.File]::Open($Path,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{
            $buffer=[byte[]]::new(131072);$total=0L
            while(($read=$input.ReadAsync($buffer,0,$buffer.Length).GetAwaiter().GetResult()) -gt 0){
                $total+=$read;if($total -gt $Maximum){throw 'Official download exceeds its size bound.'}
                $null=$stream.WriteAsync($buffer,0,$read).GetAwaiter().GetResult()
                if($reply.Content.Headers.ContentLength){Write-Progress -Activity ('Downloading '+[IO.Path]::GetFileName($Url.AbsolutePath)) -Status ($total.ToString('N0')+' bytes') -PercentComplete ([int](100*$total/$reply.Content.Headers.ContentLength))}
            }
            $stream.Flush($true)
        }finally{$stream.Dispose();$input.Dispose();Write-Progress -Activity 'Downloading runtime' -Completed}
    }finally{$reply.Dispose()}
}
function Sha([string]$Path,[string]$Algorithm){(Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()}
try{
    $channel=([version]$Version).Major.ToString()+'.0'
    $metadataUrl='https://builds.dotnet.microsoft.com/dotnet/release-metadata/'+$channel+'/releases.json'
    $metadataPath=Join-Path $output ('release-metadata-'+$channel+'.json')
    if(!(Test-Path -LiteralPath $metadataPath)){Download ([uri]$metadataUrl) $metadataPath 20MB}
    $metadata=Get-Content -LiteralPath $metadataPath -Raw|ConvertFrom-Json -AsHashtable
    if($metadata['channel-version'] -cne $channel){throw 'Microsoft release metadata belongs to another channel.'}
    $release=@($metadata.releases|Where-Object{$_['release-version'] -ceq $Version})
    if($release.Count -ne 1 -or $release[0].runtime.version -cne $Version -or $release[0].windowsdesktop.version -cne $Version){throw 'The exact runtime and Windows Desktop patch were not found in official release metadata.'}
    $downloads=Join-Path $output 'downloads';$payload=Join-Path $output 'runtime'
    New-Item -ItemType Directory -Path $downloads,$payload -Force|Out-Null
    $records=[Collections.Generic.List[object]]::new()
    foreach($component in @('runtime','windowsdesktop')){
        $expectedName=if($component -eq 'runtime'){'dotnet-runtime-'+$Runtime+'.zip'}else{'windowsdesktop-runtime-'+$Runtime+'.zip'}
        $asset=@($release[0][$component].files|Where-Object{$_.rid -ceq $Runtime -and $_.name -ceq $expectedName})
        if($asset.Count -ne 1 -or $asset[0].hash -cnotmatch '^[0-9a-fA-F]{128}$'){throw 'The exact runtime ZIP is absent from official metadata; apphost packs are not runtime downloads.'}
        $asset=$asset[0];Official ([uri]$asset.url)
        $archive=Join-Path $downloads ([IO.Path]::GetFileName(([uri]$asset.url).AbsolutePath))
        if(!(Test-Path -LiteralPath $archive)){
            Write-Output ('Downloading official '+$component+' '+$Version+' '+$Runtime)
            $pending=$archive+'.partial';Download ([uri]$asset.url) $pending 256MB
            if((Sha $pending 'SHA512') -ine $asset.hash){throw 'Microsoft runtime ZIP SHA-512 verification failed; retained partial is not trusted.'}
            Move-Item -LiteralPath $pending -Destination $archive
        }
        if((Sha $archive 'SHA512') -ine $asset.hash){throw 'The retained Microsoft runtime ZIP differs from its official SHA-512.'}
        $zip=[IO.Compression.ZipFile]::OpenRead($archive)
        try{
            $expanded=0L
            foreach($entry in $zip.Entries){
                $relative=$entry.FullName.Replace('\','/')
                if(!$relative -or $relative.EndsWith('/')){continue}
                if($relative.StartsWith('/') -or $relative -match '(^|/)\.{1,2}(/|$)|[\x00-\x1f<>:"|?*]' -or $relative.Length -gt 240){throw 'Unsafe Microsoft ZIP entry path.'}
                $unixType=($entry.ExternalAttributes -shr 16) -band 0xF000
                if($unixType -notin @(0,0x8000) -or ($entry.ExternalAttributes -band 0x400)){throw 'Links are not permitted in runtime ZIPs.'}
                $allowed=$relative -eq 'dotnet.exe' -or $relative -in @('LICENSE.txt','ThirdPartyNotices.txt') -or
                    $relative.StartsWith('host/fxr/'+$Version+'/',[StringComparison]::Ordinal) -or
                    $relative.StartsWith('shared/Microsoft.NETCore.App/'+$Version+'/',[StringComparison]::Ordinal) -or
                    $relative.StartsWith('shared/Microsoft.WindowsDesktop.App/'+$Version+'/',[StringComparison]::Ordinal)
                if(!$allowed){throw ('Unexpected content outside the standard runtime layout: '+$relative)}
                $expanded+=$entry.Length;if($expanded -gt 512MB){throw 'The runtime ZIP exceeds its expanded bound.'}
                $target=[IO.Path]::GetFullPath((Join-Path $payload $relative));Plain $target
                if(!$target.StartsWith($payload+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Runtime ZIP path escapes its owned layout.'}
                $input=$entry.Open()
                try{
                    if(Test-Path -LiteralPath $target){
                        if((Get-Item -LiteralPath $target).Length -ne $entry.Length -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)) -ine (Sha $target 'SHA256')){throw 'Runtime components disagree on retained destination bytes.'}
                    }else{
                        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
                        $stream=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                        try{$null=$input.CopyToAsync($stream).GetAwaiter().GetResult();$stream.Flush($true)}finally{$stream.Dispose()}
                    }
                }finally{$input.Dispose()}
            }
        }finally{$zip.Dispose()}
        $records.Add([ordered]@{component=$component;url=$asset.url;path=$archive;sha512=$asset.hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $archive).Length;expandedBytes=$expanded})
    }
    foreach($required in @('dotnet.exe',('host/fxr/'+$Version+'/hostfxr.dll'),('shared/Microsoft.NETCore.App/'+$Version+'/coreclr.dll'),('shared/Microsoft.WindowsDesktop.App/'+$Version+'/PresentationFramework.dll'),'LICENSE.txt','ThirdPartyNotices.txt')){
        if(!(Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)){throw ('Missing required runtime file '+$required)}
    }
    $globalInstaller=@($release[0].windowsdesktop.files|Where-Object{$_.rid -ceq $Runtime -and $_.name -ceq ('windowsdesktop-runtime-'+$Runtime+'.exe')})
    $receipt=[ordered]@{version=$Version;runtime=$Runtime;sourceDirectory=$payload;metadataUrl=$metadataUrl;metadataPath=$metadataPath;metadataSha256=Sha $metadataPath 'SHA256';archives=$records.ToArray();globalDesktopInstaller=$(if($globalInstaller.Count -eq 1){[ordered]@{url=$globalInstaller[0].url;sha512=$globalInstaller[0].hash}});payloadBytes=[long]((Get-ChildItem -LiteralPath $payload -Recurse -File|Measure-Object -Property Length -Sum).Sum);cleanupRequiresApproval=$true}
    $receipt|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $output 'runtime-source.json') -Encoding utf8NoBOM
    Write-Output ('Verified runtime layout: '+$payload)
}finally{$http.Dispose();$handler.Dispose();$lock.Dispose()}
