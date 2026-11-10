using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
using WinOptimizer.Tweaks;
using WinOptimizer.Views;

namespace WinOptimizer.ViewModels;

public sealed partial class MainViewModel
{
    public async Task InitializeAsync()
    {
        await RefreshDashboardAsync();
        StatusText = "Готово";
    }

    private async Task RefreshDashboardAsync()
    {
        try
        {
            DashboardStatus = "Обновляю системные показатели…";
            DashboardSnapshot = await Task.Run(SystemMonitorService.Collect);
            RefreshRecentOperations();
            DashboardStatus = $"Обновлено: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось обновить панель состояния", ex);
            DashboardStatus = "Не удалось получить часть показателей: " + ex.Message;
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        var snapshot = DashboardSnapshot;
        if (snapshot is null)
        {
            await RefreshDashboardAsync();
            snapshot = DashboardSnapshot;
        }
        if (snapshot is null)
        {
            DashboardStatus = "Диагностика недоступна: не удалось получить системные показатели.";
            return;
        }

        DashboardStatus = "Проверяю состояние Windows…";
        DiagnosticFindings = await Task.Run(() => _diagnostics.Run(snapshot));
        RecordOperation("Диагностика", "Проверка состояния Windows", "Завершено");
        DashboardStatus = $"Диагностика завершена: найдено замечаний — {DiagnosticFindings.Count(x => x.Level != DiagnosticLevel.Info)}.";
    }

    private void RefreshRecentOperations()
    {
        RecentOperations.Clear();
        foreach (var entry in _history.GetRecent(8)) RecentOperations.Add(entry);
    }

    private void RecordOperation(string category, string operation, string result)
    {
        try
        {
            var entry = _history.Record(category, operation, result);
            RecentOperations.Insert(0, entry);
            while (RecentOperations.Count > 8) RecentOperations.RemoveAt(RecentOperations.Count - 1);
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось сохранить историю операций", ex);
        }
    }

    private void SaveProfile()
    {
        if (!IsTweakStateReady || IsBusy) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить профиль оптимизации",
            Filter = "Профиль WinOptimizer (*.json)|*.json",
            FileName = "WinOptimizer-profile.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var profile = OptimizationProfileService.Create(
                Path.GetFileNameWithoutExtension(dialog.FileName),
                Tweaks.Where(t => t.IsSelected).Select(t => t.Tweak.Info.Id));
            OptimizationProfileService.Save(dialog.FileName, profile);
            StatusText = $"Профиль сохранён: {profile.Name}.";
            _dialogs.Info("Профиль сохранён",
                $"Сохранено выбранных твиков: {profile.SelectedTweakIds.Count}. Профиль не применяет изменения автоматически.",
                DialogKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logger.Error("Не удалось сохранить профиль оптимизации", ex);
            _dialogs.Info("Ошибка профиля", ex.Message, DialogKind.Warning);
        }
    }

    private void LoadProfile()
    {
        if (!IsTweakStateReady || IsBusy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Загрузить профиль оптимизации",
            Filter = "Профиль WinOptimizer (*.json)|*.json",
            DefaultExt = ".json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var profile = OptimizationProfileService.Load(dialog.FileName);
            var selected = new HashSet<string>(profile.SelectedTweakIds, StringComparer.OrdinalIgnoreCase);
            foreach (var tweak in Tweaks)
                tweak.IsSelected = selected.Contains(tweak.Tweak.Info.Id);

            var known = Tweaks.Select(t => t.Tweak.Info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = selected.Count(id => !known.Contains(id));
            StatusText = $"Профиль «{profile.Name}» загружен. Изменений к применению: {PendingCount}.";
            _dialogs.Info("Профиль загружен",
                $"Выбрано твиков: {selected.Count - unknown}. Неизвестных идентификаторов: {unknown}. Изменения ещё не применены — проверьте список и нажмите «Применить».",
                DialogKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Logger.Error("Не удалось загрузить профиль оптимизации", ex);
            _dialogs.Info("Ошибка профиля", ex.Message, DialogKind.Warning);
        }
    }

    private void Navigate(string? pageName)
    {
        if (!Enum.TryParse<AppPage>(pageName, ignoreCase: true, out var page))
            return;

        if (page == AppPage.Apps && !AppSettings.Current.IsTabVisible("Apps"))
        {
            page = AppSettings.Current.IsTabVisible("Debloat")
                ? AppPage.Debloat
                : AppSettings.Current.IsTabVisible("Store") ? AppPage.Store : AppPage.Settings;
        }

        SelectPage(page, true);
    }

    /// <summary>Переход к предыдущему открытому разделу (Mouse 4 / кнопка «Назад»).</summary>
    public bool NavigateBack() => NavigateHistory(forward: false);

    /// <summary>Возврат к разделу, из которого был выполнен переход назад (Mouse 5 / «Вперёд»).</summary>
    public bool NavigateForward() => NavigateHistory(forward: true);

    private bool _isHistoryNavigation;

    private bool NavigateHistory(bool forward)
    {
        var target = _page;
        var moved = forward
            ? _navigationHistory.TryForward(_page, IsPageVisible, out target)
            : _navigationHistory.TryBack(_page, IsPageVisible, out target);
        if (!moved) return false;

        _isHistoryNavigation = true;
        try
        {
            SelectPage(target, true);
        }
        finally
        {
            _isHistoryNavigation = false;
        }

        return true;
    }

    private static bool IsPageVisible(AppPage page) => page switch
    {
        AppPage.Tweaks => AppSettings.Current.IsTabVisible("Tweaks"),
        AppPage.Debloat => AppSettings.Current.IsTabVisible("Debloat"),
        AppPage.Apps => AppSettings.Current.IsTabVisible("Apps"),
        AppPage.Store => AppSettings.Current.IsTabVisible("Store"),
        AppPage.Games => AppSettings.Current.IsTabVisible("Games"),
        AppPage.System => AppSettings.Current.IsTabVisible("System"),
        AppPage.Maintenance => AppSettings.Current.IsTabVisible("Maintenance"),
        _ => true,
    };

    private async Task InitializePageAsync(AppPage page)
    {
        if (!_initializedPages.Add(page)) return;
        try
        {
            switch (page)
            {
                case AppPage.Tweaks:
                    await RefreshTweakStatesAsync();
                    break;
                case AppPage.System:
                    await RefreshInfoAsync();
                    break;
                case AppPage.Maintenance:
                    await Maintenance.InitializeAsync();
                    break;
                case AppPage.Debloat:
                    await Debloat.InitializeAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _initializedPages.Remove(page);
            Logger.Error($"Не удалось загрузить раздел {page}", ex);
            StatusText = $"Не удалось загрузить раздел: {page}";
        }
    }

    public string BuildInfoReport() => SystemInfoService.ToPlainText(InfoSections);

    private void SetCategory(string? category)
    {
        if (!IsTweakStateReady || IsBusy) return;
        if (!string.IsNullOrWhiteSpace(category))
            TweakCategory = category;
    }

    private void ToggleGroup(TweakViewModel? header)
    {
        if (header is null || !header.IsGroupHeader) return;
        header.IsGroupExpanded = !header.IsGroupExpanded;
        OnPropertyChanged(nameof(FilteredTweaks));
    }

    public bool IsSettingsPage
    {
        get => _page == AppPage.Settings;
        set => SelectPage(AppPage.Settings, value);
    }

    private void EnsurePageVisible()
    {
        if (_page == AppPage.Settings) return;
        var hidden = _page switch
        {
            AppPage.Tweaks => !AppSettings.Current.IsTabVisible("Tweaks"),
            AppPage.Debloat => !AppSettings.Current.IsTabVisible("Debloat"),
            AppPage.Apps => !AppSettings.Current.IsTabVisible("Apps"),
            AppPage.Store => !AppSettings.Current.IsTabVisible("Store"),
            AppPage.Games => !AppSettings.Current.IsTabVisible("Games"),
            AppPage.System => !AppSettings.Current.IsTabVisible("System"),
            AppPage.Maintenance => !AppSettings.Current.IsTabVisible("Maintenance"),
            _ => false,
        };
        if (hidden)
            SelectPage(AppPage.Settings, true);
    }

    private void SelectPage(AppPage page, bool selected)
    {
        if (!selected || _page == page)
            return;

        if (!_isHistoryNavigation)
            _navigationHistory.Record(_page, page);

        _page = page;
        OnPropertyChanged(nameof(IsDashboardPage));
        OnPropertyChanged(nameof(IsTweaksPage));
        OnPropertyChanged(nameof(IsSystemPage));
        OnPropertyChanged(nameof(IsMaintenancePage));
        OnPropertyChanged(nameof(IsDebloatPage));
        OnPropertyChanged(nameof(IsApplicationsGroupPage));
        OnPropertyChanged(nameof(IsAppsPage));
        OnPropertyChanged(nameof(IsStorePage));
        OnPropertyChanged(nameof(IsGamesPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        _ = InitializePageAsync(page);
    }
}
