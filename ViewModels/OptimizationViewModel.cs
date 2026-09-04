using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Models.MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Strategies;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.ComponentModel;




namespace MoneyGenerator_v5.ViewModels
{
    public partial class OptimizationViewModel : ObservableObject
    {
        #region Поля

        private readonly IProvirerService _provider;
        private readonly StrategyViewModel _strategyViewModel;
        private readonly ILogger _logger;
        private readonly IBacktestEngineFactory _engineFactory;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isOptimizing;
        private readonly object _resultLock = new object();
        //private readonly string _strategyType;
        private readonly Dictionary<string, decimal> _originalParameters;

        // ✅ КЭШ ДАННЫХ - загружается один раз
        private OptimizationDataCache _dataCache = new();
        private bool _dataPrepared = false;

        private bool _disposed = false;

        // ✅ Движок бэктеста
        private IBacktestEngine _backtestEngine;

        // лимит на количество хранимых результатов (например, топ-1000 лучших)
        private const int MAX_RESULTS_TO_KEEP = 1000;

       


        #endregion

        #region Observable Properties
        [ObservableProperty]
        private string _strategyType;
        [ObservableProperty]
        private string _instrumentInfo;

        [ObservableProperty]
        private string _timeframeInfo;

        [ObservableProperty]
        private ObservableCollection<OptimizationParameter> _parameters = new();

        [ObservableProperty]
        private ObservableCollection<OptimizationResult> _results = new();

        [ObservableProperty]
        private OptimizationResult _selectedResult;

        [ObservableProperty]
        private bool _isLoadingHistory;

        [ObservableProperty]
        private string _loadingStatus = "Готово";

        [ObservableProperty]
        private double _progressValue;

        [ObservableProperty]
        private double _progressMaximum = 100;

        [ObservableProperty]
        private string _progressText = "";

        [ObservableProperty]
        private bool _isProgressVisible;

        [ObservableProperty]
        private bool _isOptimizationRunning;

        [ObservableProperty]
        private string _optimizationStatus = "Готово";

        [ObservableProperty]
        private DateTime _periodStart = DateTime.Now.AddDays(-365);

        [ObservableProperty]
        private DateTime _periodEnd = DateTime.Now;

        [ObservableProperty]
        private int _selectedHistoryPeriod = 365;

        [ObservableProperty]
        private string _selectedPeriodDisplay = "1 год";

        [ObservableProperty]
        private int _totalCombinations;

        [ObservableProperty]
        private int _completedCombinations;

        [ObservableProperty]
        private string _bestResultSummary;

        [ObservableProperty]
        private string _sortColumn = "NetProfit";

        [ObservableProperty]
        private bool _sortAscending = false;

        [ObservableProperty]
        private ObservableCollection<string> _availablePeriods = new();

        // ✅ Свойства для привязки команд в XAML
        public bool CanStartOptimizationCommand => CanStartOptimization();
        public bool CanStopOptimizationCommand => _isOptimizing;
        public bool CanApplyParametersCommand => SelectedResult != null;
        /// <summary>
        /// Проверка, был ли объект уничтожен
        /// </summary>
        public bool IsDisposed => _disposed;
        #endregion

        #region Команды

        public ICommand LoadHistoryCommand { get; }
        public ICommand StartOptimizationCommand { get; }
        public ICommand StopOptimizationCommand { get; }
        public ICommand ApplyParametersCommand { get; }
        public ICommand SetPeriodCommand { get; }
        public ICommand SortCommand { get; }
        public ICommand RestoreOriginalParametersCommand { get; }

        public event Action<Dictionary<string, decimal>> ParametersApplied;

        #endregion

        public OptimizationViewModel(
             StrategyViewModel strategyViewModel,
             IProvirerService provider,
             ILogger logger,
             IBacktestEngineFactory engineFactory = null)
        {
            Debug.WriteLine($"[OptimizationViewModel] ========== КОНСТРУКТОР НАЧАЛО ==========");
            Debug.WriteLine($"[OptimizationViewModel] strategyViewModel={strategyViewModel != null}");
            Debug.WriteLine($"[OptimizationViewModel] provider={provider != null}");
            Debug.WriteLine($"[OptimizationViewModel] logger={logger != null}");
            Debug.WriteLine($"[OptimizationViewModel] engineFactory={engineFactory != null}");

            _strategyViewModel = strategyViewModel ?? throw new ArgumentNullException(nameof(strategyViewModel));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _logger = logger;

            // ✅ Если фабрика не передана, создаем с NullLoggerFactory
            _engineFactory = engineFactory ?? new BacktestEngineFactory(null, provider);

            _strategyType = _strategyViewModel.SelectedStrategy.Type;
            _originalParameters = new Dictionary<string, decimal>();

            Debug.WriteLine($"[OptimizationViewModel] Инициализация для стратегии: {_strategyType}");

            //_results[0].StrategyType = _strategyType;

            //SelectedResult.StrategyType = _strategyType;

            // Инициализация периодов
            AvailablePeriods.Add("30 дней");
            AvailablePeriods.Add("90 дней");
            AvailablePeriods.Add("120 дней");
            AvailablePeriods.Add("365 дней");
            AvailablePeriods.Add("2 года");
            AvailablePeriods.Add("3 года");

            // Подписываемся на изменение выбранного периода
            this.PropertyChanged += OnPropertyChanged;

            // ✅ Подписываемся на изменение SelectedResult
            this.PropertyChanged += OnViewModelPropertyChanged;


            // Команды
            LoadHistoryCommand = new RelayCommand(async () => await PrepareDataAsync(), () => !_isLoadingHistory && !_dataPrepared);
            StartOptimizationCommand = new RelayCommand(async () => await StartOptimizationAsync(), CanStartOptimization);
            StopOptimizationCommand = new RelayCommand(StopOptimization, () => _isOptimizing);
            ApplyParametersCommand = new RelayCommand(ApplySelectedParameters, () => SelectedResult != null);
            SetPeriodCommand = new RelayCommand<string>(SetPeriod);
            SortCommand = new RelayCommand<string>(SortResults);
            RestoreOriginalParametersCommand = new RelayCommand(RestoreOriginalParameters);




            // Инициализация
            InitializeInstrumentInfo();
            InitializeOptimizationParameters();
            SubscribeToParameterChanges();

            // ✅ По умолчанию выбираем первый параметр, чтобы кнопка СТАРТ стала активной
            if (Parameters.Any())
            {
                Parameters[0].IsSelected = true;
                Debug.WriteLine($"[OptimizationViewModel] Автоматически выбран параметр: {Parameters[0].Name}");
            }

            UpdateTotalCombinations();
            RefreshCommands();

            SetPeriod("365");

            Debug.WriteLine($"[OptimizationViewModel] Инициализация завершена. Параметров: {Parameters.Count}");
            Debug.WriteLine($"[OptimizationViewModel] ========== КОНСТРУКТОР КОНЕЦ ==========");
        }

        #region Инициализация

        private void InitializeInstrumentInfo()
        {
            Debug.WriteLine("[InitializeInstrumentInfo] НАЧАЛО");
            var instrument = _strategyViewModel.Instrument;
            var timeframe = _strategyViewModel.SelectedTimeFrame;
            InstrumentInfo = $"{instrument.Ticker} - {instrument.Name}";
            TimeframeInfo = timeframe.DisplayName;
            Debug.WriteLine($"[InitializeInstrumentInfo] InstrumentInfo={InstrumentInfo}, TimeframeInfo={TimeframeInfo}");
            Debug.WriteLine("[InitializeInstrumentInfo] КОНЕЦ");
        }

        private void InitializeOptimizationParameters()
        {
            Debug.WriteLine($"[InitializeOptimizationParameters] НАЧАЛО для {_strategyType}");
            Parameters.Clear();

            switch (_strategyType)
            {
                case "PairsTrading":
                    Debug.WriteLine("[InitializeOptimizationParameters] Добавление параметров PairsTrading");
                    AddPairsTradingParameters();
                    break;
                case "RSI":
                    Debug.WriteLine("[InitializeOptimizationParameters] Добавление параметров RSI");
                    AddRsiParameters();
                    break;
                case "MA":
                    Debug.WriteLine("[InitializeOptimizationParameters] Добавление параметров MA");
                    AddMaOptimizationParameters(); // ✅ ИСПОЛЬЗУЕМ НОВЫЙ МЕТОД
                    break;
                case "Rating":
                    Debug.WriteLine("[InitializeOptimizationParameters] Добавление параметров Rating");
                    AddRatingParameters();
                    break;
                default:
                    Debug.WriteLine($"[InitializeOptimizationParameters] Неподдерживаемый тип: {_strategyType}");
                    break;
            }
            Debug.WriteLine($"[InitializeOptimizationParameters] КОНЕЦ. Параметров: {Parameters.Count}");
        }


        private void AddPairsTradingParameters()
        {
            Debug.WriteLine("[AddPairsTradingParameters] НАЧАЛО");
            var strategy = _strategyViewModel.PairsStrategy;
            if (strategy == null)
            {
                Debug.WriteLine("[AddPairsTradingParameters] strategy is NULL!");
                return;
            }

            var pairsParams = strategy.Parameters;
            if (pairsParams == null)
            {
                Debug.WriteLine("[AddPairsTradingParameters] pairsParams is NULL!");
                return;
            }

            Debug.WriteLine($"[AddPairsTradingParameters] LookbackPeriod={pairsParams.LookbackPeriod}");
            Debug.WriteLine($"[AddPairsTradingParameters] EntryZScore={pairsParams.EntryZScore}");
            Debug.WriteLine($"[AddPairsTradingParameters] ExitZScore={pairsParams.ExitZScore}");
            Debug.WriteLine($"[AddPairsTradingParameters] StopLossZScore={pairsParams.StopLossZScore}");
            Debug.WriteLine($"[AddPairsTradingParameters] PositionSizePercent={pairsParams.PositionSizePercent}");

            AddParameter("LookbackPeriod", "Период обучения", pairsParams.LookbackPeriod, 24, 500, 24);
            AddParameter("EntryZScore", "Порог входа Z-Score", pairsParams.EntryZScore, 1.0m, 4.0m, 0.25m);
            AddParameter("ExitZScore", "Порог выхода Z-Score", pairsParams.ExitZScore, 0.1m, 1.5m, 0.1m);
            AddParameter("StopLossZScore", "Стоп-лосс Z-Score", pairsParams.StopLossZScore, 2.5m, 5.0m, 0.25m);
            AddParameter("PositionSizePercent", "Размер позиции (%)", pairsParams.PositionSizePercent, 1, 50, 1);
            Debug.WriteLine("[AddPairsTradingParameters] КОНЕЦ");
        }

        private void AddRsiParameters()
        {
            Debug.WriteLine("[AddRsiParameters] НАЧАЛО");
            var strategy = _strategyViewModel.RsiStrategy;
            if (strategy == null)
            {
                Debug.WriteLine("[AddRsiParameters] strategy is NULL!");
                return;
            }

            var rsiParams = strategy.Parameters;
            if (rsiParams == null)
            {
                Debug.WriteLine("[AddRsiParameters] rsiParams is NULL!");
                return;
            }

            Debug.WriteLine($"[AddRsiParameters] RsiPeriod={rsiParams.RsiPeriod}");
            Debug.WriteLine($"[AddRsiParameters] RsiOverbought={rsiParams.RsiOverbought}");
            Debug.WriteLine($"[AddRsiParameters] RsiOversold={rsiParams.RsiOversold}");

            AddParameter("RsiPeriod", "Период RSI", rsiParams.RsiPeriod, 5, 50, 1);
            AddParameter("RsiOverbought", "Перекупленность RSI", rsiParams.RsiOverbought, 60, 90, 1);
            AddParameter("RsiOversold", "Перепроданность RSI", rsiParams.RsiOversold, 10, 40, 1);
            AddParameter("OrderSizePercent", "Размер позиции (%)", rsiParams.OrderSizePercent, 1, 50, 1);
            AddParameter("TakeProfitPercent", "Тейк-профит (%)", rsiParams.TakeProfitPercent, 0.5m, 10, 0.5m);
            AddParameter("StopLossPercent", "Стоп-лосс (%)", rsiParams.StopLossPercent, 0.5m, 5, 0.5m);
            Debug.WriteLine("[AddRsiParameters] КОНЕЦ");
        }

        /// <summary>
        /// Добавляет параметры для оптимизации MA стратегии
        /// АВТОМАТИЧЕСКИ СОРТИРУЕТ периоды для соблюдения логики стратегии
        /// </summary>
        private void AddMaOptimizationParameters()
        {
            Debug.WriteLine("[AddMaOptimizationParameters] НАЧАЛО");

            

            var strategy = _strategyViewModel.MaStrategy;
            if (strategy == null)
            {
                Debug.WriteLine("[AddMaOptimizationParameters] strategy is NULL!");
                return;
            }




            var maParams = strategy.Parameters;
            if (maParams == null)
            {
                Debug.WriteLine("[AddMaOptimizationParameters] maParams is NULL!");
                return;
            }




            // ✅ Выводим текущие значения из стратегии
            Debug.WriteLine($"[AddMaOptimizationParameters] Текущий PositionSizePercent из стратегии: {maParams.PositionSizePercent}%");
            Debug.WriteLine($"[AddMaOptimizationParameters] Текущие ATR параметры из стратегии:");
            Debug.WriteLine($"  StopLossATRMultiplier = {maParams.StopLossATRMultiplier}");
            Debug.WriteLine($"  TakeProfitATRMultiplier = {maParams.TakeProfitATRMultiplier}");
            Debug.WriteLine($"  TrailingStopATRMultiplier = {maParams.TrailingStopATRMultiplier}");
            Debug.WriteLine($"[AddMaOptimizationParameters] Текущий FilterSmaPeriod из стратегии: {maParams.FilterSmaPeriod}");
            Debug.WriteLine($"[AddMaOptimizationParameters] UseManualFilterSma: {maParams.UseManualFilterSma}");

            // ✅ Парсим SMA периоды и СОРТИРУЕМ для корректной логики
            var smaPeriods = ParsePeriodsSorted(maParams.SmaPeriods);
            Debug.WriteLine($"[AddMaOptimizationParameters] SMA периоды (отсортированы): {string.Join(",", smaPeriods)}");

            // ✅ Парсим EMA периоды и СОРТИРУЕМ для корректной логики
            var emaPeriods = ParsePeriodsSorted(maParams.EmaPeriods);
            Debug.WriteLine($"[AddMaOptimizationParameters] EMA периоды (отсортированы): {string.Join(",", emaPeriods)}");

            // Проверка валидности
            if (smaPeriods.Count < 3 || smaPeriods.Any(p => p <= 0))
            {
                Debug.WriteLine("[AddMaOptimizationParameters] SMA периоды невалидны, используем значения по умолчанию");
                smaPeriods = new List<int> { 20, 50, 100 };
            }

            if (emaPeriods.Count < 3 || emaPeriods.Any(p => p <= 0))
            {
                Debug.WriteLine("[AddMaOptimizationParameters] EMA периоды невалидны, используем значения по умолчанию");
                emaPeriods = new List<int> { 25, 50, 100 };
            }

            // ✅ Берем первые 3 периода (короткий, средний, длинный)
            var smaShort = smaPeriods[0];
            var smaMedium = smaPeriods[1];
            var smaLong = smaPeriods[2];

            var emaShort = emaPeriods[0];
            var emaMedium = emaPeriods[1];
            var emaLong = emaPeriods[2];

            Debug.WriteLine($"[AddMaOptimizationParameters] SMA: Short={smaShort}, Medium={smaMedium}, Long={smaLong}");
            Debug.WriteLine($"[AddMaOptimizationParameters] EMA: Short={emaShort}, Medium={emaMedium}, Long={emaLong}");

            // ✅ Добавляем параметры с ОТСОРТИРОВАННЫМИ значениями
            // SMA параметры
            AddParameter("SmaShort", "SMA короткий", smaShort, 5, 100, 5);
            AddParameter("SmaMedium", "SMA средний", smaMedium, 10, 200, 10);
            AddParameter("SmaLong", "SMA длинный", smaLong, 20, 500, 20);


            // ============================================================
            // ✅ ✅ ✅ ВАЖНО: Учитываем режим работы FilterSmaPeriod
            // ============================================================
            int filterSmaValue;
            int filterMin;
            int filterMax;

            if (maParams.UseManualFilterSma)
            {
                // ✅ Ручной режим - используем значение из настроек стратегии
                filterSmaValue = maParams.FilterSmaPeriod;
                Debug.WriteLine($"[AddMaOptimizationParameters] Ручной режим FilterSmaPeriod = {filterSmaValue} (из стратегии)");

                // Диапазон для оптимизации вокруг ручного значения
                filterMin = Math.Max(1, filterSmaValue - 20);
                filterMax = Math.Min(200, filterSmaValue + 20);

                // Если диапазон слишком мал - расширяем
                if (filterMax - filterMin < 10)
                {
                    filterMin = Math.Max(1, filterSmaValue - 15);
                    filterMax = Math.Min(200, filterSmaValue + 15);
                }
            }
            else
            {
                // ✅ Автоматический режим - вычисляем как средний SMA период
                filterSmaValue = smaMedium;
                Debug.WriteLine($"[AddMaOptimizationParameters] Автоматический режим FilterSmaPeriod = {filterSmaValue} (средний SMA)");

                // Диапазон для оптимизации вокруг среднего SMA
                filterMin = Math.Max(10, filterSmaValue - 30);
                filterMax = Math.Min(200, filterSmaValue + 30);
            }

            // ✅ Добавляем FilterSmaPeriod с правильным диапазоном
            AddParameter("FilterSmaPeriod", "Фильтр SMA", filterSmaValue, filterMin, filterMax, 5);
            Debug.WriteLine($"[AddMaOptimizationParameters] FilterSmaPeriod: значение={filterSmaValue}, диапазон={filterMin}..{filterMax}, режим={(maParams.UseManualFilterSma ? "РУЧНОЙ" : "АВТО")}");

            // EMA параметры
            AddParameter("EmaShort", "EMA короткий", emaShort, 5, 100, 5);
            AddParameter("EmaMedium", "EMA средний", emaMedium, 10, 200, 10);
            AddParameter("EmaLong", "EMA длинный", emaLong, 20, 300, 10);

            // Размер позиции
            AddParameter("PositionSizePercent", "Размер позиции (%)",
                maParams.PositionSizePercent, 1, 50, 1);

            // ATR параметры
            AddParameter("StopLossATRMultiplier", "Стоп-лосс (ATR множитель)",
                maParams.StopLossATRMultiplier, 0.5m, 5.0m, 0.25m);

            AddParameter("TakeProfitATRMultiplier", "Тейк-профит (ATR множитель)",
                maParams.TakeProfitATRMultiplier, 1.0m, 8.0m, 0.25m);

            AddParameter("TrailingStopATRMultiplier", "Трейлинг-стоп (ATR множитель)",
                maParams.TrailingStopATRMultiplier, 0.5m, 4.0m, 0.25m);


           


            Debug.WriteLine("[AddMaOptimizationParameters] КОНЕЦ");
        }

        /// <summary>
        /// Парсит строку с периодами в список целых чисел и СОРТИРУЕТ их
        /// </summary>
        private List<int> ParsePeriodsSorted(string periodsString)
        {
            if (string.IsNullOrEmpty(periodsString))
                return new List<int>();

            try
            {
                var result = periodsString
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.TryParse(p.Trim(), out var val) ? val : 0)
                    .Where(p => p > 0)
                    .OrderBy(p => p)
                    .ToList();

                Debug.WriteLine($"[ParsePeriodsSorted] '{periodsString}' -> [{string.Join(",", result)}]");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ParsePeriodsSorted] Ошибка: {ex.Message}");
                return new List<int>();
            }
        }


        /// <summary>
        /// Парсит строку с периодами в список целых чисел, СОХРАНЯЯ ПОРЯДОК
        /// </summary>
        private List<int> ParsePeriodsPreserveOrder(string periodsString)
        {
            if (string.IsNullOrEmpty(periodsString))
                return new List<int>();

            try
            {
                var result = periodsString
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.TryParse(p.Trim(), out var val) ? val : 0)
                    .Where(p => p > 0)
                    .ToList();

                return result;
            }
            catch
            {
                return new List<int>();
            }
        }

        private void AddRatingParameters()
        {
            Debug.WriteLine("[AddRatingParameters] НАЧАЛО");
            AddParameter("EntryThreshold", "Порог входа", 80, 60, 100, 1);
            AddParameter("MatchTolerance", "Допуск совпадения", 0.2m, 0.05m, 1, 0.05m);
            AddParameter("MinMatchPercentage", "Мин. % совпадений", 80, 60, 100, 1);
            AddParameter("PositionSizePercent", "Размер позиции (%)", 10, 1, 50, 1);
            Debug.WriteLine("[AddRatingParameters] КОНЕЦ");
        }

        private void AddParameter(string name, string displayName, decimal defaultValue, decimal minValue, decimal maxValue, decimal step)
        {
            Debug.WriteLine($"[AddParameter] name={name}, displayName={displayName}, defaultValue={defaultValue}, minValue={minValue}, maxValue={maxValue}, step={step}");
            var param = new OptimizationParameter
            {
                Name = name,
                DisplayName = displayName,
                CurrentValue = defaultValue,
                MinValue = minValue,
                MaxValue = maxValue,
                Step = step,
                IsSelected = false
            };
            Parameters.Add(param);
            _originalParameters[name] = defaultValue;
            Debug.WriteLine($"[AddParameter] Параметр {name} добавлен в коллекцию");
        }








        private void SubscribeToParameterChanges()
        {
            Debug.WriteLine($"[SubscribeToParameterChanges] НАЧАЛО. Параметров: {Parameters.Count}");
            foreach (var param in Parameters)
            {
                param.PropertyChanged += (s, e) =>
                {
                    Debug.WriteLine($"[SubscribeToParameterChanges] Параметр {param.Name} изменен: {e.PropertyName}");
                    if (e.PropertyName == nameof(OptimizationParameter.IsSelected) ||
                        e.PropertyName == nameof(OptimizationParameter.MinValue) ||
                        e.PropertyName == nameof(OptimizationParameter.MaxValue) ||
                        e.PropertyName == nameof(OptimizationParameter.Step))
                    {
                        Debug.WriteLine($"[SubscribeToParameterChanges] Пересчет комбинаций для {param.Name}");
                        UpdateTotalCombinations();
                        RefreshCommands();
                    }
                };
            }
            Debug.WriteLine("[SubscribeToParameterChanges] КОНЕЦ");
        }

        private void UpdateTotalCombinations()
        {
            Debug.WriteLine("[UpdateTotalCombinations] НАЧАЛО");
            try
            {
                var selectedParams = Parameters.Where(p => p.IsSelected).ToList();
                Debug.WriteLine($"[UpdateTotalCombinations] Выбрано параметров: {selectedParams.Count}");

                if (!selectedParams.Any())
                {
                    TotalCombinations = 0;
                    Debug.WriteLine("[UpdateTotalCombinations] Нет выбранных параметров, TotalCombinations=0");
                    return;
                }

                long total = 1;
                foreach (var param in selectedParams)
                {
                    var count = param.GetValueCount();
                    Debug.WriteLine($"[UpdateTotalCombinations] Параметр {param.Name}: Min={param.MinValue}, Max={param.MaxValue}, Step={param.Step}, Count={count}");

                    if (total > long.MaxValue / count)
                    {
                        Debug.WriteLine($"[UpdateTotalCombinations] ⚠️ ПЕРЕПОЛНЕНИЕ! total={total}, count={count}");
                        total = long.MaxValue;
                        break;
                    }

                    total *= count;
                }

                TotalCombinations = (int)Math.Min(total, int.MaxValue);
                Debug.WriteLine($"[UpdateTotalCombinations] Всего комбинаций: {TotalCombinations}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateTotalCombinations] ОШИБКА: {ex.Message}");
                Debug.WriteLine($"[UpdateTotalCombinations] StackTrace: {ex.StackTrace}");
                TotalCombinations = 0;
            }
            Debug.WriteLine("[UpdateTotalCombinations] КОНЕЦ");
        }


        /// <summary>
        /// Парсит строку с периодами в список целых чисел (СОРТИРУЕТ)
        /// </summary>
        private List<int> ParsePeriods(string periodsString)
        {
            if (string.IsNullOrEmpty(periodsString))
                return new List<int>();

            try
            {
                var result = periodsString
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.TryParse(p.Trim(), out var val) ? val : 0)
                    .Where(p => p > 0)
                    .OrderBy(p => p)
                    .ToList();

                return result;
            }
            catch
            {
                return new List<int>();
            }
        }
        #endregion

        #region Управление периодом

        private void SetPeriod(string periodKey)
        {
            int days;
            string displayName;

            switch (periodKey)
            {
                case "30":
                    days = 30;
                    displayName = "30 дней";
                    break;
                case "90":
                    days = 90;
                    displayName = "90 дней";
                    break;
                case "120":
                    days = 120;
                    displayName = "120 дней";
                    break;
                case "365":
                    days = 365;
                    displayName = "365 дней";
                    break;
                case "2 года":
                    days = 730;
                    displayName = "2 года";
                    break;
                case "3 года":
                    days = 1095;
                    displayName = "3 года";
                    break;
                default:
                    days = 365;
                    displayName = "365 дней";
                    break;
            }

            SelectedHistoryPeriod = days;
            SelectedPeriodDisplay = displayName;
            PeriodStart = DateTime.Now.AddDays(-days);
            PeriodEnd = DateTime.Now;

            Debug.WriteLine($"[OptimizationViewModel] Установлен период: {displayName} ({days} дней)");
        }

        private void OnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectedPeriodDisplay))
            {
                // Обновляем период при изменении выбора в комбобоксе
                UpdatePeriodFromDisplay(SelectedPeriodDisplay);
            }
        }

        // метод для обновления периода из отображаемого имени:
        private void UpdatePeriodFromDisplay(string displayName)
        {
            int days;

            switch (displayName)
            {
                case "30 дней":
                    days = 30;
                    break;
                case "90 дней":
                    days = 90;
                    break;
                case "120 дней":
                    days = 120;
                    break;
                case "365 дней":
                    days = 365;
                    break;
                case "2 года":
                    days = 730;
                    break;
                case "3 года":
                    days = 1095;
                    break;
                default:
                    days = 365;
                    break;
            }

            SelectedHistoryPeriod = days;
            PeriodStart = DateTime.Now.AddDays(-days);
            PeriodEnd = DateTime.Now;

            Debug.WriteLine($"[OptimizationViewModel] Период обновлен: {displayName} ({days} дней)");
        }
        #endregion

        #region Подготовка данных (ОДИН РАЗ!)

        /// <summary>
        /// Подготавливает все данные для оптимизации (загружается 1 раз)
        /// </summary>
        private async Task PrepareDataAsync()
        {
            Debug.WriteLine("[PrepareDataAsync] ========== НАЧАЛО ==========");
            Debug.WriteLine($"[PrepareDataAsync] _dataPrepared={_dataPrepared}");
            Debug.WriteLine($"[PrepareDataAsync] _isLoadingHistory={_isLoadingHistory}");

            if (_dataPrepared)
            {
                if (_dataCache.LoadTime != DateTime.MinValue)
                {
                    var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    "Данные уже загружены. Перезагрузить с новым периодом?",
                    "Подтверждение",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question));

                    if (result != MessageBoxResult.Yes)
                    {
                        Debug.WriteLine("[PrepareDataAsync] Перезагрузка отменена пользователем");
                        RefreshCommands();
                        return;
                    }
                }

                _dataPrepared = false;
                _dataCache = new OptimizationDataCache();
                _dataCache.PairsModels = new Dictionary<int, PairsModel>();
            }

            if (_isLoadingHistory)
            {
                Debug.WriteLine("[PrepareDataAsync] Загрузка уже выполняется");
                return;
            }

            try
            {
                _isLoadingHistory = true;
                IsProgressVisible = true;
                ProgressValue = 0;
                ProgressMaximum = 100;
                LoadingStatus = "Подготовка данных...";

                Debug.WriteLine("[PrepareDataAsync] НАЧАЛО подготовки данных");

                var instrument = _strategyViewModel.Instrument;

                // ✅ СОХРАНЯЕМ LOT SIZE ИЗ ИНСТРУМЕНТА
                _dataCache.LotSize = instrument.LotSize > 0 ? instrument.LotSize : 1m;

                Debug.WriteLine($"[PrepareDataAsync] Сохранен LotSize={_dataCache.LotSize} для {instrument.Ticker}");

                var timeframe = _strategyViewModel.SelectedTimeFrame.Value;
                int daysToLoad = SelectedHistoryPeriod;

                Debug.WriteLine($"[PrepareDataAsync] instrument={instrument.Ticker}, timeframe={timeframe}, daysToLoad={daysToLoad}");

                // ✅ Загрузка основного инструмента (B) - шаг 1-40%
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressText = $"Загрузка {instrument.Ticker}...";
                    ProgressValue = 1;
                    LoadingStatus = $"Загрузка {instrument.Ticker}...";
                });

                var candlesB = await LoadCandlesWithCacheCheckAsync(
                    instrument.Ticker,
                    instrument.Uid,
                    timeframe,
                    daysToLoad);

                // Обновляем прогресс до 40% после загрузки B
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressText = $"✅ Загружено {candlesB?.Count ?? 0} свечей для {instrument.Ticker}";
                    ProgressValue = 40;
                    LoadingStatus = ProgressText;
                });

                Debug.WriteLine($"[PrepareDataAsync] Загружено {candlesB?.Count ?? 0} свечей для {instrument.Ticker}");
                _dataCache.Candles[instrument.Ticker] = candlesB;
                _dataCache.CandlesB = candlesB;

                // ✅ Для парной стратегии загружаем свечи для инструмента A
                if (_strategyType == "PairsTrading")
                {
                    // ✅ ПОЛУЧАЕМ ТИКЕР ИНСТРУМЕНТА A ИЗ ПАРАМЕТРОВ СТРАТЕГИИ
                    string tickerA = _strategyViewModel.PairsStrategy?.Parameters?.FirstInstrumentTicker ?? "IMOEXF";
                    string uidA = _strategyViewModel.PairsStrategy?.Parameters?.FirstInstrumentUid;

                    Debug.WriteLine($"[PrepareDataAsync] Инструмент A (из параметров стратегии): {tickerA}, UID: {uidA ?? "не задан"}");

                    // Если UID не задан, попробуем найти инструмент
                    if (string.IsNullOrEmpty(uidA))
                    {
                        var instruments = await _provider.GetInstrumentsAsync();
                        var instA = instruments?.FirstOrDefault(i => i.Ticker == tickerA);
                        if (instA != null)
                        {
                            uidA = instA.Uid;
                            Debug.WriteLine($"[PrepareDataAsync] Найден UID для {tickerA}: {uidA}");
                        }
                    }

                    // Загрузка A - шаг 41-70%
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressText = $"Загрузка {tickerA}...";
                        ProgressValue = 41;
                        LoadingStatus = $"Загрузка {tickerA}...";
                    });

                    var candlesA = await LoadCandlesWithCacheCheckAsync(
                         tickerA,
                         uidA,
                         timeframe,
                         daysToLoad);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressText = $"✅ Загружено {candlesA?.Count ?? 0} свечей для {tickerA}";
                        ProgressValue = 70;
                        LoadingStatus = ProgressText;
                    });

                    Debug.WriteLine($"[PrepareDataAsync] Загружено {candlesA?.Count ?? 0} свечей для {tickerA}");
                    _dataCache.Candles[tickerA] = candlesA;
                    _dataCache.CandlesA = candlesA;

                    // ✅ Сохраняем тикеры в кэш для использования в бэктесте
                    _dataCache.InstrumentATicker = tickerA;
                    _dataCache.InstrumentBTicker = instrument.Ticker;

                    // ✅ Выравниваем данные - шаг 71-75%
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressText = "Выравнивание данных...";
                        ProgressValue = 71;
                        LoadingStatus = "Выравнивание данных...";
                    });

                    Debug.WriteLine("[PrepareDataAsync] Выравнивание данных...");
                    _dataCache.AlignedData = AlignCandles(candlesA, candlesB);
                    Debug.WriteLine($"[PrepareDataAsync] Выровненных точек: {_dataCache.AlignedData?.Count ?? 0}");

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressValue = 75;
                        LoadingStatus = $"Выровнено {_dataCache.AlignedData?.Count ?? 0} точек";
                    });

                    // ✅ Строим модели - шаг 76-95%
                    if (_dataCache.AlignedData.Count >= 50)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            ProgressText = "Построение моделей...";
                            ProgressValue = 76;
                            LoadingStatus = "Построение моделей...";
                        });

                        Debug.WriteLine("[PrepareDataAsync] Запуск построения моделей");
                        await BuildAllModelsAsync();
                    }
                    else
                    {
                        Debug.WriteLine($"[PrepareDataAsync] ⚠️ Недостаточно выровненных данных: {_dataCache.AlignedData.Count}");
                        LoadingStatus = $"⚠️ Недостаточно данных: {_dataCache.AlignedData.Count} точек";
                        _dataPrepared = false;
                        IsProgressVisible = false;
                        _isLoadingHistory = false;
                        RefreshCommands();
                        return;
                    }
                }


                // ✅ ДЛЯ MA СТРАТЕГИИ - НЕ ТРЕБУЕТСЯ ДОПОЛНИТЕЛЬНАЯ ЗАГРУЗКА
                else if (_strategyType == "MA")
                {
                    Debug.WriteLine("[PrepareDataAsync] MA стратегия: данные по одному инструменту подготовлены");

                    // Для MA стратегии просто копируем данные в AlignedData
                    // Это упрощает работу бэктест-движка
                    _dataCache.AlignedData = candlesB.Select(c => new AlignedCandleData
                    {
                        Time = c.Time,
                        PriceA = c.Close,
                        PriceB = c.Close
                    }).ToList();

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressValue = 85;
                        LoadingStatus = $"Данные для MA подготовлены: {candlesB?.Count ?? 0} свечей";
                    });

                    Debug.WriteLine($"[PrepareDataAsync] MA: AlignedData содержит {_dataCache.AlignedData?.Count ?? 0} точек");

                    // ✅ Сохраняем тикер инструмента в кэш для бэктеста
                    _dataCache.InstrumentBTicker = instrument.Ticker;
                }
                else
                {
                    // ✅ Для остальных стратегий (RSI, Rating) - данные по одному инструменту
                    _dataCache.AlignedData = candlesB.Select(c => new AlignedCandleData
                    {
                        Time = c.Time,
                        PriceA = c.Close,
                        PriceB = c.Close
                    }).ToList();

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressValue = 85;
                        LoadingStatus = $"Данные подготовлены: {candlesB?.Count ?? 0} свечей";
                    });

                    Debug.WriteLine($"[PrepareDataAsync] {_strategyType}: AlignedData содержит {_dataCache.AlignedData?.Count ?? 0} точек");
                    _dataCache.InstrumentBTicker = instrument.Ticker;
                }

                // ✅ Проверяем валидность данных для всех стратегий
                bool dataValid = true;
                string errorMessage = "";

                if (_dataCache.AlignedData == null || _dataCache.AlignedData.Count < 20)
                {
                    dataValid = false;
                    errorMessage = $"Недостаточно данных: {_dataCache.AlignedData?.Count ?? 0} точек (нужно минимум 20)";
                }

                // ✅ Для PairsTrading дополнительно проверяем наличие моделей
                if (_strategyType == "PairsTrading")
                {
                    if (_dataCache.PairsModels == null || !_dataCache.PairsModels.Any())
                    {
                        dataValid = false;
                        errorMessage = "Не построено ни одной модели для PairsTrading";
                        Debug.WriteLine("[PrepareDataAsync] ⚠️ НЕ ПОСТРОЕНО НИ ОДНОЙ МОДЕЛИ!");
                    }
                }

                if (!dataValid)
                {
                    Debug.WriteLine($"[PrepareDataAsync] ❌ Данные невалидны: {errorMessage}");
                    LoadingStatus = $"⚠️ {errorMessage}";
                    _dataPrepared = false;
                    IsProgressVisible = false;
                    _isLoadingHistory = false;
                    RefreshCommands();

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show(
                            $"Не удалось подготовить данные:\n\n{errorMessage}",
                            "Ошибка подготовки данных",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    });
                    return;
                }











                // ✅ ФИНАЛИЗАЦИЯ
                _dataCache.LoadTime = DateTime.Now;
                _dataCache.IsLoaded = true;
                _dataPrepared = true;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressValue = 100;

                    // Формируем информативное сообщение о загруженных данных
                    string summary = $"✅ Данные подготовлены: {_dataCache.Candles.Sum(c => c.Value?.Count ?? 0)} свечей";

                    if (_strategyType == "PairsTrading")
                    {
                        summary += $", моделей: {_dataCache.PairsModels.Count}";
                    }

                    LoadingStatus = summary;
                    ProgressText = LoadingStatus;
                });

                Debug.WriteLine("[PrepareDataAsync] Подготовка данных ЗАВЕРШЕНА УСПЕШНО");

                // Небольшая задержка перед скрытием прогресса
                await Task.Delay(500);
                IsProgressVisible = false;
                _isLoadingHistory = false;
                RefreshCommands();

                // ✅ Показываем информационное сообщение пользователю
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    string message = $"Данные успешно подготовлены!\n\n" +
                        $"Стратегия: {_strategyType}\n" +
                        $"Период: {SelectedHistoryPeriod} дней\n" +
                        $"Свечей: {_dataCache.Candles.Sum(c => c.Value?.Count ?? 0)}\n" +
                        $"Выровненных точек: {_dataCache.AlignedData?.Count ?? 0}";

                    if (_strategyType == "PairsTrading")
                    {
                        message += $"\nМоделей: {_dataCache.PairsModels.Count}";
                        message += $"\nИнструмент A: {_dataCache.InstrumentATicker ?? "не указан"}";
                        message += $"\nИнструмент B: {_dataCache.InstrumentBTicker ?? "не указан"}";
                    }
                    else
                    {
                        message += $"\nИнструмент: {_dataCache.InstrumentBTicker ?? instrument.Ticker}";
                    }

                    MessageBox.Show(
                        message,
                        "Готово",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PrepareDataAsync] ОШИБКА: {ex.Message}");
                Debug.WriteLine($"[PrepareDataAsync] StackTrace: {ex.StackTrace}");
                _logger?.LogError(ex, "Ошибка подготовки данных");
                LoadingStatus = $"❌ Ошибка: {ex.Message}";
                IsProgressVisible = false;
                _isLoadingHistory = false;
                _dataPrepared = false;
                RefreshCommands();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Ошибка подготовки данных:\n\n{ex.Message}",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            }

            Debug.WriteLine("[PrepareDataAsync] ========== КОНЕЦ ==========");
        }

        /// <summary>
        /// Загружает свечи с проверкой БД - догружает только недостающие
        /// С ОБНОВЛЕНИЕМ ПРОГРЕССА ЧЕРЕЗ CALLBACK
        /// </summary>
        private async Task<List<Candle>> LoadCandlesWithCacheCheckAsync(string ticker, string uid, string timeframe, int daysToLoad)
        {
            Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] НАЧАЛО: ticker={ticker}, daysToLoad={daysToLoad}");

            try
            {
                if (string.IsNullOrEmpty(uid))
                {
                    Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] UID пуст, ищем инструмент {ticker}");
                    var instruments = await _provider.GetInstrumentsAsync();
                    var instrument = instruments?.FirstOrDefault(i => i.Ticker == ticker);
                    if (instrument != null)
                    {
                        uid = instrument.Uid;
                        Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Найден UID для {ticker}: {uid}");
                    }
                    else
                    {
                        Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Инструмент {ticker} не найден");
                        return new List<Candle>();
                    }
                }

                if (string.IsNullOrEmpty(uid))
                {
                    Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Не найден UID для {ticker}");
                    return new List<Candle>();
                }

                // ✅ Пытаемся получить данные из БД
                var existingCandles = await GetCandlesFromDatabaseAsync(ticker, uid, timeframe);
                Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] В БД найдено {existingCandles?.Count ?? 0} свечей для {ticker}");

                // Определяем, сколько дней нужно загрузить
                DateTime endDate = DateTime.UtcNow;
                DateTime requiredStartDate = endDate.AddDays(-daysToLoad);

                List<Candle> resultCandles = new List<Candle>();

                if (existingCandles != null && existingCandles.Any())
                {
                    // Сортируем по времени
                    var sorted = existingCandles.OrderBy(c => c.Time).ToList();
                    var oldestDate = sorted.First().Time;
                    var latestDate = sorted.Last().Time;

                    Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Диапазон в БД: {oldestDate:yyyy-MM-dd} - {latestDate:yyyy-MM-dd}");

                    // Проверяем, достаточно ли данных и актуальны ли они
                    bool hasEnoughData = oldestDate <= requiredStartDate;
                    bool isUpToDate = (endDate - latestDate).TotalHours < 1; // последняя свеча не старше 1 часа

                    if (hasEnoughData && isUpToDate)
                    {
                        // Данных достаточно - берем из БД
                        Debug.WriteLine("[LoadCandlesWithCacheCheckAsync] Данные в БД актуальны и достаточны");
                        resultCandles = sorted
                            .Where(c => c.Time >= requiredStartDate)
                            .ToList();

                        // ✅ ОБНОВЛЯЕМ ПРОГРЕСС - данные уже есть в БД
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            ProgressText = $"✅ Данные для {ticker} уже загружены ({resultCandles.Count} свечей)";
                            LoadingStatus = ProgressText;
                            IsProgressVisible = true;
                        });
                    }
                    else
                    {
                        // Нужно догрузить
                        DateTime loadStartDate;
                        string loadMode;

                        if (!hasEnoughData)
                        {
                            // Не хватает истории - загружаем с requiredStartDate
                            loadStartDate = requiredStartDate;
                            loadMode = "загрузка истории";
                            Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Не хватает истории, загружаем с {loadStartDate:yyyy-MM-dd}");
                        }
                        else
                        {
                            // Не хватает актуальных данных - загружаем с latestDate
                            loadStartDate = latestDate.AddMinutes(-5); // небольшой перекрытие
                            loadMode = "дозагрузка";
                            Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Догружаем с {loadStartDate:yyyy-MM-dd HH:mm}");
                        }


                        // ✅ УСТАНАВЛИВАЕМ CALLBACK ДЛЯ ОБНОВЛЕНИЯ ПРОГРЕССА
                        var progressCallback = new TinkoffApiService.ProgressCallback((message, current, total) =>
                        {
                            // Вычисляем общий прогресс для этого инструмента (от 1% до 40% для основного, 41%-70% для парного)
                            // Это будет обработано в PrepareDataAsync
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                ProgressText = message;
                                LoadingStatus = message;
                                IsProgressVisible = true;
                            });
                        });

                        // ✅ УСТАНАВЛИВАЕМ CALLBACK В ПРОВАЙДЕР
                        if (_provider is TinkoffApiService tinkoffProvider)
                        {
                            tinkoffProvider.SetProgressCallback(progressCallback);
                        }




                        try
                        {
                            // ✅ ЗАГРУЖАЕМ ДАННЫЕ (ВНУТРИ ПРОВАЙДЕРА БУДУТ ОБНОВЛЕНИЯ ПРОГРЕССА)
                            var newCandles = await _provider.GetHistoricalDataAsync(
                                ticker, uid, timeframe, loadStartDate, endDate);

                            Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Загружено {newCandles?.Count ?? 0} новых свечей");

                            if (newCandles != null && newCandles.Any())
                            {
                                // Объединяем с существующими, удаляем дубликаты
                                var combined = sorted.Concat(newCandles)
                                    .GroupBy(c => new DateTime(c.Time.Year, c.Time.Month, c.Time.Day, c.Time.Hour, c.Time.Minute, 0, c.Time.Kind))
                                    .Select(g => g.OrderByDescending(c => c.Time).First())
                                    .ToList();

                                resultCandles = combined
                                    .Where(c => c.Time >= requiredStartDate)
                                    .OrderBy(c => c.Time)
                                    .ToList();

                                // ✅ Сохраняем обновленные данные в БД
                                await SaveCandlesToDatabaseAsync(ticker, uid, timeframe, combined);

                                // ✅ ОБНОВЛЯЕМ ПРОГРЕСС ПОСЛЕ СОХРАНЕНИЯ
                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    ProgressText = $"✅ Сохранено {resultCandles.Count} свечей для {ticker}";
                                    LoadingStatus = ProgressText;
                                    IsProgressVisible = true;
                                });
                            }
                            else
                            {
                                resultCandles = sorted
                                    .Where(c => c.Time >= requiredStartDate)
                                    .ToList();

                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    ProgressText = $"⚠️ Используем существующие данные для {ticker} ({resultCandles.Count} свечей)";
                                    LoadingStatus = ProgressText;
                                });
                            }
                        }
                        finally
                        {
                            // ✅ СБРАСЫВАЕМ CALLBACK ПОСЛЕ ЗАГРУЗКИ
                            //if (_provider is TinkoffApiService tinkoffProvider)
                            //{
                            //    tinkoffProvider.SetProgressCallback(null);
                            //}

                            _provider.SetProgressCallback(null);
                        }
                    }
                }
                else
                {
                    // В БД нет данных - загружаем все
                    Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] В БД нет данных для {ticker}, загружаем все");

                    // ✅ УСТАНАВЛИВАЕМ CALLBACK ДЛЯ ОБНОВЛЕНИЯ ПРОГРЕССА
                    var progressCallback = new TinkoffApiService.ProgressCallback((message, current, total) =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProgressText = message;
                            LoadingStatus = message;
                            IsProgressVisible = true;
                        });
                    });

                    if (_provider is TinkoffApiService tinkoffProvider)
                    {
                        tinkoffProvider.SetProgressCallback(progressCallback);
                    }








                    try
                    {
                        var newCandles = await _provider.GetHistoricalDataAsync(
                            ticker, uid, timeframe, requiredStartDate, endDate);

                        if (newCandles != null && newCandles.Any())
                        {
                            resultCandles = newCandles.ToList();
                            await SaveCandlesToDatabaseAsync(ticker, uid, timeframe, resultCandles);

                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                ProgressText = $"✅ Сохранено {resultCandles.Count} свечей для {ticker}";
                                LoadingStatus = ProgressText;
                            });
                        }
                    }
                    finally
                    {
                        //if (_provider is TinkoffApiService tinkoffProvider)
                        //{
                        //    tinkoffProvider.SetProgressCallback(null);
                        //}
                        _provider.SetProgressCallback(null);
                    }
                }

                // ✅ Обработка загруженных свечей
                if (resultCandles.Any())
                {
                    var uniqueCandles = resultCandles
                        .GroupBy(c => new DateTime(
                            c.Time.Year,
                            c.Time.Month,
                            c.Time.Day,
                            c.Time.Hour,
                            c.Time.Minute,
                            0,
                            c.Time.Kind))
                        .Select(g => g.Last())
                        .ToList();

                    Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] После удаления дубликатов: {uniqueCandles.Count} свечей (было {resultCandles.Count})");

                    foreach (var candle in uniqueCandles)
                    {
                        candle.Time = candle.Time.ToLocalTime();
                        candle.Ticker = ticker;
                        candle.Timeframe = timeframe;
                    }

                    Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Итоговое количество свечей для {ticker}: {uniqueCandles.Count}");

                    // ✅ ФИНАЛЬНОЕ ОБНОВЛЕНИЕ ПРОГРЕССА
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressText = $"✅ Готово: {uniqueCandles.Count} свечей для {ticker}";
                        LoadingStatus = ProgressText;
                        IsProgressVisible = true;
                    });

                    return uniqueCandles;
                }

                Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] Нет данных для {ticker}");
                return new List<Candle>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] ОШИБКА для {ticker}: {ex.Message}");
                Debug.WriteLine($"[LoadCandlesWithCacheCheckAsync] StackTrace: {ex.StackTrace}");
                return new List<Candle>();
            }
        }


        /// <summary>
        /// Получает свечи из БД через DatabaseManager
        /// </summary>
        private async Task<List<Candle>> GetCandlesFromDatabaseAsync(string ticker, string uid, string timeframe)
        {
            Debug.WriteLine($"[GetCandlesFromDatabaseAsync] НАЧАЛО: ticker={ticker}, timeframe={timeframe}");

            try
            {
                var candles = new List<Candle>();

                // ✅ Используем DatabaseManager для получения открытого соединения
                using (var connection = await DatabaseManager.GetConnectionAsync())
                {
                    // ✅ Убеждаемся, что соединение открыто
                    if (connection.State != System.Data.ConnectionState.Open)
                    {
                        await connection.OpenAsync();
                    }

                    // ✅ Проверяем существование таблицы Candles
                    var checkTableCmd = new Microsoft.Data.Sqlite.SqliteCommand(
                        "SELECT name FROM sqlite_master WHERE type='table' AND name='Candles'",
                        connection);
                    var tableExists = await checkTableCmd.ExecuteScalarAsync();

                    if (tableExists == null)
                    {
                        Debug.WriteLine("[GetCandlesFromDatabaseAsync] Таблица Candles не существует, создаем...");

                        string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS Candles (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Time DATETIME NOT NULL,
                        Open REAL NOT NULL,
                        High REAL NOT NULL,
                        Low REAL NOT NULL,
                        Close REAL NOT NULL,
                        Volume INTEGER NOT NULL,
                        IsClosed INTEGER NOT NULL DEFAULT 1,
                        Ticker TEXT NOT NULL,
                        Timeframe TEXT NOT NULL,
                        InstrumentUid TEXT,
                        UNIQUE(Time, Ticker, Timeframe)
                    );
                    
                    CREATE INDEX IF NOT EXISTS idx_candles_ticker_timeframe 
                    ON Candles(Ticker, Timeframe);
                    
                    CREATE INDEX IF NOT EXISTS idx_candles_time 
                    ON Candles(Time DESC);";

                        using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(createTableQuery, connection))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }
                        Debug.WriteLine("[GetCandlesFromDatabaseAsync] Таблица Candles создана");
                    }

                    string query = @"
                SELECT Time, Open, High, Low, Close, Volume, IsClosed, Ticker, Timeframe, InstrumentUid
                FROM Candles
                WHERE Ticker = @ticker AND Timeframe = @timeframe
                ORDER BY Time ASC";

                    using (var command = new Microsoft.Data.Sqlite.SqliteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@ticker", ticker);
                        command.Parameters.AddWithValue("@timeframe", timeframe);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var candle = new Candle
                                {
                                    Time = reader.GetDateTime(0),
                                    Open = (decimal)reader.GetDouble(1),
                                    High = (decimal)reader.GetDouble(2),
                                    Low = (decimal)reader.GetDouble(3),
                                    Close = (decimal)reader.GetDouble(4),
                                    Volume = (decimal)reader.GetInt64(5),
                                    IsClosed = reader.GetBoolean(6),
                                    Ticker = reader.GetString(7),
                                    Timeframe = reader.GetString(8),
                                    InstrumentUid = reader.IsDBNull(9) ? null : reader.GetString(9)
                                };
                                candles.Add(candle);
                            }
                        }
                    }
                }

                Debug.WriteLine($"[GetCandlesFromDatabaseAsync] Получено {candles.Count} свечей для {ticker}");
                return candles;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetCandlesFromDatabaseAsync] ОШИБКА: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Сохраняет свечи в БД через DatabaseManager
        /// </summary>
        private async Task SaveCandlesToDatabaseAsync(string ticker, string uid, string timeframe, List<Candle> candles)
        {
            Debug.WriteLine($"[SaveCandlesToDatabaseAsync] НАЧАЛО: ticker={ticker}, candles={candles?.Count ?? 0}");

            try
            {
                if (candles == null || !candles.Any())
                {
                    Debug.WriteLine("[SaveCandlesToDatabaseAsync] Нет свечей для сохранения");
                    return;
                }

                // ✅ Используем DatabaseManager для получения открытого соединения
                using (var connection = await DatabaseManager.GetConnectionAsync())
                {
                    // ✅ Убеждаемся, что соединение открыто
                    if (connection.State != System.Data.ConnectionState.Open)
                    {
                        await connection.OpenAsync();
                    }

                    // Создаем таблицу если её нет
                    string createTableQuery = @"
                CREATE TABLE IF NOT EXISTS Candles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Time DATETIME NOT NULL,
                    Open REAL NOT NULL,
                    High REAL NOT NULL,
                    Low REAL NOT NULL,
                    Close REAL NOT NULL,
                    Volume INTEGER NOT NULL,
                    IsClosed INTEGER NOT NULL DEFAULT 1,
                    Ticker TEXT NOT NULL,
                    Timeframe TEXT NOT NULL,
                    InstrumentUid TEXT,
                    UNIQUE(Time, Ticker, Timeframe)
                );
                
                CREATE INDEX IF NOT EXISTS idx_candles_ticker_timeframe 
                ON Candles(Ticker, Timeframe);
                
                CREATE INDEX IF NOT EXISTS idx_candles_time 
                ON Candles(Time DESC);";

                    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(createTableQuery, connection))
                    {
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Используем транзакцию для массовой вставки
                    using (var transaction = connection.BeginTransaction())
                    {
                        string insertQuery = @"
                    INSERT OR REPLACE INTO Candles 
                    (Time, Open, High, Low, Close, Volume, IsClosed, Ticker, Timeframe, InstrumentUid)
                    VALUES (@time, @open, @high, @low, @close, @volume, @isClosed, @ticker, @timeframe, @instrumentUid)";

                        int saved = 0;
                        foreach (var candle in candles)
                        {
                            using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(insertQuery, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@time", candle.Time);
                                cmd.Parameters.AddWithValue("@open", (double)candle.Open);
                                cmd.Parameters.AddWithValue("@high", (double)candle.High);
                                cmd.Parameters.AddWithValue("@low", (double)candle.Low);
                                cmd.Parameters.AddWithValue("@close", (double)candle.Close);
                                cmd.Parameters.AddWithValue("@volume", (long)candle.Volume);
                                cmd.Parameters.AddWithValue("@isClosed", candle.IsClosed ? 1 : 0);
                                cmd.Parameters.AddWithValue("@ticker", candle.Ticker ?? ticker);
                                cmd.Parameters.AddWithValue("@timeframe", candle.Timeframe ?? timeframe);
                                cmd.Parameters.AddWithValue("@instrumentUid", uid ?? (object)DBNull.Value);

                                await cmd.ExecuteNonQueryAsync();
                                saved++;
                            }
                        }

                        transaction.Commit();
                        Debug.WriteLine($"[SaveCandlesToDatabaseAsync] Сохранено {saved} свечей для {ticker}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveCandlesToDatabaseAsync] ОШИБКА: {ex.Message}");
                Debug.WriteLine($"[SaveCandlesToDatabaseAsync] StackTrace: {ex.StackTrace}");
            }
        }















        /// <summary>
        /// Загружает свечи для инструмента
        /// </summary>
        private async Task<List<Candle>> LoadCandlesForInstrumentAsync(string ticker, string uid, string timeframe, int daysToLoad)
        {
            Debug.WriteLine($"[LoadCandlesForInstrumentAsync] НАЧАЛО: ticker={ticker}, uid={uid}, timeframe={timeframe}, daysToLoad={daysToLoad}");
            try
            {
                if (string.IsNullOrEmpty(uid))
                {
                    Debug.WriteLine($"[LoadCandlesForInstrumentAsync] UID пуст, ищем инструмент {ticker}");
                    var instruments = await _provider.GetInstrumentsAsync();
                    var instrument = instruments?.FirstOrDefault(i => i.Ticker == ticker);
                    if (instrument != null)
                    {
                        uid = instrument.Uid;
                        Debug.WriteLine($"[LoadCandlesForInstrumentAsync] Найден UID для {ticker}: {uid}");
                    }
                    else
                    {
                        Debug.WriteLine($"[LoadCandlesForInstrumentAsync] Инструмент {ticker} не найден");
                    }
                }

                if (string.IsNullOrEmpty(uid))
                {
                    Debug.WriteLine($"[LoadCandlesForInstrumentAsync] Не найден UID для {ticker}");
                    return new List<Candle>();
                }

                var endDate = DateTime.UtcNow;
                var startDate = endDate.AddDays(-daysToLoad);
                Debug.WriteLine($"[LoadCandlesForInstrumentAsync] startDate={startDate:yyyy-MM-dd HH:mm:ss}, endDate={endDate:yyyy-MM-dd HH:mm:ss}");

                var candles = await _provider.GetHistoricalDataAsync(
                    ticker, uid, timeframe, startDate, endDate);

                Debug.WriteLine($"[LoadCandlesForInstrumentAsync] Получено {candles?.Count ?? 0} свечей");

                if (candles != null && candles.Any())
                {
                    // ✅ Убираем дубликаты по времени
                    var uniqueCandles = candles
                        .GroupBy(c => new DateTime(
                            c.Time.Year,
                            c.Time.Month,
                            c.Time.Day,
                            c.Time.Hour,
                            c.Time.Minute,
                            0,
                            c.Time.Kind))
                        .Select(g => g.Last())
                        .ToList();

                    Debug.WriteLine($"[LoadCandlesForInstrumentAsync] После удаления дубликатов: {uniqueCandles.Count} свечей (было {candles.Count})");

                    foreach (var candle in uniqueCandles)
                    {
                        candle.Time = candle.Time.ToLocalTime();
                        candle.Ticker = ticker;
                        candle.Timeframe = timeframe;
                    }

                    Debug.WriteLine($"[LoadCandlesForInstrumentAsync] Загружено {uniqueCandles.Count} свечей для {ticker}");
                    return uniqueCandles;
                }

                Debug.WriteLine($"[LoadCandlesForInstrumentAsync] Нет данных для {ticker}");
                return new List<Candle>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadCandlesForInstrumentAsync] ОШИБКА для {ticker}: {ex.Message}");
                Debug.WriteLine($"[LoadCandlesForInstrumentAsync] StackTrace: {ex.StackTrace}");
                return new List<Candle>();
            }
        }

        /// <summary>
        /// Выравнивает свечи по времени с группировкой по минутам
        /// </summary>
        private List<AlignedCandleData> AlignCandles(List<Candle> candlesA, List<Candle> candlesB)
        {
            Debug.WriteLine($"[AlignCandles] НАЧАЛО: candlesA={candlesA?.Count ?? 0}, candlesB={candlesB?.Count ?? 0}");
            var result = new List<AlignedCandleData>();

            if (candlesA == null || candlesB == null || !candlesA.Any() || !candlesB.Any())
            {
                Debug.WriteLine("[AlignCandles] Нет данных для выравнивания");
                return result;
            }

            // ✅ Группируем свечи B по времени (с округлением до минут)
            var dictB = new Dictionary<DateTime, decimal>();
            Debug.WriteLine($"[AlignCandles] Группировка свечей B...");
            int dictBCount = 0;

            foreach (var candle in candlesB)
            {
                // Округляем время до минут (убираем миллисекунды и секунды)
                var key = new DateTime(
                    candle.Time.Year,
                    candle.Time.Month,
                    candle.Time.Day,
                    candle.Time.Hour,
                    candle.Time.Minute,
                    0,
                    candle.Time.Kind);

                // Если ключ уже существует, берем последнее значение (самое свежее)
                dictB[key] = candle.Close;
                dictBCount++;
            }
            Debug.WriteLine($"[AlignCandles] Сгруппировано {dictBCount} свечей B, уникальных ключей: {dictB.Count}");

            Debug.WriteLine($"[AlignCandles] Поиск совпадений со свечами A...");
            int matchedCount = 0;
            foreach (var ca in candlesA)
            {
                // Округляем время свечи A так же
                var keyA = new DateTime(
                   ca.Time.Year,
                   ca.Time.Month,
                   ca.Time.Day,
                   ca.Time.Hour,
                   ca.Time.Minute,
                   0,
                   ca.Time.Kind);

                if (dictB.TryGetValue(keyA, out var priceB))
                {
                    result.Add(new AlignedCandleData
                    {
                        Time = keyA,
                        PriceA = ca.Close,
                        PriceB = priceB
                    });
                    matchedCount++;
                }
            }
            Debug.WriteLine($"[AlignCandles] Найдено совпадений: {matchedCount} из {candlesA.Count}");

            // ✅ Убираем дубликаты по времени (оставляем последний)
            var uniqueResult = result
                .GroupBy(d => d.Time)
                .Select(g => g.Last())
                .OrderBy(d => d.Time)
                .ToList();

            Debug.WriteLine($"[AlignCandles] Всего точек: {result.Count}, уникальных: {uniqueResult.Count}");

            Debug.WriteLine("[AlignCandles] КОНЕЦ");
            return uniqueResult;
        }

        /// <summary>
        /// Строит модели для всех уникальных значений LookbackPeriod
        /// </summary>
        private async Task BuildAllModelsAsync()
        {
            Debug.WriteLine("[BuildAllModelsAsync] ========== НАЧАЛО ==========");
            Debug.WriteLine($"[BuildAllModelsAsync] AlignedData.Count={_dataCache.AlignedData?.Count ?? 0}");

            if (_dataCache.AlignedData == null || _dataCache.AlignedData.Count < 50)
            {
                Debug.WriteLine($"[BuildAllModelsAsync] Недостаточно данных: {_dataCache.AlignedData?.Count ?? 0}");
                return;
            }

            var lookbackParam = Parameters.FirstOrDefault(p => p.Name == "LookbackPeriod");
            if (lookbackParam == null)
            {
                Debug.WriteLine("[BuildAllModelsAsync] Параметр LookbackPeriod не найден");
                return;
            }

            var maxLookback = _dataCache.AlignedData.Count / 2;
            var uniqueLookbacks = lookbackParam.GetValues()
                .Select(v => (int)v)
                .Where(v => v >= 24 && v <= maxLookback)
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            Debug.WriteLine($"[BuildAllModelsAsync] maxLookback={maxLookback}, uniqueLookbacks.Count={uniqueLookbacks.Count}");

            if (!uniqueLookbacks.Any())
            {
                Debug.WriteLine($"[BuildAllModelsAsync] Нет валидных значений LookbackPeriod");
                return;
            }

            int total = uniqueLookbacks.Count;
            int completed = 0;

            foreach (var lookback in uniqueLookbacks)
            {
                Debug.WriteLine($"[BuildAllModelsAsync] Построение модели для Lookback={lookback} ({completed + 1}/{total})");

                // ✅ Плавное обновление прогресса от 76% до 95%
                double progress = 76 + ((double)completed / total * 19);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressValue = progress;
                    ProgressText = $"Построение моделей: {completed + 1}/{total} (Lookback={lookback})";
                    LoadingStatus = ProgressText;
                });

                try
                {
                    var model = await BuildModelForLookbackAsync(lookback);
                    if (model != null && model.IsValid)
                    {
                        _dataCache.PairsModels[lookback] = model;
                        Debug.WriteLine($"[BuildAllModelsAsync] ✅ Модель для Lookback={lookback}: β={model.HedgeRatio:F4}, ρ={model.Correlation:F2}");
                    }
                    else
                    {
                        Debug.WriteLine($"[BuildAllModelsAsync] ⚠️ Модель для Lookback={lookback} невалидна");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BuildAllModelsAsync] Ошибка для Lookback={lookback}: {ex.Message}");
                }

                completed++;
            }

            // ✅ Финальное обновление
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ProgressValue = 95;
                ProgressText = $"✅ Построено {_dataCache.PairsModels.Count} моделей";
                LoadingStatus = ProgressText;
            });

            Debug.WriteLine($"[BuildAllModelsAsync] Построено {_dataCache.PairsModels.Count} моделей");
            Debug.WriteLine("[BuildAllModelsAsync] ========== КОНЕЦ ==========");
        }

        /// <summary>
        /// Строит модель для указанного периода обучения
        /// </summary>
        private async Task<PairsModel> BuildModelForLookbackAsync(int lookback)
        {
            Debug.WriteLine($"[BuildModelForLookbackAsync] НАЧАЛО: lookback={lookback}");
            var alignedData = _dataCache.AlignedData;
            if (alignedData == null || alignedData.Count < lookback + 10)
            {
                Debug.WriteLine($"[BuildModelForLookbackAsync] Недостаточно данных: alignedData.Count={alignedData?.Count ?? 0}, нужно {lookback + 10}");
                return null;
            }

            // Берем последние N точек
            var modelData = alignedData.TakeLast(lookback).ToList();
            Debug.WriteLine($"[BuildModelForLookbackAsync] modelData.Count={modelData.Count}");

            // Рассчитываем линейную регрессию
            Debug.WriteLine("[BuildModelForLookbackAsync] Расчет линейной регрессии...");
            var (beta, alpha, correlation) = CalculateLinearRegression(modelData);
            Debug.WriteLine($"[BuildModelForLookbackAsync] Регрессия: β={beta:F6}, α={alpha:F6}, ρ={correlation:F6}");

            if (beta <= 0 || correlation < 0.3m)
            {
                Debug.WriteLine($"[BuildModelForLookbackAsync] Модель невалидна: β={beta}, correlation={correlation}");
                return null;
            }

            // Рассчитываем статистику спреда
            Debug.WriteLine("[BuildModelForLookbackAsync] Расчет статистики спреда...");
            var spreads = modelData.Select(d => d.PriceA - beta * d.PriceB).ToList();
            var mean = spreads.Average();
            var std = CalculateStdDev(spreads, mean);
            Debug.WriteLine($"[BuildModelForLookbackAsync] Спред: mean={mean:F6}, std={std:F6}, min={spreads.Min():F6}, max={spreads.Max():F6}");

            var result = new PairsModel
            {
                LookbackPeriod = lookback,
                HedgeRatio = beta,
                SpreadMean = mean,
                SpreadStd = std,
                Correlation = correlation,
                BuildTime = DateTime.Now
            };

            Debug.WriteLine("[BuildModelForLookbackAsync] КОНЕЦ");
            return result;
        }

        private (decimal beta, decimal alpha, decimal correlation) CalculateLinearRegression(List<AlignedCandleData> data)
        {
            Debug.WriteLine($"[CalculateLinearRegression] НАЧАЛО: data.Count={data.Count}");
            int n = data.Count;
            if (n < 2)
            {
                Debug.WriteLine("[CalculateLinearRegression] Недостаточно данных");
                return (0, 0, 0);
            }

            decimal sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
            foreach (var d in data)
            {
                sumX += d.PriceB;
                sumY += d.PriceA;
                sumXY += d.PriceB * d.PriceA;
                sumX2 += d.PriceB * d.PriceB;
                sumY2 += d.PriceA * d.PriceA;
            }

            decimal meanX = sumX / n, meanY = sumY / n;
            Debug.WriteLine($"[CalculateLinearRegression] sumX={sumX:F2}, sumY={sumY:F2}, meanX={meanX:F2}, meanY={meanY:F2}");

            decimal numerator = sumXY - n * meanX * meanY;
            decimal denominator = sumX2 - n * meanX * meanX;
            Debug.WriteLine($"[CalculateLinearRegression] numerator={numerator:F4}, denominator={denominator:F4}");

            if (denominator == 0)
            {
                Debug.WriteLine("[CalculateLinearRegression] denominator == 0");
                return (0, 0, 0);
            }

            decimal beta = numerator / denominator;
            decimal alpha = meanY - beta * meanX;

            decimal covXY = (sumXY - n * meanX * meanY) / n;
            decimal varX = (sumX2 - n * meanX * meanX) / n;
            decimal varY = (sumY2 - n * meanY * meanY) / n;

            decimal correlation = (varX > 0 && varY > 0) ?
                covXY / (decimal)Math.Sqrt((double)(varX * varY)) : 0;

            Debug.WriteLine($"[CalculateLinearRegression] beta={beta:F6}, alpha={alpha:F6}, correlation={correlation:F6}");
            Debug.WriteLine("[CalculateLinearRegression] КОНЕЦ");
            return (beta, alpha, correlation);
        }

        private decimal CalculateStdDev(List<decimal> values, decimal mean)
        {
            Debug.WriteLine($"[CalculateStdDev] НАЧАЛО: values.Count={values.Count}, mean={mean:F6}");
            if (values.Count < 2)
            {
                Debug.WriteLine("[CalculateStdDev] Недостаточно данных");
                return 0;
            }
            double sumSq = 0;
            foreach (var v in values)
            {
                double diff = (double)(v - mean);
                sumSq += diff * diff;
            }
            var result = (decimal)Math.Sqrt(sumSq / (values.Count - 1));
            Debug.WriteLine($"[CalculateStdDev] result={result:F6}");
            Debug.WriteLine("[CalculateStdDev] КОНЕЦ");
            return result;
        }

        #endregion

        #region Оптимизация

        private bool CanStartOptimization()
        {
            Debug.WriteLine("[CanStartOptimization] ВЫЗОВ");

            // ✅ Проверяем, есть ли выбранные параметры
            bool hasSelected = Parameters.Any(p => p.IsSelected);
            Debug.WriteLine($"[CanStartOptimization] hasSelected={hasSelected}");

            // ✅ Проверяем, подготовлены ли данные
            bool dataReady = _dataPrepared;
            Debug.WriteLine($"[CanStartOptimization] dataReady={dataReady}");

            // ✅ Проверяем, есть ли модели в кэше (только для PairsTrading)
            bool hasModels = true;
            if (_strategyType == "PairsTrading")
            {
                hasModels = _dataCache?.PairsModels != null && _dataCache.PairsModels.Any();
            }
            Debug.WriteLine($"[CanStartOptimization] hasModels={hasModels}");

            // ✅ Проверяем, не выполняется ли оптимизация
            bool notRunning = !_isOptimizing;
            Debug.WriteLine($"[CanStartOptimization] notRunning={notRunning}");

            bool result = hasSelected && dataReady && hasModels && notRunning;
            Debug.WriteLine($"[CanStartOptimization] result={result}");

            return result;
        }

        private bool CanStopOptimization()
        {
            var result = _isOptimizing;
            Debug.WriteLine($"[CanStopOptimization] _isOptimizing={_isOptimizing}, result={result}");
            return result;
        }

        /// <summary>
        /// Проверяет, можно ли применить выбранные параметры
        /// </summary>
        private bool CanApplyParameters()
        {
            bool result = SelectedResult != null;
            Debug.WriteLine($"[CanApplyParameters] SelectedResult={SelectedResult != null}, result={result}");
            return result;
        }

        public void RefreshCommands()
        {
            Debug.WriteLine("[RefreshCommands] ========== НАЧАЛО ==========");

            // ✅ Принудительно обновляем все команды
            Debug.WriteLine("[RefreshCommands] Вызов CommandManager.InvalidateRequerySuggested()");
            CommandManager.InvalidateRequerySuggested();

            // ✅ Уведомляем каждую команду отдельно
            Debug.WriteLine("[RefreshCommands] Уведомление LoadHistoryCommand");
            (LoadHistoryCommand as RelayCommand)?.NotifyCanExecuteChanged();

            Debug.WriteLine("[RefreshCommands] Уведомление StartOptimizationCommand");
            (StartOptimizationCommand as RelayCommand)?.NotifyCanExecuteChanged();

            Debug.WriteLine("[RefreshCommands] Уведомление StopOptimizationCommand");
            (StopOptimizationCommand as RelayCommand)?.NotifyCanExecuteChanged();

            Debug.WriteLine("[RefreshCommands] Уведомление ApplyParametersCommand");
            (ApplyParametersCommand as RelayCommand)?.NotifyCanExecuteChanged();

            // ✅ Дополнительно обновляем UI
            Debug.WriteLine("[RefreshCommands] Обновление свойств UI");
            OnPropertyChanged(nameof(CanStartOptimizationCommand));
            OnPropertyChanged(nameof(CanStopOptimizationCommand));
            OnPropertyChanged(nameof(CanApplyParametersCommand));


            // Передаем название стратегии в окно эквити через результаты стратегии
            if (SelectedResult != null)
            {
                SelectedResult.StrategyType = StrategyType;
            }
            

            //Debug.WriteLine($"[RefreshCommands] ========== _results[0].StrategyType ==========  {_results[0].StrategyType}");



            Debug.WriteLine("[RefreshCommands] ========== КОНЕЦ ==========");
        }

        /// <summary>
        /// Запускает процесс оптимизации параметров
        /// </summary>
        private async Task StartOptimizationAsync()
        {
            Debug.WriteLine("[StartOptimizationAsync] ========== НАЧАЛО ==========");
            Debug.WriteLine($"[StartOptimizationAsync] _isOptimizing={_isOptimizing}");
            Debug.WriteLine($"[StartOptimizationAsync] _dataPrepared={_dataPrepared}");
            Debug.WriteLine($"[StartOptimizationAsync] _engineFactory={_engineFactory != null}");
            Debug.WriteLine($"[StartOptimizationAsync] _strategyType={_strategyType}");

            // ============================================================
            // 1. ПРОВЕРКА: не выполняется ли уже оптимизация
            // ============================================================
            if (_isOptimizing)
            {
                Debug.WriteLine("[StartOptimizationAsync] ПРОПУСК: оптимизация уже выполняется");
                return;
            }

            // ============================================================
            // 2. ПРОВЕРКА: выбраны ли параметры для оптимизации
            // ============================================================
            var selectedParams = Parameters.Where(p => p.IsSelected).ToList();
            Debug.WriteLine($"[StartOptimizationAsync] Выбрано параметров: {selectedParams.Count}");

            if (!selectedParams.Any())
            {
                Debug.WriteLine("[StartOptimizationAsync] Нет выбранных параметров");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("Выберите хотя бы один параметр для оптимизации", "Предупреждение",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                });
                return;
            }

            // ============================================================
            // 3. РАСЧЕТ КОМБИНАЦИЙ (ТОЛЬКО ОДИН РАЗ!)
            // ============================================================
            // Вызываем метод обновления, который корректно рассчитывает 
            // количество комбинаций на основе выбранных параметров
            UpdateTotalCombinations();

            // Получаем РЕАЛЬНОЕ количество комбинаций
            long totalCombinations = TotalCombinations;
            Debug.WriteLine($"[StartOptimizationAsync] Всего комбинаций (корректный расчет): {totalCombinations}");

            // ============================================================
            // 4. ПРОВЕРКА: слишком много комбинаций?
            // ============================================================
            if (totalCombinations > 500000)
            {
                Debug.WriteLine("[StartOptimizationAsync] Много комбинаций, запрос подтверждения");

                var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    return MessageBox.Show(
                        $"Будет выполнено {totalCombinations:N0} комбинаций.\n" +
                        $"Это может занять много времени.\n\n" +
                        $"Продолжить?",
                        "Подтверждение",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                });

                if (result != MessageBoxResult.Yes)
                {
                    Debug.WriteLine("[StartOptimizationAsync] ОТМЕНА: пользователь отказался");
                    return;
                }
            }

            // ============================================================
            // 5. ПРОВЕРКА: подготовлены ли данные для оптимизации
            // ============================================================
            if (!_dataPrepared)
            {
                Debug.WriteLine("[StartOptimizationAsync] Данные не подготовлены");

                var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    return MessageBox.Show(
                        "Данные не подготовлены. Загрузить исторические данные сейчас?",
                        "Подтверждение",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                });

                if (result == MessageBoxResult.Yes)
                {
                    Debug.WriteLine("[StartOptimizationAsync] Пользователь согласился на загрузку данных");
                    await PrepareDataAsync();

                    if (!_dataPrepared)
                    {
                        Debug.WriteLine("[StartOptimizationAsync] Не удалось подготовить данные");
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show("Не удалось подготовить данные", "Ошибка",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                        return;
                    }
                }
                else
                {
                    Debug.WriteLine("[StartOptimizationAsync] Пользователь отменил загрузку данных");
                    return;
                }
            }

            // ============================================================
            // 6. ПРОВЕРКА: наличие данных для MA стратегии
            // ============================================================
            if (_strategyType == "MA")
            {
                if (_dataCache.Candles == null || !_dataCache.Candles.Any())
                {
                    Debug.WriteLine("[StartOptimizationAsync] Нет кэшированных данных для MA стратегии!");
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show(
                            "Не удалось загрузить данные для оптимизации. Попробуйте перезагрузить данные.",
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return;
                }

                var instrument = _strategyViewModel.Instrument;
                if (!_dataCache.Candles.ContainsKey(instrument.Ticker) ||
                    _dataCache.Candles[instrument.Ticker]?.Count < 50)
                {
                    Debug.WriteLine($"[StartOptimizationAsync] Недостаточно данных для {instrument.Ticker}");
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show(
                            $"Недостаточно данных для {instrument.Ticker}. Нужно минимум 50 свечей.",
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return;
                }
            }

            // ============================================================
            // 7. ПРОВЕРКА: наличие моделей для PairsTrading
            // ============================================================
            if (_strategyType == "PairsTrading")
            {
                if (_dataCache.PairsModels == null || !_dataCache.PairsModels.Any())
                {
                    Debug.WriteLine("[StartOptimizationAsync] Нет кэшированных моделей для бэктеста!");
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show(
                            "Не удалось построить модели для оптимизации. Попробуйте перезагрузить данные.",
                            "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return;
                }
            }

            // ============================================================
            // 8. ИНИЦИАЛИЗАЦИЯ ФЛАГОВ И ПЕРЕМЕННЫХ
            // ============================================================
            Debug.WriteLine("[StartOptimizationAsync] Установка флагов...");
            _isOptimizing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            // Обновляем UI с правильным количеством комбинаций
            TotalCombinations = (int)Math.Min(totalCombinations, int.MaxValue);
            CompletedCombinations = 0;
            BestResultSummary = "";

            IsOptimizationRunning = true;
            OptimizationStatus = "Выполняется оптимизация...";
            IsProgressVisible = true;
            ProgressMaximum = TotalCombinations;
            ProgressValue = 0;

            Results.Clear();
            RefreshCommands();

            // ============================================================
            // 9. СОЗДАНИЕ ДВИЖКА БЭКТЕСТА
            // ============================================================
            try
            {
                Debug.WriteLine("[StartOptimizationAsync] Создание движка бэктеста...");

                if (_engineFactory == null)
                {
                    Debug.WriteLine("[StartOptimizationAsync] _engineFactory is NULL!");
                    throw new InvalidOperationException("Engine factory is null");
                }

                Debug.WriteLine($"[StartOptimizationAsync] Вызов CreateEngine для {_strategyType}");
                _backtestEngine = _engineFactory.CreateEngine(_strategyType);
                Debug.WriteLine($"[StartOptimizationAsync] _backtestEngine={_backtestEngine != null}");

                if (_backtestEngine == null)
                {
                    Debug.WriteLine("[StartOptimizationAsync] _backtestEngine is NULL!");
                    throw new InvalidOperationException($"Failed to create backtest engine for {_strategyType}");
                }

                Debug.WriteLine("[StartOptimizationAsync] Вызов _backtestEngine.InitializeAsync...");
                await _backtestEngine.InitializeAsync(_strategyViewModel, _dataCache, _logger);
                Debug.WriteLine("[StartOptimizationAsync] _backtestEngine.InitializeAsync завершен успешно");
                Debug.WriteLine($"[StartOptimizationAsync] Создан движок типа: {_backtestEngine?.GetType().Name}");
            }
            catch (NotSupportedException ex)
            {
                Debug.WriteLine($"[StartOptimizationAsync] Неподдерживаемая стратегия: {ex.Message}");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Стратегия '{_strategyType}' пока не поддерживается для оптимизации.\n\n" +
                        $"Доступные стратегии: PairsTrading, MA",
                        "Информация",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                _isOptimizing = false;
                IsOptimizationRunning = false;
                IsProgressVisible = false;
                RefreshCommands();
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartOptimizationAsync] Ошибка инициализации движка: {ex.Message}");
                Debug.WriteLine($"[StartOptimizationAsync] StackTrace: {ex.StackTrace}");

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Ошибка инициализации бэктеста:\n{ex.Message}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });

                _isOptimizing = false;
                IsOptimizationRunning = false;
                IsProgressVisible = false;
                RefreshCommands();
                return;
            }

            // ============================================================
            // 10. ЗАПУСК ОПТИМИЗАЦИИ В ОТДЕЛЬНОМ ПОТОКЕ
            // ============================================================
            Debug.WriteLine("[StartOptimizationAsync] Запуск RunOptimizationAsync...");
            await Task.Run(() => RunOptimizationAsync(selectedParams, _cancellationTokenSource.Token));
            Debug.WriteLine("[StartOptimizationAsync] ========== КОНЕЦ ==========");
        }

       
        private async Task RunOptimizationAsync(List<OptimizationParameter> selectedParams, CancellationToken cancellationToken)
        {
            Debug.WriteLine("[RunOptimizationAsync] ========== НАЧАЛО ==========");

            try
            {
                // ✅ ПОЛУЧАЕМ ВСЕ ПАРАМЕТРЫ (включая НЕвыбранные) с их текущими значениями
                var allParams = Parameters.ToDictionary(p => p.Name, p => p.CurrentValue);

                Debug.WriteLine($"[RunOptimizationAsync] Все параметры (текущие значения):");
                foreach (var kvp in allParams)
                {
                    //Debug.WriteLine($"  {kvp.Key} = {kvp.Value}");
                }

                Debug.WriteLine($"[RunOptimizationAsync] Выбрано для оптимизации: {selectedParams.Count} параметров");
                foreach (var p in selectedParams)
                {
                    Debug.WriteLine($"  {p.Name} (диапазон: {p.MinValue}..{p.MaxValue}, шаг: {p.Step})");
                }

                // ✅ ПРЕДВАРИТЕЛЬНЫЙ РАСЧЕТ КОЛИЧЕСТВА КОМБИНАЦИЙ (БЕЗ ГЕНЕРАЦИИ ВСЕХ!)
                long totalCombinations = 1;
                foreach (var param in selectedParams)
                {
                    var count = param.GetValueCount();
                    if (totalCombinations > long.MaxValue / count)
                    {
                        totalCombinations = long.MaxValue;
                        break;
                    }
                    totalCombinations *= count;
                }
                int total = (int)Math.Min(totalCombinations, int.MaxValue);

                Debug.WriteLine($"[RunOptimizationAsync] Всего комбинаций: {total}");

                // ✅ КОРРЕКТИРУЕМ TotalCombinations
                if (TotalCombinations != total)
                {
                    TotalCombinations = total;
                    ProgressMaximum = total;
                }

                // ✅ СРАЗУ ПОКАЗЫВАЕМ ПРОГРЕСС-БАР
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsProgressVisible = true;
                    ProgressValue = 0;
                    ProgressMaximum = total;
                    ProgressText = $"Обработано: 0/{total} комбинаций";
                    OptimizationStatus = $"Выполняется оптимизация... 0/{total}";
                });

                Debug.WriteLine($"[RunOptimizationAsync] _backtestEngine={_backtestEngine != null}");

                int processed = 0;
                int validResultsCount = 0;
                int invalidCount = 0;
                OptimizationResult bestResult = null;

                DateTime startTime = DateTime.Now;
                double averageSpeed = 0;
                int speedSamples = 0;

                // ✅ СЧЕТЧИК ДЛЯ ПЕРИОДИЧЕСКОЙ ОЧИСТКИ ПАМЯТИ
                int gcCounter = 0;

                // ============================================================
                // ✅ ОСНОВНОЙ ЦИКЛ - ИСПОЛЬЗУЕМ LAZY ГЕНЕРАЦИЮ (БЕЗ ХРАНЕНИЯ ВСЕХ КОМБИНАЦИЙ В ПАМЯТИ)
                // ============================================================
                foreach (var paramSet in GenerateCombinationsLazy(selectedParams))
                {
                    // Проверка отмены
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Debug.WriteLine("[RunOptimizationAsync] ОСТАНОВКА по запросу пользователя");
                        break;
                    }

                    try
                    {
                        // ✅ УВЕЛИЧИВАЕМ СЧЕТЧИК НА КАЖДОЙ ИТЕРАЦИИ
                        processed++;
                        gcCounter++;

                        // Формируем полный набор параметров
                        var fullParamSet = new Dictionary<string, decimal>(allParams);
                        foreach (var kvp in paramSet)
                        {
                            fullParamSet[kvp.Key] = kvp.Value;
                        }

                        // Отладочный вывод для первых 5 комбинаций
                        if (processed <= 5)
                        {
                            Debug.WriteLine($"[RunOptimizationAsync] Комбинация {processed}: " +
                                $"{string.Join(", ", fullParamSet.Select(p => $"{p.Key}={p.Value}"))}");
                        }

                        // ✅ Проверяем валидность комбинации ПЕРЕД бэктестом
                        bool isValidCombination = IsValidParameterCombination(fullParamSet);

                        OptimizationResult result = null;

                        if (isValidCombination)
                        {
                            // Только валидные комбинации отправляем в бэктест
                            result = await _backtestEngine.RunBacktestAsync(fullParamSet, cancellationToken);

                            if (result != null && result.TotalTrades > 0 && IsValidOptimizationResult(result))
                            {
                                // ✅ ВАЛИДНЫЙ РЕЗУЛЬТАТ - добавляем в коллекцию
                                result.Iteration = ++validResultsCount;

                                lock (_resultLock)
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        // ============================================================
                                        // ✅ ОГРАНИЧИВАЕМ КОЛИЧЕСТВО СОХРАНЯЕМЫХ РЕЗУЛЬТАТОВ (ТОП-500)
                                        // ============================================================
                                        const int MAX_RESULTS_TO_KEEP = 500;

                                        if (Results.Count >= MAX_RESULTS_TO_KEEP)
                                        {
                                            // Находим худший результат в коллекции
                                            var worstResult = Results
                                                .OrderBy(r => r.NetProfit)
                                                .FirstOrDefault();

                                            // Если текущий результат лучше худшего - заменяем
                                            if (worstResult != null && result.NetProfit > worstResult.NetProfit)
                                            {
                                                // ✅ ОЧИЩАЕМ ИСТОРИЮ У УДАЛЯЕМОГО РЕЗУЛЬТАТА (освобождаем память)
                                                worstResult.EquityHistory = null;
                                                worstResult.EquityDates = null;

                                                // Удаляем худший и добавляем текущий
                                                Results.Remove(worstResult);
                                                Results.Add(result);
                                            }
                                            // Если текущий результат не лучше худшего - просто пропускаем
                                        }
                                        else
                                        {
                                            // Если коллекция еще не заполнена - добавляем
                                            Results.Add(result);
                                        }

                                        // Проверяем, не стал ли этот результат лучшим
                                        if (bestResult == null || result.NetProfit > bestResult.NetProfit)
                                        {
                                            bestResult = result;
                                            Debug.WriteLine($"[RunOptimizationAsync] 🏆 НОВЫЙ ЛУЧШИЙ РЕЗУЛЬТАТ {_instrumentInfo}: NetProfit={result.NetProfit:F2}, Trades={result.TotalTrades}");
                                            UpdateBestResultSummary(bestResult);
                                        }
                                    });
                                }
                            }
                            else
                            {
                                invalidCount++;
                                // ✅ ОСВОБОЖДАЕМ ПАМЯТЬ ОТ НЕВАЛИДНОГО РЕЗУЛЬТАТА
                                if (result != null)
                                {
                                    result.EquityHistory = null;
                                    result.EquityDates = null;
                                    result = null;
                                }
                            }
                        }
                        else
                        {
                            invalidCount++;
                        }

                        // ============================================================
                        // ОБНОВЛЕНИЕ ПРОГРЕССА
                        // ============================================================
                        CompletedCombinations = processed;

                        // Обновляем прогресс каждые 50 комбинаций
                        if (processed % 50 == 0 || processed == total)
                        {
                            await UpdateProgressAsync(processed, total, startTime, averageSpeed, speedSamples, bestResult);
                        }

                        // ============================================================
                        // ✅ ПЕРИОДИЧЕСКИЙ СБОР МУСОРА ДЛЯ ОСВОБОЖДЕНИЯ ПАМЯТИ
                        // ============================================================
                        if (gcCounter % 500 == 0)
                        {
                            // Принудительно вызываем сборщик мусора каждые 500 итераций
                            // Это помогает освободить память от временных объектов
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("[RunOptimizationAsync] Операция отменена");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[RunOptimizationAsync] Ошибка в комбинации {processed}: {ex.Message}");
                        invalidCount++;
                    }
                }

                // Финальное сообщение
                await FinishOptimizationAsync(processed, total, validResultsCount, invalidCount, startTime, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RunOptimizationAsync] КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}");
                _logger?.LogError(ex, "Ошибка выполнения оптимизации");

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    OptimizationStatus = $"Ошибка: {ex.Message}";
                    IsOptimizationRunning = false;
                    IsProgressVisible = false;
                    _isOptimizing = false;
                    RefreshCommands();
                });
            }
            finally
            {
                Debug.WriteLine("[RunOptimizationAsync] Освобождение ресурсов _backtestEngine");
                _backtestEngine?.Dispose();
                _backtestEngine = null;
            }
            Debug.WriteLine("[RunOptimizationAsync] ========== КОНЕЦ ==========");
        }

        /// <summary>
        /// Обновляет прогресс выполнения оптимизации
        /// </summary>
        private async Task UpdateProgressAsync(
            int processed,
            int total,
            DateTime startTime,
            double averageSpeed,
            int speedSamples,
            OptimizationResult bestResult)
        {
            double progressPercent = (double)processed / total * 100;
            TimeSpan elapsed = DateTime.Now - startTime;

            if (processed > 5)
            {
                double speed = processed / elapsed.TotalSeconds;
                if (speedSamples == 0)
                    averageSpeed = speed;
                else
                    averageSpeed = averageSpeed * 0.7 + speed * 0.3;
                speedSamples++;
            }

            string remainingTimeText = "вычисляется...";
            if (averageSpeed > 0 && processed > 0)
            {
                int remainingCombinations = total - processed;
                double remainingSeconds = remainingCombinations / averageSpeed;
                if (remainingSeconds > 0)
                    remainingTimeText = FormatTimeSpan(TimeSpan.FromSeconds(remainingSeconds));
            }

            string elapsedTimeText = FormatTimeSpan(elapsed);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ProgressValue = processed;
                ProgressText = $"Обработано: ({progressPercent:F1}%) {processed}/{total} комбинаций";
                LoadingStatus = $"⏱ Затрачено: {elapsedTimeText}, осталось: {remainingTimeText}";

                OptimizationStatus = bestResult != null
                    ? $"Лучший результат: {bestResult.NetProfit:F2} руб. | {LoadingStatus}"
                    : $"Выполняется... | {LoadingStatus}";
            });
        }

        /// <summary>
        /// Финальное сообщение по завершении оптимизации
        /// </summary>
        private async Task FinishOptimizationAsync(
            int processed,
            int total,
            int validResultsCount,
            int invalidCount,
            DateTime startTime,
            CancellationToken cancellationToken)
        {
            TimeSpan totalElapsed = DateTime.Now - startTime;
            string totalTimeText = FormatTimeSpan(totalElapsed);

            Debug.WriteLine($"[RunOptimizationAsync] ЗАВЕРШЕНО. Обработано: {processed} комбинаций");
            Debug.WriteLine($"[RunOptimizationAsync] Валидных результатов: {validResultsCount}");
            Debug.WriteLine($"[RunOptimizationAsync] Невалидных: {invalidCount}");
            Debug.WriteLine($"[RunOptimizationAsync] Затрачено времени: {totalTimeText}");

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsOptimizationRunning = false;
                IsProgressVisible = false;

                // ✅ ПОСЛЕДНИЙ СБОР МУСОРА ПЕРЕД ЗАВЕРШЕНИЕМ
                GC.Collect();
                GC.WaitForPendingFinalizers();

                OptimizationStatus = cancellationToken.IsCancellationRequested
                    ? $"Оптимизация остановлена. Обработано {processed} комбинаций. Найдено {validResultsCount} валидных результатов. Невалидных: {invalidCount}. Затрачено: {totalTimeText}"
                    : $"Оптимизация завершена. Обработано {processed} комбинаций. Найдено {validResultsCount} валидных результатов из {total} комбинаций. Невалидных: {invalidCount}. Затрачено: {totalTimeText}";

                _isOptimizing = false;
                RefreshCommands();
            });
        }





        /// <summary>
        /// Форматирует TimeSpan в удобочитаемый формат
        /// </summary>
        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalSeconds < 60)
            {
                return $"{timeSpan.Seconds}с";
            }
            else if (timeSpan.TotalMinutes < 60)
            {
                return $"{timeSpan.Minutes}м {timeSpan.Seconds}с";
            }
            else if (timeSpan.TotalHours < 24)
            {
                return $"{timeSpan.Hours}ч {timeSpan.Minutes}м {timeSpan.Seconds}с";
            }
            else
            {
                return $"{timeSpan.Days}д {timeSpan.Hours}ч {timeSpan.Minutes}м";
            }
        }

        /// <summary>
        /// Проверяет, является ли результат оптимизации валидным
        /// </summary>
        private bool IsValidOptimizationResult(OptimizationResult result)
        {
            if (result == null)
                return false;

            // ✅ Используем общую логику проверки комбинации параметров
            if (!IsValidParameterCombination(result.Parameters))
            {
                Debug.WriteLine($"[OptimizationViewModel] ❌ Результат не прошел проверку параметров");
                return false;
            }

            // ✅ Проверка базовой валидности (есть сделки и прибыль не равна -999999)
            if (result.TotalTrades <= 0 || result.NetProfit < -500000)
            {
                Debug.WriteLine($"[OptimizationViewModel] ❌ Недостаточно сделок или слишком большой убыток");
                return false;
            }

            // ✅ Проверяем, что WinRate в разумных пределах (0-100%)
            if (result.WinRate < 0 || result.WinRate > 100)
            {
                Debug.WriteLine($"[OptimizationViewModel] ❌ WinRate={result.WinRate} вне диапазона 0-100%");
                return false;
            }

            // ✅ Проверяем, что просадка не слишком большая (>100% означает полную потерю депозита)
            if (result.MaxDrawdown > 100)
            {
                Debug.WriteLine($"[OptimizationViewModel] ❌ MaxDrawdown={result.MaxDrawdown} > 100%");
                return false;
            }

            // ✅ Проверяем, что ProfitFactor не отрицательный
            if (result.ProfitFactor < 0)
            {
                Debug.WriteLine($"[OptimizationViewModel] ❌ ProfitFactor={result.ProfitFactor} отрицательный");
                return false;
            }

            // ✅ Проверяем, что AverageWin не отрицательный
            if (result.AverageWin < 0)
            {
                Debug.WriteLine($"[OptimizationViewModel] ❌ AverageWin={result.AverageWin} отрицательный");
                return false;
            }

            // ✅ Проверяем, что AverageLoss не отрицательный (убыток может быть только отрицательным числом)
            if (result.AverageLoss < 0)
            {
                Debug.WriteLine($"[OptimizationViewModel] ❌ AverageLoss={result.AverageLoss} отрицательный");
                return false;
            }

            // ✅ Дополнительная проверка: Expectancy должен быть в разумных пределах
            if (result.Expectancy < -10000 || result.Expectancy > 100000)
            {
                Debug.WriteLine($"[OptimizationViewModel] ⚠️ Expectancy={result.Expectancy} выходит за разумные пределы");
                // Не блокируем, только предупреждаем
            }

            return true;
        }

        /// <summary>
        /// Добавляет результат в коллекцию с проверкой валидности
        /// </summary>
        private void AddResult(OptimizationResult result)
        {
            if (result == null)
                return;

            // ✅ Фильтруем невалидные результаты
            if (!IsValidOptimizationResult(result))
            {
                Debug.WriteLine($"[OptimizationViewModel] ⚠️ Пропущен невалидный результат: NetProfit={result.NetProfit}, TotalTrades={result.TotalTrades}");
                return;
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                Results.Add(result);
            });
        }

        /// <summary>
        /// Обработчик изменения выбранного результата (автоматически вызывается при изменении SelectedResult)
        /// </summary>
        partial void OnSelectedResultChanged(OptimizationResult value)
        {
            Debug.WriteLine($"[OptimizationViewModel] SelectedResult изменен: {(value != null ? $"Iteration={value.Iteration}, NetProfit={value.NetProfit:F2}" : "null")}");

            // ✅ Принудительно обновляем команду ApplyParametersCommand
            (ApplyParametersCommand as RelayCommand)?.NotifyCanExecuteChanged();

            // Обновляем свойство для UI
            OnPropertyChanged(nameof(CanApplyParametersCommand));
        }

        /// <summary>
        /// Обработчик изменения свойств ViewModel
        /// </summary>
        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectedResult))
            {
                Debug.WriteLine($"[OptimizationViewModel] PropertyChanged: SelectedResult={(SelectedResult != null ? SelectedResult.Iteration : "null")}");

                // ✅ Обновляем команду ApplyParametersCommand
                (ApplyParametersCommand as RelayCommand)?.NotifyCanExecuteChanged();

                // Обновляем свойство для UI
                OnPropertyChanged(nameof(CanApplyParametersCommand));
            }
        }



        /// <summary>
        /// Генерирует ВСЕ комбинации параметров (БЕЗ фильтрации невалидных)
        /// </summary>
        private List<Dictionary<string, decimal>> GenerateCombinations(List<OptimizationParameter> selectedParams)
        {
            Debug.WriteLine($"[GenerateCombinations] НАЧАЛО: selectedParams.Count={selectedParams.Count}");
            var result = new List<Dictionary<string, decimal>>();
            var paramValues = new List<List<(string Name, decimal Value)>>();

            foreach (var param in selectedParams)
            {
                Debug.WriteLine($"[GenerateCombinations] Параметр {param.Name}");
                var values = new List<(string, decimal)>();
                foreach (var val in param.GetValues())
                {
                    values.Add((param.Name, val));
                    Debug.WriteLine($"[GenerateCombinations]   {param.Name}={val}");
                }
                paramValues.Add(values);
                Debug.WriteLine($"[GenerateCombinations]   Всего значений: {values.Count}");
            }

            // ✅ Генерируем ВСЕ комбинации (БЕЗ фильтрации!)
            GenerateCombinationsRecursive(paramValues, 0, new Dictionary<string, decimal>(), result);

            Debug.WriteLine($"[GenerateCombinations] Сгенерировано {result.Count} комбинаций");
            Debug.WriteLine("[GenerateCombinations] КОНЕЦ");
            return result;
        }

        /// <summary>
        /// Генерирует ВСЕ комбинации параметров "на лету" (без хранения в памяти)
        /// Использует yield return для пошаговой генерации
        /// </summary>
        private IEnumerable<Dictionary<string, decimal>> GenerateCombinationsLazy(List<OptimizationParameter> selectedParams)
        {
            Debug.WriteLine($"[GenerateCombinationsLazy] НАЧАЛО: selectedParams.Count={selectedParams.Count}");

            var paramValues = new List<List<(string Name, decimal Value)>>();

            foreach (var param in selectedParams)
            {
                var values = new List<(string, decimal)>();
                foreach (var val in param.GetValues())
                {
                    values.Add((param.Name, val));
                }
                paramValues.Add(values);
                Debug.WriteLine($"[GenerateCombinationsLazy] Параметр {param.Name}: {values.Count} значений");
            }

            // ✅ Используем рекурсивный генератор с yield return
            foreach (var combo in GenerateCombinationsRecursiveLazy(paramValues, 0, new Dictionary<string, decimal>()))
            {
                yield return combo;
            }

            Debug.WriteLine("[GenerateCombinationsLazy] КОНЕЦ");
        }

        /// <summary>
        /// Рекурсивный генератор комбинаций с yield return
        /// </summary>
        private IEnumerable<Dictionary<string, decimal>> GenerateCombinationsRecursiveLazy(
            List<List<(string Name, decimal Value)>> paramValues,
            int index,
            Dictionary<string, decimal> current)
        {
            if (index >= paramValues.Count)
            {
                yield return new Dictionary<string, decimal>(current);
                yield break;
            }

            foreach (var (name, value) in paramValues[index])
            {
                current[name] = value;
                foreach (var combo in GenerateCombinationsRecursiveLazy(paramValues, index + 1, current))
                {
                    yield return combo;
                }
                current.Remove(name);
            }
        }




















        /// <summary>
        /// Проверяет, является ли комбинация параметров валидной для текущей стратегии
        /// </summary>
        private bool IsValidParameterCombination(Dictionary<string, decimal> parameters)
        {
            switch (_strategyType)
            {
                case "MA":
                    return IsValidMaCombination(parameters);

                case "PairsTrading":
                    return IsValidPairsTradingCombination(parameters);

                case "RSI":
                    return IsValidRsiCombination(parameters);

                case "Rating":
                    return IsValidRatingCombination(parameters);

                default:
                    // Для неизвестных стратегий пропускаем все комбинации
                    Debug.WriteLine($"[IsValidParameterCombination] Неизвестная стратегия: {_strategyType}, пропускаем все");
                    return true;
            }
        }


        /// <summary>
        /// Проверка валидности комбинации для MA стратегии
        /// </summary>
        private bool IsValidMaCombination(Dictionary<string, decimal> parameters)
        {
            // ✅ Проверяем SMA порядок (Short < Medium < Long)
            if (parameters.TryGetValue("SmaShort", out var smaShort) &&
                parameters.TryGetValue("SmaMedium", out var smaMedium) &&
                parameters.TryGetValue("SmaLong", out var smaLong))
            {
                if (!(smaShort < smaMedium && smaMedium < smaLong))
                {
                    //Debug.WriteLine($"[IsValidMaCombination] ❌ Невалидный SMA порядок: " +
                    //    $"SmaShort={smaShort}, SmaMedium={smaMedium}, SmaLong={smaLong}");
                    return false;
                }
            }

            // ✅ Проверяем EMA порядок (Short < Medium < Long)
            if (parameters.TryGetValue("EmaShort", out var emaShort) &&
                parameters.TryGetValue("EmaMedium", out var emaMedium) &&
                parameters.TryGetValue("EmaLong", out var emaLong))
            {
                if (!(emaShort < emaMedium && emaMedium < emaLong))
                {
                    //Debug.WriteLine($"[IsValidMaCombination] ❌ Невалидный EMA порядок: " +
                    //    $"EmaShort={emaShort}, EmaMedium={emaMedium}, EmaLong={emaLong}");
                    return false;
                }
            }

            // ✅ Проверка ATR множителей (StopLoss <= TrailingStop)
            if (parameters.TryGetValue("StopLossATRMultiplier", out var slMultiplier) &&
                parameters.TryGetValue("TrailingStopATRMultiplier", out var tsMultiplier))
            {
                if (slMultiplier > tsMultiplier)
                {
                    //Debug.WriteLine($"[IsValidMaCombination] ❌ Невалидные ATR множители: " +
                    //    $"StopLoss={slMultiplier} > TrailingStop={tsMultiplier}");
                    return false;
                }
            }

            // ✅ Проверка: TakeProfit должен быть больше StopLoss
            if (parameters.TryGetValue("TakeProfitATRMultiplier", out var tpMultiplier) &&
                parameters.TryGetValue("StopLossATRMultiplier", out var slMultiplier2))
            {
                if (tpMultiplier <= slMultiplier2)
                {
                    //Debug.WriteLine($"[IsValidMaCombination] ❌ Невалидные ATR множители: " +
                    //    $"TakeProfit={tpMultiplier} <= StopLoss={slMultiplier2}");
                    return false;
                }
            }

            // ✅ Проверка: FilterSmaPeriod должен быть между SmaShort и SmaMedium
            if (parameters.TryGetValue("FilterSmaPeriod", out var filterSma) &&
                parameters.TryGetValue("SmaShort", out var smaShort2) &&
                parameters.TryGetValue("SmaMedium", out var smaMedium2))
            {
                if (filterSma < smaShort2 || filterSma > smaMedium2)
                {
                    //Debug.WriteLine($"[IsValidMaCombination] ⚠️ FilterSmaPeriod={filterSma} вне диапазона " +
                     //   $"SmaShort={smaShort2}..SmaMedium={smaMedium2}, но это не критично");
                    // Не блокируем, только предупреждаем
                }
            }

            return true;
        }

        /// <summary>
        /// Проверка валидности комбинации для PairsTrading стратегии
        /// </summary>
        private bool IsValidPairsTradingCombination(Dictionary<string, decimal> parameters)
        {
            // ✅ EntryZScore должен быть меньше StopLossZScore
            if (parameters.TryGetValue("EntryZScore", out var entryZ) &&
                parameters.TryGetValue("StopLossZScore", out var stopLossZ))
            {
                if (entryZ >= stopLossZ)
                {
                    Debug.WriteLine($"[IsValidPairsTradingCombination] ❌ Невалидный результат: " +
                        $"EntryZScore ({entryZ}) >= StopLossZScore ({stopLossZ})");
                    return false;
                }
            }

            // ✅ ExitZScore должен быть меньше EntryZScore
            if (parameters.TryGetValue("ExitZScore", out var exitZ) &&
                parameters.TryGetValue("EntryZScore", out var entryZ2))
            {
                if (exitZ >= entryZ2)
                {
                    Debug.WriteLine($"[IsValidPairsTradingCombination] ❌ Невалидный результат: " +
                        $"ExitZScore ({exitZ}) >= EntryZScore ({entryZ2})");
                    return false;
                }
            }

            // ✅ LookbackPeriod должен быть не меньше минимального значения
            if (parameters.TryGetValue("LookbackPeriod", out var lookback))
            {
                if (lookback < 24)
                {
                    Debug.WriteLine($"[IsValidPairsTradingCombination] ❌ LookbackPeriod={lookback} < 24");
                    return false;
                }
            }

            // ✅ PositionSizePercent должен быть в разумных пределах
            if (parameters.TryGetValue("PositionSizePercent", out var posSize))
            {
                if (posSize < 1 || posSize > 100)
                {
                    Debug.WriteLine($"[IsValidPairsTradingCombination] ❌ PositionSizePercent={posSize} вне диапазона 1-100%");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Проверка валидности комбинации для RSI стратегии
        /// </summary>
        private bool IsValidRsiCombination(Dictionary<string, decimal> parameters)
        {
            // ✅ RsiOverbought должен быть больше RsiOversold
            if (parameters.TryGetValue("RsiOverbought", out var overbought) &&
                parameters.TryGetValue("RsiOversold", out var oversold))
            {
                if (overbought <= oversold)
                {
                    Debug.WriteLine($"[IsValidRsiCombination] ❌ Невалидные RSI уровни: " +
                        $"Overbought={overbought} <= Oversold={oversold}");
                    return false;
                }
            }

            // ✅ RsiPeriod должен быть не меньше 5
            if (parameters.TryGetValue("RsiPeriod", out var period))
            {
                if (period < 5)
                {
                    Debug.WriteLine($"[IsValidRsiCombination] ❌ RsiPeriod={period} < 5");
                    return false;
                }
            }

            // ✅ TakeProfitPercent должен быть больше StopLossPercent
            if (parameters.TryGetValue("TakeProfitPercent", out var tp) &&
                parameters.TryGetValue("StopLossPercent", out var sl))
            {
                if (tp <= sl)
                {
                    Debug.WriteLine($"[IsValidRsiCombination] ❌ TakeProfit={tp} <= StopLoss={sl}");
                    return false;
                }
            }

            // ✅ Размер позиции должен быть в разумных пределах
            if (parameters.TryGetValue("OrderSizePercent", out var orderSize))
            {
                if (orderSize < 1 || orderSize > 100)
                {
                    Debug.WriteLine($"[IsValidRsiCombination] ❌ OrderSizePercent={orderSize} вне диапазона 1-100%");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Проверка валидности комбинации для Rating стратегии
        /// </summary>
        private bool IsValidRatingCombination(Dictionary<string, decimal> parameters)
        {
            // ✅ EntryThreshold должен быть в разумных пределах
            if (parameters.TryGetValue("EntryThreshold", out var threshold))
            {
                if (threshold < 50 || threshold > 100)
                {
                    Debug.WriteLine($"[IsValidRatingCombination] ❌ EntryThreshold={threshold} вне диапазона 50-100");
                    return false;
                }
            }

            // ✅ MinMatchPercentage должен быть в разумных пределах
            if (parameters.TryGetValue("MinMatchPercentage", out var minMatch))
            {
                if (minMatch < 50 || minMatch > 100)
                {
                    Debug.WriteLine($"[IsValidRatingCombination] ❌ MinMatchPercentage={minMatch} вне диапазона 50-100");
                    return false;
                }
            }

            // ✅ MatchTolerance должен быть больше 0
            if (parameters.TryGetValue("MatchTolerance", out var tolerance))
            {
                if (tolerance <= 0)
                {
                    Debug.WriteLine($"[IsValidRatingCombination] ❌ MatchTolerance={tolerance} <= 0");
                    return false;
                }
            }

            // ✅ PositionSizePercent должен быть в разумных пределах
            if (parameters.TryGetValue("PositionSizePercent", out var posSize))
            {
                if (posSize < 1 || posSize > 100)
                {
                    Debug.WriteLine($"[IsValidRatingCombination] ❌ PositionSizePercent={posSize} вне диапазона 1-100%");
                    return false;
                }
            }

            return true;
        }



        private void GenerateCombinationsRecursive(
            List<List<(string Name, decimal Value)>> paramValues,
            int index,
            Dictionary<string, decimal> current,
            List<Dictionary<string, decimal>> result)
        {
            if (index >= paramValues.Count)
            {
                result.Add(new Dictionary<string, decimal>(current));
                return;
            }

            foreach (var (name, value) in paramValues[index])
            {
                current[name] = value;
                GenerateCombinationsRecursive(paramValues, index + 1, current, result);
                current.Remove(name);
            }
        }

        private void UpdateBestResultSummary(OptimizationResult result)
        {
            Debug.WriteLine("[UpdateBestResultSummary] НАЧАЛО");
            var paramSummary = string.Join(", ",
                result.Parameters.Select(p => $"{p.Key}={p.Value:F2}"));



            BestResultSummary = $"{_instrumentInfo}  Лучший: P&L={result.NetProfit:F2}, Фактор={result.ProfitFactor:F2}, " +
                               $"Сделок={result.TotalTrades} \nПараметры: {paramSummary}";
            Debug.WriteLine($"[UpdateBestResultSummary] {BestResultSummary}");
            Debug.WriteLine("[UpdateBestResultSummary] КОНЕЦ");
        }

        public void StopOptimization()
        {
            Debug.WriteLine("[StopOptimization] НАЧАЛО");
            Debug.WriteLine("[StopOptimization] Отмена _cancellationTokenSource");
            _cancellationTokenSource?.Cancel();
            IsOptimizationRunning = false;
            OptimizationStatus = "Остановка...";
            RefreshCommands();
            Debug.WriteLine("[StopOptimization] КОНЕЦ");
        }

        #endregion

        #region Применение результатов

        private void ApplySelectedParameters()
        {
            Debug.WriteLine("[ApplySelectedParameters] НАЧАЛО");
            Debug.WriteLine($"[ApplySelectedParameters] SelectedResult={SelectedResult != null}");

            if (SelectedResult == null)
            {
                Debug.WriteLine("[ApplySelectedParameters] SelectedResult is null");
                return;
            }

            Debug.WriteLine($"[ApplySelectedParameters] SelectedResult.Iteration={SelectedResult.Iteration}");
            Debug.WriteLine($"[ApplySelectedParameters] SelectedResult.NetProfit={SelectedResult.NetProfit:F2}");

            var result = MessageBox.Show(
                $"Применить параметры к стратегии?\n\n" +
                string.Join("\n", SelectedResult.Parameters.Select(p => $"{p.Key}: {p.Value}")),
                "Применить параметры",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                Debug.WriteLine("[ApplySelectedParameters] Отменено пользователем");
                return;
            }

            Debug.WriteLine("[ApplySelectedParameters] Применение параметров...");
            ApplyParametersToStrategy(SelectedResult.Parameters);
            ParametersApplied?.Invoke(SelectedResult.Parameters);

            // ✅ НОВОЕ: После применения параметров ОБНОВЛЯЕМ значения в окне оптимизации
            RefreshOptimizationParameters();

            Debug.WriteLine("[ApplySelectedParameters] Параметры применены и окно обновлено");
            MessageBox.Show("Параметры успешно применены к стратегии", "Успех",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Debug.WriteLine("[ApplySelectedParameters] КОНЕЦ");
        }

        /// <summary>
        /// Обновляет параметры в окне оптимизации из текущих значений стратегии
        /// </summary>
        private void RefreshOptimizationParameters()
        {
            Debug.WriteLine("[RefreshOptimizationParameters] НАЧАЛО");

            try
            {
                // Получаем текущие параметры из стратегии
                var strategy = _strategyViewModel.MaStrategy;
                if (strategy == null)
                {
                    Debug.WriteLine("[RefreshOptimizationParameters] strategy is NULL!");
                    return;
                }

                var maParams = strategy.Parameters;
                if (maParams == null)
                {
                    Debug.WriteLine("[RefreshOptimizationParameters] maParams is NULL!");
                    return;
                }

                Debug.WriteLine($"[RefreshOptimizationParameters] Текущие параметры стратегии:");
                Debug.WriteLine($"  PositionSizePercent = {maParams.PositionSizePercent}%");
                Debug.WriteLine($"  StopLossATRMultiplier = {maParams.StopLossATRMultiplier}");
                Debug.WriteLine($"  TakeProfitATRMultiplier = {maParams.TakeProfitATRMultiplier}");
                Debug.WriteLine($"  TrailingStopATRMultiplier = {maParams.TrailingStopATRMultiplier}");
                Debug.WriteLine($"  SmaPeriods = {maParams.SmaPeriods}");
                Debug.WriteLine($"  EmaPeriods = {maParams.EmaPeriods}");
                Debug.WriteLine($"  FilterSmaPeriod = {maParams.FilterSmaPeriod}");
                Debug.WriteLine($"  UseManualFilterSma = {maParams.UseManualFilterSma}");

                // ✅ ПАРСИМ ТЕКУЩИЕ ПЕРИОДЫ ИЗ СТРАТЕГИИ
                var smaPeriods = ParsePeriodsSorted(maParams.SmaPeriods);
                var emaPeriods = ParsePeriodsSorted(maParams.EmaPeriods);

                // Если периодов меньше 3, используем значения по умолчанию
                if (smaPeriods.Count < 3)
                {
                    smaPeriods = new List<int> { 20, 50, 100 };
                    Debug.WriteLine("[RefreshOptimizationParameters] SMA периоды невалидны, используем значения по умолчанию");
                }
                if (emaPeriods.Count < 3)
                {
                    emaPeriods = new List<int> { 25, 50, 100 };
                    Debug.WriteLine("[RefreshOptimizationParameters] EMA периоды невалидны, используем значения по умолчанию");
                }

                // ✅ Обновляем CurrentValue для каждого параметра в окне оптимизации
                foreach (var param in Parameters)
                {
                    decimal newValue = 0;
                    bool found = false;

                    switch (param.Name)
                    {
                        // ✅ SMA ПЕРИОДЫ - обновляем и CurrentValue, и MinValue, и MaxValue
                        case "SmaShort":
                            if (smaPeriods.Count >= 1)
                            {
                                newValue = smaPeriods[0];
                                param.CurrentValue = newValue;
                                param.MinValue = Math.Max(5, newValue - 20);
                                param.MaxValue = Math.Min(100, newValue + 20);
                                param.Step = 5;
                                found = true;
                                Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue} (диапазон: {param.MinValue}..{param.MaxValue})");
                            }
                            break;

                        case "SmaMedium":
                            if (smaPeriods.Count >= 2)
                            {
                                newValue = smaPeriods[1];
                                param.CurrentValue = newValue;
                                param.MinValue = Math.Max(10, newValue - 30);
                                param.MaxValue = Math.Min(200, newValue + 30);
                                param.Step = 10;
                                found = true;
                                Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue} (диапазон: {param.MinValue}..{param.MaxValue})");
                            }
                            break;

                        case "SmaLong":
                            if (smaPeriods.Count >= 3)
                            {
                                newValue = smaPeriods[2];
                                param.CurrentValue = newValue;
                                param.MinValue = Math.Max(20, newValue - 50);
                                param.MaxValue = Math.Min(500, newValue + 50);
                                param.Step = 20;
                                found = true;
                                Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue} (диапазон: {param.MinValue}..{param.MaxValue})");
                            }
                            break;

                        // ✅ EMA ПЕРИОДЫ - обновляем и CurrentValue, и MinValue, и MaxValue
                        case "EmaShort":
                            if (emaPeriods.Count >= 1)
                            {
                                newValue = emaPeriods[0];
                                param.CurrentValue = newValue;
                                param.MinValue = Math.Max(5, newValue - 20);
                                param.MaxValue = Math.Min(100, newValue + 20);
                                param.Step = 5;
                                found = true;
                                Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue} (диапазон: {param.MinValue}..{param.MaxValue})");
                            }
                            break;

                        case "EmaMedium":
                            if (emaPeriods.Count >= 2)
                            {
                                newValue = emaPeriods[1];
                                param.CurrentValue = newValue;
                                param.MinValue = Math.Max(10, newValue - 30);
                                param.MaxValue = Math.Min(200, newValue + 30);
                                param.Step = 10;
                                found = true;
                                Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue} (диапазон: {param.MinValue}..{param.MaxValue})");
                            }
                            break;

                        case "EmaLong":
                            if (emaPeriods.Count >= 3)
                            {
                                newValue = emaPeriods[2];
                                param.CurrentValue = newValue;
                                param.MinValue = Math.Max(20, newValue - 50);
                                param.MaxValue = Math.Min(300, newValue + 50);
                                param.Step = 10;
                                found = true;
                                Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue} (диапазон: {param.MinValue}..{param.MaxValue})");
                            }
                            break;

                        // ✅ FILTER SMA - учитываем режим ручного/автоматического управления
                        case "FilterSmaPeriod":
                            // ✅ Берем значение из стратегии
                            newValue = maParams.FilterSmaPeriod;
                            param.CurrentValue = newValue;

                            // ✅ Устанавливаем диапазон в зависимости от режима
                            int filterMin;
                            int filterMax;

                            if (maParams.UseManualFilterSma)
                            {
                                // Ручной режим - узкий диапазон вокруг значения
                                filterMin = Math.Max(1, (int)newValue - 20);
                                filterMax = Math.Min(200, (int)newValue + 20);

                                if (filterMax - filterMin < 10)
                                {
                                    filterMin = Math.Max(1, (int)newValue - 15);
                                    filterMax = Math.Min(200, (int)newValue + 15);
                                }
                                Debug.WriteLine($"[RefreshOptimizationParameters] Фильтр SMA: РУЧНОЙ режим, значение={newValue}");
                            }
                            else
                            {
                                // Автоматический режим - широкий диапазон
                                filterMin = 10;
                                filterMax = 200;
                                Debug.WriteLine($"[RefreshOptimizationParameters] Фильтр SMA: АВТОМАТИЧЕСКИЙ режим, значение={newValue}");
                            }

                            param.MinValue = filterMin;
                            param.MaxValue = filterMax;
                            param.Step = 5;
                            found = true;
                            Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue} (диапазон: {param.MinValue}..{param.MaxValue})");
                            break;

                        // ✅ ATR ПАРАМЕТРЫ - обновляем CurrentValue
                        case "PositionSizePercent":
                            newValue = maParams.PositionSizePercent;
                            param.CurrentValue = newValue;
                            found = true;
                            Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue}");
                            break;

                        case "StopLossATRMultiplier":
                            newValue = maParams.StopLossATRMultiplier;
                            param.CurrentValue = newValue;
                            found = true;
                            Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue}");
                            break;

                        case "TakeProfitATRMultiplier":
                            newValue = maParams.TakeProfitATRMultiplier;
                            param.CurrentValue = newValue;
                            found = true;
                            Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue}");
                            break;

                        case "TrailingStopATRMultiplier":
                            newValue = maParams.TrailingStopATRMultiplier;
                            param.CurrentValue = newValue;
                            found = true;
                            Debug.WriteLine($"[RefreshOptimizationParameters] Обновлен {param.Name} = {newValue}");
                            break;

                        default:
                            Debug.WriteLine($"[RefreshOptimizationParameters] Неизвестный параметр: {param.Name}");
                            break;
                    }

                    // Если параметр не был найден и обработан - пропускаем
                    if (!found && param.Name != "SmaShort" && param.Name != "SmaMedium" &&
                        param.Name != "SmaLong" && param.Name != "EmaShort" &&
                        param.Name != "EmaMedium" && param.Name != "EmaLong" &&
                        param.Name != "FilterSmaPeriod")
                    {
                        Debug.WriteLine($"[RefreshOptimizationParameters] Параметр {param.Name} не был обновлен");
                    }
                }

                // ✅ Обновляем словарь оригинальных параметров для кнопки "Восстановить"
                _originalParameters.Clear();
                foreach (var param in Parameters)
                {
                    _originalParameters[param.Name] = param.CurrentValue;
                }

                // ✅ Пересчитываем количество комбинаций
                UpdateTotalCombinations();

                // ✅ Принудительно обновляем UI
                OnPropertyChanged(nameof(Parameters));
                RefreshCommands();

                Debug.WriteLine("[RefreshOptimizationParameters] КОНЕЦ");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RefreshOptimizationParameters] ОШИБКА: {ex.Message}");
                Debug.WriteLine($"[RefreshOptimizationParameters] StackTrace: {ex.StackTrace}");
            }
        }


        private void ApplyParametersToStrategy(Dictionary<string, decimal> paramSet)
        {
            Debug.WriteLine($"[ApplyParametersToStrategy] НАЧАЛО для {_strategyType}");
            Debug.WriteLine($"[ApplyParametersToStrategy] paramSet.Count={paramSet.Count}");

            switch (_strategyType)
            {
                case "PairsTrading":
                    Debug.WriteLine("[ApplyParametersToStrategy] Применение PairsTrading");
                    ApplyPairsTradingParametersToReal(paramSet);
                    break;
                case "RSI":
                    Debug.WriteLine("[ApplyParametersToStrategy] Применение RSI");
                    ApplyRsiParametersToReal(paramSet);
                    break;
                case "MA":
                    Debug.WriteLine("[ApplyParametersToStrategy] Применение MA");
                    ApplyMaOptimizationParametersToReal(paramSet); // ✅ НОВЫЙ МЕТОД
                    break;
                case "Rating":
                    Debug.WriteLine("[ApplyParametersToStrategy] Применение Rating");
                    ApplyRatingParametersToReal(paramSet);
                    break;
                default:
                    Debug.WriteLine($"[ApplyParametersToStrategy] Неизвестный тип: {_strategyType}");
                    break;
            }
            Debug.WriteLine("[ApplyParametersToStrategy] КОНЕЦ");
        }

        private void ApplyPairsTradingParametersToReal(Dictionary<string, decimal> paramSet)
        {
            Debug.WriteLine("[ApplyPairsTradingParametersToReal] НАЧАЛО");
            var strategy = _strategyViewModel.PairsStrategy;
            if (strategy?.Parameters == null)
            {
                Debug.WriteLine("[ApplyPairsTradingParametersToReal] strategy или Parameters is null");
                return;
            }

            var p = strategy.Parameters;

            foreach (var kvp in paramSet)
            {
                Debug.WriteLine($"[ApplyPairsTradingParametersToReal] {kvp.Key}={kvp.Value}");
            }

            if (paramSet.TryGetValue("LookbackPeriod", out var lookback))
                p.LookbackPeriod = (int)lookback;
            if (paramSet.TryGetValue("EntryZScore", out var entryZ))
                p.EntryZScore = entryZ;
            if (paramSet.TryGetValue("ExitZScore", out var exitZ))
                p.ExitZScore = exitZ;
            if (paramSet.TryGetValue("StopLossZScore", out var stopLossZ))
                p.StopLossZScore = stopLossZ;
            if (paramSet.TryGetValue("PositionSizePercent", out var posSize))
                p.PositionSizePercent = posSize;

            p.ApplyParameters();
            Debug.WriteLine("[ApplyPairsTradingParametersToReal] Параметры применены");
            Debug.WriteLine("[ApplyPairsTradingParametersToReal] КОНЕЦ");
        }

        private void ApplyRsiParametersToReal(Dictionary<string, decimal> paramSet)
        {
            Debug.WriteLine("[ApplyRsiParametersToReal] НАЧАЛО");
            var strategy = _strategyViewModel.RsiStrategy;
            if (strategy?.Parameters == null)
            {
                Debug.WriteLine("[ApplyRsiParametersToReal] strategy или Parameters is null");
                return;
            }

            var p = strategy.Parameters;

            if (paramSet.TryGetValue("RsiPeriod", out var period))
                p.RsiPeriod = (int)period;
            if (paramSet.TryGetValue("RsiOverbought", out var overbought))
                p.RsiOverbought = overbought;
            if (paramSet.TryGetValue("RsiOversold", out var oversold))
                p.RsiOversold = oversold;
            if (paramSet.TryGetValue("OrderSizePercent", out var orderSize))
                p.OrderSizePercent = orderSize;
            if (paramSet.TryGetValue("TakeProfitPercent", out var tp))
                p.TakeProfitPercent = tp;
            if (paramSet.TryGetValue("StopLossPercent", out var sl))
                p.StopLossPercent = sl;

            p.ApplyParameters();
            Debug.WriteLine("[ApplyRsiParametersToReal] Параметры применены");
            Debug.WriteLine("[ApplyRsiParametersToReal] КОНЕЦ");
        }

        /// <summary>
        /// Применяет параметры оптимизации к реальной MA стратегии
        /// </summary>
        private void ApplyMaOptimizationParametersToReal(Dictionary<string, decimal> paramSet)
        {
            Debug.WriteLine("[ApplyMaOptimizationParametersToReal] НАЧАЛО");
            var strategy = _strategyViewModel.MaStrategy;
            if (strategy?.Parameters == null)
            {
                Debug.WriteLine("[ApplyMaOptimizationParametersToReal] strategy или Parameters is null");
                return;
            }

            var p = strategy.Parameters;

            // SMA периоды
            int smaShort = (int)(paramSet.TryGetValue("SmaShort", out var sShort) && sShort > 0 ? sShort : 20);
            int smaMedium = (int)(paramSet.TryGetValue("SmaMedium", out var sMedium) && sMedium > 0 ? sMedium : 50);
            int smaLong = (int)(paramSet.TryGetValue("SmaLong", out var sLong) && sLong > 0 ? sLong : 100);
            p.SmaPeriods = $"{smaShort},{smaMedium},{smaLong}";
            Debug.WriteLine($"[ApplyMaOptimizationParametersToReal] SMA: {p.SmaPeriods}");

            // EMA периоды
            int emaShort = (int)(paramSet.TryGetValue("EmaShort", out var eShort) && eShort > 0 ? eShort : 25);
            int emaMedium = (int)(paramSet.TryGetValue("EmaMedium", out var eMedium) && eMedium > 0 ? eMedium : 50);
            int emaLong = (int)(paramSet.TryGetValue("EmaLong", out var eLong) && eLong > 0 ? eLong : 100);
            p.EmaPeriods = $"{emaShort},{emaMedium},{emaLong}";
            Debug.WriteLine($"[ApplyMaOptimizationParametersToReal] EMA: {p.EmaPeriods}");

            // Размер позиции
            if (paramSet.TryGetValue("PositionSizePercent", out var posSize))
            {
                p.PositionSizePercent = posSize;
                Debug.WriteLine($"[ApplyMaOptimizationParametersToReal] PositionSizePercent: {posSize}%");
            }

            // ✅ НОВЫЕ ПАРАМЕТРЫ ДЛЯ ATR
            if (paramSet.TryGetValue("StopLossATRMultiplier", out var slMultiplier))
            {
                p.StopLossATRMultiplier = slMultiplier;
                Debug.WriteLine($"[ApplyMaOptimizationParametersToReal] StopLossATRMultiplier: {slMultiplier}");
            }

            if (paramSet.TryGetValue("TakeProfitATRMultiplier", out var tpMultiplier))
            {
                p.TakeProfitATRMultiplier = tpMultiplier;
                Debug.WriteLine($"[ApplyMaOptimizationParametersToReal] TakeProfitATRMultiplier: {tpMultiplier}");
            }

            if (paramSet.TryGetValue("TrailingStopATRMultiplier", out var tsMultiplier))
            {
                p.TrailingStopATRMultiplier = tsMultiplier;
                Debug.WriteLine($"[ApplyMaOptimizationParametersToReal] TrailingStopATRMultiplier: {tsMultiplier}");
            }

            p.ApplyParameters();
            Debug.WriteLine("[ApplyMaOptimizationParametersToReal] КОНЕЦ");
        }

        private void ApplyRatingParametersToReal(Dictionary<string, decimal> paramSet)
        {
            Debug.WriteLine("[ApplyRatingParametersToReal] НАЧАЛО");
            var strategy = _strategyViewModel.RatingStrategy;
            if (strategy?.Parameters == null)
            {
                Debug.WriteLine("[ApplyRatingParametersToReal] strategy или Parameters is null");
                return;
            }

            var p = strategy.Parameters;

            if (paramSet.TryGetValue("EntryThreshold", out var threshold))
                p.EntryThreshold = (int)threshold;
            if (paramSet.TryGetValue("MatchTolerance", out var tolerance))
                p.MatchTolerance = tolerance;
            if (paramSet.TryGetValue("MinMatchPercentage", out var minMatch))
                p.MinMatchPercentage = (int)minMatch;
            if (paramSet.TryGetValue("PositionSizePercent", out var posSize))
                p.PositionSizePercent = posSize;

            p.ApplyParameters();
            Debug.WriteLine("[ApplyRatingParametersToReal] Параметры применены");
            Debug.WriteLine("[ApplyRatingParametersToReal] КОНЕЦ");
        }

        public void RestoreOriginalParameters()
        {
            Debug.WriteLine("[RestoreOriginalParameters] НАЧАЛО");
            Debug.WriteLine($"[RestoreOriginalParameters] _originalParameters.Count={_originalParameters.Count}");
            ApplyParametersToStrategy(_originalParameters);
            MessageBox.Show("Исходные параметры восстановлены", "Успех",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Debug.WriteLine("[RestoreOriginalParameters] КОНЕЦ");
        }

        #endregion

        #region Сортировка

        private void SortResults(string column)
        {
            Debug.WriteLine($"[SortResults] column={column}");
            if (string.IsNullOrEmpty(column)) return;

            if (SortColumn == column)
                SortAscending = !SortAscending;
            else
            {
                SortColumn = column;
                SortAscending = true;
            }

            Debug.WriteLine($"[SortResults] SortColumn={SortColumn}, SortAscending={SortAscending}");
            ApplySorting();
        }

        private void ApplySorting()
        {
            Debug.WriteLine("[ApplySorting] НАЧАЛО");
            if (string.IsNullOrEmpty(SortColumn)) return;

            var view = CollectionViewSource.GetDefaultView(Results);
            if (view == null)
            {
                Debug.WriteLine("[ApplySorting] view is null");
                return;
            }

            view.SortDescriptions.Clear();

            var sortDesc = new System.ComponentModel.SortDescription(SortColumn,
                SortAscending ? System.ComponentModel.ListSortDirection.Ascending :
                System.ComponentModel.ListSortDirection.Descending);

            view.SortDescriptions.Add(sortDesc);
            view.Refresh();

            // ✅ ПРИНУДИТЕЛЬНО ОБНОВЛЯЕМ UI И ОСВОБОЖДАЕМ ПАМЯТЬ
            GC.Collect();

            Debug.WriteLine($"[ApplySorting] Применена сортировка по {SortColumn}, Ascending={SortAscending}");
            Debug.WriteLine("[ApplySorting] КОНЕЦ");
        }

        internal void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }



        /// <summary>
        /// Виртуальный метод для освобождения ресурсов
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                Debug.WriteLine("[OptimizationViewModel] Начало освобождения управляемых ресурсов...");

                // 1. Отменяем выполнение оптимизации
                if (_cancellationTokenSource != null)
                {
                    Debug.WriteLine("[OptimizationViewModel] Отмена CancellationTokenSource...");
                    try
                    {
                        if (!_cancellationTokenSource.IsCancellationRequested)
                        {
                            _cancellationTokenSource.Cancel();
                        }
                        _cancellationTokenSource.Dispose();
                        _cancellationTokenSource = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OptimizationViewModel] Ошибка при отмене CancellationTokenSource: {ex.Message}");
                    }
                }

                // 2. Освобождаем BacktestEngine
                if (_backtestEngine != null)
                {
                    Debug.WriteLine($"[OptimizationViewModel] Освобождение BacktestEngine: {_backtestEngine.GetType().Name}");
                    try
                    {
                        if (_backtestEngine is IDisposable disposableEngine)
                        {
                            disposableEngine.Dispose();
                        }
                        else if (_backtestEngine is IAsyncDisposable asyncDisposable)
                        {
                            // Асинхронное освобождение - запускаем синхронно
                            asyncDisposable.DisposeAsync().GetAwaiter().GetResult();
                        }
                        _backtestEngine = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OptimizationViewModel] Ошибка при освобождении BacktestEngine: {ex.Message}");
                    }
                }

                // 3. Очищаем кэш данных для освобождения памяти
                if (_dataCache != null)
                {
                    Debug.WriteLine("[OptimizationViewModel] Очистка кэша данных...");
                    try
                    {
                        _dataCache.Candles?.Clear();
                        _dataCache.AlignedData?.Clear();
                        _dataCache.PairsModels?.Clear();
                        _dataCache = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OptimizationViewModel] Ошибка при очистке кэша: {ex.Message}");
                    }
                }

                // 4. Очищаем коллекции результатов
                if (Results != null)
                {
                    Debug.WriteLine($"[OptimizationViewModel] Очистка коллекции результатов ({Results.Count} элементов)...");
                    try
                    {
                        // Освобождаем память из каждого результата
                        foreach (var result in Results)
                        {
                            result.EquityHistory = null;
                            result.EquityDates = null;
                        }
                        Results.Clear();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OptimizationViewModel] Ошибка при очистке результатов: {ex.Message}");
                    }
                }

                // 5. Очищаем параметры
                if (Parameters != null)
                {
                    Debug.WriteLine($"[OptimizationViewModel] Очистка параметров ({Parameters.Count} элементов)...");
                    try
                    {
                        foreach (var param in Parameters)
                        {
                            param.PropertyChanged -= null;
                        }
                        Parameters.Clear();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OptimizationViewModel] Ошибка при очистке параметров: {ex.Message}");
                    }
                }

                // 6. Очищаем словарь оригинальных параметров
                _originalParameters?.Clear();

                // 7. Отписываемся от событий
                Debug.WriteLine("[OptimizationViewModel] Отписка от событий...");
                try
                {
                    this.PropertyChanged -= OnPropertyChanged;
                    this.PropertyChanged -= OnViewModelPropertyChanged;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OptimizationViewModel] Ошибка при отписке от событий: {ex.Message}");
                }

                Debug.WriteLine("[OptimizationViewModel] Управляемые ресурсы освобождены.");
            }


            // Освобождение неуправляемых ресурсов (если есть)
            // ...



            _disposed = true;
            Debug.WriteLine("[OptimizationViewModel] Ресурсы полностью освобождены.");
        }



        /// <summary>
        /// Финализатор (деструктор) - вызывается сборщиком мусора
        /// </summary>
        ~OptimizationViewModel()
        {
            Dispose(false);
        }



        #endregion
    }
}