using System.Globalization;
using System.Windows.Data;
using WDM.Models;

namespace WDM.ViewModels;

public sealed class StatusToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TaskStatus status)
        {
            return status switch
            {
                TaskStatus.Downloading => "\uF01DA", // Download arrow
                TaskStatus.Completed => "\uF012C",   // Checkmark
                TaskStatus.Paused => "\uF03E4",      // Pause bars
                TaskStatus.Failed => "\uF0156",      // Alert circle
                TaskStatus.Queued => "\uF051B",      // Clock / queue
                _ => "\uF01DA",
            };
        }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
