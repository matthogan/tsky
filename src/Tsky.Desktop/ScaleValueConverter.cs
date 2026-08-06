using System.Globalization;
using System.Windows.Data;

namespace tsky;

public sealed class ScaleValueConverter : IValueConverter
{
    public double Factor { get; set; } = 1.0;

    public double Minimum { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double numericValue || double.IsNaN(numericValue) || double.IsInfinity(numericValue))
        {
            return Minimum;
        }

        var scaled = numericValue * Factor;
        return Math.Max(Minimum, scaled);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
