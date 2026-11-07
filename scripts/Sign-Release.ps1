# Сборка Release и подпись Authenticode (signtool).
# Требует Windows SDK / Visual Studio (signtool.exe) и PFX.

param(
    [string]$Configuration = "Release",
    [string]$PfxPath = "$PSScriptRoot\..\certs\WinOptimizer-Codesign.pfx",
    [string]$Password = "WinOptimizerLocal",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

if (-not (Test-Path $PfxPath)) {
    Write-Host "PFX не найден: $PfxPath"
    Write-Host "Сначала запустите: .\scripts\New-CodeSigningCert.ps1"
    exit 1
}

Write-Host "Сборка $Configuration..."
dotnet publish -c $Configuration -r win-x64 --self-contained false -o "$root\publish"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $root "publish\WinOptimizer.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Не найден $exe"
    exit 1
}

$signtool = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
    "${env:ProgramFiles}\Windows Kits\10\bin\*\x64\signtool.exe"
) | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $signtool) {
    Write-Host "signtool.exe не найден. Установите Windows SDK (Signing Tools)."
    Write-Host "https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/"
    exit 1
}

Write-Host "Подпись: $exe"
Write-Host "signtool: $signtool"

& $signtool sign `
    /fd SHA256 `
    /td SHA256 `
    /tr $TimestampUrl `
    /f $PfxPath `
    /p $Password `
    /d "WinOptimizer" `
    /du "https://github.com/" `
    $exe

if ($LASTEXITCODE -ne 0) {
    Write-Host "Подпись не удалась (код $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Проверка подписи:"
& $signtool verify /pa /v $exe

Write-Host ""
Write-Host "Готово: $exe"
Write-Host "Самоподписанный сертификат убирает предупреждение только на машинах, где он в Trusted Publishers."
Write-Host "Публичный SmartScreen требует сертификат от коммерческого CA."
