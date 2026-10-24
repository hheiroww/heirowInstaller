#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Inventory,
    [Parameter(Mandatory)][string]$Launcher,
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SigningKey,
    [string[]]$ProductId
)
$ErrorActionPreference = 'Stop'
$products = (Get-Content -LiteralPath $Inventory -Raw | ConvertFrom-Json).products
if (-not $products) { throw 'Inventory requires a products array.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$receipts = @()
foreach ($product in $products) {
    if ($ProductId -and $product.id -notin $ProductId) { continue }
    if ($product.id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$' -or $product.version -notmatch '^\d+(\.\d+){0,3}$') { throw 'Unsafe product ID/version in inventory.' }
    if ($product.kind -notin @('msi','exe')) { throw "Unsupported installer kind: $($product.kind)" }
    $source = Get-Item -LiteralPath $product.sourcePath -ErrorAction Stop
    if ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installer source cannot be a reparse point.' }
    $sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($product.sha256 -and $sourceHash -ne $product.sha256.ToLowerInvariant()) { throw "Source installer changed: $($product.id)" }
    if ($product.bytes -and $source.Length -ne $product.bytes) { throw "Source installer length changed: $($product.id)" }
    $directory = Join-Path $OutputDirectory "$($product.id)-$($product.version)"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $filename = $source.Name
    $manifest = [ordered]@{
        formatVersion=1; id=$product.id; name=$product.name; version=$product.version; publisher='SocketJack'; scope=$product.scope
        description="$($product.name) setup, delivered by heirowInstaller with its original installation backend."
        homepage='https://socketjack.com/heirowInstaller/'
        updateUrl="https://socketjack.com/installers/$($product.id)/update.json"
        payload=@(@{source=$filename;path=$filename})
    }
    if ($product.PSObject.Properties.Name -contains 'nativeUpdateUrl') { $manifest.updateUrl = $product.nativeUpdateUrl }
    if ($product.kind -eq 'msi') {
        $manifest.legacyMsi = @{payloadPath=$filename;productCode=$product.productCode;installDirectoryProperty='INSTALLFOLDER';arguments=@()}
    } else {
        if (-not $product.silentArgs -or @($product.silentArgs).Count -eq 0) { throw "The EXE requires verified silent arguments: $($product.id)" }
        $interactiveArgs = if ($null -ne $product.installArgs) { @($product.installArgs) } else { @() }
        $manifest.legacyExecutable = @{payloadPath=$filename;installArguments=$interactiveArgs;silentInstallArguments=@($product.silentArgs);repairArguments=@();uninstallArguments=@()}
        if ($product.directoryArgument) { $manifest.legacyExecutable.installDirectoryArgument = $product.directoryArgument }
        if ($product.repairArgs) { $manifest.legacyExecutable.repairArguments = @($product.repairArgs) }
        if ($product.uninstallArgs) { $manifest.legacyExecutable.uninstallArguments = @($product.uninstallArgs) }
        if ($product.uninstallExecutable) { $manifest.legacyExecutable.uninstallExecutable = $product.uninstallExecutable }
    }
    $manifestPath = Join-Path $directory 'installer.json'
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    $package = Join-Path $directory "$($product.id)-$($product.version).hinst"
    $setup = Join-Path $directory "$($product.id)-$($product.version)-heirowInstaller-Setup.exe"
    $packArguments = @('pack',$manifestPath,'--source',$source.DirectoryName,'--output',$package)
    if ($SigningKey) { $packArguments += @('--sign-key',[IO.Path]::GetFullPath($SigningKey)) }
    Write-Host "Packaging $($product.name) $($product.version)"
    & $Cli @packArguments *> (Join-Path $directory 'pack.log')
    if ($LASTEXITCODE -ne 0) { throw "Package build failed for $($product.id). See its pack log." }
    & $Cli bundle $package --launcher ([IO.Path]::GetFullPath($Launcher)) --output $setup *> (Join-Path $directory 'bundle.log')
    if ($LASTEXITCODE -ne 0) { throw "Bundle build failed for $($product.id)." }
    & $Cli verify $setup *> (Join-Path $directory 'verify.log')
    if ($LASTEXITCODE -ne 0) { throw "Bundle verification failed for $($product.id)." }
    $receipt = [ordered]@{productId=$product.id;name=$product.name;version=$product.version;scope=$product.scope;backend=$product.kind;
        originalSha256=$sourceHash;originalBytes=$source.Length;package=$package;installer=$setup;
        sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $setup).Length;
        installExecuted=$false;verifiedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'conversion.json') -Encoding utf8NoBOM
    $receipts += $receipt
}
$receiptPath = Join-Path $OutputDirectory 'conversions.json'
$combined = @()
if (Test-Path -LiteralPath $receiptPath) {
    $completedIds = @($receipts | ForEach-Object { $_.productId })
    $combined += @(Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json | Where-Object { $_.productId -notin $completedIds })
}
$combined += @($receipts | ForEach-Object { [pscustomobject]$_ })
$combined | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
$receipts | ForEach-Object { [pscustomobject]$_ } | Select-Object productId,version,backend,bytes,sha256
