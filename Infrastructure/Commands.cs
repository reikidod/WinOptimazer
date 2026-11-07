using System.Windows.Input;

namespace WinOptimizer.Infrastructure;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        var value = parameter is T t ? t : default;
        return _canExecute?.Invoke(value) ?? true;
    }

    public void Execute(object? parameter)
    {
        var value = parameter is T t ? t : default;
        _execute(value);
    }
}

/// <summary>
/// Асинхронная команда. Повторный запуск во время выполнения блокируется.
/// Исключения не уходят в async void: они логируются и передаются в <c>onError</c>.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // Граница UI: исключение нельзя пропускать из async void, иначе упадёт процесс.
            Logger.Error("Необработанная ошибка в команде", ex);
            _onError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    public AsyncRelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        if (_running) return false;
        var value = parameter is T t ? t : default;
        return _canExecute?.Invoke(value) ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        var value = parameter is T t ? t : default;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(value);
        }
        catch (Exception ex)
        {
            Logger.Error("Необработанная ошибка в команде", ex);
            _onError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
