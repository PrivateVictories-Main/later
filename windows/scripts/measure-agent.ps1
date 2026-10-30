# Gate G2: the background agent's cold cost. Starts the published Release agent (no browser open, onboarding done) and checks
# its private memory, then, after the agent has settled, its CPU and its wake-ups over a quiet stretch. Then the warm figure:
# the same agent after it has read a real browser window (UI Automation loaded), reported with no gate; over $WarmAlertMB private
# memory is an alert line. Prints counts only. The data folder is the ephemeral runner's own.
#
# The agent prints each wake-up by kind (LATER_AGENT_TRACE=1; nothing in a normal run), so the window can say what woke it:
#   own timers   deadline, retry, loadretry, save      the agent started these itself: any one fails the gate
#   derived      debounce, post, history, visitdays    work that follows something else: fails the gate when nothing external arrived
#   external     winevent, session                     the system's events (foreground changes, lock, power): reported, never a failure
# CPU over $MaxCpuMs fails only in a window with no external event. With external events the CPU is divided by their count
# and over $MaxCpuMsPerEvent each fails: an event costs the agent a lookup of one window's process (well under a millisecond
# once warm), and the first one also pays for compiling that path, so 10 ms each is generous for the real cost and still catches
# an agent that does real work per event.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$Seconds = 30,
    [double]$MaxPrivateMB = 15.0,
    [double]$MaxCpuMs = 50,
    [double]$MaxCpuMsPerEvent = 10,
    [int]$SettleSeconds = 5,
    [double]$WarmAlertMB = 25.0,
    [string]$ReadyToRunExe = '',
    [switch]$Experiment
)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
$dir = Join-Path $env:LOCALAPPDATA 'Later'
New-Item -ItemType Directory -Force $dir | Out-Null
Set-Content (Join-Path $dir 'settings.json') '{"onboarded":true}' -NoNewline

function Read-Lines([string]$path) {
    try { return @(Get-Content $path -ErrorAction Stop) } catch { return @() }
}
function Start-Agent([string]$name, [hashtable]$vars = @{}, [string]$exe = $exePath) {
    $out = Join-Path $env:RUNNER_TEMP "agent-$name.txt"
    foreach ($k in $vars.Keys) { Set-Item "Env:$k" $vars[$k] }
    try { $proc = Start-Process $exe -ArgumentList '--agent' -PassThru -RedirectStandardOutput $out }
    finally { foreach ($k in $vars.Keys) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue } }
    return [pscustomobject]@{ Proc = $proc; Out = $out; Started = Get-Date }
}
function Stop-Agent($a) {
    Stop-Process -Id $a.Proc.Id -Force -ErrorAction SilentlyContinue
    $a.Proc.WaitForExit(10000) | Out-Null
    Remove-Item $a.Out -Force -ErrorAction SilentlyContinue
}
# Settled: the engine has started and its first History question has been answered (the agent says both), plus a few seconds
# for the compiling that follows them. Returns the seconds it took from the start.
function Wait-Settled($a) {
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        $a.Proc.Refresh()
        if ($a.Proc.HasExited) { throw "the agent exited early with code $($a.Proc.ExitCode)" }
        $lines = Read-Lines $a.Out
        $up = @($lines | Where-Object { $_ -like 'TRACE engine started*' }).Count -gt 0
        $asked = @($lines | Where-Object { $_ -like 'EVT VisitDays *' }).Count -gt 0
        if ($up -and $asked) { break }
        Start-Sleep -Milliseconds 250
    }
    Start-Sleep -Seconds $SettleSeconds
    $a.Proc.Refresh()
    if ($a.Proc.HasExited) { throw "the agent exited early with code $($a.Proc.ExitCode)" }
    return ((Get-Date) - $a.Started).TotalSeconds
}
function Get-ThreadCpu($proc) {
    $proc.Refresh()
    $h = @{}
    foreach ($t in $proc.Threads) { try { $h[$t.Id] = $t.TotalProcessorTime.TotalMilliseconds } catch { } }
    return $h
}

# 1. Memory as shipped: a plain run, nothing listening.
$coldFailure = $null
$plain = Start-Agent 'plain'
try {
    Start-Sleep -Seconds 8
    $plain.Proc.Refresh()
    if ($plain.Proc.HasExited) { throw "the agent exited early with code $($plain.Proc.ExitCode)" }
    $privateMB = $plain.Proc.PrivateMemorySize64 / 1MB
    Write-Host ('AGENT cold plain private={0:N1} working={1:N1} threads={2} handles={3}' -f $privateMB, ($plain.Proc.WorkingSet64 / 1MB), $plain.Proc.Threads.Count, $plain.Proc.HandleCount)
    if ($privateMB -gt $MaxPrivateMB) { $coldFailure = "gate G2 failed: private memory $([math]::Round($privateMB, 1)) MB is over $MaxPrivateMB MB" }
}
finally { Stop-Agent $plain }

# 2. The gate: a settled agent over a quiet window, with its wake-ups counted by kind.
$traced = Start-Agent 'traced' @{ LATER_AGENT_TRACE = '1' }
try {
    $settledAfter = Wait-Settled $traced
    $p = $traced.Proc
    $cpu0 = $p.TotalProcessorTime.TotalMilliseconds
    $threads0 = Get-ThreadCpu $p
    $lines0 = (Read-Lines $traced.Out).Count
    Start-Sleep -Seconds $Seconds
    $p.Refresh()
    if ($p.HasExited) { throw "the agent exited with code $($p.ExitCode) during the measurement" }
    $cpu = $p.TotalProcessorTime.TotalMilliseconds - $cpu0
    $threads1 = Get-ThreadCpu $p
    $all = Read-Lines $traced.Out
    $window = @($all | Select-Object -Skip $lines0)
    $wakes = @{}
    $others = @{}
    foreach ($l in $window) {
        if ($l -match '^EVT Wake (\S+)') { $wakes[$Matches[1]] = 1 + [int]$wakes[$Matches[1]] }
        elseif ($l -match '^EVT (\S+)') { $others[$Matches[1]] = 1 + [int]$others[$Matches[1]] }
    }
    function Sum-Of([string[]]$kinds) { $n = 0; foreach ($k in $kinds) { $n += [int]$wakes[$k] }; return $n }
    $own = Sum-Of @('deadline', 'retry', 'loadretry', 'save')
    $derived = Sum-Of @('debounce', 'post', 'history', 'visitdays')
    $external = Sum-Of @('winevent', 'session')
    $kinds = 'deadline', 'retry', 'loadretry', 'save', 'debounce', 'post', 'history', 'visitdays', 'winevent', 'session'
    $counts = ($kinds | ForEach-Object { '{0}={1}' -f $_, [int]$wakes[$_] }) -join ' '
    $perEvent = if ($external -gt 0) { $cpu / $external } else { 0 }
    Write-Host ('AGENT cold window cpu_ms={0:N0} over {1} s (settled after {2:N1} s, then {3} s) wakes: {4} | own={5} derived={6} external={7} cpu_ms_per_external={8:N1} threads={9} handles={10}' -f `
        $cpu, $Seconds, $settledAfter, $SettleSeconds, $counts, $own, $derived, $external, $perEvent, $p.Threads.Count, $p.HandleCount)
    $moved = @()
    $sum = 0.0
    foreach ($id in $threads1.Keys) {
        $d = $threads1[$id] - [double]$threads0[$id]
        if ($d -gt 0) { $moved += ('thread {0} +{1:N0} ms' -f $id, $d); $sum += $d }
    }
    Write-Host ('AGENT cold window threads with cpu: {0}{1}' -f $(if ($moved.Count) { $moved -join ', ' } else { 'none' }), $(if ($cpu - $sum -gt 1) { ' (' + [math]::Round($cpu - $sum) + ' ms on threads that ended)' } else { '' }))
    if ($others.Count -gt 0) { Write-Host ('AGENT cold window other events: ' + (($others.Keys | Sort-Object | ForEach-Object { '{0}={1}' -f $_, $others[$_] }) -join ' ')) }
    if ($external -gt 0) { Write-Host ('AGENT cold window had {0} external events (winevent={1} session={2}); the CPU bound is {3} ms each' -f $external, [int]$wakes['winevent'], [int]$wakes['session'], $MaxCpuMsPerEvent) }
    $failed = @()
    if ($own -gt 0) { $failed += "the agent woke itself $own times ($counts)" }
    if ($external -eq 0 -and $derived -gt 0) { $failed += "work with no event behind it: $derived wake-ups ($counts)" }
    if ($external -eq 0 -and $cpu -gt $MaxCpuMs) { $failed += "CPU $([math]::Round($cpu)) ms in $Seconds s is over $MaxCpuMs ms with no external event" }
    if ($external -gt 0 -and $perEvent -gt $MaxCpuMsPerEvent) { $failed += "CPU $([math]::Round($perEvent, 1)) ms per external event is over $MaxCpuMsPerEvent ms" }
    # What the agent printed as it started: where the memory goes.
    $all | Where-Object { $_ -like 'TRACE *' } | ForEach-Object { Write-Host $_ }
    if ($failed.Count -gt 0) { $coldFailure = $(if ($coldFailure) { "$coldFailure; " } else { '' }) + 'gate G2 failed: ' + ($failed -join '; ') }
    else { Write-Host 'gate G2 CPU and wake-ups passed' }
}
catch { $coldFailure = if ($coldFailure) { "$coldFailure; $_" } else { $_ } }
finally { Stop-Agent $traced }

# 2b. On request: does turning tiered PGO off, or ReadyToRun, make the cold start cheaper? Not a gate.
if ($Experiment) {
    $variants = [ordered]@{ 'default' = @{ Vars = @{}; Exe = $exePath }; 'tieredpgo-off' = @{ Vars = @{ DOTNET_TieredPGO = '0' }; Exe = $exePath } }
    if ($ReadyToRunExe) { $variants['readytorun'] = @{ Vars = @{}; Exe = (Resolve-Path $ReadyToRunExe).Path } }
    for ($i = 1; $i -le 3; $i++) {
        foreach ($name in $variants.Keys) {
            $v = $variants[$name]
            $vars = @{ LATER_AGENT_TRACE = '1' }
            foreach ($k in $v.Vars.Keys) { $vars[$k] = $v.Vars[$k] }
            $a = Start-Agent "exp-$name" $vars $v.Exe
            try {
                $took = Wait-Settled $a
                $a.Proc.Refresh()
                Write-Host ('AGENT experiment {0} run {1}: startup_cpu_ms={2:N0} private={3:N1} working={4:N1} threads={5} settled_after={6:N1} s' -f `
                    $name, $i, $a.Proc.TotalProcessorTime.TotalMilliseconds, ($a.Proc.PrivateMemorySize64 / 1MB), ($a.Proc.WorkingSet64 / 1MB), $a.Proc.Threads.Count, $took)
            }
            finally { Stop-Agent $a }
        }
    }
}

# 3. Warm: a real browser window is open and the agent has read it. Chrome's own (default) profile on the ephemeral runner, so
#    the agent looks at what it would look at on a user's machine.
function Test-UiaLoaded($proc) {
    try { return [bool]($proc.Modules | Where-Object { $_.ModuleName -eq 'UIAutomationCore.dll' }) } catch { return $false }
}
$chrome = $env:LATER_CHROME
if ($chrome -and (Test-Path $chrome)) {
    $chromeData = Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data'
    $hadChromeData = Test-Path $chromeData
    $w = Start-Process $exePath -ArgumentList '--agent' -PassThru
    try {
        Start-Sleep -Seconds 5
        $w.Refresh()
        if ($w.HasExited) { throw "the agent exited early with code $($w.ExitCode)" }
        $chromeArgs = @('--no-first-run', '--no-default-browser-check', '--disable-sync', '--new-window', 'about:blank')
        $loaded = $false
        for ($try = 1; $try -le 3 -and -not $loaded; $try++) {
            Start-Process $chrome -ArgumentList $chromeArgs | Out-Null   # a new window: a show and a foreground event for the agent to read
            for ($i = 0; $i -lt 10 -and -not $loaded; $i++) { Start-Sleep -Seconds 1; $w.Refresh(); $loaded = Test-UiaLoaded $w }
        }
        if (-not $loaded) { throw 'the agent never read the browser window (UI Automation was not loaded)' }
        Start-Sleep -Seconds 10   # let the read and its retries settle
        $w.Refresh()
        $warmFirst = @{ Private = $w.PrivateMemorySize64; Cpu = $w.TotalProcessorTime.TotalMilliseconds }
        Start-Sleep -Seconds $Seconds
        $w.Refresh()
        if ($w.HasExited) { throw "the agent exited with code $($w.ExitCode) during the warm measurement" }
        $warmPrivate = $w.PrivateMemorySize64 / 1MB
        Write-Host ('AGENT warm private={0:N1} working={1:N1} cpu_ms_delta={2:N0} threads={3} handles={4} (after the read: private={5:N1}; {6} s apart; UI Automation loaded)' -f `
            $warmPrivate, ($w.WorkingSet64 / 1MB), ($w.TotalProcessorTime.TotalMilliseconds - $warmFirst.Cpu), $w.Threads.Count, $w.HandleCount, ($warmFirst.Private / 1MB), $Seconds)
        if ($warmPrivate -gt $WarmAlertMB) {
            Write-Host ('AGENT warm ALERT: private {0:N1} MB is over {1} MB' -f $warmPrivate, $WarmAlertMB)
            Write-Host ('::warning title=Agent warm memory::private {0:N1} MB is over {1} MB with a browser open' -f $warmPrivate, $WarmAlertMB)
        }
    }
    finally {
        Stop-Process -Id $w.Id -Force -ErrorAction SilentlyContinue
        Get-Process chrome -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        if (-not $hadChromeData) { Remove-Item -Recurse -Force $chromeData -ErrorAction SilentlyContinue }
    }
} else {
    Write-Host 'AGENT warm not measured: LATER_CHROME is not set'
}
if ($coldFailure) { throw $coldFailure }
