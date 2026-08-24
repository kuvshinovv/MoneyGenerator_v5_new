// Common/ProfitLossBrushConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MoneyGenerator_v5.Common
{
    public class ProfitLossBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is decimal decimalValue)
            {
                return decimalValue >= 0 ? new SolidColorBrush(Colors.DarkGreen) : new SolidColorBrush(Colors.Red);
            }

            if (value is double doubleValue)
            {
                return doubleValue >= 0 ? new SolidColorBrush(Colors.DarkGreen) : new SolidColorBrush(Colors.Red);
            }

            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}