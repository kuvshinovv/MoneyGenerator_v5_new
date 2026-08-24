// Common/ProfitLossSignConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;

namespace MoneyGenerator_v5.Common
{
    /// <summary>
    /// Конвертер для отображения знака "+" перед положительными числами
    /// </summary>
    public class ProfitLossSignConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return "";

            try
            {
                // Поддерживаем различные числовые типы
                decimal decimalValue;

                if (value is decimal dec)
                    decimalValue = dec;
                else if (value is double dbl)
                    decimalValue = (decimal)dbl;
                else if (value is float flt)
                    decimalValue = (decimal)flt;
                else if (value is int intVal)
                    decimalValue = intVal;
                else if (value is long longVal)
                    decimalValue = longVal;
                else if (value is short shortVal)
                    decimalValue = shortVal;
                else if (value is byte byteVal)
                    decimalValue = byteVal;
                else if (value is string str && decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
                    decimalValue = parsed;
                else
                    return "";

                // Возвращаем "+" для положительных чисел, иначе пустую строку
                return decimalValue > 0 ? "+" : "";
            }
            catch
            {
                return "";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}