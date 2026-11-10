using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class DashboardServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "winoptimizer-dashboard-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SystemSnapshot_ExposesConsistentDashboardMetrics()
    {
        var snapshot = new SystemSnapshot(
            "Windows test",
            12,
            50,
            16UL * 1024 * 1024 * 1024,
            8UL * 1024 * 1024 * 1024,
            "C:\\\\",
            200L * 1024 * 1024 * 1024,
            50L * 1024 * 1024 * 1024,
            true);

        Assert.Equal("12 логических процессоров", snapshot.CpuSummary);
        Assert.Equal("50% занято", snapshot.MemoryLoadSummary);
        Assert.Equal(25d, snapshot.DiskFreePercent, precision: 2);
        Assert.Equal("Подключение доступно", snapshot.NetworkSummary);
        Assert.Equal("8 ГБ", snapshot.MemoryUsedText);
        Assert.Equal("16 ГБ", snapshot.MemoryTotalText);
        Assert.Equal("50 ГБ", snapshot.DiskFreeText);
        Assert.Equal("200 ГБ", snapshot.DiskTotalText);
    }

    [Fact]
    public void OperationHistory_StoresAndReturnsNewestFirst()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.jsonl");
        var history = new OperationHistoryService(path);

        history.Record("Оптимизация", "Включён игровой режим", "Успешно");
        history.Record("Обслуживание", "Очистка временных файлов", "Удалено: 3");

        var recent = history.GetRecent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("Очистка временных файлов", recent[0].Operation);
        Assert.Equal("Включён игровой режим", recent[1].Operation);
    }

    [Fact]
    public void OperationHistory_SkipsCorruptLines()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.jsonl");
        var history = new OperationHistoryService(path);
        history.Record("Тест", "Валидная запись", "ОК");
        File.AppendAllText(path, "{ not valid json" + Environment.NewLine);

        var recent = history.GetRecent(10);

        Assert.Single(recent);
        Assert.Equal("Валидная запись", recent[0].Operation);
    }

    [Fact]
    public void Diagnostics_ReportsLowDiskAndHighMemory()
    {
        var snapshot = new SystemSnapshot(
            "Windows test",
            8,
            95,
            16UL * 1024 * 1024 * 1024,
            800UL * 1024 * 1024,
            "C:\\",
            100L * 1024 * 1024 * 1024,
            5L * 1024 * 1024 * 1024,
            false);

        var findings = new DiagnosticsService().Run(snapshot);

        Assert.Contains(findings, x => x.Level == DiagnosticLevel.Critical && x.Title.Contains("диске", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, x => x.Level == DiagnosticLevel.Warning && x.Title.Contains("памяти", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, x => x.Title.Contains("Сеть", StringComparison.OrdinalIgnoreCase));
    }
}
