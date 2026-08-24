using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace MoneyGenerator_v5.Common
{
    public class SearchFilterConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] is IEnumerable instruments && values[1] is string searchText)
            {
                if (string.IsNullOrWhiteSpace(searchText))
                    return instruments;

                // Фильтруем инструменты
                var filtered = instruments
                    .Cast<Models.Instrument>()
                    .Where(instrument =>
                        instrument.DisplayName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();

                return filtered;
            }

            return values[0];
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}