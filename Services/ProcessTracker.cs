using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace WinOptimizer.Services;

/// <summary>Снимок одного процесса на момент замера. <c>CpuTime</c> = null, если Windows не дала прочитать (защищённые процессы).</summary>
public sealed record ProcessSample(int Pid, string Name, long StartTicks, TimeSpan? CpuTime, long WorkingSetBytes);

/// <summary>Сводка по активным процессам одного имени (у браузеров их десятки — показываем одной строкой).</summary>
public sealed record ProcessUsage(string Name, int Count, double? CpuPercent, long WorkingSetBytes);

/// <summary>Процесс, который закрылся, пока мы за ним следили, и его последние замеры.</summary>
public sealed record ExitedProcess(string Name, int Pid, DateTime ExitedAtUtc, TimeSpan RunTime, double? LastCpuPercent, long PeakWorkingSetBytes);

/// <summary>
/// Следит за процессами по серии замеров: считает загрузку процессора между замерами и замечает, какие процессы пропали.
/// Без обращений к системе — замеры подаются снаружи (<see cref="ProcessSampler"/>), поэтому логика проверяется тестами.
/// </summary>
public sealed class ProcessTracker
{
    private sealed class State
    {
        public required string Name { get; init; }
        public required long StartTicks { get; init; }
        public required DateTime FirstSeenUtc { get; init; }
        public TimeSpan? CpuTime { get; set; }
        public DateTime CpuAtUtc { get; set; }
        public double? CpuPercent { get; set; }
        public long WorkingSet { get; set; }
        public long PeakWorkingSet { get; set; }
    }

    private readonly Dictionary<(int Pid, long StartTicks), State> _known = new();
    private readonly List<ExitedProcess> _exited = new();
    private readonly int _cores;
    private readonly int _maxExited;

    public ProcessTracker(int cpuCores, int maxExited = 100)
    {
        _cores = Math.Max(1, cpuCores);
        _maxExited = Math.Max(1, maxExited);
    }

    /// <summary>Сколько процессов сейчас запущено (по последнему замеру).</summary>
    public int ActiveCount => _known.Count;

    /// <summary>Закрывшиеся процессы, самые свежие — первыми.</summary>
    public IReadOnlyList<ExitedProcess> Exited => _exited;

    /// <summary>Применяет очередной замер: обновляет загрузку и переносит пропавшие процессы в «завершённые».</summary>
    public void Update(IReadOnlyList<ProcessSample> samples, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var seen = new HashSet<(int, long)>();
        foreach (var s in samples)
        {
            var key = (s.Pid, s.StartTicks);
            seen.Add(key);

            if (_known.TryGetValue(key, out var st))
            {
                if (s.CpuTime is { } cpu)
                {
                    if (st.CpuTime is { } prev)
                    {
                        var elapsed = (nowUtc - st.CpuAtUtc).TotalSeconds;
                        st.CpuPercent = elapsed > 0
                            ? Math.Clamp((cpu - prev).TotalSeconds / elapsed / _cores * 100, 0, 100)
                            : null;
                    }
                    else
                    {
                        // Первый доступный замер после запуска/пропуска — только новая база, не 0%.
                        st.CpuPercent = null;
                    }

                    st.CpuTime = cpu;
                    st.CpuAtUtc = nowUtc;
                }
                else
                {
                    // Не переносим старое значение через интервал, в котором Windows закрыла доступ к CPU time.
                    st.CpuTime = null;
                    st.CpuPercent = null;
                    st.CpuAtUtc = nowUtc;
                }

                st.WorkingSet = s.WorkingSetBytes;
                st.PeakWorkingSet = Math.Max(st.PeakWorkingSet, s.WorkingSetBytes);
            }
            else
            {
                _known[key] = new State
                {
                    Name = s.Name,
                    StartTicks = s.StartTicks,
                    FirstSeenUtc = nowUtc,
                    CpuTime = s.CpuTime,
                    CpuAtUtc = nowUtc,
                    WorkingSet = s.WorkingSetBytes,
                    PeakWorkingSet = s.WorkingSetBytes,
                };
            }
        }

        foreach (var key in _known.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var st = _known[key];
            _known.Remove(key);

            var started = st.StartTicks > 0 ? new DateTime(st.StartTicks, DateTimeKind.Local).ToUniversalTime() : st.FirstSeenUtc;
            var runTime = nowUtc - started;
            _exited.Insert(0, new ExitedProcess(st.Name, key.Pid, nowUtc,
                runTime < TimeSpan.Zero ? TimeSpan.Zero : runTime, st.CpuPercent, st.PeakWorkingSet));
        }

        if (_exited.Count > _maxExited)
        {
            _exited.RemoveRange(_maxExited, _exited.Count - _maxExited);
        }
    }

    /// <summary>Активные процессы, сгруппированные по имени; самые «тяжёлые» по процессору (затем по памяти) — первыми.</summary>
    public IReadOnlyList<ProcessUsage> GetActive(int top)
    {
        return _known.Values
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUsage(g.First().Name, g.Count(),
                g.All(x => x.CpuPercent.HasValue) ? g.Sum(x => x.CpuPercent!.Value) : null,
                g.Sum(x => x.WorkingSet)))
            .OrderByDescending(u => u.CpuPercent ?? double.NegativeInfinity)
            .ThenByDescending(u => u.WorkingSetBytes)
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, top))
            .ToList();
    }

    /// <summary>Суммарная загрузка процессора (0–100 %) и память всех известных процессов.</summary>
    /// <summary>CPU остаётся неизвестным, пока все известные процессы не имеют полного интервала измерения.</summary>
    public (double? CpuPercent, long WorkingSetBytes) GetTotals()
    {
        var cpuValues = _known.Values.Select(s => s.CpuPercent).ToArray();
        double? cpu = cpuValues.Length > 0 && cpuValues.All(value => value.HasValue)
            ? Math.Min(100, cpuValues.Sum(value => value!.Value))
            : null;

        return (cpu, _known.Values.Sum(s => s.WorkingSet));
    }
}

/// <summary>Читает список процессов из Windows. Вызывать не из UI-потока: обход сотни процессов занимает заметное время.</summary>
public static class ProcessSampler
{
    public static List<ProcessSample> Take()
    {
        var result = new List<ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0) continue; // «Idle»: считает простой процессора, а не работу

                    TimeSpan? cpu = null;
                    long start = 0;
                    try { cpu = process.TotalProcessorTime; }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }

                    try { start = process.StartTime.Ticks; }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }

                    result.Add(new ProcessSample(process.Id, process.ProcessName, start, cpu, process.WorkingSet64));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Процесс закрылся между перечислением и чтением — просто пропускаем.
                }
            }
        }

        return result;
    }
}

/// <summary>Короткий русский текст для чисел в списках процессов.</summary>
public static class ProcessFormat
{
    public static string Memory(long bytes)
    {
        const double mb = 1024d * 1024d;
        var value = bytes / mb;
        return value >= 1024
            ? (value / 1024).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " ГБ"
            : value.ToString("0", CultureInfo.InvariantCulture) + " МБ";
    }

    public static string Cpu(double percent) =>
        Math.Round(percent, 1).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " %";

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds} с";
        if (span.TotalHours < 1) return $"{span.Minutes} мин {span.Seconds} с";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} ч {span.Minutes} мин";
        return $"{(int)span.TotalDays} д {span.Hours} ч";
    }
}
