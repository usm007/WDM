using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WDM.ViewModels;

/// <summary>Maps a bool to a fixed-pixel <see cref="GridLength"/> (true) or zero
/// (false), so adaptive columns collapse completely in narrow windows.
/// ConverterParameter is the visible width in pixels (default 80).</summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            double width = 80;
            if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                width = parsed;
            else if (parameter is double d)
                width = d;
            return new GridLength(width);
        }
        return new GridLength(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
