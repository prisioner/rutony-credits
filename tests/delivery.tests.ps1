param([string]$Repository = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$module = 'RutonyCredits'
$native = 'rutony_credits'
$web = @('web/control.css','web/control.html','web/control.js','web/font-picker.js','web/local-font.js','web/overlay.css','web/overlay.html','web/overlay.js','web/playback.js')
$extras = @('manual-test.md','obs-automation.md','Открыть титры.url','Открыть титры.html')
$paths = @('LICENSE', "$module.dll", "$native.cs", "$native.json", 'install.ps1') + $web + $extras
if (Test-Path (Join-Path $Repository 'scripts/package-contract.ps1')) { $paths += 'package-contract.ps1' }
$root = Join-Path $Repository ('.runtime/delivery-tests/' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$passed = 0
$failed = @()
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Destination($path) {
    if ($path -in @("$native.cs", "$native.json")) { return "scripts/$path" }
    if ($path -eq 'LICENSE' -or $path -eq "$module.dll" -or $path.StartsWith('web/')) { return "scripts/$module/$path" }
    return $null
}
function New-Fixture([string]$PackageName = 'package') {
    $case = Join-Path $root ([Guid]::NewGuid().ToString('N').Substring(0,8))
    $pkg = Join-Path $case $PackageName
    $hostDir = Join-Path $case 'host'
    New-Item -ItemType Directory -Path $pkg,$hostDir -Force | Out-Null
    Set-Content -Encoding UTF8 (Join-Path $hostDir 'RutonyChat.dll') 'synthetic host'
    $records = foreach ($path in $paths) {
        $file = Join-Path $pkg $path
        New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        if ($path -in @('install.ps1','package-contract.ps1')) {
            Microsoft.PowerShell.Management\Copy-Item -LiteralPath (Join-Path $Repository "scripts/$path") -Destination $file
        } else { Set-Content -Encoding UTF8 -LiteralPath $file -Value "new $path" -NoNewline }
        $destination = Destination $path
        if ($destination) {
            $old = Join-Path $hostDir $destination
            New-Item -ItemType Directory -Path (Split-Path -Parent $old) -Force | Out-Null
            Set-Content -Encoding UTF8 -LiteralPath $old -Value "old $path" -NoNewline
        }
        [ordered]@{ path=$path; length=(Get-Item -LiteralPath $file).Length; sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); install=$destination }
    }
    [ordered]@{schema=1; module=$module; revision=('1' * 40); files=@($records)} | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $pkg 'package-manifest.json')
    Set-Content -Encoding UTF8 (Join-Path $hostDir "scripts/$module/unowned.txt") 'keep this'
    [pscustomobject]@{ Package=$pkg; Host=$hostDir; Records=$records }
}
function Assert-Old($fixture) {
    foreach ($entry in $fixture.Records) {
        if ($entry.install) { Assert ((Get-Content -Raw (Join-Path $fixture.Host $entry.install)) -ceq "old $($entry.path)") "Mixed installation: $($entry.path)" }
    }
    Assert ((Get-Content -Raw (Join-Path $fixture.Host "scripts/$module/unowned.txt")).Trim() -eq 'keep this') 'Unowned file lost'
}
function Run-Install($fixture, [switch]$Recover) {
    function Get-Process { param($Name,$ErrorAction) return @() }
    if ($Recover) { & (Join-Path $fixture.Package 'install.ps1') -RutonyDirectory $fixture.Host -Recover }
    else { & (Join-Path $fixture.Package 'install.ps1') -RutonyDirectory $fixture.Host }
}
function Expect-Failure([scriptblock]$action) {
    $caught = $false
    try { & $action | Out-Null } catch { $caught = $true }
    Assert $caught 'Expected failure'
}
function Case($name, [scriptblock]$action) {
    try { & $action; $script:passed++; Write-Output "PASS $name" }
    catch { $script:failed += "$name : $_"; Write-Output "FAIL $name : $_" }
}
Case 'complete bracketed package validates every member and installs' {
    $f=New-Fixture 'p [1]'
    . (Join-Path $Repository 'scripts/package-contract.ps1')
    $manifest=Read-DeliveryManifest $f.Package
    Assert (@($manifest.files).Count -eq $paths.Count) 'Bracketed manifest incomplete'
    Run-Install $f | Out-Null
    foreach ($entry in $f.Records) {
        if ($entry.install) { Assert ((Get-FileHash -LiteralPath (Join-Path $f.Host $entry.install)).Hash -eq $entry.sha256) "Wrong installed $($entry.path)" }
    }
    Assert (Test-Path -LiteralPath (Join-Path $f.Host "scripts/$module/unowned.txt")) 'Unowned file lost'
}
Case 'versioned schema 1 package installs with the unchanged target layout' {
    $f=New-Fixture
    $path=Join-Path $f.Package 'package-manifest.json'
    $manifest=Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json
    $manifest | Add-Member -NotePropertyName version -NotePropertyValue '1.0.0-rc.1'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $path
    Run-Install $f | Out-Null
    foreach ($entry in $f.Records) {
        if ($entry.install) { Assert ((Get-FileHash -LiteralPath (Join-Path $f.Host $entry.install)).Hash -eq $entry.sha256) "Wrong installed $($entry.path)" }
    }
}
Case 'damaged bracketed package fails hash preflight before target mutation' {
    $f=New-Fixture 'p [1]'
    Add-Content -LiteralPath (Join-Path $f.Package $web[-1]) -Value 'damage'
    $message=''
    try { Run-Install $f | Out-Null } catch { $message="$_" }
    Assert ($message -match 'hash mismatch') "Expected hash preflight failure: $message"
    Assert-Old $f
    Assert (-not (Test-Path -LiteralPath (Join-Path $f.Host "scripts/.$module-install"))) 'Preflight mutated target'
}
Case 'missing late package member leaves every target unchanged' {
    $f=New-Fixture
    Remove-Item -LiteralPath (Join-Path $f.Package $web[-1])
    Expect-Failure { Run-Install $f }
    Assert-Old $f
}
Case 'hash mismatch leaves every target unchanged' {
    $f=New-Fixture
    Add-Content -LiteralPath (Join-Path $f.Package $web[-1]) -Value 'damage'
    Expect-Failure { Run-Install $f }
    Assert-Old $f
}
Case 'manifest traversal is rejected' {
    $f=New-Fixture
    $manifest = Get-Content -Raw (Join-Path $f.Package 'package-manifest.json') | ConvertFrom-Json
    $manifest.files[-1].path = '../escape'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $f.Package 'package-manifest.json')
    Expect-Failure { Run-Install $f }
    Assert-Old $f
}
Case 'unexpected package file is rejected' {
    $f=New-Fixture
    Set-Content -Encoding UTF8 (Join-Path $f.Package 'private-token.txt') 'synthetic'
    Expect-Failure { Run-Install $f }
    Assert-Old $f
}
Case 'locked late destination fails without mutation' {
    $f=New-Fixture
    $handle=[IO.File]::Open((Join-Path $f.Host (Destination $web[-1])), 'Open','ReadWrite','None')
    try { Expect-Failure { Run-Install $f } } finally { $handle.Dispose() }
    Assert-Old $f
}
Case 'failure midway through commit restores DLL web native JSON and LICENSE' {
    $f=New-Fixture
    $faultDestination=Join-Path $f.Host (Destination "$native.json")
    function Copy-Item {
        param($LiteralPath,$Destination,[switch]$Force)
        if ($Destination -eq $faultDestination -and $LiteralPath -like '*stage*') { throw 'fixture commit copy failure' }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
    }
    Expect-Failure { Run-Install $f }
    Assert-Old $f
    Assert (@(Get-ChildItem (Join-Path $f.Host "scripts/.$module-install") -Filter snapshot.json -Recurse).Count -eq 1) 'Consistent backup missing'
}
Case 'rollback failure is visible, retains backup and recovery blocks regular install' {
    $f=New-Fixture
    $faultDestination=Join-Path $f.Host (Destination "$native.json")
    function Copy-Item {
        param($LiteralPath,$Destination,[switch]$Force)
        if (($Destination -eq $faultDestination -and $LiteralPath -like '*stage*') -or ($Destination -eq (Join-Path $f.Host (Destination "$module.dll")) -and $LiteralPath -like '*backup*')) { throw 'fixture rollback failure' }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
    }
    $message=''
    try { Run-Install $f | Out-Null } catch { $message="$_" }
    Assert ($message -match 'ROLLBACK FAILED') "Rollback failure hidden: $message"
    Assert (Test-Path (Join-Path $f.Host "scripts/.$module-install/active.json")) 'Recovery marker missing'
    Expect-Failure { Run-Install $f }
    Remove-Item Function:Copy-Item
    Run-Install $f -Recover | Out-Null
    Assert-Old $f
}
Case 'successful update preserves unowned data and coherent backups' {
    $f=New-Fixture
    Run-Install $f | Out-Null
    foreach ($entry in $f.Records) {
        if ($entry.install) { Assert ((Get-FileHash (Join-Path $f.Host $entry.install)).Hash -eq $entry.sha256) "Wrong installed $($entry.path)" }
    }
    Assert (Test-Path (Join-Path $f.Host "scripts/$module/unowned.txt")) 'Unowned file lost'
    Assert (-not (Test-Path (Join-Path $f.Host "scripts/.$module-install/active.json"))) 'Success left active marker'
}
Case 'target junction rejected before mutation' {
    $f=New-Fixture
    $outside=Join-Path $root ([Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $outside | Out-Null
    Move-Item -LiteralPath (Join-Path $f.Host "scripts/$module/web") -Destination (Join-Path $outside 'web')
    New-Item -ItemType Junction -Path (Join-Path $f.Host "scripts/$module/web") -Target (Join-Path $outside 'web') | Out-Null
    Expect-Failure { Run-Install $f }
    Assert-Old $f
}

Case 'first installation succeeds and failed first commit removes only new owned files' {
    $f=New-Fixture
    foreach ($entry in $f.Records) { if ($entry.install) { Remove-Item -LiteralPath (Join-Path $f.Host $entry.install) } }
    $faultDestination=Join-Path $f.Host (Destination "$native.json")
    function Copy-Item {
        param($LiteralPath,$Destination,[switch]$Force)
        if ($Destination -eq $faultDestination -and $LiteralPath -like '*stage*') { throw 'fixture first install failure' }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
    }
    Expect-Failure { Run-Install $f }
    foreach ($entry in $f.Records) { if ($entry.install) { Assert (-not (Test-Path (Join-Path $f.Host $entry.install))) "New file survived rollback: $($entry.path)" } }
    Remove-Item Function:Copy-Item
    Run-Install $f | Out-Null
    foreach ($entry in $f.Records) { if ($entry.install) { Assert ((Get-FileHash (Join-Path $f.Host $entry.install)).Hash -eq $entry.sha256) 'First install mismatch' } }
}
Case 'process crash during commit leaves recovery marker and complete old backup' {
    $f=New-Fixture
    $crashScript=Join-Path (Split-Path $f.Package -Parent) 'crash.ps1'
    @'
param($Package,$HostDirectory,$Module)
$ErrorActionPreference='Stop'
function Get-Process { param($Name,$ErrorAction) return @() }
function Copy-Item {
    param($LiteralPath,$Destination,[switch]$Force)
    if ($LiteralPath -like '*stage*' -and $Destination.EndsWith("$Module.dll")) { [Environment]::Exit(86) }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
& (Join-Path $Package 'install.ps1') -RutonyDirectory $HostDirectory
'@ | Set-Content -Encoding UTF8 -LiteralPath $crashScript
    & pwsh -NoProfile -File $crashScript $f.Package $f.Host $module
    Assert ($LASTEXITCODE -eq 86) 'Fixture did not interrupt commit'
    Assert (Test-Path (Join-Path $f.Host "scripts/.$module-install/active.json")) 'Crash marker missing'
    Expect-Failure { Run-Install $f }
    Run-Install $f -Recover | Out-Null
    Assert-Old $f
}
Case 'duplicate or omitted manifest members are rejected' {
    $f=New-Fixture
    $manifest=Get-Content -LiteralPath (Join-Path $f.Package 'package-manifest.json') -Raw | ConvertFrom-Json
    $manifest.files[-1]=$manifest.files[0]
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $f.Package 'package-manifest.json')
    Expect-Failure { Run-Install $f }
    Assert-Old $f
}
Case 'package source junction rejected before mutation' {
    $f=New-Fixture
    $outside=Join-Path $root ([Guid]::NewGuid().ToString('N').Substring(0,8))
    Move-Item -LiteralPath (Join-Path $f.Package 'web') -Destination $outside
    New-Item -ItemType Junction -Path (Join-Path $f.Package 'web') -Target $outside | Out-Null
    Expect-Failure { Run-Install $f }
    Assert-Old $f
}
Case 'builder creates exact manifest and refuses unexpected source web file' {
    $fakeRepo=Join-Path $root ([Guid]::NewGuid().ToString('N').Substring(0,8) + '[1]')
    New-Item -ItemType Directory -Path (Join-Path $fakeRepo 'scripts') -Force | Out-Null
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath (Join-Path $Repository 'scripts/package.ps1'),(Join-Path $Repository 'scripts/package-contract.ps1') -Destination (Join-Path $fakeRepo 'scripts')
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath (Join-Path $Repository 'Directory.Build.props') -Destination (Join-Path $fakeRepo 'Directory.Build.props')
    foreach ($path in $paths) {
        if ($path -eq "$module.dll") { $source="src/$module/bin/Release/net9.0/$path" }
        elseif ($path.StartsWith('web/')) { $source="src/$module/$path" }
        elseif ($path -in @("$native.cs","$native.json")) { $source="integration/$path" }
        elseif ($path -in @('install.ps1','package-contract.ps1')) { $source="scripts/$path" }
        elseif ($path.EndsWith('.md')) { $source="docs/$path" }
        elseif ($path.EndsWith('.url') -or $path.EndsWith('.html')) { $source="launcher/$path" }
        else { $source=$path }
        $file=Join-Path $fakeRepo $source
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        if ($path -ne 'package-contract.ps1') { Set-Content -Encoding UTF8 -LiteralPath $file -Value "synthetic $path" }
    }
    & (Join-Path $fakeRepo 'scripts/package.ps1') -SourceRevision ('1' * 40) | Out-Null
    $built=Join-Path $fakeRepo "dist/$module"
    $manifest=Get-Content -LiteralPath (Join-Path $built 'package-manifest.json') -Raw | ConvertFrom-Json
    Assert (@($manifest.files).Count -eq $paths.Count) 'Manifest incomplete'
    Assert ('package-manifest.json' -notin $manifest.files.path) 'Manifest includes recursive self hash'
    Assert (@(Get-ChildItem -LiteralPath $built -Recurse -File).Count -eq $paths.Count+1) 'Unexpected package file'
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $fakeRepo "src/$module/web/untracked-secret.txt") -Value 'synthetic'
    Expect-Failure { & (Join-Path $fakeRepo 'scripts/package.ps1') -SourceRevision ('1' * 40) }
    Assert (-not (Test-Path (Join-Path $built 'web/untracked-secret.txt'))) 'Untracked source leaked'
}

Case 'staging failure leaves complete previous installation untouched' {
    $f=New-Fixture
    function Copy-Item {
        param($LiteralPath,$Destination,[switch]$Force)
        if ($Destination -like '*stage*' -and $LiteralPath.EndsWith("$module.dll")) { throw 'fixture stage failure' }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
    }
    Expect-Failure { Run-Install $f }
    Assert-Old $f
    Assert (-not (Test-Path (Join-Path $f.Host "scripts/.$module-install/active.json"))) 'Stage failure created active commit marker'
}
Case 'backup failure occurs before any owned target changes' {
    $f=New-Fixture
    function Copy-Item {
        param($LiteralPath,$Destination,[switch]$Force)
        if ($Destination -like '*backup*' -and $LiteralPath.EndsWith("$native.json")) { throw 'fixture backup failure' }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
    }
    Expect-Failure { Run-Install $f }
    Assert-Old $f
}

Write-Output "Delivery fixtures: $passed passed, $($failed.Count) failed. Synthetic artifacts: $root"
if ($failed.Count) { throw ($failed -join [Environment]::NewLine) }
