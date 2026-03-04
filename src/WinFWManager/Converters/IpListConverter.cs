using System.Globalization;
using System.Net;
using System.Windows.Data;

namespace WinFWManager.Converters;

public class IpListConverter : IValueConverter
{
    public static readonly IpListConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is List<IPAddress> addresses && addresses.Count > 0)
            return string.Join(", ", addresses);
        return "None";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
