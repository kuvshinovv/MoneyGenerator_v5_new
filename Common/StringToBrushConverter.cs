using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MoneyGenerator_v5.Common
{
    public class StringToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string colorName)
            {
                // Преобразуем строку в Brush
                return GetBrushFromString(colorName);
            }

            return Brushes.Gray; // Цвет по умолчанию
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        private Brush GetBrushFromString(string colorName)
        {
            if (string.IsNullOrWhiteSpace(colorName))
                return Brushes.Gray;

            // Приводим к нижнему регистру для удобства сравнения
            string lowerColor = colorName.Trim().ToLowerInvariant();

            switch (lowerColor)
            {
                case "green":
                case "зеленый":
                    return Brushes.LightGreen;

                case "red":
                case "красный":
                    return Brushes.Red;

                case "yellow":
                case "желтый":
                    return Brushes.Yellow;

                case "orange":
                case "оранжевый":
                    return Brushes.Orange;

                case "blue":
                case "синий":
                    return Brushes.Blue;

                case "gray":
                case "grey":
                case "серый":
                    return Brushes.Gray;

                case "white":
                case "белый":
                    return Brushes.White;

                case "black":
                case "черный":
                    return Brushes.Black;

                default:
                    // Пробуем создать цвет по имени
                    try
                    {
                        Color color = (Color)ColorConverter.ConvertFromString(colorName);
                        return new SolidColorBrush(color);
                    }
                    catch
                    {
                        // Если не удалось преобразовать - возвращаем серый
                        return Brushes.Gray;
                    }
            }
        }
    }
}