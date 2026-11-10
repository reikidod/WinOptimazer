using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class OperationHistoryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "winoptimizer-history-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void GetRecent_ClampsRequestedCountToRetentionLimit()
    {
        Directory.CreateDirectory(_directory);
        var history = new OperationHistoryService(Path.Combine(_directory, "history.jsonl"));

        for (var i = 0; i < 510; i++)
            history.Record("Тест", $"Операция {i}", "Успешно");

        var recent = history.GetRecent(10_000);

        Assert.Equal(500, recent.Count);
        Assert.Equal("Операция 509", recent[0].Operation);
        Assert.Equal("Операция 10", recent[^1].Operation);
    }
}
