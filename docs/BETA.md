# WinOptimizer 0.6 Beta

<div align="center">

**Предварительная версия единого центра управления Windows 10/11**

[![Beta CI](https://img.shields.io/github/actions/workflow/status/reikidod/WinOptimazer/ci.yml?branch=beta&style=for-the-badge&label=BETA%20CI)](https://github.com/reikidod/WinOptimazer/actions/workflows/ci.yml)
[![Beta releases](https://img.shields.io/github/v/release/reikidod/WinOptimazer?include_prereleases&filter=v0.6.*-beta&style=for-the-badge&label=BETA)](https://github.com/reikidod/WinOptimazer/releases)

[**Скачать Beta**](https://github.com/reikidod/WinOptimazer/releases) · [**Официальный сайт**](https://reikidod.github.io/WinOptimazer-site/) · [**Сообщить об ошибке**](https://github.com/reikidod/WinOptimazer/issues/new/choose)

</div>

---

> [!WARNING]
> Beta предназначена для тестирования. Перед применением системных твиков внимательно читайте предупреждения и создавайте точку восстановления. Для повседневного использования выбирайте стабильный релиз.

## Что входит в Beta 0.6

| Раздел | Возможности |
|---|---|
| **Главная панель** | Состояние CPU, RAM, дисков и сети, диагностика, журнал операций |
| **Оптимизация** | Лёгкий, средний и агрессивный профили; ручной выбор сложных твиков; информация о рисках и откате |
| **Мой ПК** | Сведения о системе, дополнительные диски, активные и завершённые процессы |
| **Приложения** | Установка, удаление и Microsoft Store |
| **Обслуживание** | Очистка, автозагрузка и инструменты Windows |
| **Обновления** | Скачивание EXE последнего релиза выбранного канала без автоматического запуска |

## Навигация

- Основные разделы доступны через боковую панель.
- **Mouse 4** (боковая кнопка «Назад») возвращает к предыдущему посещённому разделу.
- **Mouse 5** (боковая кнопка «Вперёд») восстанавливает следующий раздел в истории.
- Если раздел был скрыт в настройках, история пропускает его и выбирает доступную страницу.

## Визуальная система

Интерфейс использует общую стилистику официального сайта: графитовые поверхности, мягкие красно-синие свечения, тонкую сетку и стально-голубой акцент. Сворачиваемые панели помогают держать сложные настройки закрытыми до момента, когда они нужны.

## Безопасное тестирование

1. Скачайте установщик из [Beta Releases](https://github.com/reikidod/WinOptimazer/releases).
2. Перед применением твиков ознакомьтесь с описанием, уровнем подтверждения эффекта и предупреждением.
3. Создайте точку восстановления Windows, если приложение предлагает её для выбранной операции.
4. Проверяйте изменения по одному небольшому набору и перезапускайте систему только когда это требуется.
5. Если заметили проблему, откройте [обсуждение ошибок](https://github.com/reikidod/WinOptimazer/issues/9) и приложите версию приложения, сборку Windows и фрагмент журнала без личных данных.

## Проверки качества

- Автоматическая сборка и тесты запускаются при каждом push в `beta`.
- Release workflow собирает и проверяет установочный пакет.
- GitHub Actions: [все запуски](https://github.com/reikidod/WinOptimazer/actions?query=branch%3Abeta).
- Автоматические тесты не заменяют ручную проверку внешнего вида и поведения на Windows 10/11.

## Полезные ссылки

- [История изменений](../CHANGELOG.md)
- [Исходный код](https://github.com/reikidod/WinOptimazer/tree/beta)
- [Stable Releases](https://github.com/reikidod/WinOptimazer/releases/latest)
- [Beta Releases](https://github.com/reikidod/WinOptimazer/releases)
