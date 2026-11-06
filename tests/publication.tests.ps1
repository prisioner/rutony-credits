# Dependency-free fixtures; all identities and signatures below are synthetic.
param([string]$CaseFilter = '*')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path $PSScriptRoot -Parent
$checker = Join-Path $sourceRoot 'scripts/check-publication.ps1'
$isCredits = Test-Path (Join-Path $sourceRoot 'src/RutonyCredits')
$runtimeNames = if ($isCredits) { @('settings.json', 'current.json', 'obs.json', 'obs-secret.bin', 'writer.lock', 'sessions/example.jsonl', 'workshopitem.meta') } else { @('settings.json', 'credentials.bin', 'deliveries.jsonl', 'ledger.version', 'ledger.initialized', 'workshopitem.meta') }
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('publication-fixtures-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$script:passed = 0
$script:failed = 0

function Git([string]$root, [string[]]$arguments) {
    $output = & git.exe -C $root @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Git fixture command failed.' }
    return $output
}
function WriteFixture([string]$root, [string]$path, [string]$content) {
    $file = Join-Path $root $path
    [void][IO.Directory]::CreateDirectory((Split-Path $file -Parent))
    [IO.File]::WriteAllText($file, $content, [Text.UTF8Encoding]::new($false))
}
function CommitFixture([string]$root) {
    $null = Git $root @('add', '--all')
    $null = Git $root @('-c', 'user.name=Synthetic Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '-m', 'synthetic fixture')
}
function NewFixture([string]$name) {
    $root = Join-Path $fixtureRoot $name
    [void][IO.Directory]::CreateDirectory($root)
    $null = Git $root @('init', '--quiet')
    if (Test-Path $checker) {
        WriteFixture $root 'scripts/check-publication.ps1' ([IO.File]::ReadAllText($checker))
    } else {
        # RED baseline: accepting everything must fail the negative cases.
        WriteFixture $root 'scripts/check-publication.ps1' "Write-Output 'No publication checks implemented.'; exit 0"
    }
    WriteFixture $root 'README.md' "# Fixture`n`n[Guide](docs/guide.md#clean-heading)`n"
    WriteFixture $root 'docs/guide.md' "# Clean heading`n`n[Home](../README.md)`n"
    CommitFixture $root
    return $root
}
function RunChecker([string]$root) {
    $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/check-publication.ps1') 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Output = $output }
}
function AssertCheck([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Case([string]$name, [scriptblock]$body) {
    if ($name -notlike $CaseFilter) { return }
    try { & $body; $script:passed++; Write-Output "PASS: $name" }
    catch { $script:failed++; Write-Output "FAIL: $name ($($_.Exception.Message))" }
}

foreach ($bigEndian in @($false, $true)) {
    $order = if ($bigEndian) { 'BE' } else { 'LE' }
    Case "UTF-16$order BOM current and deleted signatures fail with sanitized diagnostics" {
        $root = NewFixture ("utf16-" + $order)
        $fake = ('gh' + 'p_') + ('V' * 36)
        $encoding = [Text.UnicodeEncoding]::new($bigEndian, $true)
        [IO.File]::WriteAllText((Join-Path $root 'encoded-value.txt'), "synthetic prefix`r`n$fake`r`n", $encoding)
        # Empty and binary objects exercise batch boundaries independently of text encoding.
        WriteFixture $root 'empty.txt' ''
        [IO.File]::WriteAllBytes((Join-Path $root 'boundary.bin'), [byte[]]@(0, 10, 255, 254, 0, 10, 13, 0))
        CommitFixture $root
        $current = RunChecker $root
        AssertCheck ($current.Code -ne 0 -and $current.Output -match 'tracked-secret') 'BOM-encoded tracked signature accepted.'
        AssertCheck (-not $current.Output.Contains($fake) -and -not $current.Output.Contains('encoded-value.txt')) 'Tracked diagnostic disclosed sensitive value or path.'
        [IO.File]::Delete((Join-Path $root 'encoded-value.txt'))
        CommitFixture $root
        $deleted = RunChecker $root
        AssertCheck ($deleted.Code -ne 0 -and $deleted.Output -match 'history-secret') 'BOM-encoded deleted signature accepted.'
        AssertCheck ($deleted.Output -notmatch 'PASS: publication') 'Deleted BOM signature received complete-history PASS.'
        AssertCheck (-not $deleted.Output.Contains($fake) -and -not $deleted.Output.Contains('encoded-value.txt')) 'History diagnostic disclosed sensitive value or path.'
    }
}
Case 'root README links to tracked repository root are portable' {
    $root = NewFixture 'root-directory-link'
    WriteFixture $root 'README.md' "[Repository](.)`n[Repository with slash](./)`n"
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -eq 0) 'Root directory link rejected.'
}
Case 'nested document links to tracked repository root are portable' {
    $root = NewFixture 'nested-root-directory-link'
    WriteFixture $root 'docs/guide.md' "# Clean heading`n[Repository](..)`n[Repository with slash](../)`n"
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -eq 0) 'Nested repository-root link rejected.'
}
Case 'nested document link beyond repository root is rejected' {
    $root = NewFixture 'nested-escaping-directory-link'
    WriteFixture $root 'docs/guide.md' "# Clean heading`n[Outside](../../)`n"
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -ne 0 -and $result.Output -match 'markdown-link') 'True directory escape accepted.'
}
Case 'fenced-only headings and explicit HTML anchors do not create anchors' {
    $root = NewFixture 'fenced-only-anchors'
    $fence = [string]::new([char]96, 3)
    WriteFixture $root 'README.md' "[Hidden](docs/guide.md#hidden)`n[Hidden HTML](docs/guide.md#hidden-html)`n"
    WriteFixture $root 'docs/guide.md' ("# Clean heading`n`n$fence" + "text`n# Hidden`n<a id='hidden-html'></a>`n$fence`n")
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -ne 0 -and $result.Output -match 'markdown-link: 2\.') 'Fenced examples fabricated rendered anchors.'
}
Case 'real same-name heading after a fence has no phantom duplicate suffix' {
    $root = NewFixture 'fenced-duplicate-heading'
    $fence = [string]::new([char]96, 3)
    WriteFixture $root 'README.md' '[Visible](docs/guide.md#visible)'
    WriteFixture $root 'docs/guide.md' ("$fence" + "text`n# Visible`n$fence`n`n# Visible`n")
    CommitFixture $root
    $positive = RunChecker $root
    AssertCheck ($positive.Code -eq 0) 'Real heading after fenced example rejected.'
    WriteFixture $root 'README.md' '[Phantom duplicate](docs/guide.md#visible-1)'
    CommitFixture $root
    $negative = RunChecker $root
    AssertCheck ($negative.Code -ne 0 -and $negative.Output -match 'markdown-link') 'Fenced example shifted duplicate heading numbering.'
}
Case 'clean placeholders, relative anchors and reference links; ignored synthetic data unread' {
    $root = NewFixture 'clean'
    WriteFixture $root '.gitignore' ".env`n.local/`n"
    WriteFixture $root '.env' (('gh' + 'p_') + ('Z' * 36))
    WriteFixture $root '.local/private.md' '[outside](../../outside.md)'
    WriteFixture $root 'docs/guide.md' "# Clean heading`n`n[Home][home]`n`n[home]: ../README.md`n`nclient_secret = '<your-secret>'`naccess_token = 'example'`n"
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -eq 0) 'Clean repository rejected.'
    AssertCheck (-not $result.Output.Contains('System.Threading.Tasks.')) 'Unexpected task result in sanitized diagnostics.'
    AssertCheck ($result.Output -match 'Non-noreply identities: 1; owner review pending') 'Missing sanitized owner-review warning.'
    AssertCheck (-not $result.Output.Contains('fixture@example.invalid')) 'Identity leaked.'
}
Case 'historical deleted known secret rejected without revealing its value' {
    $root = NewFixture 'history-secret'
    $fake = ('gh' + 'p_') + ('Q' * 36)
    WriteFixture $root 'removed.txt' $fake
    CommitFixture $root
    [IO.File]::Delete((Join-Path $root 'removed.txt'))
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -ne 0 -and $result.Output -match 'history-secret') 'Deleted historical secret accepted.'
    AssertCheck (-not $result.Output.Contains($fake)) 'Signature leaked.'
}
Case 'modified tracked content scanned' {
    $root = NewFixture 'working-secret'
    WriteFixture $root 'tracked.txt' 'placeholder'
    CommitFixture $root
    WriteFixture $root 'tracked.txt' (('AK' + 'IA') + ('X' * 16))
    $result = RunChecker $root
    AssertCheck ($result.Code -ne 0 -and $result.Output -match 'tracked-secret') 'Modified tracked secret accepted.'
}
Case 'private key signature rejected' {
    $root = NewFixture 'private-key'
    WriteFixture $root 'key.txt' ('-----BEGIN ' + 'PRIVATE KEY-----')
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -ne 0 -and $result.Output -match 'secret') 'Private key accepted.'
}
Case 'all module runtime names rejected, including deleted history' {
    foreach ($name in $runtimeNames) {
        $root = NewFixture ('runtime-' + [guid]::NewGuid().ToString('N'))
        WriteFixture $root $name 'synthetic runtime data'
        CommitFixture $root
        $result = RunChecker $root
        AssertCheck ($result.Code -ne 0 -and $result.Output -match 'runtime-path') 'Runtime path accepted.'
        [IO.File]::Delete((Join-Path $root $name))
        CommitFixture $root
        $result = RunChecker $root
        AssertCheck ($result.Code -ne 0 -and $result.Output -match 'history-runtime-path') 'Deleted runtime path accepted.'
    }
}
Case 'escaping Markdown path rejected' {
    $root = NewFixture 'escaping-link'
    WriteFixture $root 'README.md' '[outside](../outside.md)'
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -ne 0 -and $result.Output -match 'markdown-link') 'Escaping link accepted.'
}
Case 'machine and private-file Markdown dependencies rejected' {
    foreach ($target in @(('C' + ':\Users\fixture\private.md'), ('file' + ':///private.md'), '.local/private.md', '.runtime/result.md', '/outside.md', '//server/share.md')) {
        $root = NewFixture ('private-link-' + [guid]::NewGuid().ToString('N'))
        WriteFixture $root 'README.md' ("[outside]($target)")
        CommitFixture $root
        $result = RunChecker $root
        AssertCheck ($result.Code -ne 0 -and $result.Output -match 'markdown-link') 'Private or absolute link accepted.'
    }
}
Case 'untracked target, missing anchor and machine path prose rejected' {
    foreach ($content in @('[missing](docs/missing.md)', '[missing](docs/guide.md#absent)', ('Machine location: ' + 'D' + ':\Work\private'))) {
        $root = NewFixture ('missing-' + [guid]::NewGuid().ToString('N'))
        WriteFixture $root 'README.md' $content
        CommitFixture $root
        $result = RunChecker $root
        AssertCheck ($result.Code -ne 0) 'Nonportable documentation accepted.'
    }
}
Case 'interrupted runtime temporary and recovery copies are covered narrowly' {
    $root = NewFixture 'runtime-copies'
    $copies = if ($isCredits) { @('settings.json.tmp', 'current.json.tmp', ('settings.json.recovery-' + ('a' * 32)), ('current.json.recovery-' + ('b' * 32)), ('obs.json.tmp-' + ('c' * 32)), ('obs.json.recovery-' + ('d' * 32)), ('obs-secret.bin.tmp-' + ('e' * 32)), ('obs-secret.bin.recovery-' + ('f' * 32)), ('obs-secret.bin.rollback-' + ('1' * 32))) } else { @('settings.json.tmp', 'credentials.bin.tmp') }
    foreach ($name in $copies) { WriteFixture $root $name 'synthetic interrupted runtime copy' }
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -ne 0 -and $result.Output -match "tracked-runtime-path: $($copies.Count)\.") 'Runtime copies accepted.'
    foreach ($name in $copies) { [IO.File]::Delete((Join-Path $root $name)) }
    CommitFixture $root
    $result = RunChecker $root
    AssertCheck ($result.Code -ne 0 -and $result.Output -match "history-runtime-path: $($copies.Count)\.") 'Historical runtime copies accepted.'
    WriteFixture $root '.gitignore' ([IO.File]::ReadAllText((Join-Path $sourceRoot '.gitignore')))
    foreach ($name in $copies) {
        & git.exe -C $root check-ignore --no-index --quiet -- $name
        AssertCheck ($LASTEXITCODE -eq 0) 'Runtime copy is not ignored.'
    }
    & git.exe -C $root check-ignore --no-index --quiet -- 'docs/settings.json.description.md'
    AssertCheck ($LASTEXITCODE -eq 1) 'Unrelated file hidden by runtime-copy policy.'
}
Case 'ignore rules cover runtime names without hiding example or manifest JSON' {
    $root = NewFixture 'ignore-policy'
    WriteFixture $root '.gitignore' ([IO.File]::ReadAllText((Join-Path $sourceRoot '.gitignore')))
    foreach ($name in $runtimeNames) {
        & git.exe -C $root check-ignore --no-index --quiet -- $name
        AssertCheck ($LASTEXITCODE -eq 0) 'Owned runtime path is not ignored.'
    }
    foreach ($name in @('.env.example', 'docs/settings-example.json', 'integration/example.json', 'package-manifest.json', 'docs/workshop/metadata.json', 'sample.meta', 'src/example.cs')) {
        & git.exe -C $root check-ignore --no-index --quiet -- $name
        AssertCheck ($LASTEXITCODE -eq 1) 'Legitimate source or example is ignored.'
    }
}
Case 'shallow repository fails explicitly without full-history success' {
    $root = NewFixture 'full-source'
    WriteFixture $root 'second.txt' 'clean'
    CommitFixture $root
    $shallow = Join-Path $fixtureRoot 'shallow'
    $uri = ([uri]($root.Replace('\', '/') + '/')).AbsoluteUri
    $null = Git $fixtureRoot @('clone', '--quiet', '--depth=1', $uri, $shallow)
    $result = RunChecker $shallow
    AssertCheck ($result.Code -ne 0 -and $result.Output -match 'history-incomplete') 'Shallow history accepted as complete.'
    AssertCheck ($result.Output -notmatch 'PASS: publication') 'False complete PASS.'
}
Write-Output "Publication fixtures: $script:passed passed; $script:failed failed."
Write-Output "Synthetic evidence: $fixtureRoot"
Write-Output 'Only synthetic local Git fixtures were used; no service or application acceptance.'
if ($script:failed -gt 0) { exit 1 }
