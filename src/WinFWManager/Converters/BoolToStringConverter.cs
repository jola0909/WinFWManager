using System.Globalization;
using System.Windows.Data;

namespace WinFWManager.Converters;

public class BoolToStringConverter : IValueConverter
{
    public string? TrueValue { get; set; }
    public string? FalseValue { get; set; }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? TrueValue : FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
