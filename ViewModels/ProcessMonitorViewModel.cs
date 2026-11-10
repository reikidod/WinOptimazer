using System.Collections.ObjectModel;
using System.Windows.Threading;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.ViewModels;

/// <summary>Строка списка активных процессов (процессы с одним именем объединены).</summary>
public sealed record ProcessUsageRow(string Name, string CpuText, string MemoryText)
{
    public static ProcessUsageRow From(ProcessUsage u) => new(
        u.Count > 1 ? $"{u.Name} ×{u.Count}" : u.Name,
        ProcessFormat.Cpu(u.CpuPercent),
        ProcessFormat.Memory(u.WorkingSetBytes));
}

/// <summary>Строка списка завершённых процессов: когда закрылся, сколько работал и сколько потреблял в последний замер.</summary>
public sealed record ExitedProcessRow(string Name, string ExitedAtText, string RunTimeText, string CpuText, string MemoryText)
{
    public static ExitedProcessRow From(ExitedProcess p) => new(
        p.Name,
        p.ExitedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
        ProcessFormat.Duration(p.RunTime),
        ProcessFormat.Cpu(p.LastCpuPercent),
        ProcessFormat.Memory(p.PeakWorkingSetBytes));
}

/// <summary>
/// Процессы на странице «Мой ПК»: активные (с загрузкой процессора и памятью) и завершённые с момента запуска приложения.
/// Замеры идут всегда (чтобы поймать завершения), но списки на экране обновляются только в раскрытых разделах.
/// </summary>
public sealed class ProcessMonitorViewModel : ObservableObject
{
    private const int MaxActiveRows = 40;
    private static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HiddenInterval = TimeSpan.FromSeconds(8);

    private readonly ProcessTracker _tracker = new(Environment.ProcessorCount);
    private readonly DispatcherTimer _timer;
    private bool _sampling;
    private bool _isPageVisible;
    private bool _showActive;
    private bool _showExited;
    private string _activeHeader = "Активные процессы";
    private string _exitedHeader = "Завершённые процессы";
    private string _activeSummary = string.Empty;

    public ProcessMonitorViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = HiddenInterval };
        _timer.Tick += async (_, _) => await SampleAsync();
    }

    public ObservableCollection<ProcessUsageRow> Active { get; } = new();

    public ObservableCollection<ExitedProcessRow> Exited { get; } = new();

    public string ActiveHeader
    {
        get => _activeHeader;
        private set => SetField(ref _activeHeader, value);
    }

    public string ExitedHeader
    {
        get => _exitedHeader;
        private set => SetField(ref _exitedHeader, value);
    }

    /// <summary>Итог по активным: сколько процессор и память занимают все процессы вместе.</summary>
    public string ActiveSummary
    {
        get => _activeSummary;
        private set => SetField(ref _activeSummary, value);
    }

    public bool ShowActive
    {
        get => _showActive;
        set
        {
            if (SetField(ref _showActive, value) && value) Publish();
        }
    }

    public bool ShowExited
    {
        get => _showExited;
        set
        {
            if (SetField(ref _showExited, value) && value) Publish();
        }
    }

    /// <summary>Открыта ли страница «Мой ПК»: на ней замеры идут чаще.</summary>
    public bool IsPageVisible
    {
        get => _isPageVisible;
        set
        {
            if (!SetField(ref _isPageVisible, value)) return;
            _timer.Interval = value ? VisibleInterval : HiddenInterval;
            if (value) _ = SampleAsync();
        }
    }

    /// <summary>Запускает наблюдение. Вызывать из UI-потока.</summary>
    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        _ = SampleAsync();
    }

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var samples = await Task.Run(ProcessSampler.Take);
            _tracker.Update(samples, DateTime.UtcNow);
            Publish();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.Error("Не удалось обновить список процессов", ex);
        }
        finally
        {
            _sampling = false;
        }
    }

    private void Publish()
    {
        var (cpu, memory) = _tracker.GetTotals();
        ActiveHeader = $"Активные процессы · {_tracker.ActiveCount}";
        ExitedHeader = $"Завершённые процессы · {_tracker.Exited.Count}";
        ActiveSummary = $"Все процессы вместе: процессор {ProcessFormat.Cpu(cpu)}, память {ProcessFormat.Memory(memory)}";

        if (_showActive)
        {
            Replace(Active, _tracker.GetActive(MaxActiveRows).Select(ProcessUsageRow.From));
        }

        if (_showExited)
        {
            Replace(Exited, _tracker.Exited.Select(ExitedProcessRow.From));
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
