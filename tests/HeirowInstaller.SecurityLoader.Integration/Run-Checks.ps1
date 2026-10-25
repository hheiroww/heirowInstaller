$ErrorActionPreference = 'Stop'
$integrationProject = Join-Path $PSScriptRoot 'HeirowInstaller.SecurityLoader.Integration.csproj'
dotnet run --project $integrationProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'SecurityLoader installer integration checks failed.' }
