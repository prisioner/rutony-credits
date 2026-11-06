param([Parameter(Mandatory=$true)][string]$RutonyDirectory, [switch]$Recover)
$ErrorActionPreference = 'Stop'
# Refuse redirected helper/package directories before loading the standalone contract.
$bootstrap = Get-Item -LiteralPath $PSScriptRoot -Force
while ($bootstrap) {
    if ($bootstrap.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package ancestor is a reparse point.' }
    $bootstrap = $bootstrap.Parent
}
$helper = Get-Item -LiteralPath (Join-Path $PSScriptRoot 'package-contract.ps1') -Force
if ($helper.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package helper is a reparse point.' }
. $helper.FullName
$target = [IO.Path]::GetFullPath($RutonyDirectory).TrimEnd('\','/')
Assert-OrdinaryPath $target
$hostDll = Join-DeliveryPath $target 'RutonyChat.dll'
if (-not (Test-Path -LiteralPath $hostDll -PathType Leaf)) { throw 'Choose the installed RutonyChat directory.' }
if (Get-Process -Name RutonyChat -ErrorAction SilentlyContinue) { throw 'Close RutonyChat before installation, update or recovery.' }
$transactionRoot = Join-DeliveryPath $target "scripts/.$DeliveryModule-install"
$active = Join-DeliveryPath $transactionRoot 'active.json'
$owned = @(Get-DeliveryLayout | Where-Object install)

function Restore-DeliverySnapshot($snapshot, [string]$transaction) {
    $errors = @()
    foreach ($entry in $snapshot.files) {
        try {
            $destination = Join-DeliveryPath $target $entry.path
            if ($entry.existed) {
                $backup = Join-DeliveryPath $transaction ("backup/" + $entry.path)
                if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Backup hash mismatch: $($entry.path)" }
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath $backup -Destination $destination -Force
                if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Restored hash mismatch: $($entry.path)" }
            } elseif (Test-Path -LiteralPath $destination) {
                if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Unexpected target directory: $destination" }
                [IO.File]::Delete($destination)
            }
        } catch { $errors += "$($entry.path): $_" }
    }
    if ($errors.Count) { throw ($errors -join '; ') }
}
if (Test-Path -LiteralPath $active) {
    if (-not $Recover) { throw "Unfinished installation. Keep RutonyChat closed. Run this installer with -Recover. Recovery marker: $active" }
    $recoveryLock = [IO.File]::Open((Join-DeliveryPath $transactionRoot 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
    $marker = Get-Content -LiteralPath $active -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($marker.transaction -cnotmatch '^[0-9a-f]{32}$') { throw 'Invalid recovery transaction.' }
    $transaction = Join-DeliveryPath $transactionRoot $marker.transaction
    $snapshotFile = Join-DeliveryPath $transaction 'snapshot.json'
    if ((Get-FileHash -LiteralPath $snapshotFile -Algorithm SHA256).Hash -ine $marker.snapshotSha256) { throw 'Recovery snapshot hash mismatch. Keep backup for manual recovery.' }
    $snapshot = Get-Content -LiteralPath $snapshotFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (@($snapshot.files).Count -ne $owned.Count) { throw 'Invalid recovery file count.' }
    foreach ($entry in $snapshot.files) {
        if (-not $seen.Add($entry.path) -or $entry.path -cnotin @($owned.install) -or $entry.existed -isnot [bool] -or ($entry.existed -and $entry.sha256 -cnotmatch '^[0-9a-f]{64}$')) { throw 'Invalid recovery path or hash.' }
        $null = Join-DeliveryPath $target $entry.path
    }
    try { Restore-DeliverySnapshot $snapshot $transaction }
    catch { throw "ROLLBACK FAILED. Backup retained at $transaction. $_" }
    [IO.File]::Delete($active)
    Write-Output "Recovered previous complete installation. Backup retained at $transaction. Run again without -Recover to install."
    return
    } finally { $recoveryLock.Dispose() }
}
if ($Recover) { throw 'No unfinished installation to recover.' }

# Validate all payload bytes before creating target directories or replacing any file.
$manifest = Read-DeliveryManifest $PSScriptRoot
$entries = @($manifest.files | Where-Object install)
foreach ($entry in $entries) {
    $destination = Join-DeliveryPath $target $entry.install
    if (Test-Path -LiteralPath $destination) {
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Expected ordinary destination file: $destination" }
        $handle = [IO.File]::Open($destination,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $handle.Dispose()
    }
}
$transactionId = [Guid]::NewGuid().ToString('N')
$transaction = Join-DeliveryPath $transactionRoot $transactionId
New-Item -ItemType Directory -Path $transaction -Force | Out-Null
# Serialize cooperating installers. An interrupted run leaves the durable marker, not a held OS lock.
$lockPath = Join-DeliveryPath $transactionRoot 'install.lock'
$lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    if (Test-Path -LiteralPath $active) { throw "Another incomplete installation requires recovery: $active" }
    $snapshotEntries = foreach ($entry in $entries) {
        $source = Join-DeliveryPath $PSScriptRoot $entry.path
        $stage = Join-DeliveryPath $transaction ("stage/" + $entry.path)
        New-Item -ItemType Directory -Path (Split-Path -Parent $stage) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $stage -Force
        if ((Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash -ine $entry.sha256) { throw 'Staged hash mismatch.' }
        $destination = Join-DeliveryPath $target $entry.install
        $existed = Test-Path -LiteralPath $destination -PathType Leaf
        $oldHash = $null
        if ($existed) {
            $backup = Join-DeliveryPath $transaction ("backup/" + $entry.install)
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            $oldHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            Copy-Item -LiteralPath $destination -Destination $backup -Force
            if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ine $oldHash) { throw 'Backup hash mismatch.' }
        }
        [ordered]@{ path=$entry.install; existed=[bool]$existed; sha256=$oldHash }
    }
    $snapshot = [ordered]@{ schema=1; module=$DeliveryModule; files=@($snapshotEntries) }
    $snapshotFile = Join-DeliveryPath $transaction 'snapshot.json'
    Write-DeliveryJson $snapshotFile $snapshot
    $marker = [ordered]@{ transaction=$transactionId; status='committing'; snapshotSha256=(Get-FileHash -LiteralPath $snapshotFile -Algorithm SHA256).Hash.ToLowerInvariant() }
    Write-DeliveryJson $active $marker
    try {
        foreach ($entry in $entries) {
            $destination = Join-DeliveryPath $target $entry.install
            $stage = Join-DeliveryPath $transaction ("stage/" + $entry.path)
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $stage -Destination $destination -Force
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Installed hash mismatch: $($entry.path)" }
        }
        [IO.File]::Delete($active)
    } catch {
        $installError = "$_"
        try { Restore-DeliverySnapshot $snapshot $transaction; [IO.File]::Delete($active) }
        catch {
            $marker.status = 'rollback-failed'
            try { Write-DeliveryJson $active $marker } catch { }
            throw "ROLLBACK FAILED. Installation error: $installError. Backup retained at $transaction. Recovery marker: $active. Rollback error: $_"
        }
        throw "Installation failed; previous complete installation restored. Backup retained at $transaction. $installError"
    }
} finally { $lock.Dispose() }
Write-Output "Installed $DeliveryModule. Backup retained at $transaction. Restart RutonyChat and enable $DeliveryNative.cs."
