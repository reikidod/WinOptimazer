using System.Text.Json;

namespace WinOptimizer.Services;

public sealed record OperationHistoryEntry(
    DateTimeOffset Timestamp,
    string Category,
    string Operation,
    string Result);

/// <summary>
/// Локальная история операций. Записывает только тип действия и результат, без путей к личным файлам
/// и содержимого пользовательских документов.
/// </summary>
public sealed class OperationHistoryService
{
    private const int MaxEntries = 500;
    private readonly string _path;
    private readonly object _sync = new();

    public OperationHistoryService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinOptimizer", "history.jsonl");
    }

    public void Record(string category, string operation, string result)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Категория обязательна.", nameof(category));
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("Операция обязательна.", nameof(operation));

        var entry = new OperationHistoryEntry(
            DateTimeOffset.Now,
            category.Trim(),
            operation.Trim(),
            string.IsNullOrWhiteSpace(result) ? "Неизвестно" : result.Trim());

        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            TrimIfNeeded();
        }
    }

    public IReadOnlyList<OperationHistoryEntry> GetRecent(int count = 20)
    {
        if (count <= 0) return [];
        lock (_sync)
        {
            if (!File.Exists(_path)) return [];
            var result = new List<OperationHistoryEntry>();
            foreach (var line in File.ReadLines(_path).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<OperationHistoryEntry>(line);
                    if (entry is not null) result.Add(entry);
                }
                catch (JsonException)
                {
                    // Повреждённая строка пропускается, остальные записи остаются доступны.
                }
                if (result.Count >= count) break;
            }
            return result;
        }
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(_path);
        if (info.Length < 1024 * 1024) return;

        var entries = GetRecent(MaxEntries).Reverse().ToArray();
        File.WriteAllLines(_path, entries.Select(entry => JsonSerializer.Serialize(entry)));
    }
}
