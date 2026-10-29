# Gate G2: the background agent's cold cost. Starts the published Release agent (no browser open, onboarding done) and checks
# its private memory and its CPU over a quiet stretch. Then the warm figure: the same agent after it has read a real browser
# window (UI Automation loaded), reported with no gate; over $WarmAlertMB private memory is an alert line. Prints counts only.
# The data folder is the ephemeral runner's own.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$Seconds = 30,
    [double]$MaxPrivateMB = 15.0,
    [double]$MaxCpuMs = 50,
    [double]$WarmAlertMB = 25.0
)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
$dir = Join-Path $env:LOCALAPPDATA 'Later'
New-Item -ItemType Directory -Force $dir | Out-Null
Set-Content (Join-Path $dir 'settings.json') '{"onboarded":true}' -NoNewline

# 1. Where the memory goes: the agent prints its private memory at named points of its start (LATER_AGENT_TRACE=1).
$trace = Join-Path $env:RUNNER_TEMP 'agent-trace.txt'
$env:LATER_AGENT_TRACE = '1'
$t = Start-Process $exePath -ArgumentList '--agent' -PassThru -RedirectStandardOutput $trace
try { Start-Sleep -Seconds 6 } finally { Stop-Process -Id $t.Id -Force -ErrorAction SilentlyContinue }
Remove-Item Env:LATER_AGENT_TRACE
$t.WaitForExit(10000) | Out-Null
if (Test-Path $trace) { Get-Content $trace | ForEach-Object { Write-Host $_ }; Remove-Item $trace -Force }

# 2. The gate: a plain run.
$coldFailure = $null
$p = Start-Process $exePath -ArgumentList '--agent' -PassThru
try {
    Start-Sleep -Seconds 5
    $p.Refresh()
    if ($p.HasExited) { throw "the agent exited early with code $($p.ExitCode)" }
    $first = @{ Private = $p.PrivateMemorySize64; Working = $p.WorkingSet64; Cpu = $p.TotalProcessorTime.TotalMilliseconds; Threads = $p.Threads.Count; Handles = $p.HandleCount }
    Start-Sleep -Seconds $Seconds
    $p.Refresh()
    if ($p.HasExited) { throw "the agent exited with code $($p.ExitCode) during the measurement" }
    $privateMB = $p.PrivateMemorySize64 / 1MB
    $workingMB = $p.WorkingSet64 / 1MB
    $cpuDelta = $p.TotalProcessorTime.TotalMilliseconds - $first.Cpu
    $line = 'AGENT cold private={0:N1} working={1:N1} cpu_ms_delta={2:N0} threads={3} handles={4} (at 5 s: private={5:N1} working={6:N1} threads={7} handles={8}; {9} s apart)' -f `
        $privateMB, $workingMB, $cpuDelta, $p.Threads.Count, $p.HandleCount, ($first.Private / 1MB), ($first.Working / 1MB), $first.Threads, $first.Handles, $Seconds
    Write-Host $line
    $failed = @()
    if ($privateMB -gt $MaxPrivateMB) { $failed += "private memory $([math]::Round($privateMB, 1)) MB is over $MaxPrivateMB MB" }
    if ($cpuDelta -gt $MaxCpuMs) { $failed += "CPU $([math]::Round($cpuDelta)) ms in $Seconds s is over $MaxCpuMs ms" }
    if ($failed.Count -gt 0) { throw ('gate G2 failed: ' + ($failed -join '; ')) }
    Write-Host 'gate G2 passed'
}
catch { $coldFailure = $_ }
finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
$p.WaitForExit(10000) | Out-Null

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
