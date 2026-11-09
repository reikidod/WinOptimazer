using System.Collections.ObjectModel;
using System.Text.Json;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public enum OperationKind
{
    TweakApply,
    TweakRevert,
    Cleanup,
    Debloat,
    Install,
    Uninstall,
    RestorePoint,
    Profile,
    Other,
}

public enum OperationOutcome
{
    Success,
    Failed,
    Cancelled,
    Partial,
}

public sealed record OperationRecord(
    DateTime Utc,
    OperationKind Kind,
    string Title,
    OperationOutcome Outcome,
    string? Detail = null);

/// <summary>Журнал операций приложения (локальный JSON, без отправки данных).</summary>
public interface IOperationHistoryService
{
    IReadOnlyList<OperationRecord> Recent { get; }
    void Add(OperationKind kind, string title, OperationOutcome outcome, string? detail = null);
    void Clear();
}

public sealed class OperationHistoryService : IOperationHistoryService
{
    private const int MaxEntries = 200;
    private readonly string _path;
    private readonly List<OperationRecord> _items = new();
    private readonly object _gate = new();

    public OperationHistoryService(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "operation-history.json");
        Load();
    }

    public IReadOnlyList<OperationRecord> Recent
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    public void Add(OperationKind kind, string title, OperationOutcome outcome, string? detail = null)
    {
        var rec = new OperationRecord(DateTime.UtcNow, kind, title, outcome, detail);
        lock (_gate)
        {
            _items.Insert(0, rec);
            if (_items.Count > MaxEntries)
                _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
            SaveUnlocked();
        }
        Logger.Info($"[history] {kind} {outcome}: {title}");
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            SaveUnlocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<OperationRecord>>(File.ReadAllText(_path));
            if (list is null) return;
            lock (_gate)
            {
                _items.Clear();
                _items.AddRange(list.Take(MaxEntries));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Error("Не удалось загрузить журнал операций", ex);
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Не удалось сохранить журнал операций", ex);
        }
    }
}
