using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CFRezManager;

internal static class WindowThemeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaColorDefault = -1;

    public static void Apply(Window window, AppTheme theme)
    {
        if (window.IsLoaded)
        {
            ApplyToHandle(window, theme);
            return;
        }

        window.SourceInitialized -= Window_SourceInitialized;
        window.SourceInitialized += Window_SourceInitialized;
        return;

        void Window_SourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= Window_SourceInitialized;
            ApplyToHandle(window, theme);
        }
    }

    private static void ApplyToHandle(Window window, AppTheme theme)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int value = theme == AppTheme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
            }

            ApplyWindowColors(handle, theme);
        }
        catch
        {
            // Native title bar theming is cosmetic; unsupported Windows versions can ignore it.
        }
    }

    private static void ApplyWindowColors(IntPtr handle, AppTheme theme)
    {
        int captionColor = theme == AppTheme.Dark ? ToColorRef(31, 36, 41) : DwmwaColorDefault;
        int borderColor = theme == AppTheme.Dark ? ToColorRef(59, 69, 78) : DwmwaColorDefault;
        int textColor = theme == AppTheme.Dark ? ToColorRef(243, 244, 246) : DwmwaColorDefault;

        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColor, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
    }

    private static int ToColorRef(byte red, byte green, byte blue)
    {
        return red | green << 8 | blue << 16;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
