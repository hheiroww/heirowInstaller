#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime,
    [string]$Version,
    [string]$OutputDirectory,
    [string]$SigningKey,
    [string]$UpdateUrl,
    [string]$ProjectZPackageVersion,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Runtime) { $Runtime = if ($IsMacOS) { 'osx-' } elseif ($IsLinux) { 'linux-' } else { throw 'Run portable archive creation on Linux or macOS.' }; $Runtime += [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() }
$platform = if ($Runtime.StartsWith('osx-')) { 'macos' } else { 'linux' }
if (($platform -eq 'macos' -and -not $IsMacOS) -or ($platform -eq 'linux' -and -not $IsLinux)) { throw 'Create each archive on its target operating system so executable modes and native app signing are preserved.' }
if ($UpdateUrl -and -not $SigningKey) { throw 'Automatic application updates require a publisher signing key.' }
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts/portable/$Version/$Runtime" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$build = Join-Path $OutputDirectory ('.build/' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $build 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed." }
}
if (-not $SkipTests) { Invoke-DotNet -Arguments @('run','--project',(Join-Path $projectRoot 'tests/HeirowInstaller.CrossPlatform.Tests'),'-c','Release') }
foreach ($project in @('HeirowInstaller.App','HeirowInstaller.Cli','HeirowUpdater')) {
    $published = Join-Path $build $project
    $arguments = @('publish',(Join-Path $projectRoot "src/$project/$project.csproj"),'-c','Release','-r',$Runtime,'--self-contained','true',
        '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:IncludeAllContentForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true','-p:CopyOutputSymbolsToPublishDirectory=false',"-p:Version=$Version",'-o',$published,'--nologo')
    if ($ProjectZPackageVersion) { $arguments += "-p:ProjectZPackageVersion=$ProjectZPackageVersion" }
    Invoke-DotNet $arguments
    foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
        if ($file.Extension -in @('.pdb','.xml')) { continue }
        $relative = [IO.Path]::GetRelativePath($published,$file.FullName)
        $destination = Join-Path $payload $relative
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Conflicting published file: $relative" }
        } else {
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}
foreach ($document in @('LICENSE','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $payload }
foreach ($directory in @('publishers','legal')) { Copy-Item -LiteralPath (Join-Path $projectRoot $directory) -Destination $payload -Recurse }
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$projectZVersion = if ($ProjectZPackageVersion) { $ProjectZPackageVersion.Trim('[',']') } else { ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.ProjectZPackageVersion.'#text'.Trim('[',']') }
$packageId = if ($platform -eq 'linux') { 'projectz.linux' } else { 'projectz.macos' }
$notices = Join-Path $payload 'ThirdParty'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/HeirowInstaller.App/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$socketJack = @($assets.libraries.Keys | Where-Object { $_ -match '^SocketJack/' -and $assets.libraries[$_].type -eq 'package' })
if ($socketJack.Count -ne 1) { throw 'The restored application must identify exactly one SocketJack package for its license notice.' }
$socketJackPath = $assets.libraries[$socketJack[0]].path
foreach ($item in @(@("$packageId/$projectZVersion/THIRD-PARTY-NOTICES.md",'ProjectZ-NOTICES.md'),@("$socketJackPath/License.txt",'SocketJack-LICENSE.txt'))) {
    Copy-Item -LiteralPath (Join-Path $cache $item[0]) -Destination (Join-Path $notices $item[1])
}
$manifest = [ordered]@{
    formatVersion=4; id='heirowinstaller'; name='heirowInstaller'; version=$Version; publisher='SocketJack'
    description='Cross-platform installers, signed updates, and publishing built with Project-Z.'
    targetPlatform=$platform; targetRuntime=$Runtime; entryPoint='heirowInstaller'; scope='user'
    homepage='https://socketjack.com/heirowInstaller/'; licenseFile='LICENSE'
    shortcuts=@(@{name='heirowInstaller';location='startMenu';target='heirowInstaller'})
    payload=@(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/')
        $file = [ordered]@{source=$relative;path=$relative;unixMode=([int][IO.File]::GetUnixFileMode($_.FullName) -band 493)}
        if ($relative -in @('heirowInstaller','heirowInstaller.Cli','heirowUpdater')) { $file.unixMode=493 }
        if ($UpdateUrl -and $relative -eq 'heirowUpdater') { $file.feature='updates' }
        $file
    })
}
if ($UpdateUrl) {
    $manifest.updateUrl=$UpdateUrl
    $manifest.features=@(@{id='updates';name='Application updates';required=$false;default=$true})
    $manifest.updateCompanion=@{feature='updates';executablePath='heirowUpdater';mode='after-exit';runtimeMode='shared';updateMode='files';credentialOrigin='https://socketjack.com'}
}
$manifestPath = Join-Path $build 'installer.json'
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$package = Join-Path $OutputDirectory "heirowInstaller-$Version-$Runtime.hinst"
$pack = @('run','--project',(Join-Path $projectRoot 'src/HeirowInstaller.Cli'),'-c','Release','--','pack',$manifestPath,'--source',$payload,'--output',$package,
    '--legal-documents',(Join-Path $projectRoot 'legal/heirowinstaller/legal-documents.json'))
if ($SigningKey) { $pack += @('--sign-key',[IO.Path]::GetFullPath($SigningKey)) }
Invoke-DotNet $pack
$distribution = Join-Path $build "heirowInstaller-$Runtime"
if ($platform -eq 'macos') {
    $app = Join-Path $distribution 'heirowInstaller Setup.app/Contents'
    New-Item -ItemType Directory -Path (Join-Path $app 'MacOS'),(Join-Path $app 'Resources') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'heirowInstaller') -Destination (Join-Path $app 'MacOS/heirowInstaller')
    Copy-Item -LiteralPath $package -Destination (Join-Path $app 'Resources/Application.hinst')
    $bundleVersion = (($Version -split '[+\-]')[0] -split '\.')[0..2] -join '.'
    $plist = '<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>CFBundleExecutable</key><string>heirowInstaller</string><key>CFBundleIdentifier</key><string>com.socketjack.heirowinstaller.setup</string><key>CFBundleName</key><string>heirowInstaller Setup</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleVersion</key><string>' + $bundleVersion + '</string><key>NSHighResolutionCapable</key><true/></dict></plist>'
    [IO.File]::WriteAllText((Join-Path $app 'Info.plist'),$plist)
    & /usr/bin/codesign --force --sign - (Split-Path $app -Parent)
    if ($LASTEXITCODE -ne 0) { throw 'Ad-hoc application signing failed.' }
} else {
    New-Item -ItemType Directory -Path $distribution -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'heirowInstaller') -Destination (Join-Path $distribution 'heirowInstaller-Setup')
    Copy-Item -LiteralPath $package -Destination (Join-Path $distribution 'Application.hinst')
}
Copy-Item -LiteralPath (Join-Path $payload 'heirowInstaller.Cli') -Destination $distribution
foreach ($document in @('LICENSE','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $payload $document) -Destination $distribution }
Copy-Item -LiteralPath $notices -Destination $distribution -Recurse
$archive = Join-Path $OutputDirectory "heirowInstaller-$Version-$Runtime.tar.gz"
& tar -czf $archive -C $build ([IO.Path]::GetFileName($distribution))
if ($LASTEXITCODE -ne 0) { throw 'Portable archive creation failed.' }
@($package,$archive) | ForEach-Object { "$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))" } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8NoBOM
Write-Output "Built $Runtime package and setup archive in $OutputDirectory"
