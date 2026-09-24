using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WDM.Models;

namespace WDM.ViewModels;

public sealed class StatusToBrushConverter : IValueConverter
{
    private static SolidColorBrush? Resolve(string key) =>
        Application.Current?.Resources[key] as SolidColorBrush;

    private static SolidColorBrush FallbackFg =>
        Resolve("Brush.TextDim") ?? new SolidColorBrush(Color.FromRgb(0x4A, 0x51, 0x5D));

    private static SolidColorBrush FallbackBg =>
        Resolve("Brush.SurfaceAlt") ?? new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xF6));

    public static SolidColorBrush ForegroundFor(TaskStatus status) => status switch
    {
        TaskStatus.Failed => Resolve("FailedBrush") ?? Resolve("Brush.StatusFailed") ?? new SolidColorBrush(Color.FromRgb(0xA1, 0x3B, 0x3B)),
        TaskStatus.Downloading => Resolve("AccentBrush") ?? FallbackFg,
        _ => Resolve("Brush.TextDim") ?? FallbackFg,
    };

    public static SolidColorBrush BackgroundFor(TaskStatus status) =>
        Resolve("Brush.SurfaceAlt") ?? FallbackBg;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TaskStatus status)
        {
            string mode = parameter?.ToString()?.ToLowerInvariant() ?? "fg";
            return mode switch
            {
                "bg" or "background" or "pill" => BackgroundFor(status),
                _ => ForegroundFor(status),
            };
        }
        return FallbackFg;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
