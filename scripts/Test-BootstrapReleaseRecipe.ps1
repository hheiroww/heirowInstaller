#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (![IO.Path]::IsPathFullyQualified($OutputDirectory) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Supply a fresh absolute owned fixture directory.' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $root | Out-Null
$recipe = Join-Path $PSScriptRoot 'Build-Bootstrap.ps1'
$probe = @{ Calls = [Collections.Generic.List[object]]::new(); Checks = 0 }
function Check([bool]$Value, [string]$Message) {
    if (!$Value) { throw $Message }
    $probe.Checks++
    Write-Host ('PASS ' + $Message)
}
function Reject([scriptblock]$Action, [string]$Message) {
    $denied = $false
    try { & $Action | Out-Null } catch { $denied = $true }
    Check $denied $Message
}
# Shadow only this process's command lookup. Neither dotnet nor a native
# executable is started: the real recipe receives tiny inert output files.
function dotnet {
    $values = @($args | ForEach-Object { [string]$_ })
    $probe.Calls.Add($values)
    if ($values[0] -eq 'publish') {
        $directory = $values[[Array]::IndexOf($values, '-o') + 1]
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $directory 'heirowBootstrap.exe'), [byte[]]::new(256))
    } elseif ($values[0] -eq 'run') {
        $path = $values[[Array]::IndexOf($values, '--output') + 1]
        [IO.File]::WriteAllBytes($path, [byte[]]::new(512))
    } else { throw 'Unexpected dotnet command.' }
    $global:LASTEXITCODE = 0
}
$previousExitCode = $global:LASTEXITCODE
try {
    $syntax = $null; $tokens = $null
    [void][Management.Automation.Language.Parser]::ParseFile($recipe, [ref]$tokens, [ref]$syntax)
    Check (@($syntax).Count -eq 0) 'the production bootstrap recipe parses without errors'

    $defaults = Join-Path $root 'defaults'
    & $recipe -OutputDirectory $defaults | Out-Null
    $receipt = Get-Content (Join-Path $defaults 'bootstrap-build.json') -Raw | ConvertFrom-Json
    $expectedVersion = [string]([xml](Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    Check ($receipt.version -eq $expectedVersion -and $receipt.version.Length -gt 0) 'omitted Version reads the existing managed version without an empty XML leaf value'
    Check ($receipt.compilerDirectory -eq (Join-Path $defaults '.compiler')) 'omitted CompilerDirectory preserves the existing output-local default'
    Check ($probe.Calls.Count -eq 1 -and $probe.Calls[0][0] -eq 'publish') 'generic launcher generation invokes only the native project publish command'
    Check ($probe.Calls[0] -contains (Join-Path (Split-Path $PSScriptRoot -Parent) 'src/HeirowInstaller.Bootstrap/HeirowInstaller.Bootstrap.csproj')) 'the actual recipe selects the standalone Bootstrap project'
    $nativeProject = [xml](Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'src/HeirowInstaller.Bootstrap/HeirowInstaller.Bootstrap.csproj') -Raw)
    Check ($nativeProject.SelectNodes('//ProjectReference').Count -eq 0) 'native publishing has no App, Core or ProjectZ project-reference graph'

    $compiler = Join-Path $root 'canonical-aot'
    New-Item -ItemType Directory -Path $compiler | Out-Null
    $marker = Join-Path $compiler 'retained-cache.txt'; [IO.File]::WriteAllText($marker, 'existing compiler cache; preserve')
    $markerHash = (Get-FileHash -LiteralPath $marker).Hash
    $fresh = Join-Path $root 'native-2026.11.5.1'
    & $recipe -OutputDirectory $fresh -CompilerDirectory $compiler -Version 2026.11.5.1 | Out-Null
    $receipt = Get-Content (Join-Path $fresh 'bootstrap-build.json') -Raw | ConvertFrom-Json
    $call = $probe.Calls[$probe.Calls.Count - 1]
    Check ($receipt.version -eq '2026.11.5.1' -and $call -contains '-p:Version=2026.11.5.1') 'the explicit native patch version reaches the native publish command'
    Check ($receipt.compilerDirectory -eq $compiler -and $call[[Array]::IndexOf($call, '--artifacts-path') + 1] -eq $compiler) 'the caller-selected canonical compiler directory is reused'
    Check ($call[[Array]::IndexOf($call, '-o') + 1] -eq (Join-Path $fresh 'generic')) 'new generic bytes go to the fresh output, independently of the compiler cache'
    Check ((Get-FileHash -LiteralPath $marker).Hash -eq $markerHash) 'the existing compiler marker remains unchanged'

    $descriptor = Join-Path $root 'descriptor.json'
    @{ formatVersion = 1; productId = 'fixture.native-only'; version = '2026.11.5.1'; packages = @(@{ id = 'fixture.maintenance'; version = '2026.11.4.1' }) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $descriptor
    $cli = Join-Path $root 'stub-cli.ps1'
    @'
$values = @($args | ForEach-Object { [string]$_ })
$output = $values[[Array]::IndexOf($values, '--output') + 1]
$values | ConvertTo-Json | Set-Content -LiteralPath ($output + '.cli-arguments.json')
[IO.File]::WriteAllBytes($output, [byte[]]::new(512))
$global:LASTEXITCODE = 0
'@ | Set-Content -LiteralPath $cli
    $generic = Join-Path $fresh 'generic/heirowBootstrap.exe'; $genericHash = (Get-FileHash -LiteralPath $generic).Hash
    $count = $probe.Calls.Count; $bound = Join-Path $root 'bound-existing-cli'
    & $recipe -DescriptorPath $descriptor -SigningKey 'fixture-unused-key' -GenericLauncherPath $generic -OutputDirectory $bound -CompilerDirectory $compiler -CliPath $cli | Out-Null
    $receipt = Get-Content (Join-Path $bound 'bootstrap-build.json') -Raw | ConvertFrom-Json
    $arguments = Get-Content (Join-Path $bound 'heirowBootstrap.exe.cli-arguments.json') -Raw | ConvertFrom-Json
    Check ($probe.Calls.Count -eq $count) 'supplied GenericLauncherPath and CliPath perform no dotnet or Core rebuild command'
    Check ($receipt.version -eq '2026.11.5.1' -and $receipt.cliPath -eq $cli -and $arguments[0] -eq 'bind-bootstrap') 'the existing CLI receives the descriptor binding command and is recorded in provenance'
    Check ((Get-FileHash -LiteralPath $generic).Hash -eq $genericHash) 'binding with an existing CLI preserves the supplied generic launcher bytes'

    $failCli = Join-Path $root 'failing-cli.ps1'; '$global:LASTEXITCODE = 7' | Set-Content -LiteralPath $failCli
    Reject { & $recipe -DescriptorPath $descriptor -SigningKey 'fixture-unused-key' -GenericLauncherPath $generic -OutputDirectory (Join-Path $root 'failed-cli') -CliPath $failCli } 'a supplied CLI failure is surfaced instead of falling back to a source CLI build'
    Check ($probe.Calls.Count -eq $count) 'CLI failure cannot trigger a hidden dotnet fallback'

    $fallback = Join-Path $root 'default-binder'
    & $recipe -DescriptorPath $descriptor -SigningKey 'fixture-unused-key' -GenericLauncherPath $generic -OutputDirectory $fallback | Out-Null
    Check ($probe.Calls[$probe.Calls.Count - 1][0] -eq 'run') 'omitted CliPath preserves the existing source-binder behavior'

    $invalidCases = @(@{ CompilerDirectory = 'relative' }, @{ CompilerDirectory = '' }, @{ CompilerDirectory = $marker }, @{ CompilerDirectory = [IO.Path]::GetPathRoot($root) },
        @{ CliPath = 'relative' }, @{ CliPath = '' }, @{ CliPath = (Join-Path $root 'missing.exe') }, @{ CliPath = $compiler })
    if ($IsWindows) { $invalidCases += @{ CliPath = $marker } }
    foreach ($invalidParameters in $invalidCases) {
        $invalidOutput = Join-Path $root ('invalid-' + $probe.Checks)
        $count = $probe.Calls.Count
        Reject { & $recipe -OutputDirectory $invalidOutput @invalidParameters } 'invalid or ambiguous helper input is rejected before any build command'
        Check (!(Test-Path -LiteralPath $invalidOutput) -and $probe.Calls.Count -eq $count) 'invalid input creates no output directory and runs no compiler'
    }
    $count = $probe.Calls.Count
    Reject { & $recipe -OutputDirectory $fresh } 'an existing generic output is rejected before native publish'
    Check ($probe.Calls.Count -eq $count -and (Get-FileHash -LiteralPath $generic).Hash -eq $genericHash) 'overwrite refusal preserves the previous generic bytes'
    Reject { & $recipe -OutputDirectory $bound } 'an existing bound bootstrap is rejected before any build or bind command'
    Check ($probe.Calls.Count -eq $count) 'bound-output refusal runs no commands'

    [ordered]@{ passed = $true; checks = $probe.Checks; actualRecipeSha256 = (Get-FileHash -LiteralPath $recipe).Hash.ToLowerInvariant();
        productionScriptInvoked = $true; commandsWereInProcessStubs = $true; actualNativeOrManagedBuild = $false; actualSigning = $false;
        nativeOrGuiExecutableStarted = $false; originalProductionArtifactsTouched = $false; retained = $true; cleanupRequiresApproval = $true } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'receipt.json')
    Write-Host ('PASS ' + $probe.Checks + ' bootstrap release recipe checks.')
} finally { $global:LASTEXITCODE = $previousExitCode }
