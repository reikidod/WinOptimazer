$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
Write-Host "WinOptimizer build installer"
Write-Host "Dir: $(Get-Location)"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found. Install .NET 8 SDK."
}

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish -c Release -r win-x64 --self-contained false -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path "publish\WinOptimizer.exe")) { throw "publish\WinOptimizer.exe missing" }

$iscc = @(
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw "ISCC.exe not found. Install Inno Setup 6." }

New-Item -ItemType Directory -Force -Path dist | Out-Null
& $iscc "$PWD\installer\WinOptimizer.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed: $LASTEXITCODE" }

Get-ChildItem dist\WinOptimizer-Setup-*.exe | ForEach-Object { Write-Host "OK: $($_.FullName)" }
