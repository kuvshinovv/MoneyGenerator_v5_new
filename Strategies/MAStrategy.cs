using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Common;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.ViewModels;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MoneyGenerator_v5.Strategies
{
    public partial class MaStrategy
    {
        public string Name => "Multi-Timeframe MA Strategy";
        public string Type => "MA";

        private readonly ILogger _logger;
        private readonly IProvirerService _provider;
        private readonly StrategyViewModel _strategyViewModel;
        private readonly MaSettingsViewModel _parameters;
        private readonly MaViewModel _indicatorValues;
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

        // Периоды для стратегии - будут загружаться из настроек
        private List<int> _trendSmaPeriods = new(); // Для анализа тренда (длинные периоды)
        private List<int> _signalEmaPeriods = new(); // Для сигналов (короткие периоды)
        private int _filterSmaPeriod = 20; // SMA для фильтра
        private const int _atrPeriod = 14; // ATR период (обычно не меняют)

        // Состояние
        private bool _isBullishTrend;
        private bool _isBearishTrend;
        private string _trendStrength = "Нейтральный";
        private string _currentSignal = "ОЖИДАНИЕ";
        private decimal _entryPrice;
        private decimal _stopLoss;
        private decimal _takeProfit;

        //private bool _hasPosition = false;
        private DateTime _lastPositionCheck = DateTime.MinValue;

        private bool _isEntering = false; // Флаг блокировки входа
        private readonly object _entryLock = new object();
        private decimal _lastKnownPosition = 0;
        private readonly object _positionLock = new object();
        private DateTime _lastEntryAttempt = DateTime.MinValue;
        private const int ENTRY_COOLDOWN_SECONDS = 5; // Задержка между попытками входа
        private string _accountId;
        List<Position> _positionsList = new List<Position>();
        bool _checkPos = false;

        private bool _isExiting = false; // Флаг блокировки выхода
        private readonly object _exitLock = new object();
        private DateTime _lastExitAttempt = DateTime.MinValue;
        private const int EXIT_COOLDOWN_SECONDS = 5; // Задержка между попытками выхода
        string exitReason = "";

        private Dictionary<int, decimal> _previousEmaValues = new(); // Для хранения предыдущих значений EMA
        private decimal _previousEmaShort;
        private decimal _previousEmaMedium;

        public MaStrategy.MaSettingsViewModel Parameters => _parameters;

        public StrategyState State { get; set; } = StrategyState.Stopped;

        public event Action OnValuesUpdated;
        public event Action<decimal> OnPriceUpdated;


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






        public MaStrategy(
            ILogger<MaStrategy> logger,
            IProvirerService provider,
            ConnectionManager connectionManager,
            StrategyViewModel strategyViewModel,
            TransactionsService transactionsService,
            MainViewModel mainViewModel = null)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MaStrategy>.Instance;
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            //_connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            _strategyViewModel = strategyViewModel ?? throw new ArgumentNullException(nameof(strategyViewModel));
            _parameters = new MaSettingsViewModel();
            _indicatorValues = new MaViewModel();
            _transactionsService = transactionsService ?? throw new ArgumentNullException();

            /*// ✅ СОЗДАЕМ TransactionsService
            var transactionsLogger = logger as ILogger<TransactionsService> ??
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionsService>.Instance;
            _transactionsService = new TransactionsService(
                provider,
                mainViewModel,
                strategyViewModel,
                _strategyViewModel.Instrument,
                mainViewModel.SelectedAccount,
                transactionsLogger);*/


            _parameters.OnParametersChanged += OnParametersChanged;
            UpdatePeriodsFromSettings(); // Загружаем начальные настройки

            // ✅ ПОДПИСКА НА ОБНОВЛЕНИЕ СДЕЛОК
            if (_provider is TinkoffApiService tinkoffService)
            {
                tinkoffService.OnDealsUpdated += OnDealsUpdated;
            }
        }

        // ✅ ОБРАБОТЧИК ОБНОВЛЕНИЯ СДЕЛОК
        private async void OnDealsUpdated()
        {
            // Проверяем, не закрыта ли наша позиция
            await SyncPositionStateAsync();
        }
        public void OnParametersChanged()
        {
            // ✅ Проверяем сортировку перед обновлением
            var smaPeriods = _parameters.SmaPeriods
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.Parse(p.Trim()))
                .ToList();

            var emaPeriods = _parameters.EmaPeriods
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.Parse(p.Trim()))
                .ToList();

            var sortedSma = smaPeriods.OrderBy(p => p).ToList();
            var sortedEma = emaPeriods.OrderBy(p => p).ToList();

            bool smaSorted = smaPeriods.SequenceEqual(sortedSma);
            bool emaSorted = emaPeriods.SequenceEqual(sortedEma);

            if (!smaSorted || !emaSorted)
            {
                string message = "⚠️ Периоды НЕ ОТСОРТИРОВАНЫ!\n\n";
                if (!smaSorted)
                    message += $"SMA: {string.Join(",", smaPeriods)} → должно быть: {string.Join(",", sortedSma)}\n";
                if (!emaSorted)
                    message += $"EMA: {string.Join(",", emaPeriods)} → должно быть: {string.Join(",", sortedEma)}\n";
                message += "\nПериоды будут автоматически отсортированы.";

                MessageBox.Show(message, "Предупреждение: несортированные периоды",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            UpdatePeriodsFromSettings();
            _ = Task.Run(async () =>
            {
                await CalculateIndicators();
                await CalculatePositionSize();
                OnValuesUpdated?.Invoke();
            });
        }

        private void UpdatePeriodsFromSettings()
        {
            try
            {
                // ✅ Парсим периоды из настроек
                var allSmaPeriods = _parameters.SmaPeriods
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.Parse(p.Trim()))
                    .ToList();

                var allEmaPeriods = _parameters.EmaPeriods
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.Parse(p.Trim()))
                    .ToList();

                if (!allSmaPeriods.Any() || !allEmaPeriods.Any())
                {
                    _logger.LogWarning("No periods specified, using defaults");
                    allSmaPeriods = new List<int> { 20, 50, 100, 200, 300, 500 };
                    allEmaPeriods = new List<int> { 20, 50, 100, 200, 300, 500 };
                }

                // ✅ СОРТИРУЕМ периоды для корректной работы стратегии
                var sortedSma = allSmaPeriods.OrderBy(p => p).ToList();
                var sortedEma = allEmaPeriods.OrderBy(p => p).ToList();

                // ✅ ПРОВЕРЯЕМ, БЫЛИ ЛИ ПЕРИОДЫ НЕОТСОРТИРОВАНЫ
                bool smaWasUnsorted = !allSmaPeriods.SequenceEqual(sortedSma);
                bool emaWasUnsorted = !allEmaPeriods.SequenceEqual(sortedEma);

                if (smaWasUnsorted || emaWasUnsorted)
                {
                    Debug.WriteLine($"[MAStrategy] ⚠️ ВНИМАНИЕ: Периоды были автоматически отсортированы!");

                    if (smaWasUnsorted)
                    {
                        Debug.WriteLine($"[MAStrategy]   SMA было: [{string.Join(",", allSmaPeriods)}] -> стало: [{string.Join(",", sortedSma)}]");
                        _parameters.SmaPeriods = string.Join(",", sortedSma);
                    }

                    if (emaWasUnsorted)
                    {
                        Debug.WriteLine($"[MAStrategy]   EMA было: [{string.Join(",", allEmaPeriods)}] -> стало: [{string.Join(",", sortedEma)}]");
                        _parameters.EmaPeriods = string.Join(",", sortedEma);
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        string message = "⚠️ Периоды были автоматически отсортированы для корректной работы стратегии!\n\n";
                        if (smaWasUnsorted)
                            message += $"SMA: {string.Join(",", allSmaPeriods)} → {string.Join(",", sortedSma)}\n";
                        if (emaWasUnsorted)
                            message += $"EMA: {string.Join(",", allEmaPeriods)} → {string.Join(",", sortedEma)}\n";
                        message += "\nКороткий < Средний < Длинный период";

                        MessageBox.Show(message, "Автоматическая сортировка периодов",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                }

                // ✅ Для тренда берем 3 самых длинных SMA периода (из отсортированных)
                _trendSmaPeriods = sortedSma
                    .OrderByDescending(p => p)
                    .Take(3)
                    .OrderBy(p => p)
                    .ToList();

                // ✅ Для сигналов берем первые 3 EMA периода (из отсортированных)
                _signalEmaPeriods = sortedEma.Take(3).ToList();

                // ✅ ✅ ✅ ИСПРАВЛЕНИЕ: Учитываем режим работы FilterSmaPeriod
                if (_parameters.UseManualFilterSma)
                {
                    // ✅ Ручной режим - используем значение из настроек
                    _filterSmaPeriod = _parameters.FilterSmaPeriod;
                    Debug.WriteLine($"[MAStrategy] Ручной режим FilterSmaPeriod = {_filterSmaPeriod}");
                }
                else
                {
                    // ✅ Автоматический режим - вычисляем как ближайший к 20
                    _filterSmaPeriod = sortedSma
                        .OrderBy(p => Math.Abs(p - 20))
                        .FirstOrDefault(20);

                    // ✅ Обновляем значение в настройках, чтобы пользователь видел актуальное
                    _parameters.FilterSmaPeriod = _filterSmaPeriod;
                    Debug.WriteLine($"[MAStrategy] Автоматический режим FilterSmaPeriod = {_filterSmaPeriod}");
                }









                // ✅ Проверяем, что фильтр существует в списке SMA
                if (!_smaValues.ContainsKey(_filterSmaPeriod))
                {
                    // Если фильтр не найден в списке, берем ближайший существующий
                    _filterSmaPeriod = sortedSma
                        .OrderBy(p => Math.Abs(p - _filterSmaPeriod))
                        .FirstOrDefault(20);

                    // Обновляем настройки, чтобы пользователь видел актуальное значение
                    if (_filterSmaPeriod != _parameters.FilterSmaPeriod)
                    {
                        _parameters.FilterSmaPeriod = _filterSmaPeriod;
                        Debug.WriteLine($"[MAStrategy] FilterSmaPeriod скорректирован до {_filterSmaPeriod}");
                    }
                }

                Debug.WriteLine($"[MAStrategy] Итоговые периоды:");
                Debug.WriteLine($"[MAStrategy]   Trend SMA: [{string.Join(",", _trendSmaPeriods)}]");
                Debug.WriteLine($"[MAStrategy]   Signal EMA: [{string.Join(",", _signalEmaPeriods)}]");
                Debug.WriteLine($"[MAStrategy]   Filter SMA: {_filterSmaPeriod} ({(_parameters.UseManualFilterSma ? "РУЧНОЙ" : "АВТО")})");

                // Очищаем и инициализируем словари для всех периодов
                _smaValues.Clear();
                _emaValues.Clear();

                foreach (var period in sortedSma)
                {
                    _smaValues[period] = 0;
                }

                foreach (var period in sortedEma)
                {
                    _emaValues[period] = 0;
                }

                _logger.LogInformation($"MA periods loaded - All SMA: [{string.Join(",", sortedSma)}], " +
            $"Trend SMA: [{string.Join(",", _trendSmaPeriods)}], " +
            $"Signal EMA: [{string.Join(",", _signalEmaPeriods)}], " +
            $"Filter SMA: {_filterSmaPeriod} ({(_parameters.UseManualFilterSma ? "MANUAL" : "AUTO")})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing MA periods, using defaults");

                // Значения по умолчанию (робастные)
                _trendSmaPeriods = new List<int> { 150, 300, 500 };
                _signalEmaPeriods = new List<int> { 25, 50, 100 };
                _filterSmaPeriod = 20;
                _parameters.FilterSmaPeriod = 20;
                _parameters.UseManualFilterSma = false;

                _smaValues.Clear();
                _emaValues.Clear();

                foreach (var period in new[] { 20, 50, 100, 150, 200, 300, 500 })
                {
                    _smaValues[period] = 0;
                }

                foreach (var period in new[] { 20, 25, 50, 100, 200 })
                {
                    _emaValues[period] = 0;
                }
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

            // ✅ ЛОГИРУЕМ LotSize ПРИ ИНИЦИАЛИЗАЦИИ
            Debug.WriteLine($"[MAStrategy] InitializeAsync: Инструмент {_instrument.Ticker}, LotSize={_instrument.LotSize}, MinLotSize={_instrument.MinLotSize}");

            _accountId = await GetAccountIdAsync();

            // Автоматически устанавливаем оптимальные параметры для выбранного таймфрейма
            MaOptimalParameters.ApplySettingsToViewModel(_parameters, _timeframe);
            _logger.LogInformation($"Optimal parameters set for timeframe {_timeframe}: SMA={_parameters.SmaPeriods}, EMA={_parameters.EmaPeriods}");

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

            _logger.LogInformation($"MA strategy initialized for {_instrument.Ticker}");
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
                    //_hasPosition = true;
                    _lastKnownPosition = position.Quantity;
                    _logger.LogInformation($"Restored position from DB: {position.Quantity} lots at {position.EntryPrice}");
                }
                else
                {
                    _currentPosition = null;
                    //_hasPosition = false;
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
            if (_quoteCache.Count < 500) // Нужно минимум 500 свечей для расчета всех периодов
            {
                _logger.LogDebug($"Not enough candles for calculation: {_quoteCache.Count} < 500");
                return;
            }

            try
            {
                // Создаем временную копию кэша с добавлением текущей цены как "виртуальной свечи"
                var workingQuotes = new List<Quote>(_quoteCache);

                // Добавляем текущую цену как последнюю свечу для более точных расчетов
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

                // Расчет SMA для всех периодов из настроек
                foreach (var period in _smaValues.Keys.ToList())
                {
                    try
                    {
                        var sma = workingQuotes.GetSma(period).ToList();
                        if (sma.Any() && sma.Last().Sma.HasValue)
                        {
                            _smaValues[period] = (decimal)sma.Last().Sma.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Error calculating SMA {period}: {ex.Message}");
                    }
                }

                // Расчет EMA для всех периодов из настроек
                foreach (var period in _emaValues.Keys.ToList())
                {
                    try
                    {
                        var ema = workingQuotes.GetEma(period).ToList();
                        if (ema.Any() && ema.Last().Ema.HasValue)
                        {
                            _emaValues[period] = (decimal)ema.Last().Ema.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug($"Error calculating EMA {period}: {ex.Message}");
                    }
                }


                // После расчета новых EMA, сохраняем предыдущие значения
                var signalPeriods = _signalEmaPeriods.OrderBy(p => p).ToList();
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



                // Расчет ATR
                var atr = workingQuotes.GetAtr(_atrPeriod).ToList();
                if (atr.Any() && atr.Last().Atr.HasValue)
                {
                    _atrValue = (decimal)atr.Last().Atr.Value;
                }


                // Логируем все SMA значения
                //var smaLog = string.Join(", ", _smaValues
                //    .Where(kv => kv.Value > 0)
                //    .OrderBy(kv => kv.Key)
                //    .Select(kv => $"SMA{kv.Key}: {kv.Value:F2}"));

                //Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Calculated SMA: {smaLog}");

                // Логируем все EMA значения
                //var emaLog = string.Join(", ", _emaValues
                //    .Where(kv => kv.Value > 0)
                //    .OrderBy(kv => kv.Key)
                //    .Select(kv => $"EMA{kv.Key}: {kv.Value:F2}"));

                //Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Calculated EMA: {emaLog}");

                // Логируем все ATR значения
                // Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Calculated ATR: {_atrValue}");


                AnalyzeTrend();
                await GenerateSignals();
                await UpdateIndicatorValues();

                _lastCalculation = DateTime.Now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating indicators");
            }
        }

        private void AnalyzeTrend()
        {
            if (_trendSmaPeriods.Count < 3)
            {
                _isBullishTrend = false;
                _isBearishTrend = false;
                _trendStrength = "Недостаточно данных";
                return;
            }

            // Берем три самых длинных периода для анализа тренда
            var sortedPeriods = _trendSmaPeriods.OrderBy(p => p).ToList();
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

            // Бычий тренд: короткая SMA > средняя SMA > длинная SMA
            _isBullishTrend = smaShort > smaMedium && smaMedium > smaLong;

            // Медвежий тренд: короткая SMA < средняя SMA < длинная SMA
            _isBearishTrend = smaShort < smaMedium && smaMedium < smaLong;

            // Оценка силы тренда
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
            // Проверяем наличие данных для сигнальных EMA
            var signalPeriods = _signalEmaPeriods.OrderBy(p => p).ToList();


            // ИСПРАВЛЕНИЕ: Добавляем проверку на количество и наличие значений
            if (signalPeriods.Count < 3)
            {
                _currentSignal = "⏸️ НЕТ ДАННЫХ (мало периодов)";
                _indicatorValues.CurrentSignal = _currentSignal;
                _logger.LogDebug($"Not enough signal periods: {signalPeriods.Count}");
                return;
            }

            var shortEma = signalPeriods[0];
            var mediumEma = signalPeriods[1];
            var longEma = signalPeriods[2];

            // Проверяем, что все необходимые EMA рассчитаны
            if (!_emaValues.ContainsKey(shortEma) || _emaValues[shortEma] == 0 ||
                !_emaValues.ContainsKey(mediumEma) || _emaValues[mediumEma] == 0 ||
                !_emaValues.ContainsKey(longEma) || _emaValues[longEma] == 0)
            {
                _currentSignal = "⏸️ НЕТ ДАННЫХ (расчет EMA)";
                _indicatorValues.CurrentSignal = _currentSignal;
                _logger.LogDebug($"EMA values not calculated: " +
                    $"EMA{shortEma}={_emaValues.GetValueOrDefault(shortEma)}, " +
                    $"EMA{mediumEma}={_emaValues.GetValueOrDefault(mediumEma)}, " +
                    $"EMA{longEma}={_emaValues.GetValueOrDefault(longEma)}");
                return;
            }

            var emaShort = _emaValues[shortEma];
            var emaMedium = _emaValues[mediumEma];
            var emaLong = _emaValues[longEma];

            // Используем фильтр SMA
            var smaFilter = _smaValues.ContainsKey(_filterSmaPeriod) ? _smaValues[_filterSmaPeriod] : 0;







            // Обновляем позицию из кэша если нужно
            if ((DateTime.Now - _lastPositionCheck).TotalMilliseconds > 5000)
            {
                if (_currentPosition == null || _currentPosition.Quantity == 0)
                {
                    await RestorePositionFromDbAsync();
                }
                
            }






            // Если есть позиция - не генерируем сигналы на вход
            if (_currentPosition != null && _currentPosition?.Quantity != 0)
            {
                string direction = _currentPosition.Quantity > 0 ? "LONG" : "SHORT";
                _currentSignal = $"⏸️ В ПОЗИЦИИ ({direction} {Math.Abs(_currentPosition.Quantity)} лотов)";
                _indicatorValues.CurrentSignal = _currentSignal;
                _indicatorValues.SignalDescription = $"Ожидание выхода. Вход: {_currentPosition.EntryPrice:F2}";
                _indicatorValues.SignalColor = Brushes.Gray;

                await _transactionsService.UpdateOpenDealsPnLAsync(_instrument.Uid, _indicatorValues.CurrentPrice);

                return;
            }








            // Сигнал на ПОКУПКУ (Long)
            if (_isBullishTrend && _currentPrice > smaFilter)
            {
                bool emaBullish = emaShort > emaMedium && emaMedium > emaLong;
                bool priceAtEmaLong = Math.Abs(_currentPrice - emaLong) / emaLong < 0.005m;
                bool priceAtEmaMedium = Math.Abs(_currentPrice - emaMedium) / emaMedium < 0.003m;

                if (emaBullish && (priceAtEmaLong || priceAtEmaMedium))
                {
                    _currentSignal = "📈 LONG (Откат к EMA)";
                    CalculateEntryPrices("Long");
                }
                else
                {
                    _currentSignal = "⏸️ Ожидание LONG";
                }
            }
            // Сигнал на ПРОДАЖУ (Short)
            else if (_isBearishTrend && _currentPrice < smaFilter)
            {
                bool emaBearish = emaShort < emaMedium && emaMedium < emaLong;
                bool priceAtEmaLong = Math.Abs(_currentPrice - emaLong) / emaLong < 0.005m;
                bool priceAtEmaMedium = Math.Abs(_currentPrice - emaMedium) / emaMedium < 0.003m;

                if (emaBearish && (priceAtEmaLong || priceAtEmaMedium))
                {
                    _currentSignal = "📉 SHORT (Откат к EMA)";
                    CalculateEntryPrices("Short");
                }
                else
                {
                    _currentSignal = "⏸️ Ожидание SHORT";
                }
            }
            else
            {
                _currentSignal = "⏸️ ОЖИДАНИЕ";
            }

            _indicatorValues.CurrentSignal = _currentSignal;

            // Обновляем описание сигнала
            if (_currentSignal.Contains("LONG"))
            {
                _indicatorValues.SignalDescription = $"Цена: {_currentPrice:F2}, Стоп: {_stopLoss:F2}, Тейк: {_takeProfit:F2}";
                _indicatorValues.SignalColor = Brushes.Green;
            }
            else if (_currentSignal.Contains("SHORT"))
            {
                _indicatorValues.SignalDescription = $"Цена: {_currentPrice:F2}, Стоп: {_stopLoss:F2}, Тейк: {_takeProfit:F2}";
                _indicatorValues.SignalColor = Brushes.Red;
            }
            else
            {
                _indicatorValues.SignalDescription = "Ожидание условий для входа";
                _indicatorValues.SignalColor = Brushes.Gray;
            }
        }

        private void CalculateEntryPrices(string direction)
        {
            _entryPrice = _currentPrice;

            // Получаем множители из настроек
            decimal slMultiplier = _parameters.StopLossATRMultiplier;
            decimal tpMultiplier = _parameters.TakeProfitATRMultiplier;

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] CalculateEntryPrices: direction={direction}, SL multiplier={slMultiplier}, TP multiplier={tpMultiplier}, ATR={_atrValue:F4}");

            if (direction == "Long")
            {
                _stopLoss = _entryPrice - _atrValue * slMultiplier;
                _takeProfit = _entryPrice + _atrValue * tpMultiplier;
            }
            else
            {
                _stopLoss = _entryPrice + _atrValue * slMultiplier;
                _takeProfit = _entryPrice - _atrValue * tpMultiplier;
            }

            _indicatorValues.EntryPrice = _entryPrice;
            _indicatorValues.StopLossPrice = _stopLoss;
            _indicatorValues.TakeProfitPrice = _takeProfit;

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Уровни: SL={_stopLoss:F4}, TP={_takeProfit:F4}");
        }

       

        public bool ShouldExit(string direction)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] ShouldExit НАЧАЛО для {direction}");

            if (_currentPosition == null || _currentPosition.Quantity == 0)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Нет позиции, выход не требуется");
                return false;
            }

            // Проверяем кулдаун выхода
            if (_isExiting)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Выход уже выполняется, пропускаем");
                return false;
            }

            if ((DateTime.Now - _lastExitAttempt).TotalSeconds < EXIT_COOLDOWN_SECONDS)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Кулдаун выхода: {(DateTime.Now - _lastExitAttempt).TotalSeconds:F1}с < {EXIT_COOLDOWN_SECONDS}с");
                return false;
            }

            // Не выходим сразу после входа - даем время позиции развиться
            if (_currentPosition.EntryDateTime != null && _currentPosition.EntryDateTime != DateTime.MinValue)
            {
                TimeSpan timeInPosition = (TimeSpan)(DateTime.Now - _currentPosition.EntryDateTime);
                if (timeInPosition.TotalSeconds < 15)
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Слишком рано для выхода: {timeInPosition.TotalSeconds:F0}с в позиции");
                    return false;
                }
            }

            // Получаем параметры ATR из настроек
            decimal stopLossMultiplier = _parameters.StopLossATRMultiplier;
            decimal takeProfitMultiplier = _parameters.TakeProfitATRMultiplier;
            decimal trailingStopMultiplier = _parameters.TrailingStopATRMultiplier;

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] ATR множители: SL={stopLossMultiplier}, TP={takeProfitMultiplier}, TS={trailingStopMultiplier}");

            var signalPeriods = _signalEmaPeriods.OrderBy(p => p).ToList();
            if (signalPeriods.Count < 2)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] Недостаточно периодов EMA для выхода");
                return false;
            }

            var shortEma = signalPeriods[0];
            var mediumEma = signalPeriods[1];

            if (!_emaValues.ContainsKey(shortEma) || !_emaValues.ContainsKey(mediumEma))
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] EMA значения не рассчитаны");
                return false;
            }

            var emaShort = _emaValues[shortEma];
            var emaMedium = _emaValues[mediumEma];

            // Проверка на смену тренда
            bool trendChanged = false;
            if (direction == "Long")
            {
                trendChanged = _isBearishTrend;
            }
            else
            {
                trendChanged = _isBullishTrend;
            }

            bool shouldExit = false;
            string exitReason = "";
            decimal priceChangePercent = 0;

            // Расчет процентного изменения цены от входа
            if (_currentPosition.EntryPrice > 0)
            {
                if (direction == "Long")
                {
                    priceChangePercent = (_currentPrice - _currentPosition.EntryPrice) / _currentPosition.EntryPrice * 100;
                }
                else
                {
                    priceChangePercent = (_currentPosition.EntryPrice - _currentPrice) / _currentPosition.EntryPrice * 100;
                }
            }

            // ✅ СТОП-ЛОСС ПО ATR
            decimal atrStopLoss = direction == "Long"
                ? _currentPosition.EntryPrice - _atrValue * stopLossMultiplier
                : _currentPosition.EntryPrice + _atrValue * stopLossMultiplier;

            // ✅ ТЕЙК-ПРОФИТ ПО ATR
            decimal atrTakeProfit = direction == "Long"
                ? _currentPosition.EntryPrice + _atrValue * takeProfitMultiplier
                : _currentPosition.EntryPrice - _atrValue * takeProfitMultiplier;

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] ATR уровни: StopLoss={atrStopLoss:F4}, TakeProfit={atrTakeProfit:F4}, текущая цена={_currentPrice:F4}");

            if (direction == "Long")
            {
                // ✅ СТОП-ЛОСС ПО ATR
                if (_currentPrice <= atrStopLoss)
                {
                    shouldExit = true;
                    exitReason = $"Стоп-лосс по ATR (SL={stopLossMultiplier}xATR)";
                }
                // ✅ ТЕЙК-ПРОФИТ ПО ATR
                else if (_currentPrice >= atrTakeProfit)
                {
                    shouldExit = true;
                    exitReason = $"Тейк-профит по ATR (TP={takeProfitMultiplier}xATR)";
                }
                // ✅ ПРОБОЙ СРЕДНЕЙ EMA
                else if (_currentPrice < emaMedium * 0.995m)
                {
                    shouldExit = true;
                    exitReason = "Пробой средней EMA вниз";
                }
                // ✅ ПЕРЕСЕЧЕНИЕ EMA
                else if (emaShort < emaMedium && _previousEmaShort > _previousEmaMedium)
                {
                    shouldExit = true;
                    exitReason = "Пересечение EMA (короткая ниже средней)";
                }
                // ✅ СМЕНА ТРЕНДА
                else if (trendChanged)
                {
                    shouldExit = true;
                    exitReason = "Смена тренда на медвежий";
                }
                // ✅ ТРЕЙЛИНГ-СТОП ПО ATR (только если есть прибыль)
                else if (priceChangePercent > 1.0m)
                {
                    decimal trailingStop = GetTrailingStop("Long", trailingStopMultiplier);
                    if (_currentPrice < trailingStop)
                    {
                        shouldExit = true;
                        exitReason = $"Трейлинг-стоп (TS={trailingStopMultiplier}xATR)";
                    }
                }
            }
            else // Short
            {
                // ✅ СТОП-ЛОСС ПО ATR
                if (_currentPrice >= atrStopLoss)
                {
                    shouldExit = true;
                    exitReason = $"Стоп-лосс по ATR (SL={stopLossMultiplier}xATR)";
                }
                // ✅ ТЕЙК-ПРОФИТ ПО ATR
                else if (_currentPrice <= atrTakeProfit)
                {
                    shouldExit = true;
                    exitReason = $"Тейк-профит по ATR (TP={takeProfitMultiplier}xATR)";
                }
                // ✅ ПРОБОЙ СРЕДНЕЙ EMA
                else if (_currentPrice > emaMedium * 1.005m)
                {
                    shouldExit = true;
                    exitReason = "Пробой средней EMA вверх";
                }
                // ✅ ПЕРЕСЕЧЕНИЕ EMA
                else if (emaShort > emaMedium && _previousEmaShort < _previousEmaMedium)
                {
                    shouldExit = true;
                    exitReason = "Пересечение EMA (короткая выше средней)";
                }
                // ✅ СМЕНА ТРЕНДА
                else if (trendChanged)
                {
                    shouldExit = true;
                    exitReason = "Смена тренда на бычий";
                }
                // ✅ ТРЕЙЛИНГ-СТОП ПО ATR (только если есть прибыль)
                else if (priceChangePercent > 1.0m)
                {
                    decimal trailingStop = GetTrailingStop("Short", trailingStopMultiplier);
                    if (_currentPrice > trailingStop)
                    {
                        shouldExit = true;
                        exitReason = $"Трейлинг-стоп (TS={trailingStopMultiplier}xATR)";
                    }
                }
            }

            // Обновляем предыдущие значения EMA
            _previousEmaValues[mediumEma] = emaMedium;
            _previousEmaValues[shortEma] = emaShort;

            if (shouldExit)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] СИГНАЛ НА ВЫХОД для {direction}: {exitReason}");
            }

            return shouldExit;
        }

        /// <summary>
        /// Получение уровня трейлинг-стопа с использованием ATR множителя
        /// </summary>
        private decimal GetTrailingStop(string direction, decimal atrMultiplier)
        {
            if (_currentPosition == null) return 0;

            if (direction == "Long")
            {
                // Трейлинг-стоп от максимальной цены с момента входа
                decimal highestPrice = Math.Max(_currentPrice, _currentPosition.EntryPrice);
                return highestPrice - _atrValue * atrMultiplier;
            }
            else
            {
                // Трейлинг-стоп от минимальной цены с момента входа
                decimal lowestPrice = Math.Min(_currentPrice, _currentPosition.EntryPrice);
                return lowestPrice + _atrValue * atrMultiplier;
            }
        }



       

        private async Task UpdateIndicatorValues()
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Создаем НОВЫЕ словари вместо обновления существующих
                var newSmaValues = new ObservableDictionary<int, decimal>();
                foreach (var kvp in _smaValues.Where(kv => kv.Value > 0))
                {
                    newSmaValues.Add(kvp.Key, kvp.Value);
                }
                _indicatorValues.SmaValues = newSmaValues;

                var newEmaValues = new ObservableDictionary<int, decimal>();
                foreach (var kvp in _emaValues.Where(kv => kv.Value > 0))
                {
                    newEmaValues.Add(kvp.Key, kvp.Value);
                }
                _indicatorValues.EmaValues = newEmaValues;

                _indicatorValues.TrendDescription = $"{(_isBullishTrend ? "БЫЧИЙ" : _isBearishTrend ? "МЕДВЕЖИЙ" : "НЕЙТРАЛЬНЫЙ")} ({_trendStrength})";

                //Debug.WriteLine($"DEBUG: MAStrategy UpdateIndicatorValues - UI Updated: " +
                //               $"SMA count={newSmaValues.Count}, " +
                //               $"EMA count={newEmaValues.Count}, " +
                //               $"Price={_currentPrice:F2}, " +
                //               $"Signal={_currentSignal}");

                OnValuesUpdated?.Invoke();
            });
        }

        /// <summary>
        /// Расчет размера позиции для MA стратегии
        /// Исправлена формула расчета количества лотов для российского рынка
        /// </summary>
        private async Task CalculatePositionSize()
        {
            try
            {
                if (_instrument == null || _currentPrice <= 0)
                {
                    Debug.WriteLine($"[MAStrategy] CalculatePositionSize: Инструмент null или цена <= 0");
                    return;
                }

                decimal availableAmount = 0;

                // ✅ ЛОГИРУЕМ ИСХОДНЫЕ ДАННЫЕ
                Debug.WriteLine($"[MAStrategy] ========== РАСЧЕТ РАЗМЕРА ПОЗИЦИИ ==========");
                Debug.WriteLine($"[MAStrategy] Инструмент: {_instrument.Ticker}");
                Debug.WriteLine($"[MAStrategy] Текущая цена: {_currentPrice:F2} RUB");
                Debug.WriteLine($"[MAStrategy] LotSize инструмента: {_instrument.LotSize}");
                Debug.WriteLine($"[MAStrategy] MinLotSize: {_instrument.MinLotSize}");

                // ✅ ПОЛУЧАЕМ БАЛАНС
                var balance = await _provider.GetAccountBalanceAsync();
                Debug.WriteLine($"[MAStrategy] Баланс счета: {balance:F2} RUB");

                // ✅ РАССЧИТЫВАЕМ ДОСТУПНУЮ СУММУ
                if (_parameters.PositionSizeType == "Percent")
                {
                    availableAmount = balance * (_parameters.PositionSizePercent / 100);
                    _indicatorValues.PositionSizeValue = _parameters.PositionSizePercent;
                    Debug.WriteLine($"[MAStrategy] Тип: Процент от депозита");
                    Debug.WriteLine($"[MAStrategy] Процент: {_parameters.PositionSizePercent}%");
                }
                else
                {
                    availableAmount = _parameters.PositionSizeAbsolute;
                    _indicatorValues.PositionSizeValue = _parameters.PositionSizeAbsolute;
                    Debug.WriteLine($"[MAStrategy] Тип: Абсолютное значение");
                    Debug.WriteLine($"[MAStrategy] Сумма: {_parameters.PositionSizeAbsolute} RUB");
                }

                Debug.WriteLine($"[MAStrategy] Доступная сумма для сделки: {availableAmount:F2} RUB");

                // ✅ РАССЧИТЫВАЕМ КОЛИЧЕСТВО ЛОТОВ ПО ПРАВИЛЬНОЙ ФОРМУЛЕ
                if (availableAmount > 0 && _instrument.LotSize > 0)
                {
                    // ✅ ПРАВИЛЬНАЯ ФОРМУЛА ДЛЯ РОССИЙСКОГО РЫНКА
                    // Лоты = Сумма / (Цена * Размер_лота)
                    decimal denominator = _currentPrice * _instrument.LotSize;
                    decimal calculatedLots = Math.Floor(availableAmount / denominator);

                    Debug.WriteLine($"[MAStrategy] Расчет:");
                    Debug.WriteLine($"[MAStrategy]   Знаменатель (Цена * LotSize) = {_currentPrice:F2} * {_instrument.LotSize} = {denominator:F2}");
                    Debug.WriteLine($"[MAStrategy]   Расчетное количество лотов = {availableAmount:F2} / {denominator:F2} = {calculatedLots:F2}");

                    // ✅ ОКРУГЛЯЕМ ВНИЗ ДО ЦЕЛОГО
                    _indicatorValues.PositionSizeLots = Math.Floor(calculatedLots);

                    // ✅ ПРОВЕРЯЕМ НА МИНИМАЛЬНЫЙ РАЗМЕР
                    if (_indicatorValues.PositionSizeLots < 1 && calculatedLots > 0)
                    {
                        Debug.WriteLine($"[MAStrategy] ⚠️ Расчетное количество лотов меньше 1 ({calculatedLots:F2}), округляем до 1");
                        _indicatorValues.PositionSizeLots = 1;
                    }

                    Debug.WriteLine($"[MAStrategy] ✅ Итоговое количество лотов: {_indicatorValues.PositionSizeLots}");

                    // ✅ ПРОВЕРКА: СКОЛЬКО АКЦИЙ В ЭТИХ ЛОТАХ
                    decimal totalShares = _indicatorValues.PositionSizeLots * _instrument.LotSize;
                    decimal totalCost = totalShares * _currentPrice;
                    Debug.WriteLine($"[MAStrategy]   Акций: {totalShares} (лоты * LotSize)");
                    Debug.WriteLine($"[MAStrategy]   Стоимость позиции: {totalCost:F2} RUB");
                    Debug.WriteLine($"[MAStrategy]   % от баланса: {(totalCost / balance * 100):F2}%");

                    // ✅ ПРОВЕРЯЕМ, НЕ ПРЕВЫШАЕТ ЛИ ПОЗИЦИЯ БАЛАНС
                    if (totalCost > balance)
                    {
                        Debug.WriteLine($"[MAStrategy] ⚠️ Стоимость позиции ({totalCost:F2}) превышает баланс ({balance:F2})!");
                        // Уменьшаем количество лотов до доступного
                        decimal maxLots = Math.Floor(balance / (_currentPrice * _instrument.LotSize));
                        Debug.WriteLine($"[MAStrategy]   Максимально возможное количество лотов: {maxLots}");
                        _indicatorValues.PositionSizeLots = Math.Min(_indicatorValues.PositionSizeLots, maxLots);
                        Debug.WriteLine($"[MAStrategy]   Уменьшено до: {_indicatorValues.PositionSizeLots} лотов");
                    }
                }
                else
                {
                    Debug.WriteLine($"[MAStrategy] ⚠️ Не удалось рассчитать позицию: availableAmount={availableAmount}, LotSize={_instrument.LotSize}");
                    _indicatorValues.PositionSizeLots = 0;
                }

                // ✅ СОХРАНЯЕМ БАЛАНС ДЛЯ ОТОБРАЖЕНИЯ В UI
                _indicatorValues.AccountBalance = balance;

                Debug.WriteLine($"[MAStrategy] ========== КОНЕЦ РАСЧЕТА ==========");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MAStrategy] ❌ ОШИБКА CalculatePositionSize: {ex.Message}");
                Debug.WriteLine($"[MAStrategy] StackTrace: {ex.StackTrace}");
                _logger.LogError(ex, "Error calculating position size");
                _indicatorValues.PositionSizeLots = 0;
            }
        }

        public async Task StartAsync()
        {
            State = StrategyState.Running;
            _logger.LogInformation($"MA strategy started for {_instrument.Ticker}");

            await CalculatePositionSize();
            await CalculateIndicators();

            _indicatorValues.StrategyStatus = "РАБОТАЕТ";
            _indicatorValues.StrategyStatusColor = Brushes.Green;
        }

        public async Task StopAsync()
        {
            State = StrategyState.Stopped;
            _logger.LogInformation($"MA strategy stopped for {_instrument.Ticker}");

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

                // ✅ ПЕРИОДИЧЕСКАЯ СИНХРОНИЗАЦИЯ ПОЗИЦИИ (каждые 30 секунд)
                if ((DateTime.Now - _lastPositionCheck).TotalSeconds > 30)
                {
                    await SyncPositionStateAsync();
                }


                // Если  _currentPosition не существует,то проверяем нет ли позиции в апи и если есть то создает ее  
                //  !_checkPos = это ключ который чрабатывает 1 раз и далее блокирует проверку..  чтобы не напрягала АПИ
                if (_currentPosition == null  && !_checkPos)
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


                            // И заодно проверяем тогда что есть в БД и пересисываем _currentPosition для полноты сведений
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
                        await ExecuteExitAsync(direction, exitReason);
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

            // Определяем время начала текущей свечи
            var candleStartTime = new DateTime(
                now.Year, now.Month, now.Day,
                now.Hour, (now.Minute / timeframeMinutes) * timeframeMinutes, 0);

            var lastQuote = _quoteCache.LastOrDefault();

            if (lastQuote == null || lastQuote.Date != candleStartTime)
            {
                // Создаем новую свечу
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
                // Обновляем существующую свечу
                lastQuote.High = Math.Max(lastQuote.High, marketData.LastPrice);
                lastQuote.Low = Math.Min(lastQuote.Low, marketData.LastPrice);
                lastQuote.Close = marketData.LastPrice;
                lastQuote.Volume++;
            }

            // Ограничиваем размер кэша
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
            // Блокируем повторные входы
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

                /// Проверяем, нет ли уже позиции (еще раз, для надежности)
                var currentPosition = await _provider.GetPositionAsync(_accountId, _instrument.Uid);
                if (currentPosition != 0)
                {
                    _logger.LogInformation($"ExecuteEntryAsync - {_instrument.Ticker} Cannot enter {direction} - position already exists: {currentPosition}");
                    return;
                }

                // Рассчитываем размер позиции
                await CalculatePositionSize();
                int quantity = (int)_indicatorValues.PositionSizeLots;

                if (quantity <= 0)
                {
                    _logger.LogWarning($"Cannot enter {direction} - invalid quantity: {quantity}");
                    return;
                }

                string orderDirection = direction == "Long" ? "Buy" : "Sell";

                _logger.LogInformation($"Placing {direction} entry order: {quantity} lots at {_currentPrice:F2}");

                // ✅ ИСПОЛЬЗУЕМ TRANSACTIONS SERVICE
                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: orderDirection,
                    quantity: quantity,
                    ticker: _instrument.Ticker,
                    isEntryOrder: true,
                    isExitOrder: false,
                    exitReason: reason,
                    accountId: _accountId);

                await Task.Delay(1000);

                if (result.IsSuccess)
                {
                    _currentPosition = new Position()
                    {
                        Ticker = _instrument.Ticker,
                        InstrumentUid = _instrument.Uid,
                        Direction = orderDirection,
                        Quantity = quantity,
                        EntryPrice = _currentPrice,
                        EntryOrderId = result.OrderId,
                        EntryDateTime = DateTime.Now,
                        EntryReason = reason,
                        Status = DealStatus.Open,
                    };

                    await _transactionsService.AddOpenDealAsync(
                        _instrument.Ticker,
                        _instrument.Uid,
                        this.Type,
                        _strategyViewModel.CurrentTimeframe,
                        DateTime.Now,
                        _currentPrice,
                        quantity,
                        result.OrderId,
                        orderDirection,
                        reason);


                    _logger.LogInformation($"Entry order placed successfully: {direction} {quantity} lots");
                    _indicatorValues.SignalDescription = $"Вход в {direction}: {quantity} лотов по {_currentPrice:F2}";


                    // Даем время на обновление позиции в API
                    await Task.Delay(1000);

                    // Форсируем проверку позиции
                    var pos = await _provider.GetPositionAsync(_accountId, _instrument.Uid);

                    // Сразу устанавливаем флаг наличия позиции
                    //_hasPosition = true;   Это точно работает
                    //_hasPosition = pos != 0;   // этот вариант проверим
                    _lastPositionCheck = DateTime.Now; // Сбрасываем таймер проверки

                    if (pos != 0)
                    {
                        _currentPosition.Quantity = (int)pos;
                    }

                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] DEBUG: {_instrument.Ticker} Force position check after entry: hasPosition={_currentPosition.Quantity}, quantity={pos}");
                }
                else
                {
                    _logger.LogError($"Failed to place entry order: {result.ErrorMessage}");
                    _indicatorValues.SignalDescription = $"Ошибка входа: {result.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing entry order");
                _indicatorValues.SignalDescription = $"Ошибка входа: {ex.Message}";
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
            // Блокируем повторные выходы
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
                // Получаем текущую позицию из API
                var currentQty = (int)Math.Abs(await _provider.GetPositionAsync(_accountId, _instrument.Uid));

                if (currentQty == 0)
                {
                    _logger.LogWarning($"Cannot exit - no position found");
                    return;
                }



                // ВСЕГДА загружаем актуальную позицию из БД перед выходом
                await RestorePositionFromDbAsync();

                if (_currentPosition == null || string.IsNullOrEmpty(_currentPosition.EntryOrderId))
                {
                    _logger.LogError("Cannot exit - position not found in DB");
                    return;
                }

                string orderDirection = direction == "Long" ? "Sell" : "Buy";

                _logger.LogInformation($"Placing {direction} exit order: {currentQty} lots at {_currentPrice:F2}");

                // ✅ ИСПОЛЬЗУЕМ TRANSACTIONS SERVICE
                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: orderDirection,
                    quantity: currentQty,
                    ticker: _instrument.Ticker,
                    isEntryOrder: false,
                    isExitOrder: true,
                    exitReason: reason,
                    accountId: _accountId);

                await Task.Delay(1000);

                if (result.IsSuccess)
                {
                    _logger.LogInformation($"Exit order placed successfully: {result.OrderId}");


                    // Ждем обновления позиции
                    for (int i = 0; i < 10; i++) // 5 секунд ожидания
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




                    // Рассчитываем P&L
                    // Явное вычисление с проверкой типа
                    decimal pnl = 0;
                    decimal pnlPercent = 0;
                    decimal priceDiff = 0;

                    if (_currentPosition.Direction == PositionDirection.Long || _currentPosition.Direction == "Long" || _currentPosition.Direction == "Buy")
                    {
                        priceDiff = _currentPrice - _currentPosition.EntryPrice;
                        pnl = priceDiff * _currentPosition.Quantity * _instrument.LotSize;
                        pnlPercent = _currentPosition.EntryPrice > 0
                            ? priceDiff / _currentPosition.EntryPrice * 100
                            : 0;
                    }
                    else if (_currentPosition.Direction == PositionDirection.Short || _currentPosition.Direction == "Short" || _currentPosition.Direction == "Sell")
                    {
                        priceDiff = _currentPosition.EntryPrice - _currentPrice;
                        pnl = priceDiff * _currentPosition.Quantity * _instrument.LotSize;
                        pnlPercent = _currentPosition.EntryPrice > 0
                            ? priceDiff / _currentPosition.EntryPrice * 100
                            : 0;
                    }


                    // Закрываем сделку в БД
                    bool dealClosed = await _transactionsService.CloseDealAsync(
                        _instrument.Uid,
                        _currentPosition.EntryOrderId,
                        DateTime.Now,
                        _currentPrice,
                        result.OrderId,
                        pnl,
                        pnlPercent,
                        reason
                    );


                    // Все равно сбрасываем позицию, так как она закрыта
                    _logger.LogInformation($"Deal closed: P&L={pnl:F2} ({pnlPercent:F2}%)");
                    _indicatorValues.SignalDescription = $"Выход: {reason}, P&L={pnl:F2} ({pnlPercent:F2}%)";

                    // Сбрасываем позицию
                    _currentPosition = null;
                    ResetExitVariables();
                }
                else
                {
                    _logger.LogError($"Failed to place exit order: {result.ErrorMessage}");
                    _indicatorValues.SignalDescription = $"Ошибка выхода: {result.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing exit order");
                _indicatorValues.SignalDescription = $"Ошибка выхода: {ex.Message}";
            }
            finally
            {
                lock (_exitLock)
                {
                    _isExiting = false;
                }
            }
        }

       /* public async Task RestoreAsync()
        {
            await LoadHistoricalDataAsync();
            await RestorePositionFromDbAsync();
            _logger.LogInformation($"MA strategy restored for {_instrument.Ticker}");
        }*/

        /*public async Task<decimal> GetCurrentPositionDirectAsync(string accountId, string instrumentUid)
        {
            try
            {
                // Получаем позиции через уже существующий метод
                var positions = await _provider.GetPositionsAsync();

                if (positions == null || !positions.Any())
                {
                    _logger.LogDebug("No positions found");
                    return 0;
                }

                // Ищем позицию по instrumentUid или Figi
                var position = positions.FirstOrDefault(p =>
                    p.InstrumentUid == instrumentUid ||
                    p.Figi == instrumentUid ||
                    p.Ticker == _instrument?.Ticker); // Добавляем поиск по тикеру для надежности

                if (position != null)
                {
                    _logger.LogDebug($"Found position for {instrumentUid}: Quantity={position.Quantity}, " +
                                    $"Ticker={position.Ticker}, LastUpdate={position.LastUpdate:HH:mm:ss.fff}");
                    return position.Quantity;
                }

                _logger.LogDebug($"No position found for {instrumentUid}");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting position directly for {InstrumentUid}", instrumentUid);
                return 0;
            }
        }*/


        public void ResetExitVariables()
        {
            // Сбраываем после всех манипуляций
            _currentPosition = null;
            //_hasPosition = false;
            _lastKnownPosition = 0;
            _lastPositionCheck = DateTime.Now;
            _checkPos = false;
            _logger.LogDebug("Exit variables reset");

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] DEBUG: ResetExitVariables ======================================3 ОБНУЛИЛИ ПЕРЕМЕННЫЕ ПОЗИЦИИ в стратегии");
        }


        #region Обновление позиции
        /// <summary>
        /// Синхронизация состояния позиции с реальным положением
        /// </summary>
        public async Task SyncPositionStateAsync()
        {
            try
            {
                // Получаем текущую позицию из API
                var currentQty = await _provider.GetPositionAsync(_accountId, _instrument.Uid);

                // Получаем открытую сделку из БД
                var openDeals = await _transactionsService.ReadDBOpenDealsAsync();
                var openDeal = openDeals.FirstOrDefault(d => d.Ticker == _instrument.Ticker);

                bool hasPosition = currentQty != 0 || openDeal != null;

                if (!hasPosition && (_currentPosition != null && _currentPosition.Quantity != 0))
                {
                    // Позиция закрыта извне, сбрасываем состояние
                    _logger.LogInformation($"Position for {_instrument.Ticker} was closed externally. Resetting state.");

                    // Сбрасываем все переменные состояния
                    _currentPosition = null;
                    _lastKnownPosition = 0;
                    _checkPos = false;

                    // Сбрасываем флаги входа/выхода
                    lock (_entryLock)
                    {
                        _isEntering = false;
                    }
                    lock (_exitLock)
                    {
                        _isExiting = false;
                    }

                    // Сбрасываем сигнал
                    _currentSignal = "⏸️ ОЖИДАНИЕ (позиция закрыта извне)";
                    _indicatorValues.CurrentSignal = _currentSignal;
                    _indicatorValues.SignalDescription = "Позиция закрыта, мониторинг сигналов возобновлен";
                    _indicatorValues.SignalColor = Brushes.Gray;

                    // Обновляем UI
                    await UpdateIndicatorValues();

                    _logger.LogInformation($"Position state reset for {_instrument.Ticker}");
                }
                else if (hasPosition && (_currentPosition == null || _currentPosition.Quantity == 0))
                {
                    // Есть позиция, но у нас нет - восстанавливаем
                    _logger.LogInformation($"Position exists for {_instrument.Ticker} but not tracked. Restoring...");
                    await RestorePositionFromDbAsync();
                }

                _lastPositionCheck = DateTime.Now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing position state");
            }
        }
        #endregion




        #region UI Methods
        public object GetSettingsView()
        {
            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10)
            };

            var title = new TextBlock
            {
                Text = "Настройки Multi-Timeframe MA стратегии",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 15)
            };
            stackPanel.Children.Add(title);

           /* var infoText = new TextBlock
            {
                Text = 
                       "Трендовые SMA: 150,300,500 (для анализа тренда)\n" +
                       "Сигнальные EMA: 25,50,100 (для точек входа)\n" +
                       "\n" +
                       "Оптимальные настройки для разных таймфреймов:\n" +
                       "\n" +
                       "Для 1-min таймфрейма\n" +
                       "trend: { 300, 600, 1200 }    5-20 часов\n" +
                       "signal: { 50, 100, 200 }     50 мин - 3.3 часа\n" +
                       "\n" +
                       "Для 5-min таймфрейма (текущие)\n" +
                       "trend: { 150, 300, 500 }     12.5 - 41.7 часов\n" +
                       "signal: { 25, 50, 100 }      2 - 8.3 часов\n" +
                       "\n" +
                       "Для 15-min таймфрейма \n" +
                       "trend: { 100, 200, 300 }     25 - 75 часов\n" +
                       "signal: { 20, 40, 80 }       5 - 20 часов\n" +
                       "\n" +
                       "Для 1-hour таймфрейма\n" +
                       "trend: { 50, 100, 200 }      2 - 8 дней\n" +
                       "signal: { 12, 24, 48 }       12 часов - 2 дня\n",
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10),
                FontSize = 11
            };
            stackPanel.Children.Add(infoText);*/



            // Информационный текст с оптимальными настройками для текущего таймфрейма
            var currentSettings = MaOptimalParameters.GetSettings(_timeframe);
            var infoText = new TextBlock
            {
                Text =
                       $"📊 ТЕКУЩИЙ ТАЙМФРЕЙМ: {_timeframe}\n" +
                       $"📈 Оптимальные настройки для этого таймфрейма:\n" +
                       $"   SMA: {currentSettings.SmaPeriods}\n" +
                       $"   EMA: {currentSettings.EmaPeriods}\n" +
                       $"ℹ️ {currentSettings.Description}\n" +
                       "\n" +
                       "Вы можете использовать кнопку 'Оптимальные' для автоматической установки\n" +
                       "рекомендованных параметров для текущего таймфрейма.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            stackPanel.Children.Add(infoText);





            var smaPanel = CreateParameterPanel(
                 "Все периоды SMA (через запятую):",
                 _parameters,
                 nameof(MaSettingsViewModel.SmaPeriods),
                 "Пример: 20,50,100,150,200,300,500");
            stackPanel.Children.Add(smaPanel);

            var emaPanel = CreateParameterPanel(
                "Все периоды EMA (через запятую):",
                _parameters,
                nameof(MaSettingsViewModel.EmaPeriods),
                "Пример: 20,50,100,150,200");
            stackPanel.Children.Add(emaPanel);


            // ✅ ✅ ✅ НОВОЕ ПОЛЕ ДЛЯ FILTER SMA (С ЧЕКБОКСОМ)
            var filterSmaPanel = CreateParameterPanel(
                "Фильтр SMA (период):",
                _parameters,
                nameof(MaSettingsViewModel.FilterSmaPeriod),
                "Период SMA для фильтрации сигналов (обычно 20-200)");
            stackPanel.Children.Add(filterSmaPanel);

            // Добавляем информационную подсказку
            var filterInfoText = new TextBlock
            {
                Text = "ℹ️ Фильтр SMA используется для дополнительной проверки сигналов.\n" +
                       "Для LONG: цена должна быть ВЫШЕ фильтра.\n" +
                       "Для SHORT: цена должна быть НИЖЕ фильтра.\n" +
                       "Рекомендуемое значение: 20-50 для коротких таймфреймов, 100-200 для длинных.",
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            stackPanel.Children.Add(filterInfoText);



            stackPanel.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = Brushes.LightGray,
                Margin = new Thickness(0, 10, 0, 10)
            });

            var positionSizeTitle = new TextBlock
            {
                Text = "Настройки размера позиции",
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10)
            };
            stackPanel.Children.Add(positionSizeTitle);

            var typePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var typeLabel = new TextBlock
            {
                Text = "Тип расчета:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            typePanel.Children.Add(typeLabel);

            var percentRadio = new RadioButton
            {
                Content = "Процент от депозита",
                IsChecked = _parameters.PositionSizeType == "Percent",
                Margin = new Thickness(0, 0, 10, 0),
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

            stackPanel.Children.Add(typePanel);

            var percentPanel = CreateParameterPanel(
                "Размер позиции (%):",
                _parameters,
                nameof(MaSettingsViewModel.PositionSizePercent),
                "Процент от доступного депозита");
            stackPanel.Children.Add(percentPanel);

            var absolutePanel = CreateParameterPanel(
                "Размер позиции (в рублях):",
                _parameters,
                nameof(MaSettingsViewModel.PositionSizeAbsolute),
                "Фиксированная сумма в рублях");
            stackPanel.Children.Add(absolutePanel);

            var percentBinding = new Binding("PositionSizeType")
            {
                Source = _parameters,
                Converter = new PositionSizeTypeToVisibilityConverter(),
                ConverterParameter = "Percent"
            };
            percentPanel.SetBinding(StackPanel.VisibilityProperty, percentBinding);

            var absoluteBinding = new Binding("PositionSizeType")
            {
                Source = _parameters,
                Converter = new PositionSizeTypeToVisibilityConverter(),
                ConverterParameter = "Absolute"
            };
            absolutePanel.SetBinding(StackPanel.VisibilityProperty, absoluteBinding);


            // ✅ Проверяем, отсортированы ли периоды
            var smaPeriods = _parameters.SmaPeriods
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.Parse(p.Trim()))
                .ToList();

            var emaPeriods = _parameters.EmaPeriods
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.Parse(p.Trim()))
                .ToList();

            var sortedSma = smaPeriods.OrderBy(p => p).ToList();
            var sortedEma = emaPeriods.OrderBy(p => p).ToList();

            bool smaSorted = smaPeriods.SequenceEqual(sortedSma);
            bool emaSorted = emaPeriods.SequenceEqual(sortedEma);

            // ✅ Если периоды не отсортированы, показываем предупреждение
            if (!smaSorted || !emaSorted)
            {
                var warningText = new TextBlock
                {
                    Text = "⚠️ ВНИМАНИЕ! Периоды НЕ ОТСОРТИРОВАНЫ!\n" +
                           "Для корректной работы стратегии периоды должны быть:\n" +
                           "Короткий < Средний < Длинный\n\n" +
                           $"SMA: {string.Join(",", smaPeriods)} → должно быть: {string.Join(",", sortedSma)}\n" +
                           $"EMA: {string.Join(",", emaPeriods)} → должно быть: {string.Join(",", sortedEma)}",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 0)), // Красный
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 5, 0, 10),
                    TextWrapping = TextWrapping.Wrap
                };
                stackPanel.Children.Insert(2, warningText); // Вставляем после infoText
            }
            else
            {
                var okText = new TextBlock
                {
                    Text = "✅ Периоды корректно отсортированы (короткий < средний < длинный)",
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 150, 0)), // Зеленый
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 5, 0, 10)
                };
                stackPanel.Children.Insert(2, okText);
            }



            // Панель ATR параметров
            stackPanel.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = Brushes.LightGray,
                Margin = new Thickness(0, 10, 0, 10)
            });

            var atrTitle = new TextBlock
            {
                Text = "Настройки ATR (стоп-лосс и тейк-профит)",
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10)
            };
            stackPanel.Children.Add(atrTitle);

            var atrInfoText = new TextBlock
            {
                Text = "Множители ATR используются для расчета уровней стоп-лосса и тейк-профита.\n" +
            "Большой множитель = более широкие уровни (меньше срабатываний).\n" +
            "Рекомендуемые значения: SL=2, TP=4, TS=2 (соотношение риск/прибыль 1:2)",
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            stackPanel.Children.Add(atrInfoText);

            var slPanel = CreateParameterPanel(
                "Стоп-лосс (множитель ATR):",
                _parameters,
                nameof(MaSettingsViewModel.StopLossATRMultiplier),
                "Уровень стоп-лосса в множителях ATR (обычно 2-3)");
            stackPanel.Children.Add(slPanel);

            var tpPanel = CreateParameterPanel(
                "Тейк-профит (множитель ATR):",
                _parameters,
                nameof(MaSettingsViewModel.TakeProfitATRMultiplier),
                "Уровень тейк-профита в множителях ATR (обычно 3-6)");
            stackPanel.Children.Add(tpPanel);

            var tsPanel = CreateParameterPanel(
                "Трейлинг-стоп (множитель ATR):",
                _parameters,
                nameof(MaSettingsViewModel.TrailingStopATRMultiplier),
                "Уровень трейлинг-стопа в множителях ATR (обычно 1-3)");
            stackPanel.Children.Add(tsPanel);




















            // Панель с кнопками
            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 15, 0, 0)
            };


            // Кнопка "Оптимальные" (новая)
            var optimalButton = new Button
            {
                Content = "Оптимальные",
                Width = 100,
                Height = 30,
                Margin = new Thickness(5),
                Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)), // Зеленый цвет
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                ToolTip = "Установить оптимальные параметры для текущего таймфрейма"
            };
            optimalButton.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            optimalButton.Click += (s, e) =>
            {
                MaOptimalParameters.ApplySettingsToViewModel(_parameters, _timeframe);
            };

            // Кнопка "Применить"
            var applyButton = new Button
            {
                Content = "Применить",
                Width = 100,
                Height = 30,
                Margin = new Thickness(5),
                Background = new SolidColorBrush(Color.FromRgb(33, 150, 243)), // Синий цвет
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            applyButton.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            applyButton.Click += (s, e) => _parameters.ApplyParameters();

            // Кнопка "Сброс"
            var resetButton = new Button
            {
                Content = "Сброс",
                Width = 100,
                Height = 30,
                Margin = new Thickness(5),
                Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)), // Красный цвет
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            resetButton.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            resetButton.Click += (s, e) => _parameters.ResetParameters();

            buttonsPanel.Children.Add(optimalButton);
            buttonsPanel.Children.Add(applyButton);
            buttonsPanel.Children.Add(resetButton);
            stackPanel.Children.Add(buttonsPanel);

            // Добавляем эффекты при наведении
            AddButtonHoverEffect(optimalButton, Color.FromRgb(56, 142, 60), Color.FromRgb(76, 175, 80));
            AddButtonHoverEffect(applyButton, Color.FromRgb(25, 118, 210), Color.FromRgb(33, 150, 243));
            AddButtonHoverEffect(resetButton, Color.FromRgb(211, 47, 47), Color.FromRgb(244, 67, 54));

            return stackPanel;
        }
        // Вспомогательный метод для эффекта наведения
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

            // ✅ Для поля FilterSmaPeriod добавляем чекбокс и поле ввода в одной строке
            if (propertyName == nameof(MaSettingsViewModel.FilterSmaPeriod))
            {
                var wrapperPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical
                };

                // Строка с чекбоксом и полем ввода
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 0)
                };

                // Чекбокс "Ручной режим"
                var checkBox = new CheckBox
                {
                    Content = "Ручной",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0),
                    ToolTip = "Отметьте для ручного ввода FilterSMA (автоматически при отключении)"
                };

                // Привязка для чекбокса
                var checkBoxBinding = new Binding(nameof(MaSettingsViewModel.UseManualFilterSma))
                {
                    Source = source,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                };
                checkBox.SetBinding(CheckBox.IsCheckedProperty, checkBoxBinding);

                // Текстовое поле для ввода значения
                var textBox = new TextBox
                {
                    Margin = new Thickness(0, 0, 0, 0),
                    Padding = new Thickness(5),
                    ToolTip = toolTip,
                    Width = 80,
                    IsEnabled = false // По умолчанию неактивно
                };

                // Привязка для текстового поля
                var textBinding = new Binding(propertyName)
                {
                    Source = source,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    ValidatesOnDataErrors = true,
                    ValidatesOnExceptions = true,
                    NotifyOnValidationError = true
                };
                textBox.SetBinding(TextBox.TextProperty, textBinding);

                // ✅ АКТИВАЦИЯ/ДЕАКТИВАЦИЯ ПОЛЯ ВВОДА В ЗАВИСИМОСТИ ОТ ЧЕКБОКСА
                checkBox.Checked += (s, e) =>
                {
                    textBox.IsEnabled = true;
                    textBox.Background = Brushes.White;
                    // Обновляем источник, чтобы применить значение
                    var bindingExpr = textBox.GetBindingExpression(TextBox.TextProperty);
                    bindingExpr?.UpdateSource();
                };

                checkBox.Unchecked += (s, e) =>
                {
                    textBox.IsEnabled = false;
                    textBox.Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                    // При отключении переключаем в автоматический режим
                    var settings = source as MaStrategy.MaSettingsViewModel;
                    if (settings != null)
                    {
                        settings.FilterSmaPeriod = 20; // Временно, будет пересчитано
                        settings.ApplyParameters();
                    }
                };

                // ✅ ВАЛИДАЦИЯ ПРИ ВВОДЕ
                textBox.TextChanged += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(textBox.Text))
                    {
                        if (int.TryParse(textBox.Text, out int value) && value > 0)
                        {
                            textBox.Background = Brushes.White;
                            textBox.ToolTip = toolTip;

                            // Обновляем источник
                            var bindingExpr = textBox.GetBindingExpression(TextBox.TextProperty);
                            bindingExpr?.UpdateSource();
                        }
                        else
                        {
                            textBox.Background = new SolidColorBrush(Color.FromRgb(255, 200, 200));
                            textBox.ToolTip = "⚠️ Введите положительное целое число";
                        }
                    }
                };

                // Обработка ENTER
                textBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        var bindingExpr = textBox.GetBindingExpression(TextBox.TextProperty);
                        bindingExpr?.UpdateSource();
                        // Применяем параметры
                        var settings = source as MaStrategy.MaSettingsViewModel;
                        settings?.ApplyParameters();
                        e.Handled = true;
                    }
                };

                rowPanel.Children.Add(checkBox);
                rowPanel.Children.Add(textBox);

                wrapperPanel.Children.Add(labelControl);
                wrapperPanel.Children.Add(rowPanel);

                // Информационная подсказка
                var infoText = new TextBlock
                {
                    Text = "ℹ️ При отключенном чекбоксе FilterSMA рассчитывается автоматически",
                    Foreground = Brushes.Gray,
                    FontSize = 9,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                wrapperPanel.Children.Add(infoText);

                panel.Children.Add(wrapperPanel);
                return panel;
            }

            // Стандартная обработка для остальных полей
            var textBoxStandard = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(5),
                ToolTip = toolTip
            };

            // ✅ ПРОВЕРКА ВАЛИДНОСТИ ПРИ ВВОДЕ
            textBoxStandard.TextChanged += (s, e) =>
            {
                string currentText = textBoxStandard.Text;

                if (string.IsNullOrEmpty(currentText) || currentText == "," || currentText == "-")
                {
                    textBoxStandard.Background = new SolidColorBrush(Color.FromRgb(255, 200, 200));
                    textBoxStandard.ToolTip = "⚠️ Введите корректное значение";
                    return;
                }

                bool isValid = false;

                if (propertyName == "SmaPeriods" || propertyName == "EmaPeriods")
                {
                    var parts = currentText.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        isValid = parts.All(p =>
                        {
                            string trimmed = p.Trim();
                            if (string.IsNullOrEmpty(trimmed)) return false;
                            return int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int val) && val > 0;
                        });
                    }
                    else
                    {
                        isValid = true;
                    }
                }
                else
                {
                    string normalized = currentText/*.Replace(",", ".")*/;
                    if (currentText.EndsWith(",") || currentText.EndsWith("."))
                    {
                        isValid = true;
                    }
                    else
                    {
                        isValid = decimal.TryParse(normalized,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out _);
                    }
                }

                if (isValid)
                {
                    textBoxStandard.Background = Brushes.White;
                    textBoxStandard.ToolTip = toolTip;

                    var bindingExpression = textBoxStandard.GetBindingExpression(TextBox.TextProperty);
                    if (bindingExpression != null)
                    {
                        var sourceValue = bindingExpression.DataItem?.GetType()
                            .GetProperty(propertyName)?.GetValue(bindingExpression.DataItem);
                        string currentSourceValue = sourceValue?.ToString() ?? "";

                        if (currentSourceValue != currentText)
                        {
                            bindingExpression.UpdateSource();
                        }
                    }
                }
                else
                {
                    textBoxStandard.Background = new SolidColorBrush(Color.FromRgb(255, 200, 200));
                    textBoxStandard.ToolTip = "⚠️ Некорректный формат!";
                }
            };

            // ✅ ОБРАБОТКА НАЖАТИЯ ENTER
            textBoxStandard.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    var bindingExpr = textBoxStandard.GetBindingExpression(TextBox.TextProperty);
                    bindingExpr?.UpdateSource();
                    var settings = source as MaStrategy.MaSettingsViewModel;
                    settings?.ApplyParameters();
                    e.Handled = true;
                }
            };

            // ✅ НАСТРОЙКА ПРИВЯЗКИ
            var bindingStandard = new Binding(propertyName)
            {
                Source = source,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                ValidatesOnDataErrors = true,
                ValidatesOnExceptions = true,
                NotifyOnValidationError = true
            };
            textBoxStandard.SetBinding(TextBox.TextProperty, bindingStandard);

            panel.Children.Add(labelControl);
            panel.Children.Add(textBoxStandard);

            return panel;
        }

        public object GetControlView()
        {
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10)
            };

            var mainPanel = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 15)
            };

            var instrumentText = new TextBlock
            {
                Text = $"{_instrument?.Ticker} - {_instrument?.Name}",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = Brushes.DarkBlue
            };
            headerPanel.Children.Add(instrumentText);
            mainPanel.Children.Add(headerPanel);

            // Сигналы
            var signalGroup = new GroupBox
            {
                Header = "Торговые сигналы",
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(10)
            };

            var signalPanel = new StackPanel();

            var pricePanel = CreateInfoRow("Текущая цена:", _indicatorValues, "CurrentPrice", "{0:F2} ₽");
            signalPanel.Children.Add(pricePanel);

            var positionSizePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 5, 0, 5)
            };
            positionSizePanel.Children.Add(new TextBlock
            {
                Text = "Размер позиции:",
                FontWeight = FontWeights.SemiBold,
                Width = 120
            });

            var positionSizeText = new TextBlock();
            var positionSizeBinding = new MultiBinding
            {
                Converter = new PositionSizeDisplayConverter()
            };
            positionSizeBinding.Bindings.Add(new Binding("PositionSizeValue") { Source = _indicatorValues });
            positionSizeBinding.Bindings.Add(new Binding("PositionSizeLots") { Source = _indicatorValues });
            positionSizeBinding.Bindings.Add(new Binding("PositionSizeType") { Source = _parameters });
            positionSizeBinding.Bindings.Add(new Binding("CurrentPrice") { Source = _indicatorValues });

            positionSizeText.SetBinding(TextBlock.TextProperty, positionSizeBinding);
            positionSizePanel.Children.Add(positionSizeText);
            signalPanel.Children.Add(positionSizePanel);

            var balancePanel = CreateInfoRow("Баланс счета:", _indicatorValues, "AccountBalance", "{0:F0} ₽");
            signalPanel.Children.Add(balancePanel);

            var signalDisplayPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 250)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 10, 0, 10)
            };

            var signalStack = new StackPanel();

            var signalTitle = new TextBlock
            {
                Text = "Текущий сигнал:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            };
            signalStack.Children.Add(signalTitle);

            var signalValue = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 5)
            };
            signalValue.SetBinding(TextBlock.TextProperty, new Binding("CurrentSignal") { Source = _indicatorValues });
            signalValue.SetBinding(TextBlock.ForegroundProperty, new Binding("SignalColor") { Source = _indicatorValues });
            signalStack.Children.Add(signalValue);

            signalDisplayPanel.Child = signalStack;
            signalPanel.Children.Add(signalDisplayPanel);

            signalGroup.Content = signalPanel;
            mainPanel.Children.Add(signalGroup);

            // SMA группа
            var smaGroup = CreateMaGroupBox(
                "Простые скользящие средние (SMA)",
                _indicatorValues,
                nameof(MaViewModel.SmaValues),
                Brushes.Blue);
            mainPanel.Children.Add(smaGroup);

            // EMA группа
            var emaGroup = CreateMaGroupBox(
                "Экспоненциальные скользящие средние (EMA)",
                _indicatorValues,
                nameof(MaViewModel.EmaValues),
                Brushes.Purple);
            mainPanel.Children.Add(emaGroup);

            // Тренд
            var trendGroup = new GroupBox
            {
                Header = "Анализ тренда",
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(10)
            };

            var trendPanel = new StackPanel();

            var trendText = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5)
            };
            trendText.SetBinding(TextBlock.TextProperty, new Binding("TrendDescription") { Source = _indicatorValues });

            var trendColorBinding = new Binding("IsBullishTrend")
            {
                Source = _indicatorValues,
                Converter = new BoolToColorConverter()
            };
            trendText.SetBinding(TextBlock.ForegroundProperty, trendColorBinding);

            trendPanel.Children.Add(trendText);

            var legendPanel = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 0)
            };

            AddLegendItem(legendPanel, "🟢 Бычий тренд (восходящий)", Brushes.Green);
            AddLegendItem(legendPanel, "🔴 Медвежий тренд (нисходящий)", Brushes.Red);
            AddLegendItem(legendPanel, "⚪ Боковой тренд (флэт)", Brushes.Gray);

            trendPanel.Children.Add(legendPanel);
            trendGroup.Content = trendPanel;
            mainPanel.Children.Add(trendGroup);

            scrollViewer.Content = mainPanel;
            return scrollViewer;
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

        private GroupBox CreateMaGroupBox(string header, object source, string propertyName, Brush headerColor)
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

            // ✅ ОТПИСКА ОТ СОБЫТИЙ
            if (_provider is TinkoffApiService tinkoffService)
            {
                tinkoffService.OnDealsUpdated -= OnDealsUpdated;
            }

            if (State == StrategyState.Running)
            {
                await StopAsync();
            }
        }
        #endregion

        #region ViewModels

        public partial class MaSettingsViewModel : ObservableObject
        {
            private string _smaPeriods = "20,50,100,150,200,300,500,1200";
            private string _emaPeriods = "20,25,50,100,150,200";
            private string _positionSizeType = "Percent";
            private decimal _positionSizePercent = 5.0m;
            private decimal _positionSizeAbsolute = 1000m;

            // ✅ НОВЫЕ СВОЙСТВА ДЛЯ ATR
            private decimal _stopLossATRMultiplier = 1.0m;
            private decimal _takeProfitATRMultiplier = 2.0m;
            private decimal _trailingStopATRMultiplier = 1.0m;

            // ✅ НОВОЕ СВОЙСТВО ДЛЯ FILTER SMA
            private int _filterSmaPeriod = 20;

            public event Action OnParametersChanged;
            public event Action<string, string> OnOptimalParametersApplied;

            public string SmaPeriods
            {
                get => _smaPeriods;
                set => SetProperty(ref _smaPeriods, value);
            }

            public string EmaPeriods
            {
                get => _emaPeriods;
                set => SetProperty(ref _emaPeriods, value);
            }

            public int FilterSmaPeriod
            {
                get => _filterSmaPeriod;
                set => SetProperty(ref _filterSmaPeriod, value);
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

            // ✅ НОВЫЕ СВОЙСТВА
            public decimal StopLossATRMultiplier
            {
                get => _stopLossATRMultiplier;
                set => SetProperty(ref _stopLossATRMultiplier, value);
            }

            public decimal TakeProfitATRMultiplier
            {
                get => _takeProfitATRMultiplier;
                set => SetProperty(ref _takeProfitATRMultiplier, value);
            }

            public decimal TrailingStopATRMultiplier
            {
                get => _trailingStopATRMultiplier;
                set => SetProperty(ref _trailingStopATRMultiplier, value);
            }


            // ✅ НОВОЕ СВОЙСТВО ДЛЯ УПРАВЛЕНИЯ РЕЖИМОМ FILTER SMA
            private bool _useManualFilterSma = false;

            public bool UseManualFilterSma
            {
                get => _useManualFilterSma;
                set => SetProperty(ref _useManualFilterSma, value);
            }






            public void ApplyParameters()
            {
                OnParametersChanged?.Invoke();
            }

            public void ResetParameters()
            {
                SmaPeriods = "20,50,100,150,200,300,500,1200";
                EmaPeriods = "20,25,50,100,150,200";
                PositionSizeType = "Percent";
                PositionSizePercent = 5.0m;
                PositionSizeAbsolute = 1000m;
                StopLossATRMultiplier = 1.0m;
                TakeProfitATRMultiplier = 2.0m;
                TrailingStopATRMultiplier = 1.0m;
                FilterSmaPeriod = 20;
                UseManualFilterSma = false; // ✅ Сбрасываем флаг
                ApplyParameters();
            }

            public void SetOptimalParameters(string smaPeriods, string emaPeriods, int filterSmaPeriod)
            {
                if (SmaPeriods != smaPeriods || EmaPeriods != emaPeriods || FilterSmaPeriod != filterSmaPeriod)
                {
                    SmaPeriods = smaPeriods;
                    EmaPeriods = emaPeriods;
                    FilterSmaPeriod = filterSmaPeriod;
                    OnOptimalParametersApplied?.Invoke(smaPeriods, emaPeriods);
                    ApplyParameters();
                }
            }

            public bool ValidatePeriods(string periods)
            {
                if (string.IsNullOrWhiteSpace(periods))
                    return false;

                var parts = periods.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                if (parts.Count == 0)
                    return false;

                return parts.All(p =>
                    int.TryParse(p, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int val) && val > 0);
            }
        }

        public partial class MaViewModel : ObservableObject
        {
            private ObservableDictionary<int, decimal> _smaValues = new();
            private ObservableDictionary<int, decimal> _emaValues = new();
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

            public ObservableDictionary<int, decimal> SmaValues
            {
                get => _smaValues;
                set
                {
                    if (_smaValues != value)
                    {
                        _smaValues = value;
                        OnPropertyChanged();
                        // Принудительно обновляем привязку
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
        }

        #endregion
    }

    #region Converters

    public class PositionSizeTypeToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is string type && parameter is string expectedType)
            {
                return type == expectedType ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class PositionSizeDisplayConverter : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length >= 4 &&
                values[0] is decimal positionSizeValue &&
                values[1] is decimal positionSizeLots &&
                values[2] is string positionSizeType &&
                values[3] is decimal currentPrice)
            {
                if (positionSizeType == "Percent")
                {
                    return $"{positionSizeValue:F1}% от депозита ≈ {positionSizeLots:F0} лотов";
                }
                else
                {
                    return $"{positionSizeValue:F0} ₽ ≈ {positionSizeLots:F0} лотов по цене {currentPrice:F2} ₽";
                }
            }
            return "Нет данных";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class BoolToColorConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool isBullish && isBullish)
                return Brushes.Green;
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class ObservableDictionary<TKey, TValue> : ObservableObject, IEnumerable<KeyValuePair<TKey, TValue>>
    {
        private Dictionary<TKey, TValue> _dictionary = new();
        private Dictionary<int, decimal> smaValues;

        public ObservableDictionary(Dictionary<int, decimal> smaValues)
        {
            this.smaValues = smaValues;
        }

        public ObservableDictionary() { }

        public TValue this[TKey key]
        {
            get => _dictionary[key];
            set
            {
                _dictionary[key] = value;
                OnPropertyChanged(nameof(Values));
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(Items));
                OnPropertyChanged(nameof(Keys));

                // Добавляем уведомление об изменении всей коллекции
                OnPropertyChanged(string.Empty);
            }
        }

        public Dictionary<TKey, TValue>.KeyCollection Keys => _dictionary.Keys;
        public Dictionary<TKey, TValue>.ValueCollection Values => _dictionary.Values;
        public int Count => _dictionary.Count;

        // Важно! Это свойство используется для привязки в ListView
        public IEnumerable<KeyValuePair<TKey, TValue>> Items => _dictionary;

        public void Add(TKey key, TValue value)
        {
            _dictionary.Add(key, value);
            OnPropertyChanged(nameof(Values));
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(Items));
            OnPropertyChanged(nameof(Keys));
            OnPropertyChanged(string.Empty);
        }

        public void Clear()
        {
            _dictionary.Clear();
            OnPropertyChanged(nameof(Values));
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(Items));
            OnPropertyChanged(nameof(Keys));
            OnPropertyChanged(string.Empty);
        }

        public bool Remove(TKey key)
        {
            var result = _dictionary.Remove(key);
            if (result)
            {
                OnPropertyChanged(nameof(Values));
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(Items));
                OnPropertyChanged(nameof(Keys));
                OnPropertyChanged(string.Empty);
            }
            return result;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();
    }

    #endregion
}