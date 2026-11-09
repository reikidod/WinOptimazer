# WinOptimizer — инструкции для Claude

## Всегда
- Отвечай пользователю по-русски, кратко: что сделано и что дальше.
- Для любого кода сначала применяй скилл `code-quality-pipeline` (7 фиксированных разделов). Скилл требует английский — отчёт пайплайна пиши по-английски, итог для пользователя — по-русски.
- Локально нет dotnet: единственная проверка сборки — CI (`dotnet test` → publish → smoke test). После пуша дождись зелёного CI.
- Перед пушем: `git pull --rebase origin main`. Коммиты — в main, релиз собирается автоматически.
- Скроллбары (Themes/DarkTheme.Extras.xaml: ScrollThumb, ScrollBar, OverlayScroll) не менять без прямой просьбы.
- Новая логика — с тестом в tests/WinOptimizer.Tests.

## Архитектура
WPF/MVVM, net8.0-windows, тёмная тема (Themes/), настройки — Infrastructure/AppSettings.cs (settings.json), твики — Tweaks/TweakCatalog.cs, правки реестра с бэкапом через BackupStore.
