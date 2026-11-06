param([string]$Repository = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. (Join-Path $Repository 'scripts/package-contract.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Keep nested synthetic checkouts within Windows PowerShell 5.1 path limits.
$root = Join-Path $Repository ('.runtime/release-tests/' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$passed = 0
$failed = @()
$revision = '1' * 40
$version = '1.2.3-rc.4'
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:passed++; Write-Output "PASS $Name" }
    catch { $script:failed += "$Name : $_"; Write-Output "FAIL $Name : $_" }
}
function Expect-Failure([scriptblock]$Action) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true }
    Assert $caught 'Expected rejection of invalid candidate'
}
function New-Fixture {
    $repo = Join-Path $root ([Guid]::NewGuid().ToString('N').Substring(0,8) + ' [1]')
    $scripts = Join-Path $repo 'scripts'
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    foreach ($name in @('package.ps1','package-contract.ps1','package-release.ps1')) {
        $source = Join-Path $Repository "scripts/$name"
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $scripts }
    }
    [IO.File]::WriteAllText((Join-Path $repo 'Directory.Build.props'), '<Project><PropertyGroup><Version>1.2.3-rc.4</Version></PropertyGroup></Project>')
    foreach ($entry in Get-DeliveryLayout) {
        $path = $entry.path
        if ($path -eq "$DeliveryModule.dll") { $source = "src/$DeliveryModule/bin/Release/net9.0/$path" }
        elseif ($path.StartsWith('web/')) { $source = "src/$DeliveryModule/$path" }
        elseif ($path -in @("$DeliveryNative.cs","$DeliveryNative.json")) { $source = "integration/$path" }
        elseif ($path -in @('install.ps1','package-contract.ps1')) { $source = "scripts/$path" }
        elseif ($path.EndsWith('.md')) { $source = "docs/$path" }
        elseif ($path.EndsWith('.url') -or $path.EndsWith('.html')) { $source = "launcher/$path" }
        else { $source = $path }
        $file = Join-Path $repo $source
        New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        if ($path -ne 'package-contract.ps1') { [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes("synthetic bytes: $path")) }
    }
    & (Join-Path $scripts 'package.ps1') -SourceRevision $revision | Out-Null
    [pscustomobject]@{ Repo=$repo; Package=(Join-Path $repo "dist/$DeliveryModule"); Dist=(Join-Path $repo 'dist'); Script=(Join-Path $scripts 'package-release.ps1') }
}
function Read-Package($Fixture) { Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Fixture.Package 'package-manifest.json') | ConvertFrom-Json }
function Write-Package($Fixture, $Manifest) { [IO.File]::WriteAllText((Join-Path $Fixture.Package 'package-manifest.json'), ($Manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) }
function Release($Fixture, [switch]$Verify) {
    Assert (Test-Path -LiteralPath $Fixture.Script) 'Versioned release packaging is not implemented'
    & $Fixture.Script -SourceRevision $revision -Verify:$Verify | Out-Null
}
function Read-Release($Fixture) { Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Fixture.Dist 'release-manifest.json') | ConvertFrom-Json }
function Write-Release($Fixture, $Manifest) { [IO.File]::WriteAllText((Join-Path $Fixture.Dist 'release-manifest.json'), ($Manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) }
function Archive-Path($Fixture) { Join-Path $Fixture.Dist "$DeliveryModule-$version-$revision.zip" }
function Refresh-ArchiveHash($Fixture) {
    $manifest = Read-Release $Fixture
    $archive = Archive-Path $Fixture
    $manifest.archive.length = (Get-Item -LiteralPath $archive).Length
    $manifest.archive.sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Release $Fixture $manifest
}
Case 'new package version follows the fixture props instead of a hardcoded candidate' {
    $f = New-Fixture
    $m = Read-Package $f
    Assert ($m.PSObject.Properties['version'] -and $m.version -ceq '1.2.3-rc.4') 'Package version must come from Directory.Build.props'
}
Case 'schema 1 package without version remains readable' {
    $f = New-Fixture
    $m = Read-Package $f
    $m.PSObject.Properties.Remove('version')
    Write-Package $f $m
    $null = Read-DeliveryManifest $f.Package
}
Case 'schema 1 valid optional version remains readable' {
    $f = New-Fixture
    $m = Read-Package $f
    $m | Add-Member -NotePropertyName version -NotePropertyValue '2.0.0-beta.1' -Force
    Write-Package $f $m
    $null = Read-DeliveryManifest $f.Package
}
foreach ($bad in @('', '01.2.3', '1.2', '1.2.3-01', '1.2.3/escape', '1.2.3+metadata', $null, 7)) {
    Case "invalid optional version rejected [$bad]" {
        $f = New-Fixture
        $m = Read-Package $f
        $m | Add-Member -NotePropertyName version -NotePropertyValue $bad -Force
        Write-Package $f $m
        Expect-Failure { Read-DeliveryManifest $f.Package }
    }
}
Case 'optional version rejects a trailing LF' {
    $f = New-Fixture
    $m = Read-Package $f
    $m | Add-Member -NotePropertyName version -NotePropertyValue "1.2.3`n" -Force
    Write-Package $f $m
    Expect-Failure { Read-DeliveryManifest $f.Package }
}
Case 'release ZIP entries are byte identical to every validated package member' {
    $f = New-Fixture
    Release $f
    Release $f -Verify
    $r = Read-Release $f
    Assert ($r.version -ceq '1.2.3-rc.4' -and $r.revision -ceq $revision -and $r.module -ceq $DeliveryModule) 'Release identity lost'
    Assert ($r.tools.dotnet -match '^\d+\.\d+\.\d+' -and $r.tools.node -match '^v\d+\.\d+\.\d+$' -and $r.tools.powershell -match '^\d+\.\d+') 'Tool versions missing'
    Assert ($r.packageManifest.sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $f.Package 'package-manifest.json')).Hash.ToLowerInvariant()) 'Package manifest binding lost'
    $zip = [IO.Compression.ZipFile]::OpenRead((Archive-Path $f))
    try {
        $files = @(Get-ChildItem -LiteralPath $f.Package -File -Recurse)
        Assert ($zip.Entries.Count -eq $files.Count) 'ZIP has wrong file count'
        foreach ($file in $files) {
            $name = $file.FullName.Substring($f.Package.Length + 1).Replace('\','/')
            $entry = $zip.GetEntry($name)
            Assert ($null -ne $entry) "Missing archive member $name"
            $stream = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try { $stream.CopyTo($memory); Assert ([Convert]::ToBase64String($memory.ToArray()) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))) "ZIP bytes differ: $name" }
            finally { $stream.Dispose(); $memory.Dispose() }
        }
    } finally { $zip.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath (Archive-Path $f)).Hash
    $metadata = [IO.File]::ReadAllBytes((Join-Path $f.Dist 'release-manifest.json'))
    Get-ChildItem -LiteralPath $f.Package -File -Recurse | ForEach-Object { $_.LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(5) }
    Release $f
    Assert ((Get-FileHash -LiteralPath (Archive-Path $f)).Hash -ceq $zipHash) 'ZIP depends on source timestamps'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $f.Dist 'release-manifest.json'))) -ceq [Convert]::ToBase64String($metadata)) 'Release metadata is nondeterministic'
}
foreach ($change in @('missing-version','wrong-version','wrong-revision','missing-revision','missing-hash','missing-file','mutated-payload','extra-file')) {
    Case "release creation rejects package $change" {
        $f = New-Fixture
        $m = Read-Package $f
        switch ($change) {
            'missing-version' { $m.PSObject.Properties.Remove('version'); Write-Package $f $m }
            'wrong-version' { $m | Add-Member -NotePropertyName version -NotePropertyValue '9.9.9' -Force; Write-Package $f $m }
            'wrong-revision' { $m.revision = '2' * 40; Write-Package $f $m }
            'missing-revision' { $m.PSObject.Properties.Remove('revision'); Write-Package $f $m }
            'missing-hash' { $m.files[0].PSObject.Properties.Remove('sha256'); Write-Package $f $m }
            'missing-file' { [IO.File]::Move((Join-Path $f.Package 'LICENSE'), (Join-Path $f.Repo 'removed-license')) }
            'mutated-payload' { [IO.File]::AppendAllText((Join-Path $f.Package 'LICENSE'), 'mutation') }
            'extra-file' { [IO.File]::WriteAllText((Join-Path $f.Package 'unapproved.txt'), 'unapproved') }
        }
        Assert (Test-Path -LiteralPath $f.Script) 'Versioned release packaging is not implemented'
        Expect-Failure { Release $f }
        Assert (-not (Test-Path -LiteralPath (Join-Path $f.Dist 'release-manifest.json'))) 'Invalid input published metadata'
    }
}
foreach ($change in @('extra','missing','duplicate','traversal','mutated')) {
    Case "release verification rejects ZIP $change even with updated archive hash" {
        $f = New-Fixture
        Release $f
        $zip = [IO.Compression.ZipFile]::Open((Archive-Path $f), [IO.Compression.ZipArchiveMode]::Update)
        try {
            switch ($change) {
                'extra' { $null = $zip.CreateEntry('unapproved.txt') }
                'missing' { $zip.GetEntry('LICENSE').Delete() }
                'duplicate' { $null = $zip.CreateEntry('LICENSE') }
                'traversal' { $zip.GetEntry('LICENSE').Delete(); $null = $zip.CreateEntry('../LICENSE') }
                'mutated' { $entry = $zip.GetEntry('LICENSE'); $entry.Delete(); $entry = $zip.CreateEntry('LICENSE'); $stream = $entry.Open(); try { $stream.WriteByte(7) } finally { $stream.Dispose() } }
            }
        } finally { $zip.Dispose() }
        Refresh-ArchiveHash $f
        Expect-Failure { Release $f -Verify }
    }
}
foreach ($change in @('version','revision','archive-hash','archive-path','manifest-hash','tool-path')) {
    Case "release verification rejects metadata $change" {
        $f = New-Fixture
        Release $f
        $m = Read-Release $f
        switch ($change) {
            'version' { $m.version = '9.9.9' }
            'revision' { $m.revision = '2' * 40 }
            'archive-hash' { $m.archive.sha256 = '0' * 64 }
            'archive-path' { $m.archive.path = '../candidate.zip' }
            'manifest-hash' { $m.packageManifest.sha256 = '0' * 64 }
            'tool-path' { $m.tools.dotnet = 'C:/machine/private/sdk' }
        }
        Write-Release $f $m
        Expect-Failure { Release $f -Verify }
    }
}
Write-Output "Release fixtures: $passed passed, $($failed.Count) failed. Synthetic artifacts: $root"
if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
