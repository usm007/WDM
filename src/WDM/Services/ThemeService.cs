using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WDM.Services;

/// <summary>
/// Swaps the active palette dictionary (light/dark) so every DynamicResource
/// brush reference re-resolves without touching per-window XAML.
/// Also applies the DWM dark title bar flag to every window automatically.
/// </summary>
public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static void Apply(bool dark = false)
    {
        var app = Application.Current;
        if (app is null) return;

        IsDark = dark;

        var dict = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString?.Contains("Palette.", StringComparison.OrdinalIgnoreCase) == true);
        if (dict is null) return;

        dict.Source = new Uri(
            dark
                ? "pack://application:,,,/Themes/Palette.Dark.xaml"
                : "pack://application:,,,/Themes/Palette.Light.xaml",
            UriKind.Absolute);
    }

    /// <summary>
    /// Tells DWM to render the title bar in dark mode for the given window.
    /// Safe to call from Window.Loaded — the HWND is guaranteed to exist by then.
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        int value = IsDark ? 1 : 0;

        // Windows 10 20H1 and later / Windows 11
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));

        // Fallback for Windows 10 before 20H1
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}