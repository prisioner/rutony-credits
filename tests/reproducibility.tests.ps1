# Run after a clean commit and scripts/build.ps1, using only generated artifacts.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'scripts/package-contract.ps1')
$root = Join-Path $repo ('.runtime/reproducibility-tests/' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$dll = Join-Path $repo "src/$DeliveryModule/bin/Release/net9.0/$DeliveryModule.dll"
$packageDll = Join-Path $repo "dist/$DeliveryModule/$DeliveryModule.dll"
$manifestPath = Join-Path $repo "dist/$DeliveryModule/package-manifest.json"
$originalDll = [IO.File]::ReadAllBytes($dll)
$originalPackage = [IO.File]::ReadAllBytes($packageDll)
$originalManifest = [IO.File]::ReadAllBytes($manifestPath)
[IO.File]::WriteAllBytes((Join-Path $root 'canonical.dll'), $originalDll)
[IO.File]::WriteAllBytes((Join-Path $root 'package.dll'), $originalPackage)
[IO.File]::WriteAllBytes((Join-Path $root 'package-manifest.json'), $originalManifest)
function Set-PackageHash {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $entry = $manifest.files | Where-Object path -EQ "$DeliveryModule.dll"
    $entry.length = (Get-Item -LiteralPath $packageDll).Length
    $entry.sha256 = (Get-FileHash -LiteralPath $packageDll -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}
function Assert-GateRejects([string]$Name, [string]$Diagnostic) {
    $log = Join-Path $root "$Name.log"
    & pwsh -NoProfile -File (Join-Path $repo 'scripts/check-reproducibility.ps1') *> $log
    if ($LASTEXITCODE -eq 0) { throw "Gate accepted $Name; see $log" }
    if ((Get-Content -LiteralPath $log -Raw) -notmatch $Diagnostic) { throw "Wrong failure for $Name; see $log" }
    Write-Output "PASS reproducibility gate rejects $Name"
}
$failed = @()
try {
    # Keep the manifest valid: rejection must compare deliverables, not just detect damage.
    [IO.File]::WriteAllBytes($packageDll, [byte[]]($originalPackage + [byte]0))
    Set-PackageHash
    try { Assert-GateRejects 'package-mismatch' 'Packaged DLL bytes differ from canonical' }
    catch { $failed += "$_"; Write-Output "FAIL $_" }
    [IO.File]::WriteAllBytes($packageDll, $originalPackage)
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)

    # Appending a byte leaves valid PE/PDB metadata; canonical and package agree,
    # but committed-source outputs must reject this different candidate.
    [IO.File]::WriteAllBytes($dll, [byte[]]($originalDll + [byte]0))
    [IO.File]::WriteAllBytes($packageDll, [byte[]]($originalDll + [byte]0))
    Set-PackageHash
    try { Assert-GateRejects 'canonical-mismatch' 'Canonical DLL/PDB bytes differ from checkout' }
    catch { $failed += "$_"; Write-Output "FAIL $_" }
} finally {
    [IO.File]::WriteAllBytes($dll, $originalDll)
    [IO.File]::WriteAllBytes($packageDll, $originalPackage)
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
}
Write-Output "Reproducibility rejection fixtures: $(2 - $failed.Count) passed, $($failed.Count) failed. Evidence: $root"
if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
