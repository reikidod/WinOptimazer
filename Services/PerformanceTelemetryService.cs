using System.Net.NetworkInformation;

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

public static class PerformanceTelemetryService
{
    private static readonly object Sync = new();
    private static readonly NetworkTrafficRateTracker Tracker = new();

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
