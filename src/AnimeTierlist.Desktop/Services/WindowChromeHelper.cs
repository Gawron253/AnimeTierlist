using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AnimeTierlist.Desktop.Services;

/// <summary>
/// Synchronizes the native Windows title bar with Dark or Light mode while preserving standard OS chrome,
/// resize borders, Snap Layouts, and high-DPI scaling.
/// </summary>
public static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>Applies the immersive dark title bar attribute to <paramref name="window"/> once its HWND exists.</summary>
    public static void ApplyTitleBarTheme(Window window, bool useDarkMode)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = useDarkMode ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "Keep AllowUnsafeBlocks disabled in the WPF desktop project.")]
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
