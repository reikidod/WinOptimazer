using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace WinOptimizer.GameBoostService;

internal sealed class GameBoostWorker : BackgroundService
{
    private const string PipeName = "WinOptimizer.GameBoost.v1";
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinOptimizer", "GameBoost");
    private static readonly string ConfigPath = Path.Combine(DataDirectory, "games.json");
    private static readonly string JournalPath = Path.Combine(DataDirectory, "recovery.json");
    private static readonly string HighPerformanceScheme = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProcessSnapshot> _changedProcesses = new();
    private string? _originalPowerScheme;
    private bool _sessionActive;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(DataDirectory);
        LoadConfiguration();
        await RecoverAsync();
        var pipeTask = Task.Run(() => PipeLoopAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanProcessesAsync(stoppingToken); }
            catch (Exception ex) { WriteLog("Game scan failed: " + ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }

        await RestoreSessionAsync();
        try { await pipeTask; } catch (OperationCanceledException) { }
    }

    private void LoadConfiguration()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(ConfigPath)) ?? Array.Empty<string>();
                _games = paths.Where(IsSafeExecutablePath).Select(Path.GetFileName)
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { WriteLog("Configuration load failed: " + ex.Message); }
    }

    private async Task PipeLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                await using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var request = JsonSerializer.Deserialize<PipeRequest>(line);
                var response = await HandleRequestAsync(request);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { WriteLog("IPC error: " + ex.Message); await Task.Delay(500, token); }
        }
    }

    private async Task<PipeResponse> HandleRequestAsync(PipeRequest? request)
    {
        if (request is null) return new(false, "Некорректный запрос", Array.Empty<string>(), false);
        if (request.Command == "configure")
        {
            var paths = (request.Paths ?? Array.Empty<string>()).Where(IsSafeExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await _gate.WaitAsync();
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var temp = ConfigPath + ".tmp";
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, ConfigPath, true);
                _games = paths.Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            finally { _gate.Release(); }
            return new(true, $"Профилей игр: {paths.Length}", _games.Order().ToArray(), _sessionActive);
        }
        if (request.Command == "status")
            return new(true, _sessionActive ? "Игровая оптимизация активна" : "Ожидание запуска игры", _games.Order().ToArray(), _sessionActive);
        return new(false, "Команда не поддерживается", Array.Empty<string>(), _sessionActive);
    }

    private async Task ScanProcessesAsync(CancellationToken token)
    {
        if (_games.Count == 0) return;
        var running = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (_games.Contains(process.ProcessName + ".exe")) running.Add(process);
                else process.Dispose();
            }
            catch { process.Dispose(); }
        }

        if (running.Count > 0 && !_sessionActive) await BeginSessionAsync();
        if (running.Count > 0)
        {
            foreach (var process in running)
            {
                try
                {
                    if (_changedProcesses.ContainsKey(process.Id)) continue;
                    var start = process.StartTime.ToUniversalTime();
                    var original = process.PriorityClass;
                    var snapshot = new ProcessSnapshot(process.Id, start, (int)original);
                    _changedProcesses[process.Id] = snapshot;
                    await SaveJournalAsync();
                    if (original < ProcessPriorityClass.AboveNormal)
                        process.PriorityClass = ProcessPriorityClass.AboveNormal;
                }
                catch (Exception ex) { WriteLog($"Cannot boost PID {SafePid(process)}: {ex.Message}"); }
                finally { process.Dispose(); }
            }
        }
        else
        {
            foreach (var process in running) process.Dispose();
            if (_sessionActive) await RestoreSessionAsync();
        }
    }

    private async Task BeginSessionAsync()
    {
        _sessionActive = true;
        _originalPowerScheme = ReadActivePowerScheme();
        if (!string.IsNullOrWhiteSpace(_originalPowerScheme))
        {
            await SaveJournalAsync();
            if (!string.Equals(_originalPowerScheme, HighPerformanceScheme, StringComparison.OrdinalIgnoreCase))
                RunPowerCfg("/setactive", HighPerformanceScheme);
        }
        await SaveJournalAsync();
        WriteLog("Game Boost session started");
    }

    private async Task RestoreSessionAsync()
    {
        foreach (var snapshot in _changedProcesses.Values.ToArray())
        {
            try
            {
                using var process = Process.GetProcessById(snapshot.ProcessId);
                if (process.StartTime.ToUniversalTime() == snapshot.StartTimeUtc)
                    process.PriorityClass = (ProcessPriorityClass)snapshot.OriginalPriority;
            }
            catch { /* process already exited or access denied */ }
        }
        _changedProcesses.Clear();
        if (!string.IsNullOrWhiteSpace(_originalPowerScheme))
            RunPowerCfg("/setactive", _originalPowerScheme);
        _originalPowerScheme = null;
        _sessionActive = false;
        await SaveJournalAsync();
        WriteLog("Game Boost session restored");
    }

    private async Task SaveJournalAsync()
    {
        var journal = new RecoveryJournal(_originalPowerScheme, _changedProcesses.Values.ToArray());
        var temp = JournalPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(journal));
        File.Move(temp, JournalPath, true);
    }

    private async Task RecoverAsync()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            var journal = JsonSerializer.Deserialize<RecoveryJournal>(await File.ReadAllTextAsync(JournalPath));
            if (journal is null) return;
            _originalPowerScheme = journal.OriginalPowerScheme;
            foreach (var snapshot in journal.Processes ?? Array.Empty<ProcessSnapshot>())
                _changedProcesses[snapshot.ProcessId] = snapshot;
            await RestoreSessionAsync();
        }
        catch (Exception ex) { WriteLog("Recovery failed: " + ex.Message); }
    }

    private static string? ReadActivePowerScheme()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", "/getactivescheme")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(3000);
            var match = System.Text.RegularExpressions.Regex.Match(output, @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
            return match.Success ? match.Value : null;
        }
        catch { return null; }
    }

    private static void RunPowerCfg(params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", string.Join(" ", args))
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    private static bool IsSafeExecutablePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase);

    private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }
    private static void WriteLog(string message)
    {
        try { Directory.CreateDirectory(DataDirectory); File.AppendAllText(Path.Combine(DataDirectory, "service.log"),
            $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}"); } catch { }
    }

    private sealed record PipeRequest(string Command, string[]? Paths = null);
    private sealed record PipeResponse(bool Success, string Message, string[] ConfiguredGames, bool IsActive);
    private sealed record ProcessSnapshot(int ProcessId, DateTime StartTimeUtc, int OriginalPriority);
    private sealed record RecoveryJournal(string? OriginalPowerScheme, ProcessSnapshot[] Processes);
}