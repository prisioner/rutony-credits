# Requires PowerShell 7. Reads only Git-tracked files and objects reachable from refs.
# Diagnostics deliberately contain categories/counts, never paths, identities or matches.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$script:issues = [Collections.Generic.Dictionary[string,int]]::new()
$runtimeLeaves = @('settings.json', 'current.json', 'obs.json', 'obs-secret.bin', 'writer.lock', 'workshopitem.meta')
$runtimeCopies = @('settings.json.tmp', 'current.json.tmp', 'settings.json.recovery-*', 'current.json.recovery-*', 'obs.json.tmp-*', 'obs.json.recovery-*', 'obs-secret.bin.tmp-*', 'obs-secret.bin.recovery-*', 'obs-secret.bin.rollback-*')
function Issue([string]$category) {
    if (-not $script:issues.ContainsKey($category)) { $script:issues[$category] = 0 }
    $script:issues[$category]++
}
function NewGitProcess([string[]]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = 'git'
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @('-c', 'core.quotePath=false') + $arguments) { [void]$info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    return $process
}
function RunGit([string[]]$arguments, [string]$inputText = '') {
    $process = NewGitProcess $arguments
    try {
        [void]$process.Start()
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if ($inputText.Length -gt 0) { $process.StandardInput.Write($inputText) }
        $process.StandardInput.Close()
        $output = $outputTask.GetAwaiter().GetResult()
        $null = $errorTask.GetAwaiter().GetResult()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'Git publication query failed.' }
        return $output
    } finally { $process.Dispose() }
}
function UnsafePath([string]$path) {
    $segments = $path.Replace('\', '/').Split('/')
    $leaf = $segments[-1]
    if ($segments -contains '.local' -or $segments -contains '.runtime') { return $true }
    if ($leaf -match '^\.env(?:\..+)?$' -and $leaf -ne '.env.example') { return $true }
    if ($runtimeLeaves -contains $leaf) { return $true }
    foreach ($pattern in $runtimeCopies) { if ($leaf -like $pattern) { return $true } }
    if ($segments -contains 'sessions') { return $true }
    return $false
}
# Narrow provider/key signatures; split construction keeps policy/fixtures scannable.
$signatures = @(
    ('gh' + '[pousr]_[A-Za-z0-9]{36,}'),
    ('github' + '_pat_[A-Za-z0-9_]{40,}'),
    ('(?:AK' + 'IA|AS' + 'IA)[A-Z0-9]{16}'),
    ('xox' + '[baprs]-[A-Za-z0-9-]{20,}'),
    ('AI' + 'za[A-Za-z0-9_-]{35}'),
    ('-----BEGIN ' + '(?:(?:RSA|EC|DSA|OPENSSH|ENCRYPTED) )?PRIVATE KEY-----')
)
$secretPattern = [regex]::new(($signatures -join '|'), [Text.RegularExpressions.RegexOptions]::CultureInvariant)
function HasSecret([string]$text) { return $secretPattern.IsMatch($text) }
function ScanHistory([string[]]$objectIds) {
    $process = NewGitProcess @('cat-file', '--batch')
    $started = $false
    try {
        [void]$process.Start()
        $started = $true
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.AutoFlush = $true
        # Write concurrently: large object-id lists must not block output consumption.
        $inputTask = $process.StandardInput.WriteAsync(($objectIds -join "`n") + "`n")
        $output = $process.StandardOutput.BaseStream
        foreach ($objectId in $objectIds) {
            $header = [Collections.Generic.List[byte]]::new()
            while ($true) {
                $value = $output.ReadByte()
                if ($value -lt 0) { throw 'Incomplete Git object header.' }
                if ($value -eq 10) { break }
                if ($header.Count -ge 128) { throw 'Unexpected Git object header.' }
                $header.Add([byte]$value)
            }
            $parsed = [regex]::Match([Text.Encoding]::ASCII.GetString($header.ToArray()), '^([0-9a-f]{40,64}) (blob|tree|commit|tag) ([0-9]+)$')
            if (-not $parsed.Success -or $parsed.Groups[1].Value -ne $objectId) { throw 'Unexpected Git object header.' }
            $length = [long]::Parse($parsed.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
            if ($length -gt [int]::MaxValue) { throw 'Git object exceeds scanner byte-buffer limit.' }
            $content = [byte[]]::new([int]$length)
            $offset = 0
            while ($offset -lt $content.Length) {
                $read = $output.Read($content, $offset, $content.Length - $offset)
                if ($read -eq 0) { throw 'Incomplete Git object content.' }
                $offset += $read
            }
            if ($output.ReadByte() -ne 10) { throw 'Unexpected Git object terminator.' }
            # Decode each exact object independently, using the same BOM detection as
            # current text files (UTF-8 default; UTF-16/UTF-32 LE/BE when marked).
            $reader = [IO.StreamReader]::new([IO.MemoryStream]::new($content, $false), [Text.Encoding]::UTF8, $true)
            try { if (HasSecret ($reader.ReadToEnd())) { Issue 'history-secret' } }
            finally { $reader.Dispose() }
        }
        $null = $inputTask.GetAwaiter().GetResult()
        $process.StandardInput.Close()
        $null = $errorTask.GetAwaiter().GetResult()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'Git publication query failed.' }
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
function ReparseInPath([string]$relative) {
    $part = $root
    foreach ($segment in $relative.Replace('\', '/').Split('/')) {
        $part = Join-Path $part $segment
        if (Test-Path -LiteralPath $part) {
            if (([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        }
    }
    return $false
}
function MarkdownContent([string]$text) {
    # Apply the same fenced-example exclusion to outgoing links and anchors.
    return [regex]::Replace($text, '(?ms)^ {0,3}(```|~~~).*?^ {0,3}\1\s*$', '')
}
function MarkdownAnchors([string]$text) {
    $text = MarkdownContent $text
    $anchors = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $duplicates = @{}
    foreach ($heading in [regex]::Matches($text, '(?m)^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$')) {
        $title = $heading.Groups[1].Value.ToLowerInvariant()
        $title = [regex]::Replace($title, '<[^>]*>', '')
        $slug = [regex]::Replace($title, '[^\p{L}\p{N}_\-\s]', '')
        $slug = [regex]::Replace($slug.Trim(), '\s', '-')
        $number = if ($duplicates.ContainsKey($slug)) { $duplicates[$slug] + 1 } else { 0 }
        $duplicates[$slug] = $number
        [void]$anchors.Add($(if ($number -eq 0) { $slug } else { "$slug-$number" }))
    }
    foreach ($anchor in [regex]::Matches($text, '(?i)<a\s+(?:name|id)\s*=\s*["'']([^"'']+)["'']')) { [void]$anchors.Add($anchor.Groups[1].Value) }
    return ,$anchors
}
function CheckLink([string]$document, [string]$rawTarget, $tracked, $texts) {
    $target = [uri]::UnescapeDataString($rawTarget)
    if ($target -match '^(?i)(https?://|mailto:)') { return }
    if ($target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:' -or $target -match '^[\\/]' -or $target.Contains('\')) { Issue 'markdown-link'; return }
    $split = $target.Split('#', 2)
    $relative = $split[0].Split('?', 2)[0]
    $base = Split-Path (Join-Path $root $document) -Parent
    $full = if ($relative.Length -eq 0) { Join-Path $root $document } else { [IO.Path]::GetFullPath((Join-Path $base $relative)) }
    $rootDirectory = [IO.Path]::GetFullPath($root).TrimEnd([char[]]@('\', '/'))
    $isRoot = [string]::Equals($full.TrimEnd([char[]]@('\', '/')), $rootDirectory, [StringComparison]::OrdinalIgnoreCase)
    $prefix = $rootDirectory + [IO.Path]::DirectorySeparatorChar
    if (-not $isRoot -and -not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { Issue 'markdown-link'; return }
    $normalized = if ($isRoot) { '' } else { $full.Substring($prefix.Length).Replace('\', '/') }
    if (UnsafePath $normalized) { Issue 'markdown-link'; return }
    $isFile = $tracked.Contains($normalized)
    $isDirectory = if ($isRoot) { $tracked.Count -gt 0 } else {
        @($tracked | Where-Object { $_.StartsWith($normalized.TrimEnd('/') + '/', [StringComparison]::Ordinal) }).Count -gt 0
    }
    if (-not $isFile -and -not $isDirectory) { Issue 'markdown-link'; return }
    if ($split.Length -eq 2 -and $split[1].Length -gt 0 -and $normalized.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
        if (-not $texts.ContainsKey($normalized) -or -not (MarkdownAnchors $texts[$normalized]).Contains($split[1])) { Issue 'markdown-link' }
    }
}
try {
    $shallow = (RunGit @('rev-parse', '--is-shallow-repository')).Trim()
    if ($shallow -ne 'false') { Issue 'history-incomplete' }
    $tracked = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $texts = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($path in (RunGit @('ls-files', '-z')).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        [void]$tracked.Add($path)
        if (UnsafePath $path) { Issue 'tracked-runtime-path'; continue }
        if (ReparseInPath $path) { Issue 'tracked-reparse-path'; continue }
        $full = Join-Path $root $path
        if (-not [IO.File]::Exists($full)) { Issue 'tracked-file-missing'; continue }
        $text = [IO.File]::ReadAllText($full)
        if (HasSecret $text) { Issue 'tracked-secret' }
        if ($path.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) { $texts[$path] = $text }
    }
    # All refs, including deleted files in reachable commits. No reflogs/unreachable objects.
    $historyPaths = (RunGit @('log', '--all', '--format=', '--name-only', '-z')).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($path in ($historyPaths | ForEach-Object { $_.Trim([char[]]@("`r", "`n")) } | Sort-Object -Unique)) {
        if (UnsafePath $path) { Issue 'history-runtime-path' }
    }
    $objects = (RunGit @('rev-list', '--objects', '--all', '--no-object-names')).Trim()
    if ($objects.Length -eq 0) { Issue 'history-incomplete' }
    else {
        # Preserve raw object boundaries before BOM-aware text decoding.
        ScanHistory ($objects.Split([char[]]@("`r", "`n"), [StringSplitOptions]::RemoveEmptyEntries))
    }
    $identities = @((RunGit @('log', '--all', '--format=%ae%n%ce')).Split("`n") |
        ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '^[^@\s]+@(?:users\.)?noreply\.github\.com$' } | Sort-Object -Unique)
    if ($identities.Count -gt 0) { Write-Output "WARNING: Non-noreply identities: $($identities.Count); owner review pending." }
    else { Write-Output 'Non-noreply identities: 0.' }
    foreach ($entry in $texts.GetEnumerator()) {
        if ($entry.Value -match '(?i)(?<![A-Za-z0-9])[A-Z]:[\\/]') { Issue 'markdown-machine-path' }
        # Fenced examples are prose, not active Markdown dependencies.
        $markdown = MarkdownContent $entry.Value
        $targets = [Collections.Generic.List[string]]::new()
        foreach ($link in [regex]::Matches($markdown, '!?\[[^\]\r\n]*\]\(\s*(?:<(?<target>[^>]+)>|(?<target>[^\s)]+))(?:\s+["''][^)]*)?\s*\)')) { $targets.Add($link.Groups['target'].Value) }
        foreach ($link in [regex]::Matches($markdown, '(?m)^ {0,3}\[[^\]]+\]:\s*(?:<(?<target>[^>]+)>|(?<target>\S+))')) { $targets.Add($link.Groups['target'].Value) }
        foreach ($link in [regex]::Matches($markdown, '(?i)(?:href|src)\s*=\s*["''](?<target>[^"'']+)["'']')) { $targets.Add($link.Groups['target'].Value) }
        foreach ($target in $targets) { CheckLink $entry.Key $target $tracked $texts }
    }
    Write-Output "Checked tracked files: $($tracked.Count); reachable-history scope: all local refs."
} catch {
    # Do not emit exception text: Git errors can contain paths or sensitive content.
    Issue 'check-error'
}
foreach ($entry in ($script:issues.GetEnumerator() | Sort-Object Key)) { Write-Output "FAIL: $($entry.Key): $($entry.Value)." }
if ($script:issues.Count -gt 0) { exit 1 }
Write-Output 'PASS: publication checks (known signatures, runtime paths, Markdown portability; complete reachable local history).'
exit 0
