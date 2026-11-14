using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
    private Dictionary<string, GameProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProcessSnapshot> _changedProcesses = new();
    private string? _originalPowerScheme;
    private bool _sessionActive;
    private bool _highPerformanceSchemeApplied;
    private int _activeGameCount;

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
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                WriteLog("Game scan failed: " + ex);
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }

        await RestoreSessionAsync();
        try { await pipeTask; } catch (OperationCanceledException) { }
    }

    private void LoadConfiguration()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;

            var profiles = new List<GameProfile>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                // Migration: the first Game Boost version stored a simple string[] of executable paths.
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var legacyPath = entry.GetString();
                    if (GameBoostPolicy.IsSafeExecutablePath(legacyPath))
                        profiles.Add(new GameProfile(Path.GetFullPath(legacyPath!), true, GameBoostPolicy.BalancedMode));
                    continue;
                }

                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var profile = entry.Deserialize<GameProfile>();
                if (profile is null || !GameBoostPolicy.IsSafeExecutablePath(profile.Path) ||
                    !GameBoostPolicy.IsValidMode(profile.Mode))
                    continue;

                profiles.Add(profile with { Path = Path.GetFullPath(profile.Path), Mode = NormalizeMode(profile.Mode) });
            }

            _profiles = profiles
                .GroupBy(profile => profile.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToDictionary(profile => profile.Path, StringComparer.OrdinalIgnoreCase);
            WriteLog($"Loaded per-game configuration: profiles={_profiles.Count}; enabled={_profiles.Values.Count(p => p.Enabled)}");
        }
        catch (Exception ex)
        {
            WriteLog("Configuration load failed: " + ex);
        }
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

                await using var pipe = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    4096, 4096, security);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var request = JsonSerializer.Deserialize<PipeRequest>(line);
                var response = await HandleRequestAsync(request);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                WriteLog("IPC error: " + ex);
                try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<PipeResponse> HandleRequestAsync(PipeRequest? request)
    {
        if (request is null)
            return BuildResponse(false, "Некорректный запрос");

        if (request.Command == "configure")
        {
            var profiles = (request.Profiles ?? Array.Empty<GameProfile>())
                .Where(profile => profile is not null &&
                                  GameBoostPolicy.IsSafeExecutablePath(profile.Path) &&
                                  GameBoostPolicy.IsValidMode(profile.Mode))
                .Select(profile => profile with
                {
                    Path = Path.GetFullPath(profile.Path),
                    Mode = NormalizeMode(profile.Mode)
                })
                .GroupBy(profile => profile.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();

            await _gate.WaitAsync();
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var temp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(temp,
                    JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, ConfigPath, true);
                _profiles = profiles.ToDictionary(profile => profile.Path, StringComparer.OrdinalIgnoreCase);
            }
            finally { _gate.Release(); }

            var enabledCount = _profiles.Values.Count(profile => profile.Enabled);
            return BuildResponse(true, $"Сохранено профилей: {_profiles.Count}; включено: {enabledCount}");
        }

        if (request.Command == "status")
        {
            var enabledCount = _profiles.Values.Count(profile => profile.Enabled);
            var message = _activeGameCount > 0
                ? $"Game Boost активен для игр: {_activeGameCount}"
                : $"Ожидание игр · включено профилей: {enabledCount}";
            return BuildResponse(true, message);
        }

        return BuildResponse(false, "Команда не поддерживается");
    }

    private PipeResponse BuildResponse(bool success, string message) =>
        new(success, message, _profiles.Values.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            _sessionActive, _activeGameCount);

    private async Task ScanProcessesAsync(CancellationToken token)
    {
        var activeProcessIds = new HashSet<int>();
        var activeGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maximumModeActive = false;
        var changedJournal = false;

        foreach (var process in Process.GetProcesses())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath))
                    continue;

                var fullPath = Path.GetFullPath(executablePath);
                if (!_profiles.TryGetValue(fullPath, out var profile) || !profile.Enabled)
                    continue;

                activeProcessIds.Add(process.Id);
                activeGamePaths.Add(profile.Path);
                maximumModeActive |= string.Equals(profile.Mode, GameBoostPolicy.MaximumMode, StringComparison.OrdinalIgnoreCase);

                var startTime = process.StartTime.ToUniversalTime();
                var currentPriority = process.PriorityClass;
                var targetPriority = GameBoostPolicy.TargetPriority(profile.Mode);

                if (_changedProcesses.TryGetValue(process.Id, out var snapshot))
                {
                    if (!GameBoostPolicy.IsSameProcessInstance(
                            snapshot.ProcessId, snapshot.StartTimeUtc, process.Id, startTime))
                    {
                        _changedProcesses.Remove(process.Id);
                        snapshot = null!;
                    }

                    // Only adjust a priority we previously changed; do not fight the game or another tool
                    // if it has independently changed the process priority since our last scan.
                    if (snapshot is not null && (int)currentPriority == snapshot.AppliedPriority &&
                        currentPriority != targetPriority)
                    {
                        process.PriorityClass = targetPriority;
                        _changedProcesses[process.Id] = snapshot with { AppliedPriority = (int)targetPriority };
                        changedJournal = true;
                    }
                }
                else if (GameBoostPolicy.IsPriorityBelow(currentPriority, targetPriority))
                {
                    var snapshotToRestore = new ProcessSnapshot(
                        process.Id, startTime, (int)currentPriority, (int)targetPriority);
                    _changedProcesses[process.Id] = snapshotToRestore;
                    await SaveJournalAsync();
                    process.PriorityClass = targetPriority;
                    changedJournal = false;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Cannot apply Game Boost to PID {SafePid(process)}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var stale in _changedProcesses.Values.Where(snapshot => !activeProcessIds.Contains(snapshot.ProcessId)).ToArray())
        {
            RestoreProcess(stale);
            _changedProcesses.Remove(stale.ProcessId);
            changedJournal = true;
        }

        if (maximumModeActive)
        {
            if (string.IsNullOrWhiteSpace(_originalPowerScheme))
            {
                _originalPowerScheme = ReadActivePowerScheme();
                if (!string.IsNullOrWhiteSpace(_originalPowerScheme))
                    changedJournal = true;
            }

            if (!string.IsNullOrWhiteSpace(_originalPowerScheme) && !_highPerformanceSchemeApplied)
            {
                _highPerformanceSchemeApplied =
                    string.Equals(_originalPowerScheme, HighPerformanceScheme, StringComparison.OrdinalIgnoreCase) ||
                    RunPowerCfg("/setactive", HighPerformanceScheme);
                if (!_highPerformanceSchemeApplied)
                    WriteLog("Could not activate High Performance power scheme; will retry while a maximum-mode game is running.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(_originalPowerScheme))
        {
            if (RunPowerCfg("/setactive", _originalPowerScheme))
            {
                _originalPowerScheme = null;
                _highPerformanceSchemeApplied = false;
                changedJournal = true;
            }
            else
            {
                WriteLog("Original power scheme restoration failed; it will be retried.");
            }
        }

        _activeGameCount = activeGamePaths.Count;
        _sessionActive = _activeGameCount > 0;
        if (changedJournal)
            await SaveJournalAsync();
    }

    private static void RestoreProcess(ProcessSnapshot snapshot)
    {
        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            if (GameBoostPolicy.IsSameProcessInstance(
                    snapshot.ProcessId, snapshot.StartTimeUtc,
                    process.Id, process.StartTime.ToUniversalTime()))
            {
                var current = process.PriorityClass;
                if ((int)current == snapshot.AppliedPriority)
                    process.PriorityClass = (ProcessPriorityClass)snapshot.OriginalPriority;
            }
        }
        catch { /* The process exited or Windows denied access; never block service recovery. */ }
    }

    private async Task RestoreSessionAsync()
    {
        foreach (var snapshot in _changedProcesses.Values.ToArray())
            RestoreProcess(snapshot);

        _changedProcesses.Clear();
        var powerPlanRestored = string.IsNullOrWhiteSpace(_originalPowerScheme) ||
            RunPowerCfg("/setactive", _originalPowerScheme);

        if (powerPlanRestored)
        {
            _originalPowerScheme = null;
            _highPerformanceSchemeApplied = false;
        }

        _sessionActive = false;
        _activeGameCount = 0;
        await SaveJournalAsync();
        WriteLog(powerPlanRestored
            ? "Game Boost session restored"
            : "Process priorities restored; original power plan remains pending recovery");
    }

    private async Task SaveJournalAsync()
    {
        var journal = new RecoveryJournal(_originalPowerScheme, _changedProcesses.Values.ToArray());
        var temp = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(journal));
        File.Move(temp, JournalPath, true);
    }

    private async Task RecoverAsync()
    {
        try
        {
            if (!File.Exists(JournalPath))
                return;

            var journal = JsonSerializer.Deserialize<RecoveryJournal>(await File.ReadAllTextAsync(JournalPath));
            if (journal is null)
                return;

            _originalPowerScheme = journal.OriginalPowerScheme;
            foreach (var snapshot in journal.Processes ?? Array.Empty<ProcessSnapshot>())
                _changedProcesses[snapshot.ProcessId] = snapshot;

            await RestoreSessionAsync();
        }
        catch (Exception ex)
        {
            WriteLog("Recovery failed: " + ex);
        }
    }

    private static string NormalizeMode(string? mode) =>
        string.Equals(mode, GameBoostPolicy.MaximumMode, StringComparison.OrdinalIgnoreCase)
            ? GameBoostPolicy.MaximumMode
            : GameBoostPolicy.BalancedMode;

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

    private static int SafePid(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "service.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed record PipeRequest(string Command, GameProfile[]? Profiles = null);
    private sealed record GameProfile(string Path, bool Enabled, string Mode);
    private sealed record PipeResponse(bool Success, string Message, GameProfile[] Profiles, bool IsActive, int ActiveGameCount);
    private sealed record ProcessSnapshot(int ProcessId, DateTime StartTimeUtc, int OriginalPriority, int AppliedPriority);
    private sealed record RecoveryJournal(string? OriginalPowerScheme, ProcessSnapshot[] Processes);
}
