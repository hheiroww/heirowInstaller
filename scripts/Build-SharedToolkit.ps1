#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SigningKey,
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][string]$RuntimePackagePath,
    [Parameter(Mandatory)][uri]$RuntimePackageUrl,
    [ValidatePattern('^\[\d+\.\d+\.\d+\.\d+\]$')][string]$ProjectZPackageVersion = '[2026.11.4.1]',
    [string]$Version,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version.InnerText }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($RuntimePackageUrl.Scheme -ne 'https' -or $RuntimePackageUrl.UserInfo -or $RuntimePackageUrl.Fragment) { throw 'The canonical runtime URL must use HTTPS.' }
& $CliPath verify $RuntimePackagePath --require-signature | Out-Null
if ($LASTEXITCODE) { throw 'Canonical runtime verification failed.' }
$runtimeInspection = & $CliPath inspect $RuntimePackagePath | ConvertFrom-Json
if ($LASTEXITCODE -or !$runtimeInspection.signed -or $runtimeInspection.manifest.id -cne 'socketjack.dotnet.desktop.10' -or $runtimeInspection.manifest.targetRuntime -cne $Runtime) { throw 'Supply the canonical signed .NET 10 Windows Desktop archive for this architecture.' }
$runtimeVersion = $runtimeInspection.manifest.version
foreach ($required in @('dotnet.exe',"host/fxr/$runtimeVersion/hostfxr.dll","shared/Microsoft.NETCore.App/$runtimeVersion/coreclr.dll","shared/Microsoft.WindowsDesktop.App/$runtimeVersion/PresentationFramework.dll")) {
    if ($required -cnotin $runtimeInspection.manifest.payload.path) { throw 'The canonical runtime archive lacks the standard desktop runtime layout.' }
}
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'Use an empty task-owned toolkit output directory.' }
if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace -lt 3GB) { throw 'Shared toolkit publication needs at least 3 GiB of staging space.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$work = Join-Path $output '.build'
$payload = Join-Path $work 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
# Hosts retain their native apphosts and framework-dependent configuration. The
# canonical runtime is a separate signed archive reused by every compatible app.
foreach ($project in @('HeirowInstaller.App','HeirowUpdater','HeirowInstaller.Cli')) {
    $published = Join-Path $work $project
    & dotnet publish (Join-Path $projectRoot "src/$project/$project.csproj") -c Release -r $Runtime --self-contained false `
        --artifacts-path (Join-Path $work 'compiler') -o $published --nologo '-p:PublishSingleFile=false' "-p:RuntimeFrameworkVersion=$runtimeVersion" '-p:UseAppHost=true' '-p:IncludeAllContentForSelfExtract=false' `
        '-p:InstallerSharedRuntimePublish=true' "-p:ProjectZPackageVersion=$ProjectZPackageVersion" "-p:Version=$Version" '-p:CopyOutputSymbolsToPublishDirectory=false' '-p:DebugType=embedded'
    if ($LASTEXITCODE) { throw "Shared toolkit host publish failed: $project" }
    foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
        if ($file.Extension -in @('.pdb','.xml') -or $file.Name.EndsWith('.runtimeconfig.dev.json')) { continue }
        $relative = [IO.Path]::GetRelativePath($published,$file.FullName)
        $destination = Join-Path $payload $relative
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) { throw "Shared hosts have incompatible output bytes: $relative" }
        } else {
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}
foreach ($name in @('heirowInstaller','heirowUpdater','heirowInstaller.Cli')) {
    foreach ($extension in @('.exe','.dll','.deps.json','.runtimeconfig.json')) { if (!(Test-Path -LiteralPath (Join-Path $payload ($name+$extension)))) { throw "Incomplete framework-dependent maintenance host: $name$extension" } }
    $configuration = Get-Content -LiteralPath (Join-Path $payload ($name+'.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if ($configuration.runtimeOptions.includedFrameworks -or (!$configuration.runtimeOptions.framework -and !$configuration.runtimeOptions.frameworks)) { throw 'Maintenance hosts must remain framework-dependent.' }
}
foreach ($forbidden in @('dotnet.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll')) {
    if (Test-Path -LiteralPath (Join-Path $payload $forbidden)) { throw "A private .NET runtime was duplicated inside maintenance: $forbidden" }
}
foreach ($notice in @('LICENSE','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $notice) -Destination (Join-Path $payload $notice) }
$legal = (Get-Content -LiteralPath (Join-Path $projectRoot 'legal/heirowinstaller/legal-documents.json') -Raw | ConvertFrom-Json).legalDocuments
$manifest = [ordered]@{formatVersion=4;id='socketjack.maintenance';name='SocketJack installer and updater runtime';version=$Version;publisher='SocketJack';targetPlatform='windows';targetRuntime=$Runtime;scope='user';payload=@();legalDocuments=$legal}
$manifestPath = Join-Path $work 'maintenance.json'
$manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $manifestPath
$packagePath = Join-Path $output "socketjack.maintenance-$Version-$Runtime.hinst"
& $CliPath pack $manifestPath --source $payload --output $packagePath --sign-key $SigningKey
if ($LASTEXITCODE) { throw 'Shared toolkit signing failed.' }
& $CliPath verify $packagePath --require-signature --trust-key $runtimeInspection.publisherKeySha256
if ($LASTEXITCODE) { throw 'Shared toolkit verification failed.' }
[ordered]@{id='socketjack.maintenance';version=$Version;targetRuntime=$Runtime;package=$packagePath;bytes=(Get-Item -LiteralPath $packagePath).Length;sha256=(Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash;publisherKeySha256=$runtimeInspection.publisherKeySha256;projectZPackageVersion=$ProjectZPackageVersion;payloadBytes=(Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object -Property Length -Sum).Sum;runtimePackagePath=[IO.Path]::GetFullPath($RuntimePackagePath);runtimePackageUrl=$RuntimePackageUrl.AbsoluteUri;runtimeVersion=$runtimeVersion;runtimePackageSha256=(Get-FileHash -LiteralPath $RuntimePackagePath -Algorithm SHA256).Hash;staging=$work;cleanupRequiresApproval=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'shared-toolkit.json')
