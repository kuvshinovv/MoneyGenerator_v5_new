using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MoneyGenerator_v5.Common
{
    public class BooleanToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isTrading)
            {
                return isTrading ? Brushes.LightGreen : Brushes.Red;
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}