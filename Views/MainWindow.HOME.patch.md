# MainWindow Home integration (apply manually once)

The full `MainWindow.xaml` is large; apply this patch on top of `beta` / current `Views/MainWindow.xaml`:

1. Sidebar width 280 → 252; window 1240×820 (Fluent shell).
2. Add first nav RadioButton bound to `IsHomePage` with title «Главная».
3. Before «Страница Твики», insert:

```xml
<local:DashboardPage Visibility="{Binding IsHomePage, Converter={StaticResource BoolToVis}}"
                     local:Motion.PageTransition="True" />
```

Ready-made files in project artifacts (local workdir):
- `MainWindow.xaml.v06-home` — full fixed XAML
- `MainWindow-home.patch` — unified diff vs beta

Until applied, the branch keeps a **placeholder** MainWindow — **do not merge** until this file is restored.
