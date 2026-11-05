#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DescriptorPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SigningKey,
    [string]$GenericLauncherPath,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$Version,
    [long]$MaximumBytes = 20000000
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$descriptor = if ($DescriptorPath) { (Resolve-Path -LiteralPath $DescriptorPath).Path } else { $null }
$binding = if ($descriptor) { Get-Content -LiteralPath $descriptor -Raw | ConvertFrom-Json } else { $null }
if ($descriptor -and ($binding.formatVersion -ne 1 -or !$binding.productId -or !$binding.packages.Count -or !$SigningKey)) { throw 'A product descriptor and its publisher signing key are required together.' }
if (!$Version) { $Version = if ($binding) { [string]$binding.version } else { ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version.InnerText } }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (Test-Path -LiteralPath (Join-Path $output 'heirowBootstrap.exe')) { throw 'Bootstrap output already exists; preserve it and select a new task-owned output directory.' }
$buildRoot = Join-Path $output '.compiler'
$free = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($output)).AvailableFreeSpace
if ($free -lt 2GB) { throw 'Native compilation needs at least 2 GiB of free staging space.' }
$generic = if ($GenericLauncherPath) { (Resolve-Path -LiteralPath $GenericLauncherPath).Path } else { Join-Path $output 'generic/heirowBootstrap.exe' }
if (!$GenericLauncherPath) {
    & dotnet publish (Join-Path $projectRoot 'src/HeirowInstaller.Bootstrap/HeirowInstaller.Bootstrap.csproj') -c Release -r $Runtime --self-contained true `
        --artifacts-path $buildRoot -o (Split-Path $generic -Parent) "-p:Version=$Version" --nologo
    if ($LASTEXITCODE) { throw 'Native bootstrap publish failed.' }
}
$executable = Get-Item -LiteralPath $generic
if ($descriptor) {
    & dotnet run --project (Join-Path $projectRoot 'src/HeirowInstaller.Cli/HeirowInstaller.Cli.csproj') -c Release --artifacts-path (Join-Path $buildRoot 'binder') -- `
        bind-bootstrap $descriptor --launcher $generic --output (Join-Path $output 'heirowBootstrap.exe') --sign-key $SigningKey
    if ($LASTEXITCODE) { throw 'Product bootstrap binding failed.' }
    $executable = Get-Item -LiteralPath (Join-Path $output 'heirowBootstrap.exe')
}
if ($executable.Length -ge $MaximumBytes) { throw "Bootstrap size gate failed: $($executable.Length) bytes; limit $MaximumBytes." }
[ordered]@{productId=if($binding){$binding.productId}else{$null};version=$Version;runtime=$Runtime;bytes=$executable.Length;sha256=(Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256).Hash;descriptorSha256=if($descriptor){(Get-FileHash -LiteralPath $descriptor -Algorithm SHA256).Hash}else{$null};compilerDirectory=$buildRoot;genericLauncher=$generic} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'bootstrap-build.json')
Write-Output "BOOTSTRAP_SIZE_OK $($executable.Length) bytes"
