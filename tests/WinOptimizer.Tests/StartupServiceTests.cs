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
        store.Folders[StartupScope.AllUsersFolder].Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\Monitor.lnk");

        var entries = new StartupService(store).GetEntries();

        Assert.Contains(entries, entry => entry.Name == "Updater" && entry.Scope == StartupScope.CurrentUser);
        Assert.Contains(entries, entry => entry.Name == "Chat" && entry.Scope == StartupScope.CurrentUserFolder);
        Assert.Contains(entries, entry => entry.Name == "Monitor" && entry.Scope == StartupScope.AllUsersFolder);
        Assert.Equal(3, entries.Count);
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
