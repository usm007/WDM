using System.Globalization;
using System.Windows.Data;
using WDM.Models;

namespace WDM.ViewModels;

public sealed class TaskStatusToColorConverter : IValueConverter
{
    public static string ColorHex(TaskStatus status) => status switch
    {
        TaskStatus.Downloading => "#2563EB",
        TaskStatus.Completed => "#1E8E5A",
        TaskStatus.Paused => "#000000",
        TaskStatus.Failed => "#B3261E",
        TaskStatus.Queued => "#B45309",
        _ => "#000000",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TaskStatus status)
            return "#64748B";
        return ColorHex(status);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
