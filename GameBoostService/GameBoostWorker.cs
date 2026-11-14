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
            try
            {
                await _gate.WaitAsync(stoppingToken);
                try { await ScanProcessesAsync(stoppingToken); }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
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
                _games = paths.Where(GameBoostPolicy.IsSafeExecutablePath)
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            var paths = (request.Paths ?? Array.Empty<string>())
                .Where(GameBoostPolicy.IsSafeExecutablePath)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await _gate.WaitAsync();
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var temp = ConfigPath + ".tmp";
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, ConfigPath, true);
                _games = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (_games.Count == 0 && _sessionActive) await RestoreSessionAsync();
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
        if (_games.Count == 0)
        {
            if (_sessionActive)
                await RestoreSessionAsync();
            return;
        }
        var running = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var executablePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executablePath) && _games.Contains(Path.GetFullPath(executablePath))) running.Add(process);
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
                    if (GameBoostPolicy.ShouldPromotePriority(original))
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
                if (GameBoostPolicy.IsSameProcessInstance(
                        snapshot.ProcessId, snapshot.StartTimeUtc,
                        process.Id, process.StartTime.ToUniversalTime()))
                    process.PriorityClass = (ProcessPriorityClass)snapshot.OriginalPriority;
            }
            catch { /* process already exited or access denied */ }
        }
        _changedProcesses.Clear();
        var powerPlanRestored = string.IsNullOrWhiteSpace(_originalPowerScheme) ||
            RunPowerCfg("/setactive", _originalPowerScheme);
        // Keep the original plan in the recovery journal until Windows confirms restoration.
        if (powerPlanRestored)
            _originalPowerScheme = null;
        _sessionActive = !powerPlanRestored;
        await SaveJournalAsync();
        WriteLog(powerPlanRestored
            ? "Game Boost session restored"
            : "Game Boost priorities restored; original power plan remains pending recovery");
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
            using var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/getactivescheme")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (process is null)
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                WriteLog("powercfg.exe /getactivescheme timed out");
                return null;
            }

            return GameBoostPolicy.ExtractPowerSchemeGuid(outputTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            WriteLog("Could not read active power plan: " + ex.Message);
            return null;
        }
    }

    private static bool RunPowerCfg(params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in args)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                WriteLog("powercfg.exe timed out: " + string.Join(" ", args));
                return false;
            }
            if (process.ExitCode != 0)
                WriteLog($"powercfg.exe failed ({process.ExitCode}): {string.Join(" ", args)}");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            WriteLog("powercfg.exe failed: " + ex.Message);
            return false;
        }
    }

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