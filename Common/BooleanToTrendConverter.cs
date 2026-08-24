using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MoneyGenerator_v5.Common
{
    public class BooleanToTrendConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is bool isBullish && values[1] is bool isBearish)
            {
                if (isBullish) return "БЫЧИЙ ТРЕНД";
                if (isBearish) return "МЕДВЕЖИЙ ТРЕНД";
                return "ФЛЭТ";
            }
            return "НЕТ ДАННЫХ";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

   
}