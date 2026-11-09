# Модули настроек (DLL)

## WinOptimizer.Core.dll
Общая инфраструктура:
- `AppPaths`, `Logger`
- `ObservableObject`, `RelayCommand`

Namespace: `WinOptimizer.Infrastructure` (без изменений).

## WinOptimizer.Settings.dll
- `AppSettings`, `ThemeManager`
- `SettingsViewModel`, `ThemeOption`, `AccentOption`
- `Views.SettingsPage`

Namespace типов **те же**, что в монолите → main-проект почти без правок `using`.

## Подключение

В `WinOptimizer.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="modules\WinOptimizer.Settings\WinOptimizer.Settings.csproj" />
</ItemGroup>
```

Удалить из main (перенесены в DLL):
`Infrastructure/AppSettings.cs`, `ThemeManager.cs`, `ObservableObject.cs`, `Commands.cs`, `Logger.cs`, `ViewModels/SettingsViewModel.cs`.

В `MainWindowPages.xaml`:

```xml
xmlns:settings="clr-namespace:WinOptimizer.Views;assembly=WinOptimizer.Settings"
...
<settings:SettingsPage Visibility="{Binding IsSettingsPage, Converter={StaticResource BoolToVis}}"
                       DataContext="{Binding}" />
```
