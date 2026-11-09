<p align="center">
  <img src="Assets/app.ico" width="72" height="72" alt="WinOptimizer">
</p>

<h1 align="center">WinOptimizer</h1>

<p align="center">
  <strong>Бесплатный оптимизатор Windows 10/11</strong><br>
  Твики с откатом · debloat · программы · игры · очистка — в одном тёмном окне
</p>

<p align="center">
  <a href="https://github.com/reikidod/WinOptimazer/releases/latest"><img src="https://img.shields.io/github/v/release/reikidod/WinOptimazer?style=for-the-badge&label=stable&color=4B6CA3" alt="Stable release"></a>
  <a href="https://github.com/reikidod/WinOptimazer/releases"><img src="https://img.shields.io/github/v/release/reikidod/WinOptimazer?include_prereleases&style=for-the-badge&label=beta&color=6B5B95" alt="Beta release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue?style=for-the-badge" alt="MIT"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/8.0"><img src="https://img.shields.io/badge/.NET-8-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Windows">
</p>

<p align="center">
  <a href="https://github.com/reikidod/WinOptimazer/releases/latest"><strong>⬇ Скачать последнюю версию</strong></a>
  ·
  <a href="https://github.com/reikidod/WinOptimazer/releases">Все релизы</a>
  ·
  <a href="#что-умеет">Возможности</a>
  ·
  <a href="#безопасность">Безопасность</a>
</p>

---

## Зачем это нужно

Один инструмент вместо набора разрозненных утилит: ускорить систему, убрать лишнее, поставить нужное и подготовить ПК к играм — с понятными описаниями и возможностью откатить изменения.

| | |
|:---:|:---|
| ⚡ | **Оптимизация** — твики реестра и служб с оценкой пользы и предупреждениями |
| 🎮 | **Игры** — Steam / Epic / GOG / Xbox, OptiScaler, режим высокой производительности GPU |
| 📦 | **Программы** — установка популярного софта, Zapret, TG WS Proxy, VC++ / DirectX |
| 🛒 | **Microsoft Store** — каталог с иконками через winget |
| 🗑️ | **Удаление** — программы и UWP с поиском, фильтрами и зачисткой остатков |
| 🧹 | **Очистка** — временные файлы, кэши, автозагрузка |
| 💻 | **Мой ПК** — процессор, видеокарта, память, диски |
| 🎨 | **Темы** — несколько тёмных оформлений, акценты, свой фон |

Обновления ставятся из приложения: **«Проверить обновления»** (каналы stable / beta в настройках).

---

## Быстрый старт

1. Скачайте **`WinOptimizer-Setup.exe`** или **`WinOptimizer-win-x64.zip`** из [последнего релиза](https://github.com/reikidod/WinOptimazer/releases/latest).
2. Установите или распакуйте (лучше не оставлять в «Загрузках»).
3. Запустите `WinOptimizer.exe`. SmartScreen может предупредить о неизвестном издателе — исходники открыты, сборка с GitHub Actions.
4. В разделе **Оптимизация** отметьте твики и примените. Согласитесь на **точку восстановления**, если предложит.
5. Не понравилось — отмените твик: прежние значения сохранены.

> Совет: твики с предупреждением (защита, обновления Windows) включайте только осознанно.

### Требования

- Windows 10 или 11, **64-bit**
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (установщик может поставить сам)
- Права администратора (нужны для HKLM и служб)

---

## Что умеет

| Раздел | Описание |
|--------|----------|
| **Оптимизация системы** | Каталог твиков с описанием, оценкой пользы и откатом. Перед изменениями — точка восстановления. |
| **Игры** | Обнаружение игр, OptiScaler, режим производительности GPU, подсказки по настройкам. |
| **Установка программ** | Популярные программы, Zapret / TG WS Proxy, рекомендуемые runtime’ы с проверкой SHA-256. |
| **Microsoft Store** | Каталог с иконками, установка через winget (с проверкой и починкой). |
| **Удаление программ** | Поиск и фильтры; игры лаунчеров удаляются только через лаунчер. |
| **Очистка и автозагрузка** | Temp, кэши, автозапуск. |
| **Мой ПК** | Сводка по железу и системе. |
| **Настройки** | Приветствие, автозапуск, масштаб UI, анимации, канал обновлений, вкладки. |

---

## Безопасность

- Изменения реестра **запоминаются** и откатываются кнопкой.
- Скачиваемые файлы сверяются по **SHA-256**.
- Игры Steam / Epic и др. **не удаляются напрямую**.
- **Нет телеметрии**: данные о ПК никуда не отправляются. Логи: `%ProgramData%\WinOptimizer\log.txt`.
- Код открыт, релизы собирает **GitHub Actions**.

---

## Каналы обновлений

| Канал | Ветка | Что получает |
|--------|-------|----------------|
| **Stable** | `main` | Проверенные релизы |
| **Beta** | `beta` | Новые функции раньше, возможны шероховатости |

Переключение — в **Настройках** приложения.

---

## Известные ограничения

- Только Windows (не Linux / macOS).
- OptiScaler **нельзя** в онлайн-играх с античитом (VAC, EAC, BattlEye) — риск бана.
- Подбор игровых настроек по железу ещё развивается.

---

## Лицензия

[MIT](LICENSE) — можно использовать, менять и распространять со ссылкой на лицензию.

---

<details>
<summary><b>🛠 Сборка, тесты, подпись и установщик</b> (для разработчиков)</summary>

### Сборка

```powershell
dotnet build -c Release
dotnet run
```

Иконка: `Assets/app.ico` · лицензия копируется в выходную папку.

### Тесты

```powershell
dotnet test tests/WinOptimizer.Tests/WinOptimizer.Tests.csproj -c Release
```

### Установщик (Inno Setup)

Нужны [.NET 8 SDK](https://dotnet.microsoft.com/download) и [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

Результат: `dist\WinOptimizer-Setup.exe`

### Подпись Authenticode

| Тип | SmartScreen | Кому подходит |
|-----|-------------|----------------|
| Самоподпись | Предупреждает, пока не доверите сертификат | Своя машина / тесты |
| OV/EV Code Signing | Репутация накапливается | Раздача другим |

```powershell
.\scripts\New-CodeSigningCert.ps1
.\scripts\Sign-Release.ps1
```

Коммерческий сертификат:

```powershell
dotnet publish -c Release -r win-x64 -o publish
.\scripts\Sign-WithCommercialCert.ps1 -PfxPath "C:\path\to\real.pfx" -Password "secret"
```

Проверка: `signtool verify /pa /v publish\WinOptimizer.exe`

### Windows Defender / SmartScreen

MIT **не отключает** SmartScreen. Смотрят на подпись, репутацию и поведение. Ложные срабатывания: [Microsoft Security Intelligence](https://www.microsoft.com/en-us/wdsi/filesubmission).

`requireAdministrator` нужен для твиков HKLM и служб — из‑за этого SmartScreen строже к «неизвестным» сборкам.

### CI

Push в `main` или `beta` → GitHub Actions: тесты → publish → smoke-test → GitHub Release (`WinOptimizer-win-x64.zip` + `WinOptimizer-Setup.exe`).

</details>

<p align="center">
  <sub>Сделано с заботой о производительности Windows · <a href="https://github.com/reikidod/WinOptimazer">reikidod/WinOptimazer</a></sub>
</p>
