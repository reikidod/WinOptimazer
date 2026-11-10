using System.Security;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.ViewModels;

/// <summary>Одна программа автозагрузки с переключателем «запускать при входе в Windows».</summary>
public sealed class StartupItemViewModel : ObservableObject
{
    private readonly IStartupService _service;
    private readonly OperationHistoryService _history;
    private StartupEntry _entry;
    private string? _errorText;

    public StartupItemViewModel(StartupEntry entry, IStartupService service, OperationHistoryService? history = null)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _history = history ?? new OperationHistoryService();
        ToggleCommand = new RelayCommand(Toggle);
    }

    public string Name => _entry.Name;

    public string Command => _entry.Command;

    public string ScopeTitle => _entry.ScopeTitle;

    public bool IsEnabled => _entry.Enabled;

    public ICommand ToggleCommand { get; }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetField(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    private void RecordHistory(string operation, string result)
    {
        try
        {
            _history.Record("Автозагрузка", operation, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Ошибка журнала не должна отменять уже выполненное изменение автозагрузки.
            Logger.Error("Не удалось записать изменение автозагрузки в историю", ex);
        }
    }

    private void Toggle()
    {
        var target = !_entry.Enabled;
        try
        {
            _service.SetEnabled(_entry, target);
            _entry = _entry with { Enabled = target };
            ErrorText = null;
            var result = target ? "Включена" : "Отключена";
            Logger.Info($"Автозагрузка «{_entry.Name}»: {result.ToLowerInvariant()}");
            RecordHistory($"Автозагрузка «{_entry.Name}»", result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            Logger.Error($"Не удалось изменить автозагрузку «{_entry.Name}»", ex);
            ErrorText = $"Не удалось изменить: {ex.Message}";
            RecordHistory($"Автозагрузка «{_entry.Name}»", "Ошибка");
        }
        finally
        {
            // Возвращает переключатель в реальное состояние, даже если запись не удалась.
            OnPropertyChanged(nameof(IsEnabled));
        }
    }
}
