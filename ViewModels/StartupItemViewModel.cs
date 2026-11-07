using System.Security;
using System.Windows.Input;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.ViewModels;

/// <summary>Одна программа автозагрузки с переключателем «запускать при входе в Windows».</summary>
public sealed class StartupItemViewModel : ObservableObject
{
    private readonly IStartupService _service;
    private StartupEntry _entry;
    private string? _errorText;

    public StartupItemViewModel(StartupEntry entry, IStartupService service)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _service = service ?? throw new ArgumentNullException(nameof(service));
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

    private void Toggle()
    {
        var target = !_entry.Enabled;
        try
        {
            _service.SetEnabled(_entry, target);
            _entry = _entry with { Enabled = target };
            ErrorText = null;
            Logger.Info($"Автозагрузка «{_entry.Name}»: {(target ? "включена" : "отключена")}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            Logger.Error($"Не удалось изменить автозагрузку «{_entry.Name}»", ex);
            ErrorText = $"Не удалось изменить: {ex.Message}";
        }
        finally
        {
            // Возвращает переключатель в реальное состояние, даже если запись не удалась.
            OnPropertyChanged(nameof(IsEnabled));
        }
    }
}
