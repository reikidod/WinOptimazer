<div align="center">

<img src="docs/banner.svg" alt="WinOptimizer — единый центр управления Windows" width="100%">

<br>

# WinOptimizer

### Единый центр управления Windows 10/11

Настройка, обслуживание и оптимизация системы в одном приложении. Контролируемые изменения, понятные предупреждения и возможность отката — без лишней сложности.

<br>

[![Stable](https://img.shields.io/github/v/release/reikidod/WinOptimazer?style=for-the-badge&label=STABLE&color=4B6CA3)](https://github.com/reikidod/WinOptimazer/releases/latest)
[![Beta](https://img.shields.io/github/v/release/reikidod/WinOptimazer?include_prereleases&filter=v0.7.*-beta&style=for-the-badge&label=BETA&color=C0262D)](https://github.com/reikidod/WinOptimazer/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/reikidod/WinOptimazer/ci.yml?branch=beta&style=for-the-badge&label=BUILD%20%26%20TESTS)](https://github.com/reikidod/WinOptimazer/actions/workflows/ci.yml)
[![Загрузки](https://img.shields.io/github/downloads/reikidod/WinOptimazer/total?style=for-the-badge&label=DOWNLOADS&color=353840)](https://github.com/reikidod/WinOptimazer/releases)
[![MIT](https://img.shields.io/badge/LICENSE-MIT-8AAAD8?style=for-the-badge)](LICENSE)

![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-202225?style=flat-square&logo=windows11&logoColor=8AAAD8)
![.NET 8](https://img.shields.io/badge/.NET-8-202225?style=flat-square&logo=dotnet&logoColor=8AAAD8)
![WPF](https://img.shields.io/badge/UI-WPF-202225?style=flat-square)
![C#](https://img.shields.io/badge/C%23-202225?style=flat-square&logo=csharp&logoColor=8AAAD8)

<br>

[**Скачать Stable**](https://github.com/reikidod/WinOptimazer/releases/latest) · [Бета-версии](https://github.com/reikidod/WinOptimazer/releases) · [Официальный сайт](https://reikidod.github.io/WinOptimazer-site/) · [Возможности](#возможности) · [Beta 0.7](docs/BETA.md · [План 0.7](docs/ROADMAP-0.7.md)) · [Безопасность](#безопасность) · [Обратная связь](https://github.com/reikidod/WinOptimazer/issues/new/choose)

</div>

---

> [!IMPORTANT]
> **Текущая разработка: 0.7 Beta — Performance Center.** Ветка `beta` — рабочая площадка для новых возможностей и тестирования. Бета-сборки публикуются отдельно от стабильного канала. Для повседневного использования выбирайте Stable.

## Возможности

<table>
<tr>
<td width="50%" valign="top">

### ◈ Главная панель
Состояние CPU, RAM, системного диска и сети, быстрые действия, диагностика и история последних операций. Данные обновляются, пока открыта главная страница.

</td>
<td width="50%" valign="top">

### ◈ Оптимизация Windows
Каталог твиков реестра и служб с описаниями, оценкой пользы и предупреждениями. Поддерживаемые изменения можно отменить; перед применением предлагается создать точку восстановления.

</td>
</tr>
<tr>
<td valign="top">

### ◈ Игры и производительность
Поиск игр из Steam, Epic, GOG и Xbox, инструменты OptiScaler и настройки режима высокой производительности GPU.

</td>
<td valign="top">

### ◈ Приложения
Установка и удаление программ, интеграция с Microsoft Store. Поддерживаемые загрузки проверяются по контрольным суммам.

</td>
</tr>
<tr>
<td valign="top">

### ◈ Обслуживание
Временные файлы, восстановление системы, автозагрузка и инструменты обслуживания Windows. Планировщик задач доступен в режиме чтения; изменения задач из приложения не выполняются.

</td>
<td valign="top">

### ◈ Диагностика и «Мой ПК»
Информация о процессоре, видеокарте, памяти, накопителях и системе; сведения о свободном месте, сети, правах запуска, ожидающей перезагрузке и доступных датчиках.

</td>
</tr>
</table>

## Скачать

<table>
<tr>
<td width="50%" valign="top">

### Stable

Для повседневного использования.

[**Открыть последний стабильный релиз →**](https://github.com/reikidod/WinOptimazer/releases/latest)

</td>
<td width="50%" valign="top">

### Beta 0.7

Предварительные функции и изменения для тестирования. Возможны ошибки.

[**Открыть бета-релизы →**](https://github.com/reikidod/WinOptimazer/releases) · [Что нового в Beta 0.7](docs/BETA.md · [План 0.7](docs/ROADMAP-0.7.md))

</td>
</tr>
</table>

В релизах доступны установщик `WinOptimizer-Setup.exe` и, если приложен к конкретному релизу, архив `WinOptimizer-win-x64.zip`.

## Быстрый старт

1. Выберите Stable или Beta выше и откройте страницу соответствующего релиза.
2. Скачайте `WinOptimizer-Setup.exe` либо доступный ZIP-архив.
3. Запустите установщик или распакуйте архив и откройте `WinOptimizer.exe`.
4. Для операций с системными параметрами предоставьте права администратора, когда приложение их запросит.
5. Перед применением твиков прочитайте описание и предупреждения.

> [!TIP]
> Перед крупными изменениями создайте точку восстановления Windows. Не включайте настройки, назначение которых вам непонятно.

## Безопасность и прозрачность

- Поддерживаемые изменения реестра сохраняют исходные значения для отката.
- Перед применением системных изменений предлагается точка восстановления.
- Диагностические разделы читают состояние системы; автоматическое исправление без действия пользователя не выполняется.
- Поддерживаемые загрузки сверяются по SHA-256.
- Планировщик задач Windows отображается в режиме чтения.
- История операций хранится локально; приложение не отправляет сведения о компьютере на сторонние серверы.

Логи приложения: `%ProgramData%\WinOptimizer\log.txt`. История операций: `%LOCALAPPDATA%\WinOptimizer\history.jsonl`.

## Технологии

| Компонент | Использование |
|---|---|
| C# | Основной язык |
| .NET 8 | Платформа приложения |
| WPF / XAML | Интерфейс Windows |
| GitHub Actions | Сборка, тесты и публикация релизов |
| MIT | Лицензия |

## Разработка

Для сборки требуется Windows и .NET 8 SDK.

```powershell
dotnet restore
dotnet build -c Release
dotnet test tests/WinOptimizer.Tests/WinOptimizer.Tests.csproj -c Release
```

Рабочая ветка текущей бета-разработки — `beta`. Стабильный канал поддерживается отдельно в `main`.

## Обратная связь

- [Сообщить об ошибке](https://github.com/reikidod/WinOptimazer/issues/new/choose)
- [Предложить улучшение](https://github.com/reikidod/WinOptimazer/issues)
- [Посетить официальный сайт](https://reikidod.github.io/WinOptimazer-site/)

**Лицензия:** [MIT](LICENSE).

---

<details>
<summary><b>Сборка проекта, тесты, подпись и установщик</b> (для разработчиков)</summary>

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

### Тесты

```powershell
dotnet test tests/WinOptimizer.Tests/WinOptimizer.Tests.csproj -c Release
```

### Автоматические релизы

Push в `beta` запускает тесты, Release-сборку, публикацию, проверку чистой установки и удаления, обновления с предыдущей бета-версии и smoke test, после чего создаётся prerelease `v0.6.<номер сборки>-beta`. Release Notes берутся из соответствующего раздела CHANGELOG. Push в `main` обслуживает стабильный канал; изменения этой бета-ветки не затрагивают `main` до отдельного слияния.

</details>
