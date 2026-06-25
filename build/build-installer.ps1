<#
    End-to-end release build:
      1. (re)generate the icon
      2. publish the single-file exe
      3. compile the Inno Setup installer

    Requires the .NET 8 SDK and Inno Setup 6 (ISCC.exe).
    Output: installer_output\CoreShift-Setup-<ver>.exe
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 1. Icon (uses Windows PowerShell for System.Drawing)
Write-Host "[1/3] Generating icon..." -ForegroundColor Cyan
powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build\generate-icon.ps1')
if ($LASTEXITCODE -ne 0) { throw "Icon generation failed." }

# 2. Publish
Write-Host "[2/3] Publishing single-file executable..." -ForegroundColor Cyan
& (Join-Path $root 'build\publish.ps1')
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# 3. Locate ISCC and compile installer
Write-Host "[3/3] Compiling installer..." -ForegroundColor Cyan
$iscc = $null
foreach ($candidate in @(
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")) {
    if (Test-Path $candidate) { $iscc = $candidate; break }
}
if (-not $iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
    # Fall back to the install location recorded in the uninstall registry (handles non-C: installs).
    $arp = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
    $loc = Get-ItemProperty $arp -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like '*Inno Setup*' -and $_.InstallLocation } |
        Select-Object -First 1 -ExpandProperty InstallLocation
    if ($loc -and (Test-Path (Join-Path $loc 'ISCC.exe'))) { $iscc = Join-Path $loc 'ISCC.exe' }
}
if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install it:  winget install JRSoftware.InnoSetup"
}

& $iscc (Join-Path $root 'installer\CoreShift.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$setup = Get-ChildItem (Join-Path $root 'installer_output') -Filter 'CoreShift-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ""
Write-Host "Installer ready: $($setup.FullName)" -ForegroundColor Green
Write-Host "Size: $([Math]::Round($setup.Length / 1MB, 2)) MB" -ForegroundColor Green
