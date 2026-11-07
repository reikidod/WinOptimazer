using System.Globalization;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Tweaks;

internal static class MouseSettings
{
    /// <summary>
    /// Читает MouseSpeed/MouseThreshold1/2 из реестра и применяет их к текущей сессии,
    /// чтобы отключение ускорения указателя заработало без выхода из системы.
    /// </summary>
    public static void ApplyFromRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: false);

        // Порядок массива для SPI_SETMOUSE: порог 1, порог 2, ускорение.
        int[] values =
        [
            ReadInt(key, "MouseThreshold1", 6),
            ReadInt(key, "MouseThreshold2", 10),
            ReadInt(key, "MouseSpeed", 1),
        ];

        if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, values, NativeMethods.SPIF_SENDCHANGE))
        {
            throw new InvalidOperationException("SystemParametersInfo(SPI_SETMOUSE) вернула ошибку.");
        }
    }

    private static int ReadInt(RegistryKey? key, string name, int fallback) =>
        int.TryParse(key?.GetValue(name) as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
