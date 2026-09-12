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
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static bool IsDark { get; private set; }
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Default;

    private const int GWL_STYLE = -16;
    private const int WS_THICKFRAME = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;
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

        // Keep WPF-UI controls on the matching theme by swapping only its theme
        // resource dictionary. Do NOT use ApplicationThemeManager.Apply(): its
        // default Mica backdrop path calls SetWindowThemeAttribute with
        // WTNCA_NODRAWCAPTION and sets a transparent DWM caption color, which
        // permanently hides the native title text (in both themes) from the
        // first toggle on.
        try
        {
            var uiTheme = app.Resources.MergedDictionaries
                .OfType<Wpf.Ui.Markup.ThemesDictionary>()
                .FirstOrDefault();
            if (uiTheme is not null)
                uiTheme.Theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                                     : Wpf.Ui.Appearance.ApplicationTheme.Light;
        }
        catch { }

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

    /// <summary>Selects the native caption scheme (dark/light) to match the app
    /// theme and applies DWM rounded corners. Caption, text and border colors
    /// are deliberately left at DWM defaults: explicit DWMWA_CAPTION_COLOR /
    /// DWMWA_TEXT_COLOR values make the native title text vanish on some
    /// Windows builds (and the broken caption persists across further toggles),
    /// while the default chrome follows the immersive-mode flag and always
    /// renders the title.</summary>
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

        // NOTE: caption/text/border colors are intentionally left at DWM defaults.
        // Custom DWMWA_CAPTION_COLOR/TEXT_COLOR values suppress the title text on
        // some Windows builds even when the calls report success — default chrome
        // is always visible and always native.
        // Apply smooth Windows 11 hardware-anti-aliased rounded corners
        int round = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

        // Force a non-client frame repaint so re-applied attributes (e.g. after a
        // theme toggle) take effect instead of leaving a stale caption behind.
        try
        {
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch { }

        if (window.ResizeMode == ResizeMode.NoResize)
        {
            int style = GetWindowLong(hwnd, GWL_STYLE);
            if ((style & WS_THICKFRAME) != 0)
            {
                SetWindowLong(hwnd, GWL_STYLE, style & ~WS_THICKFRAME);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
        }
    }
}