using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Data;

namespace MoneyGenerator_v5.Common
{
    public class DecimalToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is decimal dec)
            {
                // Используем текущую культуру для форматирования
                return dec.ToString(System.Globalization.CultureInfo.CurrentCulture);
            }
            return value?.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str)
            {
                // Заменяем точку на запятую перед парсингом
                string normalized = str.Replace('.', ',');

                if (decimal.TryParse(normalized,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture,
                    out decimal result))
                {
                    return result;
                }
            }
            return 0m;
        }
    }
}
