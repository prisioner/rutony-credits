param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+\z')]
    [string]$RuntimeVersion,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$projects = @('RutonyCredits.Checks','RutonyCredits.NativeChecks')
$props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)
$frameworkNames = @($props.SelectNodes('/Project/PropertyGroup/TargetFramework') | ForEach-Object { $_.InnerText } | Sort-Object -Unique)
if ($frameworkNames.Count -ne 1) { throw 'No single target framework in Directory.Build.props.' }
$targetFramework = $frameworkNames[0]
$inventory = @(& dotnet --list-runtimes)
if ($LASTEXITCODE -ne 0) { throw 'Unable to list installed runtimes.' }
$runRoot = Join-Path $repo ('.runtime/runtime-compatibility/' + $RuntimeVersion + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Push-Location $repo
try {
    foreach ($project in $projects) {
        $output = Join-Path $repo ('tests/' + $project + '/bin/' + $Configuration + '/' + $targetFramework)
        $assembly = Join-Path $output ($project + '.dll')
        $original = Join-Path $output ($project + '.runtimeconfig.json')
        if (-not (Test-Path -LiteralPath $assembly -PathType Leaf) -or -not (Test-Path -LiteralPath $original -PathType Leaf)) {
            throw 'Build checks first with ./scripts/build.ps1 -Check.'
        }
        $config = Get-Content -LiteralPath $original -Raw | ConvertFrom-Json
        $config.runtimeOptions | Add-Member -NotePropertyName rollForward -NotePropertyValue 'Disable' -Force
        $frameworks = @()
        if ($config.runtimeOptions.framework) { $frameworks += $config.runtimeOptions.framework }
        if ($config.runtimeOptions.frameworks) { $frameworks += $config.runtimeOptions.frameworks }
        if ($frameworks.Count -eq 0) { throw 'No shared frameworks in test runtimeconfig.' }
        foreach ($framework in $frameworks) {
            $prefix = $framework.name + ' ' + $RuntimeVersion + ' '
            if (-not @($inventory | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) }).Count) {
                throw ('Required installed runtime is missing: ' + $framework.name + ' ' + $RuntimeVersion)
            }
            $framework.version = $RuntimeVersion
        }
        $temporary = Join-Path $runRoot ($project + '.runtimeconfig.json')
        [IO.File]::WriteAllText($temporary, ($config | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
        & dotnet exec --roll-forward Disable --runtimeconfig $temporary $assembly
        if ($LASTEXITCODE -ne 0) { throw ('Runtime checks failed: ' + $project + ' on ' + $RuntimeVersion) }
        Write-Output ('PASS ' + $project + ' on explicitly selected runtime ' + $RuntimeVersion)
    }
} finally { Pop-Location }
