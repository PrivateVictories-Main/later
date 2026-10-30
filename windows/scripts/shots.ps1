# Renders the sample window in each scene of the list below and saves one PNG per scene (and, on x64, a second one from a plain
# screen copy, named <scene>-screen.png, which shows the Mica backdrop). Debug builds only: the flags live in Sample.cs.
# A scene whose window a later task builds makes the app exit with 3; it is skipped, not failed.
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$data = Join-Path $env:RUNNER_TEMP 'later-shots-data'
New-Item -ItemType Directory -Force $data | Out-Null
$env:LATER_DATA_DIR = $data   # a sample run touches no data; this only guarantees it could not reach a real folder

$sections = 'all', 'shopping', 'posts', 'school', 'reading', 'videos', 'tools', 'other', 'older', 'done', 'regulars'
$scenes = [ordered]@{
    'library-light'         = @('--sample')
    'library-dark'          = @('--sample', '--sample-dark')
    'site-group-dark'       = @('--sample', '--sample-dark', '--sample-group', 'site')
    'kind-group-light'      = @('--sample', '--sample-group', 'kind')
    'sort-title-light'      = @('--sample', '--sample-sort', 'title')
    'tab-order-light'       = @('--sample', '--sample-sort', 'tabOrder')
    'search-light'          = @('--sample', '--sample-search', 'walnut')
    'search-results-light'  = @('--sample', '--sample-search', 'apple')
    'narrow-light'          = @('--sample', '--shot-size', '900x600')
    'narrow-dark'           = @('--sample', '--sample-dark', '--shot-size', '900x600')
    'selection-light'       = @('--sample', '--sample-select', '2')
    'settings-light'        = @('--sample', '--sample-settings')
    'onboarding-light'      = @('--sample-onboarding')
}
foreach ($s in $sections) {
    if ($s -eq 'all') { continue }
    $scenes["section-$s-light"] = @('--sample', '--sample-section', $s)
    $scenes["section-$s-dark"] = @('--sample', '--sample-dark', '--sample-section', $s)
}

# Mica needs the screen itself (x64 only): a window that fits the runner's small display, without PrintWindow's picture.
if ($Arch -eq 'x64') {
    foreach ($theme in 'light', 'dark') {
        $p = Start-Process $Exe -ArgumentList (@('--sample') + $(if ($theme -eq 'dark') { @('--sample-dark') } else { @() }) + @('--shot-size', '1000x700', '--shot-screen', (Join-Path $Out "mica-$theme.png"))) -PassThru
        $null = $p.Handle
        if (-not $p.WaitForExit(40000)) { $p.Kill($true); throw "mica-$theme did not finish in 40 s" }
        if ($p.ExitCode -ne 0) { throw "mica-$theme exited with $($p.ExitCode)" }
    }
}

$made = 0; $skipped = @()
foreach ($name in $scenes.Keys) {
    $png = Join-Path $Out "$name.png"
    $arguments = @($scenes[$name]) + @('--shot', $png)
    if ($Arch -eq 'x64') { $arguments += @('--shot-screen', (Join-Path $Out "$name-screen.png")) }
    $p = Start-Process $Exe -ArgumentList $arguments -PassThru
    $null = $p.Handle   # keeps the exit code readable after the process ends
    if (-not $p.WaitForExit(40000)) {
        $p.Kill($true)
        throw "scene '$name' did not finish in 40 s"
    }
    if ($p.ExitCode -eq 3) { $skipped += $name; continue }
    if ($p.ExitCode -ne 0) { throw "scene '$name' exited with $($p.ExitCode)" }
    foreach ($file in @($png) + $(if ($Arch -eq 'x64') { @((Join-Path $Out "$name-screen.png")) } else { @() })) {
        if (-not (Test-Path $file)) { throw "scene '$name' left no $(Split-Path $file -Leaf)" }
        if ((Get-Item $file).Length -lt 5KB) { throw "scene '$name': $(Split-Path $file -Leaf) is under 5 KB" }
    }
    $made++
}
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
Write-Host "SHOTS made=$made skipped=$($skipped.Count) ($($skipped -join ', ')) arch=$Arch"
if ($made -eq 0) { throw 'no scene produced a picture' }
