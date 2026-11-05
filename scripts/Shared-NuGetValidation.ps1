# Authoring helper. Use NuGet's signature-aware content hash, not the raw signed
# ZIP digest, for a .deps.json lock binding. No metadata hash alone is authority.
function Initialize-AuthoringNuGet {
    if('NuGet.Packaging.PackageArchiveReader' -as [type]){return}
    $selected=(& dotnet --version).Trim();if($LASTEXITCODE){throw 'The selected .NET SDK is required for NuGet package validation.'}
    $entry=@(& dotnet --list-sdks|Where-Object{$_ -match ('^'+[regex]::Escape($selected)+'\s+\[(.+)\]$')})
    if(!$entry.Count -or $entry[0] -notmatch '\[(.+)\]$'){throw 'Cannot locate the selected SDK NuGet verifier.'}
    $sdk=Join-Path $Matches[1] $selected
    foreach($name in @('NuGet.Common','NuGet.Versioning','NuGet.Frameworks','NuGet.Packaging')){Add-Type -LiteralPath (Join-Path $sdk ($name+'.dll'))}
}
function Open-VerifiedPublicNuGetArchive([string]$Path,[string]$PackageId,[string]$Version,[string]$ExpectedContentHash,[string]$MetadataPath){
    if(!$ExpectedContentHash){throw 'A public package requires an exact application dependency content hash.'}
    if((Get-Item -LiteralPath $MetadataPath).Length -gt 64KB){throw 'NuGet provenance metadata exceeds its size bound.'}
    $metadata=Get-Content -LiteralPath $MetadataPath -Raw|ConvertFrom-Json
    if($metadata.source -notin @('https://api.nuget.org/v3/index.json','https://www.nuget.org/api/v2/')){throw 'Only reviewed public NuGet source packages can be exported.'}
    Initialize-AuthoringNuGet
    $reader=[NuGet.Packaging.PackageArchiveReader]::new([IO.Path]::GetFullPath($Path))
    try{
        $identity=$reader.GetIdentity()
        if($identity.Id -ine $PackageId -or $identity.Version.ToNormalizedString() -cne $Version){throw 'NuGet archive identity differs from the application dependency.'}
        $expected=$ExpectedContentHash -creplace '^sha512-',''
        $actual=$reader.GetContentHash([Threading.CancellationToken]::None,$null)
        if($actual -cne $expected){throw ('The public NuGet content differs from the application dependency lock: '+$PackageId)}
        $signature=$reader.GetPrimarySignatureAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if($signature){
            $signature.SignedCms.CheckSignature($true)
            $null=$reader.ValidateIntegrityAsync($signature.SignatureContent,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        return $reader
    }catch{$reader.Dispose();throw}
}
function Assert-VerifiedNuGetAsset($Reader,[string]$Asset,[string]$ExpandedPath){
    if(!$Asset -or [IO.Path]::IsPathRooted($Asset) -or $Asset.Contains('\') -or @($Asset.Split('/')|Where-Object{$_ -in @('','.','..')}).Count){throw 'Unsafe NuGet asset path.'}
    $stream=$Reader.GetStream($Asset)
    try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
    if($hash -ine (Get-FileHash -LiteralPath $ExpandedPath -Algorithm SHA256).Hash){throw 'An expanded NuGet asset differs from its verified archive content.'}
}
