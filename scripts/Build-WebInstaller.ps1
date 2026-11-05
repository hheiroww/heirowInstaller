#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$SigningKey,
    [Parameter(Mandatory)][string]$GenericBootstrapPath,
    [Parameter(Mandatory)][string]$MaintenancePackagePath,
    [Parameter(Mandatory)][uri]$MaintenancePackageUrl,
    [Parameter(Mandatory)][string]$RuntimePackagePath,
    [Parameter(Mandatory)][uri]$RuntimePackageUrl,
    [Parameter(Mandatory)][uri]$ApplicationPackageUrl,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$CliPath,
    [string[]]$AllowedDownloadOrigins = @(),
    [switch]$RequiresAdministrator
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'Use an empty task-owned web-installer output directory.' }
foreach ($url in @($MaintenancePackageUrl,$RuntimePackageUrl,$ApplicationPackageUrl)) { if ($url.Scheme -ne 'https' -or $url.UserInfo -or $url.Fragment) { throw 'Release URLs must use HTTPS without user information or fragments.' } }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$buildRoot = Join-Path $output '.compiler'
function Invoke-InstallerCli([string[]]$Arguments) {
    if ($CliPath) { & $CliPath @Arguments } else { & dotnet run --project (Join-Path $projectRoot 'src/HeirowInstaller.Cli/HeirowInstaller.Cli.csproj') -c Release --artifacts-path $buildRoot -- @Arguments }
    if ($LASTEXITCODE) { throw "Installer authoring command failed: $($Arguments[0])" }
}
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.targetRuntime -ne $Runtime -or $manifest.legacyMsi -or $manifest.legacyExecutable) { throw 'Web installers require a native payload authored for the exact selected runtime.' }
if (!$manifest.entryPoint) { throw 'The application entry point is required.' }
$application = Join-Path $output ($manifest.id + '-' + $manifest.version + '.hinst')
Invoke-InstallerCli -Arguments @('pack',$ManifestPath,'--source',$SourceDirectory,'--output',$application,'--sign-key',$SigningKey) | Out-Null
if ((Get-Item -LiteralPath $application).Length -ge 100000000) { throw 'The compressed application payload must be smaller than 100,000,000 bytes. Split declared dependencies before building.' }
$applicationInspection = Invoke-InstallerCli -Arguments @('inspect',$application) | ConvertFrom-Json
$maintenanceInspection = Invoke-InstallerCli -Arguments @('inspect',$MaintenancePackagePath) | ConvertFrom-Json
if (!$maintenanceInspection.signed -or $maintenanceInspection.publisherKeySha256 -ine $applicationInspection.publisherKeySha256 -or $maintenanceInspection.manifest.targetRuntime -ne $Runtime) { throw 'Maintenance runtime must have the same verified publisher and target runtime as the application.' }
Invoke-InstallerCli -Arguments @('verify',$MaintenancePackagePath,'--require-signature','--trust-key',$applicationInspection.publisherKeySha256) | Out-Null
$runtimeInspection = Invoke-InstallerCli -Arguments @('inspect',$RuntimePackagePath) | ConvertFrom-Json
if (!$runtimeInspection.signed -or $runtimeInspection.publisherKeySha256 -ine $applicationInspection.publisherKeySha256 -or $runtimeInspection.manifest.id -cne 'socketjack.dotnet.desktop.10' -or $runtimeInspection.manifest.targetRuntime -cne $Runtime) { throw 'The maintenance host requires the same-publisher canonical .NET 10 Desktop archive.' }
Invoke-InstallerCli -Arguments @('verify',$RuntimePackagePath,'--require-signature','--trust-key',$applicationInspection.publisherKeySha256) | Out-Null
foreach ($required in @('dotnet.exe',('host/fxr/'+$runtimeInspection.manifest.version+'/hostfxr.dll'),('shared/Microsoft.NETCore.App/'+$runtimeInspection.manifest.version+'/coreclr.dll'),('shared/Microsoft.WindowsDesktop.App/'+$runtimeInspection.manifest.version+'/PresentationFramework.dll'))) {
    if ($required -cnotin $runtimeInspection.manifest.payload.path) { throw 'The maintenance runtime lacks the standard private .NET Desktop layout.' }
}
$maintenanceZip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($MaintenancePackagePath))
try {
    foreach ($hostName in @('heirowInstaller','heirowUpdater','heirowInstaller.Cli')) {
        foreach ($extension in @('.exe','.dll','.deps.json','.runtimeconfig.json')) { if (!$maintenanceZip.GetEntry('payload/'+$hostName+$extension)) { throw 'The maintenance package must contain complete framework-dependent native apphosts.' } }
        $entry=$maintenanceZip.GetEntry('payload/'+$hostName+'.runtimeconfig.json')
        if($entry.Length -gt 64KB){throw 'The maintenance runtime configuration is too large.'}
        $reader=[IO.StreamReader]::new($entry.Open())
        try{$configuration=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
        if($configuration.runtimeOptions.includedFrameworks -or (!$configuration.runtimeOptions.framework -and !$configuration.runtimeOptions.frameworks)){throw 'Self-contained maintenance packages duplicate the canonical runtime; rebuild framework-dependent.'}
        foreach($framework in @($configuration.runtimeOptions.framework)+@($configuration.runtimeOptions.frameworks)|Where-Object{$_}){
            $minimum=[version]$framework.version;$provided=[version]$runtimeInspection.manifest.version
            if($framework.name -cnotin @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') -or $minimum.Major -ne $provided.Major -or $minimum.Minor -ne $provided.Minor -or $minimum -gt $provided){throw 'The canonical runtime does not satisfy the maintenance host framework pin.'}
        }
    }
    foreach($forbidden in @('dotnet.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll')){if($maintenanceZip.GetEntry('payload/'+$forbidden)){throw 'A maintenance payload contains a duplicate private runtime.'}}
}finally{$maintenanceZip.Dispose()}
$manifest = $applicationInspection.manifest
$maintenanceBinding = [ordered]@{id=$maintenanceInspection.manifest.id;version=$maintenanceInspection.manifest.version;targetRuntime=$Runtime;url=$MaintenancePackageUrl.AbsoluteUri;sha256=(Get-FileHash -LiteralPath $MaintenancePackagePath -Algorithm SHA256).Hash;size=(Get-Item -LiteralPath $MaintenancePackagePath).Length}
$runtimeBinding = [ordered]@{id=$runtimeInspection.manifest.id;version=$runtimeInspection.manifest.version;targetRuntime=$Runtime;url=$RuntimePackageUrl.AbsoluteUri;sha256=(Get-FileHash -LiteralPath $RuntimePackagePath -Algorithm SHA256).Hash;size=(Get-Item -LiteralPath $RuntimePackagePath).Length}
if ($maintenanceBinding.id -ieq $runtimeBinding.id) { throw 'Maintenance host and .NET runtime must be distinct signed packages.' }
if($manifest.updateCompanion.sharedPackageId){
    foreach($binding in @($maintenanceBinding,$runtimeBinding)){
        $dependency=@($manifest.sharedPackages|Where-Object id -IEQ $binding.id)
        if($dependency.Count -ne 1 -or $dependency[0].sha256 -ine $binding.sha256 -or $dependency[0].size -ne $binding.size -or $dependency[0].version -cne $binding.version -or
            ($dependency[0].feature -and $dependency[0].feature -cne $manifest.updateCompanion.feature)){throw 'The signed application must provision the exact updater maintenance/runtime closure during setup.'}
    }
}
$descriptor = [ordered]@{
    formatVersion=1;productId=$manifest.id;name=$manifest.name;version=$manifest.version;scope=$(if($manifest.scope){$manifest.scope}else{'user'});hostKind='installer'
    publisherKeySha256=$applicationInspection.publisherKeySha256
    requiresAdministrator=([bool]$RequiresAdministrator -or [bool]$manifest.requiresAdministrator -or [bool]$manifest.applicationLaunch.requiresAdministrator -or $manifest.scope -eq 'machine')
    allowedDownloadOrigins=$AllowedDownloadOrigins
    packages=@($maintenanceBinding,$runtimeBinding)
    hostPackageId=$maintenanceInspection.manifest.id;hostEntryPoint='heirowInstaller.exe';hostArguments=@()
    dotNetPackageId=$runtimeInspection.manifest.id
    applicationPackage=@{url=$ApplicationPackageUrl.AbsoluteUri;sha256=(Get-FileHash -LiteralPath $application -Algorithm SHA256).Hash;size=(Get-Item -LiteralPath $application).Length}
    applicationPackageRequiresAuthentication=$true
}
$descriptorPath = Join-Path $output 'bootstrap-descriptor.json'
$descriptor | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $descriptorPath
$setup = Join-Path $output ($manifest.id + '-Setup.exe')
Invoke-InstallerCli -Arguments @('bind-bootstrap',$descriptorPath,'--launcher',$GenericBootstrapPath,'--output',$setup,'--sign-key',$SigningKey) | Out-Null
Invoke-InstallerCli -Arguments @('inspect-bootstrap',$setup,'--trust-key',$applicationInspection.publisherKeySha256) | Out-Null
if ((Get-Item -LiteralPath $setup).Length -ge 20000000) { throw 'The completed setup must be smaller than 20,000,000 bytes.' }
$dependencies = @($manifest.sharedPackages | ForEach-Object { [pscustomobject]@{id=$_.id;version=$_.version;bytes=[long]$_.size;sha256=$_.sha256;feature=$_.feature} })
$sharedBytes=[long](($dependencies | Measure-Object -Property bytes -Sum).Sum)
$bootstrapBytes=[long]$maintenanceBinding.size+[long]$runtimeBinding.size
$uniqueArchives=@{};foreach($dependency in $dependencies){$uniqueArchives[$dependency.sha256.ToLowerInvariant()]=$dependency.bytes};foreach($dependency in @($maintenanceBinding,$runtimeBinding)){$uniqueArchives[$dependency.sha256.ToLowerInvariant()]=[long]$dependency.size}
$entryBytes=[long](Get-Item -LiteralPath $setup).Length+[long](Get-Item -LiteralPath $application).Length
[ordered]@{productId=$manifest.id;version=$manifest.version;runtime=$Runtime;setupPath=$setup;setupBytes=(Get-Item -LiteralPath $setup).Length;applicationPath=$application;applicationBytes=(Get-Item -LiteralPath $application).Length;sharedDependencies=$dependencies;sharedDependencyBytes=$sharedBytes;maintenanceBytes=$maintenanceBinding.size;maintenanceRuntimeBytes=$runtimeBinding.size;uniqueCleanInstallDownloadBytes=($entryBytes+[long](($uniqueArchives.Values|Measure-Object -Sum).Sum));cleanInstallDownloadBytes=($entryBytes+$bootstrapBytes+$sharedBytes);downloadEstimate='Upper bound for all authored features with separate user-bootstrap and target-scope caches; warm verified cache reuse reduces actual transfers';compilerDirectory=$buildRoot} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'size-report.json')
