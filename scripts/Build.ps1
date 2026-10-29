#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$Version,
    [string]$OutputDirectory,
    [string]$SigningKey,
    [string]$LegalDocuments,
    [string]$CertificateThumbprint,
    [string]$PackageCache,
    [string]$PreviousPackage,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $LegalDocuments) { $LegalDocuments = Join-Path $projectRoot 'legal/heirowinstaller/legal-documents.json' }
$LegalDocuments = [IO.Path]::GetFullPath($LegalDocuments)
if (-not (Test-Path -LiteralPath $LegalDocuments -PathType Leaf)) { throw 'Supply the actual heirowInstaller Privacy Policy and Terms and Conditions as legal document JSON.' }
$sourceCommit = (& git -C $projectRoot rev-parse --verify HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Build from the committed heirowInstaller repository so release provenance can be recorded.' }
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts/release/$Version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'Test.ps1') -Configuration $Configuration -PackageCache $PackageCache }
$buildDirectory = Join-Path $OutputDirectory ('.build/' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $buildDirectory 'app'
$cliDirectory = Join-Path $buildDirectory 'cli'
$updaterDirectory = Join-Path $buildDirectory 'updater'
foreach ($project in @(@('src/HeirowInstaller.App/HeirowInstaller.App.csproj',$appDirectory),@('src/HeirowInstaller.Cli/HeirowInstaller.Cli.csproj',$cliDirectory),@('src/HeirowUpdater/HeirowUpdater.csproj',$updaterDirectory))) {
    $publishArguments = @('publish',(Join-Path $projectRoot $project[0]),'-c',$Configuration,'-r',$Runtime,'--self-contained','true',
        '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:IncludeAllContentForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true','-p:CopyOutputSymbolsToPublishDirectory=false','-p:DebugType=embedded',"-p:Version=$Version",'-o',$project[1],'--nologo',
        '-m:1','-nodeReuse:false','-p:UseSharedCompilation=false')
    $publishArguments += @("-p:SourceRevisionId=$sourceCommit", "-p:RepositoryCommit=$sourceCommit")
    if ($PackageCache) { $publishArguments += "-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))" }
    Invoke-DotNet -Arguments $publishArguments
}
$launcher = Join-Path $appDirectory 'heirowInstaller.exe'
$cli = Join-Path $cliDirectory 'heirowInstaller.Cli.exe'
$updater = Join-Path $updaterDirectory 'heirowUpdater.exe'
if (-not (Test-Path -LiteralPath $launcher) -or -not (Test-Path -LiteralPath $cli) -or -not (Test-Path -LiteralPath $updater)) { throw 'Published single-file executables are missing.' }
if ($CertificateThumbprint) {
    $signTool = Get-Command signtool.exe -ErrorAction Stop
    foreach ($binary in @($launcher,$cli,$updater)) {
        & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /tr https://timestamp.digicert.com /td SHA256 $binary
        if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
    }
}
$payloadDirectory = Join-Path $buildDirectory 'payload'
New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
# Exact source allowlist: application output, CLI output, public identity, and notices.
foreach ($publishedDirectory in @($appDirectory,$cliDirectory,$updaterDirectory)) {
    foreach ($file in Get-ChildItem -LiteralPath $publishedDirectory -Recurse -File) {
        if ($file.Extension -in @('.pdb','.xml')) { continue }
        $relative = [IO.Path]::GetRelativePath($publishedDirectory,$file.FullName)
        $destination = Join-Path $payloadDirectory $relative
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Conflicting published file: $relative" }
        } else {
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $payloadDirectory 'LICENSE')
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $payloadDirectory 'THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'publishers') -Destination (Join-Path $payloadDirectory 'publishers') -Recurse -Force
foreach ($ownApp in @('heirowinstaller','heirowUpdater')) {
    $legalSource = Join-Path $projectRoot ("legal/" + $ownApp)
    $legalTarget = Join-Path $payloadDirectory ("legal/" + $ownApp)
    New-Item -ItemType Directory -Path $legalTarget -Force | Out-Null
    foreach ($document in @('privacy-policy.txt','terms-and-conditions.txt','legal-documents.json')) {
        Copy-Item -LiteralPath (Join-Path $legalSource $document) -Destination (Join-Path $legalTarget $document)
    }
}
$packageCacheRoot = if ($PackageCache) { [IO.Path]::GetFullPath($PackageCache) } elseif ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$noticesDirectory = Join-Path $payloadDirectory 'ThirdParty'
New-Item -ItemType Directory -Path $noticesDirectory -Force | Out-Null
foreach ($notice in @(@('projectz/2026.10.23.2/THIRD-PARTY-NOTICES.md','ProjectZ-NOTICES.md'),@('socketjack/2026.10.24/License.txt','SocketJack-LICENSE.txt'))) {
    $noticePath = Join-Path $packageCacheRoot $notice[0]
    if (-not (Test-Path -LiteralPath $noticePath)) { throw "Required package notice is missing: $($notice[0])" }
    Copy-Item -LiteralPath $noticePath -Destination (Join-Path $noticesDirectory $notice[1])
}
$manifest = [ordered]@{
    formatVersion=2; id='heirowinstaller'; name='heirowInstaller'; version=$Version; publisher='SocketJack'
    description='Native Windows installers, updates, and publishing built with Project-Z.'
    entryPoint='heirowInstaller.exe'; installDirectory='{LocalAppData}/Programs/SocketJack/heirowInstaller'; scope='user'
    homepage='https://socketjack.com/heirowInstaller/'; licenseFile='LICENSE'
    updateUrl='https://socketjack.com/installers/heirowinstaller/update.json'
    features=@(@{id='updates';name='Application updates';description='Check for updates after heirowInstaller exits.';required=$false;default=[bool]$SigningKey})
    updateCompanion=@{feature='updates';mode='after-exit';runtimeMode='shared';updateMode='files';authApplication='heirowinstaller';credentialOrigin='https://socketjack.com';tokenEnvironmentVariable='HEIROWUPDATER_TOKEN';cookieEnvironmentVariable='HEIROWUPDATER_COOKIE'}
    shortcuts=@(@{name='heirowInstaller';location='startMenu';target='heirowInstaller.exe'})
    registry=@(
        @{hive='user';key='Software\Classes\.hinst';name='';type='string';value='SocketJack.heirowInstaller.Package'},
        @{hive='user';key='Software\Classes\SocketJack.heirowInstaller.Package';name='';type='string';value='heirowInstaller package'},
        @{hive='user';key='Software\Classes\SocketJack.heirowInstaller.Package\DefaultIcon';name='';type='string';value='"{InstallDir}\heirowInstaller.exe",0'},
        @{hive='user';key='Software\Classes\SocketJack.heirowInstaller.Package\shell\open\command';name='';type='string';value='"{InstallDir}\heirowInstaller.exe" "%1"'}
    )
    payload=@(Get-ChildItem -LiteralPath $payloadDirectory -Recurse -File | ForEach-Object {
        $relative=[IO.Path]::GetRelativePath($payloadDirectory,$_.FullName).Replace('\','/')
        $file=[ordered]@{source=$relative;path=$relative}
        if($relative -eq 'heirowUpdater.exe'){$file.feature='updates'}
        $file
    })
}
$manifestPath = Join-Path $OutputDirectory 'installer.json'
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$package = Join-Path $OutputDirectory "heirowInstaller-$Version.hinst"
$setup = Join-Path $OutputDirectory "heirowInstaller-$Version-Setup.exe"
$packArguments = @('pack',$manifestPath,'--source',$payloadDirectory,'--output',$package,'--legal-documents',$LegalDocuments)
if ($SigningKey) { $packArguments += @('--sign-key',[IO.Path]::GetFullPath($SigningKey)) }
& $cli @packArguments
if ($LASTEXITCODE -ne 0) { throw 'Self-package build failed.' }
& $cli bundle $package --launcher $launcher --output $setup
if ($LASTEXITCODE -ne 0) { throw 'Setup bundle build failed.' }
& $cli verify $setup
if ($LASTEXITCODE -ne 0) { throw 'Setup bundle verification failed.' }
# Authenticode must be applied after the payload overlay has been appended.
if ($CertificateThumbprint) {
    & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /tr https://timestamp.digicert.com /td SHA256 $setup
    if ($LASTEXITCODE -ne 0) { throw 'Setup Authenticode signing failed.' }
    & $cli verify $setup
    if ($LASTEXITCODE -ne 0) { throw 'Signed setup payload verification failed.' }
}
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$feed = [ordered]@{productId='heirowinstaller';name='heirowInstaller';publisher='SocketJack';version=$Version;
    installerUrl="https://github.com/hheiroww/heirowInstaller/releases/download/v$Version/$([IO.Path]::GetFileName($setup))";
    sha256=$hash;size=(Get-Item -LiteralPath $setup).Length;packageType='exe';releaseNotes="heirowInstaller $Version";arguments=@('--silent')}
$feed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'update.json') -Encoding utf8NoBOM
$releaseFiles = @($setup,$package,(Join-Path $OutputDirectory 'update.json'))
if ($PreviousPackage) {
    if (!$SigningKey) { throw 'A file update requires the same publisher signing key as both full packages.' }
    $incremental = Join-Path $OutputDirectory "heirowInstaller-$Version.hupd"
    & $cli build-update $package --base ([IO.Path]::GetFullPath($PreviousPackage)) --output $incremental --sign-key ([IO.Path]::GetFullPath($SigningKey))
    if ($LASTEXITCODE -ne 0) { throw 'Signed file-update build failed.' }
    & $cli verify-update $incremental
    if ($LASTEXITCODE -ne 0) { throw 'Signed file-update verification failed.' }
    $releaseFiles += $incremental
}
$releaseFiles | ForEach-Object { "$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))" } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8NoBOM
$receipt = [ordered]@{version=$Version;sourceCommit=$sourceCommit;setup=$setup;package=$package;launcher=$launcher;cli=$cli;updater=$updater;sha256=$hash;bytes=(Get-Item -LiteralPath $setup).Length}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build.json') -Encoding utf8NoBOM
$receipt | ConvertTo-Json
