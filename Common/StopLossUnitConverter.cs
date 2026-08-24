using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Common
{
    public class StopLossUnitConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is string type)
            {
                return type == "Percentage" ? "% от цены входа" : "руб.";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}
