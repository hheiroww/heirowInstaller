function Get-ToolkitLocalRuntimeVersion($Manifest) {
    $localFiles = @($Manifest.payload.path | Where-Object { $_ -and $_.StartsWith('dotnet/', [StringComparison]::OrdinalIgnoreCase) })
    if (!$localFiles.Count) { return }

    $versions = @(foreach ($path in $localFiles) {
        if ($path -cmatch '^dotnet/host/fxr/(10\.0\.[0-9]+)/hostfxr\.dll$') { $Matches[1] }
    })
    if ($versions.Count -ne 1) { throw 'The toolkit must have exactly one package-local .NET 10 hostfxr.' }
    $version = $versions[0]
    foreach ($required in @('dotnet/dotnet.exe', "dotnet/shared/Microsoft.NETCore.App/$version/coreclr.dll",
        "dotnet/shared/Microsoft.WindowsDesktop.App/$version/PresentationFramework.dll")) {
        if ($required -cnotin $localFiles) { throw 'The toolkit lacks a complete package-local Desktop runtime.' }
    }
    return $version
}
