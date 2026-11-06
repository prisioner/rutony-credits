param([Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceRevision)
# Builds only explicit package members. Never recursively copy source web directories.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-contract.ps1')
$version = Get-DeliveryVersion $repo
$webRoot = Join-DeliveryPath $repo "src/$DeliveryModule/web"
$expectedWeb = @(Get-DeliveryLayout | Where-Object { $_.path.StartsWith('web/') } | ForEach-Object { $_.path.Substring(4) })
$actualWeb = @(Get-DeliveryFiles $webRoot | ForEach-Object { $_.Substring($webRoot.Length + 1).Replace('\','/') })
if (@(Compare-Object $expectedWeb $actualWeb -CaseSensitive).Count) { throw 'Source web directory differs from exact package allowlist. Remove untracked/unapproved files before packaging.' }
$package = Join-DeliveryPath $repo "dist/$DeliveryModule"
$backupRoot = Join-DeliveryPath $repo '.runtime/package-backups'
$stage = Join-DeliveryPath $repo ('.runtime/package-stage/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$records = foreach ($entry in Get-DeliveryLayout) {
    $path = $entry.path
    if ($path -eq "$DeliveryModule.dll") { $sourceRelative = "src/$DeliveryModule/bin/Release/net9.0/$path" }
    elseif ($path.StartsWith('web/')) { $sourceRelative = "src/$DeliveryModule/$path" }
    elseif ($path -in @("$DeliveryNative.cs","$DeliveryNative.json")) { $sourceRelative = "integration/$path" }
    elseif ($path -in @('install.ps1','package-contract.ps1')) { $sourceRelative = "scripts/$path" }
    elseif ($path.EndsWith('.md')) { $sourceRelative = "docs/$path" }
    elseif ($path.EndsWith('.url') -or $path.EndsWith('.html')) { $sourceRelative = "launcher/$path" }
    else { $sourceRelative = $path }
    $source = Join-DeliveryPath $repo $sourceRelative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Package input missing: $sourceRelative" }
    $destination = Join-DeliveryPath $stage $path
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    [ordered]@{ path=$path; length=(Get-Item -LiteralPath $destination).Length; sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant(); install=$entry.install }
}
Write-DeliveryJson (Join-DeliveryPath $stage 'package-manifest.json') ([ordered]@{ schema=1; module=$DeliveryModule; version=$version; revision=$SourceRevision; files=@($records) })
$null = Read-DeliveryManifest $stage
New-Item -ItemType Directory -Path (Split-Path -Parent $package),$backupRoot -Force | Out-Null
if (Test-Path -LiteralPath $package) {
    if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw 'Package destination is not a directory.' }
    $previous = Join-DeliveryPath $backupRoot ($DeliveryModule + '-' + [Guid]::NewGuid().ToString('N'))
    Move-Item -LiteralPath $package -Destination $previous
}
Move-Item -LiteralPath $stage -Destination $package
$null = Read-DeliveryManifest $package
Write-Output "Package ready: dist/$DeliveryModule (exact allowlist and SHA-256 manifest)"
