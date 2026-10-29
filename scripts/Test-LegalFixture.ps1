# Source this helper only from isolated acceptance scripts. It never supplies documents for a real app.
function Add-TestLegalArguments([string]$Executable, [string[]]$Arguments) {
    if ([IO.Path]::GetFileNameWithoutExtension($Executable) -ne 'heirowInstaller.Cli' -or
        $Arguments.Count -eq 0 -or $Arguments[0] -notin @('pack','build','import-msi','import-exe','compile') -or
        $Arguments -contains '--legal-documents') { return ,$Arguments }
    $path = Join-Path ([IO.Path]::GetTempPath()) ('heirowInstaller-test-legal-' + [Guid]::NewGuid().ToString('N') + '.json')
    $documents = [ordered]@{ legalDocuments = [ordered]@{
        privacyPolicy = [ordered]@{url='https://example.invalid/installer-fixture/privacy';fileName='privacy-policy.txt';mediaType='text/plain';content='This isolated installer acceptance fixture uses synthetic data and temporary local files. This is not a policy for a distributed application.'}
        termsAndConditions = [ordered]@{url='https://example.invalid/installer-fixture/terms';fileName='terms-and-conditions.txt';mediaType='text/plain';content='Use this fixture only for isolated installer acceptance. These terms do not apply to another application or service.'}
    } }
    $documents | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    $result = @($Arguments) + @('--legal-documents',$path)
    if ($Arguments[0] -eq 'compile' -and $Arguments -notcontains '--id') { $result += @('--id','installer.test-fixture') }
    return ,$result
}
