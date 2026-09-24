using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WDM.Models;

namespace WDM.ViewModels;

public sealed class TaskStatusToBrushConverter : IValueConverter
{
    private static SolidColorBrush? Resolve(string key) =>
        System.Windows.Application.Current?.Resources[key] as SolidColorBrush;

    /// <summary>Neutral fallback brush that stays visible on both light and dark
    /// surfaces (a hardcoded pure black was invisible in dark mode).</summary>
    private static SolidColorBrush Fallback =>
        Resolve("Brush.TextMuted") ?? new SolidColorBrush(Color.FromRgb(0x90, 0x93, 0x9E));

    public static SolidColorBrush? BrushFor(TaskStatus status) => status switch
    {
        TaskStatus.Downloading => Resolve("Brush.StatusActive"),
        TaskStatus.Completed => Resolve("Brush.StatusComplete"),
        TaskStatus.Paused => Resolve("Brush.StatusPaused"),
        TaskStatus.Failed => Resolve("Brush.StatusFailed"),
        TaskStatus.Queued => Resolve("Brush.StatusQueued"),
        _ => Fallback,
    };

    public static SolidColorBrush? PillBackground(TaskStatus status) => status switch
    {
        TaskStatus.Downloading => Resolve("Brush.StatusActiveSoft"),
        TaskStatus.Completed => Resolve("Brush.StatusCompleteSoft"),
        TaskStatus.Paused => Resolve("Brush.StatusPausedSoft"),
        TaskStatus.Failed => Resolve("Brush.StatusFailedSoft"),
        TaskStatus.Queued => Resolve("Brush.StatusQueuedSoft"),
        _ => Fallback,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TaskStatus status)
        {
            bool pill = Equals(parameter?.ToString(), "pill");
            return pill ? PillBackground(status) ?? Fallback : BrushFor(status) ?? Fallback;
        }
        return Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}