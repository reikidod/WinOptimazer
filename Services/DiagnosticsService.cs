using System.Security.Principal;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

public enum DiagnosticLevel
{
    Info,
    Warning,
    Critical,
}

public enum RebootPendingState
{
    Pending,
    NotPending,
    Unknown,
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
    public IReadOnlyList<DiagnosticFinding> Run(SystemSnapshot snapshot) =>
        Run(snapshot, GetRebootPendingState);

    /// <summary>Overload with an injectable read-only probe so diagnostic outcomes can be tested deterministically.</summary>
    public IReadOnlyList<DiagnosticFinding> Run(
        SystemSnapshot snapshot,
        Func<RebootPendingState> rebootStatusProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rebootStatusProvider);
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
            : new("Сеть: нет активного подключения", "Сетевые интерфейсы не сообщают об активном подключении.", "Проверьте Wi-Fi, Ethernet или режим «в самолёте».", DiagnosticLevel.Warning));

        findings.Add(IsAdministrator()
            ? new("Права доступа", "Приложение запущено с правами администратора.", "Подтверждайте только те системные изменения, которые понимаете.", DiagnosticLevel.Info)
            : new("Ограниченные права", "Часть системных операций может быть недоступна.", "Запускайте от имени администратора только при необходимости.", DiagnosticLevel.Warning));

        RebootPendingState rebootState;
        try
        {
            rebootState = rebootStatusProvider();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось получить состояние ожидающей перезагрузки", ex);
            rebootState = RebootPendingState.Unknown;
        }

        findings.Add(rebootState switch
        {
            RebootPendingState.Pending => new DiagnosticFinding(
                "Требуется перезагрузка",
                "В Windows обнаружены признаки ожидающей перезагрузки.",
                "Сохраните работу и перезагрузите компьютер, когда это будет удобно.",
                DiagnosticLevel.Warning),
            RebootPendingState.NotPending => new DiagnosticFinding(
                "Состояние перезагрузки",
                "Признаки ожидающей перезагрузки не обнаружены.",
                "Дополнительные действия не требуются.",
                DiagnosticLevel.Info),
            _ => new DiagnosticFinding(
                "Не удалось проверить перезагрузку",
                "Windows не предоставила доступ к одному или нескольким диагностическим ключам реестра. Наличие ожидающей перезагрузки неизвестно.",
                "Повторите диагностику с необходимыми правами. Не считайте это подтверждением отсутствия ожидающей перезагрузки.",
                DiagnosticLevel.Warning),
        });

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

    private static RebootPendingState GetRebootPendingState()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            // Сохраняем уже подтверждённый признак: ошибка чтения следующего ключа
            // не должна отменять факт, обнаруженный в предыдущем.
            using (var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            {
                if (cbs is not null)
                    return RebootPendingState.Pending;
            }

            using (var wu = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            {
                if (wu is not null)
                    return RebootPendingState.Pending;
            }

            using (var session = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            {
                if (session?.GetValue("PendingFileRenameOperations") is not null)
                    return RebootPendingState.Pending;
            }

            return RebootPendingState.NotPending;
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось проверить ожидающую перезагрузку Windows", ex);
            return RebootPendingState.Unknown;
        }
    }
}
