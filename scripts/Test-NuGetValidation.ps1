#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$PackageCache=$(if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'}))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Shared-NuGetValidation.ps1')
$output=[IO.Path]::GetFullPath($OutputDirectory);if(Test-Path -LiteralPath $output){throw 'Use a fresh owned fixture directory.'};New-Item -ItemType Directory -Path $output|Out-Null
$root=Join-Path $PackageCache 'projectz/2026.11.4.1';$metadata=Join-Path $root '.nupkg.metadata';$archive=Join-Path $root 'projectz.2026.11.4.1.nupkg'
$expected=(Get-Content -LiteralPath $metadata -Raw|ConvertFrom-Json).contentHash
$stream=[IO.File]::OpenRead($archive);try{$raw=[Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($stream))}finally{$stream.Dispose()}
if($raw -ceq $expected){throw 'This fixture must exercise a real repository-signed NuGet package with distinct raw/content hashes.'}
$reader=Open-VerifiedPublicNuGetArchive $archive 'ProjectZ' '2026.11.4.1' ('sha512-'+$expected) $metadata
try{
    Assert-VerifiedNuGetAsset $reader 'lib/net8.0-windows7.0/ProjectZ.dll' (Join-Path $root 'lib/net8.0-windows7.0/ProjectZ.dll')
    $bad=Join-Path $output 'changed-expanded.dll';Set-Content -LiteralPath $bad -Value 'inert changed expanded asset; never executed'
    $blocked=$false;try{Assert-VerifiedNuGetAsset $reader 'lib/net8.0-windows7.0/ProjectZ.dll' $bad}catch{if($_.Exception.Message -like '*expanded NuGet asset differs*'){$blocked=$true}else{throw}}
    if(!$blocked){throw 'A changed expanded public asset was accepted.'}
}finally{$reader.Dispose()}
$blocked=$false;try{$unexpected=Open-VerifiedPublicNuGetArchive $archive 'ProjectZ' '2026.11.4.1' ('sha512-'+$raw) $metadata;$unexpected.Dispose()}catch{if($_.Exception.Message -like '*content differs*'){$blocked=$true}else{throw}}
if(!$blocked){throw 'A raw ZIP hash was accepted as the content lock.'}
$changed=Join-Path $output 'changed-signed-package.nupkg';Copy-Item -LiteralPath $archive -Destination $changed
$zip=[IO.Compression.ZipFile]::Open($changed,[IO.Compression.ZipArchiveMode]::Update)
try{$asset=$zip.GetEntry('lib/net8.0-windows7.0/ProjectZ.dll');$stream=$asset.Open();try{$stream.SetLength(0);$bytes=[Text.Encoding]::UTF8.GetBytes('inert changed signed archive asset');$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}finally{$zip.Dispose()}
$blocked=$false;try{$unexpected=Open-VerifiedPublicNuGetArchive $changed 'ProjectZ' '2026.11.4.1' ('sha512-'+$expected) $metadata;$unexpected.Dispose()}catch{if($_.Exception.Message -match 'content differs|integrity'){$blocked=$true}else{throw}}
if(!$blocked){throw 'A modified repository-signed archive was accepted.'}
@{signedContentAccepted=$true;archiveAssetBound=$true;changedAssetRejected=$true;wrongContentHashRejected=$true;modifiedSignedArchiveRejected=$true;cleanupRequiresApproval=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Output 'PASS real signed NuGet content/asset binding and3negative cases.'
