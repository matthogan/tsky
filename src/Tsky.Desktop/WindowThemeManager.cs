using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace tsky;

public static class WindowThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    public static void ApplyTitleBarTheme(Window window, bool useDarkTheme)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var darkModeValue = useDarkTheme ? 1 : 0;

        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkModeValue, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref darkModeValue, sizeof(int));
    }
}
