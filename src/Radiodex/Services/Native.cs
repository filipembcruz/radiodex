using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Radiodex.Services;

internal static class Native
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    /// <summary>Gives the standard Windows title bar the dark look of the app (Windows 10 20H1+ / 11).</summary>
    public static void UseDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            // Windows 11: match the title bar to the app background (#0B0D10, as COLORREF 0x00BBGGRR).
            var color = 0x00100D0B;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref color, sizeof(int));
        }
        catch
        {
            // Older Windows: keep the default title bar.
        }
    }
}
