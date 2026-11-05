#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (![IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Supply an absolute owned output directory.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh owned output directory.' }
$authoring = Join-Path $PSScriptRoot 'Build-WebInstaller.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($authoring, [ref]$null, [ref]$null)
$descriptor = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$descriptor'
}, $true)
$table = $descriptor.Right.Find({ param($node) $node -is [Management.Automation.Language.HashtableAst] }, $true)
$expression = @($table.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -ceq 'requiresAdministrator' })
if ($expression.Count -ne 1) { throw 'Find the actual signed bootstrap administrator requirement expression.' }
$evaluate = [scriptblock]::Create($expression[0].Item2.Extent.Text)
$cases = @(
    @{ name='ordinary user'; scope='user'; setup=$false; application=$false; forced=$false; expected=$false },
    @{ name='machine default leaves scope choice open'; scope='machine'; setup=$false; application=$false; forced=$false; expected=$false },
    @{ name='user setup explicitly requires approval'; scope='user'; setup=$true; application=$false; forced=$false; expected=$true },
    @{ name='machine setup explicitly requires approval'; scope='machine'; setup=$true; application=$false; forced=$false; expected=$true },
    @{ name='user application explicitly requires approval'; scope='user'; setup=$false; application=$true; forced=$false; expected=$true },
    @{ name='machine application explicitly requires approval'; scope='machine'; setup=$false; application=$true; forced=$false; expected=$true },
    @{ name='explicit authoring approval switch'; scope='user'; setup=$false; application=$false; forced=$true; expected=$true }
)
$results = foreach ($case in $cases) {
    $manifest = @{ scope=$case.scope; requiresAdministrator=$case.setup; applicationLaunch=@{ requiresAdministrator=$case.application } }
    $RequiresAdministrator = $case.forced
    $actual = & $evaluate
    if ($actual -isnot [bool] -or $actual -ne $case.expected) { throw ('Bootstrap administrator requirement failed: ' + $case.name) }
    Write-Host ('PASS ' + $case.name)
    [ordered]@{ name=$case.name; scope=$case.scope; requiresAdministrator=$actual }
}
New-Item -ItemType Directory -Path $output | Out-Null
[ordered]@{ passed=$true; checks=$results.Count; actualAuthoringSourceSha256=(Get-FileHash -LiteralPath $authoring -Algorithm SHA256).Hash.ToLowerInvariant();
    cases=$results; packagesBuilt=$false; processesElevated=$false; retained=$true; cleanupRequiresApproval=$true } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'receipt.json') -Encoding utf8NoBOM
Write-Host ('PASS ' + $results.Count + ' actual bootstrap authoring requirement checks.')
