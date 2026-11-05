#requires -Version 7.0
<# Builds or verifies one canonical inert shared .NET Desktop package. No installation, publication or cleanup. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.0\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SigningKey,
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$PublisherKeySha256,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64'
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory);$projectRoot=Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Prepare-DotNetRuntime.ps1') -Version $Version -OutputDirectory $output -Runtime $Runtime
$receipt=Get-Content -LiteralPath (Join-Path $output 'runtime-source.json') -Raw|ConvertFrom-Json -AsHashtable
$major=([version]$Version).Major;$id='socketjack.dotnet.desktop.'+$major
$package=Join-Path $output ($id+'-'+$Version+'-'+$Runtime+'.hinst')
$legal=(Get-Content -LiteralPath (Join-Path $projectRoot 'legal/heirowinstaller/legal-documents.json') -Raw|ConvertFrom-Json -AsHashtable).legalDocuments
$manifest=[ordered]@{formatVersion=4;id=$id;name=('.NET '+$major+' Windows Desktop Runtime');version=$Version;publisher='SocketJack';scope='user';targetPlatform='windows';targetRuntime=$Runtime;legalDocuments=$legal;payload=@()}
$manifestPath=Join-Path $output 'runtime-manifest.json'
$manifest|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
if(!(Test-Path -LiteralPath $package)){
    & $CliPath pack $manifestPath --source $receipt.sourceDirectory --output $package --sign-key $SigningKey|Out-Null
    if($LASTEXITCODE){throw 'Canonical runtime package creation failed.'}
}
& $CliPath verify $package --require-signature --trust-key $PublisherKeySha256|Out-Null
if($LASTEXITCODE){throw 'Canonical runtime package signature or payload verification failed.'}
$actual=& $CliPath inspect $package|ConvertFrom-Json -AsHashtable
if($LASTEXITCODE -or $actual.manifest.id -cne $id -or $actual.manifest.version -cne $Version -or $actual.manifest.targetRuntime -cne $Runtime -or $actual.publisherKeySha256 -ine $PublisherKeySha256){throw 'Canonical runtime package identity differs.'}
$inputs=@(Get-ChildItem -LiteralPath $receipt.sourceDirectory -Recurse -File)
if($actual.manifest.payload.Count -ne $inputs.Count){throw 'Canonical package inventory differs from the official runtime.'}
foreach($file in $inputs){
    $relative=[IO.Path]::GetRelativePath($receipt.sourceDirectory,$file.FullName).Replace('\','/')
    $record=@($actual.manifest.payload|Where-Object path -CEQ $relative)
    if($record.Count -ne 1 -or $record[0].size -ne $file.Length -or $record[0].sha256 -ine (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash){throw 'Canonical package payload differs from its official source layout.'}
}
$result=[ordered]@{id=$id;version=$Version;targetRuntime=$Runtime;packagePath=$package;sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant();size=(Get-Item -LiteralPath $package).Length;publisherKeySha256=$PublisherKeySha256.ToLowerInvariant();sourceDirectory=$receipt.sourceDirectory;sourceReceipt=(Join-Path $output 'runtime-source.json');payloadBytes=$receipt.payloadBytes;payloadFiles=$inputs.Count;scope='user-or-machine-inherited';cleanupRequiresApproval=$true}
$result|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $output 'shared-runtime.json') -Encoding utf8NoBOM
Write-Output ($result|ConvertTo-Json -Compress)
