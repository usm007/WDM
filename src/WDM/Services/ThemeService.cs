using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WDM.Services;

/// <summary>
/// Swaps the active palette dictionary (light/dark) so every DynamicResource
/// brush reference re-resolves without touching per-window XAML.
/// </summary>
public static class ThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static bool IsDark { get; private set; }
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Default;

    // Removes the native 1px black bottom/side border Windows draws around plain
    // dialogs: extend the DWM glass frame 1px on every side so the frame sheet is
    // fully covered by the client area (the XAML background paints the window).
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    private const int GWL_STYLE = -16;
    private const int WS_THICKFRAME = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public static void Apply(AppTheme theme, bool dark)
    {
        CurrentTheme = AppTheme.Default;
        IsDark = dark;

        var app = Application.Current;
        if (app is null) return;

        // Swap palette (colors) — light/dark
        var paletteDict = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString?.Contains("Palette.", StringComparison.OrdinalIgnoreCase) == true);
        if (paletteDict is not null)
        {
            string palettePath = dark ? "Themes/Palette.Dark.xaml" : "Themes/Palette.Light.xaml";
            paletteDict.Source = new Uri($"pack://application:,,,/{palettePath}", UriKind.Absolute);
        }

        // Swap Theme.xaml (control styles)
        var themeDict = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString?.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (themeDict is not null)
        {
            string themePath = "Themes/Theme.xaml";
            var newSource = new Uri($"pack://application:,,,/{themePath}", UriKind.Absolute);
            if (!string.Equals(themeDict.Source?.OriginalString, newSource.OriginalString, StringComparison.OrdinalIgnoreCase))
                themeDict.Source = newSource;
        }

        foreach (Window window in app.Windows)
            ApplyTitleBar(window);
    }

    public static void Apply(bool dark = false)
    {
        Apply(CurrentTheme, dark);
    }

    public static void Apply(AppTheme theme)
    {
        Apply(theme, IsDark);
    }

    /// <summary>Paints the native window title bar dark (or light) to match the theme,
    /// and removes the native black border around plain dialogs: non-resizable windows
    /// get WS_THICKFRAME stripped (no frame edge at all), resizable ones keep the
    /// frame but extend the DWM sheet so the XAML background covers the edge pixels.</summary>
    public static void ApplyTitleBar(Window window)
    {
        if (window is null || window.WindowStyle == WindowStyle.None)
            return;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));

        if (window.ResizeMode == ResizeMode.NoResize)
        {
            // Fixed-size dialog: strip WS_THICKFRAME so Windows paints no border edge.
            int style = GetWindowLong(hwnd, GWL_STYLE);
            if ((style & WS_THICKFRAME) != 0)
            {
                SetWindowLong(hwnd, GWL_STYLE, style & ~WS_THICKFRAME);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
        }
        else
        {
            // Resizable window: cover the frame edge with the client area.
            var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
    }
}