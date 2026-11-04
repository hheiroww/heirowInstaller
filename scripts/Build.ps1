#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$Version,
    [string]$OutputDirectory,
    [string]$BuildDirectory,
    [switch]$BuildOnly,
    [switch]$UseRawBuild,
    [string]$SigningKey,
    [string]$LegalDocuments,
    [string]$CertificateThumbprint,
    [ValidateSet('none','certificate','microsoft')][string]$WindowsSigning = 'none',
    [switch]$MachineCertificateStore,
    [string]$SignTool,
    [string]$ArtifactSigningDlib,
    [string]$ArtifactEndpoint,
    [string]$ArtifactAccount,
    [string]$ArtifactProfile,
    [string]$TimestampUrl,
    [string]$PackageCache,
    [string]$ProjectZVsix,
    [string[]]$PreviousPackage,
    [switch]$FirstRelease,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
if ($CertificateThumbprint -and $WindowsSigning -eq 'none') { $WindowsSigning = 'certificate' }
$windowsSigningArguments = @()
if ($WindowsSigning -eq 'none') {
    if ($MachineCertificateStore -or $SignTool -or $ArtifactSigningDlib -or $ArtifactEndpoint -or $ArtifactAccount -or $ArtifactProfile -or $TimestampUrl) {
        throw 'Select -WindowsSigning certificate or microsoft when supplying Windows signing options.'
    }
} else {
    $windowsSigningArguments = @('--windows-signing',$WindowsSigning)
    if ($WindowsSigning -eq 'certificate') {
        if (-not $CertificateThumbprint) { throw '-WindowsSigning certificate requires -CertificateThumbprint.' }
        if ($ArtifactSigningDlib -or $ArtifactEndpoint -or $ArtifactAccount -or $ArtifactProfile) { throw 'Microsoft Artifact Signing options require -WindowsSigning microsoft.' }
        $windowsSigningArguments += @('--certificate-thumbprint',$CertificateThumbprint)
        if ($MachineCertificateStore) { $windowsSigningArguments += '--machine-certificate-store' }
    } else {
        if ($CertificateThumbprint -or $MachineCertificateStore) { throw 'Certificate-store options cannot be combined with -WindowsSigning microsoft.' }
        if (-not $ArtifactEndpoint -or -not $ArtifactAccount -or -not $ArtifactProfile) { throw '-WindowsSigning microsoft requires -ArtifactEndpoint, -ArtifactAccount and -ArtifactProfile.' }
        $windowsSigningArguments += @('--artifact-endpoint',$ArtifactEndpoint,'--artifact-account',$ArtifactAccount,'--artifact-profile',$ArtifactProfile)
        if ($ArtifactSigningDlib) { $windowsSigningArguments += @('--artifact-signing-dlib',$ArtifactSigningDlib) }
    }
    if ($SignTool) { $windowsSigningArguments += @('--signtool',$SignTool) }
    if ($TimestampUrl) { $windowsSigningArguments += @('--timestamp-url',$TimestampUrl) }
}
if ($SigningKey -and -not $PreviousPackage -and -not $FirstRelease) { throw 'Signed releases require -PreviousPackage so existing installations receive a signed file update. Use -FirstRelease only for an initial release with no installed native baseline.' }
if ($FirstRelease -and $PreviousPackage) { throw '-FirstRelease cannot be combined with -PreviousPackage.' }
if ($PreviousPackage -and -not $SigningKey) { throw 'A file update requires the same publisher signing key as both full packages.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $LegalDocuments) { $LegalDocuments = Join-Path $projectRoot 'legal/heirowinstaller/legal-documents.json' }
$LegalDocuments = [IO.Path]::GetFullPath($LegalDocuments)
if (-not (Test-Path -LiteralPath $LegalDocuments -PathType Leaf)) { throw 'Supply the actual heirowInstaller Privacy Policy and Terms and Conditions as legal document JSON.' }
$sourceCommit = (& git -C $projectRoot rev-parse --verify HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Build from the committed heirowInstaller repository so release provenance can be recorded.' }
if ($SigningKey) {
    & git -C $projectRoot diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'Commit and review tracked source changes before building signed release provenance.' }
    $untrackedSource = @(& git -C $projectRoot ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $untrackedSource.Count) { throw 'Review and synchronize untracked source before building a signed release.' }
}
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts/release/$Version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (!$BuildOnly) {
    $existingReleaseFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object {
        $_.Name -in @('installer.json','build.json','update.json','SHA256SUMS.txt',"heirowInstaller-$Version.hinst","heirowInstaller-$Version-Setup.exe") -or
        $_.Name -like "heirowInstaller-*-to-$Version.hupd"
    })
    if ($existingReleaseFiles.Count) { throw 'Release outputs already exist; preserve them and select a fresh OutputDirectory before packaging.' }
}
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'Test.ps1') -Configuration $Configuration -PackageCache $PackageCache }
if ($BuildDirectory) { $buildDirectory = [IO.Path]::GetFullPath($BuildDirectory) }
else { $buildDirectory = Join-Path $OutputDirectory ('.build/' + [Guid]::NewGuid().ToString('N')) }
$appDirectory = Join-Path $buildDirectory 'app'
$cliDirectory = Join-Path $buildDirectory 'cli'
$updaterDirectory = Join-Path $buildDirectory 'updater'
$dx11Directory = Join-Path $buildDirectory 'dx11'
# Compiler caches can be reused; published inputs must begin empty so a removed
# file cannot survive into the next signed manifest or isolated signing tool.
foreach ($publishedOutput in @($appDirectory,$cliDirectory,$updaterDirectory,$dx11Directory,(Join-Path $buildDirectory 'payload'),(Join-Path $buildDirectory 'signing-tool'))) {
    if (!$UseRawBuild -and (Test-Path -LiteralPath $publishedOutput) -and @(Get-ChildItem -LiteralPath $publishedOutput -Force).Count) {
        throw "Published build output already exists; preserve it and select a fresh BuildDirectory: $publishedOutput"
    }
}
$rawReceiptPath = Join-Path $buildDirectory 'raw-build.json'
if (!$UseRawBuild) {
$cliArguments = @('publish',(Join-Path $projectRoot 'src/HeirowInstaller.Cli/HeirowInstaller.Cli.csproj'),'-c',$Configuration,'-r',$Runtime,'--self-contained','true',
    '--artifacts-path',(Join-Path $buildDirectory 'work/cli'),
    '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:IncludeAllContentForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true','-p:CopyOutputSymbolsToPublishDirectory=false','-p:DebugType=embedded',"-p:Version=$Version",'-o',$cliDirectory,'--nologo',
    "-p:SourceRevisionId=$sourceCommit", "-p:RepositoryCommit=$sourceCommit",'-m:1','-nodeReuse:false','-p:UseSharedCompilation=false')
if ($PackageCache) { $cliArguments += "-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))" }
Invoke-DotNet -Arguments $cliArguments
$cli = Join-Path $cliDirectory 'heirowInstaller.Cli.exe'
if ($windowsSigningArguments.Count) {
    $signingCliDirectory = Join-Path $buildDirectory 'signing-tool'
    Copy-Item -LiteralPath $cliDirectory -Destination $signingCliDirectory -Recurse
    $signingCli = Join-Path $signingCliDirectory 'heirowInstaller.Cli.exe'
}
$dx11Runtime = & (Join-Path $PSScriptRoot 'Resolve-ProjectZDx11Runtime.ps1') -OutputDirectory (Join-Path $buildDirectory 'dependencies') -Vsix $ProjectZVsix
$dx11Arguments = @('publish',(Join-Path $projectRoot 'src/HeirowInstaller.App/HeirowInstaller.App.csproj'),'-c',$Configuration,'-r',$Runtime,'--self-contained','true',
    '--artifacts-path',(Join-Path $buildDirectory 'work/dx11'),
    '-p:InstallerGraphicsBackend=DirectX11',"-p:InstallerProjectZDx11RuntimePath=$($dx11Runtime.path)",
    '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:IncludeAllContentForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true','-p:CopyOutputSymbolsToPublishDirectory=false','-p:DebugType=embedded',"-p:Version=$Version",'-o',$dx11Directory,'--nologo',
    "-p:SourceRevisionId=$sourceCommit", "-p:RepositoryCommit=$sourceCommit",'-m:1','-nodeReuse:false','-p:UseSharedCompilation=false')
if ($PackageCache) { $dx11Arguments += "-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))" }
Invoke-DotNet -Arguments $dx11Arguments
$dx11Executable = Join-Path $dx11Directory 'heirowInstaller.exe'
& (Join-Path $PSScriptRoot 'Test-RuntimeSize.ps1') -Project (Join-Path $projectRoot 'src/HeirowInstaller.App/HeirowInstaller.App.csproj') -PublishedExecutable $dx11Executable -AssetsPath (Join-Path $buildDirectory 'work/dx11/obj/HeirowInstaller.App/project.assets.json')
if ($windowsSigningArguments.Count) {
    & $signingCli sign $dx11Executable @windowsSigningArguments
    if ($LASTEXITCODE -ne 0) { throw 'DX11 fallback Windows code signing failed.' }
}
foreach ($project in @(@('src/HeirowInstaller.App/HeirowInstaller.App.csproj',$appDirectory),@('src/HeirowUpdater/HeirowUpdater.csproj',$updaterDirectory))) {
    $backendWork = if ($project[0] -like '*HeirowInstaller.App*') { 'native' } else { 'updater' }
    $publishArguments = @('publish',(Join-Path $projectRoot $project[0]),'-c',$Configuration,'-r',$Runtime,'--self-contained','true',
        '--artifacts-path',(Join-Path $buildDirectory ('work/' + $backendWork)),
        '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:IncludeAllContentForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true','-p:CopyOutputSymbolsToPublishDirectory=false','-p:DebugType=embedded',"-p:Version=$Version",'-o',$project[1],'--nologo',
        '-m:1','-nodeReuse:false','-p:UseSharedCompilation=false')
    $publishArguments += @("-p:SourceRevisionId=$sourceCommit", "-p:RepositoryCommit=$sourceCommit")
    if ($project[0] -eq 'src/HeirowInstaller.App/HeirowInstaller.App.csproj') {
        $publishArguments += @("-p:InstallerDx11FallbackExecutable=$dx11Executable",'-p:InstallerRequireDx11Fallback=true')
    }
    if ($PackageCache) { $publishArguments += "-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageCache))" }
    Invoke-DotNet -Arguments $publishArguments
    if ($project[0] -in @('src/HeirowInstaller.App/HeirowInstaller.App.csproj','src/HeirowUpdater/HeirowUpdater.csproj')) {
        $executableName = if ($project[0] -like '*HeirowInstaller.App*') { 'heirowInstaller.exe' } else { 'heirowUpdater.exe' }
        $maximumBytes = if ($project[0] -like '*HeirowInstaller.App*') { 90MB + (Get-Item -LiteralPath $dx11Executable).Length } else { 90MB }
        $projectName = [IO.Path]::GetFileNameWithoutExtension($project[0])
        & (Join-Path $PSScriptRoot 'Test-RuntimeSize.ps1') -Project (Join-Path $projectRoot $project[0]) -PublishedExecutable (Join-Path $project[1] $executableName) -MaximumBytes $maximumBytes -AssetsPath (Join-Path $buildDirectory ('work/' + $backendWork + '/obj/' + $projectName + '/project.assets.json'))
    }
}
$launcher = Join-Path $appDirectory 'heirowInstaller.exe'
$cli = Join-Path $cliDirectory 'heirowInstaller.Cli.exe'
$updater = Join-Path $updaterDirectory 'heirowUpdater.exe'
if (-not (Test-Path -LiteralPath $launcher) -or -not (Test-Path -LiteralPath $cli) -or -not (Test-Path -LiteralPath $updater)) { throw 'Published single-file executables are missing.' }
if ($windowsSigningArguments.Count) {
    # The isolated signing tool was prepared before signing/embedding the DX11 host.
    foreach ($binary in @($launcher,$cli,$updater)) {
        & $signingCli sign $binary @windowsSigningArguments
        if ($LASTEXITCODE -ne 0) { throw 'Windows code signing failed for a published executable.' }
    }
}
$rawFiles = @(foreach ($directory in @($appDirectory,$cliDirectory,$updaterDirectory,$dx11Directory)) {
    Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object {
        [ordered]@{path=$_.FullName;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
})
[ordered]@{version=$Version;sourceCommit=$sourceCommit;configuration=$Configuration;runtime=$Runtime;windowsSigning=$WindowsSigning;windowsSigningArguments=$windowsSigningArguments;dx11Runtime=$dx11Runtime;files=$rawFiles} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $rawReceiptPath -Encoding utf8NoBOM
} else {
    $rawBuild = Get-Content -LiteralPath $rawReceiptPath -Raw | ConvertFrom-Json
    if ($rawBuild.version -cne $Version -or $rawBuild.sourceCommit -cne $sourceCommit -or $rawBuild.configuration -cne $Configuration -or
        $rawBuild.runtime -cne $Runtime -or $rawBuild.windowsSigning -cne $WindowsSigning -or
        ![Linq.Enumerable]::SequenceEqual([string[]]@($rawBuild.windowsSigningArguments),[string[]]$windowsSigningArguments)) { throw 'The retained raw build has different source, version, runtime, configuration or signing policy.' }
    $actualPaths = @(foreach ($directory in @($appDirectory,$cliDirectory,$updaterDirectory,$dx11Directory)) { Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object FullName })
    if (@($rawBuild.files).Count -lt 4 -or @(Compare-Object @($rawBuild.files.path) $actualPaths).Count) { throw 'The retained raw build file inventory changed.' }
    foreach ($file in $rawBuild.files) {
        $actual = Get-Item -LiteralPath $file.path
        if ($actual.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $actual.FullName -Algorithm SHA256).Hash -ine $file.sha256) { throw 'A retained raw build file changed.' }
    }
    $reusedVsix = if ($ProjectZVsix) { $ProjectZVsix } else { [string]$rawBuild.dx11Runtime.vsix }
    $dx11Runtime = & (Join-Path $PSScriptRoot 'Resolve-ProjectZDx11Runtime.ps1') -OutputDirectory (Join-Path $buildDirectory 'dependencies') -Vsix $reusedVsix
    if ($rawBuild.dx11Runtime.bytes -ne $dx11Runtime.bytes -or $rawBuild.dx11Runtime.sha256 -ine $dx11Runtime.sha256 -or
        $rawBuild.dx11Runtime.version -cne $dx11Runtime.version -or $rawBuild.dx11Runtime.sourceCommit -cne $dx11Runtime.sourceCommit -or
        ![IO.Path]::GetFullPath([string]$rawBuild.dx11Runtime.path).Equals($dx11Runtime.path,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'The retained raw DX11 provenance differs from the reviewed official runtime.'
    }
    $dx11Executable = Join-Path $dx11Directory 'heirowInstaller.exe'
    $launcher = Join-Path $appDirectory 'heirowInstaller.exe'
    $cli = Join-Path $cliDirectory 'heirowInstaller.Cli.exe'
    $updater = Join-Path $updaterDirectory 'heirowUpdater.exe'
    # Post-package signing changes only Setup, so the verified raw CLI can sign
    # it directly. Never execute an unverified retained signing-tool copy.
    if ($windowsSigningArguments.Count) { $signingCli = $cli }
}
if ($BuildOnly) { Get-Content -LiteralPath $rawReceiptPath -Raw; return }
$payloadDirectory = Join-Path $buildDirectory 'payload'
if ((Test-Path -LiteralPath $payloadDirectory) -and @(Get-ChildItem -LiteralPath $payloadDirectory -Force).Count) { throw 'The packaging payload already exists; preserve it rather than adding stale files to a new manifest.' }
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
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses/SharpDX-LICENSE.txt') -Destination (Join-Path $noticesDirectory 'SharpDX-LICENSE.txt')
$projectZVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.ProjectZPackageVersion.InnerText.Trim([char[]]'[]')
foreach ($notice in @(@("projectz/$projectZVersion/THIRD-PARTY-NOTICES.md",'ProjectZ-NOTICES.md'),@('socketjack/2026.10.24/License.txt','SocketJack-LICENSE.txt'))) {
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
if ($windowsSigningArguments.Count) {
    & $signingCli sign $setup @windowsSigningArguments
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
foreach ($baseline in $PreviousPackage) {
    $baselineMetadata = (& $cli inspect ([IO.Path]::GetFullPath($baseline)) --require-signature | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or !$baselineMetadata.signed -or $baselineMetadata.manifest.id -ne 'heirowinstaller') { throw 'Every update baseline must be a verified signed heirowInstaller package.' }
    $baseVersion = [string]$baselineMetadata.manifest.version
    if ($baseVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]{0,127}$') { throw 'The update baseline has an unsafe version.' }
    $incremental = Join-Path $OutputDirectory "heirowInstaller-$baseVersion-to-$Version.hupd"
    if (Test-Path -LiteralPath $incremental) { throw 'Use a fresh release directory and unique baseline versions.' }
    & $cli build-update $package --base ([IO.Path]::GetFullPath($baseline)) --output $incremental --sign-key ([IO.Path]::GetFullPath($SigningKey))
    if ($LASTEXITCODE -ne 0) { throw 'Signed file-update build failed.' }
    & $cli verify-update $incremental
    if ($LASTEXITCODE -ne 0) { throw 'Signed file-update verification failed.' }
    $releaseFiles += $incremental
}
$releaseFiles | ForEach-Object { "$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))" } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8NoBOM
$receipt = [ordered]@{version=$Version;sourceCommit=$sourceCommit;setup=$setup;package=$package;launcher=$launcher;cli=$cli;updater=$updater;sha256=$hash;bytes=(Get-Item -LiteralPath $setup).Length;updates=@($releaseFiles | Where-Object { [IO.Path]::GetExtension($_) -eq '.hupd' });dx11Fallback=@{path=$dx11Executable;bytes=(Get-Item -LiteralPath $dx11Executable).Length;sha256=(Get-FileHash -LiteralPath $dx11Executable -Algorithm SHA256).Hash.ToLowerInvariant();projectZVersion=$dx11Runtime.version;projectZRuntimeSha256=$dx11Runtime.sha256;projectZSourceCommit=$dx11Runtime.sourceCommit}}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build.json') -Encoding utf8NoBOM
$receipt | ConvertTo-Json
