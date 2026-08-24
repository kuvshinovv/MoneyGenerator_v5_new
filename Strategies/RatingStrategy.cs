using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Common;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.ViewModels;
using Skender.Stock.Indicators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MoneyGenerator_v5.Strategies
{
    #region Вспомогательные классы
    // Класс для хранения фрактала
    public class Fractal
    {
        public DateTime Time { get; set; }
        public decimal Price { get; set; }
        public FractalType Type { get; set; } // High или Low
        public int LeftBars { get; set; } // Количество свечей слева
        public int RightBars { get; set; } // Количество свечей справа
        public int Index { get; set; } // Индекс в массиве свечей
    }

    public enum FractalType
    {
        High,  // Верхний фрактал (максимум)
        Low    // Нижний фрактал (минимум)
    }

    // Класс для хранения значений индикатора на фрактале
    public class IndicatorValueAtFractal
    {
        public string IndicatorName { get; set; }
        public int Period { get; set; }
        public decimal Value { get; set; }
        public Fractal Fractal { get; set; }
        public decimal PriceAtFractal { get; set; }
    }

    // Класс для хранения совпадений
    public class IndicatorMatch : ObservableObject
    {
        private int _rank;
        private FractalType _type;
        private int _matchCount;
        private Dictionary<string, decimal> _typicalValues;
        private double _matchPercentage;
        private DateTime _firstSeen;
        private DateTime _lastSeen;
        private decimal _avgPrice;
        private MatchType _matchType; // Добавьте это поле

        public MatchType MatchType // Добавьте это свойство
        {
            get => _matchType;
            set => SetProperty(ref _matchType, value);
        }
        public int Rank
        {
            get => _rank;
            set => SetProperty(ref _rank, value);
        }

        public FractalType Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
        }

        public string TypeDisplay => Type == FractalType.Low ? "📈 ПОКУПКА" : "📉 ПРОДАЖА";

        public int MatchCount
        {
            get => _matchCount;
            set => SetProperty(ref _matchCount, value);
        }

        public Dictionary<string, decimal> TypicalValues
        {
            get => _typicalValues;
            set => SetProperty(ref _typicalValues, value);
        }

        public double MatchPercentage
        {
            get => _matchPercentage;
            set => SetProperty(ref _matchPercentage, value);
        }

        public string MatchPercentageDisplay => $"{MatchPercentage:F1}%";

        public DateTime FirstSeen
        {
            get => _firstSeen;
            set => SetProperty(ref _firstSeen, value);
        }

        public DateTime LastSeen
        {
            get => _lastSeen;
            set => SetProperty(ref _lastSeen, value);
        }

        public decimal AvgPrice
        {
            get => _avgPrice;
            set => SetProperty(ref _avgPrice, value);
        }

        public string AvgPriceDisplay => AvgPrice.ToString("F2");

        // Для отображения в списке
        public string Summary => $"#{Rank} | {TypeDisplay} | Совпадений: {MatchCount} | Цена: {AvgPrice:F2}";

        public object IndicatorName { get; internal set; }
    }

    public enum MatchType
    {
        Exact,      // Точное совпадение (0%)
        LowDeviation, // Малое отклонение (0.01-0.1%)
        MediumDeviation // Среднее отклонение (0.2-0.5%)
    }

    // Класс для хранения результатов анализа
    public class RatingAnalysisResult
    {
        public List<Fractal> Fractals { get; set; } = new List<Fractal>();
        public Dictionary<string, List<IndicatorMatch>> BuyMatches { get; set; } = new Dictionary<string, List<IndicatorMatch>>();
        public Dictionary<string, List<IndicatorMatch>> SellMatches { get; set; } = new Dictionary<string, List<IndicatorMatch>>();
        public int TotalBuyRating { get; set; }
        public int TotalSellRating { get; set; }
        public DateTime AnalysisTime { get; set; }
        public int AnalyzedCandlesCount { get; set; }
    }

    // Класс для хранения индикатора
    public class IndicatorInfo
    {
        public string Name { get; set; }
        public int Period { get; set; }
        public Func<IEnumerable<Quote>, int, IEnumerable<object>> Calculator { get; set; }
        public Func<object, decimal?> ValueSelector { get; set; }
    }

    // Класс для хранения статистики по фракталам
    public class FractalStatistics : INotifyPropertyChanged
    {
        private int _totalFractals;
        private int _buyFractals;
        private int _sellFractals;
        private DateTime _firstFractalDate;
        private DateTime _lastFractalDate;

        public int TotalFractals
        {
            get => _totalFractals;
            set { _totalFractals = value; OnPropertyChanged(); }
        }

        public int BuyFractals
        {
            get => _buyFractals;
            set { _buyFractals = value; OnPropertyChanged(); }
        }

        public int SellFractals
        {
            get => _sellFractals;
            set { _sellFractals = value; OnPropertyChanged(); }
        }

        public DateTime FirstFractalDate
        {
            get => _firstFractalDate;
            set { _firstFractalDate = value; OnPropertyChanged(); }
        }

        public DateTime LastFractalDate
        {
            get => _lastFractalDate;
            set { _lastFractalDate = value; OnPropertyChanged(); }
        }



        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // Класс для хранения статистики совпадений
    public class MatchStatistics : INotifyPropertyChanged
    {
        private int _exactMatches;
        private int _lowDeviationMatches;
        private int _mediumDeviationMatches;
        private int _totalGroups;
        private int _totalCombinations;
        private int _mostFrequentGroup;

        public int TotalGroups
        {
            get => _totalGroups;
            set { _totalGroups = value; OnPropertyChanged(); }
        }

        public int TotalCombinations
        {
            get => _totalCombinations;
            set { _totalCombinations = value; OnPropertyChanged(); }
        }

        public int MostFrequentGroup
        {
            get => _mostFrequentGroup;
            set { _mostFrequentGroup = value; OnPropertyChanged(); }
        }

        public void ForceUpdate()
        {
            OnPropertyChanged(nameof(ExactMatches));
            OnPropertyChanged(nameof(LowDeviationMatches));
            OnPropertyChanged(nameof(MediumDeviationMatches));
            OnPropertyChanged(nameof(Matches));
        }


        private ObservableCollection<IndicatorMatch> _matches = new ObservableCollection<IndicatorMatch>();

        public int ExactMatches
        {
            get => _exactMatches;
            set { _exactMatches = value; OnPropertyChanged(); }
        }

        public int LowDeviationMatches
        {
            get => _lowDeviationMatches;
            set { _lowDeviationMatches = value; OnPropertyChanged(); }
        }

        public int MediumDeviationMatches
        {
            get => _mediumDeviationMatches;
            set { _mediumDeviationMatches = value; OnPropertyChanged(); }
        }

        public ObservableCollection<IndicatorMatch> Matches
        {
            get => _matches;
            set { _matches = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // Класс для хранения прогресса анализа
    public class AnalysisProgress : INotifyPropertyChanged
    {
        private int _currentStep;
        private int _totalSteps = 100;
        private string _currentOperation;

        public int CurrentStep
        {
            get => _currentStep;
            set { _currentStep = value; OnPropertyChanged(); OnPropertyChanged(nameof(Percentage)); }
        }

        public int TotalSteps
        {
            get => _totalSteps;
            set { _totalSteps = value; OnPropertyChanged(); OnPropertyChanged(nameof(Percentage)); }
        }

        public string CurrentOperation
        {
            get => _currentOperation;
            set { _currentOperation = value; OnPropertyChanged(); }
        }

        public double Percentage => TotalSteps > 0 ? (double)CurrentStep / TotalSteps * 100 : 0;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
    // Класс для хранения комбинации значений индикаторов на фрактале
    public class IndicatorCombination
    {
        public DateTime FractalTime { get; set; }
        public FractalType Type { get; set; } // High или Low
        public decimal FractalPrice { get; set; }
        public Dictionary<string, decimal> IndicatorValues { get; set; } = new();

        // Для сравнения комбинаций
        public string GetSignature(int precision = 4)
        {
            // Сортируем индикаторы для одинакового порядка
            var sorted = IndicatorValues.OrderBy(x => x.Key);
            return string.Join("|", sorted.Select(x => $"{x.Key}:{Math.Round(x.Value, precision)}"));
        }

        // Проверка совпадения с другой комбинацией с заданной погрешностью
        public bool Matches(IndicatorCombination other, decimal tolerancePercent, out int matchCount, out int totalCount)
        {
            matchCount = 0;
            totalCount = IndicatorValues.Count;

            foreach (var kvp in IndicatorValues)
            {
                if (other.IndicatorValues.TryGetValue(kvp.Key, out decimal otherValue))
                {
                    // Защита от деления на ноль
                    if (otherValue == 0)
                    {
                        // Если оба значения равны 0 - считаем совпадением
                        if (kvp.Value == 0)
                        {
                            matchCount++;
                        }
                        // Иначе - не совпадает
                        continue;
                    }

                    decimal deviation = Math.Abs((kvp.Value - otherValue) / otherValue * 100);
                    if (deviation <= tolerancePercent)
                    {
                        matchCount++;
                    }
                }
            }

            return matchCount > 0;
        }
    }

    // Класс для хранения сгруппированных совпадений
    public class MatchGroup
    {
        public string Signature { get; set; } // Уникальная сигнатура комбинации
        public FractalType Type { get; set; }
        public List<IndicatorCombination> Combinations { get; set; } = new();
        public Dictionary<string, decimal> TypicalValues { get; set; } = new(); // Типичные значения (средние)

        public int Count => Combinations.Count;
        public decimal AveragePrice => Combinations.Average(c => c.FractalPrice);
        public DateTime FirstOccurrence => Combinations.Min(c => c.FractalTime);
        public DateTime LastOccurrence => Combinations.Max(c => c.FractalTime);

        // Процент совпадения с данной группой
        public double MatchPercentage(IndicatorCombination combination, decimal tolerancePercent)
        {
            int matchCount = 0;
            int totalCount = TypicalValues.Count;

            foreach (var kvp in TypicalValues)
            {
                if (combination.IndicatorValues.TryGetValue(kvp.Key, out decimal value))
                {
                    // Защита от деления на ноль
                    if (kvp.Value == 0)
                    {
                        if (value == 0)
                        {
                            matchCount++;
                        }
                        continue;
                    }

                    decimal deviation = Math.Abs((value - kvp.Value) / kvp.Value * 100);
                    if (deviation <= tolerancePercent)
                    {
                        matchCount++;
                    }
                }
            }

            return totalCount > 0 ? (double)matchCount / totalCount * 100 : 0;
        }
    }

    // Класс для отображения в UI
    public class MatchGroupDisplay
    {
        public string Rank { get; set; } // Место в рейтинге (1, 2, 3...)
        public FractalType Type { get; set; }
        public int OccurrenceCount { get; set; } // Сколько раз встретилась
        public string MatchPercentage { get; set; } // Процент совпадений (для текущей группы)
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public decimal AvgPrice { get; set; }
        public List<IndicatorValueDisplay> IndicatorValues { get; set; } = new();
    }

    public class IndicatorValueDisplay
    {
        public string Name { get; set; }
        public decimal Value { get; set; }
        public string FormattedValue => Value.ToString("F4");
    }
    #endregion

    public partial class RatingStrategy : INotifyPropertyChanged
    {

        #region Поля и свойства
        public string Name => "Рейтинговая стратегия";
        public string Type => "Rating";

        private readonly ILogger _logger;
        private readonly IProvirerService _provider;
        private readonly StrategyViewModel _strategyViewModel;
        private readonly RatingSettingsViewModel _parameters;
        private readonly RatingViewModel _indicatorValues;
        private readonly TransactionsService _transactionsService;

        private Models.Instrument _instrument;
        private string _timeframe;
        private decimal _currentPrice;
        public Position _currentPosition;
        private decimal _atrValue;
        private DateTime _lastCalculation = DateTime.MinValue;

        // Кэш свечей из реального потока данных
        private readonly List<Quote> _quoteCache = new();
        private readonly int _maxCacheSize = 1000;
        private DateTime _lastCandleTime = DateTime.MinValue;

        // Текущие значения индикаторов
        private readonly Dictionary<int, decimal> _smaValues = new();
        private readonly Dictionary<int, decimal> _emaValues = new();
        private readonly Dictionary<int, decimal> _rsiValues = new();

        // Периоды для стратегии - будут загружаться из настроек
        private List<int> _trendPeriods = new(); // Для анализа тренда
        private List<int> _oscillatorPeriods = new(); // Для осцилляторов
        private List<int> _volumePeriods = new(); // Для объемных индикаторов
        private const int _atrPeriod = 14; // ATR период

        // Состояние
        private bool _isBullishTrend;
        private bool _isBearishTrend;
        private string _trendStrength = "Нейтральный";
        private string _currentSignal = "ОЖИДАНИЕ";
        private decimal _entryPrice;
        private decimal _stopLoss;
        private decimal _takeProfit;

        // Рейтинговые показатели
        private int _buyRating = 0;

        private int _sellRating = 0;
        private int _maxRating = 10;

        private DateTime _lastPositionCheck = DateTime.MinValue;

        private bool _isEntering = false;
        private readonly object _entryLock = new object();
        private decimal _lastKnownPosition = 0;
        private readonly object _positionLock = new object();
        private DateTime _lastEntryAttempt = DateTime.MinValue;
        private const int ENTRY_COOLDOWN_SECONDS = 5;
        private string _accountId;
        List<Position> _positionsList = new List<Position>();
        bool _checkPos = false;

        private bool _isExiting = false;
        private readonly object _exitLock = new object();
        private DateTime _lastExitAttempt = DateTime.MinValue;
        private const int EXIT_COOLDOWN_SECONDS = 5;

        private Dictionary<int, decimal> _previousEmaValues = new();
        private decimal _previousEmaShort;
        private decimal _previousEmaMedium;

        public StrategyState State { get; set; } = StrategyState.Stopped;

        public event Action OnValuesUpdated;
        public event Action<decimal> OnPriceUpdated;

        // Результаты анализа
        private RatingAnalysisResult _analysisResult = new RatingAnalysisResult();
        private DateTime _lastAnalysisTime = DateTime.MinValue;
        private const int ANALYSIS_COOLDOWN_HOURS = 24; // Анализ раз в сутки
        private bool _isAnalyzing = false;
        private readonly object _analysisLock = new object();

        // Список всех доступных индикаторов
        private List<IndicatorInfo> _availableIndicators = new List<IndicatorInfo>();

        // Для отображения в UI
        // И замените на обычные поля:
        private string _analysisStatus = "Анализ не выполнен";
        private DateTime _lastAnalysisDate = DateTime.MinValue;
        private ObservableCollection<IndicatorMatch> _topBuyMatches = new();
        private ObservableCollection<IndicatorMatch> _topSellMatches = new();
        private int _totalCandlesAnalyzed;
        private int _fractalsFound;

        public RatingStrategy.RatingSettingsViewModel Parameters => _parameters;



        // Добавьте публичные свойства для доступа из UI:
        public string AnalysisStatus
        {
            get => _analysisStatus;
            set
            {
                if (_analysisStatus != value)
                {
                    _analysisStatus = value;
                    OnPropertyChanged(nameof(AnalysisStatus));
                }
            }
        }

        public DateTime LastAnalysisDate
        {
            get => _lastAnalysisDate;
            set
            {
                if (_lastAnalysisDate != value)
                {
                    _lastAnalysisDate = value;
                    OnPropertyChanged(nameof(LastAnalysisDate));
                }
            }
        }
        private bool _useGlobalStopLoss;
        public bool UseGlobalStopLoss
        {
            get => _useGlobalStopLoss;
            set
            {
                if (_useGlobalStopLoss != value)
                {
                    _useGlobalStopLoss = value;
                    Debug.WriteLine($"{GetType().Name}.UseGlobalStopLoss = {value}");
                }
            }
        }

        private decimal _globalStopLossValue = 2;
        public decimal GlobalStopLossValue
        {
            get => _globalStopLossValue;
            set
            {
                if (_globalStopLossValue != value)
                {
                    _globalStopLossValue = value;
                    Debug.WriteLine($"{GetType().Name}.GlobalStopLossValue = {value}%");
                }
            }
        }

        private bool _useGlobalTakeProfit;
        public bool UseGlobalTakeProfit
        {
            get => _useGlobalTakeProfit;
            set
            {
                if (_useGlobalTakeProfit != value)
                {
                    _useGlobalTakeProfit = value;
                    Debug.WriteLine($"{GetType().Name}.UseGlobalTakeProfit = {value}");
                }
            }
        }

        private decimal _globalTakeProfitValue = 5;
        public decimal GlobalTakeProfitValue
        {
            get => _globalTakeProfitValue;
            set
            {
                if (_globalTakeProfitValue != value)
                {
                    _globalTakeProfitValue = value;
                    Debug.WriteLine($"{GetType().Name}.GlobalTakeProfitValue = {value}%");
                }
            }
        }
        public ObservableCollection<IndicatorMatch> TopBuyMatches => _topBuyMatches;
        public ObservableCollection<IndicatorMatch> TopSellMatches => _topSellMatches;
        public int TotalCandlesAnalyzed => _totalCandlesAnalyzed;
        public int FractalsFound => _fractalsFound;

        public event PropertyChangedEventHandler PropertyChanged;


        // Для отображения прогресса анализа
        private AnalysisProgress _analysisProgress = new AnalysisProgress();
        private bool _isAnalysisProgressVisible = false;

        // Статистика фракталов
        
        private FractalStatistics _fractalStats;
        private MatchStatistics _buyMatchStats;
        private MatchStatistics _sellMatchStats;
        private List<MatchGroup> _buyGroups = new();
        private List<MatchGroup> _sellGroups = new();


        // Для отслеживания новых фракталов
        private DateTime _lastFractalCheck = DateTime.MinValue;
        private int _lastFractalCount = 0;
        private const int FRACTAL_CHECK_INTERVAL_MINUTES = 5; // Проверка новых фракталов каждые 5 минут



        // Публичные свойства для привязки к UI
        public FractalStatistics FractalStats => _fractalStats;
        public MatchStatistics BuyMatchStats => _buyMatchStats;
        public MatchStatistics SellMatchStats => _sellMatchStats;








        // Публичные свойства для привязки к UI
        public bool IsAnalysisProgressVisible
        {
            get => _isAnalysisProgressVisible;
            set
            {
                if (_isAnalysisProgressVisible != value)
                {
                    _isAnalysisProgressVisible = value;
                    OnPropertyChanged(nameof(IsAnalysisProgressVisible));
                }
            }
        }

        public double AnalysisProgressPercentage => _analysisProgress.Percentage;
        public string AnalysisCurrentOperation => _analysisProgress.CurrentOperation;

        















        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion









        public RatingStrategy(
            ILogger<RatingStrategy> logger,
            IProvirerService provider,
            ConnectionManager connectionManager,
            StrategyViewModel strategyViewModel,
            TransactionsService transactionsService,
            MainViewModel mainViewModel = null)
        {
            _logger = logger;
            _provider = provider;
            _strategyViewModel = strategyViewModel;
            _parameters = new RatingSettingsViewModel();
            _indicatorValues = new RatingViewModel();

            _parameters.OnParametersChanged += OnParametersChanged;
            UpdatePeriodsFromSettings();

            // Инициализируем список доступных индикаторов
            InitializeIndicators();


            // ✅ СОЗДАЕМ TransactionsService
            var transactionsLogger = logger as ILogger<TransactionsService> ??
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionsService>.Instance;
            _transactionsService = new TransactionsService(
                provider,
                mainViewModel,
                strategyViewModel,
                _strategyViewModel.Instrument,
                mainViewModel.SelectedAccount,
                transactionsLogger);



            // Статистика фракталов
            _fractalStats = new FractalStatistics();
            _buyMatchStats = new MatchStatistics();
            _sellMatchStats = new MatchStatistics();
        }



        private void InitializeIndicators()
        {
            // Трендовые индикаторы
            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "SMA",
                Period = 20,
                Calculator = (q, p) => q.GetSma(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var sma = obj as SmaResult;
                    return (decimal?)(sma?.Sma);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "EMA",
                Period = 20,
                Calculator = (q, p) => q.GetEma(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var ema = obj as EmaResult;
                    return (decimal?)(ema?.Ema);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "WMA",
                Period = 20,
                Calculator = (q, p) => q.GetWma(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var wma = obj as WmaResult;
                    return (decimal?)(wma?.Wma);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "HMA",
                Period = 20,
                Calculator = (q, p) => q.GetHma(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var hma = obj as HmaResult;
                    return (decimal?)(hma?.Hma);
                }
            });

            // Осцилляторы
            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "RSI",
                Period = 14,
                Calculator = (q, p) => q.GetRsi(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var rsi = obj as RsiResult;
                    return (decimal?)(rsi?.Rsi);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "CCI",
                Period = 20,
                Calculator = (q, p) => q.GetCci(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var cci = obj as CciResult;
                    return (decimal?)(cci?.Cci);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "STOCH",
                Period = 14,
                Calculator = (q, p) => q.GetStoch(p, 3, 3).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var stoch = obj as StochResult;
                    return (decimal?)(stoch?.Oscillator);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "WILLIAMS",
                Period = 14,
                Calculator = (q, p) => q.GetWilliamsR(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var williams = obj as WilliamsResult;
                    return (decimal?)(williams?.WilliamsR);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "MFI",
                Period = 14,
                Calculator = (q, p) => q.GetMfi(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var mfi = obj as MfiResult;
                    return (decimal?)(mfi?.Mfi);
                }
            });

            // Индикаторы волатильности
            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "ATR",
                Period = 14,
                Calculator = (q, p) => q.GetAtr(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var atr = obj as AtrResult;
                    return (decimal?)(atr?.Atr);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "BBANDS",
                Period = 20,
                Calculator = (q, p) => q.GetBollingerBands(p, 2).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var bb = obj as BollingerBandsResult;
                    return (decimal?)(bb?.Sma);
                }
            });

            // Объемные индикаторы
            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "OBV",
                Period = 0,
                Calculator = (q, p) => q.GetObv().Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var obv = obj as ObvResult;
                    return (decimal?)(obv?.Obv);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "ADL",
                Period = 0,
                Calculator = (q, p) => q.GetAdl().Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var adl = obj as AdlResult;
                    return (decimal?)(adl?.Adl);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "CMF",
                Period = 20,
                Calculator = (q, p) => q.GetCmf(p).Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var cmf = obj as CmfResult;
                    return (decimal?)(cmf?.Cmf);
                }
            });

            _availableIndicators.Add(new IndicatorInfo
            {
                Name = "VWAP",
                Period = 0,
                Calculator = (q, p) => q.GetVwap().Cast<object>(),
                ValueSelector = (obj) =>
                {
                    var vwap = obj as VwapResult;
                    return (decimal?)(vwap?.Vwap);
                }
            });
        }

        // Метод для поиска фракталов
        private List<Fractal> FindFractals(List<Quote> quotes, int minBars = 10, int maxBars = 15)
        {
            var fractals = new List<Fractal>();

            if (quotes.Count < maxBars * 2 + 1)
                return fractals;

            for (int i = maxBars; i < quotes.Count - maxBars; i++)
            {
                // Поиск верхнего фрактала (High)
                bool isHigh = true;
                for (int j = 1; j <= maxBars; j++)
                {
                    if (quotes[i - j].High > quotes[i].High || quotes[i + j].High > quotes[i].High)
                    {
                        isHigh = false;
                        break;
                    }
                }

                // Проверяем, что минимум 6 свечей слева и справа ниже
                if (isHigh)
                {
                    // Проверяем, что нет более высоких точек в диапазоне minBars-maxBars
                    bool valid = true;
                    int leftCount = 0, rightCount = 0;

                    for (int j = 1; j <= maxBars; j++)
                    {
                        if (j <= maxBars && quotes[i - j].High < quotes[i].High)
                            leftCount++;
                        if (j <= maxBars && quotes[i + j].High < quotes[i].High)
                            rightCount++;
                    }

                    if (leftCount >= minBars && rightCount >= minBars)
                    {
                        fractals.Add(new Fractal
                        {
                            Time = quotes[i].Date,
                            Price = quotes[i].High,
                            Type = FractalType.High,
                            LeftBars = leftCount,
                            RightBars = rightCount,
                            Index = i
                        });
                    }
                }

                // Поиск нижнего фрактала (Low)
                bool isLow = true;
                for (int j = 1; j <= maxBars; j++)
                {
                    if (quotes[i - j].Low < quotes[i].Low || quotes[i + j].Low < quotes[i].Low)
                    {
                        isLow = false;
                        break;
                    }
                }

                if (isLow)
                {
                    int leftCount = 0, rightCount = 0;

                    for (int j = 1; j <= maxBars; j++)
                    {
                        if (j <= maxBars && quotes[i - j].Low > quotes[i].Low)
                            leftCount++;
                        if (j <= maxBars && quotes[i + j].Low > quotes[i].Low)
                            rightCount++;
                    }

                    if (leftCount >= minBars && rightCount >= minBars)
                    {
                        fractals.Add(new Fractal
                        {
                            Time = quotes[i].Date,
                            Price = quotes[i].Low,
                            Type = FractalType.Low,
                            LeftBars = leftCount,
                            RightBars = rightCount,
                            Index = i
                        });
                    }
                }
            }

            return fractals;
        }


        // Метод для расчета всех индикаторов на заданной свече
        private Dictionary<string, decimal> CalculateAllIndicatorsAtPoint(List<Quote> quotes, int index)
        {
            var result = new Dictionary<string, decimal>();

            foreach (var indicator in _availableIndicators)
            {
                try
                {
                    // Берем подмножество свечей до текущего индекса
                    var subset = quotes.Take(index + 1).ToList();
                    if (subset.Count < indicator.Period && indicator.Period > 0)
                        continue;

                    var indicatorValues = indicator.Calculator(subset, indicator.Period).ToList();
                    if (indicatorValues.Any() && indicatorValues.Last() != null)
                    {
                        var value = indicator.ValueSelector(indicatorValues.Last());
                        // Проверяем, что значение не null
                        if (value.HasValue)
                        {
                            // Для decimal проверяем, что значение не равно 0 (если 0 - пропускаем)
                            // или можно добавить другие условия валидации
                            if (value.Value != 0)
                            {
                                string key = $"{indicator.Name}_{indicator.Period}";
                                result[key] = value.Value;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Error calculating {indicator.Name}_{indicator.Period}: {ex.Message}");
                }
            }

            return result;
        }

        // Основной метод анализа
        public async Task PerformAnalysisAsync()
        {
            if (_isAnalyzing)
            {
                Debug.WriteLine("[ANALYSIS] Analysis already in progress, skipping");
                return;
            }

            lock (_analysisLock)
            {
                _isAnalyzing = true;
            }

            try
            {
                Debug.WriteLine($"[ANALYSIS] Starting analysis at {DateTime.Now}");
                Debug.WriteLine($"[ANALYSIS] Trigger: Manual={!_isAnalyzing}, LastAnalysis={_lastAnalysisDate}");
                IsAnalysisProgressVisible = true;
                UpdateProgress(0, "Загрузка исторических данных...");

                // Загружаем ВСЮ историю
                var allCandles = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(100000);
                var quotes = allCandles.Select(c => new Quote
                {
                    Date = c.Time,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume
                }).ToList();

                UpdateProgress(20, "Поиск фракталов...");

                // Находим ВСЕ фракталы
                var allFractals = FindFractals(quotes, 6, 10);
                _fractalStats.TotalFractals = allFractals.Count;
                _fractalStats.BuyFractals = allFractals.Count(f => f.Type == FractalType.Low);
                _fractalStats.SellFractals = allFractals.Count(f => f.Type == FractalType.High);

                // Обновляем счетчик последних фракталов
                _lastFractalCount = allFractals.Count;
                Debug.WriteLine($"[ANALYSIS] Found {allFractals.Count} fractals");

                UpdateProgress(40, "Расчет индикаторов на фракталах...");

                // Для каждого фрактала рассчитываем ВСЕ индикаторы
                var combinations = new List<IndicatorCombination>();
                int processed = 0;

                foreach (var fractal in allFractals)
                {
                    var indicatorValues = CalculateAllIndicatorsAtPoint(quotes, fractal.Index);

                    var combination = new IndicatorCombination
                    {
                        FractalTime = fractal.Time,
                        Type = fractal.Type,
                        FractalPrice = fractal.Price,
                        IndicatorValues = indicatorValues
                    };

                    combinations.Add(combination);

                    processed++;
                    if (processed % 10 == 0)
                    {
                        UpdateProgress(40 + (int)((double)processed / allFractals.Count * 30),
                            $"Обработано фракталов: {processed}/{allFractals.Count}");
                    }
                }

                UpdateProgress(70, "Группировка совпадений...");

                // Проверяем, есть ли комбинации с данными
                var validCombinations = combinations.Where(c => c.IndicatorValues.Any()).ToList();
                Debug.WriteLine($"[ANALYSIS] Valid combinations: {validCombinations.Count} of {combinations.Count}");

                if (!validCombinations.Any())
                {
                    AnalysisStatus = "Нет данных для группировки";
                    IsAnalysisProgressVisible = false;
                    return;
                }

                // Группируем по типу и сигнатуре
                var buyGroups = GroupCombinations(validCombinations.Where(c => c.Type == FractalType.Low).ToList());
                var sellGroups = GroupCombinations(validCombinations.Where(c => c.Type == FractalType.High).ToList());

                // Сортируем по популярности
                var topBuyGroups = buyGroups.OrderByDescending(g => g.Count).ToList();
                var topSellGroups = sellGroups.OrderByDescending(g => g.Count).ToList();

                UpdateProgress(90, "Подготовка данных для отображения...");

                // Обновляем UI
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Очищаем старые данные
                    _topBuyMatches.Clear();
                    _topSellMatches.Clear();

                    // Добавляем топ-10 групп для покупок
                    int rank = 1;
                    foreach (var group in topBuyGroups.Take(10))
                    {
                        _topBuyMatches.Add(new IndicatorMatch
                        {
                            Rank = rank++,
                            Type = group.Type,
                            MatchCount = group.Count,
                            TypicalValues = group.TypicalValues,
                            MatchPercentage = 100,
                            FirstSeen = group.FirstOccurrence,
                            LastSeen = group.LastOccurrence,
                            AvgPrice = group.AveragePrice,
                            MatchType = MatchType.Exact // Добавьте это
                        });
                    }

                    // Добавляем топ-10 групп для продаж
                    rank = 1;
                    foreach (var group in topSellGroups.Take(10))
                    {
                        _topSellMatches.Add(new IndicatorMatch
                        {
                            Rank = rank++,
                            Type = group.Type,
                            MatchCount = group.Count,
                            TypicalValues = group.TypicalValues,
                            MatchPercentage = 100,
                            FirstSeen = group.FirstOccurrence,
                            LastSeen = group.LastOccurrence,
                            AvgPrice = group.AveragePrice,
                            MatchType = MatchType.Exact // Добавьте это
                        });
                    }

                    // Обновляем статистику
                    if (_buyMatchStats != null)
                    {
                        _buyMatchStats.ExactMatches = topBuyGroups.Sum(g => g.Count);
                        _buyMatchStats.LowDeviationMatches = topBuyGroups.Count;
                        _buyMatchStats.MediumDeviationMatches = 0;
                    }

                    if (_sellMatchStats != null)
                    {
                        _sellMatchStats.ExactMatches = topSellGroups.Sum(g => g.Count);
                        _sellMatchStats.LowDeviationMatches = topSellGroups.Count;
                        _sellMatchStats.MediumDeviationMatches = 0;
                    }

                    // Обновляем UI
                    OnPropertyChanged(nameof(TopBuyMatches));
                    OnPropertyChanged(nameof(TopSellMatches));
                    OnPropertyChanged(nameof(BuyMatchStats));
                    OnPropertyChanged(nameof(SellMatchStats));

                    AnalysisStatus = $"Анализ завершен. Найдено групп: BUY={topBuyGroups.Count}, SELL={topSellGroups.Count}";
                    LastAnalysisDate = DateTime.Now;
                });

                UpdateProgress(100, "Готово");
                await Task.Delay(1000);
                IsAnalysisProgressVisible = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analysis failed");
                Debug.WriteLine($"[ANALYSIS ERROR] {ex.Message}");
                Debug.WriteLine($"[ANALYSIS STACK] {ex.StackTrace}");
                AnalysisStatus = $"Ошибка: {ex.Message}";
            }
            finally
            {
                lock (_analysisLock) { _isAnalyzing = false; }
            }
        }

        // метод для автоматического запуска анализа
        private async Task AutoRunAnalysisAsync()
        {
            // Проверяем, не выполняется ли уже анализ
            if (_isAnalyzing)
            {
                Debug.WriteLine("[AUTO] Analysis already in progress, skipping auto-run");
                return;
            }

            try
            {
                Debug.WriteLine("[AUTO] Auto-running analysis after initialization");
                await PerformAnalysisAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto-run analysis");
                Debug.WriteLine($"[AUTO] Error: {ex.Message}");
            }
        }







        // Метод группировки комбинаций
        private List<MatchGroup> GroupCombinations(List<IndicatorCombination> combinations, decimal tolerancePercent = 0.1m)
        {
            var groups = new List<MatchGroup>();
            var processed = new bool[combinations.Count];

            for (int i = 0; i < combinations.Count; i++)
            {
                if (processed[i]) continue;

                var group = new MatchGroup
                {
                    Type = combinations[i].Type,
                    Combinations = new List<IndicatorCombination> { combinations[i] }
                };

                // Ищем похожие комбинации
                for (int j = i + 1; j < combinations.Count; j++)
                {
                    if (processed[j]) continue;

                    int matchCount, totalCount;
                    if (combinations[i].Matches(combinations[j], tolerancePercent, out matchCount, out totalCount))
                    {
                        // Проверяем, что totalCount > 0
                        if (totalCount > 0 && (double)matchCount / totalCount * 100 >= 80)
                        {
                            group.Combinations.Add(combinations[j]);
                            processed[j] = true;
                        }
                    }
                }

                // Вычисляем типичные значения для группы
                if (combinations[i].IndicatorValues.Any())
                {
                    foreach (var indicator in combinations[i].IndicatorValues.Keys)
                    {
                        var values = group.Combinations
                            .Where(c => c.IndicatorValues.ContainsKey(indicator))
                            .Select(c => c.IndicatorValues[indicator])
                            .ToList();

                        if (values.Any())
                        {
                            group.TypicalValues[indicator] = values.Average();
                        }
                    }
                }

                // Сохраняем группу, если в ней больше 1 комбинации И есть типичные значения
                if (group.Combinations.Count > 1 && group.TypicalValues.Any())
                {
                    group.Signature = group.Combinations.First().GetSignature();
                    groups.Add(group);
                }

                processed[i] = true;
            }

            return groups;
        }

        private bool IsFractal(List<Quote> quotes, int index, int minBars = 10, int maxBars = 15)
        {
            if (index < maxBars || index >= quotes.Count - maxBars)
                return false;

            return IsHighFractal(quotes, index, minBars, maxBars) ||
                   IsLowFractal(quotes, index, minBars, maxBars);
        }

        private bool IsHighFractal(List<Quote> quotes, int index, int minBars = 10, int maxBars = 15)
        {
            for (int j = 1; j <= maxBars; j++)
            {
                if (quotes[index - j].High > quotes[index].High ||
                    quotes[index + j].High > quotes[index].High)
                {
                    return false;
                }
            }

            int leftCount = 0, rightCount = 0;
            for (int j = 1; j <= maxBars; j++)
            {
                if (quotes[index - j].High < quotes[index].High) leftCount++;
                if (quotes[index + j].High < quotes[index].High) rightCount++;
            }

            return leftCount >= minBars && rightCount >= minBars;
        }

        private bool IsLowFractal(List<Quote> quotes, int index, int minBars = 10, int maxBars = 15)
        {
            for (int j = 1; j <= maxBars; j++)
            {
                if (quotes[index - j].Low < quotes[index].Low ||
                    quotes[index + j].Low < quotes[index].Low)
                {
                    return false;
                }
            }

            int leftCount = 0, rightCount = 0;
            for (int j = 1; j <= maxBars; j++)
            {
                if (quotes[index - j].Low > quotes[index].Low) leftCount++;
                if (quotes[index + j].Low > quotes[index].Low) rightCount++;
            }

            return leftCount >= minBars && rightCount >= minBars;
        }




















        private void UpdateProgress(int step, string operation)
        {
            // Проверяем, нужно ли вызывать через Dispatcher
            if (Application.Current.Dispatcher.CheckAccess())
            {
                _analysisProgress.CurrentStep = step;
                _analysisProgress.CurrentOperation = operation;
                OnPropertyChanged(nameof(AnalysisProgressPercentage));
                OnPropertyChanged(nameof(AnalysisCurrentOperation));
                Debug.WriteLine($"[PROGRESS] Step: {step}%, Operation: {operation}");
            }
            else
            {
                // Используем BeginInvoke вместо Invoke чтобы избежать deadlock
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    _analysisProgress.CurrentStep = step;
                    _analysisProgress.CurrentOperation = operation;
                    OnPropertyChanged(nameof(AnalysisProgressPercentage));
                    OnPropertyChanged(nameof(AnalysisCurrentOperation));
                    Debug.WriteLine($"[PROGRESS] Step: {step}%, Operation: {operation}");
                });
            }
        }


        // метод для проверки новых фракталов
        private async Task CheckForNewFractalsAsync()
        {
            if (_isAnalyzing || State != StrategyState.Running) return;

            try
            {
                // Загружаем последние свечи для поиска новых фракталов
                var recentCandles = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(500);
                var quotes = recentCandles.Select(c => new Quote
                {
                    Date = c.Time,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume
                }).ToList();

                var newFractals = FindFractals(quotes, 6, 10);

                // Проверяем, появились ли новые фракталы
                bool hasNewFractals = newFractals.Count > _lastFractalCount;

                // Также проверяем значительное изменение (например, +10% новых фракталов)
                bool significantChange = newFractals.Count > _lastFractalCount * 1.1;

                if (hasNewFractals || significantChange)
                {
                    _logger.LogInformation($"Обнаружены новые фракталы. Было: {_lastFractalCount}, стало: {newFractals.Count}");
                    Debug.WriteLine($"[FRACTAL] New fractals detected: {newFractals.Count} (was {_lastFractalCount})");

                    // Запускаем пересчет анализа
                    await PerformAnalysisAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for new fractals");
            }
        }

        private void OnParametersChanged()
        {
            UpdatePeriodsFromSettings();
            _ = Task.Run(async () =>
            {
                await CalculateIndicators();
                await CalculatePositionSize();



                // Добавьте принудительное обновление сигналов и UI
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    GenerateSignals().Wait(); // Wait - не лучший вариант, но для простоты
                    ForceIndicatorUpdate();
                    OnValuesUpdated?.Invoke();
                });

               
            });
        }

        private void UpdatePeriodsFromSettings()
        {
            try
            {
                // Парсим периоды из настроек
                var allTrendPeriods = _parameters.TrendPeriods
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.Parse(p.Trim()))
                    .OrderBy(p => p)
                    .ToList();

                var allOscillatorPeriods = _parameters.OscillatorPeriods
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.Parse(p.Trim()))
                    .OrderBy(p => p)
                    .ToList();

                var allVolumePeriods = _parameters.VolumePeriods
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.Parse(p.Trim()))
                    .OrderBy(p => p)
                    .ToList();

                if (!allTrendPeriods.Any() || !allOscillatorPeriods.Any() || !allVolumePeriods.Any())
                {
                    _logger.LogWarning("No periods specified, using defaults");
                    allTrendPeriods = new List<int> { 20, 50, 100, 200 };
                    allOscillatorPeriods = new List<int> { 14, 28, 56 };
                    allVolumePeriods = new List<int> { 20, 50 };
                }

                _trendPeriods = allTrendPeriods;
                _oscillatorPeriods = allOscillatorPeriods;
                _volumePeriods = allVolumePeriods;

                // Очищаем и инициализируем словари для всех периодов
                _smaValues.Clear();
                _emaValues.Clear();
                _rsiValues.Clear();

                foreach (var period in allTrendPeriods)
                {
                    _smaValues[period] = 0;
                    _emaValues[period] = 0;
                }

                foreach (var period in allOscillatorPeriods)
                {
                    _rsiValues[period] = 0;
                }

                _logger.LogInformation($"Rating periods loaded - Trend: [{string.Join(",", allTrendPeriods)}], " +
                    $"Oscillator: [{string.Join(",", allOscillatorPeriods)}], " +
                    $"Volume: [{string.Join(",", allVolumePeriods)}]");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing periods, using defaults");

                _trendPeriods = new List<int> { 20, 50, 100, 200 };
                _oscillatorPeriods = new List<int> { 14, 28, 56 };
                _volumePeriods = new List<int> { 20, 50 };

                _smaValues.Clear();
                _emaValues.Clear();
                _rsiValues.Clear();
            }
        }

        public void UpdateCurrentPrice(decimal price)
        {
            _currentPrice = price;
            Application.Current.Dispatcher.Invoke(async () =>
            {
                _indicatorValues.CurrentPrice = price;

                // Обновляем сигналы при каждом изменении цены
                await GenerateSignals();

         

                OnPriceUpdated?.Invoke(price);
            });
        }

        public async Task InitializeAsync(Models.Instrument instrument, string timeframe)
        {
            _instrument = instrument;
            _timeframe = timeframe;
            _accountId = await GetAccountIdAsync();

            // Загружаем исторические данные из БД
            await LoadHistoricalDataAsync();

            // Восстанавливаем позицию из БД при инициализации
            await RestorePositionFromDbAsync();

            // Получаем текущую цену из последней свечи в кэше
            var lastQuote = _quoteCache.LastOrDefault();
            if (lastQuote != null)
            {
                UpdateCurrentPrice(lastQuote.Close);
            }

            _logger.LogInformation($"Rating strategy initialized for {_instrument.Ticker}");

            // Автоматически запускаем анализ после инициализации
            // Используем Task.Run чтобы не блокировать инициализацию
            _ = Task.Run(async () => await AutoRunAnalysisAsync());
        }

        private async Task<string> GetAccountIdAsync()
        {
            try
            {
                var accounts = await _provider.GetAccountsAsync();
                return accounts?.FirstOrDefault()?.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting account ID");
                return null;
            }
        }

        private async Task RestorePositionFromDbAsync()
        {
            try
            {
                var positionsFromDb = await _transactionsService.ReadDBOpenDealsAsync();
                var position = positionsFromDb.FirstOrDefault(p => p.Ticker == _instrument.Ticker);

                if (position != null)
                {
                    _currentPosition = position;
                    _lastKnownPosition = position.Quantity;
                    _logger.LogInformation($"Restored position from DB: {position.Quantity} lots at {position.EntryPrice}");
                }
                else
                {
                    _currentPosition = null;
                    _lastKnownPosition = 0;
                }

                _lastPositionCheck = DateTime.Now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring position from DB");
            }
        }

        private async Task LoadHistoricalDataAsync()
        {
            try
            {
                var candles = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(1500);

                _quoteCache.Clear();
                foreach (var candle in candles.OrderBy(c => c.Time))
                {
                    _quoteCache.Add(new Quote
                    {
                        Date = candle.Time,
                        Open = candle.Open,
                        High = candle.High,
                        Low = candle.Low,
                        Close = candle.Close,
                        Volume = candle.Volume
                    });
                }

                if (_quoteCache.Any())
                {
                    _lastCandleTime = _quoteCache.Last().Date;
                    _logger.LogDebug($"Loaded {_quoteCache.Count} historical candles for {_instrument.Ticker}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading historical data");
            }
        }

        private async Task CalculateIndicators()
        {
            if (_quoteCache.Count < 100)
            {
                _logger.LogDebug($"Not enough candles for calculation: {_quoteCache.Count} < 100");
                return;
            }

            try
            {
                var workingQuotes = new List<Quote>(_quoteCache);

                var lastQuote = workingQuotes.LastOrDefault();
                if (lastQuote != null && lastQuote.Date < DateTime.Now)
                {
                    workingQuotes.Add(new Quote
                    {
                        Date = DateTime.Now,
                        Open = lastQuote.Close,
                        High = Math.Max(lastQuote.High, _currentPrice),
                        Low = Math.Min(lastQuote.Low, _currentPrice),
                        Close = _currentPrice,
                        Volume = lastQuote.Volume + 1
                    });
                }

                // Расчет SMA для трендовых периодов
                foreach (var period in _trendPeriods)
                {
                    try
                    {
                        var sma = workingQuotes.GetSma(period).ToList();
                        if (sma.Any() && sma.Last().Sma.HasValue)
                        {
                            _smaValues[period] = (decimal)sma.Last().Sma.Value;
                        }

                        var ema = workingQuotes.GetEma(period).ToList();
                        if (ema.Any() && ema.Last().Ema.HasValue)
                        {
                            _emaValues[period] = (decimal)ema.Last().Ema.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Error calculating trend indicator {period}: {ex.Message}");
                    }
                }

                // Расчет RSI для осцилляторных периодов
                foreach (var period in _oscillatorPeriods)
                {
                    try
                    {
                        var rsi = workingQuotes.GetRsi(period).ToList();
                        if (rsi.Any() && rsi.Last().Rsi.HasValue)
                        {
                            _rsiValues[period] = (decimal)rsi.Last().Rsi.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Error calculating RSI {period}: {ex.Message}");
                    }
                }

                // Расчет ATR
                var atr = workingQuotes.GetAtr(_atrPeriod).ToList();
                if (atr.Any() && atr.Last().Atr.HasValue)
                {
                    _atrValue = (decimal)atr.Last().Atr.Value;
                }

                // Сохраняем предыдущие значения EMA
                var signalPeriods = _trendPeriods.OrderBy(p => p).ToList();
                if (signalPeriods.Count >= 2)
                {
                    var shortEma = signalPeriods[0];
                    var mediumEma = signalPeriods[1];

                    if (_emaValues.ContainsKey(shortEma))
                    {
                        _previousEmaShort = _emaValues[shortEma];
                    }
                    if (_emaValues.ContainsKey(mediumEma))
                    {
                        _previousEmaMedium = _emaValues[mediumEma];
                    }
                }

                AnalyzeTrend();
                await GenerateSignals();
                await UpdateIndicatorValues();
                // Добавьте принудительное обновление здесь
                ForceIndicatorUpdate();

                _lastCalculation = DateTime.Now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating indicators");
            }
        }

        private void AnalyzeTrend()
        {
            if (_trendPeriods.Count < 3)
            {
                _isBullishTrend = false;
                _isBearishTrend = false;
                _trendStrength = "Недостаточно данных";
                return;
            }

            var sortedPeriods = _trendPeriods.OrderBy(p => p).ToList();
            var shortTerm = sortedPeriods[0];
            var mediumTerm = sortedPeriods[1];
            var longTerm = sortedPeriods[2];

            if (!_smaValues.ContainsKey(shortTerm) ||
                !_smaValues.ContainsKey(mediumTerm) ||
                !_smaValues.ContainsKey(longTerm))
                return;

            var smaShort = _smaValues[shortTerm];
            var smaMedium = _smaValues[mediumTerm];
            var smaLong = _smaValues[longTerm];

            _isBullishTrend = smaShort > smaMedium && smaMedium > smaLong;
            _isBearishTrend = smaShort < smaMedium && smaMedium < smaLong;

            if (_isBullishTrend || _isBearishTrend)
            {
                decimal slopeMedium = Math.Abs((smaMedium - smaShort) / smaShort * 100);
                decimal slopeLong = Math.Abs((smaLong - smaMedium) / smaMedium * 100);

                if (slopeMedium > 1.0m && slopeLong > 0.5m)
                    _trendStrength = "Сильный";
                else if (slopeMedium > 0.5m)
                    _trendStrength = "Средний";
                else
                    _trendStrength = "Слабый";
            }
            else
            {
                _trendStrength = "Нейтральный";
            }

            _indicatorValues.IsBullishTrend = _isBullishTrend;
            _indicatorValues.IsBearishTrend = _isBearishTrend;
        }

        private async Task GenerateSignals()
        {
            if (_quoteCache.Count == 0) return;

            // Проверяем, является ли последняя свеча фракталом
            int lastIndex = _quoteCache.Count - 1;
            if (IsFractal(_quoteCache, lastIndex))
            {
                var fractalType = IsLowFractal(_quoteCache, lastIndex) ? FractalType.Low : FractalType.High;

                // Рассчитываем значения индикаторов на текущем фрактале
                var currentValues = CalculateAllIndicatorsAtPoint(_quoteCache, lastIndex);
                var currentCombination = new IndicatorCombination
                {
                    FractalTime = _quoteCache[lastIndex].Date,
                    Type = fractalType,
                    FractalPrice = fractalType == FractalType.Low ? _quoteCache[lastIndex].Low : _quoteCache[lastIndex].High,
                    IndicatorValues = currentValues
                };

                // Ищем совпадения с историческими группами
                var matches = FindMatchingGroups(currentCombination);

                // Обновляем рейтинги
                if (fractalType == FractalType.Low)
                {
                    _buyRating = matches.Count;
                    _indicatorValues.BuyRating = _buyRating;
                }
                else
                {
                    _sellRating = matches.Count;
                    _indicatorValues.SellRating = _sellRating;
                }

                // Генерируем сигнал
                GenerateTradeSignal(matches, fractalType, currentCombination);
            }
        }

        private List<MatchGroup> FindMatchingGroups(IndicatorCombination combination)
        {
            var matches = new List<MatchGroup>();
            var groups = combination.Type == FractalType.Low ? _buyGroups : _sellGroups;

            foreach (var group in groups)
            {
                double matchPercentage = group.MatchPercentage(combination, _parameters.MatchTolerance);
                if (matchPercentage >= _parameters.MinMatchPercentage)
                {
                    matches.Add(group);
                }
            }

            return matches.OrderByDescending(g => g.MatchPercentage(combination, _parameters.MatchTolerance)).ToList();
        }

        private void GenerateTradeSignal(List<MatchGroup> matches, FractalType type, IndicatorCombination current)
        {
            if (!matches.Any())
            {
                _currentSignal = "⏸️ ОЖИДАНИЕ (нет совпадений)";
                _indicatorValues.SignalColor = Brushes.Gray;
                return;
            }

            var bestMatch = matches.First();
            double matchPercent = bestMatch.MatchPercentage(current, _parameters.MatchTolerance);

            if (type == FractalType.Low)
            {
                if (matchPercent >= _parameters.EntryThreshold)
                {
                    _currentSignal = $"📈 LONG (совпадение {matchPercent:F1}%)";
                    _indicatorValues.SignalColor = Brushes.Green;
                    CalculateEntryPrices("Long");
                }
                else
                {
                    _currentSignal = $"⏸️ ОЖИДАНИЕ (совпадение {matchPercent:F1}% < {_parameters.EntryThreshold}%)";
                    _indicatorValues.SignalColor = Brushes.Orange;
                }
            }
            else
            {
                if (matchPercent >= _parameters.EntryThreshold)
                {
                    _currentSignal = $"📉 SHORT (совпадение {matchPercent:F1}%)";
                    _indicatorValues.SignalColor = Brushes.Red;
                    CalculateEntryPrices("Short");
                }
                else
                {
                    _currentSignal = $"⏸️ ОЖИДАНИЕ (совпадение {matchPercent:F1}% < {_parameters.EntryThreshold}%)";
                    _indicatorValues.SignalColor = Brushes.Orange;
                }
            }

            // Исправленная часть с безопасной обработкой строки
            string signature = bestMatch.Combinations.First().GetSignature();
            string shortSignature = string.IsNullOrEmpty(signature)
                ? ""
                : signature.Length > 20
                    ? signature.Substring(0, 20) + "..."
                    : signature;

            _indicatorValues.CurrentSignal = _currentSignal;
            _indicatorValues.SignalDescription =
                $"Лучшая группа: #{shortSignature} ({bestMatch.Count} совпадений)";
        }




















        // метод для принудительного обновления UI после анализа
        private void RefreshUI()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {

                // Принудительно обновляем все свойства статистики
                if (_buyMatchStats != null)
                {
                    _buyMatchStats.ForceUpdate();
                    OnPropertyChanged(nameof(BuyMatchStats));
                }

                if (_sellMatchStats != null)
                {
                    _sellMatchStats.ForceUpdate();
                    OnPropertyChanged(nameof(SellMatchStats));
                }

                if (_fractalStats != null)
                {
                    OnPropertyChanged(nameof(FractalStats));
                }

                Debug.WriteLine($"[UI] Statistics updated - Buy: Exact={_buyMatchStats?.ExactMatches}, Low={_buyMatchStats?.LowDeviationMatches}, Medium={_buyMatchStats?.MediumDeviationMatches}");
                Debug.WriteLine($"[UI] Statistics updated - Sell: Exact={_sellMatchStats?.ExactMatches}, Low={_sellMatchStats?.LowDeviationMatches}, Medium={_sellMatchStats?.MediumDeviationMatches}");

                // Обновляем порог входа в ViewModel
                if (_indicatorValues != null)
                {
                    _indicatorValues.EntryThreshold = _parameters.EntryThreshold;
                    OnPropertyChanged(nameof(_indicatorValues.EntryThreshold));
                }

            });




            // Обновляем все свойства
            OnPropertyChanged(nameof(FractalStats));
                OnPropertyChanged(nameof(BuyMatchStats));
                OnPropertyChanged(nameof(SellMatchStats));
                OnPropertyChanged(nameof(TopBuyMatches));
                OnPropertyChanged(nameof(TopSellMatches));
                OnPropertyChanged(nameof(AnalysisStatus));
                OnPropertyChanged(nameof(LastAnalysisDate));
                OnPropertyChanged(nameof(IsAnalysisProgressVisible));
                OnPropertyChanged(nameof(AnalysisProgressPercentage));
                OnPropertyChanged(nameof(AnalysisCurrentOperation));

                // Обновляем ViewModel
                if (_indicatorValues != null)
                {
                    _indicatorValues.BuyRating = _buyRating;
                    _indicatorValues.SellRating = _sellRating;
                    _indicatorValues.MaxRating = _maxRating;
                    _indicatorValues.AnalysisStatus = _analysisStatus;
                    _indicatorValues.LastAnalysisDate = _lastAnalysisDate;
                    _indicatorValues.EntryThreshold = _parameters.EntryThreshold; // Добавлено

                // Принудительное обновление всех привязок
                OnPropertyChanged(nameof(_indicatorValues));
                }
            
        }


        private void CalculateEntryPrices(string direction)
        {
            _entryPrice = _currentPrice;

            // Защита от нулевого ATR
            decimal atrMultiplier = _atrValue > 0 ? _atrValue : _currentPrice * 0.01m; // Если ATR=0, используем 1% от цены


            if (direction == "Long")
            {
                _stopLoss = _entryPrice - _atrValue * 2.0m;
                _takeProfit = _entryPrice + _atrValue * 4.0m;
            }
            else
            {
                _stopLoss = _entryPrice + _atrValue * 2.0m;
                _takeProfit = _entryPrice - _atrValue * 4.0m;
            }

            _indicatorValues.EntryPrice = _entryPrice;
            _indicatorValues.StopLossPrice = _stopLoss;
            _indicatorValues.TakeProfitPrice = _takeProfit;
        }

        public bool ShouldExit(string direction)
        {
            if (_currentPosition == null || _currentPosition.Quantity == 0)
                return false;

            // Проверяем кулдаун выхода
            if (_isExiting)
                return false;

            if ((DateTime.Now - _lastExitAttempt).TotalSeconds < EXIT_COOLDOWN_SECONDS)
                return false;

            // Не выходим сразу после входа
            if (_currentPosition.EntryDateTime != null)
            {
                TimeSpan timeInPosition = (TimeSpan)(DateTime.Now - _currentPosition.EntryDateTime);
                if (timeInPosition.TotalSeconds < 30) // Минимум 30 секунд
                    return false;
            }

            // Рассчитываем текущие рейтинги
            int currentBuyRating = _buyRating;
            int currentSellRating = _sellRating;

            bool shouldExit = false;
            string exitReason = "";

            // Расчет процентного изменения
            decimal priceChangePercent = 0;
            if (direction == "Long")
            {
                priceChangePercent = (_currentPrice - _currentPosition.EntryPrice) / _currentPosition.EntryPrice * 100;
            }
            else
            {
                priceChangePercent = (_currentPosition.EntryPrice - _currentPrice) / _currentPosition.EntryPrice * 100;
            }

            if (direction == "Long")
            {
                // Выход при сильном падении рейтинга покупки
                if (currentBuyRating < _maxRating * 30 / 100) // Меньше 30% от максимума
                {
                    shouldExit = true;
                    exitReason = $"Падение рейтинга покупки ({currentBuyRating}/{_maxRating})";
                }
                // Выход при превышении рейтинга продажи
                else if (currentSellRating > currentBuyRating && currentSellRating > _maxRating * 50 / 100)
                {
                    shouldExit = true;
                    exitReason = $"Рейтинг продажи выше ({currentSellRating} > {currentBuyRating})";
                }
                // Трейлинг-стоп (только если есть прибыль)
                else if (priceChangePercent > 1.0m)
                {
                    decimal trailingStop = GetTrailingStop("Long");
                    if (_currentPrice < trailingStop)
                    {
                        shouldExit = true;
                        exitReason = $"Трейлинг-стоп (прибыль {priceChangePercent:F2}%)";
                    }
                }
                // Стоп-лосс по ATR
                else if (_currentPrice < _currentPosition.EntryPrice - _atrValue * 2.0m)
                {
                    shouldExit = true;
                    exitReason = $"Стоп-лосс по ATR (убыток {Math.Abs(priceChangePercent):F2}%)";
                }
            }
            else // Short
            {
                // Выход при сильном падении рейтинга продажи
                if (currentSellRating < _maxRating * 30 / 100)
                {
                    shouldExit = true;
                    exitReason = $"Падение рейтинга продажи ({currentSellRating}/{_maxRating})";
                }
                // Выход при превышении рейтинга покупки
                else if (currentBuyRating > currentSellRating && currentBuyRating > _maxRating * 50 / 100)
                {
                    shouldExit = true;
                    exitReason = $"Рейтинг покупки выше ({currentBuyRating} > {currentSellRating})";
                }
                // Трейлинг-стоп (только если есть прибыль)
                else if (priceChangePercent > 1.0m)
                {
                    decimal trailingStop = GetTrailingStop("Short");
                    if (_currentPrice > trailingStop)
                    {
                        shouldExit = true;
                        exitReason = $"Трейлинг-стоп (прибыль {priceChangePercent:F2}%)";
                    }
                }
                // Стоп-лосс по ATR
                else if (_currentPrice > _currentPosition.EntryPrice + _atrValue * 2.0m)
                {
                    shouldExit = true;
                    exitReason = $"Стоп-лосс по ATR (убыток {Math.Abs(priceChangePercent):F2}%)";
                }
            }

            if (shouldExit)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] СИГНАЛ НА ВЫХОД для {direction}: {exitReason}");
            }

            return shouldExit;
        }

        private decimal GetTrailingStop(string direction)
        {
            if (_currentPosition == null) return 0;

            if (direction == "Long")
            {
                decimal highestPrice = Math.Max(_currentPrice, _currentPosition.EntryPrice);
                return highestPrice - _atrValue * 2.0m;
            }
            else
            {
                decimal lowestPrice = Math.Min(_currentPrice, _currentPosition.EntryPrice);
                return lowestPrice + _atrValue * 2.0m;
            }
        }

        private async Task UpdateIndicatorValues()
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ForceIndicatorUpdate();
            });
        }

        private async Task CalculatePositionSize()
        {
            try
            {
                if (_instrument == null || _currentPrice <= 0) return;

                decimal availableAmount = 0;

                if (_parameters.PositionSizeType == "Percent")
                {
                    var balance = await _provider.GetAccountBalanceAsync();
                    availableAmount = balance * (_parameters.PositionSizePercent / 100);
                    _indicatorValues.PositionSizeValue = _parameters.PositionSizePercent;
                }
                else
                {
                    availableAmount = _parameters.PositionSizeAbsolute;
                    _indicatorValues.PositionSizeValue = _parameters.PositionSizeAbsolute;
                }

                if (availableAmount > 0 && _instrument.LotSize > 0)
                {
                    decimal lots = Math.Floor(availableAmount / (_currentPrice * _instrument.LotSize));
                    _indicatorValues.PositionSizeLots = lots;
                }
                else
                {
                    _indicatorValues.PositionSizeLots = 0;
                }

                var accountBalance = await _provider.GetAccountBalanceAsync();
                _indicatorValues.AccountBalance = accountBalance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating position size");
            }
        }

        public async Task StartAsync()
        {
            State = StrategyState.Running;
            _logger.LogInformation($"Rating strategy started for {_instrument.Ticker}");

            await CalculatePositionSize();
            await CalculateIndicators();

            _indicatorValues.StrategyStatus = "РАБОТАЕТ";
            _indicatorValues.StrategyStatusColor = Brushes.Green;

            // Принудительно обновляем UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(BuyMatchStats));
                OnPropertyChanged(nameof(SellMatchStats));
                OnPropertyChanged(nameof(FractalStats));
            });

            // Принудительно обновляем UI
            ForceIndicatorUpdate();
            RefreshUI();

            // Запускаем анализ при старте стратегии, если он еще не выполнялся
            if (_lastAnalysisDate == DateTime.MinValue && !_isAnalyzing)
            {
                _ = Task.Run(async () => await AutoRunAnalysisAsync());
            }
        }

        public async Task StopAsync()
        {
            State = StrategyState.Stopped;
            _logger.LogInformation($"Rating strategy stopped for {_instrument.Ticker}");

            _indicatorValues.StrategyStatus = "ОСТАНОВЛЕНА";
            _indicatorValues.StrategyStatusColor = Brushes.Red;
            _indicatorValues.CurrentSignal = "СТОП";
        }

        public async Task ProcessMarketData(MarketData marketData)
        {
            if (State != StrategyState.Running || marketData == null)
                return;

            try
            {
                _currentPrice = marketData.LastPrice;
                UpdateCurrentPrice(_currentPrice);

                await AddQuoteToCache(marketData);

                if ((DateTime.Now - _lastCalculation).TotalMilliseconds > 333)
                {
                    await CalculateIndicators();
                }

                // Проверяем наличие новых фракталов каждые FRACTAL_CHECK_INTERVAL_MINUTES
                if ((DateTime.Now - _lastFractalCheck).TotalMinutes > FRACTAL_CHECK_INTERVAL_MINUTES)
                {
                    _lastFractalCheck = DateTime.Now;
                    _ = Task.Run(async () => await CheckForNewFractalsAsync());
                }



                if (_currentPosition == null && !_checkPos)
                {
                    if ((DateTime.Now - _lastPositionCheck).TotalMilliseconds > 1000)
                    {
                        var pos = await _provider.GetPositionAsync(_accountId, _instrument.Uid);

                        if (pos != 0)
                        {
                            _currentPosition = new Position()
                            {
                                Quantity = (int)pos
                            };

                            _lastPositionCheck = DateTime.Now;
                            await RestorePositionFromDbAsync();
                        }
                    }

                    _checkPos = true;
                }

                // Если нет позиции - проверяем вход
                if ((_currentPosition == null || _currentPosition.Quantity == 0) && !_isEntering)
                {
                    if (_currentSignal.Contains("LONG"))
                    {
                        await ExecuteEntryAsync("Long", "Сигнал на вход LONG");
                    }
                    else if (_currentSignal.Contains("SHORT"))
                    {
                        await ExecuteEntryAsync("Short", "Сигнал на вход SHORT");
                    }
                }
                // Если есть позиция - проверяем выход
                else if (_currentPosition != null && _currentPosition.Quantity != 0 && !_isExiting)
                {
                    _checkPos = false;
                    string direction = _currentPosition.Quantity > 0 ? "Long" : "Short";

                    if (ShouldExit(direction))
                    {
                        await ExecuteExitAsync(direction, "Сигнал на выход");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing market data for {_instrument?.Ticker}");
            }
        }

        private async Task AddQuoteToCache(MarketData marketData)
        {
            var now = DateTime.Now;
            var timeframeMinutes = GetTimeframeMinutes();

            var candleStartTime = new DateTime(
                now.Year, now.Month, now.Day,
                now.Hour, (now.Minute / timeframeMinutes) * timeframeMinutes, 0);

            var lastQuote = _quoteCache.LastOrDefault();

            if (lastQuote == null || lastQuote.Date != candleStartTime)
            {
                var newQuote = new Quote
                {
                    Date = candleStartTime,
                    Open = marketData.LastPrice,
                    High = marketData.LastPrice,
                    Low = marketData.LastPrice,
                    Close = marketData.LastPrice,
                    Volume = 1
                };
                _quoteCache.Add(newQuote);
            }
            else
            {
                lastQuote.High = Math.Max(lastQuote.High, marketData.LastPrice);
                lastQuote.Low = Math.Min(lastQuote.Low, marketData.LastPrice);
                lastQuote.Close = marketData.LastPrice;
                lastQuote.Volume++;
            }

            while (_quoteCache.Count > _maxCacheSize)
            {
                _quoteCache.RemoveAt(0);
            }
        }

        private int GetTimeframeMinutes()
        {
            return _timeframe?.ToLower() switch
            {
                "1min" => 1,
                "5min" => 5,
                "15min" => 15,
                "30min" => 30,
                "1hour" => 60,
                "2hour" => 120,
                "4hour" => 240,
                "1day" => 1440,
                _ => 1
            };
        }

        private async Task ExecuteEntryAsync(string direction, string reason)
        {
            lock (_entryLock)
            {
                if (_isEntering)
                {
                    _logger.LogWarning($"Entry already in progress, skipping {direction}");
                    return;
                }
                _isEntering = true;
            }

            try
            {
                _lastEntryAttempt = DateTime.Now;

                var currentPosition = await _provider.GetPositionAsync(_accountId, _instrument.Uid);
                if (currentPosition != 0)
                {
                    _logger.LogInformation($"Cannot enter {direction} - position already exists: {currentPosition}");
                    return;
                }

                await CalculatePositionSize();
                int quantity = (int)_indicatorValues.PositionSizeLots;

                if (quantity <= 0)
                {
                    _logger.LogWarning($"Cannot enter {direction} - invalid quantity: {quantity}");
                    return;
                }

                var entryOrder = new Models.Order
                {
                    InstrumentUid = _instrument.Uid,
                    Direction = direction == "Long" ? "Buy" : "Sell",
                    OrderType = "Market",
                    Quantity = quantity,
                    Price = _currentPrice,
                    Status = "Pending",
                    IsEntryOrder = true,
                    IsExitOrder = false,
                    EntryReason = reason,
                    Ticker = _instrument.Ticker,
                    Time = DateTime.Now
                };

                _logger.LogInformation($"Placing {direction} entry order: {quantity} lots at {_currentPrice:F2}");
                var result = await _provider.PlaceOrderAsync(entryOrder);

                await Task.Delay(1000);

                if (result.IsSuccess)
                {
                    _currentPosition = new Position()
                    {
                        Ticker = _instrument.Ticker,
                        InstrumentUid = _instrument.Uid,
                        Direction = entryOrder.Direction,
                        Quantity = entryOrder.Quantity,
                        EntryPrice = entryOrder.Price,
                        EntryOrderId = entryOrder.OrderId,
                        EntryDateTime = entryOrder.Time,
                        EntryReason = entryOrder.EntryReason,
                        Status = DealStatus.Open,
                    };

                    await _transactionsService.AddOpenDealAsync(
                        entryOrder.Ticker,
                        entryOrder.InstrumentUid,
                        this.Type,
                        _strategyViewModel.CurrentTimeframe,
                        entryOrder.Time,
                        entryOrder.Price,
                        entryOrder.Quantity,
                        result.OrderId,
                        entryOrder.Direction,
                        entryOrder.EntryReason);

                    _logger.LogInformation($"Entry order placed successfully: {direction} {quantity} lots");
                    _indicatorValues.SignalDescription = $"Вход в {direction}: {quantity} лотов по {_currentPrice:F2}";

                    await Task.Delay(1000);

                    var pos = await _provider.GetPositionAsync(_accountId, _instrument.Uid);
                    _lastPositionCheck = DateTime.Now;

                    if (pos != 0)
                    {
                        _currentPosition.Quantity = (int)pos;
                    }

                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] DEBUG: {_instrument.Ticker} Force position check after entry: hasPosition={_currentPosition.Quantity}, quantity={pos}");
                }
                else
                {
                    _logger.LogError($"Failed to place entry order: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing entry order");
            }
            finally
            {
                lock (_entryLock)
                {
                    _isEntering = false;
                }
            }
        }

        private async Task ExecuteExitAsync(string direction, string reason)
        {
            lock (_exitLock)
            {
                if (_isExiting)
                {
                    _logger.LogWarning($"Exit already in progress, skipping {direction}");
                    return;
                }
                _isExiting = true;
                _lastExitAttempt = DateTime.Now;
            }

            try
            {
                var currentQty = (int)Math.Abs(await _provider.GetPositionAsync(_accountId, _instrument.Uid));

                if (currentQty == 0)
                {
                    _logger.LogWarning($"Cannot exit - no position found");
                    return;
                }

                await RestorePositionFromDbAsync();

                if (_currentPosition == null || string.IsNullOrEmpty(_currentPosition.EntryOrderId))
                {
                    _logger.LogError("Cannot exit - position not found in DB");
                    return;
                }

                var exitOrder = new Models.Order
                {
                    InstrumentUid = _instrument.Uid,
                    Direction = direction == "Long" ? "Sell" : "Buy",
                    OrderType = "Market",
                    Quantity = currentQty,
                    Price = _currentPrice,
                    Status = "Pending",
                    IsEntryOrder = false,
                    IsExitOrder = true,
                    ExitReason = reason,
                    Ticker = _instrument.Ticker,
                    Time = DateTime.Now
                };

                _logger.LogInformation($"Placing {direction} exit order: {currentQty} lots at {_currentPrice:F2}");

                var result = await _provider.PlaceOrderAsync(exitOrder);
                _logger.LogInformation($"Exit order placed: {reason}");

                await Task.Delay(1000);

                if (result.IsSuccess)
                {
                    _logger.LogInformation($"Exit order placed successfully: {result.OrderId}");

                    for (int i = 0; i < 10; i++)
                    {
                        await Task.Delay(500);

                        var pos = await _provider.GetPositionAsync(_accountId, _instrument.Uid);
                        if (pos == 0)
                        {
                            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Position closed successfully");
                            break;
                        }
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for position close... attempt {i + 1}/10");
                    }

                    decimal pnl = 0;
                    decimal pnlPercent = 0;
                    decimal priceDiff = 0;
                    if (_currentPosition.Direction == PositionDirection.Long || _currentPosition.Direction == "Long" || _currentPosition.Direction == "Buy")
                    {
                        // Для LONG: P&L = (ExitPrice - EntryPrice) * Quantity * LotSize
                        priceDiff = exitOrder.Price - _currentPosition.EntryPrice;
                        pnl = priceDiff * _currentPosition.Quantity * _instrument.LotSize;
                        pnlPercent = _currentPosition.EntryPrice > 0
                            ? priceDiff / _currentPosition.EntryPrice * 100
                            : 0;
                    }
                    else if (_currentPosition.Direction == PositionDirection.Short || _currentPosition.Direction == "Short" || _currentPosition.Direction == "Sell")
                    {
                        // Для SHORT: P&L = (EntryPrice - ExitPrice) * Quantity * LotSize
                        priceDiff = _currentPosition.EntryPrice - exitOrder.Price;
                        pnl = priceDiff * _currentPosition.Quantity * _instrument.LotSize;
                        pnlPercent = _currentPosition.EntryPrice > 0
                            ? priceDiff / _currentPosition.EntryPrice * 100
                            : 0;
                    }

                    bool dealClosed = await _transactionsService.CloseDealAsync(
                        exitOrder.InstrumentUid,
                        _currentPosition.EntryOrderId,
                        exitOrder.Time,
                        exitOrder.Price,
                        result.OrderId,
                        pnl,
                        pnlPercent,
                        reason
                    );

                    _logger.LogInformation($"Deal closed successfully: P&L={pnl:F2} ({pnlPercent:F2}%)");
                    _currentPosition = null;

                    ResetExitVariables();
                }
                else
                {
                    _logger.LogError($"Failed to place exit order: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing exit order");
            }
            finally
            {
                lock (_exitLock)
                {
                    _isExiting = false;
                }
            }
        }

        public void ResetExitVariables()
        {
            _currentPosition = null;
            _lastKnownPosition = 0;
            _lastPositionCheck = DateTime.Now;
            _logger.LogDebug("Exit variables reset");

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] DEBUG: ResetExitVariables - ОБНУЛИЛИ ПЕРЕМЕННЫЕ ПОЗИЦИИ в стратегии");
        }

        #region UI Methods

        public object GetSettingsView()
        {
            var mainGrid = new Grid();
            mainGrid.Margin = new Thickness(10);

            // Определяем строки
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Заголовок
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Описание
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Параметры
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Разделитель
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Размер позиции
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Кнопки
                                                                                          


            // ЗАГОЛОВОК
            var titlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 15)
            };

            titlePanel.Children.Add(new TextBlock
            {
                Text = "⚡",
                FontSize = 24,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            titlePanel.Children.Add(new TextBlock
            {
                Text = "Рейтинговая стратегия",
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetRow(titlePanel, 0);
            mainGrid.Children.Add(titlePanel);

            // ОПИСАНИЕ СТРАТЕГИИ
            var descriptionBox = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(245, 245, 250)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 20)
            };

            var descriptionText = new TextBlock
            {
                Text = "Стратегия анализирует исторические данные, находит фракталы (разворотные точки) " +
                       "и вычисляет значения всех доступных индикаторов в этих точках. На основе частоты " +
                       "совпадений формируется рейтинг для покупок (нижние фракталы) и продаж (верхние фракталы).",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Brushes.DimGray
            };

            descriptionBox.Child = descriptionText;
            Grid.SetRow(descriptionBox, 1);
            mainGrid.Children.Add(descriptionBox);

            // ПАНЕЛЬ ПАРАМЕТРОВ
            var parametersGroup = new GroupBox
            {
                Header = new TextBlock
                {
                    Text = "📊 Параметры анализа",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(5)
                },
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(15)
            };

            var parametersGrid = new Grid();
            parametersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            parametersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            parametersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            parametersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            parametersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            parametersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            parametersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            parametersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // для MatchTolerance
            parametersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // для MinMatchPercentage


            // Трендовые периоды
            AddParameterRow(parametersGrid, 0, "📈 Трендовые периоды:",
                "Периоды для SMA и EMA (через запятую)\nПример: 20,50,100,200",
                _parameters, nameof(RatingSettingsViewModel.TrendPeriods));

            // Осцилляторные периоды
            AddParameterRow(parametersGrid, 1, "📉 Осцилляторные периоды:",
                "Периоды для RSI, CCI и др. (через запятую)\nПример: 14,28,56",
                _parameters, nameof(RatingSettingsViewModel.OscillatorPeriods));

            // Объемные периоды
            AddParameterRow(parametersGrid, 2, "📊 Объемные периоды:",
                "Периоды для объемных индикаторов (через запятую)\nПример: 20,50",
                _parameters, nameof(RatingSettingsViewModel.VolumePeriods));

            // Вместо "Максимальный рейтинг" используйте "Порог входа"
            AddParameterRow(parametersGrid, 3, "🎯 Порог входа (0-100):",
                 "Минимальная сумма баллов для входа в позицию\nРекомендуется: 70",
                 _parameters, nameof(RatingSettingsViewModel.EntryThreshold));

            // Допуск совпадения
            AddParameterRow(parametersGrid, 4, "📐 Допуск совпадения (%):",
                "Максимальное отклонение значения индикатора в процентах\nРекомендуется: 0.1",
                _parameters, nameof(RatingSettingsViewModel.MatchTolerance));

            // Минимальный процент совпадений
            AddParameterRow(parametersGrid, 5, "📊 Мин. % совпадений:",
                "Минимальный процент совпавших индикаторов для группы\nРекомендуется: 80",
                _parameters, nameof(RatingSettingsViewModel.MinMatchPercentage));




            parametersGroup.Content = parametersGrid;
            Grid.SetRow(parametersGroup, 2);
            mainGrid.Children.Add(parametersGroup);

            // РАЗДЕЛИТЕЛЬ
            var separator = new Rectangle
            {
                Height = 1,
                Fill = Brushes.LightGray,
                Margin = new Thickness(0, 5, 0, 15)
            };
            Grid.SetRow(separator, 3);
            mainGrid.Children.Add(separator);

            // ПАНЕЛЬ РАЗМЕРА ПОЗИЦИИ
            var positionGroup = new GroupBox
            {
                Header = new TextBlock
                {
                    Text = "💰 Размер позиции",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(5)
                },
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(15)
            };

            var positionGrid = new Grid();
            positionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            positionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            positionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            positionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            positionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Тип расчета
            var typeLabel = new TextBlock
            {
                Text = "📋 Тип расчета:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(typeLabel, 0);
            Grid.SetColumn(typeLabel, 0);
            positionGrid.Children.Add(typeLabel);

            var typePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var percentRadio = new RadioButton
            {
                Content = "Процент от депозита",
                IsChecked = _parameters.PositionSizeType == "Percent",
                Margin = new Thickness(0, 0, 20, 0),
                VerticalAlignment = VerticalAlignment.Center,
                GroupName = "PositionSizeType"
            };
            percentRadio.Checked += (s, e) => _parameters.PositionSizeType = "Percent";
            typePanel.Children.Add(percentRadio);

            var absoluteRadio = new RadioButton
            {
                Content = "Абсолютное значение (₽)",
                IsChecked = _parameters.PositionSizeType == "Absolute",
                VerticalAlignment = VerticalAlignment.Center,
                GroupName = "PositionSizeType"
            };
            absoluteRadio.Checked += (s, e) => _parameters.PositionSizeType = "Absolute";
            typePanel.Children.Add(absoluteRadio);

            Grid.SetRow(typePanel, 0);
            Grid.SetColumn(typePanel, 1);
            positionGrid.Children.Add(typePanel);

            // Процент
            AddParameterRow(positionGrid, 1, "📊 Процент от депозита:",
                "Процент от доступного баланса",
                _parameters, nameof(RatingSettingsViewModel.PositionSizePercent));

            // ИСПРАВЛЕНИЕ: Явно приводим к FrameworkElement для SetBinding
            var percentPanel = (FrameworkElement)positionGrid.Children[positionGrid.Children.Count - 1];
            var percentBinding = new Binding("PositionSizeType")
            {
                Source = _parameters,
                Converter = new PositionSizeTypeToVisibilityConverter(),
                ConverterParameter = "Percent"
            };
            percentPanel.SetBinding(UIElement.VisibilityProperty, percentBinding);

            // Абсолютное значение
            AddParameterRow(positionGrid, 2, "💰 Фиксированная сумма (₽):",
                "Сумма в рублях для входа",
                _parameters, nameof(RatingSettingsViewModel.PositionSizeAbsolute));

            // ИСПРАВЛЕНИЕ: Явно приводим к FrameworkElement для SetBinding
            var absolutePanel = (FrameworkElement)positionGrid.Children[positionGrid.Children.Count - 1];
            var absoluteBinding = new Binding("PositionSizeType")
            {
                Source = _parameters,
                Converter = new PositionSizeTypeToVisibilityConverter(),
                ConverterParameter = "Absolute"
            };
            absolutePanel.SetBinding(UIElement.VisibilityProperty, absoluteBinding);

            positionGroup.Content = positionGrid;
            Grid.SetRow(positionGroup, 4);
            mainGrid.Children.Add(positionGroup);

            // ПАНЕЛЬ КНОПОК
            var buttonsPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };

            // Кнопка анализа
            var analyzeButton = CreateStyledButton("🔍 Запустить анализ", Color.FromRgb(255, 152, 0), 150);
            analyzeButton.Click += async (s, e) => await PerformAnalysisAsync();
            buttonsPanel.Children.Add(analyzeButton);

            // Кнопка применения
            var applyButton = CreateStyledButton("💾 Применить", Color.FromRgb(33, 150, 243), 120);
            applyButton.Click += (s, e) => _parameters.ApplyParameters();
            buttonsPanel.Children.Add(applyButton);

            // Кнопка сброса
            var resetButton = CreateStyledButton("↺ Сброс", Color.FromRgb(244, 67, 54), 100);
            resetButton.Click += (s, e) => _parameters.ResetParameters();
            buttonsPanel.Children.Add(resetButton);

            Grid.SetRow(buttonsPanel, 5);
            mainGrid.Children.Add(buttonsPanel);

            return mainGrid;
        }

        // Вспомогательный метод для создания стилизованной кнопки
        private Button CreateStyledButton(string text, Color color, int width)
        {
            return new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontWeight = FontWeights.SemiBold
                },
                Width = width,
                Height = 35,
                Margin = new Thickness(5),
                Background = new SolidColorBrush(color),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = text
            };
        }

        // Вспомогательный метод для добавления строки параметра (возвращает Panel для возможности установки Visibility)
        private FrameworkElement AddParameterRow(Grid grid, int row, string label, string toolTip, object source, string propertyName)
        {
            var labelControl = new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = toolTip
            };
            Grid.SetRow(labelControl, row);
            Grid.SetColumn(labelControl, 0);
            grid.Children.Add(labelControl);

            var textBox = new TextBox
            {
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(8, 5, 8, 5),
                ToolTip = toolTip,
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 255))
            };

            var binding = new Binding(propertyName)
            {
                Source = source,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
            textBox.SetBinding(TextBox.TextProperty, binding);

            Grid.SetRow(textBox, row);
            Grid.SetColumn(textBox, 1);
            grid.Children.Add(textBox);

            // Возвращаем TextBox как FrameworkElement для возможности установки Visibility
            return textBox;
        }

        private void AddButtonHoverEffect(Button button, Color hoverColor, Color normalColor)
        {
            button.MouseEnter += (s, e) =>
            {
                button.Background = new SolidColorBrush(hoverColor);
            };
            button.MouseLeave += (s, e) =>
            {
                button.Background = new SolidColorBrush(normalColor);
            };
        }

        private StackPanel CreateParameterPanel(string label, object source, string propertyName, string toolTip)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var labelControl = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 5),
                FontWeight = FontWeights.SemiBold
            };

            var textBox = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(5),
                ToolTip = toolTip
            };

            var binding = new Binding(propertyName)
            {
                Source = source,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
            textBox.SetBinding(TextBox.TextProperty, binding);

            panel.Children.Add(labelControl);
            panel.Children.Add(textBox);

            return panel;
        }

        public object GetControlView()
        {
            // Подписываемся на обновление значений
            OnValuesUpdated += () =>
            {
                RefreshUI();
            };

            // Принудительно обновляем индикаторы при создании view
            ForceIndicatorUpdate();


            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(15)
            };

            var mainPanel = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            // ВЕРХНЯЯ ПАНЕЛЬ С ИНСТРУМЕНТОМ И СТАТУСОМ
            var headerPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 250)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 15)
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Инструмент
            var instrumentIcon = new TextBlock
            {
                Text = "📈",
                FontSize = 24,
                Margin = new Thickness(0, 0, 15, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(instrumentIcon, 0);
            headerGrid.Children.Add(instrumentIcon);

            var instrumentInfo = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            instrumentInfo.Children.Add(new TextBlock
            {
                Text = $"{_instrument?.Ticker} - {_instrument?.Name}",
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Foreground = Brushes.DarkBlue
            });

            instrumentInfo.Children.Add(new TextBlock
            {
                Text = $"Таймфрейм: {_timeframe} | ATR: {_atrValue:F4}",
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 3, 0, 0)
            });

            Grid.SetColumn(instrumentInfo, 1);
            headerGrid.Children.Add(instrumentInfo);

            // Статус стратегии
            var statusPanel = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center
            };

            var statusText = new TextBlock();
            statusText.SetBinding(TextBlock.TextProperty, new Binding("StrategyStatus") { Source = _indicatorValues });
            statusText.SetBinding(TextBlock.ForegroundProperty, new Binding("StrategyStatusColor") { Source = _indicatorValues });
            statusText.FontWeight = FontWeights.Bold;

            statusPanel.Child = statusText;
            Grid.SetColumn(statusPanel, 2);
            headerGrid.Children.Add(statusPanel);

            headerPanel.Child = headerGrid;
            mainPanel.Children.Add(headerPanel);

            // ПРОГРЕСС-БАР АНАЛИЗА - ИСПРАВЛЕННАЯ ВЕРСИЯ
            var progressPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(245, 245, 250)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 15),
                Visibility = Visibility.Collapsed
            };

            // ИСПРАВЛЕНИЕ 1: Добавляем конвертер для Visibility
            var progressVisibilityBinding = new Binding("IsAnalysisProgressVisible")
            {
                Source = this,
                Converter = new BooleanToVisibilityConverter() // Добавьте этот конвертер
            };
            progressPanel.SetBinding(UIElement.VisibilityProperty, progressVisibilityBinding);

            var progressStack = new StackPanel();

            var progressText = new TextBlock
            {
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 5)
            };
            progressText.SetBinding(TextBlock.TextProperty, new Binding("AnalysisCurrentOperation") { Source = this });
            progressStack.Children.Add(progressText);

            var progressBar = new ProgressBar
            {
                Height = 20,
                Minimum = 0,
                Maximum = 100
            };

            // ИСПРАВЛЕНИЕ 2: Явно указываем OneWay для read-only свойства
            var progressBinding = new Binding("AnalysisProgressPercentage")
            {
                Source = this,
                Mode = BindingMode.OneWay
            };
            progressBar.SetBinding(ProgressBar.ValueProperty, progressBinding);

            progressStack.Children.Add(progressBar);

            progressPanel.Child = progressStack;
            mainPanel.Children.Add(progressPanel);

            // ПАНЕЛЬ ТЕКУЩЕЙ ЦЕНЫ И РАЗМЕРА ПОЗИЦИИ
            var infoGrid = new Grid();
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.Margin = new Thickness(0, 0, 0, 15);

            // Текущая цена
            var pricePanel = CreateInfoCard("💰 Текущая цена",
                _indicatorValues, "CurrentPrice", "{0:F2} ₽",
                new SolidColorBrush(Color.FromRgb(230, 240, 255)));
            Grid.SetColumn(pricePanel, 0);
            infoGrid.Children.Add(pricePanel);

            // Размер позиции
            var positionSizePanel = CreatePositionSizeCard();
            Grid.SetColumn(positionSizePanel, 1);
            infoGrid.Children.Add(positionSizePanel);

            mainPanel.Children.Add(infoGrid);








            // ПАНЕЛЬ СТАТИСТИКИ ФРАКТАЛОВ
            var fractalStatsGroup = new GroupBox
            {
                Header = new TextBlock
                {
                    Text = "🔍 СТАТИСТИКА ФРАКТАЛОВ",
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Margin = new Thickness(5)
                },
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(15)
            };

            var fractalStatsGrid = new Grid();
            fractalStatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fractalStatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });







            // Панель для покупок
            var buyFractalPanel = CreateFractalStatCard("📈 НИЖНИЕ ФРАКТАЛЫ (Покупка)",
                _fractalStats, "BuyFractals", Colors.Green, 0);
            Grid.SetColumn(buyFractalPanel, 0);
            fractalStatsGrid.Children.Add(buyFractalPanel);

            // Панель для продаж
            var sellFractalPanel = CreateFractalStatCard("📉 ВЕРХНИЕ ФРАКТАЛЫ (Продажа)",
                _fractalStats, "SellFractals", Colors.Red, 1);
            Grid.SetColumn(sellFractalPanel, 1);
            fractalStatsGrid.Children.Add(sellFractalPanel);

            fractalStatsGroup.Content = fractalStatsGrid;
            mainPanel.Children.Add(fractalStatsGroup);









            // ПАНЕЛЬ СТАТИСТИКИ СОВПАДЕНИЙ
            var matchStatsGroup = new GroupBox
            {
                Header = new TextBlock
                {
                    Text = "🎯 СТАТИСТИКА СОВПАДЕНИЙ",
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Margin = new Thickness(5)
                },
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(15)
            };

            var matchStatsGrid = new Grid();
            matchStatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            matchStatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Статистика для покупок
            var buyMatchStatsPanel = CreateMatchStatCard("📈 ПОКУПКИ", _buyMatchStats, Colors.Green, 0); 
            Grid.SetColumn(buyMatchStatsPanel, 0);
            matchStatsGrid.Children.Add(buyMatchStatsPanel);

            // Статистика для продаж
            var sellMatchStatsPanel = CreateMatchStatCard("📉 ПРОДАЖИ", _sellMatchStats, Colors.Red, 1);
            Grid.SetColumn(sellMatchStatsPanel, 1);
            matchStatsGrid.Children.Add(sellMatchStatsPanel);

            matchStatsGroup.Content = matchStatsGrid;
            mainPanel.Children.Add(matchStatsGroup);







            // ПАНЕЛЬ РЕЙТИНГОВ
            var ratingGroup = new GroupBox
            {
                Header = new TextBlock
                {
                    Text = "🏆 ТЕКУЩИЕ РЕЙТИНГИ",
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Margin = new Thickness(5)
                },
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(15)
            };

            var ratingMainPanel = new StackPanel();

            // Прогресс-бары рейтингов
            var ratingGrid = new Grid();
            ratingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ratingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ratingGrid.Margin = new Thickness(0, 0, 0, 15);

            // Рейтинг покупки
            ratingGrid.Children.Add(CreateRatingBar("📈 ПОКУПКА",
                _indicatorValues, "BuyRating", "MaxRating",
                Color.FromRgb(76, 175, 80), 0));

            // Рейтинг продажи
            ratingGrid.Children.Add(CreateRatingBar("📉 ПРОДАЖА",
                _indicatorValues, "SellRating", "MaxRating",
                Color.FromRgb(244, 67, 54), 1));

            ratingMainPanel.Children.Add(ratingGrid);

            // Статус анализа
            var analysisStatusPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(245, 245, 250)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var analysisStatusStack = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };

            analysisStatusStack.Children.Add(new TextBlock
            {
                Text = "📊 ",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            });

            var analysisStatusText = new TextBlock();
            analysisStatusText.SetBinding(TextBlock.TextProperty, new Binding("AnalysisStatus") { Source = _indicatorValues });
            analysisStatusText.VerticalAlignment = VerticalAlignment.Center;
            analysisStatusText.FontStyle = FontStyles.Italic;
            analysisStatusStack.Children.Add(analysisStatusText);

            analysisStatusPanel.Child = analysisStatusStack;
            ratingMainPanel.Children.Add(analysisStatusPanel);

            ratingGroup.Content = ratingMainPanel;
            mainPanel.Children.Add(ratingGroup);

            // ПАНЕЛЬ СИГНАЛА
            var signalGroup = new GroupBox
            {
                Header = new TextBlock
                {
                    Text = "🔔 ТЕКУЩИЙ СИГНАЛ",
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Margin = new Thickness(5)
                },
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(15)
            };

            var signalPanel = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Background = Brushes.LightGray // Цвет по умолчанию
            };
            signalPanel.SetBinding(Border.BackgroundProperty, new Binding("SignalColor") { Source = _indicatorValues });

            var signalStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var signalValue = new TextBlock
            {
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 5),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            signalValue.SetBinding(TextBlock.TextProperty, new Binding("CurrentSignal") { Source = _indicatorValues });
            signalValue.SetBinding(TextBlock.ForegroundProperty, new Binding("SignalColor") { Source = _indicatorValues });
            signalStack.Children.Add(signalValue);

            var signalDescription = new TextBlock
            {
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.Black
            };
            signalDescription.SetBinding(TextBlock.TextProperty, new Binding("SignalDescription") { Source = _indicatorValues });
            signalStack.Children.Add(signalDescription);

            signalPanel.Child = signalStack;
            signalGroup.Content = signalPanel;
            mainPanel.Children.Add(signalGroup);

            // ТАБЛИЦА СОВПАДЕНИЙ
            if (_analysisResult != null && (_analysisResult.BuyMatches.Any() || _analysisResult.SellMatches.Any()))
            {
                var matchesGroup = new GroupBox
                {
                    Header = new TextBlock
                    {
                        Text = "📋 ТОП-10 СОВПАДЕНИЙ",
                        FontWeight = FontWeights.Bold,
                        FontSize = 14,
                        Margin = new Thickness(5)
                    },
                    Margin = new Thickness(0, 0, 0, 15),
                    Padding = new Thickness(15)
                };

                var tabControl = new TabControl();

                // Вкладка покупок
                if (_topBuyMatches.Any())
                {
                    var buyTab = new TabItem { Header = "📈 ПОКУПКИ" };
                    buyTab.Content = CreateMatchesTable(_topBuyMatches);
                    tabControl.Items.Add(buyTab);
                }

                // Вкладка продаж
                if (_topSellMatches.Any())
                {
                    var sellTab = new TabItem { Header = "📉 ПРОДАЖИ" };
                    sellTab.Content = CreateMatchesTable(_topSellMatches);
                    tabControl.Items.Add(sellTab);
                }

                matchesGroup.Content = tabControl;
                mainPanel.Children.Add(matchesGroup);
            }

            // ИНДИКАТОРЫ
            var indicatorsGroup = new GroupBox
            {
                Header = new TextBlock
                {
                    Text = "📊 ТЕКУЩИЕ ИНДИКАТОРЫ",
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Margin = new Thickness(5)
                },
                Padding = new Thickness(15)
            };

            var indicatorsTabControl = new TabControl();

            // SMA
            if (_indicatorValues.SmaValues.Any())
            {
                var smaTab = new TabItem { Header = "SMA" };
                smaTab.Content = CreateIndicatorsTable(_indicatorValues.SmaValues, "SMA");
                indicatorsTabControl.Items.Add(smaTab);
            }

            // EMA
            if (_indicatorValues.EmaValues.Any())
            {
                var emaTab = new TabItem { Header = "EMA" };
                emaTab.Content = CreateIndicatorsTable(_indicatorValues.EmaValues, "EMA");
                indicatorsTabControl.Items.Add(emaTab);
            }

            // RSI
            if (_indicatorValues.RsiValues.Any())
            {
                var rsiTab = new TabItem { Header = "RSI" };
                rsiTab.Content = CreateIndicatorsTable(_indicatorValues.RsiValues, "RSI");
                indicatorsTabControl.Items.Add(rsiTab);
            }

            indicatorsGroup.Content = indicatorsTabControl;
            mainPanel.Children.Add(indicatorsGroup);

            scrollViewer.Content = mainPanel;
            return scrollViewer;
        }

        // Вспомогательный метод для создания карточки с информацией
        private Border CreateInfoCard(string title, object source, string propertyName, string format, Brush background)
        {
            var card = new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(5)
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var valueText = new TextBlock
            {
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var binding = new Binding(propertyName)
            {
                Source = source,
                StringFormat = format
            };
            valueText.SetBinding(TextBlock.TextProperty, binding);
            stack.Children.Add(valueText);

            card.Child = stack;
            return card;
        }

        // Вспомогательный метод для создания карточки размера позиции
        private Border CreatePositionSizeCard()
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 240, 230)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(5)
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(new TextBlock
            {
                Text = "💰 Размер позиции",
                FontSize = 12,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var valueText = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var multiBinding = new MultiBinding
            {
                Converter = new PositionSizeDisplayConverter()
            };
            multiBinding.Bindings.Add(new Binding("PositionSizeValue") { Source = _indicatorValues });
            multiBinding.Bindings.Add(new Binding("PositionSizeLots") { Source = _indicatorValues });
            multiBinding.Bindings.Add(new Binding("PositionSizeType") { Source = _parameters });
            multiBinding.Bindings.Add(new Binding("CurrentPrice") { Source = _indicatorValues });

            valueText.SetBinding(TextBlock.TextProperty, multiBinding);
            stack.Children.Add(valueText);

            card.Child = stack;
            return card;
        }

        // Вспомогательный метод для создания прогресс-бара рейтинга
        private Border CreateRatingBar(string title, object source, string valuePath, string maxPath, Color color, int column)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(5)
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };

            // Заголовок с пояснением
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            headerPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 5, 0)
            });

            var infoIcon = new TextBlock
            {
                Text = "ⓘ",
                FontSize = 12,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Первое число - текущий рейтинг (сумма взвешенных совпадений)\nВторое число - максимальный рейтинг за всю историю\nПорог входа настраивается в параметрах стратегии"
            };
            headerPanel.Children.Add(infoIcon);

            stack.Children.Add(headerPanel);

            // Значение
            var valuePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var valueText = new TextBlock
            {
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color)
            };
            valueText.SetBinding(TextBlock.TextProperty, new Binding(valuePath) { Source = source });
            valuePanel.Children.Add(valueText);

            valuePanel.Children.Add(new TextBlock
            {
                Text = " / ",
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 3)
            });

            var maxText = new TextBlock
            {
                FontSize = 18,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            maxText.SetBinding(TextBlock.TextProperty, new Binding(maxPath) { Source = source });
            valuePanel.Children.Add(maxText);

            stack.Children.Add(valuePanel);



            /* // Добавляем пояснение под числами
             var threshold = _maxRating * _parameters.EntryThreshold / 100;
             var explanation = new TextBlock
             {
                 Text = $"Порог входа: {threshold} (% от макс.)",
                 FontSize = 10,
                 Foreground = Brushes.Gray,
                 HorizontalAlignment = HorizontalAlignment.Center,
                 Margin = new Thickness(0, 0, 0, 5)
             };
             stack.Children.Add(explanation);*/

            // Добавляем пояснение под числами с привязкой к EntryThreshold
            var thresholdPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            var thresholdLabel = new TextBlock
            {
                Text = "Порог: ",
                FontSize = 10,
                Foreground = Brushes.Gray
            };
            thresholdPanel.Children.Add(thresholdLabel);

            var thresholdValue = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray
            };
            thresholdValue.SetBinding(TextBlock.TextProperty, new Binding("EntryThreshold") { Source = source, StringFormat = "{0}%" });
            thresholdPanel.Children.Add(thresholdValue);

            stack.Children.Add(thresholdPanel);


            // Прогресс-бар
            var progressBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                CornerRadius = new CornerRadius(4),
                Height = 20,
                Margin = new Thickness(0, 5, 0, 0)
            };

            var progressBar = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var widthBinding = new MultiBinding
            {
                Converter = new ProgressBarWidthConverter()
            };
            widthBinding.Bindings.Add(new Binding(valuePath) { Source = source });
            widthBinding.Bindings.Add(new Binding(maxPath) { Source = source });
            widthBinding.Bindings.Add(new Binding("ActualWidth") { Source = progressBorder });

            progressBar.SetBinding(Border.WidthProperty, widthBinding);
            progressBorder.Child = progressBar;
            stack.Children.Add(progressBorder);

            card.Child = stack;
            Grid.SetColumn(card, column);
            return card;
        }

        // Конвертер для отображения порога
        public class ThresholdDisplayConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (values.Length >= 2 && values[0] is int current && values[1] is int max)
                {
                    int threshold = max * 70 / 100;
                    return $"Порог входа: {threshold} (70% от макс.)";
                }
                return "Порог входа: -";
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        // Вспомогательный метод для создания карточки статистики фракталов
        private Border CreateFractalStatCard(string title, FractalStatistics stats, string propertyName, Color color, int column)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(5)
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var valueText = new TextBlock
            {
                FontSize = 32,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var binding = new Binding(propertyName) { Source = stats };
            valueText.SetBinding(TextBlock.TextProperty, binding);
            stack.Children.Add(valueText);

            card.Child = stack;
            Grid.SetColumn(card, column);
            return card;
        }

        // Вспомогательный метод для создания карточки статистики совпадений
        private Border CreateMatchStatCard(string title, MatchStatistics stats, Color color, int column)
        {
            if (stats == null)
            {
                Debug.WriteLine($"[UI ERROR] stats is null for {title}");
                stats = new MatchStatistics();
            }

            Debug.WriteLine($"[UI] Creating match card: {title}, " +
                $"Exact={stats.ExactMatches}, Low={stats.LowDeviationMatches}, Medium={stats.MediumDeviationMatches}");

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(5),
                Tag = title
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // Точные совпадения с тултипом
            var exactPanel = CreateStatRowWithBinding("🎯 Точные:", stats, nameof(MatchStatistics.ExactMatches),
                MatchType.Exact, stats.Matches);
            stack.Children.Add(exactPanel);

            // Малые отклонения с тултипом
            var lowPanel = CreateStatRowWithBinding("📊 Малые (0.1%):", stats, nameof(MatchStatistics.LowDeviationMatches),
                MatchType.LowDeviation, stats.Matches);
            stack.Children.Add(lowPanel);

            // Средние отклонения с тултипом
            var mediumPanel = CreateStatRowWithBinding("📈 Средние (0.5%):", stats, nameof(MatchStatistics.MediumDeviationMatches),
                MatchType.MediumDeviation, stats.Matches);
            stack.Children.Add(mediumPanel);

            card.Child = stack;
            Grid.SetColumn(card, column);
            return card;
        }

        private StackPanel CreateStatRowWithBinding(string label, MatchStatistics stats, string propertyName,
    MatchType matchType, ObservableCollection<IndicatorMatch> allMatches)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
                Cursor = System.Windows.Input.Cursors.Help
            };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Width = 90,
                FontSize = 12
            });

            var valueText = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                FontSize = 12
            };

            var binding = new Binding(propertyName)
            {
                Source = stats,
                Mode = BindingMode.OneWay
            };
            valueText.SetBinding(TextBlock.TextProperty, binding);

            panel.Children.Add(valueText);

            // Устанавливаем начальный тултип
            UpdateToolTipSafe(panel, allMatches, matchType);

            // Подписываемся на изменения коллекции через Dispatcher
            if (allMatches != null)
            {
                allMatches.CollectionChanged += (s, e) =>
                {
                    // Обновляем тултип в UI потоке
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        UpdateToolTipSafe(panel, allMatches, matchType);
                    });
                };
            }

            return panel;
        }

        private void UpdateToolTipSafe(StackPanel panel, ObservableCollection<IndicatorMatch> matches, MatchType matchType)
        {
            if (matches == null) return;

            var filteredMatches = matches.Where(m => m.MatchType == matchType).ToList(); // MatchType теперь доступен
            if (filteredMatches.Any())
            {
                var tooltipText = string.Join("\n", filteredMatches
                    .GroupBy(m => m.IndicatorName)
                    .Select(g => $"{g.Key}: {g.Sum(m => m.MatchCount)} раз")
                    .OrderByDescending(x => {
                        var parts = x.Split(':');
                        return parts.Length > 1 ? int.Parse(parts[1].Replace(" раз", "").Trim()) : 0;
                    }));

                panel.ToolTip = $"Индикаторы:\n{tooltipText}";
            }
            else
            {
                panel.ToolTip = "Нет совпадений";
            }
        }

        // Конвертер для цвета значения
        public class ValueToBrushConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is int intValue && intValue > 0)
                {
                    return new SolidColorBrush(Colors.Black);
                }
                return new SolidColorBrush(Colors.Gray);
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        // Вспомогательный метод для создания строки статистики с тултипом
        /*private StackPanel CreateStatRow(string label, int value, string tooltip)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
                ToolTip = tooltip,
                Cursor = System.Windows.Input.Cursors.Help
            };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Width = 90,
                FontSize = 12
            });

            panel.Children.Add(new TextBlock
            {
                Text = value.ToString(),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = new SolidColorBrush(value > 0 ? Colors.Black : Colors.Gray)
            });

            return panel;
        }*/

        // Вспомогательный метод для создания тултипа со списком индикаторов
        /*private string GetMatchTooltip(List<IndicatorMatch> matches)
        {
            if (!matches.Any()) return "Нет совпадений";

            var groupedByIndicator = matches
                .GroupBy(m => m.IndicatorName)
                .Select(g => $"{g.Key}: {g.Sum(m => m.MatchCount)} раз")
                .ToList();

            return "Индикаторы:\n" + string.Join("\n", groupedByIndicator);
        }*/




        // Вспомогательный метод для создания таблицы совпадений
        private ListView CreateMatchesTable(ObservableCollection<IndicatorMatch> matches)
        {
            var listView = new ListView
            {
                Margin = new Thickness(5),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = matches
            };

            // Создаем шаблон для отображения
            var template = new DataTemplate();
            var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
            stackFactory.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            stackFactory.SetValue(StackPanel.MarginProperty, new Thickness(5));

            // Заголовок
            var headerFactory = new FrameworkElementFactory(typeof(TextBlock));
            headerFactory.SetBinding(TextBlock.TextProperty, new Binding("Summary"));
            headerFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            headerFactory.SetValue(TextBlock.FontSizeProperty, 14.0);
            stackFactory.AppendChild(headerFactory);

            // Значения индикаторов
            var indicatorsFactory = new FrameworkElementFactory(typeof(ItemsControl));
            indicatorsFactory.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("TypicalValues"));

            var itemTemplate = new DataTemplate();
            var itemStackFactory = new FrameworkElementFactory(typeof(StackPanel));
            itemStackFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var nameFactory = new FrameworkElementFactory(typeof(TextBlock));
            nameFactory.SetBinding(TextBlock.TextProperty, new Binding("Key"));
            nameFactory.SetValue(TextBlock.WidthProperty, 80.0);
            nameFactory.SetValue(TextBlock.FontSizeProperty, 11.0);
            nameFactory.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
            itemStackFactory.AppendChild(nameFactory);

            var valueFactory = new FrameworkElementFactory(typeof(TextBlock));
            valueFactory.SetBinding(TextBlock.TextProperty, new Binding("Value") { StringFormat = "{0:F4}" });
            valueFactory.SetValue(TextBlock.FontSizeProperty, 11.0);
            valueFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            itemStackFactory.AppendChild(valueFactory);

            itemTemplate.VisualTree = itemStackFactory;
            indicatorsFactory.SetValue(ItemsControl.ItemTemplateProperty, itemTemplate);

            stackFactory.AppendChild(indicatorsFactory);

            // Дополнительная информация
            var infoFactory = new FrameworkElementFactory(typeof(StackPanel));
            infoFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            infoFactory.SetValue(StackPanel.MarginProperty, new Thickness(0, 5, 0, 0));

            var firstSeenFactory = new FrameworkElementFactory(typeof(TextBlock));
            firstSeenFactory.SetBinding(TextBlock.TextProperty, new Binding("FirstSeen") { StringFormat = "Первый: {0:dd.MM HH:mm}" });
            firstSeenFactory.SetValue(TextBlock.FontSizeProperty, 10.0);
            firstSeenFactory.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
            infoFactory.AppendChild(firstSeenFactory);

            var lastSeenFactory = new FrameworkElementFactory(typeof(TextBlock));
            lastSeenFactory.SetBinding(TextBlock.TextProperty, new Binding("LastSeen") { StringFormat = " Последний: {0:dd.MM HH:mm}" });
            lastSeenFactory.SetValue(TextBlock.FontSizeProperty, 10.0);
            lastSeenFactory.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
            infoFactory.AppendChild(lastSeenFactory);

            stackFactory.AppendChild(infoFactory);

            template.VisualTree = stackFactory;
            listView.ItemTemplate = template;

            return listView;
        }

        // Вспомогательный метод для создания таблицы индикаторов
        private ListView CreateIndicatorsTable(ObservableDictionary<int, decimal> values, string indicatorName)
        {
            Debug.WriteLine($"[UI] Creating {indicatorName} table with {values?.Count ?? 0} items");
            if (values != null)
            {
                foreach (var kvp in values)
                {
                    Debug.WriteLine($"  {indicatorName} {kvp.Key} = {kvp.Value}");
                }
            }

            var listView = new ListView
            {
                Margin = new Thickness(5),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            var gridView = new GridView();

            gridView.Columns.Add(new GridViewColumn
            {
                Header = $"{indicatorName} период",
                Width = 120,
                DisplayMemberBinding = new Binding("Key") { StringFormat = "Период {0}" }
            });

            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Значение",
                Width = 100,
                DisplayMemberBinding = new Binding("Value") { StringFormat = "{0:F4}" }
            });

            listView.View = gridView;
            listView.ItemsSource = values;

            return listView;
        }

        private void ForceIndicatorUpdate()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Создаем новые словари с данными
                var newSmaValues = new ObservableDictionary<int, decimal>();
                foreach (var kvp in _smaValues.Where(kv => kv.Value > 0))
                {
                    newSmaValues[kvp.Key] = kvp.Value;
                }
                _indicatorValues.SmaValues = newSmaValues;

                var newEmaValues = new ObservableDictionary<int, decimal>();
                foreach (var kvp in _emaValues.Where(kv => kv.Value > 0))
                {
                    newEmaValues[kvp.Key] = kvp.Value;
                }
                _indicatorValues.EmaValues = newEmaValues;

                var newRsiValues = new ObservableDictionary<int, decimal>();
                foreach (var kvp in _rsiValues.Where(kv => kv.Value > 0))
                {
                    newRsiValues[kvp.Key] = kvp.Value;
                }
                _indicatorValues.RsiValues = newRsiValues;

                _indicatorValues.TrendDescription = $"{(_isBullishTrend ? "БЫЧИЙ" : _isBearishTrend ? "МЕДВЕЖИЙ" : "НЕЙТРАЛЬНЫЙ")} ({_trendStrength})";


                // Принудительно вызываем обновление всех свойств
                OnPropertyChanged(nameof(RatingViewModel.SmaValues));
                OnPropertyChanged(nameof(RatingViewModel.EmaValues));
                OnPropertyChanged(nameof(RatingViewModel.RsiValues));
                OnPropertyChanged(nameof(RatingViewModel.TrendDescription));

                // Принудительно обновляем
                OnPropertyChanged(nameof(_indicatorValues));


                //Debug.WriteLine($"[INDICATORS] Updated: SMA={newSmaValues.Count}, EMA={newEmaValues.Count}, RSI={newRsiValues.Count}");
                //foreach (var kvp in newSmaValues)
                //    Debug.WriteLine($"  SMA {kvp.Key} = {kvp.Value}");
                //foreach (var kvp in newEmaValues)
                //    Debug.WriteLine($"  EMA {kvp.Key} = {kvp.Value}");
                //foreach (var kvp in newRsiValues)
                //    Debug.WriteLine($"  RSI {kvp.Key} = {kvp.Value}");
            });
        }







        private StackPanel CreateInfoRow(string label, object source, string propertyName, string format)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 5, 0, 5)
            };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold,
                Width = 120
            });

            var valueText = new TextBlock();
            var binding = new Binding(propertyName)
            {
                Source = source,
                StringFormat = format
            };
            valueText.SetBinding(TextBlock.TextProperty, binding);
            panel.Children.Add(valueText);

            return panel;
        }

        private GroupBox CreateIndicatorGroupBox(string header, object source, string propertyName, Brush headerColor)
        {
            var groupBox = new GroupBox
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(10)
            };

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var headerRectangle = new Rectangle
            {
                Width = 10,
                Height = 10,
                Fill = headerColor,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(headerRectangle);
            headerPanel.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.Bold });
            groupBox.Header = headerPanel;

            var listView = new ListView
            {
                Margin = new Thickness(5),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Период",
                Width = 100,
                DisplayMemberBinding = new Binding("Key") { StringFormat = "Период {0}" }
            });

            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Значение",
                Width = 100,
                DisplayMemberBinding = new Binding("Value") { StringFormat = "{0:F2}" }
            });

            listView.View = gridView;

            var itemsBinding = new Binding(propertyName)
            {
                Source = source,
                Mode = BindingMode.OneWay
            };
            listView.SetBinding(ListView.ItemsSourceProperty, itemsBinding);

            groupBox.Content = listView;
            return groupBox;
        }

        private void AddLegendItem(StackPanel panel, string text, Brush color)
        {
            var itemPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2)
            };

            var colorRect = new Rectangle
            {
                Width = 12,
                Height = 12,
                Fill = color,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var textBlock = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            };

            itemPanel.Children.Add(colorRect);
            itemPanel.Children.Add(textBlock);
            panel.Children.Add(itemPanel);
        }

        public async ValueTask DisposeAsync()
        {
            _parameters.OnParametersChanged -= OnParametersChanged;
            if (State == StrategyState.Running)
            {
                await StopAsync();
            }
        }

        #endregion







        #region ViewModels

        public partial class RatingSettingsViewModel : ObservableObject
        {
            private string _trendPeriods = "20,50,100,200";
            private string _oscillatorPeriods = "14,28,56";
            private string _volumePeriods = "20,50";
            private int _entryThreshold = 90; // Изменено с MaxRating на EntryThreshold
            private string _positionSizeType = "Percent";
            private decimal _positionSizePercent = 5.0m;
            private decimal _positionSizeAbsolute = 1000m;

            public event Action OnParametersChanged;

            private decimal _matchTolerance = 0.1m; // 0.1% tolerance
            private int _minMatchPercentage = 80; // 80% minimum match

            public decimal MatchTolerance
            {
                get => _matchTolerance;
                set => SetProperty(ref _matchTolerance, value);
            }

            public int MinMatchPercentage
            {
                get => _minMatchPercentage;
                set => SetProperty(ref _minMatchPercentage, value);
            }


            public string TrendPeriods
            {
                get => _trendPeriods;
                set => SetProperty(ref _trendPeriods, value);
            }

            public string OscillatorPeriods
            {
                get => _oscillatorPeriods;
                set => SetProperty(ref _oscillatorPeriods, value);
            }

            public string VolumePeriods
            {
                get => _volumePeriods;
                set => SetProperty(ref _volumePeriods, value);
            }

            public int EntryThreshold
            {
                get => _entryThreshold;
                set => SetProperty(ref _entryThreshold, value);
            }

            public string PositionSizeType
            {
                get => _positionSizeType;
                set => SetProperty(ref _positionSizeType, value);
            }

            public decimal PositionSizePercent
            {
                get => _positionSizePercent;
                set => SetProperty(ref _positionSizePercent, value);
            }

            public decimal PositionSizeAbsolute
            {
                get => _positionSizeAbsolute;
                set => SetProperty(ref _positionSizeAbsolute, value);
            }

            public void ApplyParameters()
            {
                OnParametersChanged?.Invoke();
            }

            public void ResetParameters()
            {
                TrendPeriods = "20,50,100,200";
                OscillatorPeriods = "14,28,56";
                VolumePeriods = "20,50";
                EntryThreshold = 90;
                MatchTolerance = 0.1m;
                MinMatchPercentage = 80;
                PositionSizeType = "Percent";
                PositionSizePercent = 5.0m;
                PositionSizeAbsolute = 1000m;
                ApplyParameters();
            }
        }

        public partial class RatingViewModel : ObservableObject
        {
            private ObservableDictionary<int, decimal> _smaValues = new();
            private ObservableDictionary<int, decimal> _emaValues = new();
            private ObservableDictionary<int, decimal> _rsiValues = new();
            private bool _isBullishTrend;
            private bool _isBearishTrend;
            private string _currentSignal = "ОЖИДАНИЕ";
            private string _signalDescription;
            private Brush _signalColor = Brushes.Gray;
            private decimal _currentPrice;
            private decimal _positionSizeValue;
            private decimal _positionSizeLots;
            private decimal _accountBalance;
            private string _trendDescription;
            private string _strategyStatus = "ОСТАНОВЛЕНА";
            private Brush _strategyStatusColor = Brushes.Red;
            private decimal _entryPrice;
            private decimal _stopLossPrice;
            private decimal _takeProfitPrice;
            private int _buyRating;
            private int _sellRating;
            private int _maxRating = 10;
            private string _analysisStatus = "Анализ не выполнен";
            private DateTime _lastAnalysisDate = DateTime.MinValue;
            private ObservableCollection<IndicatorMatch> _topBuyMatches = new();
            private ObservableCollection<IndicatorMatch> _topSellMatches = new();
            // Добавьте новое свойство для отображения порога входа
            private int _entryThreshold = 70;

            public int EntryThreshold
            {
                get => _entryThreshold;
                set => SetProperty(ref _entryThreshold, value);
            }

            public ObservableDictionary<int, decimal> SmaValues
            {
                get => _smaValues;
                set
                {
                    if (_smaValues != value)
                    {
                        _smaValues = value;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(SmaValues));
                    }
                }
            }

            public ObservableDictionary<int, decimal> EmaValues
            {
                get => _emaValues;
                set
                {
                    if (_emaValues != value)
                    {
                        _emaValues = value;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(EmaValues));
                    }
                }
            }

            public ObservableDictionary<int, decimal> RsiValues
            {
                get => _rsiValues;
                set
                {
                    if (_rsiValues != value)
                    {
                        _rsiValues = value;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(RsiValues));
                    }
                }
            }

            public bool IsBullishTrend
            {
                get => _isBullishTrend;
                set => SetProperty(ref _isBullishTrend, value);
            }

            public bool IsBearishTrend
            {
                get => _isBearishTrend;
                set => SetProperty(ref _isBearishTrend, value);
            }

            public string CurrentSignal
            {
                get => _currentSignal;
                set => SetProperty(ref _currentSignal, value);
            }

            public string SignalDescription
            {
                get => _signalDescription;
                set => SetProperty(ref _signalDescription, value);
            }

            public Brush SignalColor
            {
                get => _signalColor;
                set => SetProperty(ref _signalColor, value);
            }

            public decimal CurrentPrice
            {
                get => _currentPrice;
                set => SetProperty(ref _currentPrice, value);
            }

            public decimal PositionSizeValue
            {
                get => _positionSizeValue;
                set => SetProperty(ref _positionSizeValue, value);
            }

            public decimal PositionSizeLots
            {
                get => _positionSizeLots;
                set => SetProperty(ref _positionSizeLots, value);
            }

            public decimal AccountBalance
            {
                get => _accountBalance;
                set => SetProperty(ref _accountBalance, value);
            }

            public string TrendDescription
            {
                get => _trendDescription;
                set => SetProperty(ref _trendDescription, value);
            }

            public string StrategyStatus
            {
                get => _strategyStatus;
                set => SetProperty(ref _strategyStatus, value);
            }

            public Brush StrategyStatusColor
            {
                get => _strategyStatusColor;
                set => SetProperty(ref _strategyStatusColor, value);
            }

            public decimal EntryPrice
            {
                get => _entryPrice;
                set => SetProperty(ref _entryPrice, value);
            }

            public decimal StopLossPrice
            {
                get => _stopLossPrice;
                set => SetProperty(ref _stopLossPrice, value);
            }

            public decimal TakeProfitPrice
            {
                get => _takeProfitPrice;
                set => SetProperty(ref _takeProfitPrice, value);
            }

            public int BuyRating
            {
                get => _buyRating;
                set => SetProperty(ref _buyRating, value);
            }

            public int SellRating
            {
                get => _sellRating;
                set => SetProperty(ref _sellRating, value);
            }

            public int MaxRating
            {
                get => _maxRating;
                set => SetProperty(ref _maxRating, value);
            }

            public string AnalysisStatus
            {
                get => _analysisStatus;
                set => SetProperty(ref _analysisStatus, value);
            }

            public DateTime LastAnalysisDate
            {
                get => _lastAnalysisDate;
                set => SetProperty(ref _lastAnalysisDate, value);
            }

            public ObservableCollection<IndicatorMatch> TopBuyMatches
            {
                get => _topBuyMatches;
                set => SetProperty(ref _topBuyMatches, value);
            }

            public ObservableCollection<IndicatorMatch> TopSellMatches
            {
                get => _topSellMatches;
                set => SetProperty(ref _topSellMatches, value);
            }
        }



        // Конвертер для ширины прогресс-бара
        public class ProgressBarWidthConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (values.Length >= 3 &&
                    values[0] is int current &&
                    values[1] is int max &&
                    values[2] is double totalWidth)
                {
                    if (max == 0) return 0.0;
                    double percentage = (double)current / max;
                    return Math.Min(percentage * totalWidth, totalWidth);
                }
                return 0.0;
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        // В начале файла добавьте этот класс
        // Конвертер для преобразования bool в Visibility
        public class BooleanToVisibilityConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is bool boolValue)
                {
                    return boolValue ? Visibility.Visible : Visibility.Collapsed;
                }
                return Visibility.Collapsed;
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is Visibility visibility)
                {
                    return visibility == Visibility.Visible;
                }
                return false;
            }
        }



        #endregion
    }
}