// Common/IsOpenOperationConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;
using MoneyGenerator_v5.Models;

namespace MoneyGenerator_v5.Common
{
    public class IsOpenOperationConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length > 0 && values[0] is ProcessedOperation operation)
            {
                return operation.Status?.Contains("Open") == true;
            }
            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}