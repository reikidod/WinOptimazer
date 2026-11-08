# Restores Views/MainWindow.xaml from the last known-good commit and optionally applies UI upgrades.
# Run from repo root:  pwsh -File scripts/Restore-MainWindow.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not (Test-Path (Join-Path $PWD "WinOptimizer.csproj"))) {
    if (Test-Path (Join-Path $PSScriptRoot "..\WinOptimizer.csproj")) {
        Set-Location (Join-Path $PSScriptRoot "..")
    }
}

$url = "https://raw.githubusercontent.com/reikidod/WinOptimazer/bf6d56d10c183ff11d87ce49dcd2964448924b2f/Views/MainWindow.xaml"
$out = Join-Path $PWD "Views\MainWindow.xaml"
Write-Host "Downloading good MainWindow.xaml..."
Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
Write-Host "Restored: $out ($((Get-Item $out).Length) bytes)"
Write-Host "Next: rebuild the app. For sidebar icons + collapsible recommended groups, replace the nav StackPanel and Recommended ItemsControl as documented in the PR/commit notes."
