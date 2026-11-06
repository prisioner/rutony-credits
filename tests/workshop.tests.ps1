param([string]$Repository = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. (Join-Path $Repository 'scripts/package-contract.ps1')
$root = Join-DeliveryPath $Repository ('.runtime/workshop-tests/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$passed = 0
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Expect-Failure([scriptblock]$action) {
    $failed = $false
    try { & $action | Out-Null } catch { $failed = $true }
    Assert $failed 'Expected failure'
}
function Fixture([switch]$Versioned) {
    $repo = Join-Path $root ('Сборка [1]-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory((Join-Path $repo 'scripts')) | Out-Null
    foreach ($name in @('prepare-workshop.ps1','package-contract.ps1')) {
        Copy-Item -LiteralPath (Join-Path $Repository "scripts/$name") -Destination (Join-Path $repo "scripts/$name")
    }
    foreach ($relative in @('docs/workshop/README.md','docs/workshop/description.txt','docs/workshop/metadata.json','assets/workshop/preview.svg','assets/workshop/preview.png')) {
        $target = Join-Path $repo $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Repository $relative) -Destination $target
    }
    $package = Join-Path $repo "dist/$DeliveryModule"
    $records = foreach ($entry in Get-DeliveryLayout) {
        $file = Join-Path $package $entry.path
        [IO.Directory]::CreateDirectory((Split-Path -Parent $file)) | Out-Null
        [IO.File]::WriteAllText($file, "synthetic $($entry.path)", [Text.UTF8Encoding]::new($false))
        [ordered]@{path=$entry.path; install=$entry.install; length=(Get-Item -LiteralPath $file).Length; sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    $manifest = [ordered]@{schema=1; module=$DeliveryModule; revision=('1'*40); files=@($records)}
    if ($Versioned) { $manifest.version = '1.0.0-rc.1' }
    Write-DeliveryJson (Join-Path $package 'package-manifest.json') $manifest
    return $repo
}
function Run($repo) { & (Join-Path $repo 'scripts/prepare-workshop.ps1') }
function Payload($repo) { Join-Path $repo "dist/Workshop/$DeliveryModule" }
function Materials($repo) { Join-Path $repo "dist/WorkshopMaterials/$DeliveryModule" }
function Snapshot($directory) {
    @((Get-DeliveryFiles $directory) | Sort-Object | ForEach-Object {
        $_.Substring($directory.Length).Replace('\','/') + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }) -join "`n"
}
function Case($name, [scriptblock]$action) {
    & $action
    $script:passed++
    Write-Output "PASS $name"
}
Case 'schema1 legacy source, exact payload and relative hashes, no private/publisher/installer files' {
    $repo = Fixture
    Run $repo
    $payload = Payload $repo
    $expected = @('LICENSE') + @(Get-DeliveryLayout | Where-Object install | ForEach-Object { $_.install.Substring(8) })
    $actual = @(Get-DeliveryFiles $payload | ForEach-Object { $_.Substring($payload.Length+1).Replace('\','/') })
    Assert (@(Compare-Object $expected $actual -CaseSensitive).Count -eq 0) 'Unexpected payload layout'
    $materials = Materials $repo
    $expectedMaterials = @('README.md','description.txt','metadata.json','preview.svg','preview.png','workshop-manifest.json')
    $actualMaterials = @(Get-DeliveryFiles $materials | ForEach-Object { $_.Substring($materials.Length+1).Replace('\','/') })
    Assert (@(Compare-Object $expectedMaterials $actualMaterials -CaseSensitive).Count -eq 0) 'Unexpected materials layout'
    $manifest = Get-Content -LiteralPath (Join-Path $materials 'workshop-manifest.json') -Raw | ConvertFrom-Json
    Assert ($manifest.revision -ceq ('1'*40)) 'Revision lost'
    Assert ($null -eq $manifest.PSObject.Properties['version']) 'Invented version for legacy package'
    Assert (@($manifest.files).Count -eq $expected.Count) 'Manifest file count mismatch'
    foreach ($entry in $manifest.files) {
        Assert (-not [IO.Path]::IsPathRooted($entry.path) -and $entry.path -notmatch '\\|\.\.') 'Nonrelative manifest path'
        $file = Join-Path $payload $entry.path
        Assert ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ieq $entry.sha256) 'Wrong staged hash'
        Assert ((Get-Item -LiteralPath $file).Length -eq $entry.length) 'Wrong staged length'
    }
}
Case 'optional source version preserved and prior staging retained outside deliverables' {
    $repo = Fixture -Versioned
    Run $repo
    $oldPayload = Snapshot (Payload $repo)
    [IO.File]::WriteAllText((Join-Path (Payload $repo) 'old-unowned.txt'), 'preserve previous staging')
    Run $repo
    Assert ((Snapshot (Payload $repo)) -ceq $oldPayload) 'Stale member entered new payload'
    $manifest = Get-Content -LiteralPath (Join-Path (Materials $repo) 'workshop-manifest.json') -Raw | ConvertFrom-Json
    Assert ($manifest.version -ceq '1.0.0-rc.1') 'Optional version lost'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $repo '.runtime/workshop-backups') -Recurse -Filter old-unowned.txt).Count -eq 1) 'Previous staging was not preserved'
}
foreach ($bad in @('corrupt','missing','extra','publisher','pdb')) {
    Case "$bad source rejected without changing previous staging" {
        $repo = Fixture
        Run $repo
        $before = Snapshot (Join-Path $repo 'dist/Workshop')
        $beforeMaterials = Snapshot (Join-Path $repo 'dist/WorkshopMaterials')
        $package = Join-Path $repo "dist/$DeliveryModule"
        switch ($bad) {
            corrupt { [IO.File]::AppendAllText((Join-Path $package "$DeliveryModule.dll"), 'tamper') }
            missing { Remove-Item -LiteralPath (Join-Path $package "$DeliveryNative.json") }
            extra { [IO.File]::WriteAllText((Join-Path $package 'settings.json'), 'synthetic') }
            publisher { [IO.File]::WriteAllText((Join-Path $package 'workshopitem.meta'), 'synthetic') }
            pdb { [IO.File]::WriteAllText((Join-Path $package "$DeliveryModule.pdb"), 'synthetic') }
        }
        Expect-Failure { Run $repo }
        Assert ((Snapshot (Join-Path $repo 'dist/Workshop')) -ceq $before) 'Previous payload changed on rejected source'
        Assert ((Snapshot (Join-Path $repo 'dist/WorkshopMaterials')) -ceq $beforeMaterials) 'Previous materials changed on rejected source'
    }
}
Case 'invalid preview rejected before replacing previous staging' {
    $repo = Fixture
    Run $repo
    $before = Snapshot (Join-Path $repo 'dist/Workshop')
    [IO.File]::WriteAllText((Join-Path $repo 'assets/workshop/preview.png'), 'not a PNG')
    Expect-Failure { Run $repo }
    Assert ((Snapshot (Join-Path $repo 'dist/Workshop')) -ceq $before) 'Invalid preview changed payload'
}
Case 'file at materials destination is rejected without changing payload' {
    $repo = Fixture
    $payload = Payload $repo
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'preserve.txt'), 'keep')
    [IO.Directory]::CreateDirectory((Split-Path -Parent (Materials $repo))) | Out-Null
    [IO.File]::WriteAllText((Materials $repo), 'existing user file')
    $before = Snapshot $payload
    Expect-Failure { Run $repo }
    Assert ((Snapshot $payload) -ceq $before) 'Blocked materials path mutated old payload'
    Assert ([IO.File]::ReadAllText((Materials $repo)) -ceq 'existing user file') 'Blocked materials file lost'
}
Case 'second promotion failure restores both previous outputs' {
    $repo = Fixture
    Run $repo
    [IO.File]::WriteAllText((Join-Path (Payload $repo) 'previous-payload.txt'), 'keep payload')
    [IO.File]::WriteAllText((Join-Path (Materials $repo) 'previous-materials.txt'), 'keep materials')
    $before = Snapshot (Join-Path $repo 'dist/Workshop')
    $beforeMaterials = Snapshot (Join-Path $repo 'dist/WorkshopMaterials')
    $failureState = [pscustomobject]@{Fired=$false; Target=(Materials $repo)}
    function Move-Item {
        param($LiteralPath,$Destination)
        if (-not $failureState.Fired -and $Destination -eq $failureState.Target -and $LiteralPath -match 'workshop-stage') {
            $failureState.Fired = $true
            throw 'Synthetic second promotion failure'
        }
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
    }
    Expect-Failure { Run $repo }
    Assert $failureState.Fired 'Fault injection did not reach second promotion'
    Assert ((Snapshot (Join-Path $repo 'dist/Workshop')) -ceq $before) 'Payload rollback failed'
    Assert ((Snapshot (Join-Path $repo 'dist/WorkshopMaterials')) -ceq $beforeMaterials) 'Materials rollback failed'
}
Write-Output "PASS $passed Workshop preparation checks"
