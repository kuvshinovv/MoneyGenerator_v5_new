// Common/ParametersConverter.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace MoneyGenerator_v5.Common
{
    public class ParametersConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Dictionary<string, decimal> dict && dict.Any())
            {
                return string.Join(", ", dict.Select(p => $"{p.Key}={p.Value:F2}"));
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ParametersTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Dictionary<string, decimal> dict && dict.Any())
            {
                return string.Join("\n", dict.Select(p => $"{p.Key}: {p.Value:F2}"));
            }
            return "Нет параметров";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}