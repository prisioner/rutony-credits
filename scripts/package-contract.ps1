# Standalone package contract; used by both build and the packaged installer.
Set-StrictMode -Version Latest
$DeliveryModule = 'RutonyCredits'
$DeliveryNative = 'rutony_credits'
function Get-DeliveryLayout {
    $paths = @('LICENSE', "$DeliveryModule.dll", "$DeliveryNative.cs", "$DeliveryNative.json", 'install.ps1', 'package-contract.ps1', 'web/control.css','web/control.html','web/control.js','web/font-picker.js','web/local-font.js','web/overlay.css','web/overlay.html','web/overlay.js','web/playback.js', 'manual-test.md','obs-automation.md','Открыть титры.url','Открыть титры.html')
    foreach ($path in $paths) {
        $install = $null
        if ($path -in @("$DeliveryNative.cs", "$DeliveryNative.json")) { $install = "scripts/$path" }
        elseif ($path -eq 'LICENSE' -or $path -eq "$DeliveryModule.dll" -or $path.StartsWith('web/')) { $install = "scripts/$DeliveryModule/$path" }
        [pscustomobject]@{ path=$path; install=$install }
    }
}
function Assert-OrdinaryPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $current = [IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($current.Length).Split([char[]]@('\','/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not permitted: $current" }
        } elseif ([IO.Directory]::Exists((Split-Path -Parent $current))) {
            # Test-Path can return false for dangling links. Inspect the directory entry too.
            $entry = Get-ChildItem -LiteralPath (Split-Path -Parent $current) -Force | Where-Object Name -EQ $part
            if ($entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse point is not permitted: $current" }
        }
    }
}
function Join-DeliveryPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative -notmatch '^[^\\:]+$' -or $Relative.StartsWith('/') -or @($Relative.Split('/') | Where-Object { $_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count) { throw "Invalid relative package path: $Relative" }
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $full = [IO.Path]::GetFullPath((Join-Path $fullRoot $Relative))
    if (-not $full.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes its root.' }
    Assert-OrdinaryPath $full
    return $full
}
function Get-DeliveryFiles([string]$Root) {
    Assert-OrdinaryPath $Root
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force) {
        Assert-OrdinaryPath $item.FullName
        if ($item.PSIsContainer) { Get-DeliveryFiles $item.FullName }
        else { $item.FullName }
    }
}
function Test-DeliveryVersion($Value) {
    # SemVer without build metadata: safe, unambiguous candidate filenames.
    return ($Value -is [string] -and $Value -cmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?\z')
}
function Get-DeliveryVersion([string]$Repository) {
    $file = Join-DeliveryPath $Repository 'Directory.Build.props'
    [xml]$props = Get-Content -Raw -Encoding UTF8 -LiteralPath $file
    $versions = @($props.SelectNodes('/Project/PropertyGroup/Version'))
    if ($versions.Count -ne 1 -or -not (Test-DeliveryVersion $versions[0].InnerText)) { throw 'One literal SemVer Version without build metadata is required in Directory.Build.props.' }
    return $versions[0].InnerText
}
function Read-DeliveryManifest([string]$Root) {
    $manifestFile = Join-DeliveryPath $Root 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf) -or (Get-Item -LiteralPath $manifestFile).Length -gt 1048576) { throw 'Complete bounded package manifest required.' }
    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestFile | ConvertFrom-Json
    if ($manifest.PSObject.Properties['version'] -and -not (Test-DeliveryVersion $manifest.version)) { throw 'Invalid optional package version.' }
    $layout = @(Get-DeliveryLayout)
    if ($manifest.schema -ne 1 -or $manifest.revision -cnotmatch '^[0-9a-f]{40}$' -or $manifest.module -cne $DeliveryModule -or @($manifest.files).Count -ne $layout.Count) { throw 'Invalid package manifest contract.' }
    $expected = @{}
    foreach ($entry in $layout) { $expected[$entry.path] = $entry }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $file = Join-DeliveryPath $Root $entry.path
        if (-not $seen.Add($entry.path) -or -not $expected.ContainsKey($entry.path) -or $entry.path -cne $expected[$entry.path].path -or $entry.install -cne $expected[$entry.path].install) { throw "Package path outside exact allowlist: $($entry.path)" }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $entry.length -lt 0 -or (Get-Item -LiteralPath $file).Length -ne $entry.length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Package missing or hash mismatch: $($entry.path)" }
    }
    $allFiles = @(Get-DeliveryFiles $Root)
    if ($allFiles.Count -ne $layout.Count + 1) { throw 'Unexpected files in package.' }
    foreach ($file in $allFiles) {
        $relative = $file.Substring($Root.TrimEnd('\','/').Length + 1).Replace('\','/')
        if ($relative -cne 'package-manifest.json' -and -not $seen.Contains($relative)) { throw "Unexpected package member: $relative" }
    }
    return $manifest
}
function Write-DeliveryJson([string]$Path, $Value) {
    Assert-OrdinaryPath $Path
    $temp = "$Path.tmp"
    Assert-OrdinaryPath $temp
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 12))
    $stream = [IO.FileStream]::new($temp,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp,$Path,$null) } else { [IO.File]::Move($temp,$Path) }
}
