using System.Net.NetworkInformation;
using System.Diagnostics;

namespace WinOptimizer.Services;

public sealed record NetworkTrafficRate(double? ReceivedBytesPerSecond, double? SentBytesPerSecond)
{
    public bool IsAvailable => ReceivedBytesPerSecond.HasValue && SentBytesPerSecond.HasValue;
    public string DownloadText => ReceivedBytesPerSecond is double value ? FormatRate(value) : "—";
    public string UploadText => SentBytesPerSecond is double value ? FormatRate(value) : "—";
    public string Summary => IsAvailable ? $"Сеть · ↓ {DownloadText}  ↑ {UploadText}" : "Сеть · скорость измеряется…";

    private static string FormatRate(double bytesPerSecond)
    {
        string[] units = ["Б/с", "КБ/с", "МБ/с", "ГБ/с"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed class NetworkTrafficRateTracker
{
    private long? _received;
    private long? _sent;
    private DateTime? _capturedAt;

    public NetworkTrafficRate Update(long receivedBytes, long sentBytes, DateTime capturedAt)
    {
        if (receivedBytes < 0 || sentBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(receivedBytes), "Счётчики трафика не могут быть отрицательными.");

        var previousReceived = _received;
        var previousSent = _sent;
        var previousAt = _capturedAt;
        _received = receivedBytes;
        _sent = sentBytes;
        _capturedAt = capturedAt;

        if (previousReceived is null || previousSent is null || previousAt is null)
            return new NetworkTrafficRate(null, null);

        var elapsed = (capturedAt - previousAt.Value).TotalSeconds;
        if (elapsed <= 0 || elapsed > 30 || receivedBytes < previousReceived || sentBytes < previousSent)
            return new NetworkTrafficRate(null, null);

        return new NetworkTrafficRate(
            (receivedBytes - previousReceived.Value) / elapsed,
            (sentBytes - previousSent.Value) / elapsed);
    }
}

public sealed record DiskActivityRate(double? ReadBytesPerSecond, double? WriteBytesPerSecond)
{
    public bool IsAvailable => ReadBytesPerSecond.HasValue && WriteBytesPerSecond.HasValue;

    public string ReadText => ReadBytesPerSecond is double value ? FormatRate(value) : "—";
    public string WriteText => WriteBytesPerSecond is double value ? FormatRate(value) : "—";

    private static string FormatRate(double bytesPerSecond)
    {
        string[] units = ["Б/с", "КБ/с", "МБ/с", "ГБ/с"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// Преобразует накопительные счётчики физического диска в скорость чтения/записи.
/// Экземпляр трекера должен соответствовать одному диску; первый замер только задаёт базу.
/// </summary>
public sealed class DiskActivityRateTracker
{
    private long? _readBytes;
    private long? _writeBytes;
    private DateTime? _capturedAt;

    public DiskActivityRate Update(long? readBytes, long? writeBytes, DateTime capturedAt)
    {
        if (readBytes.HasValue && readBytes.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(readBytes), "Счётчик чтения не может быть отрицательным.");
        if (writeBytes.HasValue && writeBytes.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(writeBytes), "Счётчик записи не может быть отрицательным.");

        if (readBytes is null || writeBytes is null)
        {
            Reset();
            return new DiskActivityRate(null, null);
        }

        var previousRead = _readBytes;
        var previousWrite = _writeBytes;
        var previousAt = _capturedAt;

        _readBytes = readBytes;
        _writeBytes = writeBytes;
        _capturedAt = capturedAt;

        if (previousRead is null || previousWrite is null || previousAt is null)
            return new DiskActivityRate(null, null);

        var elapsed = (capturedAt - previousAt.Value).TotalSeconds;
        if (elapsed <= 0 || elapsed > 30 || readBytes.Value < previousRead.Value || writeBytes.Value < previousWrite.Value)
            return new DiskActivityRate(null, null);

        return new DiskActivityRate(
            (readBytes.Value - previousRead.Value) / elapsed,
            (writeBytes.Value - previousWrite.Value) / elapsed);
    }

    public void Reset()
    {
        _readBytes = null;
        _writeBytes = null;
        _capturedAt = null;
    }
}


public sealed record PhysicalDiskActivitySnapshot(
    string InstanceName,
    double? ReadBytesPerSecond,
    double? WriteBytesPerSecond,
    DateTime CapturedAt)
{
    public bool IsAvailable => ReadBytesPerSecond.HasValue && WriteBytesPerSecond.HasValue;
    public bool IsWarmingUp { get; init; }
    public string ReadText => ReadBytesPerSecond is double value ? FormatRate(value) : IsWarmingUp ? "Измерение…" : "н/д";
    public string WriteText => WriteBytesPerSecond is double value ? FormatRate(value) : IsWarmingUp ? "Измерение…" : "н/д";
    public string DisplayName => InstanceName.Replace(" ", " · ", StringComparison.Ordinal);

    private static string FormatRate(double bytesPerSecond)
    {
        string[] units = ["Б/с", "КБ/с", "МБ/с", "ГБ/с"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public static class PerformanceTelemetryService
{
    private static readonly object Sync = new();
    private static readonly NetworkTrafficRateTracker Tracker = new();
    private static readonly object DiskCounterSync = new();
    private static readonly Dictionary<string, PhysicalDiskCounterPair> DiskCounters = new(StringComparer.OrdinalIgnoreCase);

    private sealed class PhysicalDiskCounterPair : IDisposable
    {
        public PhysicalDiskCounterPair(string instanceName)
        {
            Read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instanceName, readOnly: true);
            try
            {
                Write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instanceName, readOnly: true);
            }
            catch
            {
                Read.Dispose();
                throw;
            }
        }

        public PerformanceCounter Read { get; }
        public PerformanceCounter Write { get; }

        public void Dispose()
        {
            Read.Dispose();
            Write.Dispose();
        }
    }

    /// <summary>
    /// Читает скорость чтения/записи отдельно для каждого физического диска.
    /// Первый вызов NextValue только прогревает счётчики и намеренно возвращает «измерение».
    /// Недоступность одного экземпляра не скрывает остальные диски.
    /// </summary>
    public static IReadOnlyList<PhysicalDiskActivitySnapshot> CollectPhysicalDiskActivity()
    {
        lock (DiskCounterSync)
        {
            string[] instances;
            try
            {
                instances = new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames()
                    .Where(name => !string.Equals(name, "_Total", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                           or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                return Array.Empty<PhysicalDiskActivitySnapshot>();
            }

            var active = instances.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in DiskCounters.Keys.Where(name => !active.Contains(name)).ToArray())
            {
                DiskCounters[stale].Dispose();
                DiskCounters.Remove(stale);
            }

            var capturedAt = DateTime.UtcNow;
            var result = new List<PhysicalDiskActivitySnapshot>(instances.Length);
            foreach (var instance in instances)
            {
                if (!DiskCounters.TryGetValue(instance, out var pair))
                {
                    try
                    {
                        pair = new PhysicalDiskCounterPair(instance);
                        _ = pair.Read.NextValue();
                        _ = pair.Write.NextValue();
                        DiskCounters.Add(instance, pair);
                        result.Add(new PhysicalDiskActivitySnapshot(instance, null, null, capturedAt) { IsWarmingUp = true });
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                                   or System.ComponentModel.Win32Exception or PlatformNotSupportedException
                                                   or ArgumentException)
                    {
                        pair?.Dispose();
                        result.Add(new PhysicalDiskActivitySnapshot(instance, null, null, capturedAt));
                    }

                    continue;
                }

                try
                {
                    var read = pair.Read.NextValue();
                    var write = pair.Write.NextValue();
                    result.Add(new PhysicalDiskActivitySnapshot(
                        instance,
                        float.IsFinite(read) && read >= 0 ? read : null,
                        float.IsFinite(write) && write >= 0 ? write : null,
                        capturedAt));
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
                                               or System.ComponentModel.Win32Exception or PlatformNotSupportedException
                                               or ArgumentException)
                {
                    pair.Dispose();
                    DiskCounters.Remove(instance);
                    result.Add(new PhysicalDiskActivitySnapshot(instance, null, null, capturedAt));
                }
            }

            return result;
        }
    }

    /// <summary>Освобождает долгоживущие Windows-счётчики при завершении приложения.</summary>
    public static void DisposePhysicalDiskCounters()
    {
        lock (DiskCounterSync)
        {
            foreach (var pair in DiskCounters.Values)
                pair.Dispose();
            DiskCounters.Clear();
        }
    }

    public static NetworkTrafficRate CollectNetworkRate()
    {
        long received = 0;
        long sent = 0;
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                try
                {
                    var statistics = network.GetIPv4Statistics();
                    received = checked(received + statistics.BytesReceived);
                    sent = checked(sent + statistics.BytesSent);
                }
                catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or OverflowException)
                {
                    // Один недоступный интерфейс не должен ломать весь снимок.
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            return new NetworkTrafficRate(null, null);
        }

        lock (Sync)
            return Tracker.Update(received, sent, DateTime.UtcNow);
    }
}
