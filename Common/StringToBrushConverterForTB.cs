
// Common/StringToBrushConverterForTB.cs
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MoneyGenerator_v5.Common
{
    public class StringToBrushConverterForTB : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string colorString)
            {
                try
                {
                    if (colorString.StartsWith("#"))
                        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorString));

                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString($"#{colorString}"));
                }
                catch
                {
                    return new SolidColorBrush(Colors.Gray);
                }
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}