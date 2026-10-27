# Proves ImportScan rejects every way our code could reach the network. Run after ImportScan is built (Release), from the repo root.
$ErrorActionPreference = 'Stop'
$work = Join-Path $env:RUNNER_TEMP 'scan-selftest'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

function Expect-Rejected([string]$what, [string]$dir, [string]$pattern) {
    $out = dotnet run --project windows/tools/ImportScan -c Release --no-build -- $dir | Out-String
    if ($LASTEXITCODE -ne 1 -or $out -notmatch $pattern) { Write-Host $out; throw "self-test: $what was not rejected (exit $LASTEXITCODE)" }
    Write-Host "self-test: $what rejected as expected"
}

function New-ManagedProbe([string]$name, [string]$code) {
    $src = Join-Path $work "src-$name"
    $out = Join-Path $work $name
    New-Item -ItemType Directory -Force $src | Out-Null
    Set-Content (Join-Path $src "$name.csproj") "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
    Set-Content (Join-Path $src 'Probe.cs') $code
    dotnet build $src -c Release -o $out | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "self-test: could not build the $name probe" }
    return $out
}

# 1. a native import (ftp.exe imports ws2_32 and mswsock)
$native = Join-Path $work 'native'
New-Item -ItemType Directory -Force $native | Out-Null
Copy-Item "$env:SystemRoot\System32\ftp.exe" (Join-Path $native 'Later.ProbeNative.exe')
Expect-Rejected 'a native network import' $native 'Later\.ProbeNative\.exe imports .*ws2_32\.dll'

# 2. a managed P/Invoke: the target is only in metadata, and the name is deliberately bare and upper case
$pinvoke = New-ManagedProbe 'Later.ProbePInvoke' 'static class P { [System.Runtime.InteropServices.DllImport("WS2_32")] internal static extern int WSACleanup(); }'
Expect-Rejected 'a managed P/Invoke to ws2_32' $pinvoke 'Later\.ProbePInvoke\.dll imports ws2_32\.dll'

# 3. a reference to a System.Net.* assembly
$netref = New-ManagedProbe 'Later.ProbeNetRef' 'public static class N { public static object T => typeof(System.Net.Sockets.Socket); }'
Expect-Rejected 'a System.Net.Sockets assembly reference' $netref 'Later\.ProbeNetRef\.dll references assembly System\.Net\.Sockets'

# 4. network-capable runtime files in the folder
$files = Join-Path $work 'files'
New-Item -ItemType Directory -Force $files | Out-Null
Set-Content (Join-Path $files 'msquic.dll') 'x'
Expect-Rejected 'msquic.dll in the output folder' $files 'msquic\.dll is a network-capable runtime file'

Remove-Item -Recurse -Force $work
$global:LASTEXITCODE = 0   # the expected failures must not become the step's exit code
