# WinOptimizer Design System v2 — «WinOptimizer Control Center»

> **Приоритет:** дизайн — первостепенная часть любой UI-задачи. Любое изменение интерфейса обязано следовать этому документу. Правила в нём выше старых описаний дизайна (graphite/green) в других файлах.

Основа: Windows 11 Fluent. Тёмная slate-палитра, один спокойный синий акцент, слоистые поверхности, мягкие скругления, минимум декора.

## Принципы
1. **Слои вместо теней.** Глубина создаётся сменой тона: окно → боковая панель → карточка → карточка при наведении. Тени только у всплывающих элементов (контекстное меню, переключатель).
2. **Один акцент.** Синий = выбор, фокус, главное действие, прогресс. Статусные цвета только для состояния.
3. **Читаемость.** Основной текст ≥ 13 px, пары цветов ≥ 4.5:1 на карточке. Мелкий текст (10–11 px) только для подписей-меток.
4. **Спокойное движение.** Только короткие fade (≤ 250 мс). Без «пружин», масштабирования при нажатии, бесконечных анимаций (кроме IsIndeterminate).
5. **Не ломать существующее.** Все ключи ресурсов, команды, bindings, настройки и навигация сохраняются.

## Токены (Themes/DarkTheme.xaml)
| Назначение | Ключ | Значение |
|---|---|---|
| Окно | BgWindow | #14191D |
| Боковая панель | BgSidebar | #191F24 |
| Карточка | BgCard | #20282D |
| Карточка hover | BgCardHover | #1E2535 |
| Поле ввода | BgInput | #0F131B |
| Граница / hover | Border / BorderHover | #262E3F / #3A4A6B |
| Акцент | Accent | #A5C982 |
| Акцент насыщенный (переключатель, выделение текста) | AccentStrong | #526D45 |
| Акцент мягкий (выбранный пункт, чип) | AccentSoft | #A5C982 @ 15% |
| Кнопка действия | SiteAccentButton | #3D6FE0, текст белый |
| Дорожка выкл. | TrackOff | #2A3246 |
| Текст | TextPrimary / Secondary / Dim | #EAEFF8 / #A4AFC4 / #7C879E |
| Состояния | Success / Warn / Danger / Neutral | #5FD39A / #F0B35C / #F2777A / #8591A8 |
| Бренд-градиент (дуга, мини-индикаторы) | BrandGradient | #A5C982 → #8B7CFF |

Новые цвета добавлять только через токены, не хардкодом в XAML.

## Геометрия
- Карточки и секции: радиус **12**, hero-блок 14; внутренние отступы 18–20.
- Контролы (кнопки, поля): радиус **8**; навигация и кнопка сайдбара: **10**; чипы фильтров: **16** (таблетка); бейджи: 8.
- Рамка 1 px. Шаг отступов: 4 / 8 / 12 / 16 / 24.

## Компоненты
- **Кнопка**: фон BgCardHover, рамка Border; hover/press — белая подсветка 7%/14%; фокус — рамка Accent. **AccentButton** — заливка SiteAccentButton.
- **Навигация**: «таблетка» 46 px; hover — BgCardHover; выбранный — AccentSoft + текст/иконка Accent + 3 px индикатор слева.
- **Карточка (BorderCard, DashboardCard, SettingsCard, HoverCard)**: BgCard, рамка 1 px, hover — BorderHover.
- **Чип фильтра**: выбранный — AccentSoft + рамка и текст Accent.
- **Hero главной**: DashboardCard с мягким градиентом #1B2540 → BgCard; единственный градиентный фон страницы.
- **Индикатор оптимизации**: дуга без стрелки, BrandGradient, плавное заполнение.
- **Раскрывающиеся секции**: основное видно, редкое скрыто; не делать каждый элемент раскрывающимся.

## Запреты
- Не менять скроллбары (ScrollThumb, ScrollBar, OverlayScroll) без прямой просьбы.
- Не менять и не подменять иконку приложения.
- Без размытия/«стекла», неоновых свечений, декоративных градиентов (кроме перечисленных), анимаций ради анимаций.
- Без хардкода цветов вне токенов.

## Перед коммитом UI-изменений
Проверить: XAML валиден, ключи ресурсов сохранены, фокус виден, контраст ≥ 4.5:1, нет скачков layout при обновлении данных, CI на `beta` зелёный.


## Control Center visual direction

- Default surface: graphite with a restrained green operational accent.
- Avoid neon gradients, glow, glass, decorative blobs and promotional hero banners.
- The dashboard hero is a status-and-actions panel.
- Metric cards prioritize label, live value, context, threshold and recent history.
- Information groups use technical rows and separators instead of floating cards.
- Interactive states use a clear accent border or rail, not shadows or scale effects.
- Preserve the existing WinOptimizer icon and all command bindings when changing layout.
