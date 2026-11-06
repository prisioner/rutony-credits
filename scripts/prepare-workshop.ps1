param([string]$PackageDirectory)
# Offline only: this script never starts Steam, RutonyChat or an uploader.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-contract.ps1')
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) { $PackageDirectory = Join-DeliveryPath $repo "dist/$DeliveryModule" }
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$manifest = Read-DeliveryManifest $PackageDirectory
$payload = Join-DeliveryPath $repo "dist/Workshop/$DeliveryModule"
$materials = Join-DeliveryPath $repo "dist/WorkshopMaterials/$DeliveryModule"
# Reject invalid destinations before moving either previous result.
foreach ($destination in @($payload,$materials)) {
    if (Test-Path -LiteralPath $destination) {
        if (-not (Test-Path -LiteralPath $destination -PathType Container)) { throw 'Workshop destination must be a directory.' }
        $null = @(Get-DeliveryFiles $destination)
    }
}
$stage = Join-DeliveryPath $repo ('.runtime/workshop-stage/' + [Guid]::NewGuid().ToString('N'))
$stagePayload = Join-DeliveryPath $stage 'payload'
$stageMaterials = Join-DeliveryPath $stage 'materials'
[IO.Directory]::CreateDirectory($stagePayload) | Out-Null
[IO.Directory]::CreateDirectory($stageMaterials) | Out-Null
$records = foreach ($entry in $manifest.files) {
    if (-not $entry.install) { continue }
    if (-not $entry.install.StartsWith('scripts/', [StringComparison]::Ordinal)) { throw 'Unexpected install mapping.' }
    $relative = $entry.install.Substring(8)
    $source = Join-DeliveryPath $PackageDirectory $entry.path
    $targets = @($relative)
    if ($entry.path -ceq 'LICENSE') { $targets += 'LICENSE' }
    foreach ($target in $targets) {
        $file = Join-DeliveryPath $stagePayload $target
        [IO.Directory]::CreateDirectory((Split-Path -Parent $file)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $file
        $length = (Get-Item -LiteralPath $file).Length
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($length -ne $entry.length -or $hash -cne $entry.sha256) { throw 'Package changed during Workshop staging.' }
        [ordered]@{path=$target; length=$length; sha256=$hash}
    }
}
foreach ($relative in @('docs/workshop/README.md','docs/workshop/description.txt','docs/workshop/metadata.json','assets/workshop/preview.svg','assets/workshop/preview.png')) {
    $source = Join-DeliveryPath $repo $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Workshop material missing: $relative" }
    Copy-Item -LiteralPath $source -Destination (Join-DeliveryPath $stageMaterials ([IO.Path]::GetFileName($relative)))
}
$preview = [IO.File]::ReadAllBytes((Join-DeliveryPath $stageMaterials 'preview.png'))
if ($preview.Length -lt 33 -or $preview.Length -ge 1000000 -or
    [BitConverter]::ToString($preview[0..7]) -cne '89-50-4E-47-0D-0A-1A-0A' -or
    [BitConverter]::ToString($preview[12..23]) -cne '49-48-44-52-00-00-02-00-00-00-02-00') {
    throw 'Workshop preview must be a 512x512 PNG smaller than 1,000,000 bytes.'
}
$metadata = Get-Content -LiteralPath (Join-DeliveryPath $stageMaterials 'metadata.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($metadata.appId -ne 524660 -or $metadata.itemType -cne 'script' -or $metadata.status -cne 'draft' -or $metadata.module -cne $DeliveryModule) { throw 'Invalid independent Workshop draft metadata.' }
$workshopManifest = [ordered]@{schema=1; module=$DeliveryModule; revision=$manifest.revision; files=@($records | Sort-Object { $_.path })}
if ($manifest.PSObject.Properties['version']) { $workshopManifest.version = $manifest.version }
Write-DeliveryJson (Join-DeliveryPath $stageMaterials 'workshop-manifest.json') $workshopManifest
# Keep old output and failed staging for inspection; never recursively delete paths.
$backupRoot = Join-DeliveryPath $repo ('.runtime/workshop-backups/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$operations = @(
    [pscustomobject]@{stage=$stagePayload; target=$payload; backup=(Join-DeliveryPath $backupRoot 'payload'); saved=$false; promoted=$false},
    [pscustomobject]@{stage=$stageMaterials; target=$materials; backup=(Join-DeliveryPath $backupRoot 'materials'); saved=$false; promoted=$false}
)
try {
    foreach ($operation in $operations) {
        Assert-OrdinaryPath $operation.target
        [IO.Directory]::CreateDirectory((Split-Path -Parent $operation.target)) | Out-Null
        if (Test-Path -LiteralPath $operation.target) {
            Move-Item -LiteralPath $operation.target -Destination $operation.backup
            $operation.saved = $true
        }
        Move-Item -LiteralPath $operation.stage -Destination $operation.target
        $operation.promoted = $true
    }
} catch {
    $failure = $_
    for ($i = $operations.Count - 1; $i -ge 0; $i--) {
        $operation = $operations[$i]
        if ($operation.promoted) { Move-Item -LiteralPath $operation.target -Destination $operation.stage }
        if ($operation.saved) { Move-Item -LiteralPath $operation.backup -Destination $operation.target }
    }
    throw $failure
}
Write-Output "Workshop offline payload ready: dist/Workshop/$DeliveryModule"
Write-Output "Draft materials ready: dist/WorkshopMaterials/$DeliveryModule (real Steam acceptance NOT RUN)"
