param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceRevision,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$RepositorySlug,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
# Offline validation/staging only. Network access and draft creation belong to CI.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-contract.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$version = Get-DeliveryVersion $repo
if ($Tag -cne "v$version") { throw 'Release tag must match Version in Directory.Build.props.' }
$inputRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
Assert-OrdinaryPath $inputRoot
Assert-OrdinaryPath $outputRoot
if (Test-Path -LiteralPath $outputRoot) { throw 'Release output must not already exist; existing files are never overwritten.' }
$metadataPath = Join-DeliveryPath $inputRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf) -or (Get-Item -LiteralPath $metadataPath).Length -gt 1048576) { throw 'Bounded release manifest required.' }
$metadata = Get-Content -Raw -Encoding UTF8 -LiteralPath $metadataPath | ConvertFrom-Json
if ($metadata.schema -ne 1 -or $metadata.module -cne $DeliveryModule -or $metadata.version -cne $version -or $metadata.revision -cne $SourceRevision) { throw 'Release identity does not match the checked source.' }
$archiveName = "$DeliveryModule-$version-$SourceRevision.zip"
if ($metadata.archive.path -cne $archiveName -or $metadata.archive.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Release archive name/hash contract mismatch.' }
$archivePath = Join-DeliveryPath $inputRoot $archiveName
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'Release archive is missing.' }
if ((Get-Item -LiteralPath $archivePath).Length -ne $metadata.archive.length -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $metadata.archive.sha256) { throw 'Release archive hash/length mismatch.' }
if ($metadata.packageManifest.path -cne 'package-manifest.json' -or $metadata.packageManifest.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Package manifest binding is invalid.' }
# Extract only the exact package allowlist into a fresh private validation stage.
# Reuse the installer contract for all inner file hashes instead of executing ZIP content.
$stage = Join-DeliveryPath $repo ('.runtime/github-release-input/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in Get-DeliveryLayout) { [void]$expected.Add($entry.path) }
[void]$expected.Add('package-manifest.json')
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if ($zip.Entries.Count -ne $expected.Count) { throw 'Release ZIP member count mismatch.' }
    foreach ($entry in $zip.Entries) {
        if (-not $expected.Contains($entry.FullName) -or -not $seen.Add($entry.FullName)) { throw 'Release ZIP member is outside the exact allowlist.' }
        if ($entry.Length -gt 33554432) { throw 'Release ZIP member exceeds the validation size limit.' }
        $file = Join-DeliveryPath $stage $entry.FullName
        New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        $source = $entry.Open()
        $target = [IO.File]::Open($file,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
        try { $source.CopyTo($target) } finally { $source.Dispose(); $target.Dispose() }
    }
} finally { $zip.Dispose() }
$package = Read-DeliveryManifest $stage
if ($package.version -cne $version -or $package.revision -cne $SourceRevision) { throw 'Package manifest identity mismatch.' }
if ((Get-FileHash -LiteralPath (Join-Path $stage 'package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $metadata.packageManifest.sha256) { throw 'Package manifest hash mismatch.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
foreach ($name in @($archiveName,'release-manifest.json')) {
    Copy-Item -LiteralPath (Join-DeliveryPath $inputRoot $name) -Destination (Join-DeliveryPath $outputRoot $name)
}
$encoding = [Text.UTF8Encoding]::new($false)
$sums = foreach ($name in @($archiveName,'release-manifest.json')) {
    (Get-FileHash -LiteralPath (Join-DeliveryPath $outputRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
}
[IO.File]::WriteAllText((Join-DeliveryPath $outputRoot 'SHA256SUMS'),(($sums -join "`n") + "`n"),$encoding)
$base = "https://github.com/$RepositorySlug"
$notes = @(
    "$DeliveryModule $version",
    '',
    'Готовый ZIP-пакет для ручной установки. Распакуйте архив перед установкой; SDK и Node.js пользователю не нужны.',
    '',
    "[Инструкция установки]($base/blob/$SourceRevision/README.md) · [Исходный код]($base/tree/$SourceRevision)",
    '',
    'SHA256SUMS содержит контрольные суммы ZIP и release-manifest.json. Манифест связывает пакет с версией и исходным commit.',
    '',
    'Лицензия MIT. Copyright (c) 2026 Aleksandr Klimenkov. Предоставляется как есть, без гарантий.'
)
[IO.File]::WriteAllText((Join-DeliveryPath $outputRoot 'release-notes.md'),(($notes -join "`n") + "`n"),$encoding)
Write-Output "Verified GitHub release assets staged for $Tag (offline; no publication)."
