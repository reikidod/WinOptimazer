# WinOptimizer Design System v3 — «Signal Control Center»

> **Приоритет:** дизайн — первостепенная часть любой UI-задачи. Любое изменение интерфейса обязано следовать этому документу.

Основа: Windows 11 Fluent + фирменный знак (белая W + зелёная точка). Глубокий уголь, один яркий акцент логотипа `#00C58E`, слоистые поверхности, компактные радиусы, минимум декора.

## Принципы
1. **Слои вместо теней.** Глубина создаётся сменой тона: окно → боковая панель → карточка → карточка при наведении. Тени только у всплывающих элементов.
2. **Один акцент.** Зелёный логотипа (`#00C58E`) = выбор, фокус, главное действие, прогресс. Статусные цвета только для состояния.
3. **Читаемость.** Основной текст ≥ 13 px, пары цветов ≥ 4.5:1 на карточке. Мелкий текст (10–11 px) только для подписей-меток.
4. **Спокойное движение.** Только короткие fade (≤ 250 мс). Без «пружин», масштабирования при нажатии, бесконечных анимаций (кроме IsIndeterminate).
5. **Не ломать существующее.** Все ключи ресурсов, команды, bindings, настройки и навигация сохраняются.

## Токены (Themes/DarkTheme.xaml + ThemeManager aurora)
| Назначение | Ключ | Значение |
|---|---|---|
| Окно | BgWindow | #101418 |
| Боковая панель | BgSidebar | #151A1F |
| Карточка | BgCard | #1A2127 |
| Карточка hover | BgCardHover | #222A31 |
| Поле ввода | BgInput | #12171C |
| Граница / hover | Border / BorderHover | #2C363E / #3D4A54 |
| Акцент | Accent | #00C58E |
| Акцент насыщенный | AccentStrong | #00A577 |
| Акцент мягкий | AccentSoft | #00C58E @ 15% |
| Кнопка действия | SiteAccentButton | #00C58E, текст белый |
| Дорожка выкл. | TrackOff | #2A343C |
| Текст | TextPrimary / Secondary / Dim | #F2F5F3 / #B5C0BA / #8E9B94 |
| Состояния | Success / Warn / Danger / Neutral | #2FD49A / #F0B35C / #F2777A / #8591A8 |
| Бренд-градиент | BrandGradient | #00C58E → #00A577 |

Новые цвета добавлять только через токены, не хардкодом в XAML.

## Геометрия
- Рабочие панели (BorderCard, DashboardCard, HoverCard): радиус **3–4**.
- Контролы (кнопки, поля): радиус **6–8**; логотип в сайдбаре: **8**.
- Рамка 1 px. Шаг отступов: 4 / 8 / 12 / 16 / 24.
- Без крупных «пузырей» и радиусов > 16 на основных поверхностях.

## Компоненты
- **Кнопка**: фон BgCardHover, рамка Border; hover/press — лёгкая подсветка; фокус — рамка Accent. **AccentButton** — заливка SiteAccentButton (зелёный логотипа).
- **Навигация**: компактные прямоугольные пункты; выбранный — AccentSoft + текст/иконка Accent + тонкий rail слева.
- **Карточка**: BgCard, рамка 1 px, hover — BorderHover. Без теней и «плавающих» плит.
- **Hero / телеметрия**: единая статус-панель, не декоративный баннер.
- **Индикатор оптимизации**: дуга BrandGradient, плавное заполнение.

## Запреты
- Не менять скроллбары без прямой просьбы.
- Не подменять иконку приложения (DrawingImage + Assets/app.ico.b64).
- Без размытия/«стекла», неоновых свечений, декоративных градиентов (кроме BrandGradient), анимаций ради анимаций.
- Без хардкода цветов вне токенов.

## Перед коммитом UI-изменений
Проверить: XAML валиден, ключи ресурсов сохранены, фокус виден, контраст ≥ 4.5:1, нет скачков layout, CI на `beta` зелёный.


## Control Center visual direction (Signal)

- Default surface: deep charcoal with logo-green operational accent (#00C58E).
- Avoid neon gradients, glow, glass, decorative blobs and promotional hero banners.
- The dashboard hero is a status-and-actions panel.
- Metric cards prioritize label, live value, context, threshold and recent history.
- Information groups use technical rows and separators instead of floating cards.
- Interactive states use a clear accent border or rail, not shadows or scale effects.
- Preserve the existing WinOptimizer icon and all command bindings when changing layout.


## Dashboard redesign — telemetry console

The home screen uses one unified four-channel telemetry panel rather than four isolated floating cards. CPU, memory, graphics and storage share a common measurement grid with thin dividers, compact history traces and consistent hierarchy. Additional drives are shown as full-width inventory rows so the drive name, usage bar, capacity summary and utilization percentage can be compared at a glance. Preserve live bindings and command behavior when changing this layout.


## Control Desk refresh — November 2026

The visible application surface uses a native desktop control-desk language: strong page headings, compact rectangular navigation and action controls, bordered work panels, and a restrained active-state rail. Avoid decorative gradients, floating cards, oversized rounded tiles, and English-only telemetry labels in Russian UI. Preserve all existing bindings, commands, theme selection, and the shared WinOptimizer brand icon.
