using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.ViewModels;
using System.Globalization;
using System.Windows.Data;


namespace MoneyGenerator_v5.Common
{
    public class BooleanToYesNoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? "Да" : "Нет";
            }
            return "Нет";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

}



