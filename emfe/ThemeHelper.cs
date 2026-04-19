using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace emfe;

public static class ThemeHelper
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("uxtheme.dll", EntryPoint = "#135", PreserveSig = true)]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136", PreserveSig = true)]
    private static extern void FlushMenuThemes();

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    private const int APPMODE_DEFAULT = 0;
    private const int APPMODE_FORCEDARK = 2;
    private const int APPMODE_FORCELIGHT = 3;

    public static bool IsDarkMode { get; set; } = true;

    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
                return val == 0;
        }
        catch { }
        return true;
    }

    public static bool ResolveDarkMode(string theme)
    {
        return theme switch
        {
            "Light" => false,
            "System" => IsSystemDarkMode(),
            _ => true,
        };
    }

    public static void SetAppMode(bool dark)
    {
        IsDarkMode = dark;
        try
        {
            SetPreferredAppMode(dark ? APPMODE_FORCEDARK : APPMODE_FORCELIGHT);
            FlushMenuThemes();
        }
        catch { }
    }

    public static void ApplyTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int mode = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref mode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY,
                    ref mode, sizeof(int));
            }
        }
        catch { }
    }
}
