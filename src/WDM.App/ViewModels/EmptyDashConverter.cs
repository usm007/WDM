using System.Globalization;
using System.Windows.Data;

namespace WDM.ViewModels;

public sealed class EmptyDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return "—";

        if (value is string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s == "0" || s == "0 B" || s == "0B" || s == "0 B/s" || s == "0.0 B/s" || s == "—")
                return "—";
            return s;
        }

        if (value is long l && l <= 0)
            return "—";

        if (value is double d && d <= 0)
            return "—";

        if (value is int i && i <= 0)
            return "—";

        return value.ToString() ?? "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
