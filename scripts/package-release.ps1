param(
    [ValidatePattern('^[0-9a-f]{40}$')][string]$SourceRevision,
    [switch]$Verify
)
# Offline candidate packaging. No tag, release, installation or upload is performed.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-contract.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $SourceRevision) {
    $SourceRevision = (& git -C $repo rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $SourceRevision -cnotmatch '^[0-9a-f]{40}$') { throw 'Full Git source revision required.' }
}
$version = Get-DeliveryVersion $repo
$package = Join-DeliveryPath $repo "dist/$DeliveryModule"
$manifest = Read-DeliveryManifest $package
if (-not $manifest.PSObject.Properties['version'] -or $manifest.version -cne $version -or $manifest.revision -cne $SourceRevision) { throw 'Candidate package version/revision must match Directory.Build.props and the requested Git revision. Rebuild the package.' }
$archiveName = "$DeliveryModule-$version-$SourceRevision.zip"
$dist = Join-DeliveryPath $repo 'dist'
$packageManifest = Join-DeliveryPath $package 'package-manifest.json'
$manifestHash = (Get-FileHash -LiteralPath $packageManifest -Algorithm SHA256).Hash.ToLowerInvariant()

function Assert-Release([string]$Root) {
    $metadataPath = Join-DeliveryPath $Root 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf) -or (Get-Item -LiteralPath $metadataPath).Length -gt 1048576) { throw 'Bounded release metadata required.' }
    $metadata = Get-Content -Raw -Encoding UTF8 -LiteralPath $metadataPath | ConvertFrom-Json
    if ($metadata.schema -ne 1 -or $metadata.module -cne $DeliveryModule -or $metadata.version -cne $version -or $metadata.revision -cne $SourceRevision) { throw 'Release identity mismatch.' }
    if ($metadata.tools.dotnet -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or $metadata.tools.node -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' -or $metadata.tools.powershell -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$') { throw 'Release tool versions must be concrete versions without machine paths.' }
    if ($metadata.archive.path -cne $archiveName -or $metadata.archive.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $metadata.packageManifest.path -cne 'package-manifest.json' -or $metadata.packageManifest.sha256 -cne $manifestHash) { throw 'Release archive/package manifest binding mismatch.' }
    $archivePath = Join-DeliveryPath $Root $archiveName
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or (Get-Item -LiteralPath $archivePath).Length -ne $metadata.archive.length -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $metadata.archive.sha256) { throw 'Release archive hash/length mismatch.' }
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $manifest.files) { $expected.Add($entry.path, $entry) }
    $expected.Add('package-manifest.json', [pscustomobject]@{ length=(Get-Item -LiteralPath $packageManifest).Length; sha256=$manifestHash })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($zip.Entries.Count -ne $expected.Count) { throw 'Release archive member count mismatch.' }
        foreach ($entry in $zip.Entries) {
            if (-not $seen.Add($entry.FullName) -or -not $expected.ContainsKey($entry.FullName)) { throw 'Release archive member outside exact package allowlist.' }
            $record = $expected[$entry.FullName]
            if ($entry.Length -ne $record.length) { throw 'Release archive member length mismatch.' }
            $stream = $entry.Open()
            $hash = [Security.Cryptography.SHA256]::Create()
            try { $actual = ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
            finally { $stream.Dispose(); $hash.Dispose() }
            if ($actual -cne $record.sha256) { throw 'Release archive member hash mismatch.' }
        }
    } finally { $zip.Dispose() }
}
if ($Verify) {
    Assert-Release $dist
    Write-Output "PASS release candidate: $archiveName (version, revision, hashes and exact ZIP payload)"
    return
}
Push-Location -LiteralPath $repo
try {
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Concrete .NET SDK version required.' }
    $node = (& node --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $node -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Concrete Node.js version required.' }
} finally { Pop-Location }
$stage = Join-DeliveryPath $repo ('.runtime/release-stage/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$archivePath = Join-DeliveryPath $stage $archiveName
$paths = [string[]]@(@($manifest.files | ForEach-Object path) + 'package-manifest.json')
[Array]::Sort($paths, [StringComparer]::Ordinal)
$zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($path in $paths) {
        $entry = $zip.CreateEntry($path, [IO.Compression.CompressionLevel]::Optimal)
        # ZIP has no build clock, local timezone, source timestamps or machine paths.
        $entry.LastWriteTime = [DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
        $entry.ExternalAttributes = 0
        $inputStream = [IO.File]::OpenRead((Join-DeliveryPath $package $path))
        $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) }
        finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
} finally { $zip.Dispose() }
$metadata = [ordered]@{
    schema=1; module=$DeliveryModule; version=$version; revision=$SourceRevision
    tools=[ordered]@{ dotnet=$sdk; node=$node; powershell=$PSVersionTable.PSVersion.ToString() }
    archive=[ordered]@{ path=$archiveName; length=(Get-Item -LiteralPath $archivePath).Length; sha256=(Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() }
    packageManifest=[ordered]@{ path='package-manifest.json'; sha256=$manifestHash }
}
Write-DeliveryJson (Join-DeliveryPath $stage 'release-manifest.json') $metadata
Assert-Release $stage
# Preserve any differing prior candidate and metadata, including another runtime's ZIP.
foreach ($name in @($archiveName, 'release-manifest.json')) {
    $source = Join-DeliveryPath $stage $name
    $destination = Join-DeliveryPath $dist $name
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $source).Hash -ceq (Get-FileHash -LiteralPath $destination).Hash) { continue }
        $backup = Join-DeliveryPath $repo ('.runtime/release-backups/' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        Move-Item -LiteralPath $destination -Destination (Join-DeliveryPath $backup $name)
    }
    Copy-Item -LiteralPath $source -Destination $destination
}
Assert-Release $dist
Write-Output "Candidate ready: dist/$archiveName and dist/release-manifest.json (provisional; no publication)"
