# Renders the sample window in each scene of the list below and saves one PNG per scene (and, on x64, a second one from a plain
# screen copy, named <scene>-screen.png, which shows the Mica backdrop). Debug builds only: the flags live in Sample.cs.
# A scene whose window a later task builds (settings, onboarding) makes the app exit with 3; it is skipped, not failed.
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
    'selection-light'       = @('--sample', '--sample-select', '1')
    'selection-dark'        = @('--sample', '--sample-dark', '--sample-select', '1')
    'multi-light'           = @('--sample', '--sample-select', '3')
    'multi-dark'            = @('--sample', '--sample-dark', '--sample-select', '3')
    'regular-selected-light' = @('--sample', '--sample-section', 'regulars', '--sample-select', '1')
    'regular-selected-dark' = @('--sample', '--sample-dark', '--sample-section', 'regulars', '--sample-select', '1')
    'site-selected-dark'    = @('--sample', '--sample-dark', '--sample-group', 'site', '--sample-select', '1')
    'narrow-selected-light' = @('--sample', '--sample-select', '1', '--shot-size', '900x600')
    'settings-light'        = @('--sample', '--sample-settings')
    'onboarding-light'      = @('--sample-onboarding')
}
foreach ($s in $sections) {
    if ($s -eq 'all') { continue }
    $scenes["section-$s-light"] = @('--sample', '--sample-section', $s)
    $scenes["section-$s-dark"] = @('--sample', '--sample-dark', '--sample-section', $s)
}

# Mica needs the screen itself (x64 only): a window that fits the runner's small display, without PrintWindow's picture.
# What a scene that does not finish looks like, for the failure message: whether it answers, whether the picture was made, and what the
# system logged about it.
function Stop-Stuck($p, [string]$name, [string]$png) {
    $made = if ($png) { Test-Path $png } else { 'n/a' }
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-2) } -MaxEvents 6 -ErrorAction SilentlyContinue |
        ForEach-Object { "$($_.ProviderName): $(($_.Message -split "`n")[0])" })
    $info = "responding=$($p.Responding) window='$($p.MainWindowTitle)' picture=$made"
    $p.Kill($true)
    throw "scene '$name' did not finish in 40 s ($info) $($events -join ' | ')"
}

if ($Arch -eq 'x64') {
    foreach ($theme in 'light', 'dark') {
        $p = Start-Process $Exe -ArgumentList (@('--sample') + $(if ($theme -eq 'dark') { @('--sample-dark') } else { @() }) + @('--shot-size', '1000x700', '--shot-screen', (Join-Path $Out "mica-$theme.png"))) -PassThru
        $null = $p.Handle
        if (-not $p.WaitForExit(40000)) { Stop-Stuck $p "mica-$theme" (Join-Path $Out "mica-$theme.png") }
        if ($p.ExitCode -ne 0) { throw "mica-$theme exited with $($p.ExitCode)" }
    }
}

$made = 0; $skipped = @()
foreach ($name in $scenes.Keys) {
    $png = Join-Path $Out "$name.png"
    $arguments = @($scenes[$name]) + @('--sample-now', '2026-10-15T12:00:00', '--shot', $png)   # the runners' time zones differ, so "today" would too
    if ($Arch -eq 'x64') { $arguments += @('--shot-screen', (Join-Path $Out "$name-screen.png")) }
    $p = Start-Process $Exe -ArgumentList $arguments -PassThru
    $null = $p.Handle   # keeps the exit code readable after the process ends
    if (-not $p.WaitForExit(40000)) { Stop-Stuck $p $name $png }
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
