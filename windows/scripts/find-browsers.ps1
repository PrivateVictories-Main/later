# Finds (or fetches) the browsers the reader integration tests drive and exports LATER_CHROME and LATER_HELIUM
# through $env:GITHUB_ENV. Helium comes from its portable zip; once windows/browsers.lock has a line for this
# architecture the tag, asset name and SHA-256 must match it exactly.
$ErrorActionPreference = 'Stop'
$arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
$repo = 'imputnet/helium-windows'
$work = Join-Path $env:RUNNER_TEMP 'helium-dl'
$root = Join-Path $env:RUNNER_TEMP 'helium'
New-Item -ItemType Directory -Force $work, $root | Out-Null

# --- Chrome: installed on the runners; otherwise Google's enterprise MSI for this architecture.
$chrome = @('C:\Program Files\Google\Chrome\Application\chrome.exe', 'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe') |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) {
    Write-Host "CHROME not preinstalled on $arch; installing Google's enterprise MSI"
    $msiName = if ($arch -eq 'arm64') { 'googlechromestandaloneenterprise_arm64.msi' } else { 'googlechromestandaloneenterprise64.msi' }
    $msi = Join-Path $work $msiName
    Invoke-WebRequest "https://dl.google.com/dl/chrome/install/$msiName" -OutFile $msi
    Start-Process msiexec.exe -ArgumentList '/i', $msi, '/qn' -Wait
    $chrome = @('C:\Program Files\Google\Chrome\Application\chrome.exe', 'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe') |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $chrome) { throw 'Chrome could not be found or installed' }
Write-Host "CHROME exe=$chrome version=$((Get-Item $chrome).VersionInfo.FileVersion) product='$((Get-Item $chrome).VersionInfo.ProductName)'"

# --- Helium
$lockFile = Join-Path $PSScriptRoot '..\browsers.lock'
$lock = $null
if (Test-Path $lockFile) {
    $lock = Get-Content $lockFile | Where-Object { $_ -match "^helium-$arch\s" } | ForEach-Object { $p = $_ -split '\s+'; @{ Tag = $p[1]; Asset = $p[2]; Sha = $p[3] } } | Select-Object -First 1
}
if ($lock) {
    $tag = $lock.Tag; $asset = $lock.Asset
} else {
    Write-Host "HELIUM no lock line for $arch; listing the latest release"
    $listing = gh release view --repo $repo --json tagName,assets --jq '.tagName, (.assets[] | "\(.name) \(.size)")'
    $listing | ForEach-Object { Write-Host "HELIUM release: $_" }
    $tag = $listing[0]
    $names = $listing | Select-Object -Skip 1 | ForEach-Object { ($_ -split ' ')[0] }
    $cands = @($names | Where-Object { $_ -match '\.zip$' -and $_ -match 'windows' -and $_ -match $arch })
    if ($cands.Count -ne 1) { throw "expected exactly one $arch zip asset, found $($cands.Count): $($cands -join ', ')" }
    $asset = $cands[0]
}
gh release download $tag --repo $repo --pattern $asset --dir $work --clobber
$zip = Join-Path $work $asset
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "HELIUM tag=$tag asset=$asset sha256=$sha"
if ($lock -and $sha -ne $lock.Sha.ToLowerInvariant()) { throw "Helium asset $asset does not match browsers.lock (got $sha, locked $($lock.Sha))" }
Expand-Archive $zip -DestinationPath $root -Force
$helium = Get-ChildItem $root -Recurse -Filter chrome.exe | Select-Object -First 1
if (-not $helium) { throw 'chrome.exe not found in the Helium archive' }
$hv = (Get-Item $helium.FullName).VersionInfo
Write-Host "HELIUM exe=$($helium.FullName) version=$($hv.FileVersion) product='$($hv.ProductName)' description='$($hv.FileDescription)'"
Remove-Item $work -Recurse -Force

"LATER_CHROME=$chrome" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
"LATER_HELIUM=$($helium.FullName)" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
