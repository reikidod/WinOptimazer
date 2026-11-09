using System.Security.Principal;
using Microsoft.Win32;

namespace WinOptimizer.Services;

public enum DiagnosticLevel
{
    Info,
    Warning,
    Critical,
}

public sealed record DiagnosticFinding(
    string Title,
    string Message,
    string Recommendation,
    DiagnosticLevel Level)
{
    public string LevelLabel => Level switch
    {
        DiagnosticLevel.Critical => "Критично",
        DiagnosticLevel.Warning => "Внимание",
        _ => "Информация",
    };
}

/// <summary>Только читает состояние системы. Ничего не меняет и не запускает автоматическое исправление.</summary>
public sealed class DiagnosticsService
{
    public IReadOnlyList<DiagnosticFinding> Run(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var findings = new List<DiagnosticFinding>();

        if (snapshot.DriveTotalBytes > 0)
        {
            if (snapshot.DiskFreePercent < 10)
                findings.Add(new("Мало места на системном диске",
                    $"Свободно {snapshot.DiskFreePercent:0.#}% на диске {snapshot.SystemDrive}.",
                    "Проверьте временные файлы и крупные папки. Удаляйте только то, что точно не нужно.",
                    DiagnosticLevel.Critical));
            else if (snapshot.DiskFreePercent < 20)
                findings.Add(new("Заканчивается место на системном диске",
                    $"Свободно {snapshot.DiskFreePercent:0.#}% на диске {snapshot.SystemDrive}.",
                    "Рекомендуется освободить место перед крупными обновлениями Windows.",
                    DiagnosticLevel.Warning));
            else
                findings.Add(new("Свободное место",
                    $"На системном диске свободно {snapshot.DiskFreePercent:0.#}%.",
                    "Дополнительные действия не требуются.",
                    DiagnosticLevel.Info));
        }

        if (snapshot.MemoryLoadPercent >= 90)
            findings.Add(new("Высокая загрузка памяти",
                $"Используется {snapshot.MemoryLoadPercent}% физической памяти.",
                "Проверьте приложения с высоким потреблением памяти и вкладки браузера.",
                DiagnosticLevel.Warning));
        else
            findings.Add(new("Оперативная память",
                $"Загрузка физической памяти: {snapshot.MemoryLoadPercent}%.",
                "Критически высокой загрузки памяти не обнаружено.",
                DiagnosticLevel.Info));

        findings.Add(snapshot.NetworkAvailable
            ? new("Сеть", "Windows сообщает об активном сетевом подключении.", "Проверка доступности интернета не выполняется.", DiagnosticLevel.Info)
            : new("Нет активного сетевого подключения", "Сетевые интерфейсы не сообщают об активном подключении.", "Проверьте Wi-Fi, Ethernet или режим «в самолёте».", DiagnosticLevel.Warning));

        findings.Add(IsAdministrator()
            ? new("Права доступа", "Приложение запущено с правами администратора.", "Подтверждайте только те системные изменения, которые понимаете.", DiagnosticLevel.Info)
            : new("Ограниченные права", "Часть системных операций может быть недоступна.", "Запускайте от имени администратора только при необходимости.", DiagnosticLevel.Warning));

        if (IsRebootPending())
            findings.Add(new("Требуется перезагрузка",
                "В Windows обнаружены признаки ожидающей перезагрузки.",
                "Сохраните работу и перезагрузите компьютер, когда это будет удобно.",
                DiagnosticLevel.Warning));
        else
            findings.Add(new("Состояние перезагрузки", "Признаки ожидающей перезагрузки не обнаружены.", "Дополнительные действия не требуются.", DiagnosticLevel.Info));

        return findings;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRebootPending()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var wu = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var session = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return cbs is not null || wu is not null || session?.GetValue("PendingFileRenameOperations") is not null;
        }
        catch
        {
            // Недоступность отдельных диагностических ключей не должна прерывать остальные проверки.
            return false;
        }
    }
}
