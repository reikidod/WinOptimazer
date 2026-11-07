# WinOptimizer

Открытый оптимизатор Windows (твики, деблоат, Zapret).

**Лицензия:** [MIT](LICENSE)

## Сборка

```powershell
dotnet build -c Release
dotnet run
```

Иконка: `Assets/app.ico`  
Лицензия копируется в выходную папку вместе с `LICENSE`.

## Windows Defender

Лицензия MIT **не отключает** SmartScreen и Defender. Они смотрят на:

1. **Цифровую подпись** (Authenticode) — самый сильный сигнал  
2. Репутацию файла (сколько людей запуска и без жалоб)  
3. Поведение (запись в реестр, службы, админ-права)

Что можно сделать:

- Собирать **Release** и не путать с вредоносными именами  
- Опубликовать исходники на GitHub (прозрачность)  
- При ложном срабатывании: [Microsoft Security Intelligence](https://www.microsoft.com/en-us/wdsi/filesubmission) → Submit a file  
- Для распространения — подписать exe сертификатом (например, через `signtool` и купленный/облачный код-signing сертификат)

`requireAdministrator` в манифесте нужен для твиков HKLM и служб; из‑за этого SmartScreen строже к «неизвестным» сборкам — это нормально для утилит такого типа.

## Подпись Authenticode

### Важно

| Тип | SmartScreen | Кому подходит |
|-----|-------------|----------------|
| Самоподпись (в комплекте) | Всё равно предупреждает, пока не доверите сертификат вручную | Своя машина / тесты |
| OV/EV Code Signing от CA | Репутация накапливается, предупреждений меньше | Раздача другим людям |

**Бесплатно «как у Microsoft Store» сделать нельзя** — нужен платный code signing сертификат.

### Локальная подпись (самоподпись)

На **Windows** (нужны PowerShell от пользователя и Windows SDK / Visual Studio):

```powershell
# 1. Сертификат + доверие для текущего пользователя
.\scripts\New-CodeSigningCert.ps1

# 2. Сборка + подпись
.\scripts\Sign-Release.ps1
```

Готовый exe: `publish\WinOptimizer.exe`

Пароль PFX по умолчанию: `WinOptimizerLocal`  
Файл: `certs\WinOptimizer-Codesign.pfx`

### Коммерческий сертификат

```powershell
dotnet publish -c Release -r win-x64 -o publish
.\scripts\Sign-WithCommercialCert.ps1 -PfxPath "C:\path\to\real.pfx" -Password "secret"
```

Покупают, например, у SSL.com / Sectigo / DigiCert (Code Signing или EV Code Signing).

### Проверка

```powershell
signtool verify /pa /v publish\WinOptimizer.exe
# или
Get-AuthenticodeSignature publish\WinOptimizer.exe
```


## Установщик (Inno Setup)

Нужны [.NET 8 SDK](https://dotnet.microsoft.com/download) и [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
# одной командой: publish + ISCC
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

Или вручную:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" .\installer\WinOptimizer.iss
```

Результат: `dist\WinOptimizer-Setup-0.2.0.exe`

Скрипт: `installer\WinOptimizer.iss`  
- x64, права администратора  
- русский / english  
- лицензия MIT  
- ярлык в меню Пуск, опционально на рабочий стол  
