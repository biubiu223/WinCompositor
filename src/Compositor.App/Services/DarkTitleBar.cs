using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Compositor.App.Services;

/// <summary>Asks Windows 10 (20H1+) and 11 for a dark title bar to match the editor.</summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            int enabled = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on current builds, 19 on early Windows 10 ones.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            // Caption color on Windows 11 (DWMWA_CAPTION_COLOR), as a COLORREF 0x00BBGGRR.
            int caption = 0x001F1F1F;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        }
        catch
        {
            // Older Windows: the default title bar.
        }
    }
}
