#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RootPackageCache = $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' })
)
$ErrorActionPreference='Stop'
$sourceRoot=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh task-owned notice test output directory.'}
New-Item -ItemType Directory -Path $output|Out-Null
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Build-SharedToolkit.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Toolkit script did not parse.'}
$collector=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Copy-ToolkitDependencyNotices'},$true)
. (Join-Path $PSScriptRoot 'Shared-NuGetValidation.ps1')
. ([scriptblock]::Create($collector.Extent.Text))
$script:checks=0
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++;Write-Output ('PASS '+$Message)}
function Fixture([string]$Name){
    $target=Join-Path $output $Name;New-Item -ItemType Directory -Path $target|Out-Null
    foreach($file in Get-ChildItem -LiteralPath $PayloadDirectory -File){
        if($file.Extension -in @('.dll','.exe') -or $file.Name.EndsWith('.deps.json')){
            # Read-only use of hardlinks avoids dependency/resource copies. Tests
            # write only newly generated notice files, never linked source files.
            New-Item -ItemType HardLink -Path (Join-Path $target $file.Name) -Target $file.FullName|Out-Null
        }
    }
    return $target
}
$assets=Join-Path $sourceRoot 'third-party-license-assets'
$positive=Fixture 'positive'
Copy-ToolkitDependencyNotices $positive $RootPackageCache $assets
$inventory=Get-Content -LiteralPath (Join-Path $positive 'ThirdParty/dependencies.json') -Raw|ConvertFrom-Json
Check ($inventory.Count -ge 10) 'Actual shipped dependency packages are inventoried'
$map=Get-Content -LiteralPath (Join-Path $assets 'provenance.json') -Raw|ConvertFrom-Json
foreach($notice in $map.entries){
    $record=@($inventory|Where-Object{$_.id -ceq $notice.packageId -and $_.version -ceq $notice.version})
    Check ($record.Count -eq 1) ('Exact package identity has reviewed notice: '+$notice.packageId)
    $path='ThirdParty/'+$notice.packageId+'/'+$notice.version+'/'+$notice.targetFileName
    $actual=Join-Path $positive $path
    Check ((Get-Item -LiteralPath $actual).Length -eq $notice.bytes -and (Get-FileHash -LiteralPath $actual -Algorithm SHA256).Hash -ieq $notice.sha256) ('Original full notice bytes preserved: '+$notice.packageId)
    Check ($record[0].upstreamNoticeSources.Count -eq 1 -and $record[0].upstreamNoticeSources[0].revision -ceq $notice.revision -and $record[0].noticeFiles -ccontains $path) ('Immutable upstream provenance retained separately: '+$notice.packageId)
}
Check (($inventory|Where-Object id -CEQ 'LibTessDotNet').license -ceq 'MIT') 'NuGet claimed license remains unchanged beside original SGI upstream notice'
Check ((Test-Path -LiteralPath (Join-Path $positive 'ThirdParty/ProjectZ-NOTICES.md')) -and (Test-Path -LiteralPath (Join-Path $positive 'ThirdParty/SocketJack-LICENSE.txt'))) 'Exact package notice aliases remain present'
$tampered=Join-Path $output 'tampered-assets';New-Item -ItemType Directory -Path $tampered|Out-Null
foreach($notice in $map.entries){$destination=Join-Path $tampered $notice.assetPath;New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $assets $notice.assetPath) -Destination $destination}
Copy-Item -LiteralPath (Join-Path $assets 'provenance.json') -Destination (Join-Path $tampered 'provenance.json')
$changed=Join-Path $tampered $map.entries[0].assetPath;$bytes=[IO.File]::ReadAllBytes($changed);$bytes[0]=$bytes[0] -bxor 1;[IO.File]::WriteAllBytes($changed,$bytes)
$negative=Fixture 'negative-tampered-pin';$rejected=$false
try{Copy-ToolkitDependencyNotices $negative $RootPackageCache $tampered}catch{if($_.Exception.Message -notlike '*immutable SHA256/size pin*'){throw};$rejected=$true}
Check $rejected 'Same-size tampered repository license asset is rejected by its SHA256 pin'
Check (!(Test-Path -LiteralPath (Join-Path $negative ('ThirdParty/'+$map.entries[0].assetPath)))) 'Rejected upstream bytes are never copied into the output payload'
@{passed=$true;assertions=$script:checks;payloadSource=[IO.Path]::GetFullPath($PayloadDirectory);packageCount=$inventory.Count;noticeFiles=(Get-ChildItem -LiteralPath (Join-Path $positive 'ThirdParty') -Recurse -File|Measure-Object).Count;hardlinksUsedWithoutSourceWrites=$true;buildPerformed=$false;signingPerformed=$false;cleanupRequiresApproval=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'receipt.json') -Encoding utf8NoBOM
