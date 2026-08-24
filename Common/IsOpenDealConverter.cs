// Common/IsOpenDealConverter.cs

using System;
using System.Globalization;
using System.Windows.Data;
using MoneyGenerator_v5.Models;

namespace MoneyGenerator_v5.Common
{
    public class IsOpenDealConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                // Проверяем, что переданный объект - Deal
                if (value is Deal deal)
                {
                    // ✅ ВАЖНО: Проверяем только статус сделки
                    // Если сделка открыта - возвращаем true (доступно)
                    return deal.Status == DealStatus.Open;
                }

                // Если передано что-то другое или null - возвращаем false
                return false;
            }
            catch
            {
                return false;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}