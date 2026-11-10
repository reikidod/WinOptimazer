using WinOptimizer.Services;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class StartupServiceTests
{
    [Fact]
    public void GetEntries_CombinesRegistryAndStartupFolders()
    {
        var store = new FakeStartupStore();
        store.Run[StartupScope.CurrentUser]["Updater"] = "\"C:\\Tools\\Updater.exe\" /background";
        store.Folders[StartupScope.CurrentUserFolder].Add(@"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Chat.lnk");
        store.Folders[StartupScope.CurrentUserFolder].Add(@"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\desktop.ini");
        store.Folders[StartupScope.AllUsersFolder].Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\Monitor.lnk");

        var entries = new StartupService(store).GetEntries();

        Assert.Contains(entries, entry => entry.Name == "Updater" && entry.Scope == StartupScope.CurrentUser);
        Assert.Contains(entries, entry => entry.Name == "Chat" && entry.Scope == StartupScope.CurrentUserFolder);
        Assert.Contains(entries, entry => entry.Name == "Monitor" && entry.Scope == StartupScope.AllUsersFolder);
        Assert.DoesNotContain(entries, entry => entry.Name.Equals("desktop", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, entries.Count);
    }

    [Theory]
    [InlineData(@"C:\Users\Test\Startup\desktop.ini")]
    [InlineData(@"C:\ProgramData\Startup\DESKTOP.INI")]
    public void GetEntries_ExcludesWindowsDesktopMetadataFiles(string path)
    {
        var store = new FakeStartupStore();
        store.Folders[StartupScope.CurrentUserFolder].Add(path);

        Assert.Empty(new StartupService(store).GetEntries());
    }

    [Fact]
    public void FolderEntry_ReadsDisabledStateAndWritesBackUsingApprovalPath()
    {
        var path = @"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Chat.lnk";
        var store = new FakeStartupStore();
        store.Folders[StartupScope.CurrentUserFolder].Add(path);
        store.Approved[StartupScope.CurrentUserFolder][path] = StartupApproval.Create(false, DateTime.UnixEpoch);
        var service = new StartupService(store, () => DateTime.UnixEpoch);

        var entry = Assert.Single(service.GetEntries());
        Assert.False(entry.Enabled);
        Assert.Equal(path, entry.ApprovalName);

        service.SetEnabled(entry, true);

        Assert.True(StartupApproval.IsEnabled(store.Writes[(StartupScope.CurrentUserFolder, path)]));
        Assert.Equal(0x02, store.Writes[(StartupScope.CurrentUserFolder, path)][0]);
    }

    [Fact]
    public void RegistryEntry_StillUsesRegistryValueNameForApproval()
    {
        var store = new FakeStartupStore();
        store.Run[StartupScope.CurrentUser]["Updater"] = "updater.exe";
        var service = new StartupService(store, () => DateTime.UnixEpoch);

        var entry = Assert.Single(service.GetEntries());
        service.SetEnabled(entry, false);

        Assert.Contains((StartupScope.CurrentUser, "Updater"), store.Writes.Keys);
        Assert.Equal(0x03, store.Writes[(StartupScope.CurrentUser, "Updater")][0]);
    }

    [Fact]
    public void StartupItemViewModel_RecordsSuccessfulToggleInOperationHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wo-startup-history-" + Guid.NewGuid().ToString("N"));
        var historyPath = Path.Combine(directory, "history.jsonl");
        try
        {
            var entry = new StartupEntry(StartupScope.CurrentUser, "Updater", "updater.exe", true, "Updater");
            var service = new FakeToggleStartupService();
            var item = new WinOptimizer.ViewModels.StartupItemViewModel(
                entry, service, new OperationHistoryService(historyPath));

            item.ToggleCommand.Execute(null);

            Assert.False(item.IsEnabled);
            var record = Assert.Single(new OperationHistoryService(historyPath).GetRecent());
            Assert.Equal("Автозагрузка", record.Category);
            Assert.Equal("Автозагрузка «Updater»", record.Operation);
            Assert.Equal("Отключена", record.Result);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FakeToggleStartupService : IStartupService
    {
        public IReadOnlyList<StartupEntry> GetEntries() => [];
        public void SetEnabled(StartupEntry entry, bool enabled) { }
    }

    [Fact]
    public void SetEnabled_RejectsEntryThatIsNoLongerRegistered()
    {
        var store = new FakeStartupStore();
        store.Run[StartupScope.CurrentUser]["Updater"] = "updater.exe";
        var service = new StartupService(store);
        var staleEntry = new StartupEntry(StartupScope.CurrentUser, "Removed", "removed.exe", true, "Removed");

        var error = Assert.Throws<InvalidOperationException>(() => service.SetEnabled(staleEntry, false));

        Assert.Contains("больше не найден", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void SetEnabled_RejectsForgedApprovalName()
    {
        var store = new FakeStartupStore();
        store.Run[StartupScope.CurrentUser]["Updater"] = "updater.exe";
        var service = new StartupService(store);
        var realEntry = Assert.Single(service.GetEntries());
        var forgedEntry = realEntry with { ApprovalName = "AnotherApplication" };

        Assert.Throws<InvalidOperationException>(() => service.SetEnabled(forgedEntry, false));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void SetEnabled_RejectsUnknownScope()
    {
        var store = new FakeStartupStore();
        var service = new StartupService(store);
        var invalidEntry = new StartupEntry((StartupScope)999, "Updater", "updater.exe", true);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetEnabled(invalidEntry, false));
        Assert.Empty(store.Writes);
    }

    private sealed class FakeStartupStore : IStartupStore
    {
        public Dictionary<StartupScope, Dictionary<string, string>> Run { get; } =
            Enum.GetValues<StartupScope>().ToDictionary(
                scope => scope,
                _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public Dictionary<StartupScope, Dictionary<string, byte[]>> Approved { get; } =
            Enum.GetValues<StartupScope>().ToDictionary(
                scope => scope,
                _ => new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));

        public Dictionary<StartupScope, List<string>> Folders { get; } =
            Enum.GetValues<StartupScope>().ToDictionary(scope => scope, _ => new List<string>());

        public Dictionary<(StartupScope Scope, string Name), byte[]> Writes { get; } = new();

        public IReadOnlyDictionary<string, string> ReadRunValues(StartupScope scope) => Run[scope];

        public IReadOnlyDictionary<string, byte[]> ReadApproved(StartupScope scope) => Approved[scope];

        public IReadOnlyList<string> ReadStartupFolder(StartupScope scope) => Folders[scope];

        public void WriteApproved(StartupScope scope, string name, byte[] data) => Writes[(scope, name)] = data;
    }
}
