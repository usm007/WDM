using System.Globalization;
using System.Windows.Data;
using WDM.Models;

namespace WDM.ViewModels;

public sealed class TaskStatusToColorConverter : IValueConverter
{
    /// <summary>Theme-aware status color as a hex string. Resolves the current
    /// palette brushes when the app is running so dark themes don't produce
    /// invisible black text; falls back to neutral values otherwise.</summary>
    public static string ColorHex(TaskStatus status)
    {
        string key = status switch
        {
            TaskStatus.Downloading => "Brush.StatusActive",
            TaskStatus.Completed => "Brush.StatusComplete",
            TaskStatus.Paused => "Brush.StatusPaused",
            TaskStatus.Failed => "Brush.StatusFailed",
            TaskStatus.Queued => "Brush.StatusQueued",
            _ => "Brush.TextMuted",
        };
        string? resolved = ResolveColor(key);
        if (resolved is not null)
            return resolved;

        return status switch
        {
            TaskStatus.Downloading => "#2563EB",
            TaskStatus.Completed => "#1E8E5A",
            TaskStatus.Paused => "#9AA1AC",
            TaskStatus.Failed => "#B3261E",
            TaskStatus.Queued => "#B45309",
            _ => "#90939E",
        };
    }

    private static string? ResolveColor(string key) =>
        System.Windows.Application.Current?.Resources[key] is System.Windows.Media.SolidColorBrush brush
            ? brush.Color.ToString()
            : null;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TaskStatus status)
            return ResolveColor("Brush.TextMuted") ?? "#64748B";
        return ColorHex(status);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
