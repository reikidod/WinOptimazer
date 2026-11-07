using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinOptimizer.Infrastructure;

internal static class NativeMethods
{
    internal const uint SPI_SETMOUSE = 0x0004;
    internal const uint SPIF_SENDCHANGE = 0x0002;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint winIni);

    /// <summary>Включает тёмную рамку и заголовок окна (Windows 10 1809+ / 11). Ошибки игнорируются.</summary>
    internal static void UseDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enabled, sizeof(int));
        }
    }
}
