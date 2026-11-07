# Публикация + сборка установщика Inno Setup
param(
    [string]$Configuration = "Release",
    [string]$InnoSetupPath = ""
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

$publishDir = Join-Path $root "publish"
$distDir = Join-Path $root "dist"
$iss = Join-Path $root "installer\WinOptimizer.iss"
$exeName = "WinOptimizer.exe"

Write-Host "Корень проекта: $root"
Write-Host "==> dotnet publish ($Configuration, win-x64) -> publish\"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish -c $Configuration -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $publishDir $exeName
if (-not (Test-Path $exe)) {
    throw "Не найден $exe — publish пустой или сборка не удалась."
}

$fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
Write-Host "publish\: $fileCount файл(ов), $exeName OK"

$iscc = $null
if ($InnoSetupPath -and (Test-Path $InnoSetupPath)) {
    $iscc = $InnoSetupPath
} else {
    foreach ($c in @(
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path $c) { $iscc = $c; break }
    }
    if (-not $iscc) {
        $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($cmd) { $iscc = $cmd.Source }
    }
}

if (-not $iscc) {
    Write-Host "ISCC.exe не найден: https://jrsoftware.org/isdl.php"
    Write-Host "publish уже готов: $publishDir"
    exit 2
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Write-Host "==> ISCC: $iscc"
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed: $LASTEXITCODE" }

Write-Host "Установщик в: $distDir"
Get-ChildItem $distDir -Filter "WinOptimizer-Setup-*.exe" | ForEach-Object { Write-Host "  $($_.FullName)" }
