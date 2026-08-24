// BooleanToTrendTextConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MoneyGenerator_v5.Common
{
    public class BooleanToTrendTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isBullishTrend && parameter is string type)
            {
                if (type == "Text")
                {
                    // Это для текста
                    return isBullishTrend ? "БЫЧИЙ ТРЕНД" :
                           _isBearishTrend ? "МЕДВЕЖИЙ ТРЕНД" : "ФЛЭТ";
                }
                else if (type == "Color")
                {
                    // Это для цвета
                    return isBullishTrend ? Brushes.Green :
                           _isBearishTrend ? Brushes.Red : Brushes.Gray;
                }
            }
            return "ФЛЭТ";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        // Нужно добавить поле для медвежьего тренда
        private bool _isBearishTrend;

        public void SetBearishTrend(bool value)
        {
            _isBearishTrend = value;
        }
    }
}