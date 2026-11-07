# Подпись коммерческим сертификатом (реальный Authenticode / меньше проблем со SmartScreen).
#
# Где взять сертификат:
#   - SSL.com, Sectigo, DigiCert, GlobalSign (Code Signing / EV Code Signing)
#   - EV обычно быстрее набирает репутацию SmartScreen
#
# PFX обычно выдаёт CA после проверки организации (OV) или расширенной (EV).

param(
    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(Mandatory = $true)]
    [string]$Password,

    [string]$ExePath = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

if (-not $ExePath) {
    $ExePath = Join-Path $root "publish\WinOptimizer.exe"
}

if (-not (Test-Path $ExePath)) {
    Write-Host "Сначала соберите: dotnet publish -c Release -r win-x64 -o publish"
    Write-Host "Не найден: $ExePath"
    exit 1
}

$signtool = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
    "${env:ProgramFiles}\Windows Kits\10\bin\*\x64\signtool.exe"
) | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $signtool) {
    Write-Host "signtool.exe не найден. Установите Windows SDK."
    exit 1
}

& $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $PfxPath /p $Password /d "WinOptimizer" $ExePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $signtool verify /pa /v $ExePath
Write-Host "Подписано: $ExePath"
