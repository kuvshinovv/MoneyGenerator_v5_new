using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Api;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Strategies;
using MoneyGenerator_v5.Views;
using System;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tinkoff.InvestApi.V1;

namespace MoneyGenerator_v5.ViewModels
{
    public partial class StrategyViewModel : ObservableObject
    {
        #region Поля
        private readonly TradingStrategy _strategy;
        private readonly Models.Instrument _instrument;
        private readonly TimeFrame _timeFrame;
        private readonly IProvirerService _providerService;
        private readonly ILogger<StrategyViewModel> _logger;
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly string _dbPath;
        private readonly Dictionary<string, bool> _tableInitialized = new();
        private bool _isSubscribedToCandles = false;
        private bool _disposed = false;
        private DateTime _lastCandleUpdate = DateTime.MinValue;
        private Models.Candle _currentCandle = null;
        private Models.Account _selectedAccount;
        private DateTime _lastIndicatorCalculation = DateTime.MinValue;
        private const int CALCULATION_INTERVAL_MS = 333; // 3 раза в секунду
        private bool _isConnectionLost = false;
        private readonly TimeZoneInfo _moscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        private bool _useLocalTimeZone = true; // Флаг для использования локального времени
        private readonly ConnectionManager _connectionManager;
        private Deal _dealForExitByGlobalSL;
        MainViewModel _mainViewModel;
        // Добавьте в начало класса StrategyViewModel
        public event Action StrategyStarted;

        public Models.Account SelectedAccount => _selectedAccount;

        // Конкретные стратегии
        private RsiStrategy _rsiStrategy;
        private MaStrategy _maStrategy;
        private ManualStrategy _manualStrategy;
        private RatingStrategy _ratingStrategy;
        private PairsTradingStrategy _pairsStrategy;
        public PairsTradingStrategy PairsStrategy => _pairsStrategy;

        private readonly Window _ownerWindow;


        private CancellationTokenSource _candleUpdateCts;

        private DateTime _lastExitTime = DateTime.MinValue;
        private const int EXIT_COOLDOWN_SECONDS = 15;  // 15 секунд между выходами

        // Добавьте эти свойства в StrategyViewModel, если они нужны для отображения в окне загрузки
        //public bool UseGlobalStopLossDisplay => UseGlobalStopLoss;
        //public decimal GlobalStopLossValueDisplay => GlobalStopLossValue;
        //public bool UseGlobalTakeProfitDisplay => UseGlobalTakeProfit;
        //public decimal GlobalTakeProfitValueDisplay => GlobalTakeProfitValue;


        public TradingStrategy SelectedStrategy => _strategy;
        public Models.Instrument Instrument => _instrument;
        public TimeFrame SelectedTimeFrame => _timeFrame;

        // Публичные свойства для доступа к стратегиям
        public RsiStrategy RsiStrategy => _rsiStrategy;
        public MaStrategy MaStrategy => _maStrategy;
        public ManualStrategy ManualStrategy => _manualStrategy;
        public RatingStrategy RatingStrategy => _ratingStrategy;




        // Добавьте событие для уведомления об изменении цены
        public event EventHandler<decimal> PriceUpdated;
        // В методе обработки свечей, где обновляется CurrentPrice, добавьте:
        private void OnPriceChanged(decimal newPrice)
        {
            CurrentPrice = newPrice;
            PriceUpdated?.Invoke(this, newPrice);

            // Уведомляем открытые окна графиков
            NotifyChartWindows();
        }

        // Список открытых окон графиков
        private static List<WeakReference<ChartWindowViewModel>> _chartViewModels = new();
        // Регистрация ViewModel графика
        public void RegisterChartViewModel(ChartWindowViewModel chartVM)
        {
            _chartViewModels.Add(new WeakReference<ChartWindowViewModel>(chartVM));
        }

        // Уведомление всех графиков
        private void NotifyChartWindows()
        {
            foreach (var weakRef in _chartViewModels.ToList())
            {
                if (weakRef.TryGetTarget(out var chartVM))
                {
                    chartVM.NotifyPriceUpdate();
                }
                else
                {
                    _chartViewModels.Remove(weakRef);
                }
            }
        }
        #endregion





        #region Observable Properties

        [ObservableProperty]
        private bool _isRunning;

        [ObservableProperty]
        private ObservableCollection<string> _logs;

        [ObservableProperty]
        private string _status = "Не запущена";

        [ObservableProperty]
        private string _currentInstrument = "";

        [ObservableProperty]
        private string _currentInstrumentName = "";

        [ObservableProperty]
        private string _currentTimeframe = "";

        [ObservableProperty]
        private decimal _currentPrice;

        [ObservableProperty]
        private object _strategySettingsControl;

        [ObservableProperty]
        private object _strategyControlView;

        [ObservableProperty]
        private bool _isLoadingData;

        [ObservableProperty]
        private double _progressValue;

        [ObservableProperty]
        private double _progressMaximum = 100;

        [ObservableProperty]
        private string _progressText = "";

        [ObservableProperty]
        private bool _isProgressVisible = false;

        [ObservableProperty]
        private string _progressPercentage = "0%";


        [ObservableProperty]
        private int _currentUpdPositions = 0;

        #region Global Stop Loss Properties
        /*[ObservableProperty]
        private bool _useGlobalStopLoss = false;

        [ObservableProperty]
        private string _globalStopLossType = "Percentage"; // "Percentage" или "Absolute"

        [ObservableProperty]
        private decimal _globalStopLossValue = 0.15m; // 1% по умолчанию

        [ObservableProperty]
        private decimal _globalStopLossPrice;

        [ObservableProperty]
        private bool _isGlobalStopLossActive;*/
        #endregion


        #region Global Take Profit Properties
        [ObservableProperty]
        private bool _useGlobalTakeProfit;

        [ObservableProperty]
        private string _globalTakeProfitType = "Percentage";

        [ObservableProperty]
        private decimal _globalTakeProfitValue = 5;

        [ObservableProperty]
        private decimal _globalTakeProfitPrice;

        [ObservableProperty]
        private bool _isGlobalTakeProfitActive;
        #endregion
        #endregion

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }

        public ICommand RefreshPositionsCommand { get; }

        public ICommand GetDescriptionDtrategyCommand { get; }

        public string Title => $"Стратегия {_strategy.Name} - {_instrument.Ticker} ({_timeFrame.DisplayName})";

        public ICommand OpenOptimizationCommand { get; }

        public StrategyViewModel(
            TradingStrategy strategy,
            Models.Instrument instrument,
            TimeFrame timeFrame,
            Models.Account selectedAccount,
            IProvirerService providerService,
            ConnectionManager connectionManager,
            ILogger<StrategyViewModel> logger = null,
            Window ownerWindow = null)
        {
            _strategy = strategy;
            _instrument = instrument;
            _timeFrame = timeFrame;
            _providerService = providerService;
            _logger = logger;
            _connectionManager = connectionManager;
            _selectedAccount = selectedAccount;
            _ownerWindow = ownerWindow; // ✅ СОХРАНЯЕМ ВЛАДЕЛЬЦА окна

            // Инициализация часового пояса
            try
            {
                _moscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
                Debug.WriteLine($"DEBUG: Используется часовой пояс: {_moscowTimeZone.DisplayName}");
            }
            catch
            {
                // Fallback для систем, где не найдена Russian Standard Time
                _moscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow") ??
                                 TimeZoneInfo.FindSystemTimeZoneById("E. Europe Standard Time") ??
                                 TimeZoneInfo.Utc;
                Debug.WriteLine($"DEBUG: Используется альтернативный часовой пояс: {_moscowTimeZone.DisplayName}");
            }

            // отладочный вывод
            Debug.WriteLine($"DEBUG: StrategyViewModel создан. Provider: {providerService.GetType().Name}");

            // Сохраняем ссылку на TinkoffApiService для доступа к статусам
            if (providerService is TinkoffApiService tinkoffService)
            {
                // Подписываемся на обновления статусов для этого окна
                tinkoffService.OnMarketStatusesUpdated += UpdateStrategyMarketStatuses;
                Debug.WriteLine($"DEBUG: Подписались на обновления статусов рынков для {instrument.Ticker}");
            }

            // Инициализация БД
            _dbPath = "market_dataMG5.db";
            _connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");


            // Команды
            StartCommand = new RelayCommand(async () => await StartStrategy());
            StopCommand = new RelayCommand(async () => await StopStrategy());
            //RefreshPositionsCommand = new RelayCommand(async () => await providerService.RefreshPositionsAsync());
            RefreshPositionsCommand = new RelayCommand(async () => await GetCurrentPositionQuantity());
            GetDescriptionDtrategyCommand = new RelayCommand(async () => await GetDescriptionDtrategy());
            OpenOptimizationCommand = new RelayCommand(OpenOptimizationWindow);


            // Инициализация прогресса
            _progressValue = 0;
            _progressMaximum = 100;
            _progressText = "";
            _isProgressVisible = false;


            // ✅ ДОБАВЛЕНО: Подписываемся на события реконнекта
            //_connectionManager.OnConnectionLost += OnConnectionLost;
            //_connectionManager.OnReconnectCompleted += OnStrategyReconnectCompleted;

            // ✅ ИСПРАВЛЕНО: Подписываемся ТОЛЬКО на изменение статуса, без логики реконнекта
            _connectionManager.OnConnectionStateChanged += OnConnectionStateChanged;

            // Регистрируем стратегию для автоматического восстановления
            _connectionManager.RegisterStrategy(this);

            // Инициализация
            _ = InitializeAsync();

            // Инициализируем CancellationTokenSource
            _candleUpdateCts = new CancellationTokenSource();

            // Регистрируем обработчик закрытия окна
            if (Application.Current.MainWindow != null)
            {
                Application.Current.MainWindow.Closed += async (s, e) =>
                {
                    await DisposeAsync();
                };
            }

            _ownerWindow = ownerWindow;
        }


        #region Инициализации
        private async Task InitializeAsync()
        {
            try
            {
                InitializeInstrumentInfo();
                await InitializeDatabaseAsync();
                await InitializeSpecificStrategyAsync();

                Debug.WriteLine("DEBUG: StrategyViewModel initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error initializing StrategySettingsViewModel: {ex.Message}");
            }
        }

        private void InitializeInstrumentInfo()
        {
            CurrentInstrument = _instrument.Ticker ?? "Тикер не определён!";
            CurrentInstrumentName = _instrument.Name ?? " Имя не определено!";
            CurrentTimeframe = _timeFrame.Value;

            Debug.WriteLine($"DEBUG: Strategy initialized - Instrument: {CurrentInstrumentName}, Timeframe: {CurrentTimeframe}, Provider: {_providerService.GetType().Name}");
        }

        private async Task InitializeDatabaseAsync()
        {            
            try
            {
                _connection.Open();

                // ✅ Сначала создаем таблицу метаданных
                await EnsureCandleTablesMetaTableExistsAsync();

                await EnsureCandleTableExists(_instrument.Ticker, _timeFrame.Value);
                await LoadInstrumentHistoryAsync();
                StartCandleUpdates();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error initializing database: {ex.Message}");
            }
        }

        private async Task InitializeSpecificStrategyAsync()
        {
            try
            {
                Debug.WriteLine($"DEBUG: InitializeSpecificStrategyAsync called for {_strategy.Type}");

                // Пробуем разные способы получить ServiceProvider
                IServiceProvider serviceProvider = null;

                // Способ 1: через App
                if (Application.Current is App)
                {
                    serviceProvider = App.ServiceProvider;  // Используем статическое свойство
                    Debug.WriteLine($"DEBUG: Got ServiceProvider via App");
                }

                // Способ 2: через статическое свойство
                if (serviceProvider == null && App.ServiceProvider != null)
                {
                    serviceProvider = App.ServiceProvider;
                    Debug.WriteLine($"DEBUG: Got ServiceProvider via static property");
                }

                // Способ 3: через Dependency Injection (если StrategyViewModel создается через DI)
                if (serviceProvider == null)
                {
                    // Попробуем получить из конструктора
                    serviceProvider = Application.Current?.GetType().GetProperty("ServiceProvider",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)?.GetValue(null) as IServiceProvider;
                    Debug.WriteLine($"DEBUG: Got ServiceProvider via reflection: {(serviceProvider != null ? "SUCCESS" : "FAILED")}");
                }

                ILoggerFactory loggerFactory = null;
                if (serviceProvider != null)
                {
                    loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                    Debug.WriteLine($"DEBUG: Got loggerFactory from ServiceProvider: {(loggerFactory != null ? "NOT null" : "NULL")}");
                }
                else
                {
                    Debug.WriteLine($"DEBUG: ServiceProvider is null, creating logger manually");
                    // Создаем логгер вручную
                    loggerFactory = LoggerFactory.Create(builder =>
                    {
                        builder.AddDebug();
                        builder.AddConsole();
                    });
                }






                // Создаем логгер
                var manualLogger = loggerFactory?.CreateLogger<ManualStrategy>() ??
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ManualStrategy>.Instance;


                // ✅ ИСПРАВЛЕНИЕ: Получаем MainViewModel из ServiceProvider

                if (serviceProvider != null)
                {
                    try
                    {
                        _mainViewModel = serviceProvider.GetService<MainViewModel>();
                        Debug.WriteLine($"DEBUG: Got MainViewModel from ServiceProvider: {(_mainViewModel != null ? "SUCCESS" : "NULL")}");

                        if (_mainViewModel != null)
                        {
                            Debug.WriteLine($"DEBUG: mainViewModel={_mainViewModel.Accounts}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: Error getting MainViewModel: {ex.Message}");
                    }
                }



                Debug.WriteLine($"DEBUG: __________-----_____---___----__---___selectedAccount.Id={_selectedAccount.Id}");

                // Создаем TransactionsService с mainViewModel
                var transactionsLogger = loggerFactory?.CreateLogger<TransactionsService>();
                var transactionsService = new TransactionsService(
                    _providerService,
                    _mainViewModel,  // ← Передаем mainViewModel (может быть null)
                    this,
                    _instrument,
                    _selectedAccount,
                    transactionsLogger);









                switch (_strategy.Type)
                {
                    case "RSI":
                        // ✅ ИСПРАВЛЕНИЕ: Проверяем loggerFactory и создаем логгер с гарантией не-null
                        ILogger<RsiStrategy> rsiLogger;

                        if (loggerFactory != null)
                        {
                            rsiLogger = loggerFactory.CreateLogger<RsiStrategy>();
                            if (rsiLogger == null)
                            {
                                Debug.WriteLine($"WARNING: CreateLogger returned null for RsiStrategy, using NullLogger");
                                rsiLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RsiStrategy>.Instance;
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"WARNING: loggerFactory is null for RsiStrategy, using NullLogger");
                            rsiLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RsiStrategy>.Instance;
                        }

                        /*// ✅ ПОЛУЧАЕМ MainViewModel для RsiStrategy
                        MainViewModel mainViewModelRSI = null;
                        if (serviceProvider != null)
                        {
                            try
                            {
                                mainViewModelRSI = serviceProvider.GetService<MainViewModel>();
                                Debug.WriteLine($"DEBUG: Got MainViewModel for RSI from ServiceProvider: {(mainViewModelRSI != null ? "SUCCESS" : "NULL")}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"DEBUG: Error getting MainViewModel for RSI: {ex.Message}");
                            }
                        }*/

                        _rsiStrategy = new RsiStrategy(
                            rsiLogger,
                            _providerService,
                            this,
                            transactionsService,
                            _mainViewModel);

                        // Устанавливаем инструмент через рефлексию
                        SetInstrumentViaReflection(_rsiStrategy, _instrument);
                        SetFieldViaReflection(_rsiStrategy, "_timeframe", _timeFrame.Value);

                        StrategySettingsControl = _rsiStrategy.GetSettingsView();
                        StrategyControlView = _rsiStrategy.GetControlView();

                        await _rsiStrategy.InitializeAsync(_instrument, _timeFrame.Value);
                        break;

                    case "MA":
                        // ✅ ИСПРАВЛЕНИЕ: Аналогичная проверка для MA
                        ILogger<MaStrategy> maLogger;

                        if (loggerFactory != null)
                        {
                            maLogger = loggerFactory.CreateLogger<MaStrategy>();
                            if (maLogger == null)
                            {
                                maLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<MaStrategy>.Instance;
                            }
                        }
                        else
                        {
                            maLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<MaStrategy>.Instance;
                        }

                        // ✅ ПОЛУЧАЕМ MainViewModel для MaStrategy
                        
                        if (serviceProvider != null)
                        {
                            try
                            {
                                _mainViewModel = serviceProvider.GetService<MainViewModel>();
                                Debug.WriteLine($"DEBUG: Got MainViewModel for MA from ServiceProvider: {(_mainViewModel != null ? "SUCCESS" : "NULL")}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"DEBUG: Error getting MainViewModel for MA: {ex.Message}");
                            }
                        }

                        _maStrategy = new MaStrategy(
                            maLogger,
                            _providerService,
                            _connectionManager,
                            this,
                            transactionsService,
                            _mainViewModel);

                        SetInstrumentViaReflection(_maStrategy, _instrument);
                        SetFieldViaReflection(_maStrategy, "_timeframe", _timeFrame.Value);

                        StrategySettingsControl = _maStrategy.GetSettingsView();
                        StrategyControlView = _maStrategy.GetControlView();

                        await _maStrategy.InitializeAsync(_instrument, _timeFrame.Value);
                        break;

                    case "Manual":
                        Debug.WriteLine($"DEBUG: Creating ManualStrategy...");

                        

                        _manualStrategy = new ManualStrategy(
                            manualLogger,
                            _providerService,
                            _connectionManager,
                            transactionsService,
                            this,
                            _mainViewModel);

                        SetInstrumentViaReflection(_manualStrategy, _instrument);
                        SetFieldViaReflection(_manualStrategy, "_timeframe", _timeFrame.Value);

                        Debug.WriteLine($"DEBUG: Getting StrategySettingsControl...");
                        StrategySettingsControl = _manualStrategy.GetSettingsView();

                        Debug.WriteLine($"DEBUG: Getting StrategyControlView...");
                        StrategyControlView = _manualStrategy.GetControlView();

                        Debug.WriteLine($"DEBUG: StrategySettingsControl is {(StrategySettingsControl != null ? "NOT null" : "NULL")}");
                        Debug.WriteLine($"DEBUG: StrategyControlView is {(StrategyControlView != null ? "NOT null" : "NULL")}");

                        // Инициализируем стратегию
                        await _manualStrategy.InitializeAsync(_instrument);
                        Debug.WriteLine($"DEBUG: ManualStrategy initialized successfully");

                        await StartStrategy();
                        break;


                    // КЕЙС ДЛЯ РЕЙТИНГОВОЙ СТРАТЕГИИ
                    case "Rating":
                        // ✅ ИСПРАВЛЕНИЕ: Аналогичная проверка для Rating
                        ILogger<RatingStrategy> ratingLogger;

                        if (loggerFactory != null)
                        {
                            ratingLogger = loggerFactory.CreateLogger<RatingStrategy>();
                            if (ratingLogger == null)
                            {
                                ratingLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RatingStrategy>.Instance;
                            }
                        }
                        else
                        {
                            ratingLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RatingStrategy>.Instance;
                        }

                        // ✅ ПОЛУЧАЕМ MainViewModel для RatingStrategy
                        MainViewModel mainViewModelRating = null;
                        if (serviceProvider != null)
                        {
                            try
                            {
                                mainViewModelRating = serviceProvider.GetService<MainViewModel>();
                                Debug.WriteLine($"DEBUG: Got MainViewModel for Rating from ServiceProvider: {(mainViewModelRating != null ? "SUCCESS" : "NULL")}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"DEBUG: Error getting MainViewModel for Rating: {ex.Message}");
                            }
                        }

                        _ratingStrategy = new RatingStrategy(
                            ratingLogger,
                            _providerService,
                            _connectionManager,
                            this,
                            transactionsService,
                            mainViewModelRating);

                        SetInstrumentViaReflection(_ratingStrategy, _instrument);
                        SetFieldViaReflection(_ratingStrategy, "_timeframe", _timeFrame.Value);

                        StrategySettingsControl = _ratingStrategy.GetSettingsView();
                        StrategyControlView = _ratingStrategy.GetControlView();

                        await _ratingStrategy.InitializeAsync(_instrument, _timeFrame.Value);
                        break;


                    /*case "PairsTrading":
                        ILogger<PairsTradingStrategy> pairsLogger;
                        if (loggerFactory != null)
                        {
                            pairsLogger = loggerFactory.CreateLogger<PairsTradingStrategy>();
                            if (pairsLogger == null)
                                pairsLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PairsTradingStrategy>.Instance;
                        }
                        else
                            pairsLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PairsTradingStrategy>.Instance;

                        if (serviceProvider != null)
                        {
                            try { _mainViewModel = serviceProvider.GetService<MainViewModel>(); }
                            catch (Exception ex) { Debug.WriteLine($"DEBUG: Error getting MainViewModel for PairsTrading: {ex.Message}"); }
                        }

                        _pairsStrategy = new PairsTradingStrategy(
                            pairsLogger,
                            _providerService,
                            this,
                            transactionsService,
                            _mainViewModel);

                        SetInstrumentViaReflection(_pairsStrategy, _instrument);
                        SetFieldViaReflection(_pairsStrategy, "_timeframe", _timeFrame.Value);

                        StrategySettingsControl = _pairsStrategy.GetSettingsView();
                        StrategyControlView = _pairsStrategy.GetControlView();

                        await _pairsStrategy.InitializeAsync(_instrument, _timeFrame.Value);
                        break;*/

                    case "PairsTrading":
                        ILogger<PairsTradingStrategy> pairsLogger;
                        if (loggerFactory != null)
                        {
                            pairsLogger = loggerFactory.CreateLogger<PairsTradingStrategy>();
                            if (pairsLogger == null)
                                pairsLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PairsTradingStrategy>.Instance;
                        }
                        else
                            pairsLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PairsTradingStrategy>.Instance;

                        if (serviceProvider != null)
                        {
                            try { _mainViewModel = serviceProvider.GetService<MainViewModel>(); }
                            catch (Exception ex) { Debug.WriteLine($"DEBUG: Error getting MainViewModel for PairsTrading: {ex.Message}"); }
                        }

                        _pairsStrategy = new PairsTradingStrategy(
                            pairsLogger,
                            _providerService,
                            this,
                            transactionsService,
                            _mainViewModel);

                        // ✅ НЕ устанавливаем инструмент через рефлексию для PairsTrading
                        // Инструмент будет передан через InitializeAsync как второй (B)
                        // SetInstrumentViaReflection(_pairsStrategy, _instrument); // ← УБИРАЕМ!

                        SetFieldViaReflection(_pairsStrategy, "_timeframe", _timeFrame.Value);

                        StrategySettingsControl = _pairsStrategy.GetSettingsView();
                        StrategyControlView = _pairsStrategy.GetControlView();

                        // ✅ Передаем инструмент как второй (B)
                        await _pairsStrategy.InitializeAsync(_instrument, _timeFrame.Value);
                        break;

                    default:
                        Debug.WriteLine($"DEBUG: Unknown strategy type: {_strategy.Type}");
                        break;
                }


                // Принудительно обновляем UI
                OnPropertyChanged(nameof(StrategySettingsControl));
                OnPropertyChanged(nameof(StrategyControlView));
                Debug.WriteLine($"DEBUG: UI properties notified");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error initializing strategy: {ex.Message}");
            }
        }

        private void SetFieldViaReflection(object strategy, string fieldName, object value)
        {
            try
            {
                var strategyType = strategy.GetType();
                var field = strategyType.GetField(fieldName,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (field != null)
                {
                    field.SetValue(strategy, value);
                    Debug.WriteLine($"DEBUG: Установлено поле {fieldName} для стратегии {strategyType.Name}: {value}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error setting field {fieldName} via reflection: {ex.Message}");
            }
        }

        private void SetInstrumentViaReflection(object strategy, Models.Instrument instrument)
        {
            try
            {
                // Используем рефлексию для установки инструмента, так как нет общего интерфейса
                var strategyType = strategy.GetType();

                // ✅ ПЫТАЕМСЯ НАЙТИ ПОЛЕ _instrument
                var instrumentField = strategyType.GetField("_instrument", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (instrumentField != null)
                {
                    instrumentField.SetValue(strategy, instrument);
                    Debug.WriteLine($"DEBUG: Установлен инструмент для стратегии {strategyType.Name}: {instrument.Ticker}, LotSize={instrument.LotSize}");
                }
                else
                {
                    // Пробуем через свойство
                    var instrumentProperty = strategyType.GetProperty("Instrument", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (instrumentProperty != null)
                    {
                        instrumentProperty.SetValue(strategy, instrument);
                        Debug.WriteLine($"DEBUG: Установлен инструмент через свойство для {strategyType.Name}: {instrument.Ticker}, LotSize={instrument.LotSize}");
                    }
                    else
                    {
                        Debug.WriteLine($"DEBUG: ⚠️ Не удалось найти поле или свойство Instrument для {strategyType.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error setting instrument via reflection: {ex.Message}");
            }
        }
        #endregion

        #region Методы работающие с Базой Данных
        #region Управление метаданными таблиц
        /// <summary>
        /// Создание таблицы CandleTablesMeta если не существует
        /// </summary>
        private async Task EnsureCandleTablesMetaTableExistsAsync()
        {
            try
            {
                var command = _connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CandleTablesMeta (
                        TableName TEXT PRIMARY KEY,
                        InstrumentUid TEXT,
                        Ticker TEXT NOT NULL,
                        Timeframe TEXT NOT NULL,
                        LastUpdate DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
            
                    CREATE INDEX IF NOT EXISTS idx_CandleTablesMeta_Ticker ON CandleTablesMeta(Ticker);
                    CREATE INDEX IF NOT EXISTS idx_CandleTablesMeta_Timeframe ON CandleTablesMeta(Timeframe);
                ";

                await command.ExecuteNonQueryAsync();
                Debug.WriteLine($"DEBUG: Таблица CandleTablesMeta создана/проверена");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка создания таблицы CandleTablesMeta: {ex.Message}");
            }
        }
        #endregion
        /// <summary>
        /// Убеждаемся в существвовании таблицы, если нет, то создаем ее
        /// </summary>
        private async Task EnsureCandleTableExists(string ticker, string timeframe)
        {
            var tableName = await GetTableNameAsync(ticker, timeframe, "EnsureCandleTableExists");

            if (_tableInitialized.ContainsKey(tableName))
            {
                //Debug.WriteLine($"DEBUG: EnsureCandleTableExists: Table '{tableName}' already initialized");
                return;
            }

            try
            {

                // ✅ ВАЖНО: Сначала убеждаемся, что таблица метаданных существует
                await EnsureCandleTablesMetaTableExistsAsync();



                // СНАЧАЛА проверяем существование таблицы в базе данных
                var checkTableCommand = _connection.CreateCommand();
                checkTableCommand.CommandText = @"
                SELECT name FROM sqlite_master 
                WHERE type='table' AND name=@tableName";
                checkTableCommand.Parameters.AddWithValue("@tableName", tableName);

                var existingTable = await checkTableCommand.ExecuteScalarAsync();

                // Если таблицы не существует, создаем ее
                if (existingTable == null)
                {
                    Debug.WriteLine($"DEBUG: таблицы для свечей не существует, создаем ее... EnsureCandleTableExists: Creating table '{tableName}' for '{ticker}/{timeframe}'");

                    // Создаем таблицу для свечей
                    var createTableCommand = _connection.CreateCommand();
                    createTableCommand.CommandText = $@"
                        CREATE TABLE {tableName} (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Ticker TEXT NOT NULL,
                            Timeframe TEXT NOT NULL,
                            Time DATETIME NOT NULL,
                            Open DECIMAL(18,8) NOT NULL,
                            High DECIMAL(18,8) NOT NULL,
                            Low DECIMAL(18,8) NOT NULL,
                            Close DECIMAL(18,8) NOT NULL,
                            Volume BIGINT NOT NULL,
                            IsClosed BOOLEAN NOT NULL DEFAULT 0,
                            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                            UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                            UNIQUE(Ticker, Timeframe, Time)
                        );
            
                        CREATE INDEX idx_{tableName}_time 
                        ON {tableName}(Time DESC);
            
                        CREATE INDEX idx_{tableName}_ticker 
                        ON {tableName}(Ticker, Timeframe, Time);";

                    await createTableCommand.ExecuteNonQueryAsync();

                    // После создания таблицы добавляем запись в метаданные
                    Debug.WriteLine($"DEBUG: После создания таблицы добавляем запись в метаданные....................");
                    var insertMetaCommand = _connection.CreateCommand();
                    insertMetaCommand.CommandText = @"
                        INSERT OR REPLACE INTO CandleTablesMeta 
                        (TableName, InstrumentUid, Ticker, Timeframe, LastUpdate)
                        VALUES (@tableName, @instrumentUid, @ticker, @timeframe, CURRENT_TIMESTAMP)";

                    insertMetaCommand.Parameters.AddWithValue("@tableName", tableName);

                    // ФИКС: Используем DBNull.Value вместо null для параметров, которые могут быть NULL
                    var instrumentUidParam = insertMetaCommand.CreateParameter();
                    instrumentUidParam.ParameterName = "@instrumentUid";
                    instrumentUidParam.Value = ticker.Contains('-') ? (object)ticker : DBNull.Value;
                    insertMetaCommand.Parameters.Add(instrumentUidParam);

                    insertMetaCommand.Parameters.AddWithValue("@ticker", ticker);
                    insertMetaCommand.Parameters.AddWithValue("@timeframe", timeframe);

                    await insertMetaCommand.ExecuteNonQueryAsync();
                    Debug.WriteLine($"DEBUG: После создания таблицы добаBИЛИ запись в метаданные....................++++");
                    Debug.WriteLine($"DEBUG: EnsureCandleTableExists: Created table '{tableName}' and metadata for '{ticker}/{timeframe}'");
                }
                else
                {
                    Debug.WriteLine($"DEBUG: таблицы для свечей существует!!!!   EnsureCandleTableExists: Table '{tableName}' already exists in database");

                    // Проверяем, есть ли запись в метаданных
                    var checkMetaCommand = _connection.CreateCommand();
                    checkMetaCommand.CommandText = @"
                    SELECT TableName FROM CandleTablesMeta 
                    WHERE TableName = @tableName";
                    checkMetaCommand.Parameters.AddWithValue("@tableName", tableName);

                    var existingMeta = await checkMetaCommand.ExecuteScalarAsync();

                    if (existingMeta == null)
                    {
                        //Debug.WriteLine($"DEBUG: EnsureCandleTableExists: Adding missing metadata for table '{tableName}'");

                        var insertMetaCommand = _connection.CreateCommand();
                        insertMetaCommand.CommandText = @"
                        INSERT OR REPLACE INTO CandleTablesMeta 
                        (TableName, InstrumentUid, Ticker, Timeframe, LastUpdate)
                        VALUES (@tableName, @instrumentUid, @ticker, @timeframe, CURRENT_TIMESTAMP)";

                        insertMetaCommand.Parameters.AddWithValue("@tableName", tableName);

                        // ФИКС: Используем DBNull.Value вместо null для параметров, которые могут быть NULL
                        var instrumentUidParam = insertMetaCommand.CreateParameter();
                        instrumentUidParam.ParameterName = "@instrumentUid";
                        instrumentUidParam.Value = ticker.Contains('-') ? (object)ticker : DBNull.Value;
                        insertMetaCommand.Parameters.Add(instrumentUidParam);

                        insertMetaCommand.Parameters.AddWithValue("@ticker", ticker);
                        insertMetaCommand.Parameters.AddWithValue("@timeframe", timeframe);

                        await insertMetaCommand.ExecuteNonQueryAsync();
                    }
                }

                _tableInitialized[tableName] = true;
                //Debug.WriteLine($"DEBUG: EnsureCandleTableExists: Table '{tableName}' marked as initialized");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: EnsureCandleTableExists: Error creating table '{tableName}': {ex.Message}");
                throw;
            }
        }
        /// <summary>
        /// Получает корректное имя таблицы на основе тикера
        /// </summary>
        private async Task<string> GetTableNameAsync(string ticker, string timeframe, string callerMethod = "")
        {
            // Очищаем символы для безопасного имени таблицы
            var cleanTicker = new string(ticker.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            var cleanTimeframe = new string(timeframe.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

            var tableName = $"Candles_{cleanTicker}_{cleanTimeframe}";

            //Debug.WriteLine($"DEBUG [{callerMethod}]: GetTableNameAsync called with: '{ticker}' -> '{ticker}' -> table: '{tableName}'");

            return tableName;
        }
        /// <summary>
        /// МАССОВОЕ СОХРАНЕНИЕ СВЕЧЕЙ
        /// </summary>
        public async Task SaveCandlesAsync(string ticker, string timeframe, List<Models.Candle> candles)
        {
            if (candles.Count == 0) return;

            try
            {
                var tableName = await GetTableNameAsync(ticker, timeframe);

                //Debug.WriteLine($"DEBUG: SaveCandlesAsync: Saving {candles.Count} candles for {ticker}/{timeframe}");

                await EnsureCandleTableExists(ticker, timeframe);

                // Используем транзакцию для быстрой массовой вставки
                using (var transaction = await _connection.BeginTransactionAsync())
                {
                    try
                    {
                        var command = _connection.CreateCommand();
                        command.Transaction = (SqliteTransaction?)transaction;
                        command.CommandText = $@"
                        INSERT OR REPLACE INTO {tableName} 
                        (Ticker, Timeframe, Time, Open, High, Low, Close, Volume, IsClosed, UpdatedAt)
                        VALUES (@ticker, @timeframe, @time, @open, @high, @low, @close, @volume, @isClosed, CURRENT_TIMESTAMP)";

                        command.Parameters.Add(new SqliteParameter("@ticker", SqliteType.Text));
                        command.Parameters.Add(new SqliteParameter("@timeframe", SqliteType.Text));
                        command.Parameters.Add(new SqliteParameter("@time", SqliteType.Text));
                        command.Parameters.Add(new SqliteParameter("@open", SqliteType.Real));
                        command.Parameters.Add(new SqliteParameter("@high", SqliteType.Real));
                        command.Parameters.Add(new SqliteParameter("@low", SqliteType.Real));
                        command.Parameters.Add(new SqliteParameter("@close", SqliteType.Real));
                        command.Parameters.Add(new SqliteParameter("@volume", SqliteType.Integer));
                        command.Parameters.Add(new SqliteParameter("@isClosed", SqliteType.Integer));

                        command.Parameters["@ticker"].Value = ticker;
                        command.Parameters["@timeframe"].Value = timeframe;

                        foreach (var candle in candles)
                        {
                            command.Parameters["@time"].Value = candle.Time.ToString("yyyy-MM-dd HH:mm:ss");
                            command.Parameters["@open"].Value = candle.Open;
                            command.Parameters["@high"].Value = candle.High;
                            command.Parameters["@low"].Value = candle.Low;
                            command.Parameters["@close"].Value = candle.Close;
                            command.Parameters["@volume"].Value = candle.Volume;
                            command.Parameters["@isClosed"].Value = candle.IsClosed ? 1 : 0;

                            await command.ExecuteNonQueryAsync();
                        }

                        await transaction.CommitAsync();
                        //Debug.WriteLine($"DEBUG: SaveCandlesAsync: Successfully saved {candles.Count} candles");
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        Debug.WriteLine($"DEBUG: SaveCandlesAsync: Error saving batch: {ex.Message}");
                        throw;
                    }
                }

                await UpdateTableMeta(tableName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: SaveCandlesAsync: Error: {ex.Message}");
                throw;
            }
        }
        //  метод для сохранения текущей свечи:
        private async Task SaveCurrentCandleAsync()
        {
            if (_currentCandle == null) return;

            try
            {
                var tableName = await GetTableNameAsync(_instrument.Ticker, CurrentTimeframe);

                //Debug.WriteLine($"DEBUG: Сохранение текущей свечи: " +
                //               $"Time={_currentCandle.Time:HH:mm:ss}, " +
                //               $"O={_currentCandle.Open}, H={_currentCandle.High}, " +
                //               $"L={_currentCandle.Low}, C={_currentCandle.Close}, " +
                //               $"V={_currentCandle.Volume}");

                var command = _connection.CreateCommand();
                command.CommandText = $@"
        INSERT OR REPLACE INTO {tableName} 
        (Ticker, Timeframe, Time, Open, High, Low, Close, Volume, IsClosed, UpdatedAt)
        VALUES (@ticker, @timeframe, @time, @open, @high, @low, @close, @volume, @isClosed, CURRENT_TIMESTAMP)";

                command.Parameters.AddWithValue("@ticker", _currentCandle.Ticker);
                command.Parameters.AddWithValue("@timeframe", _currentCandle.Timeframe);
                command.Parameters.AddWithValue("@time", _currentCandle.Time.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@open", _currentCandle.Open);
                command.Parameters.AddWithValue("@high", _currentCandle.High);
                command.Parameters.AddWithValue("@low", _currentCandle.Low);
                command.Parameters.AddWithValue("@close", _currentCandle.Close);
                command.Parameters.AddWithValue("@volume", _currentCandle.Volume); // ОБЪЕМ ПЕРЕДАЕТСЯ В БД
                command.Parameters.AddWithValue("@isClosed", _currentCandle.IsClosed ? 1 : 0);

                var rowsAffected = await command.ExecuteNonQueryAsync();
             //   Debug.WriteLine($"DEBUG: Строк затронуто: {rowsAffected}");

                await UpdateTableMeta(tableName);
             //   Debug.WriteLine($"DEBUG: Свеча успешно сохранена с объемом: {_currentCandle.Volume}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка сохранения текущей свечи:   {_currentCandle.Ticker} {_instrument.Ticker}    {CurrentTimeframe}  {_currentCandle.Timeframe}    {ex.Message}");
                Debug.WriteLine($"DEBUG: Параметры свечи: " +
                               $"Time={_currentCandle?.Time}, Volume={_currentCandle?.Volume}");
            }
        }
        private async Task UpdateTableMeta(string tableName)
        {
            try
            {
                var command = _connection.CreateCommand();
                command.CommandText = @"
                UPDATE CandleTablesMeta 
                SET LastUpdate = CURRENT_TIMESTAMP
                WHERE TableName = @tableName";

                command.Parameters.AddWithValue("@tableName", tableName);
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: UpdateTableMeta: Error: {ex.Message}");
            }
        }
        private async Task ProcessMarketDataAsync(decimal price)
        {
            try
            {
                // Ограничиваем частоту расчетов до 3 раз в секунду
                var now = DateTime.Now;
                if ((now - _lastIndicatorCalculation).TotalMilliseconds < CALCULATION_INTERVAL_MS)
                    return;



                if (IsRunning)
                {
                    // Загружаем свечи из БД для расчета
                    var candles = await GetHistoricalCandlesFromDbAsync(100);

                    if (candles.Any())
                    {
                        // Получаем последнюю цену из свечей или используем текущую
                        var lastCandle = candles.Last();
                        var marketData = new MarketData
                        {
                            LastPrice = price > 0 ? price : lastCandle.Close,
                            Time = DateTime.Now,
                            InstrumentUid = _instrument.Uid
                        };




                        // Вызываем обработку данных в соответствующей стратегии
                        switch (_strategy.Type)
                        {
                            case "RSI":
                                if (_rsiStrategy != null)
                                {
                                   
                                    await _rsiStrategy.ProcessMarketData(marketData);
                                }
                                break;

                            case "MA":
                                if (_maStrategy != null)
                                {
                                    
                                    await _maStrategy.ProcessMarketData(marketData);
                                }
                                break;

                            case "Manual":
                                if (_manualStrategy != null)
                                {
                                    //Debug.WriteLine($"-------_manualStrategy={_manualStrategy}---------!!!!!!!!!!!-");
                                    await _manualStrategy.ProcessMarketData(marketData);
                                }
                                break;

                            case "Rating":
                                if (_ratingStrategy != null)
                                {
                                    await _ratingStrategy.ProcessMarketData(marketData);
                                }
                                break;

                            case "PairsTrading":
                                if (_pairsStrategy != null)
                                {
                                    // ✅ ПЕРЕДАЕМ MarketData с InstrumentUid
                                    await _pairsStrategy.ProcessMarketData(marketData);
                                }
                                break;
                        }

                        _lastIndicatorCalculation = now;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error processing market data: {ex.Message}");
            }
        }
        // Метод для загрузки свечей из БД
        public async Task<List<Models.Candle>> GetHistoricalCandlesFromDbAsync(int count)
        {
            var candles = new List<Models.Candle>();

            try
            {
                var tableName = await GetTableNameAsync(_instrument.Ticker, _timeFrame.Value);

                var command = _connection.CreateCommand();
                command.CommandText = $@"
                    SELECT Id, Ticker, Timeframe, Time, Open, High, Low, Close, Volume, IsClosed
                    FROM {tableName}
                    WHERE Ticker = @ticker
                    AND Timeframe = @timeframe
                    ORDER BY Time DESC
                    LIMIT @count";

                command.Parameters.AddWithValue("@ticker", _instrument.Ticker);
                command.Parameters.AddWithValue("@timeframe", _timeFrame.Value);
                command.Parameters.AddWithValue("@count", count);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var candle = new Models.Candle
                    {
                        Id = reader.GetInt64(0),
                        Ticker = reader.GetString(1),
                        Timeframe = reader.GetString(2),
                        Time = reader.GetDateTime(3),
                        Open = reader.GetDecimal(4),
                        High = reader.GetDecimal(5),
                        Low = reader.GetDecimal(6),
                        Close = reader.GetDecimal(7),
                        Volume = reader.GetInt64(8),
                        IsClosed = reader.GetBoolean(9)
                    };

                    candles.Add(candle);
                }

                // Возвращаем в правильном порядке (от старых к новым)
                return candles.OrderBy(c => c.Time).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error loading candles from DB: {ex.Message}");
            }

            return candles;
        }
        /// <summary>
        /// Загрузка свечей для указанного инструмента
        /// </summary>
        public async Task<List<Models.Candle>> GetHistoricalCandlesFromDbAsync(string ticker, string timeframe, int count)
        {
            var candles = new List<Models.Candle>();

            try
            {
                var tableName = await GetTableNameAsync(ticker, timeframe);

                var command = _connection.CreateCommand();
                command.CommandText = $@"
            SELECT Id, Ticker, Timeframe, Time, Open, High, Low, Close, Volume, IsClosed
            FROM {tableName}
            WHERE Ticker = @ticker
            AND Timeframe = @timeframe
            ORDER BY Time DESC
            LIMIT @count";

                command.Parameters.AddWithValue("@ticker", ticker);
                command.Parameters.AddWithValue("@timeframe", timeframe);
                command.Parameters.AddWithValue("@count", count);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var candle = new Models.Candle
                    {
                        Id = reader.GetInt64(0),
                        Ticker = reader.GetString(1),
                        Timeframe = reader.GetString(2),
                        Time = reader.GetDateTime(3),
                        Open = reader.GetDecimal(4),
                        High = reader.GetDecimal(5),
                        Low = reader.GetDecimal(6),
                        Close = reader.GetDecimal(7),
                        Volume = reader.GetInt64(8),
                        IsClosed = reader.GetBoolean(9)
                    };

                    candles.Add(candle);
                }

                // Возвращаем в правильном порядке (от старых к новым)
                return candles.OrderBy(c => c.Time).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error loading candles from DB: {ex.Message}");
            }

            return candles;
        }

        #endregion

        #region Загрузка истории свечей
        /// <summary>
        /// Получает историю свечей за заданный период
        /// </summary>
        private async Task LoadInstrumentHistoryAsync()
        {
            try
            {
                Status = "Загрузка исторических данных...";
                IsLoadingData = true;
                IsProgressVisible = true;
                ProgressText = "Загрузка исторических данных...";
                ProgressValue = 0;
                ProgressMaximum = 100;

                var ticker = _instrument.Ticker;

                // Проверяем в БД наличие и время последней свечи (локальное время)
                var lastCandleTime = await GetLastCandleTime(ticker, CurrentTimeframe);
                DateTime startTime;

                if (lastCandleTime.HasValue)
                {
                    var timeframeMinutes = GetTimeframeMinutes(CurrentTimeframe);
                    var currentLocalTime = DateTime.Now;
                    var timeSinceLastCandle = currentLocalTime - lastCandleTime.Value;

                    if (timeSinceLastCandle.TotalMinutes > timeframeMinutes)
                    {
                        // Конвертируем локальное время в UTC для запроса к API
                        startTime = ConvertLocalToUtcTime(lastCandleTime.Value);
                        Debug.WriteLine($"DEBUG: Загрузка дополнительных данных с {lastCandleTime.Value} (UTC: {startTime})");
                    }
                    else
                    {
                        Debug.WriteLine($"DEBUG: История актуальна для {ticker}/{CurrentTimeframe}");
                        Status = "История актуальна";
                        IsLoadingData = false;
                        IsProgressVisible = false;
                        return;
                    }
                }
                else
                {
                    // Начинаем загрузку с 30 дней назад в локальном времени
                    var localStartTime = DateTime.Now.AddDays(-30);
                    startTime = ConvertLocalToUtcTime(localStartTime);
                    Debug.WriteLine($"DEBUG: Загрузка 30-дневной истории с {localStartTime} (UTC: {startTime})");
                }

                var endTime = DateTime.UtcNow; // Конечное время в UTC

                // Рассчитываем количество дней для прогресса
                var totalDays = (ConvertUtcToLocalTime(endTime) - ConvertUtcToLocalTime(startTime)).TotalDays;
                var currentDay = 0;

                Debug.WriteLine($"DEBUG: Загрузка исторических данных с {ConvertUtcToLocalTime(startTime)} по {ConvertUtcToLocalTime(endTime)} ({totalDays:F1} дней)");

                var allCandles = new List<Models.Candle>();
                var currentStart = startTime;

                while (currentStart < endTime)
                {
                    var currentEnd = currentStart.AddDays(1);
                    if (currentEnd > endTime)
                    {
                        currentEnd = endTime;
                    }

                    currentDay++;

                    var progressPercent = (currentDay / totalDays) * 100;
                    UpdateProgress(progressPercent, 100,
                        $"Загрузка данных: {ConvertUtcToLocalTime(currentStart):dd.MM.yyyy} - {ConvertUtcToLocalTime(currentEnd):dd.MM.yyyy}");

                    Debug.WriteLine($"DEBUG: Loading chunk {currentDay}/{totalDays:F0} ({progressPercent:F1}%)");

                    try
                    {
                        var chunkCandles = await _providerService.GetHistoricalDataAsync(
                            _instrument.Ticker,
                            _instrument.Uid,
                            CurrentTimeframe,
                            currentStart,
                            currentEnd);

                        if (chunkCandles != null && chunkCandles.Any())
                        {
                            // Конвертируем время свечей из UTC в локальное
                            foreach (var candle in chunkCandles)
                            {
                                candle.Time = ConvertUtcToLocalTime(candle.Time);
                                candle.Ticker = ticker;
                                candle.Timeframe = CurrentTimeframe;
                            }

                            allCandles.AddRange(chunkCandles);
                            Debug.WriteLine($"DEBUG: Loaded {chunkCandles.Count} candles for chunk");
                        }

                        await Task.Delay(100);
                    }
                    catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.InvalidArgument)
                    {
                        Debug.WriteLine($"DEBUG: RPC Error for chunk {currentStart}-{currentEnd}: {rpcEx.Status.Detail}");
                        currentEnd = currentStart.AddHours(12);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: Error loading chunk: {ex.Message}");
                    }

                    currentStart = currentEnd;
                }

                if (allCandles.Any())
                {
                    UpdateProgress(95, 100, "Сохранение данных в базу...");

                    await SaveCandlesAsync(ticker, CurrentTimeframe, allCandles);
                    Debug.WriteLine($"DEBUG: Всего загружено и сохранено {allCandles.Count} свечей с локальным временем");
                }
                else
                {
                    Debug.WriteLine($"DEBUG: No candles loaded");
                }

                UpdateProgress(100, 100, "Загрузка завершена");
                ProgressPercentage = "100%";
                Status = "Исторические данные загружены";

                await Task.Delay(2000);
                IsProgressVisible = false;
                IsLoadingData = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error loading instrument history: {ex.Message}");
                Status = $"Ошибка загрузки истории: {ex.Message}";
                IsLoadingData = false;
                IsProgressVisible = false;
            }
        }
        /// <summary>
        /// Проверяем в БД наличие и время последней свечи
        /// </summary>
        public async Task<DateTime?> GetLastCandleTime(string ticker, string timeframe)
        {
            try
            {
                var lastCandle = await GetLastCandleAsync(ticker, timeframe);
                return lastCandle?.Time;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: GetLastCandleTime: Error: {ex.Message}");
                return null;
            }
        }
        /// <summary>
        /// ПОЛУЧЕНИЕ ПОСЛЕДНЕЙ СВЕЧИ из БД (закрытой или незакрытой)
        /// </summary>
        public async Task<Models.Candle> GetLastCandleAsync(string ticker, string timeframe)
        {
            try
            {
                var tableName = await GetTableNameAsync(ticker, timeframe);

                // Проверяем существование таблицы
                var checkCommand = _connection.CreateCommand();
                checkCommand.CommandText = @"
                SELECT name FROM sqlite_master 
                WHERE type='table' AND name=@tableName";
                checkCommand.Parameters.AddWithValue("@tableName", tableName);

                if (await checkCommand.ExecuteScalarAsync() == null)
                    return null;

                var command = _connection.CreateCommand();
                command.CommandText = $@"
                SELECT Id, Ticker, Timeframe, Time, Open, High, Low, Close, Volume, IsClosed
                FROM {tableName}
                WHERE Ticker = @ticker
                AND Timeframe = @timeframe
                ORDER BY Time DESC
                LIMIT 1";

                command.Parameters.AddWithValue("@ticker", ticker);
                command.Parameters.AddWithValue("@timeframe", timeframe);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    Debug.WriteLine($"DEBUG: Id = {reader.GetInt64(0)},\r\n                        Ticker = {reader.GetString(1)},\r\n                        Timeframe = {reader.GetString(2)},\r\n                        Time = {reader.GetDateTime(3)},\r\n                        Open = {reader.GetDecimal(4)},\r\n                        High = {reader.GetDecimal(5)},\r\n                        Low = {reader.GetDecimal(6)},\r\n                        Close = {reader.GetDecimal(7)},\r\n                        Volume = {reader.GetInt64(8)},\r\n                        IsClosed = {reader.GetBoolean(9)}");

                    return new Models.Candle
                    {
                        Id = reader.GetInt64(0),
                        Ticker = reader.GetString(1),
                        Timeframe = reader.GetString(2),
                        Time = reader.GetDateTime(3),
                        Open = reader.GetDecimal(4),
                        High = reader.GetDecimal(5),
                        Low = reader.GetDecimal(6),
                        Close = reader.GetDecimal(7),
                        Volume = reader.GetInt64(8),
                        IsClosed = reader.GetBoolean(9)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: GetLastCandleAsync: Error: {ex.Message}");
                return null;
            }
        }
        /// <summary>
        /// Обновление прогрессбара при загрузке истории свечей
        /// </summary>
        private void UpdateProgress(double value, double max, string text = null)
        {
            ProgressValue = value;
            ProgressMaximum = max;

            // Обновляем проценты
            var percentage = (value / max) * 100;
            ProgressPercentage = $"{percentage:F1}%";

            if (!string.IsNullOrEmpty(text))
            {
                ProgressText = text;
            }

            // Автоматически показываем/скрываем прогресс
            IsProgressVisible = value > 0 && value < max;
        }
        private int GetTimeframeMinutes(string timeframe)
        {
            return timeframe?.ToLower() switch
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
        #endregion

        #region Подписка и обновление текущей свечи
        private void StartCandleUpdates()
        {
            if (_disposed) return;

            if (_providerService != null && !string.IsNullOrEmpty(_instrument.Uid))
            {
               /* if (_isSubscribedToCandles)
                {
                    Debug.WriteLine($"DEBUG: Уже подписаны на свечи для {_instrument.Ticker}");
                    return;
                }*/

                //_isSubscribedToCandles = true;
                _candleUpdateCts = new CancellationTokenSource();

                Debug.WriteLine($"DEBUG: Подписываемся на свечи для {_instrument.Ticker} " +
                               $"({_instrument.Uid}), таймфрейм: {CurrentTimeframe}");

                // Подписываемся на обновления свечей
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _providerService.SubscribeToCandlesAsync(
                            _instrument.Uid,
                            CurrentTimeframe,  // Убедитесь, что передаем правильный таймфрейм
                            async (candleUpdate) =>
                            {
                                // Проверяем отмену
                                if (_candleUpdateCts?.IsCancellationRequested == true || _disposed)
                                    return;


                                try
                                {
                                    


                                    // Проверяем таймфрейм свечи
                                    if (candleUpdate.Timeframe != null &&
                                        candleUpdate.Timeframe != CurrentTimeframe)
                                    {
                                        Debug.WriteLine($"DEBUG: Получена свеча с другим таймфреймом:  {_instrument.Ticker}" +
                                                       $"{candleUpdate.Timeframe} (ожидался {CurrentTimeframe})");
                                        return;
                                    }
                                    
                                    // Ограничиваем частоту обновлений
                                    if (DateTime.Now - _lastCandleUpdate < TimeSpan.FromMilliseconds(100))
                                        return;

                                    _lastCandleUpdate = DateTime.Now;

                                    //Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Обновление свечи {_instrument.Ticker}:");
                                    //Debug.WriteLine($"  Полная свеча: {candleUpdate.IsComplete}");
                                    //Debug.WriteLine($"  Таймфрейм: {candleUpdate.Timeframe ?? CurrentTimeframe}");
                                    


                                    // Если свеча завершена, сохраняем ее в базу данных
                                    if (candleUpdate.IsComplete && candleUpdate.Time > DateTime.MinValue)
                                    {
                                        await ProcessCompletedCandle(candleUpdate);
                                    }
                                    else
                                    {
                                        // Обновляем текущую формирующуюся свечу
                                        await UpdateCurrentCandle(candleUpdate);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"DEBUG: Ошибка в обработке обновления свечи: {ex.Message}");
                                }
                            });

                        Debug.WriteLine($"DEBUG: Успешно подписались на свечи для {_instrument.Ticker}  {CurrentTimeframe}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: Ошибка подписки на свечи: {ex.Message}");
                        //_isSubscribedToCandles = false;
                    }
                });
            }
        }

        //  метод для обработки завершенной свечи:
        private async Task ProcessCompletedCandle(CandleUpdate candleUpdate)
        {
            // Добавьте проверку на disposed
            if (_disposed)
            {
                Debug.WriteLine($"DEBUG: Стратегия {_instrument.Ticker} уничтожена, игнорируем завершенную свечу");
                return;
            }

            try
            {
                //Debug.WriteLine($"DEBUG: Завершенная свеча для {_instrument.Ticker} " +
                 //       $"в {candleUpdate.Time} (таймфрейм: {candleUpdate.Timeframe}), " +
                 //       $"Объем: {candleUpdate.Volume}");

                // НОРМАЛИЗУЕМ ТАЙМФРЕЙМ ДЛЯ СРАВНЕНИЯ
                var normalizedUpdateTimeframe = NormalizeTimeframeForComparison(candleUpdate.Timeframe);
                var normalizedCurrentTimeframe = NormalizeTimeframeForComparison(CurrentTimeframe);

                // Проверяем, что это свеча для нашего таймфрейма
                if (!string.IsNullOrEmpty(candleUpdate.Timeframe) &&
                    normalizedUpdateTimeframe != normalizedCurrentTimeframe)
                {
                    Debug.WriteLine($"DEBUG: Пропускаем свечу с несоответствующим таймфреймом: " +
                                   $"{candleUpdate.Timeframe} (нормализовано: {normalizedUpdateTimeframe}) != " +
                                   $"{CurrentTimeframe} (нормализовано: {normalizedCurrentTimeframe})");
                    return;
                }

                // Конвертируем время из UTC в локальное
                var localCandleTime = ConvertUtcToLocalTime(candleUpdate.Time);

                var completedCandle = new Models.Candle
                {
                    Ticker = _instrument.Ticker,
                    Timeframe = CurrentTimeframe,
                    Time = localCandleTime, // Сохраняем локальное время
                    Open = candleUpdate.Open,
                    High = candleUpdate.High,
                    Low = candleUpdate.Low,
                    Close = candleUpdate.Close,
                    Volume = (long)(candleUpdate.Volume > 0 ? candleUpdate.Volume : 0), // ЯВНОЕ ПРЕОБРАЗОВАНИЕ
                    IsClosed = true
                };

                //Debug.WriteLine($"DEBUG: Создана завершенная свеча с объемом: {completedCandle.Volume}");

                // Сохраняем завершенную свечу в базу данных
                await SaveCandlesAsync(_instrument.Ticker, CurrentTimeframe,
                    new List<Models.Candle> { completedCandle });

                //Debug.WriteLine($"DEBUG: Завершенная свеча сохранена в БД (локальное время): " +
                //               $"Time={localCandleTime}, O={completedCandle.Open}, " +
                //               $"H={completedCandle.High}, L={completedCandle.Low}, " +
                 //              $"C={completedCandle.Close}, V={completedCandle.Volume}");

                // Сбрасываем текущую свечу только если это свеча нашего таймфрейма
                if (_currentCandle != null &&
                    GetCandleStartTime(_currentCandle.Time, CurrentTimeframe) ==
                    GetCandleStartTime(localCandleTime, CurrentTimeframe))
                {
                    _currentCandle = null;
                    //Debug.WriteLine($"DEBUG: Текущая свеча сброшена");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка обработки завершенной свечи: {ex.Message}");
            }
        }
        //  метод для обновления текущей формирующейся свечи:
        /*private async Task UpdateCurrentCandle(CandleUpdate candleUpdate)
        {
            // Добавьте проверку на disposed
            if (_disposed )
            {
                Debug.WriteLine($"DEBUG: Стратегия {_instrument.Ticker} уничтожена или отключена, игнорируем обновление");
                return;
            }


            try
            {
                // Обновляем текущую цену
                CurrentPrice = candleUpdate.LastPrice;

                // Если стратегия запущена, обрабатываем новые данные
                if (IsRunning)
                {
                    await ProcessMarketDataAsync(candleUpdate.LastPrice);
                }





                // Определяем время начала текущей свечи в локальном времени
                var currentLocalTime = DateTime.Now;
                var currentCandleStartTime = GetCandleStartTime(currentLocalTime, CurrentTimeframe);

                //Debug.WriteLine($"DEBUG: Текущее локальное время свечи: {currentCandleStartTime}");
                //Debug.WriteLine($"DEBUG: Получен candleUpdate с данными: " +
                //               $"Price={candleUpdate.LastPrice}, Volume={candleUpdate.Volume}, " +
                //               $"Open={candleUpdate.Open}, Close={candleUpdate.Close}");

                // Проверяем, нужно ли создавать новую свечу
                if (_currentCandle == null ||
                    GetCandleStartTime(_currentCandle.Time, CurrentTimeframe) != currentCandleStartTime)
                {
                    // Закрываем предыдущую свечу, если она существует
                    if (_currentCandle != null && !_currentCandle.IsClosed)
                    {
                        _currentCandle.IsClosed = true;
                        _currentCandle.Close = _currentCandle.Close;

                //        Debug.WriteLine($"DEBUG: Закрытие предыдущей свечи. Объем: {_currentCandle.Volume}");

                        // Сохраняем завершенную свечу
                        await SaveCandlesAsync(_instrument.Ticker, CurrentTimeframe,
                            new List<Models.Candle> { _currentCandle });
                //        Debug.WriteLine($"DEBUG: Закрыта предыдущая свеча в {_currentCandle.Time} " +
                //                       $"(объем: {_currentCandle.Volume})");
                    }

                    // Создаем новую свечу с локальным временем
                    _currentCandle = new Models.Candle
                    {
                        Ticker = _instrument.Ticker,
                        Timeframe = CurrentTimeframe,
                        Time = currentCandleStartTime,
                        Open = candleUpdate.Open > 0 ? candleUpdate.Open : candleUpdate.LastPrice,
                        High = candleUpdate.High > 0 ? candleUpdate.High : candleUpdate.LastPrice,
                        Low = candleUpdate.Low > 0 ? candleUpdate.Low : candleUpdate.LastPrice,
                        Close = candleUpdate.LastPrice,
                        Volume = (long)(candleUpdate.Volume > 0 ? candleUpdate.Volume : 0),
                        IsClosed = false
                    };

                //    Debug.WriteLine($"DEBUG: Создана НОВАЯ свеча с локальным временем начала {currentCandleStartTime}");
                //    Debug.WriteLine($"DEBUG: Начальный объем новой свечи: {_currentCandle.Volume}");
                }
                else
                {
                    // Обновляем параметры текущей свечи
                    _currentCandle.Close = candleUpdate.LastPrice;

                    if (candleUpdate.LastPrice > _currentCandle.High)
                    {
                        _currentCandle.High = candleUpdate.LastPrice;
                    }

                    if (candleUpdate.LastPrice < _currentCandle.Low)
                    {
                        _currentCandle.Low = candleUpdate.LastPrice;
                    }

                    // ОБНОВЛЯЕМ ОБЪЕМ ИЗ CANDLEUPDATE
                    // Если в candleUpdate есть объем (из полной свечи или тика)
                    if (candleUpdate.Volume > 0)
                    {
                        // Если это обновление свечи с объемом, обновляем значение
                        if (candleUpdate.Open > 0 && candleUpdate.Close > 0)
                        {
                            // Это полная свеча - устанавливаем точный объем
                            _currentCandle.Volume = (long)candleUpdate.Volume;
                            //Debug.WriteLine($"DEBUG: Объем установлен из свечи: {candleUpdate.Volume}");
                        }
                        else
                        {
                            // Это тик - добавляем объем
                            _currentCandle.Volume += (long)candleUpdate.Volume;
                            Debug.WriteLine($"DEBUG: Объем увеличен из тика: +{candleUpdate.Volume} = {_currentCandle.Volume}");
                        }
                    }
                }

                CurrentPrice = candleUpdate.LastPrice;


                // при изменении цены пересчитываем глобальный стоп-лосс
                await CheckGlobalStopLossAsync();



                //Debug.WriteLine($"DEBUG: CurrentPrice обновлен: {CurrentPrice}");
                //Debug.WriteLine($"DEBUG: Свеча (локальное время): " +
                //               $"Time={_currentCandle.Time:HH:mm:ss}, " +
                //               $"O={_currentCandle.Open}, H={_currentCandle.High}, " +
                //               $"L={_currentCandle.Low}, C={_currentCandle.Close}, " +
                //               $"V={_currentCandle.Volume}");

                // Обновляем в базе данных
                await SaveCurrentCandleAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка обновления текущей свечи: {ex.Message}");
                Debug.WriteLine($"DEBUG: StackTrace: {ex.StackTrace}");
            }
        }*/
        //  метод для обновления текущей формирующейся свечи:
        private async Task UpdateCurrentCandle(CandleUpdate candleUpdate)
        {
            // Добавьте проверку на disposed
            if (_disposed /*|| _isConnectionLost*/)
            {
                Debug.WriteLine($"DEBUG: Стратегия {_instrument.Ticker} уничтожена или отключена, игнорируем обновление");
                return;
            }

            try
            {
                // Обновляем текущую цену
                CurrentPrice = candleUpdate.LastPrice;

                // Если стратегия запущена, обрабатываем новые данные
                if (IsRunning)
                {
                    await ProcessMarketDataAsync(candleUpdate.LastPrice);
                }

                // Определяем время начала текущей свечи в локальном времени
                var currentLocalTime = DateTime.Now;
                var currentCandleStartTime = GetCandleStartTime(currentLocalTime, CurrentTimeframe);

                // ✅ ИСПРАВЛЕНИЕ: Проверяем, нужно ли создавать новую свечу
                if (_currentCandle == null ||
                    GetCandleStartTime(_currentCandle.Time, CurrentTimeframe) != currentCandleStartTime)
                {
                    // Закрываем предыдущую свечу, если она существует
                    if (_currentCandle != null && !_currentCandle.IsClosed)
                    {
                        _currentCandle.IsClosed = true;
                        // Сохраняем завершенную свечу
                        await SaveCandlesAsync(_instrument.Ticker, CurrentTimeframe,
                            new List<Models.Candle> { _currentCandle });
                        //Debug.WriteLine($"DEBUG: Закрыта предыдущая свеча в {_currentCandle.Time} " +
                        //               $"(объем: {_currentCandle.Volume})");
                    }

                    // ✅ ИСПРАВЛЕНИЕ: Создаем НОВУЮ свечу с правильными начальными значениями
                    // Открытие свечи = текущая цена (первый тик)
                    // High = Low = Close = текущая цена
                    decimal currentPrice = candleUpdate.LastPrice;

                    _currentCandle = new Models.Candle
                    {
                        Ticker = _instrument.Ticker,
                        Timeframe = CurrentTimeframe,
                        Time = currentCandleStartTime,
                        Open = currentPrice,           // Открытие = текущая цена
                        High = currentPrice,            // High = текущая цена
                        Low = currentPrice,             // Low = текущая цена
                        Close = currentPrice,           // Close = текущая цена
                        Volume = 0,                     // Объем начинаем с 0
                        IsClosed = false
                    };

                    //Debug.WriteLine($"DEBUG: {_instrument.Ticker} Создана НОВАЯ свеча с локальным временем начала {currentCandleStartTime}");
                    //Debug.WriteLine($"DEBUG: Начальные значения свечи: O={_currentCandle.Open:F2}, H={_currentCandle.High:F2}, L={_currentCandle.Low:F2}, C={_currentCandle.Close:F2}");
                }
                else
                {
                    // ✅ ИСПРАВЛЕНИЕ: Обновляем параметры текущей свечи
                    // Close всегда обновляем на последнюю цену
                    _currentCandle.Close = candleUpdate.LastPrice;

                    // Обновляем High (максимум) - берем максимум из текущего High и новой цены
                    if (candleUpdate.LastPrice > _currentCandle.High)
                    {
                        _currentCandle.High = candleUpdate.LastPrice;
                        //Debug.WriteLine($"DEBUG: Обновлен High: {_currentCandle.High:F2}");
                    }

                    // Обновляем Low (минимум) - берем минимум из текущего Low и новой цены
                    if (candleUpdate.LastPrice < _currentCandle.Low)
                    {
                        _currentCandle.Low = candleUpdate.LastPrice;
                        //Debug.WriteLine($"DEBUG: Обновлен Low: {_currentCandle.Low:F2}");
                    }

                    // Обновляем объем
                    if (candleUpdate.Volume > 0)
                    {
                        // Если это обновление свечи с объемом, устанавливаем точный объем
                        if (candleUpdate.Open > 0 && candleUpdate.Close > 0)
                        {
                            _currentCandle.Volume = (long)candleUpdate.Volume;
                        }
                        else
                        {
                            // Это тик - добавляем объем
                            _currentCandle.Volume += (long)candleUpdate.Volume;
                           // Debug.WriteLine($"DEBUG: Объем увеличен из тика: +{candleUpdate.Volume} = {_currentCandle.Volume}");
                        }
                    }
                }

                CurrentPrice = candleUpdate.LastPrice;

                // при изменении цены пересчитываем глобальный стоп-лосс
                //await CheckGlobalStopLossAsync();

                // Отладочный вывод
                //Debug.WriteLine($"DEBUG:{_instrument.Ticker} Свеча (локальное время): " +
                //               $"Time={_currentCandle.Time:HH:mm:ss}, " +
                //               $"O={_currentCandle.Open:F2}, H={_currentCandle.High:F2}, " +
                //               $"L={_currentCandle.Low:F2}, C={_currentCandle.Close:F2}, " +
                 //              $"V={_currentCandle.Volume}");

                // Сохраняем текущую свечу в базу данных
                await SaveCurrentCandleAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка обновления текущей свечи: {ex.Message}");
                Debug.WriteLine($"DEBUG: StackTrace: {ex.StackTrace}");
            }
        }

        //  вспомогательный метод для определения времени начала свечи:
        private DateTime GetCandleStartTime(DateTime time, string timeframe)
        {
            // Приводим время к локальному (Московскому) времени для расчетов
            DateTime localTime;

            if (time.Kind == DateTimeKind.Utc)
            {
                localTime = ConvertUtcToLocalTime(time);
            }
            else
            {
                localTime = time;
            }

            switch (timeframe?.ToLower())
            {
                case "1min":
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        localTime.Hour, localTime.Minute, 0, DateTimeKind.Local);

                case "5min":
                    var minute5 = localTime.Minute - (localTime.Minute % 5);
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        localTime.Hour, minute5, 0, DateTimeKind.Local);

                case "15min":
                    var minute15 = localTime.Minute - (localTime.Minute % 15);
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        localTime.Hour, minute15, 0, DateTimeKind.Local);

                case "30min":
                    var minute30 = localTime.Minute - (localTime.Minute % 30);
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        localTime.Hour, minute30, 0, DateTimeKind.Local);

                case "1hour":
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        localTime.Hour, 0, 0, DateTimeKind.Local);

                case "2hour":
                    var hour2 = localTime.Hour - (localTime.Hour % 2);
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        hour2, 0, 0, DateTimeKind.Local);

                case "4hour":
                    var hour4 = localTime.Hour - (localTime.Hour % 4);
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        hour4, 0, 0, DateTimeKind.Local);

                case "1day":
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        0, 0, 0, DateTimeKind.Local);

                default:
                    return new DateTime(localTime.Year, localTime.Month, localTime.Day,
                        localTime.Hour, localTime.Minute, 0, DateTimeKind.Local);
            }
        }
        #endregion

        private void UpdateStrategyMarketStatuses(List<MarketStatus> statuses)
        {
            try
            {
                // Обновляем статусы в UI этого окна (если есть соответствующая UI логика)
                Debug.WriteLine($"StrategyViewModel: Получены обновления статусов рынков: {statuses?.Count ?? 0}");

                // Можно добавить логику для отображения статусов в окне стратегии
                // Например, показать статус в Status или в дополнительном поле
                if (statuses != null && statuses.Count > 0)
                {
                    foreach (var status in statuses)
                    {
                        Debug.WriteLine($"  {status.Name}: {status.Status} (Торги: {status.IsTrading})");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка в UpdateStrategyMarketStatuses: {ex.Message}");
            }
        }
        public async Task StartStrategy()
        {
            try
            {
                if (!IsRunning)
                {

                    // ✅ ПРОВЕРЯЕМ, НЕ БЭКТЕСТ-ЛИ ЭТО
                    if (_pairsStrategy != null && _pairsStrategy.IsBacktestMode)
                    {
                        // В бэктест-режиме НЕ вызываем события, которые триггерят MainViewModel
                        Debug.WriteLine($"DEBUG: Бэктест-режим, пропускаем события");

                        // Запускаем стратегию без вызова StrategyStarted
                        switch (_strategy.Type)
                        {
                            case "PairsTrading":
                                if (_pairsStrategy != null)
                                {
                                    await _pairsStrategy.StartAsync();
                                    IsRunning = true;
                                    Status = "Работает (бэктест)";
                                }
                                break;
                        }
                        return;
                    }




                    switch (_strategy.Type)
                    {
                        case "RSI":
                            if (_rsiStrategy != null)
                            {
                                await _rsiStrategy.StartAsync();
                                IsRunning = true;
                                Status = "Работает";
                                Debug.WriteLine($"DEBUG: RSI strategy started");
                            }
                            break;

                        case "MA":
                            if (_maStrategy != null)
                            {
                                await _maStrategy.StartAsync();
                                IsRunning = true;
                                Status = "Работает";
                                Debug.WriteLine($"DEBUG: MA strategy started");
                            }
                            break;

                        case "Manual":
                            if (_manualStrategy != null)
                            {
                                await _manualStrategy.StartAsync();
                                IsRunning = true;
                                Status = "Работает";
                                Debug.WriteLine($"DEBUG: Manual strategy started");
                            }
                            break;
                        
                        case "Rating":
                            if (_ratingStrategy != null)
                            {
                                await _ratingStrategy.StartAsync();
                                IsRunning = true;
                                Status = "Работает";
                                Debug.WriteLine($"DEBUG: Rating strategy started");
                            }
                            break;

                        case "PairsTrading":
                            if (_pairsStrategy != null)
                            {
                                await _pairsStrategy.StartAsync();
                                IsRunning = true;
                                Status = "Работает";
                                Debug.WriteLine($"DEBUG: PairsTrading strategy started");
                            }
                            break;
                    }

                    IsRunning = true;
                    Status = "Работает";

                    // ✅ ВЫЗЫВАЕМ СОБЫТИЕ ПОСЛЕ УСПЕШНОГО ЗАПУСКА
                    StrategyStarted?.Invoke();

                    Debug.WriteLine($"DEBUG: Strategy {_strategy.Name} started");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error starting strategy: {ex.Message}");
                Status = $"Ошибка запуска: {ex.Message}";
            }
        }
        private async Task StopStrategy()
        {
            try
            {
                if (IsRunning)
                {
                    switch (_strategy.Type)
                    {
                        case "RSI":
                            if (_rsiStrategy != null)
                            {
                                await _rsiStrategy.StopAsync();
                            }
                            break;

                        case "MA":
                            if (_maStrategy != null)
                            {
                                await _maStrategy.StopAsync();
                            }
                            break;

                        case "Manual":
                            if (_manualStrategy != null)
                            {
                                await _manualStrategy.StopAsync();
                            }
                            break;
                        
                        case "Rating":
                            if (_ratingStrategy != null)
                            {
                                await _ratingStrategy.StopAsync();
                            }
                            break;

                        case "PairsTrading":
                            if (_pairsStrategy != null)
                            {
                                await _pairsStrategy.StopAsync();
                            }
                            break;
                    }

                    IsRunning = false;
                    Status = "Остановлена";
                    Debug.WriteLine($"DEBUG: Strategy {_strategy.Name} stopped");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Error stopping strategy: {ex.Message}");
                Status = $"Ошибка остановки: {ex.Message}";
            }
        }

        #region Реконнект и переподписка
        // ✅ НОВЫЙ МЕТОД: Только обновляем UI статус, НЕ трогаем работу стратегии
        private void OnConnectionStateChanged(bool isConnected)
        {
            // Просто обновляем статус в UI, не останавливая и не запуская стратегию
            App.Current.Dispatcher.Invoke(() =>
            {
                if (IsRunning)
                {
                    Status = isConnected ? "Работает" : "Работает (нет соединения)";

                    // Меняем цвет статуса для наглядности
                    if (!isConnected)
                    {
                        Status = "Работает (ожидание соединения...)";
                    }
                }
            });

            Debug.WriteLine($"DEBUG: Стратегия {_instrument.Ticker}: статус соединения = {isConnected}, стратегия {(IsRunning ? "продолжает работу" : "остановлена")}");
        }

        // Для проверки позиции в стратегии
        public async Task<decimal> GetCurrentPositionQuantity()
        {
            /* try
             {
                 if (_providerService is TinkoffApiService tinkoffService && Convert.ToString(_providerService.GetAccountsAsync()) != null)
                 {

                     var positionTemp = tinkoffService.GetPositionQuantity(Convert.ToString(_providerService.GetAccountsAsync().Id), _instrument.Uid);

                     CurrentUpdPositions = Convert.ToInt32(positionTemp);

                     return positionTemp;
                 }
             }
             catch (Exception ex)
             {
                 Debug.WriteLine($"Ошибка получения позиции: {ex.Message}");
             }*/



            CurrentUpdPositions = Convert.ToInt32(await _providerService.RefreshPositionsAsync());

            Debug.WriteLine($"   - CurrentUpdPositions -   {CurrentUpdPositions} ---------------------");

            return CurrentUpdPositions;
        }
        #endregion

        #region Вспомогательные методы
        // Метод для конвертации времени UTC в локальное (Московское)
        private DateTime ConvertUtcToLocalTime(DateTime utcTime)
        {
            try
            {
                if (_useLocalTimeZone && utcTime.Kind == DateTimeKind.Utc)
                {
                    return TimeZoneInfo.ConvertTimeFromUtc(utcTime, _moscowTimeZone);
                }
                return utcTime;
            }
            catch
            {
                return utcTime;
            }
        }

        // Метод для конвертации локального времени в UTC
        private DateTime ConvertLocalToUtcTime(DateTime localTime)
        {
            try
            {
                if (_useLocalTimeZone && localTime.Kind != DateTimeKind.Utc)
                {
                    return TimeZoneInfo.ConvertTimeToUtc(localTime, _moscowTimeZone);
                }
                return localTime;
            }
            catch
            {
                return localTime;
            }
        }

        // Метод нормализации таймфреймов для сравнения:
        private string NormalizeTimeframeForComparison(string timeframe)
        {
            if (string.IsNullOrEmpty(timeframe))
                return timeframe;

            // Приводим к нижнему регистру и убираем все не-буквенные символы
            var normalized = timeframe.ToLower()
                .Replace(" ", "")
                .Replace("_", "")
                .Replace("-", "");

            // Специальная обработка для таймфреймов Tinkoff
            if (normalized.Contains("minute"))
            {
                if (normalized.Contains("one") || normalized.Contains("1"))
                    return "1min";
                if (normalized.Contains("five") || normalized.Contains("5"))
                    return "5min";
                if (normalized.Contains("ten") || normalized.Contains("10"))
                    return "15min";
                if (normalized.Contains("fifteen") || normalized.Contains("15"))
                    return "15min";
                if (normalized.Contains("thirty") || normalized.Contains("30"))
                    return "30min";
            }
            else if (normalized.Contains("hour"))
            {
                if (normalized.Contains("one") || normalized.Contains("1"))
                    return "1hour";
                if (normalized.Contains("two") || normalized.Contains("2"))
                    return "2hour";
                if (normalized.Contains("four") || normalized.Contains("4"))
                    return "4hour";
            }
            else if (normalized.Contains("day"))
            {
                return "1day";
            }

            return normalized;
        }
        #endregion

        #region Работа с графиком
        [RelayCommand]
        private void ShowChart()
        {
            try
            {
                var chartWindow = new Views.ChartWindow(this, _instrument, _timeFrame.Value);
                chartWindow.Owner = Application.Current.MainWindow;
                chartWindow.Show();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка открытия графика: {ex.Message}");
                MessageBox.Show($"Ошибка открытия графика: {ex.Message}", "Ошибка",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region Оптимизация
        private void OpenOptimizationWindow()
        {
            try
            {
                // Создаем ViewModel для оптимизации
                var optimizationVM = new OptimizationViewModel(
                    this,
                    _providerService,
                    _logger);

                // Подписываемся на событие применения параметров
                optimizationVM.ParametersApplied += (paramsDict) =>
                {
                    Debug.WriteLine($"[StrategyViewModel] Параметры применены: {paramsDict.Count}");
                    // После применения параметров обновляем UI стратегии
                    _maStrategy?.OnParametersChanged();
                };

                // Создаем окно
                var window = new OptimizationWindow(optimizationVM);

                //window.Owner = Application.Current.MainWindow;
                //window.ShowDialog();

                // ✅ ИЗМЕНЕНИЕ: Устанавливаем владельца - текущее окно стратегии
                // ✅ ИСПОЛЬЗУЕМ СОХРАНЕННОГО ВЛАДЕЛЬЦА
                if (_ownerWindow != null)
                {
                    window.Owner = _ownerWindow;
                }



                // ✅ ИЗМЕНЕНИЕ: Сохраняем ссылку на ViewModel для правильного освобождения
                var vmRef = optimizationVM;

                // ✅ ИЗМЕНЕНИЕ: Подписываемся на событие закрытия для освобождения ресурсов
                window.Closed += (s, e) =>
                {
                    Debug.WriteLine($"[StrategyViewModel] Окно оптимизации закрыто");

                    // ✅ ПРАВИЛЬНОЕ ОСВОБОЖДЕНИЕ РЕСУРСОВ
                    try
                    {
                        // Отписываемся от событий, чтобы избежать утечек памяти
                        if (vmRef != null)
                        {
                            // Очищаем все ссылки на события
                            vmRef.ParametersApplied -= null;

                            // Вызываем Dispose
                            vmRef.Dispose();

                            Debug.WriteLine($"[StrategyViewModel] OptimizationViewModel успешно освобожден");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[StrategyViewModel] Ошибка при освобождении OptimizationViewModel: {ex.Message}");
                    }
                };


                // Также подписываемся на событие закрытия окна приложения
                // чтобы освободить ресурсы, если окно оптимизации не было закрыто
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    void OnMainWindowClosed(object sender, EventArgs args)
                    {
                        Debug.WriteLine("[StrategyViewModel] Главное окно закрывается, освобождаем OptimizationViewModel...");
                        try
                        {
                            if (vmRef != null && !vmRef.IsDisposed)
                            {
                                vmRef.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[StrategyViewModel] Ошибка при освобождении OptimizationViewModel при закрытии главного окна: {ex.Message}");
                        }
                        mainWindow.Closed -= OnMainWindowClosed;
                    }
                    mainWindow.Closed += OnMainWindowClosed;
                }


                // ✅ ИЗМЕНЕНИЕ: Используем Show() вместо ShowDialog()
                // Это позволяет окну оптимизации блокировать только владельца (окно стратегии),
                // но не блокировать другие окна приложения
                window.Show();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка открытия окна оптимизации");
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }





        #endregion




        /// <summary>
        /// Принудительное обновление UI
        /// </summary>
        public void RaisePropertyChanged(string propertyName)
        {
            OnPropertyChanged(propertyName);
        }




        private async Task GetDescriptionDtrategy()
        {
            Debug.WriteLine($"__________DESCRIPTION");

            try
            {
                // Определяем тип текущей стратегии
                string strategyType = _strategy.Type;

                // Получаем описание из словаря
                var description = StrategyDescriptions.GetDescription(strategyType);

                // Показываем модальное окно в UI потоке
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        description,
                        $"Описание стратегии: {_strategy.Name}",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error showing strategy description");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            try
            {
                // 1. СНАЧАЛА останавливаем получение обновлений свечей
                if (/*_isSubscribedToCandles &&*/ _providerService != null && !string.IsNullOrEmpty(_instrument.Uid))
                {
                    try
                    {
                        await _providerService.UnsubscribeFromCandlesAsync(_instrument.Uid, CurrentTimeframe);
                        Debug.WriteLine($"DEBUG: Отписались от свечей при закрытии стратегии {_instrument.Ticker}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: Ошибка отписки от свечей: {ex.Message}");
                    }
                    _isSubscribedToCandles = false;
                }


                if (_candleUpdateCts != null)
                {
                    _candleUpdateCts.Cancel();
                    _candleUpdateCts.Dispose();
                    _candleUpdateCts = null;
                }




                // 2. Отписываемся от событий
                if (_providerService is TinkoffApiService tinkoffService)
                {
                    tinkoffService.OnMarketStatusesUpdated -= UpdateStrategyMarketStatuses;
                }

                // 3. Останавливаем стратегию
                if (IsRunning)
                {
                    await StopStrategy();
                }

                // 4. Очищаем текущую свечу, чтобы предотвратить дальнейшие обращения
                _currentCandle = null;

                // 5. Освобождаем ресурсы стратегий
                switch (_strategy.Type)
                {
                    case "RSI":
                        if (_rsiStrategy != null)
                        {
                            if (_rsiStrategy is IAsyncDisposable asyncDisposable)
                                await asyncDisposable.DisposeAsync();
                            else if (_rsiStrategy is IDisposable disposable)
                                disposable.Dispose();
                        }
                        break;

                    case "MA":
                        if (_maStrategy != null)
                        {
                            if (_maStrategy is IAsyncDisposable asyncDisposable)
                                await asyncDisposable.DisposeAsync();
                            else if (_maStrategy is IDisposable disposable)
                                disposable.Dispose();
                        }
                        break;

                    case "Manual":
                        if (_manualStrategy != null)
                        {
                            if (_manualStrategy is IAsyncDisposable asyncDisposable)
                                await asyncDisposable.DisposeAsync();
                            else if (_manualStrategy is IDisposable disposable)
                                disposable.Dispose();
                        }
                        break;

                    case "PairsTrading":
                        if (_pairsStrategy != null)
                        {
                            // ✅ Сохраняем текущие параметры перед закрытием
                            var strategy = _pairsStrategy;
                            if (strategy.Parameters != null)
                            {
                                // Параметры уже сохранены в _parameters
                                Debug.WriteLine($"[PairsTrading] Сохранение параметров перед закрытием: " +
                                               $"A={strategy.Parameters.FirstInstrumentTicker}, " +
                                               $"B={strategy.Parameters.PairInstrumentTicker}");
                            }

                            if (_pairsStrategy is IAsyncDisposable asyncDisposable)
                                await _pairsStrategy.DisposeAsync();
                            else if (_pairsStrategy is IDisposable disposable)
                                disposable.Dispose();
                        }
                        break;
                }

                // 6. Закрываем соединение с БД
                if (_connection != null)
                {
                    if (_connection.State != System.Data.ConnectionState.Closed)
                    {
                        await _connection.CloseAsync();
                    }
                    await _connection.DisposeAsync();
                }

                // 7. Отписываемся от менеджера соединений
                _connectionManager.OnConnectionStateChanged -= OnConnectionStateChanged;
                _connectionManager?.UnregisterStrategy(this);

                Debug.WriteLine($"DEBUG: Ресурсы стратегии {_instrument.Ticker} успешно освобождены");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка при очистке ресурсов: {ex.Message}");
            }

            _disposed = true;
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }
    }
}