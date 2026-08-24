// Models/OptimizationParameters.cs
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace MoneyGenerator_v5.Models
{
    /// <summary>
    /// Параметр для оптимизации
    /// </summary>
    public class OptimizationParameter : ObservableObject
    {
        private string _name;
        private string _displayName;
        private decimal _currentValue;
        private decimal _minValue;
        private decimal _maxValue;
        private decimal _step;
        private bool _isSelected;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        public decimal CurrentValue
        {
            get => _currentValue;
            set => SetProperty(ref _currentValue, value);
        }

        public decimal MinValue
        {
            get => _minValue;
            set
            {
                if (SetProperty(ref _minValue, value))
                {
                    OnPropertyChanged(nameof(ValueCount));
                    OnPropertyChanged(nameof(Values));
                }
            }
        }

        public decimal MaxValue
        {
            get => _maxValue;
            set
            {
                if (SetProperty(ref _maxValue, value))
                {
                    OnPropertyChanged(nameof(ValueCount));
                    OnPropertyChanged(nameof(Values));
                }
            }
        }

        public decimal Step
        {
            get => _step;
            set
            {
                if (SetProperty(ref _step, value))
                {
                    OnPropertyChanged(nameof(ValueCount));
                    OnPropertyChanged(nameof(Values));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// Количество значений для перебора (свойство для привязки в XAML)
        /// </summary>
        public int ValueCount => GetValueCount();

        public IEnumerable<decimal> Values => GetValues();

        /// <summary>
        /// Получает количество значений для данного параметра
        /// </summary>
        public int GetValueCount()
        {
            if (Step == 0) return 1;

            // ✅ ИСПРАВЛЕНИЕ: Используем точный расчет с округлением
            decimal range = MaxValue - MinValue;
            if (range < 0) return 0;

            // Используем decimal.Divide для точного деления
            decimal countDecimal = decimal.Divide(range, Step);

            // Округляем вниз с учетом погрешности
            int count = (int)Math.Floor(countDecimal) + 1;

            Debug.WriteLine($"[GetValueCount] {Name}: Min={MinValue}, Max={MaxValue}, Step={Step}, Count={count}");
            return count;
        }

        /// <summary>
        /// Получает все значения для данного параметра
        /// </summary>
        public IEnumerable<decimal> GetValues()
        {
            if (Step == 0)
            {
                yield return CurrentValue;
                yield break;
            }

            // ✅ ИСПРАВЛЕНИЕ: Используем точный расчет количества итераций
            int count = GetValueCount();

            for (int i = 0; i < count; i++)
            {
                decimal value = MinValue + Step * i;

                // ✅ Корректируем последнее значение, чтобы избежать погрешности
                if (i == count - 1)
                {
                    value = MaxValue;
                }

                yield return value;
            }

            Debug.WriteLine($"[GetValues] {Name}: сгенерировано {count} значений");
        }
    }

    /// <summary>
    /// Результат одной итерации оптимизации
    /// </summary>
    public class OptimizationResult : ObservableObject
    {
        private int _iteration;
        private Dictionary<string, decimal> _parameters;
        private decimal _netProfit;
        private decimal _grossProfit;
        private decimal _profitFactor;
        private decimal _sharpeRatio;
        private decimal _maxDrawdown;
        private decimal _winRate;
        private int _totalTrades;
        private int _winningTrades;
        private int _losingTrades;
        private decimal _averageWin;
        private decimal _averageLoss;
        private decimal _recoveryFactor;
        private decimal _expectancy;
        private DateTime _startDate;
        private DateTime _endDate;

        private string _ticker;

        private string _timeFrame;


        public string Ticker
        {
            get => _ticker;
            set => SetProperty(ref _ticker, value);
        }

        public string TimeFrame
        {
            get => _timeFrame;
            set => SetProperty(ref _timeFrame, value);
        }





        public int Iteration
        {
            get => _iteration;
            set => SetProperty(ref _iteration, value);
        }

        public Dictionary<string, decimal> Parameters
        {
            get => _parameters;
            set => SetProperty(ref _parameters, value);
        }

        public decimal NetProfit
        {
            get => _netProfit;
            set => SetProperty(ref _netProfit, value);
        }

        public decimal GrossProfit
        {
            get => _grossProfit;
            set => SetProperty(ref _grossProfit, value);
        }

        public decimal ProfitFactor
        {
            get => _profitFactor;
            set => SetProperty(ref _profitFactor, value);
        }

        public decimal SharpeRatio
        {
            get => _sharpeRatio;
            set => SetProperty(ref _sharpeRatio, value);
        }

        public decimal MaxDrawdown
        {
            get => _maxDrawdown;
            set => SetProperty(ref _maxDrawdown, value);
        }

        public decimal WinRate
        {
            get => _winRate;
            set => SetProperty(ref _winRate, value);
        }

        public int TotalTrades
        {
            get => _totalTrades;
            set => SetProperty(ref _totalTrades, value);
        }

        public int WinningTrades
        {
            get => _winningTrades;
            set => SetProperty(ref _winningTrades, value);
        }

        public int LosingTrades
        {
            get => _losingTrades;
            set => SetProperty(ref _losingTrades, value);
        }

        public decimal AverageWin
        {
            get => _averageWin;
            set => SetProperty(ref _averageWin, value);
        }

        public decimal AverageLoss
        {
            get => _averageLoss;
            set => SetProperty(ref _averageLoss, value);
        }

        public decimal RecoveryFactor
        {
            get => _recoveryFactor;
            set => SetProperty(ref _recoveryFactor, value);
        }

        public decimal Expectancy
        {
            get => _expectancy;
            set => SetProperty(ref _expectancy, value);
        }

        public DateTime StartDate
        {
            get => _startDate;
            set => SetProperty(ref _startDate, value);
        }

        public DateTime EndDate
        {
            get => _endDate;
            set => SetProperty(ref _endDate, value);
        }

        // Форматированные строки для отображения
        public string FormattedNetProfit => NetProfit >= 0 ? $"+{NetProfit:F2}" : $"{NetProfit:F2}";
        public string FormattedProfitFactor => ProfitFactor.ToString("F2");
        public string FormattedSharpeRatio => SharpeRatio.ToString("F2");
        public string FormattedMaxDrawdown => MaxDrawdown.ToString("F1") + "%";
        public string FormattedWinRate => WinRate.ToString("F1") + "%";
        public string FormattedTotalTrades => TotalTrades.ToString();
        public string FormattedExpectancy => Expectancy.ToString("F2");
        public string FormattedRecoveryFactor => RecoveryFactor.ToString("F2");



        private List<decimal> _equityHistory;
        public List<decimal> EquityHistory
        {
            get => _equityHistory;
            set => SetProperty(ref _equityHistory, value);
        }

        private List<DateTime> _equityDates;
        public List<DateTime> EquityDates
        {
            get => _equityDates;
            set => SetProperty(ref _equityDates, value);
        }

        // Свойство для отображения наличия эквити
        public bool HasEquityData => _equityHistory != null && _equityHistory.Count > 0; 

        // Для отображения в UI
        public string EquitySummary => HasEquityData ? $"Эквити: {_equityHistory.Count} точек" : "Нет данных";

    }

    /// <summary>
    /// Настройки периода оптимизации
    /// </summary>
    public class OptimizationPeriod
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int Days { get; set; }
        public string DisplayName { get; set; }
    }
}