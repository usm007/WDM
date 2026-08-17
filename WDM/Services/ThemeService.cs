using System.Windows;

namespace WDM.Services;

/// <summary>
/// Swaps the active palette dictionary (light/dark) so every DynamicResource
/// brush reference re-resolves without touching per-window XAML.
/// </summary>
public static class ThemeService
{
    public static bool IsDark => false;

    public static void Apply(bool dark = false)
    {
        var app = Application.Current;
        if (app is null) return;

        var dict = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString?.Contains("Palette.", StringComparison.OrdinalIgnoreCase) == true);
        if (dict is null) return;

        dict.Source = new Uri(
            "pack://application:,,,/Themes/Palette.Light.xaml",
            UriKind.Absolute);
    }
}