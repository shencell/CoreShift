<#
    Publishes CoreShift as a small, framework-dependent, single-file win-x64 executable.
    Output: dist\CoreShift.exe
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\CoreShift\CoreShift.csproj'
$out  = Join-Path $root 'dist'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $out 'CoreShift.exe'
$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host ""
Write-Host "Published: $exe  ($sizeMb MB)" -ForegroundColor Green
