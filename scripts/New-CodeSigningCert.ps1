# Создаёт самоподписанный сертификат Code Signing в хранилище CurrentUser
# и экспортирует PFX для signtool.
# ВАЖНО: это НЕ публичный Authenticode. SmartScreen всё равно может ругаться,
# пока сертификат не от коммерческого CA (Sectigo, DigiCert, SSL.com и т.д.).

param(
    [string]$Subject = "CN=WinOptimizer Local Code Signing",
    [string]$PfxPath = "$PSScriptRoot\..\certs\WinOptimizer-Codesign.pfx",
    [string]$Password = "WinOptimizerLocal",
    [int]$Years = 3
)

$ErrorActionPreference = "Stop"

$certsDir = Split-Path -Parent $PfxPath
if (-not (Test-Path $certsDir)) {
    New-Item -ItemType Directory -Path $certsDir | Out-Null
}

Write-Host "Создаю сертификат: $Subject"

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyAlgorithm RSA `
    -KeyLength 4096 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears($Years) `
    -FriendlyName "WinOptimizer Local Code Signing"

$pwd = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $pwd | Out-Null

# Чтобы Windows доверяла подписи локально — кладём в Trusted Root + Trusted Publishers (текущий пользователь)
$storeRoot = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "CurrentUser")
$storeRoot.Open("ReadWrite")
$storeRoot.Add($cert)
$storeRoot.Close()

$storePub = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPublisher", "CurrentUser")
$storePub.Open("ReadWrite")
$storePub.Add($cert)
$storePub.Close()

Write-Host ""
Write-Host "Готово."
Write-Host "  Thumbprint : $($cert.Thumbprint)"
Write-Host "  PFX        : $PfxPath"
Write-Host "  Password   : $Password"
Write-Host ""
Write-Host "Сертификат добавлен в Trusted Root и Trusted Publishers (CurrentUser)."
Write-Host "Для распространения пользователям нужен сертификат от CA, не самоподпись."
