param([string]$Repository = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. (Join-Path $Repository 'scripts/package-contract.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Join-Path $Repository ('.runtime/github-release-tests/' + [Guid]::NewGuid().ToString('N').Substring(0,8))
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
function New-Fixture {
    $repo = Join-Path $root ([Guid]::NewGuid().ToString('N').Substring(0,8) + ' [1]')
    $scripts = Join-Path $repo 'scripts'
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null
    foreach ($name in @('package-contract.ps1','prepare-github-release.ps1')) {
        $source = Join-Path $Repository "scripts/$name"
        Assert (Test-Path -LiteralPath $source) "Release helper missing: $name"
        Copy-Item -LiteralPath $source -Destination $scripts
    }
    [IO.File]::WriteAllText((Join-Path $repo 'Directory.Build.props'), '<Project><PropertyGroup><Version>1.2.3-rc.4</Version></PropertyGroup></Project>')
    $inputDir = Join-Path $repo 'artifact'
    $payload = Join-Path $repo 'payload'
    New-Item -ItemType Directory -Path $inputDir,$payload -Force | Out-Null
    $files = @(foreach ($entry in Get-DeliveryLayout) {
        $file = Join-DeliveryPath $payload $entry.path
        New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        [IO.File]::WriteAllText($file, "synthetic $($entry.path)",[Text.UTF8Encoding]::new($false))
        [ordered]@{path=$entry.path;install=$entry.install;length=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()}
    })
    Write-DeliveryJson (Join-Path $payload 'package-manifest.json') ([ordered]@{schema=1;module=$DeliveryModule;version=$version;revision=$revision;files=$files})
    $archive = "$DeliveryModule-$version-$revision.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($payload,(Join-Path $inputDir $archive))
    $f=[pscustomobject]@{Repo=$repo;Input=$inputDir;Output=(Join-Path $repo 'output');Archive=$archive;Script=(Join-Path $scripts 'prepare-github-release.ps1')}
    $meta=[ordered]@{schema=1;module=$DeliveryModule;version=$version;revision=$revision;archive=[ordered]@{path=$archive;length=0;sha256=''};packageManifest=[ordered]@{path='package-manifest.json';sha256=(Get-FileHash -LiteralPath (Join-Path $payload 'package-manifest.json')).Hash.ToLowerInvariant()}}
    Write-DeliveryJson (Join-Path $inputDir 'release-manifest.json') $meta
    Refresh-Hash $f
    return $f
}
function Read-Meta($F) { Get-Content -Raw -LiteralPath (Join-Path $F.Input 'release-manifest.json') | ConvertFrom-Json }
function Save-Meta($F,$M) { [IO.File]::WriteAllText((Join-Path $F.Input 'release-manifest.json'), ($M | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) }
function Refresh-Hash($F) {
    $m=Read-Meta $F
    $m.archive.length=(Get-Item -LiteralPath (Join-Path $F.Input $F.Archive)).Length
    $m.archive.sha256=(Get-FileHash -LiteralPath (Join-Path $F.Input $F.Archive)).Hash.ToLowerInvariant()
    Save-Meta $F $m
}
function Prepare($F,[string]$Tag="v$version",[string]$Revision=$revision) {
    & $F.Script -Tag $Tag -SourceRevision $Revision -RepositorySlug 'sample-owner/sample-repo' -ArtifactDirectory $F.Input -OutputDirectory $F.Output | Out-Null
}
function Reject([scriptblock]$Action,[string]$Pattern) {
    $caught=$false
    try { & $Action | Out-Null } catch { $caught=$true; Assert ($_.ToString() -match $Pattern) "Unexpected rejection: $_" }
    Assert $caught 'Invalid release was accepted'
}
Case 'stage friendly ZIP name, unchanged package bytes and consistent metadata' {
    $f=New-Fixture
    $publicName="$DeliveryModule-$version.zip"
    $originalManifestHash=(Get-FileHash -LiteralPath (Join-Path $f.Input 'release-manifest.json')).Hash
    $original=Read-Meta $f
    # Additional Workshop material is retained by CI but must not become release assets.
    [IO.File]::WriteAllText((Join-Path $f.Input 'not-an-asset.txt'),'synthetic')
    Prepare $f
    $actual=@(Get-ChildItem -LiteralPath $f.Output -File | ForEach-Object Name | Sort-Object)
    $expected=@($publicName,'release-manifest.json','SHA256SUMS','release-notes.md') | Sort-Object
    Assert (($actual -join '|') -ceq ($expected -join '|')) 'Unexpected release file set or unfriendly ZIP name'
    Assert ((Get-FileHash -LiteralPath (Join-Path $f.Output $publicName)).Hash -ceq (Get-FileHash -LiteralPath (Join-Path $f.Input $f.Archive)).Hash) 'ZIP bytes changed during rename'
    $published=Get-Content -Raw -LiteralPath (Join-Path $f.Output 'release-manifest.json')|ConvertFrom-Json
    Assert ($published.archive.path -ceq $publicName) 'Published archive path was not updated'
    Assert ($published.archive.sha256 -ceq $original.archive.sha256 -and $published.archive.length -eq $original.archive.length) 'Published archive identity changed'
    Assert ($published.revision -ceq $original.revision -and $published.version -ceq $original.version -and $published.packageManifest.sha256 -ceq $original.packageManifest.sha256) 'Source/package binding changed'
    Assert ((Get-FileHash -LiteralPath (Join-Path $f.Input 'release-manifest.json')).Hash -ceq $originalManifestHash) 'Producer manifest was modified'
    foreach($name in @($publicName,'release-manifest.json')) {
        $sum=(Get-FileHash -LiteralPath (Join-Path $f.Output $name)).Hash.ToLowerInvariant()
        Assert ((Get-Content -Raw -LiteralPath (Join-Path $f.Output 'SHA256SUMS')).Contains("$sum  $name")) 'Missing updated checksum'
    }
    $notes=Get-Content -Raw -LiteralPath (Join-Path $f.Output 'release-notes.md')
    Assert ($notes.Contains("https://github.com/sample-owner/sample-repo/blob/$revision/README.md")) 'Installation link is not revision-pinned and absolute'
    Assert ($notes -notmatch '\]\((?:\.\.?/|docs/)') 'Relative release link'
}
Case 'already friendly assets can be validated and staged again' {
    $f=New-Fixture
    $publicName="$DeliveryModule-$version.zip"
    Move-Item -LiteralPath (Join-Path $f.Input $f.Archive) -Destination (Join-Path $f.Input $publicName)
    $m=Read-Meta $f;$m.archive.path=$publicName;Save-Meta $f $m
    Prepare $f
    Assert ((Get-FileHash -LiteralPath (Join-Path $f.Output $publicName)).Hash -ceq (Get-FileHash -LiteralPath (Join-Path $f.Input $publicName)).Hash) 'Published ZIP bytes changed'
}
Case 'wrong tag is rejected' { $f=New-Fixture; Reject { Prepare $f -Tag 'v9.9.9' } 'tag' }
Case 'another built revision is rejected' { $f=New-Fixture; Reject { Prepare $f -Revision ('2'*40) } 'identity' }
Case 'another module is rejected' { $f=New-Fixture;$m=Read-Meta $f;$m.module='Other';Save-Meta $f $m;Reject {Prepare $f} 'identity' }
Case 'another version is rejected' { $f=New-Fixture;$m=Read-Meta $f;$m.version='9.9.9';Save-Meta $f $m;Reject {Prepare $f} 'identity' }
Case 'unsafe archive name is rejected' { $f=New-Fixture;$m=Read-Meta $f;$m.archive.path='../other.zip';Save-Meta $f $m;Reject {Prepare $f} 'archive' }
Case 'modified archive is rejected' { $f=New-Fixture;[IO.File]::AppendAllText((Join-Path $f.Input $f.Archive),'changed');Reject {Prepare $f} 'hash|length' }
Case 'missing archive is rejected' { $f=New-Fixture;Move-Item -LiteralPath (Join-Path $f.Input $f.Archive) -Destination (Join-Path $f.Input 'hidden');Reject {Prepare $f} 'archive' }
Case 'wrong package manifest binding is rejected' { $f=New-Fixture;$m=Read-Meta $f;$m.packageManifest.sha256='0'*64;Save-Meta $f $m;Reject {Prepare $f} 'manifest' }
Case 'ZIP traversal is rejected even with updated outer checksum' {
    $f=New-Fixture;$z=[IO.Compression.ZipFile]::Open((Join-Path $f.Input $f.Archive),[IO.Compression.ZipArchiveMode]::Update)
    try { $entry=$z.CreateEntry('../outside.txt');$w=[IO.StreamWriter]::new($entry.Open());$w.Write('synthetic');$w.Dispose() } finally {$z.Dispose()}
    Refresh-Hash $f;Reject {Prepare $f} 'member|allowlist'
    Assert (-not (Test-Path -LiteralPath (Join-Path $f.Repo 'outside.txt'))) 'Traversal escaped validation'
}
Case 'modified inner file is rejected even with updated outer checksum' {
    $f=New-Fixture;$z=[IO.Compression.ZipFile]::Open((Join-Path $f.Input $f.Archive),[IO.Compression.ZipArchiveMode]::Update)
    try { $z.GetEntry('LICENSE').Delete();$entry=$z.CreateEntry('LICENSE');$w=[IO.StreamWriter]::new($entry.Open());$w.Write('changed');$w.Dispose() } finally {$z.Dispose()}
    Refresh-Hash $f;Reject {Prepare $f} 'hash|missing'
}
Case 'existing output is preserved without overwrite' {
    $f=New-Fixture;New-Item -ItemType Directory -Path $f.Output | Out-Null
    $sentinel=Join-Path $f.Output 'sentinel';[IO.File]::WriteAllText($sentinel,'preserve')
    Reject {Prepare $f} 'output';Assert ([IO.File]::ReadAllText($sentinel) -ceq 'preserve') 'Output was modified'
}
Write-Output "GitHub release fixtures: $passed passed; $($failed.Count) failed."
if ($failed.Count) { throw ($failed -join "`n") }
