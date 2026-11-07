using System.Text.Json;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Tweaks;

/// <summary>Одно исходное значение, сохранённое до применения твика.</summary>
public sealed record SavedValue(string Key, string Name, string Kind, string? Data, bool Existed);

/// <summary>
/// Хранит исходные значения, чтобы твик можно было откатить точно к прежнему состоянию.
/// Запись атомарная (временный файл + замена), поэтому обрыв не портит существующий бэкап.
/// </summary>
public sealed class BackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<SavedValue>> _data;

    public BackupStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _data = Load();
    }

    public IReadOnlyList<SavedValue>? Get(string tweakId)
    {
        lock (_gate)
        {
            return _data.TryGetValue(tweakId, out var values) ? values.ToList() : null;
        }
    }

    public void Save(string tweakId, IEnumerable<SavedValue> values)
    {
        lock (_gate)
        {
            _data[tweakId] = values.ToList();
            Flush();
        }
    }

    public void Remove(string tweakId)
    {
        lock (_gate)
        {
            if (_data.Remove(tweakId))
            {
                Flush();
            }
        }
    }

    private Dictionary<string, List<SavedValue>> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, List<SavedValue>>();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<Dictionary<string, List<SavedValue>>>(stream)
                   ?? new Dictionary<string, List<SavedValue>>();
        }
        catch (JsonException ex)
        {
            // Повреждённый файл не удаляем: переименовываем, чтобы можно было разобрать вручную.
            Logger.Error("Файл бэкапа повреждён, начинаем с пустого", ex);
            QuarantineCorruptFile();
            return new Dictionary<string, List<SavedValue>>();
        }
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            File.Move(_path, $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}");
        }
        catch (IOException ex)
        {
            Logger.Error("Не удалось переименовать повреждённый бэкап", ex);
        }
    }

    private void Flush()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
