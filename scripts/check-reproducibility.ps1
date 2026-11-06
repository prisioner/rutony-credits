# Compare the actual clean-HEAD deliverable with two independent checkout builds.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-contract.ps1')
Push-Location $repo
try {
    $sha = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Git checkout required.' }
    $status = @(& git -C $repo status --porcelain=v1)
    if ($LASTEXITCODE -ne 0 -or $status.Count) { throw 'Clean Git checkout required: commit source changes before building the final package and running this gate.' }
    $sources = @(& git -C $repo ls-files -- '*.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate committed C# sources.' }
    foreach ($source in $sources) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $repo $source))
        if ([Array]::IndexOf($bytes, [byte]13) -ge 0) { throw "Noncanonical C# line endings: $source. The committed policy requires LF bytes; use a fresh checkout or explicitly normalize existing source files, then rebuild. This gate does not rewrite sources." }
    }
    $env:DOTNET_CLI_HOME = Join-Path $repo '.runtime/dotnet-cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Selected .NET SDK required.' }
    $canonicalDll = Join-Path $repo "src/$DeliveryModule/bin/Release/net9.0/$DeliveryModule.dll"
    $canonicalPdb = Join-Path $repo "src/$DeliveryModule/bin/Release/net9.0/$DeliveryModule.pdb"
    if (-not (Test-Path -LiteralPath $canonicalDll -PathType Leaf) -or -not (Test-Path -LiteralPath $canonicalPdb -PathType Leaf)) { throw 'Canonical DLL/PDB required: run scripts/build.ps1 at this clean HEAD with the selected SDK first.' }
    $package = Join-Path $repo "dist/$DeliveryModule"
    $manifest = Read-DeliveryManifest $package
    if ($manifest.revision -cne $sha) { throw 'Package revision differs from clean HEAD. Rebuild scripts/build.ps1 before running this gate.' }
    & dotnet run --project tools/ArtifactChecks -c Release -- $canonicalDll $canonicalPdb $sha
    if ($LASTEXITCODE -ne 0) { throw 'Canonical PE/PDB metadata or revision check failed. Rebuild scripts/build.ps1 at this clean HEAD and SDK.' }
    $canonical = [pscustomobject]@{ dll=(Get-FileHash -LiteralPath $canonicalDll -Algorithm SHA256).Hash; pdb=(Get-FileHash -LiteralPath $canonicalPdb -Algorithm SHA256).Hash }
    $packageHash = (Get-FileHash -LiteralPath (Join-Path $package "$DeliveryModule.dll") -Algorithm SHA256).Hash
    if ($packageHash -cne $canonical.dll) { throw "Packaged DLL bytes differ from canonical: package=$packageHash canonical=$($canonical.dll). Rebuild the final package before retrying." }
    Write-Output "Deliverable inputs: commit=$sha SDK=$sdk canonical/dist DLL=$($canonical.dll) canonical PDB=$($canonical.pdb)"
    $root = Join-DeliveryPath $repo ('.runtime/reproducibility/' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Write-Output "Retained checkout evidence: $root"
    $hashes = @()
    foreach ($name in @('checkout-a','a-longer-independent-checkout-b')) {
        $checkout = Join-Path $root $name
        & git -C $repo worktree add --detach $checkout $sha
        if ($LASTEXITCODE -ne 0) { throw 'Isolated checkout creation failed.' }
        $env:DOTNET_CLI_HOME = Join-Path $checkout '.runtime/dotnet-cli'
        Push-Location $checkout
        try {
            if ((& dotnet --version).Trim() -ne $sdk -or $LASTEXITCODE -ne 0) { throw 'SDK changed between checkouts.' }
            & dotnet build "src/$DeliveryModule" -c Release "-p:SourceRevisionId=$sha"
            if ($LASTEXITCODE -ne 0) { throw 'Reproducibility build failed.' }
            $dll = "src/$DeliveryModule/bin/Release/net9.0/$DeliveryModule.dll"
            $pdb = "src/$DeliveryModule/bin/Release/net9.0/$DeliveryModule.pdb"
            & dotnet run --project tools/ArtifactChecks -c Release -- $dll $pdb $sha
            if ($LASTEXITCODE -ne 0) { throw 'Reproducibility metadata check failed.' }
            $hash = [pscustomobject]@{ dll=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash; pdb=(Get-FileHash -LiteralPath $pdb -Algorithm SHA256).Hash }
            $hashes += $hash
            Write-Output "Checkout $name DLL=$($hash.dll) PDB=$($hash.pdb)"
        } finally { Pop-Location }
    }
    if ($hashes[0].dll -cne $hashes[1].dll -or $hashes[0].pdb -cne $hashes[1].pdb) { throw 'DLL/PDB bytes differ between checkout roots.' }
    foreach ($hash in $hashes) {
        if ($hash.dll -cne $canonical.dll -or $hash.pdb -cne $canonical.pdb) { throw "Canonical DLL/PDB bytes differ from checkout outputs. Rebuild scripts/build.ps1 from canonical LF sources at commit=$sha SDK=$sdk. Evidence retained: $root" }
    }
    Write-Output "PASS reproducibility: commit=$sha SDK=$sdk canonical/dist/two-checkout DLL=$($canonical.dll) canonical/two-checkout PDB=$($canonical.pdb). Two detached Git checkouts retained: $root"
} finally { Pop-Location }
