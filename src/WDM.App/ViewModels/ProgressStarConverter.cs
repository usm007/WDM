using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WDM.ViewModels;

/// <summary>Sizes the thin progress-bar fill as star columns so the fill
/// tracks Value at any width (binding to ActualWidth would go stale on
/// resize). parameter "fill" → fraction stars, anything else → rest stars.</summary>
public sealed class ProgressStarConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = 0;
        try
        {
            if (values is not null && values.Length >= 3)
            {
                double value = ToDouble(values[0]);
                double min = ToDouble(values[1]);
                double max = ToDouble(values[2]);
                if (max > min)
                    fraction = (value - min) / (max - min);
            }
        }
        catch { }
        if (fraction < 0) fraction = 0;
        if (fraction > 1) fraction = 1;
        bool fill = !string.Equals(parameter as string, "rest", StringComparison.OrdinalIgnoreCase);
        return new GridLength(fill ? fraction : 1 - fraction, GridUnitType.Star);
    }

    private static double ToDouble(object? v)
    {
        if (v is double d) return d;
        if (v is float f) return f;
        if (v is int i) return i;
        if (v is long l) return l;
        if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return 0;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
