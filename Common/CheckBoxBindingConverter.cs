using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.ViewModels;
using System.Globalization;
using System.Windows.Data;

namespace MoneyGenerator_v5.Common
{
    

    /// <summary>
    /// Конвертер для привязки CheckBox к ViewModel
    /// </summary>
    public class CheckBoxBindingConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is LoadSavedStrategiesViewModel vm && values[1] is SavedStrategyInfo strategy)
            {
                return new { ViewModel = vm, Strategy = strategy };
            }
            return null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}