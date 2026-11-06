param([switch]$Check)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $repo '.runtime\dotnet-cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $repo
try {
    $sha = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Git revision required for reproducible package builds.' }
    if ($Check) {
        & (Join-Path $repo 'tests/delivery.tests.ps1')
        & (Join-Path $repo 'tests/release.tests.ps1')

        & dotnet run --project tests/RutonyCredits.Checks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'C# checks failed' }
        & dotnet run --project tests/RutonyCredits.NativeChecks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Native donation contract checks failed' }
        $jsChecks = @(Get-ChildItem -LiteralPath tests -Filter '*.test.mjs' -File | Sort-Object Name | ForEach-Object FullName)
        & node --test @jsChecks
        if ($LASTEXITCODE -ne 0) { throw 'Browser checks failed' }
    }
    & dotnet build src/RutonyCredits -c Release "-p:SourceRevisionId=$sha"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if ($Check) {
        $sha = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Git revision required for artifact verification.' }
        & dotnet run --project tools/ArtifactChecks -c Release -- "src/RutonyCredits/bin/Release/net9.0/RutonyCredits.dll" "src/RutonyCredits/bin/Release/net9.0/RutonyCredits.pdb" $sha
        if ($LASTEXITCODE -ne 0) { throw 'Portable PE/PDB checks failed' }
    }
    & (Join-Path $PSScriptRoot 'package.ps1') -SourceRevision $sha
} finally { Pop-Location }
