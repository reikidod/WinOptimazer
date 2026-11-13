<div align="center">

<img src="docs/banner.svg" alt="WinOptimizer — единый центр управления Windows" width="100%">

<br>

# WinOptimizer

### Единый центр управления Windows 10/11

Настройка, обслуживание и оптимизация системы в одном приложении. Контролируемые изменения, понятные предупреждения и возможность отката — без лишней сложности.

<br>

[![Stable](https://img.shields.io/github/v/release/reikidod/WinOptimazer?style=for-the-badge&label=STABLE&color=4B6CA3)](https://github.com/reikidod/WinOptimazer/releases/latest)
[![Beta](https://img.shields.io/github/v/release/reikidod/WinOptimazer?include_prereleases&style=for-the-badge&label=BETA&color=C0262D)](https://github.com/reikidod/WinOptimazer/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/reikidod/WinOptimazer/ci.yml?branch=main&style=for-the-badge&label=STABLE%20BUILD)](https://github.com/reikidod/WinOptimazer/actions/workflows/ci.yml)
[![Загрузки](https://img.shields.io/github/downloads/reikidod/WinOptimazer/total?style=for-the-badge&label=DOWNLOADS&color=353840)](https://github.com/reikidod/WinOptimazer/releases)
[![MIT](https://img.shields.io/badge/LICENSE-MIT-8AAAD8?style=for-the-badge)](LICENSE)

![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-202225?style=flat-square&logo=windows11&logoColor=8AAAD8)
![.NET 8](https://img.shields.io/badge/.NET-8-202225?style=flat-square&logo=dotnet&logoColor=8AAAD8)
![WPF](https://img.shields.io/badge/UI-WPF-202225?style=flat-square)
![C#](https://img.shields.io/badge/C%23-202225?style=flat-square&logo=csharp&logoColor=8AAAD8)

<br>

[**Скачать Stable**](https://github.com/reikidod/WinOptimazer/releases/latest) · [Бета-версии](https://github.com/reikidod/WinOptimazer/releases) · [Официальный сайт](https://reikidod.github.io/WinOptimazer-site/) · [Возможности](#возможности) · [Безопасность](#безопасность) · [Обратная связь](https://github.com/reikidod/WinOptimazer/issues/new/choose)

</div>

---

> [!IMPORTANT]
> **Стабильный канал:** ветка `main` предназначена для повседневного использования. Новые функции сначала проходят проверку в `beta` и публикуются отдельно. Если хотите тестировать предварительные возможности, используйте Beta-релизы.

## Возможности

<table>
<tr>
<td width="50%" valign="top">

### ⚙️ Оптимизация системы
Каталог твиков реестра и служб с описаниями, оценкой пользы и предупреждениями. Исходные значения сохраняются — применённые изменения можно отменить.

</td>
<td width="50%" valign="top">

### 🎮 Игровой режим
Инструменты для подготовки Windows к играм, обнаружение библиотек Steam, Epic, GOG и Xbox, настройки производительности GPU и поддержка OptiScaler.

</td>
</tr>
<tr>
<td valign="top">

### 📦 Установка программ
Установка популярных приложений, необходимых компонентов, VC++ и DirectX, а также дополнительных утилит.

</td>
<td valign="top">

### 🛒 Microsoft Store
Каталог приложений с иконками и установкой через winget.

</td>
</tr>
<tr>
<td valign="top">

### 🧹 Очистка и обслуживание
Очистка временных файлов и кэшей, управление автозагрузкой и удаление ненужных приложений.

</td>
<td valign="top">

### 💻 Информация о ПК
Сведения о процессоре, видеокарте, оперативной памяти, накопителях и состоянии системы.

</td>
</tr>
<tr>
<td valign="top">

### 🎨 Персонализация
Несколько тёмных тем, цветовые акценты и настройка фона приложения.

</td>
<td valign="top">

### ↩️ Контроль изменений
Возможность отменять поддерживаемые твики и возвращать сохранённые исходные параметры.

</td>
</tr>
</table>

## Быстрый старт

1. Откройте [страницу последнего стабильного релиза](https://github.com/reikidod/WinOptimazer/releases/latest).
2. Скачайте установщик `WinOptimizer-Setup.exe` или архив `WinOptimizer-win-x64.zip`, если он доступен в релизе.
3. Установите приложение или распакуйте архив в удобную папку.
4. Запустите `WinOptimizer.exe`. Для системных настроек приложению потребуются права администратора.
5. Перед применением твиков внимательно прочитайте их описание и предупреждения.

> [!TIP]
> Перед масштабными изменениями системы создайте точку восстановления Windows. Не применяйте твики, назначение которых вам непонятно.

### Системные требования

| Компонент | Требование |
|---|---|
| Операционная система | Windows 10 или Windows 11, 64-bit |
| Среда выполнения | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Права доступа | Администратор для операций с HKLM и системными службами |

## Безопасность и прозрачность

- Поддерживаемые изменения реестра сохраняют исходные значения для последующего отката.
- Скачиваемые установщики и библиотеки проверяются по SHA-256 там, где это предусмотрено механизмом загрузки.
- Игры из Steam, Epic и других лаунчеров не удаляются напрямую.
- Приложение не отправляет сведения о компьютере на сторонние серверы.
- Исходный код открыт; сборки публикуются через GitHub Releases и GitHub Actions.

Логи приложения: `%ProgramData%\WinOptimizer\log.txt`.

О найденных уязвимостях сообщайте в соответствии с [SECURITY.md](SECURITY.md).

## 🔄 Каналы обновлений

| Канал | Ветка | Назначение |
|---|---|---|
| **Stable** | `main` | Основная версия для повседневного использования |
| **Beta** | `beta` | Предварительные функции и изменения для тестирования |

Бета-версия может содержать незавершённые функции и ошибки. Для обычного использования выбирайте стабильный релиз.

## ⚠️ Известные ограничения

- Приложение предназначено только для Windows.
- OptiScaler не следует использовать в онлайн-играх с античитом (VAC, EAC, BattlEye): это может привести к блокировке аккаунта.
- Некоторые игровые настройки и рекомендации продолжают развиваться.
- Результат системных твиков зависит от конфигурации Windows и установленного программного обеспечения.

## 🤝 Обратная связь

Нашли ошибку или хотите предложить улучшение?

- [Сообщить об ошибке](https://github.com/reikidod/WinOptimazer/issues/new/choose)
- [Посмотреть открытые задачи](https://github.com/reikidod/WinOptimazer/issues)
- [Посмотреть историю изменений и релизы](https://github.com/reikidod/WinOptimazer/releases)

## 📄 Лицензия

Проект распространяется под лицензией [MIT](LICENSE).

---

<details>
<summary><b>🛠 Сборка, тестирование и разработка</b></summary>

### Сборка

Требуется [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet restore
dotnet build -c Release
dotnet run
```

### Тесты

```powershell
dotnet test tests/WinOptimizer.Tests/WinOptimizer.Tests.csproj -c Release
```

### Установщик

Для создания установщика потребуется [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

### Структура проекта

- `Views/` — XAML-представления приложения.
- `ViewModels/` — логика представления и команды.
- `Services/` — системные сервисы.
- `Tweaks/` — каталог системных настроек.
- `Themes/` — ресурсы оформления.
- `Assets/` — иконки и ресурсы.

</details>

<div align="center">

<sub>WinOptimizer · Открытый проект для удобного управления Windows</sub>

</div>
