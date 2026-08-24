// Common/StatusToSymbolConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;
using MoneyGenerator_v5.Models;

namespace MoneyGenerator_v5.Common
{
    public class StatusToSymbolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DealStatus status)
            {
                //  "🟢"  "🔴"  "⚪"

                return status == DealStatus.Open ? "⚪" : "🔴";
            }
            return "⚪";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}