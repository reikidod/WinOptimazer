using System.Diagnostics;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Services;

/// <summary>
/// Автозапуск приложения вместе с Windows. Приложению нужны права администратора, а запись в Run их не даёт без окна UAC,
/// поэтому используется задача планировщика с наивысшими правами при входе в систему.
/// </summary>
public static class AutostartService
{
    public const string TaskName = "WinOptimizer";

    internal static string BuildCreateArguments(string exePath) =>
        $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" /sc onlogon /rl highest /delay 0000:20 /f";

    public static bool IsEnabled()
    {
        try
        {
            return ProcessRunner.Run("schtasks.exe", $"/query /tn \"{TaskName}\"").ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Logger.Error("Не удалось проверить автозапуск", ex);
            return false;
        }
    }

    /// <summary>Включает или выключает автозапуск. Возвращает false и текст ошибки при неудаче.</summary>
    public static bool Set(bool enable, out string error)
    {
        error = "";
        try
        {
            var exe = Environment.ProcessPath;
            if (enable && string.IsNullOrEmpty(exe))
            {
                error = "Не удалось определить путь к программе.";
                return false;
            }

            ProcessResult result;
            if (enable)
            {
                result = ProcessRunner.Run("schtasks.exe", BuildCreateArguments(exe!));
            }
            else
            {
                result = ProcessRunner.Run("schtasks.exe", $"/delete /tn \"{TaskName}\" /f");
            }

            if (result.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(result.StandardError) ? $"код {result.ExitCode}" : result.StandardError.Trim();
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Logger.Error("Не удалось изменить автозапуск", ex);
            error = ex.Message;
            return false;
        }
    }
}
