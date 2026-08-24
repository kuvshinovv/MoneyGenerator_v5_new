// Файл: PairsTradingStrategy.cs (Создайте новый файл в папке Strategies)

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tinkoff.InvestApi.V1;

namespace MoneyGenerator_v5.Strategies
{
    #region Enums & Supporting Classes
    public enum PairsTradeState
    {
        NoPosition,
        LongSpread,  // Long A, Short B
        ShortSpread, // Short A, Long B
        PendingEntry,
        PendingExit
    }

    public class PairsTradingParameters : ObservableObject
    {
        // --- Параметры пары ---

        private string _firstInstrumentTicker = "IMOEXF";
        public string FirstInstrumentTicker
        {
            get => _firstInstrumentTicker;
            set => SetProperty(ref _firstInstrumentTicker, value);
        }

        private string _firstInstrumentUid;
        public string FirstInstrumentUid
        {
            get => _firstInstrumentUid;
            set => SetProperty(ref _firstInstrumentUid, value);
        }

        private string _pairInstrumentTicker = "SBER";
        public string PairInstrumentTicker
        {
            get => _pairInstrumentTicker;
            set => SetProperty(ref _pairInstrumentTicker, value);
        }

        private string _pairInstrumentUid;
        public string PairInstrumentUid
        {
            get => _pairInstrumentUid;
            set => SetProperty(ref _pairInstrumentUid, value);
        }

        // --- Параметры модели ---
        private int _lookbackPeriod = 24;
        public int LookbackPeriod
        {
            get => _lookbackPeriod;
            set => SetProperty(ref _lookbackPeriod, value);
        }

        // --- Параметры торговли ---
        private decimal _entryZScore = 2.0m;
        public decimal EntryZScore
        {
            get => _entryZScore;
            set => SetProperty(ref _entryZScore, value);
        }

        private decimal _exitZScore = 0.5m;
        public decimal ExitZScore
        {
            get => _exitZScore;
            set => SetProperty(ref _exitZScore, value);
        }

        private decimal _stopLossZScore = 3.5m;
        public decimal StopLossZScore
        {
            get => _stopLossZScore;
            set => SetProperty(ref _stopLossZScore, value);
        }

        private decimal _positionSizePercent = 10m;
        public decimal PositionSizePercent
        {
            get => _positionSizePercent;
            set => SetProperty(ref _positionSizePercent, value);
        }

        // --- Состояние модели (расчетные параметры) ---
        private decimal _hedgeRatio;
        public decimal HedgeRatio
        {
            get => _hedgeRatio;
            set => SetProperty(ref _hedgeRatio, value);
        }

        private decimal _spreadMean;
        public decimal SpreadMean
        {
            get => _spreadMean;
            set => SetProperty(ref _spreadMean, value);
        }

        private decimal _spreadStd;
        public decimal SpreadStd
        {
            get => _spreadStd;
            set => SetProperty(ref _spreadStd, value);
        }

        private bool _modelValid = false;
        public bool ModelValid
        {
            get => _modelValid;
            set => SetProperty(ref _modelValid, value);
        }

        private DateTime _modelLastUpdate = DateTime.MinValue;
        public DateTime ModelLastUpdate
        {
            get => _modelLastUpdate;
            set => SetProperty(ref _modelLastUpdate, value);
        }

        public event Action<PairsTradingParameters> OnParametersChanged;

        public void ApplyParameters() => OnParametersChanged?.Invoke(this);
        public void ResetParameters()
        {
            PairInstrumentTicker = "SBER";
            LookbackPeriod = 120;
            EntryZScore = 2.0m;
            ExitZScore = 0.5m;
            StopLossZScore = 3.5m;
            PositionSizePercent = 10m;
            ApplyParameters();
        }

        /// <summary>
        /// Публичный метод для принудительного обновления UI
        /// </summary>
        public void RefreshUI(string propertyName)
        {
            OnPropertyChanged(propertyName);
        }
    }

    public class PairsIndicatorValues : ObservableObject
    {
        private decimal _currentSpread;
        public decimal CurrentSpread { get => _currentSpread; set => SetProperty(ref _currentSpread, value); }

        private decimal _currentZScore;
        public decimal CurrentZScore { get => _currentZScore; set => SetProperty(ref _currentZScore, value); }

        private decimal _hedgeRatio;
        public decimal HedgeRatio { get => _hedgeRatio; set => SetProperty(ref _hedgeRatio, value); }

        private decimal _spreadMean;
        public decimal SpreadMean { get => _spreadMean; set => SetProperty(ref _spreadMean, value); }

        private decimal _spreadStd;
        public decimal SpreadStd { get => _spreadStd; set => SetProperty(ref _spreadStd, value); }

        private decimal _priceA;
        public decimal PriceA { get => _priceA; set => SetProperty(ref _priceA, value); }

        private decimal _priceB;
        public decimal PriceB { get => _priceB; set => SetProperty(ref _priceB, value); }

        private string _signal = "ОЖИДАНИЕ";
        public string Signal { get => _signal; set => SetProperty(ref _signal, value); }

        private Brush _signalColor = Brushes.Gray;
        public Brush SignalColor { get => _signalColor; set => SetProperty(ref _signalColor, value); }

        private string _signalDescription = "";
        public string SignalDescription { get => _signalDescription; set => SetProperty(ref _signalDescription, value); }

        private string _status = "Ожидание модели...";
        public string Status { get => _status; set => SetProperty(ref _status, value); }

        private string _currentPosition = "Нет позиции";
        public string CurrentPosition { get => _currentPosition; set => SetProperty(ref _currentPosition, value); }

        private string _entryStatus = "Ожидание сигнала";
        public string EntryStatus { get => _entryStatus; set => SetProperty(ref _entryStatus, value); }

        private string _exitStatus = "Нет позиции";
        public string ExitStatus { get => _exitStatus; set => SetProperty(ref _exitStatus, value); }

        private string _lastAction = "";
        public string LastAction { get => _lastAction; set => SetProperty(ref _lastAction, value); }

        private string _strategyStatus = "ОСТАНОВЛЕНА";
        public string StrategyStatus { get => _strategyStatus; set => SetProperty(ref _strategyStatus, value); }

        public DateTime LastUpdate { get; set; } = DateTime.Now;

        /// <summary>
        /// Публичный метод для принудительного обновления UI
        /// </summary>
        public void RefreshUI(string propertyName)
        {
            OnPropertyChanged(propertyName);
        }
    }
    #endregion

    public partial class PairsTradingStrategy 
    {
        #region Поля и свойства
        public string Name => "Статистический Арбитраж (IMOEXF/...)";
        public string Type => "PairsTrading";
        public StrategyState State { get; set; } = StrategyState.Stopped;

        private Timer _updateTimer;
        private readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);
        private DateTime _lastModelRebuildTime = DateTime.MinValue;
        private const int MODEL_REBUILD_INTERVAL_MS = 1000; // 1 секунда
        private bool _isModelBuilding = false;
        private DateTime _lastModelBuildAttempt = DateTime.MinValue;
        private const int MODEL_BUILD_COOLDOWN_MS = 3000; // 60 секунд между попытками построения модели


        private TextBlock _priceALabel;
        private TextBlock _priceBLabel;
        private TextBlock _hedgeRatioLabel;
        private TextBlock _spreadMeanLabel;
        private TextBlock _spreadStdLabel;
        private TextBlock _signalLabel;
        private TextBlock _signalDescriptionLabel;
        private TextBlock _currentPositionLabel;
        private TextBlock _lastActionLabel;
        private TextBlock _statusLabel;
        private TextBlock _strategyStatusLabel;

        private decimal _netPositionA = 0;
        private decimal _netPositionB = 0;

        // Добавьте это поле в секцию #region Поля и свойства
        private bool _isBacktestMode = false;
        public bool IsBacktestMode
        {
            get => _isBacktestMode;
            set => _isBacktestMode = value;
        }

        // Добавьте это поле для хранения последнего сигнала при бэктесте
        private string _lastBacktestSignal = "WAIT";

        // Поля для бэктеста (симуляция позиций)
        private decimal _simulatedPositionA = 0;
        private decimal _simulatedPositionB = 0;

        // Поля для бэктеста(переданные свечи)
        private List<Models.Candle> _backtestCandlesA;
        private List<Models.Candle> _backtestCandlesB;





        private bool _candlesLoadingInProgress = false;
        private DateTime _lastCandlesLoadAttempt = DateTime.MinValue;
        private const int CANDLES_LOAD_COOLDOWN_MS = 300000; // 5 минут между попытками загрузки

        // ✅ НОВЫЕ ПОЛЯ ДЛЯ ПОДПИСОК И УПРАВЛЕНИЯ
        private bool _isSubscribedToPrices = false;
        private readonly SemaphoreSlim _subscriptionLock = new SemaphoreSlim(1, 1);
        private DateTime _lastPriceUpdateA = DateTime.MinValue;
        private DateTime _lastPriceUpdateB = DateTime.MinValue;
        private const int PRICE_UPDATE_TIMEOUT_SECONDS = 60;

        // ✅ НОВЫЕ ПОЛЯ ДЛЯ УПРАВЛЕНИЯ ПОПЫТКАМИ ПОСТРОЕНИЯ
        private int _modelBuildAttempts = 0;
        private const int MAX_MODEL_BUILD_ATTEMPTS = 3;
        private DateTime _lastModelBuildErrorTime = DateTime.MinValue;


        // Поля для кэширования инструментов в бэктест-режиме
        private Models.Instrument _cachedInstrumentsA;
        private Models.Instrument _cachedInstrumentsB;




        private readonly ILogger _logger;
        private readonly IProvirerService _provider;
        private readonly TransactionsService _transactionsService;
        private readonly PairsTradingParameters _parameters;
        private readonly PairsIndicatorValues _indicatorValues;
        private readonly StrategyViewModel _strategyViewModel;
        private readonly MainViewModel _mainViewModel;
        private string _selectedAccountId;

        // Основной инструмент (A) - IMOEXF
        private Models.Instrument _instrumentA;
        private string _instrumentAUid;
        private string _instrumentATicker;
        private string _timeframe;

        // Парный инструмент (B) - SBER
        private Models.Instrument _instrumentB;
        private string _instrumentBUid;
        private string _instrumentBTicker;

        // Текущие позиции
        private Position _positionA;
        private Position _positionB;
        private Models.Order _pendingOrderA;
        private Models.Order _pendingOrderB;

        // Состояние торговли
        private PairsTradeState _tradeState = PairsTradeState.NoPosition;
        private DateTime _positionOpenTime;
        private bool _entryPass = true;
        private bool _exitPass = true;
        private decimal _lastPriceA;
        private decimal _lastPriceB;

        // Для отладки - счетчики
        private int _debugCounter = 0;
        private DateTime _lastDebugLogTime = DateTime.MinValue;

        // События для UI
        public event Action<string> OnEntryStatusChanged;
        public event Action<string> OnExitStatusChanged;
        public event Action<string> OnOrderStatusChanged;
        public event Action<string> OnStrategyStatusChanged;

        public PairsTradingParameters Parameters => _parameters;
        public PairsIndicatorValues IndicatorValues => _indicatorValues;
        #endregion

        #region Конструктор и инициализация
        public PairsTradingStrategy(
            ILogger<PairsTradingStrategy> logger,
            IProvirerService provider,
            StrategyViewModel strategyViewModel,
            TransactionsService transactionsService,
            MainViewModel mainViewModel = null)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PairsTradingStrategy>.Instance;
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _strategyViewModel = strategyViewModel ?? throw new ArgumentNullException(nameof(strategyViewModel));
            _transactionsService = transactionsService ?? throw new ArgumentNullException(nameof(transactionsService));
            _mainViewModel = mainViewModel;

            _parameters = new PairsTradingParameters();
            _indicatorValues = new PairsIndicatorValues();

            _parameters.OnParametersChanged += OnParametersChanged;
            InitializeIndicatorBindings();

            //Debug.WriteLine($"======================================================");
            //Debug.WriteLine($"[PairsTrading] СТРАТЕГИЯ ИНИЦИАЛИЗИРОВАНА");
            //Debug.WriteLine($"[PairsTrading] Время: {DateTime.Now:HH:mm:ss.fff}");
            //Debug.WriteLine($"======================================================");
        }



        /// <summary>
        /// Конструктор для бэктест-режима (без провайдера)
        /// </summary>
        public PairsTradingStrategy(
            ILogger<PairsTradingStrategy> logger,
            StrategyViewModel strategyViewModel,
            TransactionsService transactionsService,
            MainViewModel mainViewModel = null)
            : this(logger, null, strategyViewModel, transactionsService, mainViewModel)
        {
            // Провайдер = null, используется только для бэктеста
            _isBacktestMode = true;
        }



        private void InitializeIndicatorBindings()
        {
            OnEntryStatusChanged += (status) => { _indicatorValues.EntryStatus = status; };
            OnExitStatusChanged += (status) => { _indicatorValues.ExitStatus = status; };
            OnStrategyStatusChanged += (status) => { _indicatorValues.StrategyStatus = status; };
        }

        public async Task InitializeAsync(Models.Instrument instrument, string timeframe)
        {
            // ✅ ИЗМЕНЕНИЕ: Инструмент из MainViewModel становится ВТОРЫМ (B)
            // ✅ Сохраняем переданный инструмент как начальный для B
            // Проверяем, не был ли уже изменен параметр пользователем
            if (string.IsNullOrEmpty(_parameters.PairInstrumentTicker) || _parameters.PairInstrumentTicker == "SBER")
            {
                _instrumentB = instrument;
                _instrumentBUid = instrument.Uid;
                _instrumentBTicker = instrument.Ticker;
                _parameters.PairInstrumentTicker = instrument.Ticker;
                _parameters.PairInstrumentUid = instrument.Uid;
            }
            else
            {
                // Если пользователь уже изменил параметр, используем его значение
                // и загружаем инструмент по этому тикеру
                _instrumentBTicker = _parameters.PairInstrumentTicker;
                _instrumentBUid = _parameters.PairInstrumentUid;
            }

            // ✅ ИСПРАВЛЕНИЕ: Используем значение из параметров, если оно уже установлено
            // Если FirstInstrumentTicker еще не установлен (по умолчанию "IMOEXF"), используем его
            // Но если пользователь уже изменил, сохраняем его выбор
            if (string.IsNullOrEmpty(_parameters.FirstInstrumentTicker) || _parameters.FirstInstrumentTicker == "IMOEXF")
            {
                // Только если еще не установлен, используем IMOEXF как значение по умолчанию
                _instrumentA = null;
                _instrumentAUid = null;
                _instrumentATicker = "IMOEXF";
                _parameters.FirstInstrumentTicker = "IMOEXF";
            }
            else
            {
                // Используем то, что уже установлено пользователем
                _instrumentATicker = _parameters.FirstInstrumentTicker;
                _instrumentAUid = _parameters.FirstInstrumentUid;
            }

            _timeframe = timeframe;

            ////Debug.WriteLine($"[PairsTrading] ИНИЦИАЛИЗАЦИЯ СТРАТЕГИИ");
            ////Debug.WriteLine($"[PairsTrading]   Инструмент A (базовый): {_instrumentATicker}");
            ////Debug.WriteLine($"[PairsTrading]   Инструмент B (выбранный): {_instrumentBTicker} (UID: {_instrumentBUid})");
            ////Debug.WriteLine($"[PairsTrading]   Таймфрейм: {_timeframe}");

            // ✅ ИСПРАВЛЕНИЕ: В бэктест-режиме НЕ запрашиваем счета!
            if (!_isBacktestMode)
            {
                var accounts = await _provider.GetAccountsAsync();
                if (accounts.Any())
                {
                    _selectedAccountId = accounts.First().Id;
                }
                else
                {
                    //Debug.WriteLine($"[PairsTrading]   ❌ Счета не найдены!");
                }
            }
            else
            {
                //Debug.WriteLine($"[PairsTrading]   БЭКТЕСТ-РЕЖИМ: пропускаем получение счетов");
                _selectedAccountId = "BACKTEST_ACCOUNT"; // Заглушка для бэктеста
            }

            await LoadPairInstrumentAsync();
            await BuildModelAsync();

            //Debug.WriteLine($"[PairsTrading] ИНИЦИАЛИЗАЦИЯ ЗАВЕРШЕНА");
            //Debug.WriteLine($"======================================================");

            _logger.LogInformation($"PairsTrading strategy initialized for {_instrumentBTicker} with {_instrumentATicker}");
        }
        #endregion

        #region Загрузка парного инструмента и построение модели
        private async Task LoadPairInstrumentAsync()
        {
            Debug.WriteLine($"[PairsTrading] ЗАГРУЗКА ИНСТРУМЕНТОВ ПАРЫ");
            Debug.WriteLine($"[PairsTrading]   Ищем A (базовый): {_parameters.FirstInstrumentTicker}");
            Debug.WriteLine($"[PairsTrading]   Ищем B (выбранный): {_parameters.PairInstrumentTicker}");

            try
            {
                // ✅ В БЭКТЕСТ-РЕЖИМЕ ИСПОЛЬЗУЕМ ТОЛЬКО КЭШ
                if (_isBacktestMode)
                {
                    if (_cachedInstrumentsA != null && _cachedInstrumentsB != null)
                    {
                        _instrumentA = _cachedInstrumentsA;
                        _instrumentAUid = _cachedInstrumentsA.Uid;
                        _instrumentATicker = _cachedInstrumentsA.Ticker;

                        _instrumentB = _cachedInstrumentsB;
                        _instrumentBUid = _cachedInstrumentsB.Uid;
                        _instrumentBTicker = _cachedInstrumentsB.Ticker;

                        Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: используем кэш A={_instrumentATicker}, B={_instrumentBTicker}");
                        UpdateControlPanelLabels();
                        return;
                    }
                    else
                    {
                        Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: КЭШ ПУСТ!");
                        return;
                    }
                }

                // ✅ ТОЛЬКО В РЕАЛЬНОМ РЕЖИМЕ ДЕЛАЕМ ЗАПРОС К API
                var allInstruments = await _provider.GetInstrumentsAsync();
                Debug.WriteLine($"[PairsTrading]   Всего инструментов: {allInstruments?.Count ?? 0}");

                // Загружаем первый инструмент (A)
                var firstInstrument = allInstruments?.FirstOrDefault(i => i.Ticker == _parameters.FirstInstrumentTicker);
                if (firstInstrument != null)
                {
                    _instrumentA = firstInstrument;
                    _instrumentAUid = firstInstrument.Uid;
                    _instrumentATicker = firstInstrument.Ticker;
                    _parameters.FirstInstrumentUid = firstInstrument.Uid;
                    Debug.WriteLine($"[PairsTrading]   ✅ Найден A: {_instrumentATicker} (UID: {_instrumentAUid})");
                }
                else
                {
                    Debug.WriteLine($"[PairsTrading]   ❌ Инструмент {_parameters.FirstInstrumentTicker} НЕ НАЙДЕН!");
                    _logger.LogWarning($"First instrument {_parameters.FirstInstrumentTicker} not found");
                    _indicatorValues.Status = $"❌ Инструмент {_parameters.FirstInstrumentTicker} не найден";

                    // ✅ Пытаемся найти IMOEXF как запасной вариант
                    var defaultInstrument = allInstruments?.FirstOrDefault(i => i.Ticker == "IMOEXF");
                    if (defaultInstrument != null)
                    {
                        Debug.WriteLine($"[PairsTrading]   Используем IMOEXF как запасной вариант");
                        _instrumentA = defaultInstrument;
                        _instrumentAUid = defaultInstrument.Uid;
                        _instrumentATicker = defaultInstrument.Ticker;
                        _parameters.FirstInstrumentTicker = defaultInstrument.Ticker;
                        _parameters.FirstInstrumentUid = defaultInstrument.Uid;
                    }
                    else
                    {
                        // ✅ КРИТИЧЕСКАЯ ОШИБКА: Нет инструмента A
                        _indicatorValues.Status = "❌ КРИТИЧЕСКАЯ ОШИБКА: Инструмент A не найден";
                        _parameters.ModelValid = false;
                        return;
                    }
                }

                // Загружаем парный инструмент (B)
                if (_instrumentB != null && !string.IsNullOrEmpty(_instrumentBUid) &&
                    _instrumentBTicker == _parameters.PairInstrumentTicker)
                {
                    Debug.WriteLine($"[PairsTrading]   ✅ Используем существующий B: {_instrumentBTicker} (UID: {_instrumentBUid})");
                }
                else
                {
                    var pairInstrument = allInstruments?.FirstOrDefault(i => i.Ticker == _parameters.PairInstrumentTicker);
                    if (pairInstrument != null)
                    {
                        _instrumentB = pairInstrument;
                        _instrumentBUid = pairInstrument.Uid;
                        _instrumentBTicker = pairInstrument.Ticker;
                        _parameters.PairInstrumentUid = pairInstrument.Uid;

                        Debug.WriteLine($"[PairsTrading]   ✅ Найден B: {_instrumentBTicker} (UID: {_instrumentBUid})");
                    }
                    else
                    {
                        Debug.WriteLine($"[PairsTrading]   ❌ Инструмент {_parameters.PairInstrumentTicker} НЕ НАЙДЕН!");
                        _logger.LogWarning($"Pair instrument {_parameters.PairInstrumentTicker} not found");
                        _indicatorValues.Status = $"❌ Инструмент {_parameters.PairInstrumentTicker} не найден";

                        // ✅ Пытаемся найти SBER как запасной вариант
                        var defaultB = allInstruments?.FirstOrDefault(i => i.Ticker == "SBER");
                        if (defaultB != null)
                        {
                            Debug.WriteLine($"[PairsTrading]   Используем SBER как запасной вариант");
                            _instrumentB = defaultB;
                            _instrumentBUid = defaultB.Uid;
                            _instrumentBTicker = defaultB.Ticker;
                            _parameters.PairInstrumentTicker = defaultB.Ticker;
                            _parameters.PairInstrumentUid = defaultB.Uid;
                        }
                        else
                        {
                            // ✅ КРИТИЧЕСКАЯ ОШИБКА: Нет инструмента B
                            _indicatorValues.Status = "❌ КРИТИЧЕСКАЯ ОШИБКА: Инструмент B не найден";
                            _parameters.ModelValid = false;
                            return;
                        }
                    }
                }

                // ✅ Сохраняем в кэш для бэктест-режима
                if (_instrumentA != null && _instrumentB != null)
                {
                    _cachedInstrumentsA = _instrumentA;
                    _cachedInstrumentsB = _instrumentB;
                    Debug.WriteLine($"[PairsTrading] Инструменты сохранены в кэш: A={_instrumentA.Ticker}, B={_instrumentB.Ticker}");
                }

                // Обновляем подписи в UI
                UpdateControlPanelLabels();

                if (_instrumentA != null && _instrumentB != null)
                {
                    _indicatorValues.Status = $"Пара: {_instrumentATicker} / {_instrumentBTicker}";
                    Debug.WriteLine($"[PairsTrading] ✅ Инструменты загружены: A={_instrumentATicker}, B={_instrumentBTicker}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PairsTrading]   ❌ Ошибка загрузки: {ex.Message}");
                _logger.LogError(ex, "Error loading pair instruments");
                _indicatorValues.Status = $"❌ Ошибка: {ex.Message}";
                _parameters.ModelValid = false;
            }
        }




        /// <summary>
        /// Проверяет наличие свечей для пары и загружает их, если они отсутствуют (ТОЛЬКО ОДИН РАЗ)
        /// </summary>
        private async Task<bool> CheckCandlesForPairAsync()
        {
            Debug.WriteLine($"[PairsTrading] ПРОВЕРКА СВЕЧЕЙ ДЛЯ ПАРЫ");

            if (_instrumentA == null || _instrumentB == null)
            {
                Debug.WriteLine($"[PairsTrading] ❌ Инструменты не загружены!");
                _indicatorValues.Status = "❌ Инструменты не загружены";
                return false;
            }

            if (_candlesLoadingInProgress)
            {
                Debug.WriteLine($"[PairsTrading] ⏳ Загрузка свечей уже выполняется, пропускаем");
                return false;
            }

            try
            {
                string timeFrameForLoad = _timeframe ?? "1hour";
                Debug.WriteLine($"[PairsTrading]   Используемый таймфрейм: {timeFrameForLoad}");

                int requiredCandles = CalculateLookbackCandles(timeFrameForLoad, _parameters.LookbackPeriod);
                int checkCandlesCount = Math.Min(requiredCandles * 4, 3000);

                Debug.WriteLine($"[PairsTrading]   Требуется свечей: {requiredCandles}, запрашиваем: {checkCandlesCount}");

                // ✅ Загружаем свечи из БД
                var candlesA = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(
                    _instrumentATicker, timeFrameForLoad, checkCandlesCount);
                var candlesB = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(
                    _instrumentBTicker, timeFrameForLoad, checkCandlesCount);

                Debug.WriteLine($"[PairsTrading]   Свечей в БД: A={candlesA?.Count ?? 0}, B={candlesB?.Count ?? 0}");

                // ✅ Проверяем актуальность данных для обоих инструментов
                bool needUpdateA = false;
                bool needUpdateB = false;
                DateTime now = DateTime.Now;
                int maxAgeHours = (int)GetMaxCandleAgeHours(timeFrameForLoad);

                if (candlesA != null && candlesA.Any())
                {
                    var lasttA = candlesA.Last().Time;
                    var ageeA = (now - lasttA).TotalHours;
                    if (ageeA > maxAgeHours)
                    {
                        needUpdateA = true;
                        Debug.WriteLine($"[PairsTrading] ⚠️ Данные A устарели: {ageeA:F1}ч (макс: {maxAgeHours}ч)");
                    }
                    else
                    {
                        Debug.WriteLine($"[PairsTrading] ✅ Данные A актуальны: {ageeA:F1}ч");
                    }
                }
                else
                {
                    needUpdateA = true;
                    Debug.WriteLine($"[PairsTrading] ⚠️ Нет данных A");
                }

                if (candlesB != null && candlesB.Any())
                {
                    var lasttB = candlesB.Last().Time;
                    var ageeB = (now - lasttB).TotalHours;
                    if (ageeB > maxAgeHours)
                    {
                        needUpdateB = true;
                        Debug.WriteLine($"[PairsTrading] ⚠️ Данные B устарели: {ageeB:F1}ч (макс: {maxAgeHours}ч)");
                    }
                    else
                    {
                        Debug.WriteLine($"[PairsTrading] ✅ Данные B актуальны: {ageeB:F1}ч");
                    }
                }
                else
                {
                    needUpdateB = true;
                    Debug.WriteLine($"[PairsTrading] ⚠️ Нет данных B");
                }

                // ✅ Проверяем пересечение диапазонов
                bool hasOverlap = false;
                if (candlesA != null && candlesA.Any() && candlesB != null && candlesB.Any())
                {
                    var aFirst = candlesA.First().Time;
                    var aLast = candlesA.Last().Time;
                    var bFirst = candlesB.First().Time;
                    var bLast = candlesB.Last().Time;

                    hasOverlap = !(aLast < bFirst || bLast < aFirst);

                    Debug.WriteLine($"[PairsTrading]   Диапазон A: {aFirst:yyyy-MM-dd HH:mm} - {aLast:yyyy-MM-dd HH:mm}");
                    Debug.WriteLine($"[PairsTrading]   Диапазон B: {bFirst:yyyy-MM-dd HH:mm} - {bLast:yyyy-MM-dd HH:mm}");
                    Debug.WriteLine($"[PairsTrading]   Пересечение: {hasOverlap}");

                    // ✅ Если нет пересечения - нужно обновить оба инструмента
                    if (!hasOverlap)
                    {
                        needUpdateA = true;
                        needUpdateB = true;
                        Debug.WriteLine($"[PairsTrading] ⚠️ Нет пересечения по времени! Обновляем оба инструмента");
                    }
                }

                // ✅ Если данные устарели или нет пересечения - загружаем свежие данные
                if (needUpdateA || needUpdateB)
                {
                    if ((DateTime.Now - _lastCandlesLoadAttempt).TotalMilliseconds < CANDLES_LOAD_COOLDOWN_MS)
                    {
                        Debug.WriteLine($"[PairsTrading] ⏳ Загрузка свечей недавно выполнялась, пропускаем");
                        return false;
                    }

                    Debug.WriteLine($"[PairsTrading] ЗАГРУЗКА СВЕЖИХ ДАННЫХ...");
                    _candlesLoadingInProgress = true;
                    _lastCandlesLoadAttempt = DateTime.Now;

                    try
                    {
                        // ✅ Загружаем за период, чтобы получить свежие данные для обоих инструментов
                        int daysToLoad = GetDaysToLoad(timeFrameForLoad);
                        var endTime = DateTime.UtcNow;
                        var startTime = endTime.AddDays(-daysToLoad);

                        Debug.WriteLine($"[PairsTrading]   Загрузка за {daysToLoad} дней с {startTime:yyyy-MM-dd} по {endTime:yyyy-MM-dd}");

                        // Загружаем для инструмента A
                        Debug.WriteLine($"[PairsTrading]   Загрузка свежих свечей для {_instrumentATicker}...");
                        var newCandlesA = await _provider.GetHistoricalDataAsync(
                            _instrumentATicker, _instrumentAUid, timeFrameForLoad, startTime, endTime);

                        if (newCandlesA != null && newCandlesA.Any())
                        {
                            Debug.WriteLine($"[PairsTrading]   Загружено {newCandlesA.Count} свечей для {_instrumentATicker}");
                            await _strategyViewModel.SaveCandlesAsync(_instrumentATicker, timeFrameForLoad, newCandlesA);
                        }
                        else
                        {
                            Debug.WriteLine($"[PairsTrading]   ⚠️ Не удалось загрузить свечи для {_instrumentATicker}");
                        }

                        // Загружаем для инструмента B
                        Debug.WriteLine($"[PairsTrading]   Загрузка свежих свечей для {_instrumentBTicker}...");
                        var newCandlesB = await _provider.GetHistoricalDataAsync(
                            _instrumentBTicker, _instrumentBUid, timeFrameForLoad, startTime, endTime);

                        if (newCandlesB != null && newCandlesB.Any())
                        {
                            Debug.WriteLine($"[PairsTrading]   Загружено {newCandlesB.Count} свечей для {_instrumentBTicker}");
                            await _strategyViewModel.SaveCandlesAsync(_instrumentBTicker, timeFrameForLoad, newCandlesB);
                        }
                        else
                        {
                            Debug.WriteLine($"[PairsTrading]   ⚠️ Не удалось загрузить свечи для {_instrumentBTicker}");
                        }

                        // ✅ Повторно загружаем из БД
                        candlesA = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(
                            _instrumentATicker, timeFrameForLoad, checkCandlesCount);
                        candlesB = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(
                            _instrumentBTicker, timeFrameForLoad, checkCandlesCount);

                        Debug.WriteLine($"[PairsTrading]   После загрузки: A={candlesA?.Count ?? 0}, B={candlesB?.Count ?? 0}");
                    }
                    finally
                    {
                        _candlesLoadingInProgress = false;
                    }
                }

                // ✅ Проверяем, достаточно ли данных и есть ли пересечение
                if (candlesA == null || !candlesA.Any() || candlesB == null || !candlesB.Any())
                {
                    Debug.WriteLine($"[PairsTrading] ❌ Нет данных для одного из инструментов");
                    _indicatorValues.Status = "❌ Нет данных";
                    return false;
                }

                // ✅ Проверяем актуальность после загрузки
                var lastA = candlesA.Last().Time;
                var lastB = candlesB.Last().Time;
                var ageA = (now - lastA).TotalHours;
                var ageB = (now - lastB).TotalHours;

                Debug.WriteLine($"[PairsTrading]   Актуальность A: {ageA:F1}ч, B: {ageB:F1}ч");

                // ✅ Проверяем пересечение
                var aFirst2 = candlesA.First().Time;
                var aLast2 = candlesA.Last().Time;
                var bFirst2 = candlesB.First().Time;
                var bLast2 = candlesB.Last().Time;
                hasOverlap = !(aLast2 < bFirst2 || bLast2 < aFirst2);

                if (!hasOverlap)
                {
                    Debug.WriteLine($"[PairsTrading] ❌ Нет пересечения по времени даже после загрузки!");
                    Debug.WriteLine($"[PairsTrading]   A: {aFirst2:yyyy-MM-dd HH:mm} - {aLast2:yyyy-MM-dd HH:mm}");
                    Debug.WriteLine($"[PairsTrading]   B: {bFirst2:yyyy-MM-dd HH:mm} - {bLast2:yyyy-MM-dd HH:mm}");
                    _indicatorValues.Status = "❌ Нет пересечения по времени между инструментами";
                    return false;
                }

                // ✅ Проверяем выровненные данные
                var aligned = AlignCandles(candlesA, candlesB);
                if (aligned.Count >= requiredCandles)
                {
                    Debug.WriteLine($"[PairsTrading] ✅ Выровненных точек достаточно: {aligned.Count}");
                    return true;
                }
                else
                {
                    Debug.WriteLine($"[PairsTrading] ⚠️ Выровненных точек: {aligned.Count}, нужно: {requiredCandles}");
                    _indicatorValues.Status = $"⚠️ Недостаточно выровненных данных: {aligned.Count}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PairsTrading] ❌ Ошибка проверки свечей: {ex.Message}");
                _logger.LogError(ex, "Error checking candles for pair");
                _candlesLoadingInProgress = false;
                _indicatorValues.Status = $"❌ Ошибка: {ex.Message}";
                return false;
            }
        }

        private async Task LoadCandlesForInstrumentFromApiAsync(string uid, string ticker, string timeframe)
        {
            try
            {
                //Debug.WriteLine($"[PairsTrading] Загрузка свечей для {ticker} (UID: {uid})");

                int daysToLoad = GetDaysToLoad(timeframe);
                var endTime = DateTime.UtcNow;
                var startTime = endTime.AddDays(-daysToLoad);

                // ✅ Проверяем, есть ли уже свечи в БД
                int requiredCandles = CalculateLookbackCandles(timeframe, _parameters.LookbackPeriod);
                var existingCandles = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(ticker, timeframe, requiredCandles * 2);

                if (existingCandles != null && existingCandles.Any())
                {
                    // Если свечи есть, загружаем только недостающие
                    var lastCandleTime = existingCandles.LastOrDefault()?.Time;
                    if (lastCandleTime.HasValue)
                    {
                        // Загружаем только начиная с последней свечи + 1 интервал
                        var intervalMinutes = GetTimeframeMinutesForCandles(timeframe);
                        var newStartTime = lastCandleTime.Value.AddMinutes(intervalMinutes);

                        if (newStartTime < endTime)
                        {
                            //Debug.WriteLine($"[PairsTrading]   Загрузка недостающих свечей с {newStartTime:yyyy-MM-dd HH:mm} по {endTime:yyyy-MM-dd HH:mm}");
                            startTime = newStartTime.ToUniversalTime();
                        }
                        else
                        {
                            //Debug.WriteLine($"[PairsTrading]   ✅ Свечи актуальны, пропускаем загрузку для {ticker}");
                            return;
                        }
                    }
                }

                //Debug.WriteLine($"[PairsTrading]   Загрузка за период: {startTime:yyyy-MM-dd} - {endTime:yyyy-MM-dd}");

                var candles = await _provider.GetHistoricalDataAsync(ticker, uid, timeframe, startTime, endTime);

                if (candles != null && candles.Any())
                {
                    //Debug.WriteLine($"[PairsTrading]   Загружено {candles.Count} свечей для {ticker}");
                    await _strategyViewModel.SaveCandlesAsync(ticker, timeframe, candles);
                    //Debug.WriteLine($"[PairsTrading]   Свечи сохранены в БД для {ticker}");
                }
                else
                {
                    //Debug.WriteLine($"[PairsTrading]   ⚠️ Не удалось загрузить свечи для {ticker}");
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading]   ❌ Ошибка загрузки свечей для {ticker}: {ex.Message}");
                _logger.LogError(ex, $"Error loading candles for {ticker}");
            }
        }

        //  вспомогательный метод
        private int GetTimeframeMinutesForCandles(string timeframe)
        {
            return timeframe?.ToLower() switch
            {
                "1min" => 1,
                "5min" => 5,
                "10min" => 10,
                "15min" => 15,
                "30min" => 30,
                "1hour" => 60,
                "2hour" => 120,
                "4hour" => 240,
                "1day" => 1440,
                _ => 60
            };
        }

        /// <summary>
        /// Возвращает количество дней для загрузки в зависимости от таймфрейма
        /// </summary>
        private int GetDaysToLoad(string timeframe)
        {
            // ✅ Для фьючерсов (IMOEXF) нужно загружать больше данных
            bool isFutures = _instrumentATicker == "IMOEXF" || _instrumentATicker?.Contains("F") == true;
            int multiplier = isFutures ? 3 : 1; // Для фьючерсов загружаем в 3 раза больше

            return timeframe?.ToLower() switch
            {
                "1min" => 7 * multiplier,
                "5min" => 14 * multiplier,
                "10min" => 21 * multiplier,
                "15min" => 30 * multiplier,
                "30min" => 90 * multiplier,  // Увеличено для фьючерсов
                "1hour" => 120 * multiplier,
                "2hour" => 180 * multiplier,
                "4hour" => 240 * multiplier,
                "1day" => 365 * multiplier,
                "1week" => 730 * multiplier,
                _ => 30 * multiplier
            };
        }

        /// <summary>
        /// Возвращает максимальный возраст свечей в часах для таймфрейма
        /// </summary>
        private double GetMaxCandleAgeHours(string timeframe)
        {
            // ✅ Увеличиваем допустимый возраст для фьючерсов
            bool isFutures = _instrumentATicker == "IMOEXF" || _instrumentATicker?.Contains("F") == true;
            double multiplier = isFutures ? 2 : 1;

            return timeframe?.ToLower() switch
            {
                "1min" => 0.5 * multiplier,
                "5min" => 1 * multiplier,
                "10min" => 2 * multiplier,
                "15min" => 3 * multiplier,
                "30min" => 12 * multiplier,  // Увеличено с 6 до 12 часов
                "1hour" => 24 * multiplier,
                "2hour" => 48 * multiplier,
                "4hour" => 72 * multiplier,
                "1day" => 168 * multiplier,
                "1week" => 336 * multiplier,
                _ => 12 * multiplier
            };
        }




        // Обновленный метод BuildModelAsync - добавим обновление статуса


        // Исправленный метод BuildModelAsync
        public async Task BuildModelAsync()
        {
            // Предотвращаем одновременные вызовы
            if (_isModelBuilding)
            {
                Debug.WriteLine($"[PairsTrading] ⏳ Построение модели уже выполняется, пропускаем");
                return;
            }

            if (!_isBacktestMode)
            {
                if ((DateTime.Now - _lastModelBuildAttempt).TotalMilliseconds < MODEL_BUILD_COOLDOWN_MS)
                {
                    Debug.WriteLine($"[PairsTrading] ⏳ Слишком частое построение модели, пропускаем");
                    return;
                }
            }

            _isModelBuilding = true;
            _lastModelBuildAttempt = DateTime.Now;

            try
            {
                Debug.WriteLine($"[PairsTrading] ========== ПОСТРОЕНИЕ МОДЕЛИ ==========");
                Debug.WriteLine($"[PairsTrading] Время: {DateTime.Now:HH:mm:ss.fff}");

                // ✅ Обновляем подписи инструментов в UI
                UpdateControlPanelLabels();

                // ✅ В БЭКТЕСТ-РЕЖИМЕ ИСПОЛЬЗУЕМ КЭШ
                if (_isBacktestMode)
                {
                    if (_cachedInstrumentsA != null && _cachedInstrumentsB != null)
                    {
                        _instrumentA = _cachedInstrumentsA;
                        _instrumentAUid = _cachedInstrumentsA.Uid;
                        _instrumentATicker = _cachedInstrumentsA.Ticker;

                        _instrumentB = _cachedInstrumentsB;
                        _instrumentBUid = _cachedInstrumentsB.Uid;
                        _instrumentBTicker = _cachedInstrumentsB.Ticker;

                        Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: используем кэш A={_instrumentATicker}, B={_instrumentBTicker}");
                    }
                    else
                    {
                        Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: КЭШ ПУСТ! Модель не может быть построена");
                        _indicatorValues.Status = "❌ Кэш инструментов пуст";
                        _parameters.ModelValid = false;
                        _isModelBuilding = false;
                        return;
                    }
                }
                else
                {
                    // ✅ В РЕАЛЬНОМ РЕЖИМЕ проверяем, что инструменты загружены
                    if (_instrumentA == null || string.IsNullOrEmpty(_instrumentAUid))
                    {
                        Debug.WriteLine($"[PairsTrading] Инструмент A не загружен, загружаем...");
                        await LoadPairInstrumentAsync();
                    }

                    if (_instrumentB == null || string.IsNullOrEmpty(_instrumentBUid))
                    {
                        Debug.WriteLine($"[PairsTrading] Инструмент B не загружен, загружаем...");
                        await LoadPairInstrumentAsync();
                    }
                }

                if (_instrumentA == null || _instrumentB == null)
                {
                    Debug.WriteLine($"[PairsTrading] ❌ НЕВОЗМОЖНО ПОСТРОИТЬ МОДЕЛЬ - инструменты отсутствуют!");
                    _indicatorValues.Status = "❌ Инструменты не найдены";
                    _parameters.ModelValid = false;
                    _isModelBuilding = false;
                    return;
                }

                // ✅ Проверяем наличие свечей
                bool candlesReady;
                if (_isBacktestMode && _backtestCandlesA != null && _backtestCandlesB != null)
                {
                    candlesReady = _backtestCandlesA.Count > 0 && _backtestCandlesB.Count > 0;
                    Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: свечи A={_backtestCandlesA.Count}, B={_backtestCandlesB.Count}");
                }
                else
                {
                    candlesReady = await CheckCandlesForPairAsync();
                }

                if (!candlesReady)
                {
                    Debug.WriteLine($"[PairsTrading] ❌ НЕТ ДАННЫХ СВЕЧЕЙ!");
                    _indicatorValues.Status = "❌ Нет данных свечей";
                    _parameters.ModelValid = false;
                    _isModelBuilding = false;
                    return;
                }

                try
                {
                    _indicatorValues.Status = "Построение модели...";
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));

                    int lookbackCandles = CalculateLookbackCandles(_timeframe, _parameters.LookbackPeriod);
                    Debug.WriteLine($"[PairsTrading]   Период обучения: {_parameters.LookbackPeriod}, свечей: {lookbackCandles}");

                    // ✅ Загружаем свечи
                    List<Models.Candle> candlesA, candlesB;
                    string timeFrameForLoad = _timeframe ?? "1hour";

                    if (_isBacktestMode && _backtestCandlesA != null && _backtestCandlesB != null)
                    {
                        candlesA = _backtestCandlesA;
                        candlesB = _backtestCandlesB;
                        Debug.WriteLine($"[PairsTrading]   Использование переданных свечей для бэктеста");
                    }
                    else
                    {
                        int requiredCandles = CalculateLookbackCandles(timeFrameForLoad, _parameters.LookbackPeriod);
                        int loadCount = requiredCandles * 2;

                        candlesA = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(
                            _instrumentATicker, timeFrameForLoad, loadCount);
                        candlesB = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(
                            _instrumentBTicker, timeFrameForLoad, loadCount);

                        Debug.WriteLine($"[PairsTrading]   Свечей A ({_instrumentATicker}): {candlesA?.Count ?? 0}, B ({_instrumentBTicker}): {candlesB?.Count ?? 0}");
                    }

                    if (candlesA == null || candlesA.Count < lookbackCandles ||
                        candlesB == null || candlesB.Count < lookbackCandles)
                    {
                        Debug.WriteLine($"[PairsTrading] ❌ НЕДОСТАТОЧНО ДАННЫХ!");
                        Debug.WriteLine($"[PairsTrading]   Требуется: {lookbackCandles}, A={candlesA?.Count ?? 0}, B={candlesB?.Count ?? 0}");
                        _indicatorValues.Status = $"⚠️ Недостаточно данных: A={candlesA?.Count ?? 0}, B={candlesB?.Count ?? 0}";
                        _parameters.ModelValid = false;
                        _isModelBuilding = false;
                        return;
                    }

                    // ✅ Выравниваем данные
                    Debug.WriteLine($"[PairsTrading] ВЫРАВНИВАНИЕ ДАННЫХ...");
                    var aligned = AlignCandles(candlesA, candlesB);
                    Debug.WriteLine($"[PairsTrading]   Выровненных точек: {aligned.Count}");

                    if (aligned.Count < lookbackCandles)
                    {
                        Debug.WriteLine($"[PairsTrading] ❌ НЕДОСТАТОЧНО ВЫРОВНЕННЫХ ДАННЫХ!");
                        Debug.WriteLine($"[PairsTrading]   Требуется: {lookbackCandles}, доступно: {aligned.Count}");

                        // ✅ ДОПОЛНИТЕЛЬНАЯ ДИАГНОСТИКА
                        if (candlesA.Any() && candlesB.Any())
                        {
                            Debug.WriteLine($"[PairsTrading]   Диапазон A: {candlesA.First().Time:yyyy-MM-dd HH:mm} - {candlesA.Last().Time:yyyy-MM-dd HH:mm}");
                            Debug.WriteLine($"[PairsTrading]   Диапазон B: {candlesB.First().Time:yyyy-MM-dd HH:mm} - {candlesB.Last().Time:yyyy-MM-dd HH:mm}");

                            // ✅ Проверяем пересечение
                            var aLast = candlesA.Last().Time;
                            var bLast = candlesB.Last().Time;
                            var aFirst = candlesA.First().Time;
                            var bFirst = candlesB.First().Time;
                            bool hasOverlap = !(aLast < bFirst || bLast < aFirst);

                            if (!hasOverlap)
                            {
                                Debug.WriteLine($"[PairsTrading] ❌ НЕТ ПЕРЕСЕЧЕНИЯ ПО ВРЕМЕНИ!");
                                Debug.WriteLine($"[PairsTrading]   Попытка принудительной загрузки свежих данных...");

                                // ✅ Попытка принудительной загрузки
                                _candlesLoadingInProgress = false;
                                _lastCandlesLoadAttempt = DateTime.MinValue; // Сбрасываем блокировку
                                var candlesReadyy = await CheckCandlesForPairAsync();

                                if (candlesReadyy)
                                {
                                    // Повторяем загрузку
                                    candlesA = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(
                                        _instrumentATicker, timeFrameForLoad, lookbackCandles * 2);
                                    candlesB = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(
                                        _instrumentBTicker, timeFrameForLoad, lookbackCandles * 2);

                                    aligned = AlignCandles(candlesA, candlesB);
                                    Debug.WriteLine($"[PairsTrading]   После принудительной загрузки: {aligned.Count} точек");
                                }
                            }
                        }

                        if (aligned.Count < lookbackCandles)
                        {
                            _indicatorValues.Status = $"⚠️ Недостаточно выровненных данных: {aligned.Count} (нужно {lookbackCandles})";
                            _parameters.ModelValid = false;
                            _isModelBuilding = false;
                            return;
                        }
                    }

                    // ✅ Рассчитываем регрессию
                    Debug.WriteLine($"[PairsTrading] РАСЧЕТ ЛИНЕЙНОЙ РЕГРЕССИИ...");
                    var modelData = aligned.TakeLast(lookbackCandles).ToList();
                    var (beta, alpha, correlation) = CalculateLinearRegression(modelData);

                    Debug.WriteLine($"[PairsTrading]   β={beta:F6}, ρ={correlation:F6}");

                    if (beta <= 0 || correlation < 0.3m)
                    {
                        Debug.WriteLine($"[PairsTrading] ❌ СЛАБАЯ КОРРЕЛЯЦИЯ ИЛИ ОТРИЦАТЕЛЬНЫЙ БЕТА!");
                        _indicatorValues.Status = $"⚠️ Слабая корреляция: {correlation:F2}";
                        _parameters.ModelValid = false;
                        _isModelBuilding = false;
                        return;
                    }

                    // ✅ Рассчитываем статистику спреда
                    Debug.WriteLine($"[PairsTrading] РАСЧЕТ СТАТИСТИКИ СПРЕДА...");
                    var spreads = modelData.Select(d => d.PriceA - beta * d.PriceB).ToList();
                    var mean = spreads.Average();
                    var std = CalculateStdDev(spreads, mean);

                    Debug.WriteLine($"[PairsTrading]   Среднее: {mean:F6}, Std: {std:F6}");

                    // ✅ Сохраняем модель
                    _parameters.HedgeRatio = beta;
                    _parameters.SpreadMean = mean;
                    _parameters.SpreadStd = std;
                    _parameters.ModelValid = true;
                    _parameters.ModelLastUpdate = DateTime.Now;

                    _indicatorValues.HedgeRatio = beta;
                    _indicatorValues.SpreadMean = mean;
                    _indicatorValues.SpreadStd = std;

                    Debug.WriteLine($"[PairsTrading] ✅ МОДЕЛЬ ПОСТРОЕНА УСПЕШНО!");
                    _indicatorValues.Status = $"✅ Модель готова. β={beta:F4}, ρ={correlation:F2}";
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));

                    _logger.LogInformation($"Model built: beta={beta:F4}, mean={mean:F4}, std={std:F4}, corr={correlation:F2}");

                    // ✅ Обновляем спред
                    if (!_isBacktestMode)
                    {
                        await UpdateSpreadAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PairsTrading] ❌ ОШИБКА ПОСТРОЕНИЯ МОДЕЛИ: {ex.Message}");
                    _logger.LogError(ex, "Error building model");
                    _indicatorValues.Status = $"❌ Ошибка: {ex.Message}";
                    _parameters.ModelValid = false;
                }
            }
            finally
            {
                _isModelBuilding = false;
            }
        }

        // Метод для установки свечей для бэктеста
        public void SetBacktestCandles(List<Models.Candle> candlesA, List<Models.Candle> candlesB)
        {
            _backtestCandlesA = candlesA;
            _backtestCandlesB = candlesB;
            _isBacktestMode = true;

            // ✅ Если инструменты уже загружены - сохраняем в кэш
            if (_instrumentA != null && _instrumentB != null)
            {
                _cachedInstrumentsA = _instrumentA;
                _cachedInstrumentsB = _instrumentB;
               // //Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: инструменты сохранены в кэш при установке свечей: A={_instrumentA.Ticker}, B={_instrumentB.Ticker}");
            }
            else
            {
                ////Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: инструменты НЕ загружены, кэш пуст");
            }

            //Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: установлены свечи A={candlesA?.Count ?? 0}, B={candlesB?.Count ?? 0}");
        }

        // Метод для выхода из бэктест-режима
        public void DisableBacktestMode()
        {
            _isBacktestMode = false;
            _backtestCandlesA = null;
            _backtestCandlesB = null;
            _simulatedPositionA = 0;
            _simulatedPositionB = 0;
            _lastBacktestSignal = "WAIT";
            //Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ ОТКЛЮЧЕН");
        }






        /// <summary>
        /// Рассчитывает количество свечей для обучения в зависимости от таймфрейма
        /// </summary>
        // В методе CalculateLookbackCandles, измените логику:

        private int CalculateLookbackCandles(string timeframe, int lookbackPeriod)
        {
            // Минимальное количество свечей для обучения (чтобы модель была статистически значимой)
            const int MIN_CANDLES = 50;

            // Максимальное количество свечей для обучения (ограничение производительности)
            const int MAX_CANDLES = 1500;

            // lookbackPeriod уже выражен в единицах таймфрейма
            // Например: для 30min и lookbackPeriod=24 -> нужно 24 свечи = 12 часов
            int candles = lookbackPeriod;

            // Для очень маленьких периодов используем минимум
            if (candles < MIN_CANDLES)
                candles = MIN_CANDLES;

            // Ограничиваем максимум, чтобы не запрашивать слишком много данных
            if (candles > MAX_CANDLES)
                candles = MAX_CANDLES;

            //Debug.WriteLine($"[PairsTrading] Расчет свечей: период={lookbackPeriod}, таймфрейм={timeframe}, свечей={candles}");

            return candles;
        }

        private async Task<List<Models.Candle>> LoadCandlesForInstrumentAsync(string instrumentUid, string ticker, int count)
        {
            try
            {
                //Debug.WriteLine($"[PairsTrading] Загрузка свечей для {ticker} (UID: {instrumentUid}), кол-во: {count}");

                // ✅ Используем новый метод с тикером
                var candles = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(ticker, _timeframe, count);

                //Debug.WriteLine($"[PairsTrading]   Загружено: {candles?.Count ?? 0} свечей для {ticker}");
                return candles ?? new List<Models.Candle>();
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading]   ❌ Ошибка загрузки для {ticker}: {ex.Message}");
                _logger.LogError(ex, $"Error loading candles for {ticker}");
                return new List<Models.Candle>();
            }
        }

        private List<AlignedCandleData> AlignCandles(List<Models.Candle> candlesA, List<Models.Candle> candlesB)
        {
            //Debug.WriteLine($"[PairsTrading] Выравнивание свечей...");
            var result = new List<AlignedCandleData>();
            var dictB = candlesB.ToDictionary(c => c.Time, c => c.Close);

            int matchedCount = 0;
            foreach (var ca in candlesA)
            {
                if (dictB.TryGetValue(ca.Time, out var priceB))
                {
                    result.Add(new AlignedCandleData { Time = ca.Time, PriceA = ca.Close, PriceB = priceB });
                    matchedCount++;
                }
            }

            //Debug.WriteLine($"[PairsTrading]   Совпадений по времени: {matchedCount} из {candlesA.Count}");
            return result.OrderBy(d => d.Time).ToList();
        }

        private class AlignedCandleData
        {
            public DateTime Time { get; set; }
            public decimal PriceA { get; set; }
            public decimal PriceB { get; set; }
        }

        private (decimal beta, decimal alpha, decimal correlation) CalculateLinearRegression(List<AlignedCandleData> data)
        {
            int n = data.Count;
            //Debug.WriteLine($"[PairsTrading]   Расчет регрессии для {n} точек");

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
            decimal numerator = sumXY - n * meanX * meanY;
            decimal denominator = sumX2 - n * meanX * meanX;

            if (denominator == 0)
            {
                //Debug.WriteLine($"[PairsTrading]   ❌ Знаменатель регрессии равен 0!");
                return (0, 0, 0);
            }

            decimal beta = numerator / denominator;
            decimal alpha = meanY - beta * meanX;

            decimal covXY = (sumXY - n * meanX * meanY) / n;
            decimal varX = (sumX2 - n * meanX * meanX) / n;
            decimal varY = (sumY2 - n * meanY * meanY) / n;
            decimal correlation = (varX > 0 && varY > 0) ? covXY / (decimal)Math.Sqrt((double)(varX * varY)) : 0;

            //Debug.WriteLine($"[PairsTrading]     β={beta:F6}, α={alpha:F6}, ρ={correlation:F6}");

            return (beta, alpha, correlation);
        }

        private decimal CalculateStdDev(List<decimal> values, decimal mean)
        {
            if (values.Count < 2) return 0;
            double sumSq = 0;
            foreach (var v in values)
            {
                double diff = (double)(v - mean);
                sumSq += diff * diff;
            }
            return (decimal)Math.Sqrt(sumSq / (values.Count - 1));
        }

        private async Task UpdateSpreadAsync()
        {
            if (!_parameters.ModelValid)
            {
                //Debug.WriteLine($"[PairsTrading] ⚠️ Обновление спреда пропущено - модель невалидна");
                // ✅ ДОБАВЛЯЕМ: Обновляем статус в UI
                _indicatorValues.Status = "❌ Модель невалидна. Нажмите 'Построить модель'";
                _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));
                return;
            }


            if (_isBacktestMode)
            {
                // В бэктесте позиции управляются через SetSimulatedPosition
                return;
            }









            try
            {
                _lastPriceA = await _provider.GetCurrentPriceAsync(_instrumentAUid);
                _lastPriceB = await _provider.GetCurrentPriceAsync(_instrumentBUid);
                _indicatorValues.PriceA = _lastPriceA;
                _indicatorValues.PriceB = _lastPriceB;

                if (_lastPriceA > 0 && _lastPriceB > 0)
                {
                    decimal spread = _lastPriceA - _parameters.HedgeRatio * _lastPriceB;
                    _indicatorValues.CurrentSpread = spread;

                    if (_parameters.SpreadStd > 0)
                    {
                        decimal zScore = (spread - _parameters.SpreadMean) / _parameters.SpreadStd;
                        _indicatorValues.CurrentZScore = zScore;

                        // ✅ ДОБАВЛЯЕМ: Обновляем статус с актуальными значениями
                        _indicatorValues.Status = $"✅ Модель активна. Z-Score={zScore:F2}, β={_parameters.HedgeRatio:F4}";
                        _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));

                        // Отладочный вывод (каждые 10 обновлений)
                        _debugCounter++;
                        if (_debugCounter % 10 == 0 || (DateTime.Now - _lastDebugLogTime).TotalSeconds > 5)
                        {
                            //Debug.WriteLine($"[PairsTrading] ТЕКУЩИЕ ЗНАЧЕНИЯ:");
                            //Debug.WriteLine($"[PairsTrading]   A={_lastPriceA:F4}, B={_lastPriceB:F4}");
                            //Debug.WriteLine($"[PairsTrading]   Спред={spread:F6}, Среднее={_parameters.SpreadMean:F6}, Std={_parameters.SpreadStd:F6}");
                            //Debug.WriteLine($"[PairsTrading]   Z-Score={zScore:F6} (Порог входа: ±{_parameters.EntryZScore:F2})");
                            _lastDebugLogTime = DateTime.Now;
                        }
                    }
                }
                else
                {
                    //Debug.WriteLine($"[PairsTrading] ⚠️ Некорректные цены: A={_lastPriceA}, B={_lastPriceB}");
                    _indicatorValues.Status = "⚠️ Некорректные цены";
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка обновления спреда: {ex.Message}");
                _logger.LogError(ex, "Error updating spread");
                _indicatorValues.Status = $"❌ Ошибка: {ex.Message}";
                _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));
            }
        }
        #endregion

        #region Основная логика
        // Обновленный метод ProcessMarketData - для совместимости с StrategyViewModel
        public async Task ProcessMarketData(MarketData marketData)
        {
            if (marketData == null)
                return;

            try
            {
                // ✅ БЭКТЕСТ-РЕЖИМ: Только обновляем цены и вычисляем сигналы
                if (_isBacktestMode)
                {
                    // ✅ В БЭКТЕСТ-РЕЖИМЕ НЕ ИСПОЛЬЗУЕМ РЕАЛЬНЫЕ ДАННЫЕ
                    // Данные приходят из симуляции, а не из API
                    if (marketData.InstrumentUid == _instrumentAUid && marketData.LastPrice > 0)
                    {
                        _lastPriceA = marketData.LastPrice;
                        _indicatorValues.PriceA = _lastPriceA;

                        if (_parameters.ModelValid)
                        {
                            UpdateSpreadAndZScore();
                        }
                    }
                    else if (marketData.InstrumentUid == _instrumentBUid && marketData.LastPrice > 0)
                    {
                        _lastPriceB = marketData.LastPrice;
                        _indicatorValues.PriceB = _lastPriceB;

                        if (_parameters.ModelValid)
                        {
                            UpdateSpreadAndZScore();
                        }
                    }

                    // Вычисляем сигнал без отправки ордеров
                    if (_parameters.ModelValid && _lastPriceA > 0 && _lastPriceB > 0)
                    {
                        _lastBacktestSignal = GetSignalOnly();
                    }

                    return;
                }

                // ✅ РЕАЛЬНЫЙ РЕЖИМ: Полная логика с ордерами
                if (State != StrategyState.Running)
                    return;

                // Обновляем цены через подписку
                if (marketData.InstrumentUid == _instrumentAUid && marketData.LastPrice > 0)
                {
                    _lastPriceA = marketData.LastPrice;
                    _lastPriceUpdateA = DateTime.Now;
                    _indicatorValues.PriceA = _lastPriceA;
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceA));

                    if (_parameters.ModelValid)
                    {
                        UpdateSpreadAndZScore();
                    }
                }
                else if (marketData.InstrumentUid == _instrumentBUid && marketData.LastPrice > 0)
                {
                    _lastPriceB = marketData.LastPrice;
                    _lastPriceUpdateB = DateTime.Now;
                    _indicatorValues.PriceB = _lastPriceB;
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceB));

                    if (_parameters.ModelValid)
                    {
                        UpdateSpreadAndZScore();
                    }
                }
                else
                {
                    if (_debugCounter % 100 == 0)
                    {
                        //Debug.WriteLine($"[PairsTrading] ProcessMarketData: Получен data для {marketData.InstrumentUid}, цена={marketData.LastPrice}");
                    }
                }

                await CheckPendingOrdersAsync();
                await UpdatePositionsAsync();

                // Обновляем модель только если это действительно необходимо
                bool shouldRebuild = false;

                if (!_parameters.ModelValid && _modelBuildAttempts < MAX_MODEL_BUILD_ATTEMPTS)
                {
                    shouldRebuild = true;
                    //Debug.WriteLine($"[PairsTrading] 🔄 Модель невалидна для {_instrumentBTicker}, попытка {_modelBuildAttempts + 1}/{MAX_MODEL_BUILD_ATTEMPTS}");
                }
                else if (_parameters.ModelValid && (DateTime.Now - _parameters.ModelLastUpdate).TotalHours > 6)
                {
                    shouldRebuild = true;
                    //Debug.WriteLine($"[PairsTrading] ⏰ Плановое обновление модели для {_instrumentBTicker}");
                }

                if (shouldRebuild)
                {
                    _modelBuildAttempts++;
                    await BuildModelAsync();

                    if (_parameters.ModelValid)
                    {
                        _modelBuildAttempts = 0;
                    }
                }

                await ProcessSignalsAsync();
                _indicatorValues.LastUpdate = DateTime.Now;

                _debugCounter++;
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка в ProcessMarketData для {_instrumentBTicker}: {ex.Message}");
                _logger.LogError(ex, "Error in ProcessMarketData for {Ticker}", _instrumentBTicker);
            }
        }

        /// <summary>
        /// Получает сигнал без выполнения ордеров (для бэктеста)
        /// </summary>
        private string GetSignalOnly()
        {
            if (!_parameters.ModelValid || _lastPriceA <= 0 || _lastPriceB <= 0)
                return "WAIT";

            decimal zScore = _indicatorValues.CurrentZScore;
            decimal entryThreshold = _parameters.EntryZScore;
            decimal exitThreshold = _parameters.ExitZScore;

            // Проверяем наличие позиции через внешний флаг (устанавливается из симуляции)
            bool hasPosition = _simulatedPositionA != 0 || _simulatedPositionB != 0;

            // --- Вход ---
            if (!hasPosition)
            {
                if (zScore > entryThreshold)
                {
                    // ✅ При входе также рассчитываем количества для бэктеста
                    // Они будут использованы в симуляции
                    decimal balance = 100000m;
                    decimal totalCapitalForPair = balance * (_parameters.PositionSizePercent / 100);
                    decimal capitalPerLeg = totalCapitalForPair / 2;

                    decimal priceA = _lastPriceA;
                    decimal priceB = _lastPriceB;

                    decimal qtyA = Math.Floor(capitalPerLeg / priceA);
                    decimal qtyB = Math.Floor(capitalPerLeg / priceB);

                    // ✅ Сохраняем количества для симуляции
                    _simulatedPositionA = -qtyA;
                    _simulatedPositionB = qtyB;

                    return "SHORT_A_LONG_B"; // Шорт A, Лонг B
                }
                else if (zScore < -entryThreshold)
                {
                    decimal balance = 100000m;
                    decimal totalCapitalForPair = balance * (_parameters.PositionSizePercent / 100);
                    decimal capitalPerLeg = totalCapitalForPair / 2;

                    decimal priceA = _lastPriceA;
                    decimal priceB = _lastPriceB;

                    decimal qtyA = Math.Floor(capitalPerLeg / priceA);
                    decimal qtyB = Math.Floor(capitalPerLeg / priceB);

                    _simulatedPositionA = qtyA;
                    _simulatedPositionB = -qtyB;

                    return "LONG_A_SHORT_B"; // Лонг A, Шорт B
                }
                return "WAIT";
            }

            // --- Выход ---
            if (hasPosition)
            {
                if (Math.Abs(zScore) <= exitThreshold)
                {
                    _simulatedPositionA = 0;
                    _simulatedPositionB = 0;
                    return "EXIT"; // Целевой выход
                }
                else if (Math.Abs(zScore) > _parameters.StopLossZScore)
                {
                    _simulatedPositionA = 0;
                    _simulatedPositionB = 0;
                    return "EXIT_STOP"; // Выход по стоп-лоссу
                }
                return "HOLD"; // Держим позицию
            }

            return "WAIT";
        }

        // Метод для установки симулированной позиции извне
        public void SetSimulatedPosition(decimal posA, decimal posB)
        {
            _simulatedPositionA = posA;
            _simulatedPositionB = posB;
        }

        // Метод для получения последнего сигнала (для бэктеста)
        public string GetLastBacktestSignal()
        {
            return _lastBacktestSignal;
        }






        private async Task ProcessSignalsAsync()
        {
            if (!_parameters.ModelValid || _lastPriceA <= 0 || _lastPriceB <= 0)
            {
                if (!_parameters.ModelValid)
                    Debug.WriteLine($"[PairsTrading] ⚠️ Сигналы пропущены - модель невалидна для {_instrumentBTicker}");
                return;
            }

            decimal zScore = _indicatorValues.CurrentZScore;

            // ✅ Проверяем наличие позиции через реальные данные
            bool hasPosition = _positionA != null && _positionB != null &&
                               _positionA.Quantity != 0 && _positionB.Quantity != 0;

            // ✅ ДОБАВЛЯЕМ: Проверяем, что позиция действительно валидная (противоположные направления)
            bool isValidPairPosition = false;
            if (hasPosition)
            {
                bool isA_Long = _positionA.Quantity > 0;
                bool isA_Short = _positionA.Quantity < 0;
                bool isB_Long = _positionB.Quantity > 0;
                bool isB_Short = _positionB.Quantity < 0;
                isValidPairPosition = (isA_Long && isB_Short) || (isA_Short && isB_Long);
            }

            // Если есть позиция, но она невалидна - сбрасываем состояние
            if (hasPosition && !isValidPairPosition)
            {
                Debug.WriteLine($"[PairsTrading] ⚠️ Обнаружена невалидная пара позиций, сброс состояния");
                _tradeState = PairsTradeState.NoPosition;
                _entryPass = true;
                _exitPass = true;
                _positionA = null;
                _positionB = null;
                return;
            }

            Debug.WriteLine($"[PairsTrading] ОБРАБОТКА СИГНАЛОВ ({_instrumentBTicker}):");
            Debug.WriteLine($"[PairsTrading]   Z-Score: {zScore:F4}");
            Debug.WriteLine($"[PairsTrading]   Есть позиция: {hasPosition}");
            Debug.WriteLine($"[PairsTrading]   entryPass: {_entryPass}, exitPass: {_exitPass}");

            // --- Вход ---
            if (!hasPosition && _entryPass)
            {
                // ✅ ДОБАВЛЯЕМ: Защита от слишком частых входов
                if ((DateTime.Now - _positionOpenTime).TotalMinutes < 1)
                {
                    Debug.WriteLine($"[PairsTrading] ⏳ Слишком рано для входа (прошло {(DateTime.Now - _positionOpenTime).TotalSeconds:F0}с)");
                    return;
                }

                if (zScore > _parameters.EntryZScore)
                {
                    Debug.WriteLine($"[PairsTrading] 🟢 СИГНАЛ НА ВХОД: Шорт A, Лонг B (Z-Score={zScore:F4} > {_parameters.EntryZScore:F2})");
                    _indicatorValues.Signal = "СИГНАЛ: Шорт A, Лонг B";
                    _indicatorValues.SignalColor = Brushes.Orange;
                    _indicatorValues.SignalDescription = $"Z-Score={zScore:F2} > {_parameters.EntryZScore:F2} (Ожидание схождения)";
                    await ExecuteEntryAsync(PositionDirection.Short, PositionDirection.Long);
                }
                else if (zScore < -_parameters.EntryZScore)
                {
                    Debug.WriteLine($"[PairsTrading] 🟢 СИГНАЛ НА ВХОД: Лонг A, Шорт B (Z-Score={zScore:F4} < -{_parameters.EntryZScore:F2})");
                    _indicatorValues.Signal = "СИГНАЛ: Лонг A, Шорт B";
                    _indicatorValues.SignalColor = Brushes.Blue;
                    _indicatorValues.SignalDescription = $"Z-Score={zScore:F2} < -{_parameters.EntryZScore:F2} (Ожидание схождения)";
                    await ExecuteEntryAsync(PositionDirection.Long, PositionDirection.Short);
                }
                else
                {
                    _indicatorValues.Signal = "ОЖИДАНИЕ";
                    _indicatorValues.SignalColor = Brushes.Gray;
                    _indicatorValues.SignalDescription = $"Z-Score: {zScore:F2} (Порог: ±{_parameters.EntryZScore:F2})";
                }
            }
            // --- Выход ---
            else if (hasPosition && isValidPairPosition && _exitPass)
            {
                bool shouldExit = false;
                string reason = "";

                decimal exitThreshold = _parameters.ExitZScore;
                decimal stopLossThreshold = _parameters.StopLossZScore;

                if (Math.Abs(zScore) <= exitThreshold)
                {
                    shouldExit = true;
                    reason = $"Целевой Z-Score: {zScore:F2} (≤ {exitThreshold:F2})";
                    Debug.WriteLine($"[PairsTrading] 🔴 ВЫХОД: Достигнут целевой Z-Score");
                }
                else if (Math.Abs(zScore) > stopLossThreshold)
                {
                    shouldExit = true;
                    reason = $"Стоп-лосс по Z-Score: {zScore:F2} (>{stopLossThreshold:F2})";
                    Debug.WriteLine($"[PairsTrading] 🔴 ВЫХОД ПО СТОП-ЛОССУ!");
                }
                else if ((DateTime.Now - _positionOpenTime).TotalDays > 7)
                {
                    shouldExit = true;
                    reason = $"Таймаут удержания ({(DateTime.Now - _positionOpenTime).Days} дней)";
                    Debug.WriteLine($"[PairsTrading] 🔴 ВЫХОД ПО ТАЙМАУТУ");
                }

                if (shouldExit)
                {
                    _indicatorValues.Signal = "СИГНАЛ НА ВЫХОД";
                    _indicatorValues.SignalColor = Brushes.Red;
                    _indicatorValues.SignalDescription = reason;
                    await ExecuteExitAsync(reason);
                }
                else
                {
                    string dirA = _positionA.Quantity > 0 ? "Лонг" : "Шорт";
                    string dirB = _positionB.Quantity > 0 ? "Лонг" : "Шорт";
                    _indicatorValues.CurrentPosition = $"A: {dirA} {Math.Abs(_positionA.Quantity)}, B: {dirB} {Math.Abs(_positionB.Quantity)}";
                    _indicatorValues.Signal = "ПОЗИЦИЯ АКТИВНА";
                    _indicatorValues.SignalColor = Brushes.Green;
                    _indicatorValues.SignalDescription = $"Z-Score: {zScore:F2}, Удержание: {(DateTime.Now - _positionOpenTime).TotalHours:F1}ч";
                }
            }
            else if (!hasPosition)
            {
                _indicatorValues.CurrentPosition = "Нет позиции";
                Debug.WriteLine($"[PairsTrading] ⏳ Ожидание сигнала...");
            }
        }
        #endregion

        #region Исполнение ордеров
        private async Task ExecuteEntryAsync(string directionA, string directionB)
        {
            if (!_entryPass)
            {
                Debug.WriteLine($"[PairsTrading] ⚠️ Вход заблокирован (_entryPass=false)");
                return;
            }

            // ✅ ДОБАВЛЯЕМ: Проверка на уже существующую позицию
            if (_positionA != null && _positionB != null &&
                _positionA.Quantity != 0 && _positionB.Quantity != 0)
            {
                Debug.WriteLine($"[PairsTrading] ⚠️ Уже есть активная позиция, пропускаем вход");
                return;
            }

            Debug.WriteLine($"[PairsTrading] ========== ИСПОЛНЕНИЕ ВХОДА ==========");
            Debug.WriteLine($"[PairsTrading]   Направление A: {directionA}");
            Debug.WriteLine($"[PairsTrading]   Направление B: {directionB}");

            _entryPass = false;

            try
            {
                // ✅ В бэктест-режиме НЕ запрашиваем баланс
                decimal balance;
                if (_isBacktestMode)
                {
                    balance = 100000m; // Фиксированный баланс для бэктеста
                    Debug.WriteLine($"[PairsTrading]   БЭКТЕСТ-РЕЖИМ: используется фиксированный баланс: {balance:F2}");
                }
                else
                {
                    balance = await _provider.GetAccountBalanceAsync();
                    Debug.WriteLine($"[PairsTrading]   Баланс: {balance:F2}");
                }

                // ✅ Рассчитываем общий капитал для пары
                decimal totalCapitalForPair = balance * (_parameters.PositionSizePercent / 100);
                decimal capitalPerLeg = totalCapitalForPair / 2;

                Debug.WriteLine($"[PairsTrading]   Капитал на пару: {totalCapitalForPair:F2}");
                Debug.WriteLine($"[PairsTrading]   Капитал на ногу: {capitalPerLeg:F2}");

                // ✅ Получаем текущие цены
                decimal priceA = _lastPriceA;
                decimal priceB = _lastPriceB;

                Debug.WriteLine($"[PairsTrading]   Цена A: {priceA:F2}, Цена B: {priceB:F2}");

                // ✅ Рассчитываем количество бумаг для A и B с одинаковой стоимостью
                // Используем среднюю цену для расчета количества, чтобы стоимость была примерно равна
                decimal avgPrice = (priceA + priceB) / 2;

                // ✅ Количество для A и B рассчитываем от общей стоимости пары
                // Чтобы стоимость позиций была примерно равна, используем пропорцию:
                // costA = qtyA * priceA, costB = qtyB * priceB
                // costA ≈ costB ≈ totalCapitalForPair / 2

                decimal qtyA = Math.Floor(capitalPerLeg / priceA);
                decimal qtyB = Math.Floor(capitalPerLeg / priceB);

                Debug.WriteLine($"[PairsTrading]   Расчет количества (через среднюю цену):");
                Debug.WriteLine($"[PairsTrading]     A: {capitalPerLeg:F2} / {priceA:F2} = {qtyA}");
                Debug.WriteLine($"[PairsTrading]     B: {capitalPerLeg:F2} / {priceB:F2} = {qtyB}");

                // ✅ Проверяем, что количество > 0
                if (qtyA <= 0 || qtyB <= 0)
                {
                    Debug.WriteLine($"[PairsTrading] ❌ Нулевое количество: A={qtyA}, B={qtyB}");
                    _logger.LogWarning($"Zero quantity: A={qtyA}, B={qtyB}");
                    _entryPass = true;
                    return;
                }

                // ✅ Пересчитываем реальную стоимость каждой позиции
                decimal realCostA = qtyA * priceA;
                decimal realCostB = qtyB * priceB;
                decimal totalCost = realCostA + realCostB;

                Debug.WriteLine($"[PairsTrading]   Реальная стоимость:");
                Debug.WriteLine($"[PairsTrading]     A: {qtyA} * {priceA:F2} = {realCostA:F2} ({realCostA / totalCapitalForPair * 100:F1}% от капитала)");
                Debug.WriteLine($"[PairsTrading]     B: {qtyB} * {priceB:F2} = {realCostB:F2} ({realCostB / totalCapitalForPair * 100:F1}% от капитала)");
                Debug.WriteLine($"[PairsTrading]     Общая стоимость: {totalCost:F2} ({(totalCost / totalCapitalForPair) * 100:F1}% от капитала)");

                // ✅ Проверяем, что стоимость позиций примерно равна (разница не более 20%)
                decimal maxDiff = Math.Max(realCostA, realCostB) * 0.2m;
                if (Math.Abs(realCostA - realCostB) > maxDiff && qtyA > 1 && qtyB > 1)
                {
                    Debug.WriteLine($"[PairsTrading] ⚠️ Большая разница в стоимости: A={realCostA:F2}, B={realCostB:F2}");
                    Debug.WriteLine($"[PairsTrading]   Пытаемся скорректировать количество...");

                    // ✅ Корректируем количество для выравнивания стоимости
                    // Увеличиваем меньшую позицию до стоимости большей (с учетом ограничений)
                    if (realCostA < realCostB)
                    {
                        decimal additionalA = Math.Floor((realCostB - realCostA) / priceA);
                        if (additionalA > 0)
                        {
                            qtyA += additionalA;
                            realCostA = qtyA * priceA;
                            Debug.WriteLine($"[PairsTrading]   Увеличили A на {additionalA} до {qtyA}, стоимость: {realCostA:F2}");
                        }
                    }
                    else if (realCostB < realCostA)
                    {
                        decimal additionalB = Math.Floor((realCostA - realCostB) / priceB);
                        if (additionalB > 0)
                        {
                            qtyB += additionalB;
                            realCostB = qtyB * priceB;
                            Debug.WriteLine($"[PairsTrading]   Увеличили B на {additionalB} до {qtyB}, стоимость: {realCostB:F2}");
                        }
                    }

                    totalCost = realCostA + realCostB;
                    Debug.WriteLine($"[PairsTrading]   После коррекции: A={realCostA:F2}, B={realCostB:F2}, общая={totalCost:F2}");
                }

                string orderDirA = directionA == PositionDirection.Long ? "Buy" : "Sell";
                string orderDirB = directionB == PositionDirection.Long ? "Buy" : "Sell";

                Debug.WriteLine($"[PairsTrading]   Отправка ордеров:");
                Debug.WriteLine($"[PairsTrading]     A: {orderDirA} {qtyA} {_instrumentATicker} (стоимость: {realCostA:F2})");
                Debug.WriteLine($"[PairsTrading]     B: {orderDirB} {qtyB} {_instrumentBTicker} (стоимость: {realCostB:F2})");

                var resultA = await _transactionsService.SendMarketOrderAsync(
                    _instrumentAUid, orderDirA, (int)qtyA, _instrumentATicker, true, false, null);
                var resultB = await _transactionsService.SendMarketOrderAsync(
                    _instrumentBUid, orderDirB, (int)qtyB, _instrumentBTicker, true, false, null);

                Debug.WriteLine($"[PairsTrading]   Результат A: {(resultA.IsSuccess ? "✅ УСПЕШНО" : $"❌ {resultA.ErrorMessage}")}");
                Debug.WriteLine($"[PairsTrading]   Результат B: {(resultB.IsSuccess ? "✅ УСПЕШНО" : $"❌ {resultB.ErrorMessage}")}");

                if (resultA.IsSuccess && resultB.IsSuccess)
                {
                    _pendingOrderA = resultA.Order;
                    _pendingOrderB = resultB.Order;
                    _tradeState = PairsTradeState.PendingEntry;
                    _positionOpenTime = DateTime.Now;

                    Debug.WriteLine($"[PairsTrading] ✅ ОРДЕРА ОТПРАВЛЕНЫ, ожидание исполнения");
                    Debug.WriteLine($"[PairsTrading]   Order A ID: {_pendingOrderA?.OrderId}");
                    Debug.WriteLine($"[PairsTrading]   Order B ID: {_pendingOrderB?.OrderId}");

                    OnEntryStatusChanged?.Invoke($"Вход: A {orderDirA} {qtyA}, B {orderDirB} {qtyB}");
                    _indicatorValues.LastAction = $"Вход: {directionA} A ({qtyA}), {directionB} B ({qtyB}). Z-Score={_indicatorValues.CurrentZScore:F2}";
                }
                else
                {
                    Debug.WriteLine($"[PairsTrading] ❌ ОШИБКА ВХОДА! Отмена ордеров...");
                    await CancelPendingOrdersAsync();
                    _entryPass = true;
                    _logger.LogError($"Entry failed: A={resultA.IsSuccess}, B={resultB.IsSuccess}");
                }
                Debug.WriteLine($"======================================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PairsTrading] ❌ ИСКЛЮЧЕНИЕ В ExecuteEntryAsync: {ex.Message}");
                Debug.WriteLine($"[PairsTrading]   StackTrace: {ex.StackTrace}");
                _logger.LogError(ex, "ExecuteEntryAsync error");
                _entryPass = true;
            }
        }

        private async Task ExecuteExitAsync(string reason)
        {
            if (!_exitPass)
            {
                //Debug.WriteLine($"[PairsTrading] ⚠️ Выход заблокирован (_exitPass=false)");
                return;
            }

            /*//Debug.WriteLine($"[PairsTrading] ========== ИСПОЛНЕНИЕ ВЫХОДА ==========");
            //Debug.WriteLine($"[PairsTrading]   Причина: {reason}");
            //Debug.WriteLine($"[PairsTrading]   Позиция A: {_positionA?.Quantity ?? 0} ({_positionA?.Direction})");
            //Debug.WriteLine($"[PairsTrading]   Позиция B: {_positionB?.Quantity ?? 0} ({_positionB?.Direction})");*/

            _exitPass = false;

            try
            {
                string exitDirA = _positionA.Quantity > 0 ? "Sell" : "Buy";
                string exitDirB = _positionB.Quantity > 0 ? "Sell" : "Buy";
                int qtyA = Math.Abs((int)_positionA.Quantity);
                int qtyB = Math.Abs((int)_positionB.Quantity);

                ////Debug.WriteLine($"[PairsTrading]   Выходные ордера:");
                ////Debug.WriteLine($"[PairsTrading]     A: {exitDirA} {qtyA} {_instrumentATicker}");
                ////Debug.WriteLine($"[PairsTrading]     B: {exitDirB} {qtyB} {_instrumentBTicker}");

                var resultA = await _transactionsService.SendMarketOrderAsync(
                    _instrumentAUid, exitDirA, qtyA, _instrumentATicker, false, true, reason);
                var resultB = await _transactionsService.SendMarketOrderAsync(
                    _instrumentBUid, exitDirB, qtyB, _instrumentBTicker, false, true, reason);

                ////Debug.WriteLine($"[PairsTrading]   Результат A: {(resultA.IsSuccess ? "✅ УСПЕШНО" : $"❌ {resultA.ErrorMessage}")}");
                ////Debug.WriteLine($"[PairsTrading]   Результат B: {(resultB.IsSuccess ? "✅ УСПЕШНО" : $"❌ {resultB.ErrorMessage}")}");

                if (resultA.IsSuccess && resultB.IsSuccess)
                {
                    _pendingOrderA = resultA.Order;
                    _pendingOrderB = resultB.Order;
                    _tradeState = PairsTradeState.PendingExit;

                    ////Debug.WriteLine($"[PairsTrading] ✅ ОРДЕРА НА ВЫХОД ОТПРАВЛЕНЫ, ожидание исполнения");

                    OnExitStatusChanged?.Invoke($"Выход: A {exitDirA}, B {exitDirB}. Причина: {reason}");
                    _indicatorValues.LastAction = $"Выход: {reason}";
                }
                else
                {
                    //Debug.WriteLine($"[PairsTrading] ❌ ОШИБКА ВЫХОДА! Отмена ордеров...");
                    await CancelPendingOrdersAsync();
                    _exitPass = true;
                    _logger.LogError($"Exit failed: A={resultA.IsSuccess}, B={resultB.IsSuccess}");
                }
               // //Debug.WriteLine($"======================================================");
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ ИСКЛЮЧЕНИЕ В ExecuteExitAsync: {ex.Message}");
                //Debug.WriteLine($"[PairsTrading]   StackTrace: {ex.StackTrace}");
                _logger.LogError(ex, "ExecuteExitAsync error");
                _exitPass = true;
            }
        }

        private async Task CancelPendingOrdersAsync()
        {
            //Debug.WriteLine($"[PairsTrading] Отмена pending ордеров...");
            try
            {
                if (_pendingOrderA != null)
                {
                    //Debug.WriteLine($"[PairsTrading]   Отмена Order A: {_pendingOrderA.OrderId}");
                    await _transactionsService.CancelOrderAsync(_pendingOrderA.OrderId ?? _pendingOrderA.Id);
                    _pendingOrderA = null;
                }
                if (_pendingOrderB != null)
                {
                    //Debug.WriteLine($"[PairsTrading]   Отмена Order B: {_pendingOrderB.OrderId}");
                    await _transactionsService.CancelOrderAsync(_pendingOrderB.OrderId ?? _pendingOrderB.Id);
                    _pendingOrderB = null;
                }
                //Debug.WriteLine($"[PairsTrading] ✅ Все ордера отменены");
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка отмены ордеров: {ex.Message}");
                _logger.LogError(ex, "CancelPendingOrdersAsync error");
            }
        }
        #endregion

        #region Управление позициями
        private async Task UpdatePositionsAsync()
        {
            // ✅ В бэктест-режиме пропускаем обновление позиций
            if (_isBacktestMode)
            {
                // В бэктесте позиции управляются через SetSimulatedPosition
                return;
            }


            try
            {
                var positions = await _provider.GetPositionsAsync();

                // Находим позиции для наших инструментов
                var posA = positions?.FirstOrDefault(p => p.InstrumentUid == _instrumentAUid);
                var posB = positions?.FirstOrDefault(p => p.InstrumentUid == _instrumentBUid);

                // Получаем количество лотов
                decimal quantityA = posA?.Quantity ?? 0;
                decimal quantityB = posB?.Quantity ?? 0;

                // Проверяем, изменилась ли позиция
                bool hadPosition = _positionA != null && _positionB != null;
                bool hasPosition = quantityA != 0 && quantityB != 0;

                // Сохраняем старые значения для сравнения
                var oldPosA = _positionA;
                var oldPosB = _positionB;

                _positionA = posA;
                _positionB = posB;

                // ✅ ИСПРАВЛЕНИЕ: Проверяем, что это именно наша позиция, а не чужая
                // Стратегия считается имеющей позицию только если есть позиции по ОБОИМ инструментам
                // И они противоположны по направлению (одна Long, другая Short)
                bool isValidPairPosition = false;

                if (hasPosition)
                {
                    // Проверяем, что направления противоположны
                    bool isA_Long = quantityA > 0;
                    bool isA_Short = quantityA < 0;
                    bool isB_Long = quantityB > 0;
                    bool isB_Short = quantityB < 0;

                    // Валидная пара: A Long + B Short ИЛИ A Short + B Long
                    isValidPairPosition = (isA_Long && isB_Short) || (isA_Short && isB_Long);
                }

                // Обновляем состояние
                if (isValidPairPosition)
                {
                    if (!hadPosition || _tradeState == PairsTradeState.NoPosition)
                    {
                        _tradeState = quantityA > 0 ? PairsTradeState.LongSpread : PairsTradeState.ShortSpread;
                        _positionOpenTime = DateTime.Now;
                        _entryPass = false;
                        _exitPass = true;

                        //Debug.WriteLine($"[PairsTrading] 📊 ПОЗИЦИЯ ОБНАРУЖЕНА: A={quantityA}, B={quantityB} ({_instrumentBTicker})");
                        OnEntryStatusChanged?.Invoke($"✅ Позиция активна: A={quantityA}, B={quantityB}");
                    }
                    else
                    {
                        // Позиция уже была - просто обновляем отображение
                        string dirA = quantityA > 0 ? "Лонг" : "Шорт";
                        string dirB = quantityB > 0 ? "Лонг" : "Шорт";
                        _indicatorValues.CurrentPosition = $"A: {dirA} {Math.Abs(quantityA)}, B: {dirB} {Math.Abs(quantityB)}";
                    }
                }
                else if (hadPosition && !isValidPairPosition)
                {
                    // Позиция закрылась
                    _tradeState = PairsTradeState.NoPosition;
                    _entryPass = true;
                    _exitPass = true;
                    _positionA = null;
                    _positionB = null;

                    //Debug.WriteLine($"[PairsTrading] 📊 ПОЗИЦИЯ ЗАКРЫТА ({_instrumentBTicker})");
                    OnExitStatusChanged?.Invoke("✅ Позиция закрыта");
                }
                else if (!hadPosition && !isValidPairPosition)
                {
                    // Нет позиции - все нормально
                    _indicatorValues.CurrentPosition = "Нет позиции";
                }

                // ✅ ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: Если есть старая позиция, но она не изменилась
                if (hadPosition && isValidPairPosition)
                {
                    // Проверяем, не изменилось ли количество
                    if (oldPosA != null && oldPosB != null)
                    {
                        if (oldPosA.Quantity != quantityA || oldPosB.Quantity != quantityB)
                        {
                            //Debug.WriteLine($"[PairsTrading] 📊 ПОЗИЦИЯ ИЗМЕНЕНА: A={oldPosA.Quantity}->{quantityA}, B={oldPosB.Quantity}->{quantityB} ({_instrumentBTicker})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка обновления позиций для {_instrumentBTicker}: {ex.Message}");
                _logger.LogError(ex, "UpdatePositionsAsync error for {Ticker}", _instrumentBTicker);
            }
        }

        private async Task CheckPendingOrdersAsync()
        {
            try
            {
                bool orderAChanged = false;
                bool orderBChanged = false;

                if (_pendingOrderA != null)
                {
                    var status = await _transactionsService.GetOrderStatusAsync(_pendingOrderA.OrderId ?? _pendingOrderA.Id);
                    if (status == OrderStatus.Filled)
                    {
                        //Debug.WriteLine($"[PairsTrading] ✅ Order A исполнен");
                        _pendingOrderA = null;
                        orderAChanged = true;
                    }
                    else if (status == OrderStatus.Cancelled || status == OrderStatus.Rejected)
                    {
                        //Debug.WriteLine($"[PairsTrading] ⚠️ Order A {status}");
                        _pendingOrderA = null;
                        orderAChanged = true;
                    }
                }

                if (_pendingOrderB != null)
                {
                    var status = await _transactionsService.GetOrderStatusAsync(_pendingOrderB.OrderId ?? _pendingOrderB.Id);
                    if (status == OrderStatus.Filled)
                    {
                        //Debug.WriteLine($"[PairsTrading] ✅ Order B исполнен");
                        _pendingOrderB = null;
                        orderBChanged = true;
                    }
                    else if (status == OrderStatus.Cancelled || status == OrderStatus.Rejected)
                    {
                        //Debug.WriteLine($"[PairsTrading] ⚠️ Order B {status}");
                        _pendingOrderB = null;
                        orderBChanged = true;
                    }
                }

                if (_pendingOrderA == null && _pendingOrderB == null && _tradeState == PairsTradeState.PendingEntry)
                {
                    //Debug.WriteLine($"[PairsTrading] Оба ордера входа исполнены или отменены");
                    await UpdatePositionsAsync();
                    if (_positionA != null && _positionB != null)
                    {
                        _tradeState = _positionA.Quantity > 0 ? PairsTradeState.LongSpread : PairsTradeState.ShortSpread;
                        //Debug.WriteLine($"[PairsTrading] ✅ ВХОД ВЫПОЛНЕН! Позиция: A={_positionA.Quantity}, B={_positionB.Quantity}");
                        OnEntryStatusChanged?.Invoke("✅ Вход выполнен");
                    }
                    else
                    {
                        //Debug.WriteLine($"[PairsTrading] ⚠️ Вход не выполнен - позиции отсутствуют");
                    }
                }

                if (_pendingOrderA == null && _pendingOrderB == null && _tradeState == PairsTradeState.PendingExit)
                {
                    //Debug.WriteLine($"[PairsTrading] Оба ордера выхода исполнены или отменены");
                    await UpdatePositionsAsync();
                    if (_positionA == null && _positionB == null)
                    {
                        _tradeState = PairsTradeState.NoPosition;
                        _entryPass = _exitPass = true;
                        //Debug.WriteLine($"[PairsTrading] ✅ ВЫХОД ВЫПОЛНЕН!");
                        OnExitStatusChanged?.Invoke("✅ Выход выполнен");
                    }
                    else
                    {
                        //Debug.WriteLine($"[PairsTrading] ⚠️ Выход не выполнен - позиции сохранились");
                    }
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка проверки ордеров: {ex.Message}");
                _logger.LogError(ex, "CheckPendingOrdersAsync error");
            }
        }
        #endregion

        #region Жизненный цикл стратегии
        // Обновленный метод StartAsync
        public async Task StartAsync()
        {
            //Debug.WriteLine($"[PairsTrading] ========== ЗАПУСК СТРАТЕГИИ ==========");
            //Debug.WriteLine($"[PairsTrading] Время: {DateTime.Now:HH:mm:ss.fff}");

            State = StrategyState.Running;
            _indicatorValues.StrategyStatus = "РАБОТАЕТ";
            OnStrategyStatusChanged?.Invoke("РАБОТАЕТ");

            // ✅ Обновляем подписи инструментов
            UpdateControlPanelLabels();

            // ✅ В БЭКТЕСТ-РЕЖИМЕ НЕ СТРОИМ МОДЕЛЬ ЗАНОВО
            if (!_isBacktestMode)
            {
                await BuildModelAsync();
                await UpdatePositionsAsync();
            }
            else
            {
                //Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: модель уже построена, пропускаем BuildModelAsync");
            }

            // ✅ В БЭКТЕСТ-РЕЖИМЕ НЕ ПОДПИСЫВАЕМСЯ!
            if (!_isBacktestMode)
            {
                // Подписка на обновления цен
                await SubscribeToPriceUpdatesAsync();

                // Принудительное получение начальных цен
                await ForceUpdatePricesAsync();

                // Диагностика цен
                await ForceUpdatePricesForDiagnosticAsync();

                // Запуск таймера для перестроения модели
                StartModelRebuildTimer();
            }
            else
            {
                //Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: подписки отключены");
                // В бэктест-режиме устанавливаем начальные цены из переданных свечей
                if (_backtestCandlesA != null && _backtestCandlesB != null && _backtestCandlesA.Any())
                {
                    _lastPriceA = _backtestCandlesA.Last().Close;
                    _lastPriceB = _backtestCandlesB.Last().Close;
                    _indicatorValues.PriceA = _lastPriceA;
                    _indicatorValues.PriceB = _lastPriceB;
                    //Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: начальные цены A={_lastPriceA:F2}, B={_lastPriceB:F2}");
                }
            }

            _indicatorValues.Status = _parameters.ModelValid ?
                $"✅ Модель готова. β={_parameters.HedgeRatio:F4}" :
                "❌ Модель невалидна";

            _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));

            //Debug.WriteLine($"[PairsTrading] ✅ СТРАТЕГИЯ ЗАПУЩЕНА");
            //Debug.WriteLine($"[PairsTrading] Текущие цены: A={_lastPriceA:F2}, B={_lastPriceB:F2}");
            //Debug.WriteLine($"======================================================");
            _logger.LogInformation("PairsTrading strategy started");
        }

        // Обновленный метод StopAsync
        public async Task StopAsync()
        {
            //Debug.WriteLine($"[PairsTrading] ========== ОСТАНОВКА СТРАТЕГИИ ==========");
            //Debug.WriteLine($"[PairsTrading] Время: {DateTime.Now:HH:mm:ss.fff}");

            State = StrategyState.Stopped;
            _indicatorValues.StrategyStatus = "ОСТАНОВЛЕНА";
            OnStrategyStatusChanged?.Invoke("ОСТАНОВЛЕНА");

            // ✅ В БЭКТЕСТ-РЕЖИМЕ НЕ ОТПИСЫВАЕМСЯ
            if (!_isBacktestMode)
            {
                // Остановка таймера
                StopModelRebuildTimer();

                // Отписка от обновлений цен
                try
                {
                    if (!string.IsNullOrEmpty(_instrumentAUid))
                    {
                        await _provider.UnsubscribeFromMarketDataAsync(_instrumentAUid);
                    }
                    if (!string.IsNullOrEmpty(_instrumentBUid))
                    {
                        await _provider.UnsubscribeFromMarketDataAsync(_instrumentBUid);
                    }
                }
                catch (Exception ex)
                {
                    //Debug.WriteLine($"[PairsTrading] Ошибка отписки от цен: {ex.Message}");
                }
            }
            else
            {
                //Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: отписка отключена");
            }

            await CancelPendingOrdersAsync();

            //Debug.WriteLine($"[PairsTrading] ✅ СТРАТЕГИЯ ОСТАНОВЛЕНА");
            //Debug.WriteLine($"======================================================");
            _logger.LogInformation("PairsTrading strategy stopped");
        }

        // Обновленный метод DisposeAsync
        public async ValueTask DisposeAsync()
        {
            //Debug.WriteLine($"[PairsTrading] Освобождение ресурсов...");

            StopModelRebuildTimer();
            _updateLock?.Dispose();

            _parameters.OnParametersChanged -= OnParametersChanged;
            if (State == StrategyState.Running) await StopAsync();

            //Debug.WriteLine($"[PairsTrading] Ресурсы освобождены");
        }
        #endregion

        #region Обработчики событий
        private void OnParametersChanged(PairsTradingParameters parameters)
        {
            //Debug.WriteLine($"[PairsTrading] 🔄 ПАРАМЕТРЫ ИЗМЕНЕНЫ:");
            //Debug.WriteLine($"[PairsTrading]   Первый инструмент: {parameters.FirstInstrumentTicker}");
            //Debug.WriteLine($"[PairsTrading]   Парный инструмент: {parameters.PairInstrumentTicker}");
            //Debug.WriteLine($"[PairsTrading]   Период обучения: {parameters.LookbackPeriod} (в единицах таймфрейма)");
            //Debug.WriteLine($"[PairsTrading]   Таймфрейм: {_timeframe}");
            //Debug.WriteLine($"[PairsTrading]   Порог входа: ±{parameters.EntryZScore:F2}");
            //Debug.WriteLine($"[PairsTrading]   Порог выхода: {parameters.ExitZScore:F2}");
            //Debug.WriteLine($"[PairsTrading]   Стоп-лосс: {parameters.StopLossZScore:F2}");
            //Debug.WriteLine($"[PairsTrading]   Размер позиции: {parameters.PositionSizePercent}%");

            _logger.LogInformation("PairsTrading parameters updated");
            _ = Task.Run(async () =>
            {
                try
                {
                    // ✅ Обновляем тикер B из параметров
                    _instrumentBTicker = parameters.PairInstrumentTicker;
                    _instrumentBUid = parameters.PairInstrumentUid;

                    // ✅ Обновляем тикер A из параметров (НЕ ПЕРЕЗАПИСЫВАЕМ!)
                    _instrumentATicker = parameters.FirstInstrumentTicker;
                    _instrumentAUid = parameters.FirstInstrumentUid;

                    // ✅ Обновляем подписи в UI
                    UpdateControlPanelLabels();

                    // Загружаем инструменты и свечи
                    await LoadPairInstrumentAsync();

                    // ✅ Проверяем, нужно ли перестраивать модель
                    var candlesReady = await CheckCandlesForPairAsync();
                    if (candlesReady)
                    {
                        await BuildModelAsync();
                        await ForceUpdatePricesAsync();
                    }
                    else
                    {
                        //Debug.WriteLine($"[PairsTrading] ⚠️ Не удалось загрузить свечи для построения модели");
                    }
                }
                catch (Exception ex)
                {
                    //Debug.WriteLine($"[PairsTrading] ❌ Ошибка обновления параметров: {ex.Message}");
                }
            });
        }

        // Обработчик обновления цены для инструмента A
        private void OnPriceUpdateA(MarketData data)
        {
            try
            {
                if (data.LastPrice > 0)
                {
                    _lastPriceA = data.LastPrice;
                    _lastPriceUpdateA = DateTime.Now;
                    _indicatorValues.PriceA = _lastPriceA;
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceA));

                    // ✅ Обновляем спред и Z-Score только если модель валидна
                    if (_parameters.ModelValid)
                    {
                        UpdateSpreadAndZScore();
                    }

                    // ✅ Отладочный вывод - показываем что цена обновляется
                    if (_debugCounter % 50 == 0)
                    {
                        Debug.WriteLine($"[PairsTrading] ✅ (---=== {_instrumentBTicker} ===---) Цена A обновлена для {_instrumentBTicker}: {_lastPriceA:F2}");
                    }
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка обновления цены A для {_instrumentBTicker}: {ex.Message}");
            }
        }

        // Обработчик обновления цены для инструмента B
        private void OnPriceUpdateB(MarketData data)
        {
            try
            {
                Debug.WriteLine($"[PairsTrading] OnPriceUpdateB вызван: LastPrice={data.LastPrice}, InstrumentId={data.InstrumentUid}");

                if (data.LastPrice > 0)
                {
                    _lastPriceB = data.LastPrice;
                    _indicatorValues.PriceB = _lastPriceB;
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceB));

                    Debug.WriteLine($"[PairsTrading] ✅ (---=== {_instrumentBTicker} ===---) Цена B ({_instrumentBTicker}) обновлена: {_lastPriceB:F2} (через подписку)");

                    // Обновляем спред и Z-Score
                    UpdateSpreadAndZScore();
                }
                else
                {
                    //Debug.WriteLine($"[PairsTrading] ⚠️ Цена B: LastPrice <= 0 ({data.LastPrice})");
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка обновления цены B: {ex.Message}");
            }
        }
        
        // обработчик свечей для инструмента A
        private void OnCandleUpdateA(CandleUpdate candleUpdate)
        {
            try
            {
                if (candleUpdate.LastPrice > 0)
                {
                    _lastPriceA = candleUpdate.LastPrice;
                    _indicatorValues.PriceA = _lastPriceA;
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceA));

                    Debug.WriteLine($"[PairsTrading] ✅ (---=== {_instrumentBTicker} ===---) Цена A обновлена из свечи {_instrumentATicker}: {_lastPriceA:F2}");

                    // Обновляем спред и Z-Score
                    UpdateSpreadAndZScore();
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка обновления цены A из свечи: {ex.Message}");
            }
        }

        /// <summary>
        /// Диагностика подписок для текущей стратегии
        /// </summary>
        public void DiagnoseSubscriptions()
        {
            try
            {
                //Debug.WriteLine($"[PairsTrading] ===== ДИАГНОСТИКА ПОДПИСОК ДЛЯ {_instrumentBTicker} =====");

                // Получаем счетчики через рефлексию из TinkoffApiService
                var tinkoffService = _provider as TinkoffApiService;
                if (tinkoffService != null)
                {
                    try
                    {
                        // ✅ ИСПРАВЛЕНИЕ: Используем правильное имя поля
                        var field = typeof(TinkoffApiService).GetField("_marketDataSubscriptionRefCount",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                        if (field != null)
                        {
                            var dict = field.GetValue(tinkoffService) as Dictionary<string, int>;
                            if (dict != null)
                            {
                                //Debug.WriteLine($"[PairsTrading] Счетчики подписок:");
                                foreach (var kvp in dict)
                                {
                                    //Debug.WriteLine($"[PairsTrading]   {kvp.Key}: {kvp.Value} подписчиков");
                                }
                            }
                        }
                        else
                        {
                            //Debug.WriteLine($"[PairsTrading] ⚠️ Поле _marketDataSubscriptionRefCount не найдено");
                        }
                    }
                    catch (Exception ex)
                    {
                        //Debug.WriteLine($"[PairsTrading] ⚠️ Ошибка получения счетчиков: {ex.Message}");
                    }
                }
                else
                {
                    //Debug.WriteLine($"[PairsTrading] ⚠️ Провайдер не является TinkoffApiService");
                }

                //Debug.WriteLine($"[PairsTrading] Текущая стратегия:");
                Debug.WriteLine($"[PairsTrading]   A (IMOEXF): {_instrumentAUid}, цена={_lastPriceA:F2}, обновлена={_lastPriceUpdateA:HH:mm:ss}");
                Debug.WriteLine($"[PairsTrading]   B ({_instrumentBTicker}): {_instrumentBUid}, цена={_lastPriceB:F2}, обновлена={_lastPriceUpdateB:HH:mm:ss}");
                //Debug.WriteLine($"[PairsTrading]   Модель валидна: {_parameters.ModelValid}");
                //Debug.WriteLine($"[PairsTrading]   Подписаны на цены: {_isSubscribedToPrices}");
                //Debug.WriteLine($"[PairsTrading]   Попыток построения модели: {_modelBuildAttempts}/{MAX_MODEL_BUILD_ATTEMPTS}");
                //Debug.WriteLine($"[PairsTrading] ======================================================");
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] Ошибка диагностики: {ex.Message}");
            }
        }

        #endregion

        #region UI Views (Settings & Control Panels)
        public object GetSettingsView() => CreateSettingsPanel();
        public object GetControlView() => CreateControlPanel();

        private StackPanel CreateSettingsPanel()
        {
            var panel = new StackPanel();

            // ✅ ДОБАВЛЯЕМ: Информационное сообщение
            var infoGroup = CreateGroup("Информация");
            var infoPanel = new StackPanel();
            infoPanel.Children.Add(new TextBlock
            {
                Text = "Первый инструмент (A) всегда IMOEXF (индекс МосБиржи).",
                FontSize = 11,
                Foreground = Brushes.DarkBlue,
                Margin = new Thickness(0, 5, 0, 5)
            });
            infoPanel.Children.Add(new TextBlock
            {
                Text = "Второй инструмент (B) выбирается при открытии стратегии.",
                FontSize = 11,
                Foreground = Brushes.DarkBlue,
                Margin = new Thickness(0, 0, 0, 5)
            });
            infoGroup.Content = infoPanel;
            panel.Children.Add(infoGroup);

            // Параметры пары
            var pairGroup = CreateGroup("Пара инструментов");
            var pairGrid = new Grid();
            pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ✅ ОБА ПОЛЯ РЕДАКТИРУЕМЫЕ
            AddRow(pairGrid, "Первый инструмент (A):", nameof(PairsTradingParameters.FirstInstrumentTicker), 0);
            AddRow(pairGrid, "Парный инструмент (B):", nameof(PairsTradingParameters.PairInstrumentTicker), 1);
            AddRow(pairGrid, "Период обучения (часов):", nameof(PairsTradingParameters.LookbackPeriod), 2);
            pairGroup.Content = pairGrid;
            panel.Children.Add(pairGroup);

            // Параметры торговли
            var tradeGroup = CreateGroup("Параметры торговли");
            var tradeGrid = new Grid();
            tradeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tradeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddRow(tradeGrid, "Порог входа (Z-Score):", nameof(PairsTradingParameters.EntryZScore), 0);
            AddRow(tradeGrid, "Порог выхода (Z-Score):", nameof(PairsTradingParameters.ExitZScore), 1);
            AddRow(tradeGrid, "Стоп-лосс (Z-Score):", nameof(PairsTradingParameters.StopLossZScore), 2);
            AddRow(tradeGrid, "Размер позиции (%):", nameof(PairsTradingParameters.PositionSizePercent), 3);
            tradeGroup.Content = tradeGrid;
            panel.Children.Add(tradeGroup);

            // Кнопки
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var applyBtn = new Button { Content = "Применить", Width = 100, Height = 25, Margin = new Thickness(5) };
            applyBtn.Click += async (s, e) =>
            {
                _parameters.ApplyParameters();

                // ✅ При применении параметров проверяем и загружаем свечи
                await BuildModelAsync();
            };

            var resetBtn = new Button { Content = "Сброс", Width = 100, Height = 25, Margin = new Thickness(5) };
            resetBtn.Click += (s, e) => _parameters.ResetParameters();

            var buildBtn = new Button { Content = "Построить модель", Width = 120, Height = 25, Margin = new Thickness(5) };
            buildBtn.Click += async (s, e) => await BuildModelAsync();

            btnPanel.Children.Add(applyBtn);
            btnPanel.Children.Add(resetBtn);
            btnPanel.Children.Add(buildBtn);
            panel.Children.Add(btnPanel);

            return panel;
        }

        private StackPanel CreateControlPanel()
        {
            var panel = new StackPanel();

            // Текущие значения
            var valuesGroup = CreateGroup("Текущие значения");
            var valuesGrid = new Grid();
            valuesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            valuesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ✅ ИСПРАВЛЕНИЕ: Используем метод для получения актуальных тикеров
            string tickerA = GetCurrentTickerA();
            string tickerB = GetCurrentTickerB();

            string labelA = $"Цена A ({tickerA}):";
            string labelB = $"Цена B ({tickerB}):";

            // Сохраняем ссылки на элементы для возможного обновления
            // ✅ ИСПРАВЛЕНИЕ: Правильные индексы строк - каждый элемент на своей строке
            _priceALabel = AddIndicatorRow(valuesGrid, labelA, nameof(PairsIndicatorValues.PriceA), "{0:F2}", 0);
            _priceBLabel = AddIndicatorRow(valuesGrid, labelB, nameof(PairsIndicatorValues.PriceB), "{0:F2}", 1);
            AddIndicatorRow(valuesGrid, "Текущий спред:", nameof(PairsIndicatorValues.CurrentSpread), "{0:F4}", 2);
            AddIndicatorRow(valuesGrid, "Z-Score:", nameof(PairsIndicatorValues.CurrentZScore), "{0:F2}", 3);  // ✅ ДОБАВЛЯЕМ Z-Score
            _hedgeRatioLabel = AddIndicatorRow(valuesGrid, "Коэф. хеджа (β):", nameof(PairsIndicatorValues.HedgeRatio), "{0:F4}", 4);
            _spreadMeanLabel = AddIndicatorRow(valuesGrid, "Среднее спреда:", nameof(PairsIndicatorValues.SpreadMean), "{0:F4}", 5);
            _spreadStdLabel = AddIndicatorRow(valuesGrid, "Std спреда:", nameof(PairsIndicatorValues.SpreadStd), "{0:F4}", 6);
            valuesGroup.Content = valuesGrid;
            panel.Children.Add(valuesGroup);

            // Статус
            var statusGroup = CreateGroup("Статус");
            var statusPanel = new StackPanel();
            _signalLabel = CreateTextBlock(nameof(PairsIndicatorValues.Signal), FontWeights.Bold, 14);
            _signalDescriptionLabel = CreateTextBlock(nameof(PairsIndicatorValues.SignalDescription), FontWeights.Normal, 12);
            _currentPositionLabel = CreateTextBlock(nameof(PairsIndicatorValues.CurrentPosition), FontWeights.Bold, 12);
            _lastActionLabel = CreateTextBlock(nameof(PairsIndicatorValues.LastAction), FontWeights.Normal, 11, true);
            _statusLabel = CreateTextBlock(nameof(PairsIndicatorValues.Status), FontWeights.Normal, 11);

            statusPanel.Children.Add(_signalLabel);
            statusPanel.Children.Add(_signalDescriptionLabel);
            statusPanel.Children.Add(_currentPositionLabel);
            statusPanel.Children.Add(_lastActionLabel);
            statusPanel.Children.Add(_statusLabel);
            statusGroup.Content = statusPanel;
            panel.Children.Add(statusGroup);

            // Статус стратегии
            var strategyGroup = CreateGroup("Статус стратегии");
            var strategyPanel = new StackPanel();
            _strategyStatusLabel = CreateTextBlock(nameof(PairsIndicatorValues.StrategyStatus), FontWeights.Bold, 12);

            // ✅ ИСПРАВЛЕНИЕ: Используем свойство из IndicatorValues для отображения статуса модели
            var modelStatusBlock = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 5)
            };
            modelStatusBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PairsIndicatorValues.Status))
            {
                Source = _indicatorValues
            });

            strategyPanel.Children.Add(_strategyStatusLabel);
            strategyPanel.Children.Add(modelStatusBlock);

            strategyPanel.Children.Add(new TextBlock
            {
                Text = $"Обновлено: {DateTime.Now:HH:mm:ss}",
                FontSize = 10,
                Foreground = Brushes.Gray
            });
            strategyGroup.Content = strategyPanel;
            panel.Children.Add(strategyGroup);

            return panel;
        }

        /// <summary>
        /// Возвращает актуальный тикер инструмента A
        /// </summary>
        private string GetCurrentTickerA()
        {
            // Сначала проверяем параметры (UI)
            if (!string.IsNullOrEmpty(_parameters.FirstInstrumentTicker))
                return _parameters.FirstInstrumentTicker;
            // Затем внутренние поля
            if (!string.IsNullOrEmpty(_instrumentATicker))
                return _instrumentATicker;
            // Значение по умолчанию
            return "IMOEXF";
        }

        /// <summary>
        /// Возвращает актуальный тикер инструмента B
        /// </summary>
        private string GetCurrentTickerB()
        {
            // Сначала проверяем параметры (UI)
            if (!string.IsNullOrEmpty(_parameters.PairInstrumentTicker))
                return _parameters.PairInstrumentTicker;
            // Затем внутренние поля
            if (!string.IsNullOrEmpty(_instrumentBTicker))
                return _instrumentBTicker;
            // Значение по умолчанию
            return "SBER";
        }

       







        // Вспомогательные методы для UI
        private GroupBox CreateGroup(string header)
        {
            return new GroupBox
            {
                Header = header,
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(5),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1)
            };
        }

        private void AddRow(Grid grid, string label, string property, int row)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 5, 10, 5),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            Grid.SetColumn(labelBlock, 0);
            Grid.SetRow(labelBlock, row);
            grid.Children.Add(labelBlock);

            var tb = new TextBox
            {
                Margin = new Thickness(0, 5, 0, 5),
                Width = 80
            };
            Grid.SetColumn(tb, 1);
            Grid.SetRow(tb, row);
            tb.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(property)
            {
                Source = _parameters,
                Mode = System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            });
            grid.Children.Add(tb);
        }

        private TextBlock AddIndicatorRow(Grid grid, string label, string property, string format, int row)
        {
            // ✅ Убеждаемся, что строк достаточно
            while (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var labelBlock = new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 10, 5)
            };
            Grid.SetColumn(labelBlock, 0);
            Grid.SetRow(labelBlock, row);
            grid.Children.Add(labelBlock);

            var tb = new TextBlock
            {
                Margin = new Thickness(0, 5, 0, 5)
            };
            Grid.SetColumn(tb, 1);
            Grid.SetRow(tb, row);
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(property)
            {
                Source = _indicatorValues,
                StringFormat = format
            });
            grid.Children.Add(tb);

            return labelBlock;
        }

        private TextBlock CreateTextBlock(string property, FontWeight weight, double size, bool isItalic = false)
        {
            var tb = new TextBlock
            {
                FontSize = size,
                FontWeight = weight,
                Margin = new Thickness(0, 5, 0, 5),
                TextWrapping = TextWrapping.Wrap
            };
            if (isItalic) tb.FontStyle = FontStyles.Italic;
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(property) { Source = _indicatorValues });
            return tb;
        }

        /// <summary>
        /// Обновляет лейблы в Control Panel при изменении инструментов
        /// </summary>
        public void UpdateControlPanelLabels()
        {
            try
            {
                if (_indicatorValues == null) return;

                // Получаем актуальные тикеры
                string tickerA = GetCurrentTickerA();
                string tickerB = GetCurrentTickerB();

                // Обновляем лейблы если они существуют
                if (_priceALabel != null)
                {
                    _priceALabel.Text = $"Цена A ({tickerA}):";
                }
                if (_priceBLabel != null)
                {
                    _priceBLabel.Text = $"Цена B ({tickerB}):";
                }

                // Обновляем статус
                _indicatorValues.Status = _parameters.ModelValid ?
                    $"✅ Модель готова. Пара: {tickerA}/{tickerB}, β={_parameters.HedgeRatio:F4}" :
                    $"❌ Модель невалидна. Пара: {tickerA}/{tickerB}";

                _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));

                // Обновляем другие показатели
                _indicatorValues.RefreshUI(nameof(_indicatorValues.HedgeRatio));
                _indicatorValues.RefreshUI(nameof(_indicatorValues.SpreadMean));
                _indicatorValues.RefreshUI(nameof(_indicatorValues.SpreadStd));

                //Debug.WriteLine($"[PairsTrading] Обновлены подписи: A={tickerA}, B={tickerB}");
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] Ошибка обновления подписей: {ex.Message}");
            } 
        }

        #endregion

            #region Подписки
            // Добавьте метод для подписки на обновления цен обоих инструментов
        private async Task SubscribeToPriceUpdatesAsync()
        {
            // ✅ В БЭКТЕСТ-РЕЖИМЕ НИЧЕГО НЕ ДЕЛАЕМ
            if (_isBacktestMode)
            {
                //Debug.WriteLine($"[PairsTrading] БЭКТЕСТ-РЕЖИМ: подписка на цены пропущена");
                return;
            }

            if (_isSubscribedToPrices)
            {
                //Debug.WriteLine($"[PairsTrading] Уже подписаны на обновления цен для {_instrumentBTicker}");
                return;
            }

            try
            {
                //Debug.WriteLine($"[PairsTrading] Подписка на обновления цен для {_instrumentBTicker}...");
                //Debug.WriteLine($"[PairsTrading]   A UID: {_instrumentAUid}, Ticker: {_instrumentATicker}");
                //Debug.WriteLine($"[PairsTrading]   B UID: {_instrumentBUid}, Ticker: {_instrumentBTicker}");

                await _subscriptionLock.WaitAsync();

                try
                {
                    // ✅ Подписываемся на обновления последних цен для инструмента A
                    if (!string.IsNullOrEmpty(_instrumentAUid))
                    {
                        try
                        {
                            await _provider.SubscribeToMarketDataAsync(_instrumentAUid, OnPriceUpdateA);
                            //Debug.WriteLine($"[PairsTrading] ✅ Подписка на A: {_instrumentAUid} ({_instrumentATicker}) для {_instrumentBTicker}");
                        }
                        catch (Exception ex)
                        {
                            //Debug.WriteLine($"[PairsTrading] ❌ Ошибка подписки на A для {_instrumentBTicker}: {ex.Message}");
                        }
                    }

                    // ✅ Подписываемся на обновления последних цен для инструмента B
                    if (!string.IsNullOrEmpty(_instrumentBUid))
                    {
                        try
                        {
                            await _provider.SubscribeToMarketDataAsync(_instrumentBUid, OnPriceUpdateB);
                            //Debug.WriteLine($"[PairsTrading] ✅ Подписка на B: {_instrumentBUid} ({_instrumentBTicker}) для {_instrumentBTicker}");
                        }
                        catch (Exception ex)
                        {
                            //Debug.WriteLine($"[PairsTrading] ❌ Ошибка подписки на B для {_instrumentBTicker}: {ex.Message}");
                        }
                    }

                    // ✅ Подписка на свечи для A (для получения цен через свечи)
                    if (!string.IsNullOrEmpty(_instrumentAUid))
                    {
                        try
                        {
                            await _provider.SubscribeToCandlesAsync(_instrumentAUid, _timeframe, OnCandleUpdateA);
                            //Debug.WriteLine($"[PairsTrading] ✅ Подписка на свечи A: {_instrumentAUid} для {_instrumentBTicker}");
                        }
                        catch (Exception ex)
                        {
                            //Debug.WriteLine($"[PairsTrading] ❌ Ошибка подписки на свечи A для {_instrumentBTicker}: {ex.Message}");
                        }
                    }

                    _isSubscribedToPrices = true;
                }
                finally
                {
                    _subscriptionLock.Release();
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] Ошибка подписки на цены для {_instrumentBTicker}: {ex.Message}");
                _logger.LogError(ex, "Error subscribing to price updates for {Ticker}", _instrumentBTicker);
            }
        }

        // Обновление спреда и Z-Score
        private void UpdateSpreadAndZScore()
        {
            try
            {
                if (!_parameters.ModelValid || _lastPriceA <= 0 || _lastPriceB <= 0)
                    return;

                decimal spread = _lastPriceA - _parameters.HedgeRatio * _lastPriceB;
                _indicatorValues.CurrentSpread = spread;
                _indicatorValues.RefreshUI(nameof(_indicatorValues.CurrentSpread));

                if (_parameters.SpreadStd > 0)
                {
                    decimal zScore = (spread - _parameters.SpreadMean) / _parameters.SpreadStd;
                    _indicatorValues.CurrentZScore = zScore;
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.CurrentZScore));

                    // Обновляем статус без частых изменений
                    _indicatorValues.Status = $"✅ Модель активна. Z-Score={zScore:F2}, β={_parameters.HedgeRatio:F4}";
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] Ошибка обновления спреда: {ex.Message}");
            }
        }



        #endregion

        // Запуск таймера для перестроения модели
        private void StartModelRebuildTimer()
        {
            _updateTimer?.Dispose();
            // ✅ Увеличиваем интервал до 5 минут (300000 мс)
            const int MODEL_REBUILD_INTERVAL_MS = 300000; // 5 минут
            _updateTimer = new Timer(async _ =>
            {
                await RebuildModelIfNeededAsync();
            }, null, MODEL_REBUILD_INTERVAL_MS, MODEL_REBUILD_INTERVAL_MS);

            //Debug.WriteLine($"[PairsTrading] Таймер перестроения модели запущен (интервал: {MODEL_REBUILD_INTERVAL_MS / 1000}с)");
        }


        // Перестроение модели при необходимости
        private async Task RebuildModelIfNeededAsync()
        {
            // Проверяем, не выполняется ли уже построение
            if (_isModelBuilding)
                return;

            if (!_updateLock.Wait(0))
                return;

            try
            {
                // Перестраиваем модель только если прошло достаточно времени
                // и модель невалидна или устарела
                bool needRebuild = false;

                if (!_parameters.ModelValid)
                {
                    needRebuild = true;
                    //Debug.WriteLine($"[PairsTrading] 🔄 Модель невалидна, требуется перестроение");
                }
                else if ((DateTime.Now - _parameters.ModelLastUpdate).TotalHours > 6)
                {
                    needRebuild = true;
                    //Debug.WriteLine($"[PairsTrading] 🔄 Модель устарела ({(DateTime.Now - _parameters.ModelLastUpdate).TotalHours:F1}ч), требуется перестроение");
                }

                if (needRebuild)
                {
                    await BuildModelAsync();
                }
                else if (_parameters.ModelValid && _lastPriceA > 0 && _lastPriceB > 0)
                {
                    // Просто обновляем статус
                    _indicatorValues.Status = $"✅ Модель активна. Z-Score={_indicatorValues.CurrentZScore:F2}, β={_parameters.HedgeRatio:F4}";
                    _indicatorValues.RefreshUI(nameof(_indicatorValues.Status));
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка перестроения модели: {ex.Message}");
            }
            finally
            {
                _updateLock.Release();
            }
        }

        // Остановка таймера
        private void StopModelRebuildTimer()
        {
            _updateTimer?.Dispose();
            _updateTimer = null;
        }

        /// <summary>
        /// Принудительное обновление цен при запуске
        /// </summary>
        private async Task ForceUpdatePricesAsync()
        {
            if (_isBacktestMode)
            {
                // В бэктесте позиции управляются через SetSimulatedPosition
                return;
            }

            try
            {
                if (!string.IsNullOrEmpty(_instrumentAUid))
                {
                    var price = await _provider.GetCurrentPriceAsync(_instrumentAUid);
                    if (price > 0)
                    {
                        _lastPriceA = price;
                        _indicatorValues.PriceA = _lastPriceA;
                        _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceA));
                        //Debug.WriteLine($"[PairsTrading] Начальная цена A: {_lastPriceA:F2}");
                    }
                }

                if (!string.IsNullOrEmpty(_instrumentBUid))
                {
                    var price = await _provider.GetCurrentPriceAsync(_instrumentBUid);
                    if (price > 0)
                    {
                        _lastPriceB = price;
                        _indicatorValues.PriceB = _lastPriceB;
                        _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceB));
                        //Debug.WriteLine($"[PairsTrading] Начальная цена B: {_lastPriceB:F2}");
                    }
                }

                // Обновляем спред и Z-Score
                UpdateSpreadAndZScore();
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] Ошибка принудительного обновления цен: {ex.Message}");
            }
        }

        /// <summary>
        /// Принудительное обновление цен (для диагностики)
        /// </summary>
        public async Task ForceUpdatePricesForDiagnosticAsync()
        {
            if (_isBacktestMode)
            {
                // В бэктесте позиции управляются через SetSimulatedPosition
                return;
            }

            try
            {
                //Debug.WriteLine($"[PairsTrading] ===== ДИАГНОСТИКА ЦЕН ===== ");
                //Debug.WriteLine($"[PairsTrading] A: {_instrumentATicker} (UID: {_instrumentAUid})");
                //Debug.WriteLine($"[PairsTrading] B: {_instrumentBTicker} (UID: {_instrumentBUid})");

                if (!string.IsNullOrEmpty(_instrumentAUid))
                {
                    var priceA = await _provider.GetCurrentPriceAsync(_instrumentAUid);
                    //Debug.WriteLine($"[PairsTrading] Текущая цена A (принудительно): {priceA:F2}");
                    if (priceA > 0)
                    {
                        _lastPriceA = priceA;
                        _indicatorValues.PriceA = _lastPriceA;
                        _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceA));
                    }
                }

                if (!string.IsNullOrEmpty(_instrumentBUid))
                {
                    var priceB = await _provider.GetCurrentPriceAsync(_instrumentBUid);
                    //Debug.WriteLine($"[PairsTrading] Текущая цена B (принудительно): {priceB:F2}");
                    if (priceB > 0)
                    {
                        _lastPriceB = priceB;
                        _indicatorValues.PriceB = _lastPriceB;
                        _indicatorValues.RefreshUI(nameof(_indicatorValues.PriceB));
                    }
                }

                //Debug.WriteLine($"[PairsTrading] ===== КОНЕЦ ДИАГНОСТИКИ =====");
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] Ошибка диагностики цен: {ex.Message}");
            }
        }

        /// <summary>
        /// Принудительное обновление инструмента A (для использования из UI)
        /// </summary>
        public async Task UpdateInstrumentAAsync(string ticker)
        {
            if (string.IsNullOrEmpty(ticker))
                return;

            //Debug.WriteLine($"[PairsTrading] Обновление инструмента A на {ticker}");

            try
            {
                var allInstruments = await _provider.GetInstrumentsAsync();
                var instrument = allInstruments?.FirstOrDefault(i => i.Ticker == ticker);

                if (instrument != null)
                {
                    _instrumentATicker = ticker;
                    _instrumentAUid = instrument.Uid;
                    _parameters.FirstInstrumentTicker = ticker;
                    _parameters.FirstInstrumentUid = instrument.Uid;

                    // Обновляем UI
                    UpdateControlPanelLabels();

                    // Перестраиваем модель с новым инструментом
                    await BuildModelAsync();

                    //Debug.WriteLine($"[PairsTrading] ✅ Инструмент A обновлен на {ticker}");
                }
                else
                {
                    //Debug.WriteLine($"[PairsTrading] ❌ Инструмент {ticker} не найден");
                }
            }
            catch (Exception ex)
            {
                //Debug.WriteLine($"[PairsTrading] ❌ Ошибка обновления инструмента A: {ex.Message}");
            }
        }
    }
}