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
    private async Task ApplyAsync()
    {
        if (!IsTweakStateReady || IsBusy) return;
        var changes = Tweaks.Where(t => t.IsChanged && t.IsAvailable).ToList();
        if (changes.Count == 0) return;

        if (!await EnsureRestorePointAsync(changes))
            return;

        IsBusy = true;
        StatusText = "Применяю изменения…";
        var applied = 0;
        var failed = 0;
        try
        {
            foreach (var item in changes)
            {
                try
                {
                    await Task.Run(() => item.Tweak.Apply());
                    item.MarkApplied();
                    applied++;
                    if (item.Tweak.Info.RequiresReboot)
                        RebootRequired = true;
                }
                catch (Exception ex)
                {
                    failed++;
                    Logger.Error($"Твик {item.Tweak.Info.Id}", ex);
                }
            }

            RecordOperation("Твики", $"Применено: {applied}, ошибок: {failed}", failed == 0 ? "Успех" : "Частично");
            StatusText = failed == 0
                ? $"Готово. Применено изменений: {applied}."
                : $"Готово с ошибками. Успешно: {applied}, ошибок: {failed}.";
            await RefreshTweakStatesAsync();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(PendingText));
        }
    }

    private async Task<bool> EnsureRestorePointAsync(IReadOnlyList<TweakViewModel> changes)
    {
        if (!AppSettings.Current.AskRestorePoint) return true;
        var need = changes.Any(c => c.Tweak.Info.RequiresReboot || !string.IsNullOrWhiteSpace(c.Tweak.Info.Warning));
        if (!need) return true;

        var ok = _dialogs.Confirm(
            "Точка восстановления",
            "Перед системными изменениями рекомендуется создать точку восстановления Windows. Создать сейчас?");
        if (!ok) return true;

        try
        {
            StatusText = "Создаю точку восстановления…";
            await Task.Run(() => _restorePoints.Create("WinOptimizer"));
            RecordOperation("Система", "Точка восстановления", "Создана");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Точка восстановления", ex);
            return _dialogs.Confirm(
                "Не удалось создать точку восстановления",
                $"{ex.Message}\n\nПродолжить применение изменений без точки восстановления?");
        }
    }

    private async Task CreateManualRestorePointAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = "Создаю точку восстановления…";
            await Task.Run(() => _restorePoints.Create("WinOptimizer (ручная)"));
            RecordOperation("Система", "Точка восстановления (ручная)", "Создана");
            StatusText = "Точка восстановления создана.";
            _dialogs.Info("Готово", "Точка восстановления Windows создана.", DialogKind.Success);
        }
        catch (Exception ex)
        {
            Logger.Error("Ручная точка восстановления", ex);
            _dialogs.Info("Ошибка", ex.Message, DialogKind.Warning);
            StatusText = "Не удалось создать точку восстановления.";
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshInfoAsync()
    {
        if (IsInfoLoading) return;
        IsInfoLoading = true;
        try
        {
            var sections = await Task.Run(() => _systemInfo.Collect());
            InfoSections.Clear();
            foreach (var s in sections) InfoSections.Add(s);
            OnPropertyChanged(nameof(HasNoInfo));
        }
        catch (Exception ex)
        {
            Logger.Error("System info", ex);
            StatusText = "Не удалось получить сведения о системе.";
        }
        finally { IsInfoLoading = false; }
    }

    private async Task RefreshTweakStatesAsync()
    {
        try
        {
            StatusText = "Читаю состояние твиков…";
            await Task.Run(() =>
            {
                foreach (var t in Tweaks)
                    t.RefreshState();
            });
            IsTweakStateReady = true;
            OnPropertyChanged(nameof(FilteredTweaks));
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(PendingText));
            StatusText = "Готово";
        }
        catch (Exception ex)
        {
            Logger.Error("Refresh tweak states", ex);
            StatusText = "Не удалось прочитать состояние твиков.";
        }
    }

    public async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var result = await _updates.CheckAsync();
            if (result.UpdateAvailable)
            {
                UpdateAvailable = true;
                _pendingDownloadUrl = result.DownloadUrl;
                UpdateStatus = $"Доступна версия {result.LatestVersion}";
                if (AskInstallUpdate(result))
                    await ApplyUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Startup update check", ex);
        }
    }

    private bool AskInstallUpdate(UpdateCheckResult result)
    {
        return _dialogs.Confirm(
            "Обновление WinOptimizer",
            $"Доступна версия {result.LatestVersion} (сейчас {result.CurrentVersion}).\n\n{result.ReleaseNotes}\n\nСкачать и установить?");
    }

    private async Task CheckUpdateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        UpdateStatus = "Проверяю обновления…";
        try
        {
            var result = await _updates.CheckAsync();
            if (result.UpdateAvailable)
            {
                UpdateAvailable = true;
                _pendingDownloadUrl = result.DownloadUrl;
                UpdateStatus = $"Доступна версия {result.LatestVersion}";
                if (AskInstallUpdate(result))
                    await ApplyUpdateAsync();
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatus = "Установлена последняя версия";
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Check update", ex);
            UpdateStatus = "Не удалось проверить обновления";
        }
        finally { IsBusy = false; }
    }

    private async Task ApplyUpdateAsync()
    {
        if (IsBusy || _pendingDownloadUrl is null) return;
        IsBusy = true;
        UpdateStatus = "Скачиваю обновление…";
        try
        {
            await _updates.DownloadAndInstallAsync(_pendingDownloadUrl);
            UpdateStatus = "Обновление установлено. Перезапустите приложение.";
        }
        catch (Exception ex)
        {
            Logger.Error("Apply update", ex);
            UpdateStatus = "Не удалось установить обновление";
            _dialogs.Info("Ошибка обновления", ex.Message, DialogKind.Warning);
        }
        finally { IsBusy = false; }
    }

    private void ReportUnexpected(Exception ex)
    {
        Logger.Error("Command failed", ex);
        StatusText = "Ошибка: " + ex.Message;
        try
        {
            _dialogs.Info("Ошибка",
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в {AppPaths.LogFile}",
                DialogKind.Warning);
        }
        catch { /* ignore UI errors */ }
    }

    private void OnTweakPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TweakViewModel.IsSelected) or nameof(TweakViewModel.IsChanged) or nameof(TweakViewModel.IsAvailable))
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(PendingText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ResetSelection()
    {
        if (!IsTweakStateReady || IsBusy) return;
        foreach (var t in Tweaks)
            t.ResetToApplied();
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingText));
    }

    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("OpenLink", ex);
        }
    }
}
