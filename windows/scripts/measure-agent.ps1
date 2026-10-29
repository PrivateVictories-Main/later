# Gate G2: the background agent's cold cost. Starts the published Release agent (no browser open, onboarding done) and checks
# its private memory and its CPU over a quiet stretch. Prints counts only. The data folder is the ephemeral runner's own.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [int]$Seconds = 30,
    [double]$MaxPrivateMB = 15.0,
    [double]$MaxCpuMs = 50
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
finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
