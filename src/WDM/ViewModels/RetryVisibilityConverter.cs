using System.Globalization;
using System.Windows;
using System.Windows.Data;
using WDM.Models;

namespace WDM.ViewModels;

public sealed class RetryVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TaskStatus.Failed ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
