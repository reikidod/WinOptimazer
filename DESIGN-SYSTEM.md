# WinOptimizer Design System

Reference: WinOptimizer Control Center — a practical Windows desktop utility, not a promotional dashboard.

## Visual direction
- Dense, calm, technical interface inspired by native Windows administration tools.
- Graphite surfaces with clear hierarchy; no glass effects, neon glows, decorative gradients, or floating-card grids.
- Reuse the existing application icon everywhere. Do not draw substitute brand marks or create alternate logos.
- The green accent is reserved for active navigation, enabled states, progress, and primary actions.
- Prefer tables, compact metric panels, grouped settings rows, segmented controls, and clear section dividers over repeated cards.

## Palette
- Window: #14191D; sidebar: #191F24.
- Main surface: #1D2429; hover surface: #242D32.
- Borders: #303A40; hover border: #596B60.
- Accent: #A5C982; deep accent: #526D45.
- Primary text: #E7ECEF; secondary: #AAB5BE; muted: #7E8A94.
- Success: #9BCB82; warning: #D6AA68; danger: #D9827D.

## Components
- Surfaces use 4–6 px corner radii, 1 px borders, no drop shadows.
- Dashboard metrics use flat panels with readable values, compact labels, and restrained history charts.
- Tweak and setting entries use row-like panels with a narrow state rail on hover/focus.
- Section headers and expanders remain visually distinct from their contents.
- Badges are compact, outlined labels rather than pills.
- Buttons use restrained hover/focus feedback and no bounce or glow.
- Advanced settings remain collapsible and do not dominate the page.

## Engineering constraints
- Preserve all existing commands, bindings, view models, settings, navigation behavior, and compatibility resource keys.
- Do not replace the existing app icon or introduce third-party icon packs.
- Keep keyboard focus visible and maintain readable contrast in both themes.
- Respect reduced-motion settings and avoid expensive continuous animations.
