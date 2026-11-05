#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SigningKey,
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][string]$RuntimePackagePath,
    [Parameter(Mandatory)][uri]$RuntimePackageUrl,
    [ValidatePattern('^\[\d+\.\d+\.\d+\.\d+\]$')][string]$ProjectZPackageVersion = '[2026.11.4.1]',
    [string]$RootPackageCache = $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }),
    [string]$Version,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Shared-NuGetValidation.ps1')
function Copy-ToolkitDependencyNotices([string]$PayloadDirectory, [string]$PackageCache) {
    # Read notices from the exact archives bound by the shipped dependency locks,
    # including locally restored public packages. Never substitute checkout text.
    Initialize-AuthoringNuGet
    $packages = @{}
    foreach ($depsFile in Get-ChildItem -LiteralPath $PayloadDirectory -Filter '*.deps.json' -File) {
        $deps = Get-Content -LiteralPath $depsFile.FullName -Raw | ConvertFrom-Json -AsHashtable
        $target = $deps.targets[$deps.runtimeTarget.name]
        foreach ($identity in $target.Keys) {
            $library = $deps.libraries[$identity]
            if ($library.type -cne 'package') { continue }
            if ($identity -notmatch '^([A-Za-z0-9_.-]+)/([A-Za-z0-9_.+-]+)$') { throw 'Unsafe dependency package identity.' }
            $id = $Matches[1]; $packageVersion = $Matches[2]
            if (!$packages.ContainsKey($identity)) { $packages[$identity] = @{id=$id; version=$packageVersion; hash=$library.sha512; assets=@{}; overrides=@()} }
            $package = $packages[$identity]
            if ($package.hash -cne $library.sha512) { throw 'Maintenance hosts disagree on a package content hash.' }
            foreach ($kind in @('runtime','native','resources','runtimeTargets')) {
                if (!$target[$identity].Contains($kind)) { continue }
                foreach ($asset in $target[$identity][$kind].Keys) {
                    if ($asset.EndsWith('/_._')) { continue }
                    $candidates = @((Join-Path $PayloadDirectory $asset), (Join-Path $PayloadDirectory ([IO.Path]::GetFileName($asset))))
                    if ($kind -eq 'resources') { $candidates += Join-Path $PayloadDirectory ($target[$identity][$kind][$asset].locale+'/'+[IO.Path]::GetFileName($asset)) }
                    foreach ($candidate in $candidates | Select-Object -Unique) {
                        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $package.assets[$asset] = $candidate; break }
                    }
                }
            }
        }
    }
    # Project-Z's build assets replace MonoGame's native runtime with the exact
    # runtime shipped in ProjectZ/tools/native. Bind that known override to its
    # own locked archive rather than accepting a same-name DLL from the cache.
    $projectZ = @($packages.Keys | Where-Object { $packages[$_].id -ieq 'ProjectZ' })
    foreach ($identity in @($packages.Keys)) {
        $package = $packages[$identity]
        if ($package.id -notlike 'MonoGame.Runtime.Windows.*') { continue }
        foreach ($asset in @($package.assets.Keys)) {
            if ($asset -notmatch '^runtimes/(win-(x64|arm64))/native/mgruntime\.dll$' -or $projectZ.Count -ne 1) { continue }
            $sourceEntry = 'tools/native/'+$Matches[1]+'/mgruntime.dll'
            $packages[$projectZ[0]].assets[$sourceEntry] = $package.assets[$asset]
            $package.overrides += [ordered]@{declaredAsset=$asset;sourcePackage=$projectZ[0];sourceEntry=$sourceEntry}
            $package.assets.Remove($asset)
        }
    }
    $inventory = [Collections.Generic.List[object]]::new()
    foreach ($identity in @($packages.Keys | Sort-Object)) {
        $package = $packages[$identity]
        if (!$package.assets.Count -and !$package.overrides.Count) { continue }
        $directory = Join-Path ([IO.Path]::GetFullPath($PackageCache)) ($package.id.ToLowerInvariant()+'/'+$package.version.ToLowerInvariant())
        $archive = Join-Path $directory ($package.id.ToLowerInvariant()+'.'+$package.version.ToLowerInvariant()+'.nupkg')
        $reader = [NuGet.Packaging.PackageArchiveReader]::new($archive)
        try {
            $actual = $reader.GetIdentity()
            $expectedHash = $package.hash -creplace '^sha512-',''
            if (!$expectedHash -or $actual.Id -ine $package.id -or $actual.Version.ToNormalizedString() -cne $package.version -or
                $reader.GetContentHash([Threading.CancellationToken]::None,$null) -cne $expectedHash) { throw 'A notice source archive differs from its shipped dependency lock.' }
            $signature = $reader.GetPrimarySignatureAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
            if ($signature) {
                $signature.SignedCms.CheckSignature($true)
                $null = $reader.ValidateIntegrityAsync($signature.SignatureContent,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
            }
            foreach ($asset in $package.assets.Keys) {
                try { Assert-VerifiedNuGetAsset $reader $asset $package.assets[$asset] }
                catch { throw ('Shipped notice asset does not match '+$identity+' '+$asset+': '+$_.Exception.Message) }
            }
            $nuspec = $reader.NuspecReader
            $license = $nuspec.GetLicenseMetadata()
            $declaredLicense = if ($license) { $license.License } else { $null }
            $root = Join-Path $PayloadDirectory ('ThirdParty/'+$package.id+'/'+$package.version)
            $copied = [Collections.Generic.List[string]]::new()
            foreach ($entry in @($reader.GetFiles())) {
                $leaf = [IO.Path]::GetFileName($entry)
                if ($leaf -notmatch '(?i)^(license|licence|copying|copyright|notice|third.party|ofl)([._-]|$)' -and
                    !$entry.EndsWith('.nuspec',[StringComparison]::OrdinalIgnoreCase) -and $entry -cne $declaredLicense) { continue }
                if ([IO.Path]::IsPathRooted($entry) -or $entry.Contains('\') -or @($entry.Split('/') | Where-Object { $_ -in @('','.','..') }).Count) { throw 'Unsafe package notice path.' }
                $stream = $reader.GetStream($entry)
                try {
                    $destination = Join-Path $root $entry
                    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
                    $file = [IO.File]::Create($destination)
                    try {
                        $buffer = [byte[]]::new(16384); $total = 0L
                        while (($read = $stream.Read($buffer,0,$buffer.Length)) -gt 0) {
                            $total += $read
                            if ($total -gt 2MB) { throw 'Package notice exceeds the bounded text size.' }
                            $file.Write($buffer,0,$read)
                        }
                    } finally { $file.Dispose() }
                } finally { $stream.Dispose() }
                $relative = [IO.Path]::GetRelativePath($PayloadDirectory,$destination).Replace('\','/')
                $copied.Add($relative)
                if ($package.id -ieq 'ProjectZ' -and $entry -ceq 'THIRD-PARTY-NOTICES.md') { Copy-Item -LiteralPath $destination -Destination (Join-Path $PayloadDirectory 'ThirdParty/ProjectZ-NOTICES.md') }
                if ($package.id -ieq 'SocketJack' -and $entry -ceq $declaredLicense -and $license.Type.ToString() -eq 'File') { Copy-Item -LiteralPath $destination -Destination (Join-Path $PayloadDirectory 'ThirdParty/SocketJack-LICENSE.txt') }
            }
            $inventory.Add([ordered]@{id=$package.id;version=$package.version;contentHash=$package.hash;licenseType=$(if($license){$license.Type.ToString()}else{$null});license=$declaredLicense;licenseUrl=$nuspec.GetLicenseUrl();shippedAssets=@($package.assets.Keys|Sort-Object);verifiedOverrides=$package.overrides;noticeFiles=$copied.ToArray()})
        } finally { $reader.Dispose() }
    }
    $inventory.ToArray() | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $PayloadDirectory 'ThirdParty/dependencies.json') -Encoding utf8NoBOM
}
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
Copy-ToolkitDependencyNotices $payload $RootPackageCache
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
