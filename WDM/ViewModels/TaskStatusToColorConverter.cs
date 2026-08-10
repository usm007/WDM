using System.Globalization;
using System.Windows.Data;
using WDM.Models;

namespace WDM.ViewModels;

public sealed class TaskStatusToColorConverter : IValueConverter
{
    public static string ColorHex(TaskStatus status) => status switch
    {
        TaskStatus.Downloading => "#2D7FF9",
        TaskStatus.Completed => "#1FA463",
        TaskStatus.Paused => "#F5A524",
        TaskStatus.Failed => "#E5484D",
        TaskStatus.Queued or TaskStatus.Scheduled => "#6B7280",
        _ => "#6B7280",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TaskStatus status)
            return "#6B7280";
        return ColorHex(status);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
