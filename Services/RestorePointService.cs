using System.Management;
using System.Security;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public sealed record RestorePointResult(bool Success, string Message);

public interface IRestorePointService
{
    Task<RestorePointResult> CreateAsync(string description);
}

/// <summary>
/// Создаёт точку восстановления через WMI (SystemRestore.CreateRestorePoint, тип MODIFY_SETTINGS = 12,
/// событие BEGIN_SYSTEM_CHANGE = 100) и проверяет, что точка действительно появилась.
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    private const string FrequencyKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValueName = "SystemRestorePointCreationFrequency";
    private const uint ModifySettings = 12;
    private const uint BeginSystemChange = 100;
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(10);

    public Task<RestorePointResult> CreateAsync(string description) => Task.Run(() => Create(description));

    private static RestorePointResult Create(string description)
    {
        var startedAt = DateTime.Now;
        try
        {
            // По умолчанию Windows молча пропускает создание точки, если за последние 24 часа уже была другая,
            // но при этом возвращает успех. На время вызова снимаем это ограничение и затем возвращаем как было.
            using (new FrequencyLimitOverride())
            {
                var returnValue = InvokeCreateRestorePoint(description);
                if (returnValue != 0)
                {
                    return new RestorePointResult(false, $"Windows вернула код ошибки {returnValue} при создании точки восстановления.");
                }
            }

            return WaitForRestorePoint(description, startedAt)
                ? new RestorePointResult(true, "Точка восстановления создана.")
                : new RestorePointResult(false,
                    "Windows сообщила об успехе, но точка не появилась в списке. Проверьте, что защита системы включена для диска C: " +
                    "(Свойства системы → Защита системы).");
        }
        catch (ManagementException ex)
        {
            Logger.Error("Создание точки восстановления не удалось (WMI)", ex);
            return new RestorePointResult(false,
                "Не удалось создать точку восстановления. Чаще всего защита системы отключена: включите её для диска C: " +
                $"(Свойства системы → Защита системы). Подробности: {ex.Message}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Logger.Error("Создание точки восстановления не удалось", ex);
            return new RestorePointResult(false, $"Не удалось создать точку восстановления: {ex.Message}");
        }
    }

    private static uint InvokeCreateRestorePoint(string description)
    {
        var scope = new ManagementScope(@"\\.\root\default");
        using var systemRestore = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
        using var parameters = systemRestore.GetMethodParameters("CreateRestorePoint");
        parameters["Description"] = description;
        parameters["RestorePointType"] = ModifySettings;
        parameters["EventType"] = BeginSystemChange;

        using var result = systemRestore.InvokeMethod("CreateRestorePoint", parameters, null);
        return Convert.ToUInt32(result?["ReturnValue"] ?? 0u);
    }

    /// <summary>Точка создаётся асинхронно, поэтому проверяем список несколько секунд.</summary>
    private static bool WaitForRestorePoint(string description, DateTime startedAt)
    {
        var deadline = DateTime.UtcNow + VerificationTimeout;
        do
        {
            if (RestorePointExists(description, startedAt - TimeSpan.FromMinutes(1)))
            {
                return true;
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static bool RestorePointExists(string description, DateTime notBefore)
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT Description, CreationTime FROM SystemRestore");
        foreach (var item in searcher.Get())
        {
            using (item)
            {
                if (item["Description"] as string != description || item["CreationTime"] is not string rawTime)
                {
                    continue;
                }

                if (ManagementDateTimeConverter.ToDateTime(rawTime) >= notBefore)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Временно задаёт SystemRestorePointCreationFrequency = 0 и при Dispose возвращает прежнее состояние.</summary>
    private sealed class FrequencyLimitOverride : IDisposable
    {
        private readonly object? _previous;

        public FrequencyLimitOverride()
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(FrequencyKeyPath, writable: true)
                            ?? throw new InvalidOperationException("Не удалось открыть раздел SystemRestore.");
            _previous = key.GetValue(FrequencyValueName);
            key.SetValue(FrequencyValueName, 0, RegistryValueKind.DWord);
        }

        public void Dispose()
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(FrequencyKeyPath, writable: true);
                if (key is null)
                {
                    return;
                }

                if (_previous is null)
                {
                    key.DeleteValue(FrequencyValueName, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(FrequencyValueName, _previous, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                Logger.Error("Не удалось вернуть SystemRestorePointCreationFrequency", ex);
            }
        }
    }
}
