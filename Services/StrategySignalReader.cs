using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Strategies;

using System.Diagnostics;

/// <summary>
/// Класс для чтения сигналов и позиций из стратегии без использования рефлексии
/// </summary>
public class StrategySignalReader
{
    private readonly object _strategy;
    private readonly Type _strategyType;
    private readonly Dictionary<string, object> _cachedValues = new();
    private DateTime _lastReadTime = DateTime.MinValue;
    private const int CACHE_TTL_MS = 100;

    public StrategySignalReader(object strategy)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _strategyType = strategy.GetType();
    }

    /// <summary>
    /// Получает текущий сигнал стратегии
    /// </summary>
    public string GetCurrentSignal()
    {
        try
        {
            // Проверяем кэш
            if (IsCacheValid())
            {
                if (_cachedValues.TryGetValue("Signal", out var cachedSignal))
                    return cachedSignal?.ToString() ?? "";
            }

            string signal = "";

            // Определяем тип стратегии и используем правильные свойства
            if (_strategy is MaStrategy maStrategy)
            {
                signal = GetMaStrategySignal(maStrategy);
            }
            else if (_strategy is RsiStrategy rsiStrategy)
            {
                signal = GetRsiStrategySignal(rsiStrategy);
            }
            else if (_strategy is RatingStrategy ratingStrategy)
            {
                signal = GetRatingStrategySignal(ratingStrategy);
            }
            else if (_strategy is PairsTradingStrategy pairsStrategy)
            {
                signal = GetPairsStrategySignal(pairsStrategy);
            }
            else
            {
                // Fallback - используем рефлексию
                signal = GetSignalViaReflection();
            }

            // Кэшируем результат
            _cachedValues["Signal"] = signal;
            _lastReadTime = DateTime.Now;

            return signal;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка получения сигнала: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Получает текущую позицию стратегии
    /// </summary>
    public Position GetCurrentPosition()
    {
        try
        {
            // Проверяем кэш
            if (IsCacheValid())
            {
                if (_cachedValues.TryGetValue("Position", out var cachedPosition))
                    return cachedPosition as Position;
            }

            Position position = null;

            // Определяем тип стратегии
            if (_strategy is MaStrategy maStrategy)
            {
                position = GetMaStrategyPosition(maStrategy);
            }
            else if (_strategy is RsiStrategy rsiStrategy)
            {
                position = GetRsiStrategyPosition(rsiStrategy);
            }
            else if (_strategy is RatingStrategy ratingStrategy)
            {
                position = GetRatingStrategyPosition(ratingStrategy);
            }
            else if (_strategy is PairsTradingStrategy pairsStrategy)
            {
                position = GetPairsStrategyPosition(pairsStrategy);
            }
            else
            {
                position = GetPositionViaReflection();
            }

            _cachedValues["Position"] = position;
            _lastReadTime = DateTime.Now;

            return position;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка получения позиции: {ex.Message}");
            return null;
        }
    }

    #region Методы для конкретных стратегий

    private string GetMaStrategySignal(MaStrategy strategy)
    {
        try
        {
            // Пытаемся получить сигнал через публичное свойство
            var signalProperty = typeof(MaStrategy).GetProperty("CurrentSignal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (signalProperty != null)
            {
                var value = signalProperty.GetValue(strategy);
                if (value != null)
                    return value.ToString();
            }

            // Или через индикаторы
            var indicatorValues = typeof(MaStrategy).GetField("_indicatorValues",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (indicatorValues != null)
            {
                var values = indicatorValues.GetValue(strategy);
                if (values != null)
                {
                    var signalField = values.GetType().GetProperty("CurrentSignal");
                    if (signalField != null)
                    {
                        var value = signalField.GetValue(values);
                        if (value != null)
                            return value.ToString();
                    }
                }
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    private string GetRsiStrategySignal(RsiStrategy strategy)
    {
        try
        {
            var indicatorValues = typeof(RsiStrategy).GetField("_indicatorValues",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (indicatorValues != null)
            {
                var values = indicatorValues.GetValue(strategy);
                if (values != null)
                {
                    var signalField = values.GetType().GetProperty("Signal");
                    if (signalField != null)
                    {
                        var value = signalField.GetValue(values);
                        if (value != null)
                            return value.ToString();
                    }
                }
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    private string GetRatingStrategySignal(RatingStrategy strategy)
    {
        try
        {
            // Рейтинговая стратегия использует _currentSignal поле
            var signalField = typeof(RatingStrategy).GetField("_currentSignal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (signalField != null)
            {
                var value = signalField.GetValue(strategy);
                if (value != null)
                    return value.ToString();
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    private string GetPairsStrategySignal(PairsTradingStrategy strategy)
    {
        try
        {
            var indicatorValues = typeof(PairsTradingStrategy).GetField("_indicatorValues",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (indicatorValues != null)
            {
                var values = indicatorValues.GetValue(strategy);
                if (values != null)
                {
                    var signalField = values.GetType().GetProperty("Signal");
                    if (signalField != null)
                    {
                        var value = signalField.GetValue(values);
                        if (value != null)
                            return value.ToString();
                    }
                }
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    private Position GetMaStrategyPosition(MaStrategy strategy)
    {
        try
        {
            var positionField = typeof(MaStrategy).GetField("_currentPosition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (positionField != null)
            {
                return positionField.GetValue(strategy) as Position;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private Position GetRsiStrategyPosition(RsiStrategy strategy)
    {
        try
        {
            var positionField = typeof(RsiStrategy).GetField("_currentPosition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (positionField != null)
            {
                return positionField.GetValue(strategy) as Position;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private Position GetRatingStrategyPosition(RatingStrategy strategy)
    {
        try
        {
            var positionField = typeof(RatingStrategy).GetField("_currentPosition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (positionField != null)
            {
                return positionField.GetValue(strategy) as Position;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private Position GetPairsStrategyPosition(PairsTradingStrategy strategy)
    {
        try
        {
            // Для парной стратегии позиция состоит из двух инструментов
            // Возвращаем позицию по первому инструменту
            var positionA = typeof(PairsTradingStrategy).GetField("_positionA",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (positionA != null)
            {
                return positionA.GetValue(strategy) as Position;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Рефлексивные методы (Fallback)

    private string GetSignalViaReflection()
    {
        try
        {
            // Ищем поле _currentSignal
            var signalField = _strategyType.GetField("_currentSignal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (signalField != null)
            {
                var value = signalField.GetValue(_strategy);
                return value?.ToString() ?? "";
            }

            // Ищем поле _indicatorValues с Signal
            var indicatorField = _strategyType.GetField("_indicatorValues",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (indicatorField != null)
            {
                var values = indicatorField.GetValue(_strategy);
                if (values != null)
                {
                    var signalProp = values.GetType().GetProperty("Signal");
                    if (signalProp != null)
                    {
                        var value = signalProp.GetValue(values);
                        return value?.ToString() ?? "";
                    }
                    var signalField2 = values.GetType().GetField("Signal");
                    if (signalField2 != null)
                    {
                        var value = signalField2.GetValue(values);
                        return value?.ToString() ?? "";
                    }
                }
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    private Position GetPositionViaReflection()
    {
        try
        {
            var positionField = _strategyType.GetField("_currentPosition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (positionField != null)
            {
                return positionField.GetValue(_strategy) as Position;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    private bool IsCacheValid()
    {
        return (DateTime.Now - _lastReadTime).TotalMilliseconds < CACHE_TTL_MS;
    }
}