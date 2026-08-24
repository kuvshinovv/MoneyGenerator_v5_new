using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Strategies;
using MoneyGenerator_v5.ViewModels;
using ScottPlot;
using Skender.Stock.Indicators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Tinkoff.InvestApi.V1;
using static SkiaSharp.HarfBuzz.SKShaper;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;


namespace MoneyGenerator_v5.Strategies
{
    #region Enums
    public enum OrderType
    {
        Market,
        Limit,
        StopLimit,
        MovingTakeProfitEntry, // Скользящий тейк-профит на входе
        MovingTakeProfitExit,  // Скользящий тейк-профит на выходе
        TrailingStopExit,       // Трейлинг-стоп на выходе
        LevelCrossingEntry,    // ВХОД ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ 
        LevelCrossingExit      // ВЫХОД ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ 
    }

    public enum TakeProfitType
    {
        Fixed,
        Trailing,
        LimitOrder,
        Market
    }

    public enum PriceCalculationType
    {
        Percentage,
        Absolute,
        ATR
    }

    public enum OscillatorType
    {
        StochRSI,
        Stochastic
    }

    public enum EntryState
    {
        NoSignal,                   // Нет сигнала
        WaitingForEntry,            // Ожидание условий входа
        MovingTPEntryActive,        // Активен скользящий тейк-профит на входе
        MovingTPExitActive,         // Активен скользящий тейк-профит на выходе
        OrderPending,               // Ордер на вход выставлен
        EntryFailed                 // Вход не удался
    }

    public enum ExitState
    {
        NoPosition,                 // Нет позиции
        PositionActive,             // Позиция активна
        TrailingStopActive,         // Трейлинг-стоп активен
        MovingTPExitActive,         // Активен скользящий тейк-профит на выходе
        ExitPending,                // Ордер на выход выставлен
        ExitFailed                  // Выход не удался
    }
    #endregion

    public partial class RsiStrategy
    {
        #region Поля и свойства
        public string Name => "RSI Осцилляторы Pro";
        public string Type => "RSI";
        public StrategyState State { get; set; } = StrategyState.Stopped;

        private readonly ILogger _logger;
        private readonly IProvirerService _provider;
        private readonly TransactionsService _transactionsService;
        private readonly RsiStrategyParameters _parameters;
        private readonly RsiIndicatorValues _indicatorValues;
        private readonly StrategyViewModel _strategyViewModel;
        protected string _selectedAccountId;

        private string _currentInstrumentUid; // Добавляем поле для отслеживания инструмента
        private string _currentInstrumentTicker;
        private Models.Instrument _instrument;
        private string _timeframe;
        private decimal _lastPrice = 0;
        private decimal _minStepPrice;
        private Position _currentPosition = null;
        private Models.Order _pendingOrder = null;
        //private Models.Order _activeStopLossOrder = null;
        //private Models.Order _activeTakeProfitOrder = null;
        private DateTime _lastPositionCheck = DateTime.MinValue;
        private Models.Deal _dealForExit = null;

        // Состояния входа и выхода
        private EntryState _entryState = EntryState.NoSignal;
        private ExitState _exitState = ExitState.NoPosition;

        // Переменные для скользящего тейк-профита на ВХОДЕ
        private decimal _movingTPEntryStartPrice = 0;        // Цена при начале мониторинга для входа
        private decimal _movingTPEntryCurrentLevel = 0;      // Текущий уровень скользящего TP на входе
        private decimal _movingTPEntryTargetPrice = 0;       // Цена для входа по скользящему TP
        private DateTime _movingTPEntryStartTime = DateTime.MinValue;

        // Переменные для скользящего тейк-профита на ВЫХОДЕ
        private decimal _movingTPExitStartPrice = 0;         // Цена при открытии позиции
        private decimal _movingTPExitCurrentLevel = 0;       // Текущий уровень скользящего TP на выходе
        private decimal _movingTPExitTargetPrice = 0;        // Цена для выхода по скользящему TP
        private DateTime _movingTPExitStartTime = DateTime.MinValue;

        // Переменные для трейлинг-стопа на выходе
        private decimal _trailingStopExitStartPrice = 0;     // Цена при открытии позиции
        private decimal _trailingStopExitCurrentLevel = 0;   // Текущий уровень трейлинг-стопа
        private decimal _trailingStopExitTargetPrice = 0;    // Цена для выхода по трейлинг-стопу
        private decimal _trailingStopExitBestPrice = 0;      // Лучшая цена после открытия позиции
        private bool _trailingStopExitActivated = false;

        // Для отслеживания пересечений уровней
        private bool _wasOscillatorAboveOverbought = false;
        private bool _wasOscillatorBelowOversold = false;
        private decimal _previousOscillatorValueForCrossing = 0;

        // Для защитного стоп-лосса при входе по пересечению уровня
        private decimal _levelCrossingEntryStopLossPrice = 0;
        private decimal _levelCrossingEntryTakeProfitPrice = 0;
        private bool _levelCrossingEntryProtectiveStopActive = false;

        // Для защитного стоп-лосса при выходе по пересечению уровня
        private decimal _levelCrossingExitStopLossPrice = 0;
        private bool _levelCrossingExitProtectiveStopActive = false;
        // Для отслеживания пересечений уровней (добавьте в секцию полей)
        private decimal _lastOscillatorValueForCrossing = 0;
        private bool _hasPreviousOscillatorValue = false;


        private readonly ConcurrentQueue<Models.Candle> _candleBuffer = new();
        private const int MAX_BUFFER_SIZE = 1000;
        private DateTime _lastProcessedCandleTime = DateTime.MinValue;
        public bool _entryPass = true;
        public bool _exitPass = true;

        // События для уведомления UI об изменениях
        public event Action<string> OnEntryStatusChanged;
        public event Action<string> OnExitStatusChanged;
        public event Action<string> OnOrderStatusChanged;
        public event Action<string> OnStrategyStatusChanged;

        // Защита от повторных операций
        private DateTime _lastEntryTime = DateTime.MinValue;
        private DateTime _lastExitTime = DateTime.MinValue;
        private const int ENTRY_COOLDOWN_SECONDS = 15; // 15 секунд между входами
        private const int EXIT_COOLDOWN_SECONDS = 15;  // 15 секунд между выходами


        private decimal positionValueMoney = 0;
        private decimal lotPositionSize = 0;

        private DateTime _lastEntryCheckTime = DateTime.MinValue;
        private const int ENTRY_CHECK_INTERVAL_MS = 1000; // 1 секунда
        bool shouldExitByMovingTPExitByChangeSignal = false;
        bool shouldExitByMovingTPExit = false;


        public RsiStrategyParameters Parameters => _parameters;

        // константа для минимальной прибыли
        private const decimal MIN_PROFIT_FOR_MOVING_TP_EXIT = 0.1m; // 0.1% минимальная прибыль для активации
        #endregion

        #region Конструктор и инициализации
        public RsiStrategy(
            ILogger<RsiStrategy> logger,
            IProvirerService provider,
            StrategyViewModel strategyViewModel,
            TransactionsService transactionsService,
            MainViewModel mainViewModel = null)
        {
            // ✅ ИСПРАВЛЕНИЕ: Проверка на null с созданием NullLogger если нужно
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RsiStrategy>.Instance;
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _strategyViewModel = strategyViewModel ?? throw new ArgumentNullException(nameof(strategyViewModel));
            _parameters = new RsiStrategyParameters();
            _indicatorValues = new RsiIndicatorValues();
            _transactionsService = transactionsService ?? throw new ArgumentNullException();


            // ✅ ИСПРАВЛЕНИЕ: Создаем логгер для TransactionsService с проверкой
            ILogger<TransactionsService> transactionsLogger;
            // Пробуем получить ILoggerFactory из serviceProvider или создаем новый
            ILoggerFactory loggerFactory = null;
            try
            {
                // Пытаемся получить логгер фабрику через DI (если доступно)
                var serviceProvider = App.ServiceProvider;
                if (serviceProvider != null)
                {
                    loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                }
            }
            catch { }

            if (loggerFactory != null)
            {
                transactionsLogger = loggerFactory.CreateLogger<TransactionsService>();
            }
            else
            {
                // Создаем временную фабрику логгеров
                loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.AddDebug();
                    builder.AddConsole();
                });
                transactionsLogger = loggerFactory.CreateLogger<TransactionsService>();
            }

            if (transactionsLogger == null)
            {
                transactionsLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionsService>.Instance;
            }




            // Инициализация TransactionsService
            /*_transactionsService = new TransactionsService(
                provider,
                mainViewModel,
                strategyViewModel,
                _strategyViewModel.Instrument,
                mainViewModel.SelectedAccount,
                logger);*/

            _parameters.OnParametersChanged += OnParametersChanged;

            // Инициализация привязок для UI
            InitializeIndicatorBindings();
        }

        private void InitializeIndicatorBindings()
        {
            // Привязка событий к обновлению UI значений
            OnEntryStatusChanged += (status) => {
                _indicatorValues.EntryStatus = status;
                _indicatorValues.EntryStatusDetails = GetEntryStatusDetails();
            };

            OnExitStatusChanged += (status) => {
                _indicatorValues.ExitStatus = status;
                _indicatorValues.ExitStatusDetails = GetExitStatusDetails();
            };

            OnOrderStatusChanged += (status) => {
                _indicatorValues.OrderStatus = status;
            };

            OnStrategyStatusChanged += (status) => {
                _indicatorValues.StrategyStatus = status;
            };
        }

        public async Task InitializeAsync(Models.Instrument instrument, string timeframe)
        {
            _instrument = instrument;
            _timeframe = timeframe;


            // Получаем первый доступный счет
            var accounts = await _provider.GetAccountsAsync();
            if (accounts.Any())
            {
                _selectedAccountId = accounts.First().Id;
            }



            _currentInstrumentUid = instrument.Uid; // Сохраняем UID
            _currentInstrumentTicker = instrument.Ticker; // Сохраняем тикер
            await LoadHistoricalDataAsync();
        }

        private void InitializeMovingTPExit()
        {
            if (_currentPosition == null) return;

            try
            {

                // Для ВЫХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
                if (_parameters.ExitOrderType == OrderType.LevelCrossingExit)
                {
                    _exitState = ExitState.MovingTPExitActive;

                    // Активируем защитный стоп-лосс для выхода
                    _levelCrossingExitProtectiveStopActive = true;
                    _levelCrossingExitStopLossPrice = CalculateLevelCrossingExitStopLoss(
                        _currentPosition.Direction,
                        _currentPosition.EntryPrice
                    );

                    string directionText = _currentPosition.Direction == PositionDirection.Long ? "лонга" : "шорта";
                    string levelText = _currentPosition.Direction == PositionDirection.Long
                        ? $"пересечение {_parameters.StochOverbought:F1} СВЕРХУ ВНИЗ"
                        : $"пересечение {_parameters.StochOversold:F1} СНИЗУ ВВЕРХ";

                    OnExitStatusChanged?.Invoke($"Выход по пересечению уровня активирован для {directionText}. \nОжидание: {levelText}. Защитный стоп: {_levelCrossingExitStopLossPrice:F2}");
                    _indicatorValues.LastAction = $"Выход по пересечению уровня активирован. \nЗащитный стоп: {_levelCrossingExitStopLossPrice:F2}";

                    _logger.LogInformation($"Level crossing exit activated for {_currentPosition.Direction}. \nStop loss: {_levelCrossingExitStopLossPrice:F2}");
                    return;
                }






                // Существующая логика для MovingTakeProfitExit
                if (_parameters.ExitOrderType == OrderType.MovingTakeProfitExit)
                {
                    _exitState = ExitState.MovingTPExitActive;

                    // Для начала отслеживания используем текущую цену
                    _movingTPExitCurrentLevel = _lastPrice;
                    _movingTPExitStartPrice = _currentPosition.EntryPrice;

                    // Рассчитываем начальный целевой уровень
                    decimal offset = CalculateMovingTPExitOffset();

                    if (_currentPosition.Direction == PositionDirection.Long)
                    {
                        // Для лонга: цель ниже текущего уровня
                        _movingTPExitTargetPrice = _lastPrice - offset;
                    }
                    else if (_currentPosition.Direction == PositionDirection.Short)
                    {
                        // Для шорта: цель выше текущего уровня
                        _movingTPExitTargetPrice = _lastPrice + offset;
                    }

                    _movingTPExitStartTime = DateTime.Now;

                    Debug.WriteLine($"Инициализация скользящего TP выхода: Entry={_currentPosition.EntryPrice:F2}, " +
                                   $"Current={_lastPrice:F2}, Target={_movingTPExitTargetPrice:F4}, " +
                                   $"Direction={_currentPosition.Direction}, Offset={offset:F4}");

                    _logger.LogInformation($"Скользящий TP на выходе активирован для {_currentPosition.Direction}. " +
                                          $"Entry: {_currentPosition.EntryPrice:F2}, Current: {_lastPrice:F2}, " +
                                          $"Target: {_movingTPExitTargetPrice:F2}");

                    OnExitStatusChanged?.Invoke($"Скользящий TP активирован: {_movingTPExitTargetPrice:F2}");
                    _indicatorValues.LastAction = $"Скользящий TP на выходе активирован. Цель: {_movingTPExitTargetPrice:F2}";
                }
                else if (_parameters.ExitOrderType == OrderType.TrailingStopExit)
                {
                    _trailingStopExitStartPrice = _currentPosition.EntryPrice;
                    _trailingStopExitBestPrice = _currentPosition.EntryPrice;
                    _trailingStopExitCurrentLevel = CalculateTrailingStopExitLevel(
                        _currentPosition.Direction,
                        _currentPosition.EntryPrice
                    );
                    _trailingStopExitActivated = false; // Ждем активации по прибыли
                    _exitState = ExitState.TrailingStopActive;

                    decimal protectiveStopLevel = 0;
                    if (_currentPosition.Direction == PositionDirection.Long)
                    {
                        protectiveStopLevel = _currentPosition.EntryPrice * (1 - _parameters.ProtectiveStopPercent / 100);
                    }
                    else if (_currentPosition.Direction == PositionDirection.Short)
                    {
                        protectiveStopLevel = _currentPosition.EntryPrice * (1 + _parameters.ProtectiveStopPercent / 100);
                    }

                    OnExitStatusChanged?.Invoke($"⏳ Трейлинг-стоп ожидает активации после {_parameters.TrailingStopExitActivationPercent}% прибыли (защитный стоп: {protectiveStopLevel:F2})");
                }
                else
                {
                    _exitState = ExitState.PositionActive;
                    OnExitStatusChanged?.Invoke("Позиция активна");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка инициализации механизма выхода");
                _exitState = ExitState.PositionActive;
            }
        }
        #endregion

        #region Методы жизненного цикла стратегии
        public async Task StartAsync()
        {
            State = StrategyState.Running;
            _indicatorValues.StrategyStatus = "РАБОТАЕТ";
            _indicatorValues.StrategyStatusColor = Brushes.Green;
            OnStrategyStatusChanged?.Invoke("РАБОТАЕТ");

            _logger.LogInformation($"RSI strategy started for {_instrument.Ticker}");

            // Сброс состояний при старте
            _entryState = EntryState.NoSignal;
            _exitState = ExitState.NoPosition;

            // ✅ Сброс переменных для пересечения уровней
            _wasOscillatorAboveOverbought = false;
            _wasOscillatorBelowOversold = false;
            _previousOscillatorValueForCrossing = 0;
            _lastOscillatorValueForCrossing = 0;
            _hasPreviousOscillatorValue = false;
            _levelCrossingEntryProtectiveStopActive = false;
            _levelCrossingExitProtectiveStopActive = false;
            _levelCrossingEntryStopLossPrice = 0;
            _levelCrossingEntryTakeProfitPrice = 0;
            _levelCrossingExitStopLossPrice = 0;

            // ✅ Убеждаемся, что таблица сделок существует
            if (_strategyViewModel != null)
            {
                // Вызываем через рефлексию или добавьте публичный метод в StrategyViewModel
                var method = _strategyViewModel.GetType().GetMethod("EnsureDealsJournalTableExistsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    await (Task)method.Invoke(_strategyViewModel, null);
                }
            }


            // ВАЖНО: Инициализация переменных скользящих TP
            ResetAllMovingTPVariables();

            // Инициализация позиции если она существует
            await UpdateCurrentPositionAsync();

            // Если позиция существует, инициализируем механизмы выхода
            // Если позиция существует, инициализируем механизмы выхода
            if (_currentPosition != null && !string.IsNullOrEmpty(_currentPosition.Direction))
            {
                // ✅ КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Убеждаемся, что цена входа корректна
                if (_currentPosition.EntryPrice <= 0)
                {
                    var dealFromDb = await GetOpenDealFromDatabaseAsync(_currentInstrumentUid);
                    if (dealFromDb != null && dealFromDb.EntryPrice > 0)
                    {
                        _currentPosition.EntryPrice = dealFromDb.EntryPrice;
                        _indicatorValues.EntryPrice = dealFromDb.EntryPrice;
                        _logger.LogInformation($"StartAsync: Цена входа восстановлена из БД: {_currentPosition.EntryPrice}");
                    }
                }

                // ✅ ИНИЦИАЛИЗАЦИЯ МЕХАНИЗМА ВЫХОДА ДЛЯ ВСЕХ ТИПОВ
                InitializeMovingTPExit();
            }
            else
            {
                // Если позиция была загружена, но без направления, попробуем определить ее заново
                var positions = await _provider.GetPositionsAsync();
                var existingPos = positions?.FirstOrDefault(p => p.InstrumentUid == _instrument.Uid);

                if (existingPos != null && existingPos.Quantity != 0)
                {
                    _logger.LogInformation($"Обнаружена существующая позиция: {existingPos.Quantity} лотов, загружаем...");
                    await UpdateCurrentPositionAsync();

                    // После загрузки позиции инициализируем механизм выхода
                    if (_currentPosition != null)
                    {
                        InitializeMovingTPExit();
                    }
                }

                OnEntryStatusChanged?.Invoke("Ожидание сигнала на вход");
                OnExitStatusChanged?.Invoke(_currentPosition != null ? "Позиция активна" : "Нет позиции");
                OnOrderStatusChanged?.Invoke("Стратегия запущена, мониторинг сигналов");

                await ProcessStrategyLogicAsync();

            }
        }
        public async Task StopAsync()
        {
            State = StrategyState.Stopped;

            // Сбрасываем время последних операций
            _lastEntryTime = DateTime.MinValue;
            _lastExitTime = DateTime.MinValue;

            _indicatorValues.StrategyStatus = "ОСТАНОВЛЕНА";
            _indicatorValues.StrategyStatusColor = Brushes.Red;
            _indicatorValues.Signal = "СТОП";
            _indicatorValues.SignalColor = Brushes.Gray;
            OnStrategyStatusChanged?.Invoke("ОСТАНОВЛЕНА");

            // Отменяем pending ордер если есть
            if (_pendingOrder != null)
            {
                await _transactionsService.CancelOrderAsync(_pendingOrder.OrderId ?? _pendingOrder.Id);

                _pendingOrder = null;
            }

            // Сброс состояний при остановке
            _entryState = EntryState.NoSignal;
            _exitState = ExitState.NoPosition;

            // Сброс всех переменных скользящих тейков
            ResetAllMovingTPVariables();

            OnEntryStatusChanged?.Invoke("Стратегия остановлена");
            OnExitStatusChanged?.Invoke("Стратегия остановлена");
            OnOrderStatusChanged?.Invoke("Все ордера отменены");

            _logger.LogInformation($"RSI strategy stopped for {_instrument.Ticker}");
        }
        public async Task RestoreAsync()
        {
            await LoadHistoricalDataAsync();
            _logger.LogInformation($"RSI strategy restored for {_instrument.Ticker}");
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

        #region Управление ордерами
        /*private async Task CancelActiveOrdersAsync()
        {
            try
            {
                if (_activeStopLossOrder != null)
                {
                    await _provider.CancelOrderAsync(_activeStopLossOrder.OrderId ?? _activeStopLossOrder.Id);
                    _logger.LogInformation($"Stop-loss order cancelled for {_instrument.Ticker}");
                    _activeStopLossOrder = null;
                }

                if (_activeTakeProfitOrder != null)
                {
                    await _provider.CancelOrderAsync(_activeTakeProfitOrder.OrderId ?? _activeTakeProfitOrder.Id);
                    _logger.LogInformation($"Take-profit order cancelled for {_instrument.Ticker}");
                    _activeTakeProfitOrder = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling active orders");
            }
        }*/
        /*private async Task CancelPendingOrderAsync()
        {
            try
            {
                if (_pendingOrder != null)
                {
                    await _provider.CancelOrderAsync(_pendingOrder.OrderId ?? _pendingOrder.Id);

                    if (_entryState == EntryState.OrderPending || _entryState == EntryState.MovingTPEntryActive)
                    {
                        _entryState = EntryState.EntryFailed;
                        OnEntryStatusChanged?.Invoke("Ордер отменен");
                    }
                    else if (_exitState == ExitState.ExitPending)
                    {
                        _exitState = ExitState.ExitFailed;
                        OnExitStatusChanged?.Invoke("Ордер отменен");
                    }

                    _pendingOrder = null;
                    _indicatorValues.LastAction = "Pending ордер отменен";

                    // После отмены возвращаемся к мониторингу сигналов
                    _entryState = EntryState.NoSignal;
                    _exitState = _currentPosition != null ? ExitState.PositionActive : ExitState.NoPosition;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling pending order");
                _indicatorValues.LastAction = $"Ошибка отмены ордера: {ex.Message}";
            }
        }*/

        // Мониторинг исполнения ордеров
        private async Task CheckPendingOrderExecutionAsync()
        {
            if (_pendingOrder == null) return;

            if (_pendingOrder.InstrumentUid != _currentInstrumentUid)
            {
                Debug.WriteLine($"[{_currentInstrumentTicker}] Обнаружен pending ордер другого инструмента, пропускаем");
                _pendingOrder = null;
                return;
            }

            try
            {
                var orderStatus = await _transactionsService.GetOrderStatusAsync(_pendingOrder.OrderId ?? _pendingOrder.Id);

                bool positionExists = false;
                decimal currentPositionQuantity = 0;

                if (_exitState == ExitState.ExitPending)
                {
                    var positions = await _provider.GetPositionsAsync();
                    var position = positions?.FirstOrDefault(p => p.InstrumentUid == _currentInstrumentUid);
                    positionExists = position != null && position.Quantity != 0;
                    currentPositionQuantity = position?.Quantity ?? 0;
                }

                bool wasEntryOrder = _pendingOrder.IsEntryOrder;
                bool wasExitOrder = _pendingOrder.IsExitOrder;

                if (!wasEntryOrder && !wasExitOrder)
                {
                    wasEntryOrder = _entryState == EntryState.OrderPending || _entryState == EntryState.MovingTPEntryActive;
                    wasExitOrder = _exitState == ExitState.ExitPending;

                    _pendingOrder.IsEntryOrder = wasEntryOrder;
                    _pendingOrder.IsExitOrder = wasExitOrder;
                }

                if (wasExitOrder)
                {
                    var positions = await _provider.GetPositionsAsync();
                    var position = positions?.FirstOrDefault(p => p.InstrumentUid == _currentInstrumentUid);
                    positionExists = position != null && position.Quantity != 0;
                    currentPositionQuantity = position?.Quantity ?? 0;
                }

                bool isFilled = orderStatus == OrderStatus.Filled ||
                                (_exitState == ExitState.ExitPending && !positionExists);

                if (isFilled)
                {
                    OnOrderStatusChanged?.Invoke($"Ордер исполнен по {_pendingOrder.Price:F2}");
                    //Debug.WriteLine($"[{_currentInstrumentTicker}] Ордер исполнен! wasEntryOrder={wasEntryOrder}, wasExitOrder={wasExitOrder}");

                    if (wasEntryOrder)
                    {
                        _currentPosition = new Position
                        {
                            InstrumentUid = _instrument.Uid,
                            Direction = _pendingOrder.Direction,
                            Quantity = _pendingOrder.Quantity,
                            EntryPrice = _pendingOrder.Price,
                            BestPrice = _pendingOrder.Price,
                            EntryOrderId = _pendingOrder.OrderId ?? _pendingOrder.Id
                        };

                        _dealForExit = new Deal
                        {
                            Ticker = _instrument.Ticker,
                            InstrumentUid = _instrument.Uid,
                            Direction = _pendingOrder.Direction,
                            EntryQuantity = _pendingOrder.Quantity,
                            EntryPrice = _pendingOrder.Price,
                            EntryOrderId = _pendingOrder.OrderId ?? _pendingOrder.Id,
                            EntryReason = $"Вход по сигналу {_indicatorValues.Signal}",
                        };

                        try
                        {
                            if (_strategyViewModel != null)
                            {
                                var direction = _pendingOrder.Direction == "Buy" ? "Long" : "Short";

                                await _transactionsService.AddOpenDealAsync(
                                    ticker: _dealForExit.Ticker,
                                    instrumentUid: _dealForExit.InstrumentUid,
                                    strategy: "RSI",
                                    Convert.ToString(_strategyViewModel.SelectedTimeFrame),
                                    entryTime: DateTime.Now,
                                    entryPrice: _dealForExit.EntryPrice,
                                    entryQuantity: _dealForExit.EntryQuantity,
                                    entryOrderId: _dealForExit.EntryOrderId,
                                    direction: _dealForExit.Direction,
                                    comment: _dealForExit.EntryReason
                                );

                                Debug.WriteLine($"✅ Сделка на вход записана в журнал: {_instrument.Ticker} {direction} {_pendingOrder.Quantity} лотов по {_pendingOrder.Price}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Ошибка записи сделки в журнал при входе");
                        }

                        if (_parameters.ExitOrderType == OrderType.MovingTakeProfitExit)
                        {
                            _movingTPExitStartPrice = _pendingOrder.Price;
                            _movingTPExitCurrentLevel = _pendingOrder.Price;
                            _movingTPExitTargetPrice = CalculateMovingTPExitStartLevel(
                                _pendingOrder.Direction == "Long" ? PositionDirection.Long : PositionDirection.Short,
                                _pendingOrder.Price, _pendingOrder.Price);
                            _movingTPExitStartTime = DateTime.Now;
                            _exitState = ExitState.MovingTPExitActive;

                            Debug.WriteLine($"Инициализация скользящего TP на выходе: Entry={_pendingOrder.Price:F2}, Target={_movingTPExitTargetPrice:F4}");

                            OnExitStatusChanged?.Invoke($"Скользящий TP активен: текущая цена {_pendingOrder.Price:F2}, цель выхода {_movingTPExitTargetPrice:F2}");
                        }
                        else if (_parameters.ExitOrderType == OrderType.TrailingStopExit)
                        {
                            _trailingStopExitStartPrice = _pendingOrder.Price;
                            _trailingStopExitBestPrice = _pendingOrder.Price;
                            _trailingStopExitCurrentLevel = CalculateTrailingStopExitLevel(
                                _pendingOrder.Direction == "Long" ? PositionDirection.Long : PositionDirection.Short,
                                _pendingOrder.Price);
                            _exitState = ExitState.TrailingStopActive;
                            OnExitStatusChanged?.Invoke($"Трейлинг-стоп активен: {_trailingStopExitCurrentLevel:F2}");
                        }

                        _entryState = EntryState.NoSignal;
                        OnEntryStatusChanged?.Invoke("Вход выполнен успешно");

                        _indicatorValues.LastAction = $"Ордер исполнен. Позиция открыта по {_pendingOrder.Price:F2}";
                        _logger.LogInformation($"Entry order filled for {_instrument.Ticker} at {_pendingOrder.Price:F2}");

                        _entryPass = false;
                        _exitPass = true;

                        _pendingOrder = null;
                    }
                    else if (wasExitOrder)
                    {
                        //_logger.LogInformation($"Exit order filled for {_instrument.Ticker} at {_pendingOrder.Price:F2}");

                        if (_dealForExit == null)
                        {
                            try
                            {
                                string dbPath = System.IO.Path.Combine(
                                    System.AppDomain.CurrentDomain.BaseDirectory,
                                    "market_dataMG5.db");

                                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                                await connection.OpenAsync();

                                var command = connection.CreateCommand();
                                command.CommandText = @"
                                    SELECT Id, Ticker, InstrumentUid, Strategy, EntryTime, EntryPrice, EntryQuantity, 
                                           EntryOrderId, Direction, ExitTime, ExitPrice, ExitOrderId, Status, 
                                           ClosedPnL, ClosedPnLPercent, Comment, CreatedAt, UpdatedAt
                                    FROM DealsJournal 
                                    WHERE Status = @status AND Ticker = @ticker
                                    ORDER BY EntryTime DESC
                                    LIMIT 1";

                                command.Parameters.AddWithValue("@status", DealStatus.Open.ToString());
                                command.Parameters.AddWithValue("@ticker", _instrument.Ticker);

                                using var reader = await command.ExecuteReaderAsync();
                                if (await reader.ReadAsync())
                                {
                                    _dealForExit = new Deal
                                    {
                                        Id = reader.GetInt64(0),
                                        Ticker = reader.GetString(1),
                                        InstrumentUid = reader.GetString(2),
                                        Strategy = $"{reader.GetString(3)} - {_timeframe}",
                                        EntryTime = reader.GetDateTime(4),
                                        EntryPrice = reader.GetDecimal(5),
                                        EntryQuantity = reader.GetInt32(6),
                                        EntryOrderId = reader.GetString(7),
                                        Direction = reader.GetString(8),
                                        ExitTime = reader.IsDBNull(9) ? null : (DateTime?)reader.GetDateTime(9),
                                        ExitPrice = reader.IsDBNull(10) ? null : (decimal?)reader.GetDecimal(10),
                                        ExitOrderId = reader.IsDBNull(11) ? null : reader.GetString(11),
                                        Status = Enum.Parse<DealStatus>(reader.GetString(12)),
                                        ClosedPnL = reader.IsDBNull(13) ? null : (decimal?)reader.GetDecimal(13),
                                        ClosedPnLPercent = reader.IsDBNull(14) ? null : (decimal?)reader.GetDecimal(14),
                                        Comment = reader.IsDBNull(15) ? null : reader.GetString(15),
                                        CreatedAt = reader.GetDateTime(16),
                                        UpdatedAt = reader.GetDateTime(17),
                                        EntryReason = reader.IsDBNull(15) ? null : reader.GetString(15),
                                    };
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"DEBUG: Ошибка загрузки сделок: {ex.Message}");
                            }
                        }

                        _dealForExit?.ExitPrice = _pendingOrder.Price;
                        _dealForExit?.ExitOrderId = _pendingOrder.OrderId;
                        _dealForExit?.ExitReason = _pendingOrder.ExitReason;

                        if (_strategyViewModel != null && _instrument != null && _dealForExit != null)
                        {
                            try
                            {
                                decimal pnl = 0;
                                decimal pnlPercent = 0;
                                decimal priceDiff = 0;

                                if (_dealForExit != null)
                                {
                                    if (_dealForExit.Direction == PositionDirection.Long || _dealForExit.Direction == "Long" || _dealForExit.Direction == "Buy")
                                    {
                                        priceDiff = _dealForExit.ExitPrice.Value - _dealForExit.EntryPrice;
                                        pnl = priceDiff * _dealForExit.EntryQuantity * _instrument.LotSize;
                                        pnlPercent = _dealForExit.EntryPrice > 0
                                            ? priceDiff / _dealForExit.EntryPrice * 100
                                            : 0;
                                    }
                                    else if (_dealForExit.Direction == PositionDirection.Short || _dealForExit.Direction == "Short" || _dealForExit.Direction == "Sell")
                                    {
                                        priceDiff = _dealForExit.EntryPrice - _dealForExit.ExitPrice.Value;
                                        pnl = priceDiff * _dealForExit.EntryQuantity * _instrument.LotSize;
                                        pnlPercent = _dealForExit.EntryPrice > 0
                                            ? priceDiff / _dealForExit.EntryPrice * 100
                                            : 0;
                                    }
                                }

                                var result = await _transactionsService.CloseDealAsync(
                                    instrumentUid: _dealForExit?.InstrumentUid,
                                    entryOrderId: _dealForExit?.EntryOrderId,
                                    exitTime: DateTime.Now,
                                    exitPrice: _dealForExit?.ExitPrice,
                                    exitOrderId: _dealForExit?.ExitOrderId ?? "no_exit_id",
                                    closedPnL: pnl,
                                    closedPnLPercent: pnlPercent,
                                    comment: $"ВЫХОД: {_dealForExit?.ExitReason}"
                                );

                                if (result)
                                {
                                    Debug.WriteLine($"✅ Сделка на выход записана в журнал: {_instrument.Ticker} P&L={pnl:F2} ({pnlPercent:F2}%)");

                                    _currentPosition = null;
                                    _exitState = ExitState.NoPosition;

                                    ResetExitVariables();
                                    ResetAllMovingTPVariables();

                                    OnExitStatusChanged?.Invoke($"Выход выполнен успешно: {_dealForExit?.ExitReason}");

                                    // ✅ БЛОКИРУЕМ ВХОД НА ENTRY_COOLDOWN_SECONDS СЕКУНД
                                    _entryPass = false;
                                    _exitPass = true;
                                    _lastEntryTime = DateTime.Now; // Блокируем вход на ENTRY_COOLDOWN_SECONDS

                                    shouldExitByMovingTPExit = false;
                                    shouldExitByMovingTPExitByChangeSignal = false;

                                    _indicatorValues.LastAction = $"Позиция закрыта по {_dealForExit?.ExitPrice:F2} ({_dealForExit?.ExitReason})";
                                    _indicatorValues.CurrentPosition = "Нет позиции";
                                    _indicatorValues.CurrentPnL = 0;

                                    _logger.LogInformation($"Position closed successfully at {_dealForExit?.ExitPrice:F2}");

                                    _pendingOrder = null;
                                    _dealForExit = null;

                                    // ✅ РАЗБЛОКИРУЕМ ВХОД ЧЕРЕЗ ENTRY_COOLDOWN_SECONDS СЕКУНД
                                    _ = Task.Delay(TimeSpan.FromSeconds(ENTRY_COOLDOWN_SECONDS)).ContinueWith(_ =>
                                    {
                                        _entryPass = true;
                                        Debug.WriteLine($"[{_currentInstrumentTicker}] Вход разблокирован после таймаута");
                                    });

                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Ошибка записи сделки в журнал при выходе");
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[{_currentInstrumentTicker}] ВНИМАНИЕ: Тип ордера не определен! Пытаемся определить...");

                        var positions = await _provider.GetPositionsAsync();
                        var position = positions?.FirstOrDefault(p => p.InstrumentUid == _currentInstrumentUid);

                        if (position == null || position.Quantity == 0)
                        {
                            Debug.WriteLine($"[{_currentInstrumentTicker}] Определено как выход (позиция отсутствует)");
                        }
                        else
                        {
                            Debug.WriteLine($"[{_currentInstrumentTicker}] Определено как вход (позиция существует)");

                            _currentPosition = new Position
                            {
                                InstrumentUid = _instrument.Uid,
                                Direction = position.Quantity > 0 ? PositionDirection.Long : PositionDirection.Short,
                                Quantity = Math.Abs((int)position.Quantity),
                                EntryPrice = position.AveragePrice,
                                BestPrice = position.AveragePrice,
                                EntryOrderId = _pendingOrder.OrderId ?? _pendingOrder.Id
                            };

                            _dealForExit = new Deal
                            {
                                Ticker = _instrument.Ticker,
                                InstrumentUid = _instrument.Uid,
                                Direction = position.Quantity > 0 ? PositionDirection.Long : PositionDirection.Short,
                                EntryQuantity = Math.Abs((int)position.Quantity),
                                EntryPrice = position.AveragePrice,
                                EntryOrderId = _pendingOrder.OrderId ?? _pendingOrder.Id,
                                EntryReason = $"{_strategyViewModel.CurrentTimeframe} Вход по сигналу {_indicatorValues.Signal}",
                            };

                            _entryState = EntryState.MovingTPExitActive;
                            _entryPass = false;
                            _exitPass = true;
                        }
                    }
                }
                else if (orderStatus == OrderStatus.Cancelled || orderStatus == OrderStatus.Rejected)
                {
                    OnOrderStatusChanged?.Invoke($"Ордер {orderStatus}");

                    if (_entryState == EntryState.OrderPending || _entryState == EntryState.MovingTPEntryActive)
                    {
                        _entryState = EntryState.EntryFailed;
                        OnEntryStatusChanged?.Invoke($"Ордер отменен: {orderStatus}");

                        _indicatorValues.LastAction = $"Ордер на вход отменен/отклонен: {orderStatus}";

                        _ = Task.Delay(1000).ContinueWith(_ =>
                        {
                            _entryState = EntryState.NoSignal;
                            OnEntryStatusChanged?.Invoke("Ожидание сигнала (после отмены)");
                            _entryPass = true;
                            _exitPass = true;
                        });
                    }
                    else if (_exitState == ExitState.ExitPending)
                    {
                        _exitState = ExitState.ExitFailed;
                        OnExitStatusChanged?.Invoke($"Ордер на выход отменен: {orderStatus}");

                        _indicatorValues.LastAction = $"Ордер на выход отменен/отклонен: {orderStatus}";

                        _exitState = ExitState.PositionActive;
                        OnExitStatusChanged?.Invoke("Позиция активна (после отмены)");

                        _entryPass = false;
                        _exitPass = true;
                    }
                }
                else
                {
                    OnOrderStatusChanged?.Invoke($"Ожидание исполнения ордера по {_pendingOrder.Price:F2}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking order execution");
                _indicatorValues.LastAction = $"Ошибка проверки ордера: {ex.Message}";
                OnOrderStatusChanged?.Invoke($"Ошибка проверки ордера: {ex.Message}");
            }
        }
        /* private async Task CheckActiveOrdersExecutionAsync()
         {
             try
             {
                 if (_activeStopLossOrder != null)
                 {
                     var stopLossStatus = await _provider.GetOrderStatusAsync(_activeStopLossOrder.OrderId ?? _activeStopLossOrder.Id);
                     if (stopLossStatus == OrderStatus.Filled)
                     {
                         _logger.LogInformation($"Stop-loss order filled for {_instrument.Ticker}");
                         _activeStopLossOrder = null;
                         OnOrderStatusChanged?.Invoke("Стоп-лосс сработал, позиция закрыта");

                         // Закрываем позицию
                         _currentPosition = null;
                         _exitState = ExitState.NoPosition;
                         OnExitStatusChanged?.Invoke("Позиция закрыта по стоп-лоссу");
                     }
                     else if (stopLossStatus == OrderStatus.Cancelled || stopLossStatus == OrderStatus.Rejected)
                     {
                         _logger.LogWarning($"Stop-loss order {stopLossStatus} for {_instrument.Ticker}");
                         _activeStopLossOrder = null;
                         OnOrderStatusChanged?.Invoke($"Стоп-лосс {stopLossStatus}");
                     }
                 }

                 if (_activeTakeProfitOrder != null)
                 {
                     var takeProfitStatus = await _provider.GetOrderStatusAsync(_activeTakeProfitOrder.OrderId ?? _activeTakeProfitOrder.Id);
                     if (takeProfitStatus == OrderStatus.Filled)
                     {
                         _logger.LogInformation($"Take-profit order filled for {_instrument.Ticker}");
                         _activeTakeProfitOrder = null;
                         OnOrderStatusChanged?.Invoke("Тейк-профит сработал, позиция закрыта");

                         // Закрываем позицию
                         _currentPosition = null;
                         _exitState = ExitState.NoPosition;
                         OnExitStatusChanged?.Invoke("Позиция закрыта по тейк-профиту");
                     }
                     else if (takeProfitStatus == OrderStatus.Cancelled || takeProfitStatus == OrderStatus.Rejected)
                     {
                         _logger.LogWarning($"Take-profit order {takeProfitStatus} for {_instrument.Ticker}");
                         _activeTakeProfitOrder = null;
                         OnOrderStatusChanged?.Invoke($"Тейк-профит {takeProfitStatus}");
                     }
                 }
             }
             catch (Exception ex)
             {
                 _logger.LogError(ex, "Error checking active orders execution");
             }
         }*/
        #endregion

        #region Управление позициями
        private async Task UpdateCurrentPositionAsync()
        {
            var now = DateTime.Now;
            if ((now - _lastPositionCheck).TotalSeconds < 5) return;

            try
            {
                var positions = await _provider.GetPositionsAsync();
                var currentPos = positions?.FirstOrDefault(p => p.InstrumentUid == _currentInstrumentUid);

                if (currentPos != null && currentPos.Quantity != 0)
                {
                    // ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: убедимся, что это наш инструмент
                    if (currentPos.InstrumentUid != _currentInstrumentUid)
                    {
                        Debug.WriteLine($"UpdateCurrentPositionAsync {_instrument.Ticker} Обнаружена позиция другого инструмента: {currentPos.InstrumentUid}, наш: {_currentInstrumentUid}");
                        return;
                    }



                    // ВАЖНО: Определяем направление позиции на основе количества
                    // Определяем направление
                    if (string.IsNullOrEmpty(currentPos.Direction))
                    {
                        currentPos.Direction = currentPos.Quantity > 0 ? PositionDirection.Long : PositionDirection.Short;
                        _logger.LogInformation($"UpdateCurrentPositionAsync {_instrument.Ticker} Направление позиции определено: {currentPos.Direction}");
                    }

                    // Проверяем корректность цены входа
                    // ✅ КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Не приравниваем цену входа к текущей цене!
                    // Вместо этого загружаем реальную цену входа из базы данных
                    if (currentPos.EntryPrice <= 0)
                    {
                        _logger.LogWarning($"UpdateCurrentPositionAsync {_instrument.Ticker} Некорректная цена входа из API: {currentPos.EntryPrice}");

                        var dealFromDb = await GetOpenDealFromDatabaseAsync(_currentInstrumentUid);
                        if (dealFromDb != null && dealFromDb.EntryPrice > 0)
                        {
                            currentPos.EntryPrice = dealFromDb.EntryPrice;
                            _indicatorValues.EntryPrice = dealFromDb.EntryPrice;
                            _logger.LogInformation($"UpdateCurrentPositionAsync {_instrument.Ticker} Цена входа восстановлена из БД: {currentPos.EntryPrice}");
                        }
                        else
                        {
                            var entryPriceFromHistory = await GetEntryPriceFromOperationsHistoryAsync(_currentInstrumentUid);
                            if (entryPriceFromHistory > 0)
                            {
                                currentPos.EntryPrice = entryPriceFromHistory;
                                _indicatorValues.EntryPrice = entryPriceFromHistory;
                                _logger.LogInformation($"UpdateCurrentPositionAsync {_instrument.Ticker} Цена входа восстановлена из истории операций: {currentPos.EntryPrice}");
                            }
                            else
                            {
                                _logger.LogError($"UpdateCurrentPositionAsync {_instrument.Ticker} НЕ УДАЛОСЬ ВОССТАНОВИТЬ ЦЕНУ ВХОДА! Позиция будет проигнорирована.");
                                return;
                            }
                        }
                    }

                    _currentPosition = currentPos;

                    // Обновляем информацию для UI
                    UpdatePositionAndOrderInfo();

                    // Если позиция появилась неожиданно (например, открыта вручную)
                    if (_exitState == ExitState.NoPosition)
                    {
                        if (string.IsNullOrEmpty(currentPos.Direction))
                        {
                            _logger.LogError($"UpdateCurrentPositionAsync {_instrument.Ticker} Не могу инициализировать механизм выхода: Direction пустое");
                            return;
                        }

                        // ✅ ИНИЦИАЛИЗИРУЕМ МЕХАНИЗМ ВЫХОДА
                        InitializeMovingTPExit();
                    }
                }
                else
                {
                    _currentPosition = null;

                    if (_exitState != ExitState.NoPosition)
                    {
                        _exitState = ExitState.NoPosition;
                        OnExitStatusChanged?.Invoke("Нет позиции");
                        ResetExitVariables();
                    }
                }

                _lastPositionCheck = now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UpdateCurrentPositionAsync {_instrument.Ticker} Error updating position");
                OnOrderStatusChanged?.Invoke($"Ошибка обновления позиции: {ex.Message}");
                Debug.WriteLine($"UpdateCurrentPositionAsync {_instrument.Ticker} Ошибка обновления позиции: {ex.Message} {ex.StackTrace} "); 
            }
        }
        private void UpdatePositionAndOrderInfo()
        {
            try
            {
                // Обновляем информацию о позиции
                if (_currentPosition != null)
                {
                    // ДОБАВЛЕНА ПРОВЕРКА на корректность данных
                    if (string.IsNullOrEmpty(_currentPosition.Direction) || _currentPosition.EntryPrice <= 0)
                    {
                        Debug.WriteLine($"Некорректные данные позиции. Пропускаем обновление UI.    _currentPosition.Direction={_currentPosition.Direction}     _currentPosition.EntryPrice={_currentPosition.EntryPrice}");
                        return;
                    }


                    //Debug.WriteLine($"ОБНОВЛЕНИЕ ПОЗИЦИИ: Direction={_currentPosition.Direction}, Quantity={_currentPosition.Quantity}, Entry={_currentPosition.EntryPrice:F2}");

                    _indicatorValues.CurrentPosition =
                        $"{_currentPosition.Direction} {_currentPosition.Quantity} лотов по {_indicatorValues.EntryPrice:F2}";

                    if (_lastPrice > 0 && _currentPosition.EntryPrice > 0)
                    {
                        if (_currentPosition.Direction == PositionDirection.Long)
                        {
                            _indicatorValues.CurrentPnL = (_lastPrice - _indicatorValues.EntryPrice) * (Math.Abs(_currentPosition.Quantity) * _instrument.LotSize);
                        }
                        else
                        {
                            _indicatorValues.CurrentPnL = (_indicatorValues.EntryPrice - _lastPrice) * (Math.Abs(_currentPosition.Quantity) * _instrument.LotSize);
                        }
                    }



                    // Если используются скользящие тейк-профиты/стопы, показываем текущие уровни
                    if (_exitState == ExitState.MovingTPExitActive)
                    {
                        if (_currentPosition != null && _currentPosition.Quantity != 0 && _parameters.CloseOnSignalReversal && !shouldExitByMovingTPExitByChangeSignal)
                        {
                            string reversSignal = _currentPosition.Quantity > 0 ? "ПЕРЕКУПЛЕННОСТИ" : "ПЕРЕПРОДАННОСТИ";
                            _indicatorValues.ExitStatusDetails =
                               $"Позиция {_currentPosition.Quantity} (лот) - ожидание {reversSignal}";
                        }
                        else
                        {
                            _indicatorValues.ExitStatusDetails =
                                $"Скользящий TP активен. Текущий уровень: {_movingTPExitCurrentLevel:F2}, Цель: {_movingTPExitTargetPrice:F2}";
                        }
                    }
                    else if (_exitState == ExitState.TrailingStopActive)
                    {
                        _indicatorValues.ExitStatusDetails =
                            $"Трейлинг-стоп активен. Лучшая цена: {_trailingStopExitBestPrice:F2}, Стоп: {_trailingStopExitCurrentLevel:F2}";
                    }
                }
                else
                {
                    _indicatorValues.CurrentPosition = "Нет позиции";
                    _indicatorValues.CurrentPnL = 0;
                    _indicatorValues.ExitStatus = "Нет позиции";
                    _indicatorValues.ExitStatusDetails = "";
                }

                // Обновляем информацию о pending ордере
                if (_pendingOrder != null)
                {
                    _indicatorValues.OrderStatus =
                        $"Ожидание исполнения ордера: {_pendingOrder.Direction} {_pendingOrder.Quantity} лотов по {_pendingOrder.Price:F2}";
                }
                else if (_currentPosition == null)
                {
                    _indicatorValues.OrderStatus = "Нет активных ордеров";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating position and order info");
                Debug.WriteLine($"Error updating position and order info {ex.Message} {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Получение открытой сделки из базы данных
        /// </summary>
        private async Task<Deal> GetOpenDealFromDatabaseAsync(string instrumentUid)
        {
            try
            {
                string dbPath = System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory,
                    "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = @"
            SELECT Id, Ticker, InstrumentUid, Strategy, EntryTime, EntryPrice, EntryQuantity, 
                   EntryOrderId, Direction, ExitTime, ExitPrice, ExitOrderId, Status, 
                   ClosedPnL, ClosedPnLPercent, Comment, CreatedAt, UpdatedAt
            FROM DealsJournal 
            WHERE Status = @status AND InstrumentUid = @instrumentUid
            ORDER BY EntryTime DESC
            LIMIT 1";

                command.Parameters.AddWithValue("@status", DealStatus.Open.ToString());
                command.Parameters.AddWithValue("@instrumentUid", instrumentUid);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new Deal
                    {
                        Id = reader.GetInt64(0),
                        Ticker = reader.GetString(1),
                        InstrumentUid = reader.GetString(2),
                        Strategy = reader.GetString(3),
                        EntryTime = reader.GetDateTime(4),
                        EntryPrice = reader.GetDecimal(5),
                        EntryQuantity = reader.GetInt32(6),
                        EntryOrderId = reader.GetString(7),
                        Direction = reader.GetString(8),
                        ExitTime = reader.IsDBNull(9) ? null : (DateTime?)reader.GetDateTime(9),
                        ExitPrice = reader.IsDBNull(10) ? null : (decimal?)reader.GetDecimal(10),
                        ExitOrderId = reader.IsDBNull(11) ? null : reader.GetString(11),
                        Status = Enum.Parse<DealStatus>(reader.GetString(12)),
                        ClosedPnL = reader.IsDBNull(13) ? null : (decimal?)reader.GetDecimal(13),
                        ClosedPnLPercent = reader.IsDBNull(14) ? null : (decimal?)reader.GetDecimal(14),
                        Comment = reader.IsDBNull(15) ? null : reader.GetString(15),
                        CreatedAt = reader.GetDateTime(16),
                        UpdatedAt = reader.GetDateTime(17),
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetOpenDealFromDatabaseAsync error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Получение цены входа из истории операций
        /// </summary>
        private async Task<decimal> GetEntryPriceFromOperationsHistoryAsync(string instrumentUid)
        {
            try
            {
                // Получаем историю операций за последние 7 дней
                var to = DateTime.Now;
                var from = to.AddDays(-7);

                var operations = await _provider.GetOperationsHistoryAsync(_selectedAccountId, from, to);

                // Ищем операцию покупки нашего инструмента
                var entryOperation = operations
                    .Where(o => o.InstrumentUid == instrumentUid &&
                               (o.OperationType == "BUY" || o.OperationType == "Buy"))
                    .OrderByDescending(o => o.Date)
                    .FirstOrDefault();

                if (entryOperation != null && entryOperation.Price > 0)
                {
                    Debug.WriteLine($"GetEntryPriceFromOperationsHistoryAsync: Найдена цена входа {entryOperation.Price:F2} для {instrumentUid}");
                    return entryOperation.Price;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetEntryPriceFromOperationsHistoryAsync error: {ex.Message}");
            }
            return 0;
        }

        #endregion

        #region Market Data Processing
        public async Task ProcessMarketData(MarketData marketData)
        {
            if (State != StrategyState.Running || marketData == null)
                return;

            try
            {
                if (marketData.LastPrice > 0)
                {
                    _lastPrice = marketData.LastPrice;
                    _indicatorValues.LastPrice = _lastPrice;

                    // ✅ Обновляем P&L открытой сделки в реальном времени
                    if (_currentPosition != null && _strategyViewModel != null)
                    {
                        await _transactionsService.UpdateOpenDealsPnLAsync(_instrument.Uid, _lastPrice);
                    }

                    // Проверяем исполнение pending ордера
                    await CheckPendingOrderExecutionAsync();

                    // Проверяем условия для тейк-профитов/стоп-лоссов
                    await CheckExitConditionsAsync();

                    // Обновляем скользящий тейк-профит на ВХОДЕ
                    await UpdateMovingTakeProfitEntryAsync();



                    if (shouldExitByMovingTPExitByChangeSignal)
                    {
                        // Обновляем скользящий тейк-профит на ВЫХОДЕ
                        await UpdateMovingTakeProfitExitAsync();
                    }
                    else if (shouldExitByMovingTPExit)
                    {
                        // Обновляем скользящий тейк-профит на ВЫХОДЕ
                        await UpdateMovingTakeProfitExitAsync();
                    }



                    // Обновляем трейлинг-стоп на выходе
                    await UpdateTrailingStopExitAsync();

                    // Обновляем стратегию
                    await ProcessStrategyLogicAsync();

                    // Обновляем информацию о позиции и ордерах для UI
                    UpdatePositionAndOrderInfo();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing market data");
                _indicatorValues.LastAction = $"Ошибка обработки данных: {ex.Message}";
                OnOrderStatusChanged?.Invoke($"Ошибка: {ex.Message}");
            }
        }
        #endregion

        #region Strategy Logic and Signal Processing
        private async Task ProcessStrategyLogicAsync()
        {
            try
            {
                // ПРОВЕРКА: убедимся, что мы работаем с правильным инструментом
                if (_instrument?.Uid != _currentInstrumentUid)
                {
                    Debug.WriteLine($"[{_currentInstrumentTicker}] Предупреждение: _instrument.Uid ({_instrument?.Uid}) не совпадает с _currentInstrumentUid ({_currentInstrumentUid})");
                    return;
                }




                // Загружаем свечи для расчета
                var candles = await LoadCandlesAsync();
                if (candles.Count < Math.Max(_parameters.RsiPeriod, _parameters.StochPeriod))
                {
                    _indicatorValues.Status = $"Ожидание данных ({candles.Count} свечей)";
                    _indicatorValues.LastUpdate = DateTime.Now;
                    OnOrderStatusChanged?.Invoke($"Ожидание данных ({candles.Count} свечей)");
                    return;
                }

                // Проверяем наличие текущей цены
                if (_lastPrice <= 0)
                {
                    _indicatorValues.Status = "ОЖИДАНИЕ ТЕКУЩЕЙ ЦЕНЫ";
                    _indicatorValues.LastUpdate = DateTime.Now;
                    OnOrderStatusChanged?.Invoke("Ожидание текущей цены");
                    return;
                }

                // Расчет индикаторов
                var quotes = ConvertToQuotes(candles);
                CalculateIndicators(quotes, candles);


                // ✅ ВАЖНО: Сохраняем предыдущее значение осциллятора ДО обновления
                // Это нужно делать КАЖДЫЙ раз, даже если есть позиция
                _indicatorValues.PreviousOscillatorValue = _lastOscillatorValueForCrossing;

                // ✅ Обновляем текущее значение осциллятора
                decimal currentOscillator = _indicatorValues.OscillatorValue;

                // ✅ Обновляем сохраненное значение для следующей итерации
                // ВАЖНО: сохраняем ТОЛЬКО если текущее значение > 0
                if (currentOscillator > 0)
                {
                    _lastOscillatorValueForCrossing = currentOscillator;
                    _hasPreviousOscillatorValue = true;
                }





                // Обновление UI значений
                UpdateIndicatorValues();

                // Генерация сигналов (если нет активного входа)
                if (_entryState != EntryState.OrderPending &&
                    _entryState != EntryState.MovingTPEntryActive)
                {
                    GenerateTradingSignals();
                }

                // Проверка и обновление позиции
                await UpdateCurrentPositionAsync();

                // Если есть позиция, обновляем информацию о стоп-лоссе и тейк-профите
                if (_currentPosition != null && _exitState == ExitState.PositionActive)
                {
                    // Стоп-лосс и тейк-профит теперь управляются через TransactionsService
                    // Расчет цен для UI
                    decimal stopLossPrice = CalculateStopLossPrice(
                        _currentPosition.EntryPrice,
                        _currentPosition.Direction,
                        _parameters.StopLossCalculationType,
                        _parameters.StopLossPercent,
                        _parameters.StopLossAbsolute,
                        _parameters.AtrMultiplier,
                        _indicatorValues.AtrValue,
                        _parameters.StopLossActivationPrice,
                        _parameters.StopLossSlippage);

                    decimal takeProfitPrice = CalculateTakeProfitPrice(
                        _currentPosition.EntryPrice,
                        _currentPosition.Direction,
                        _parameters.TakeProfitCalculationType,
                        _parameters.TakeProfitPercent,
                        _parameters.TakeProfitAbsolute,
                        _parameters.AtrMultiplier,
                        _indicatorValues.AtrValue,
                        _parameters.TakeProfitActivationPrice,
                        _parameters.TakeProfitSlippage);

                    _indicatorValues.StopLossPrice = stopLossPrice;
                    _indicatorValues.TakeProfitPrice = takeProfitPrice;
                }

                // Обработка торговых сигналов
                await ProcessTradingSignalsAsync();

                // Обновляем статусы входов и выходов
                _indicatorValues.EntryStatusDetails = GetEntryStatusDetails();
                _indicatorValues.ExitStatusDetails = GetExitStatusDetails();

                _indicatorValues.LastUpdate = DateTime.Now;

                if (lotPositionSize == 0)
                {
                    await CalculatePositionSize();
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing RSI strategy for {_instrument.Ticker}");
                _indicatorValues.Status = $"Ошибка: {ex.Message}";
                OnOrderStatusChanged?.Invoke($"Ошибка расчета: {ex.Message}");
            }
        }
        private async Task ProcessTradingSignalsAsync()
        {
            // Проверяем блокировку
            if (_pendingOrder != null)
            {
                return;
            }


            // Проверяем таймаут после последнего входа
            if ((DateTime.Now - _lastEntryTime).TotalSeconds < ENTRY_COOLDOWN_SECONDS)
            {
                Debug.WriteLine($"Таймаут на входе: прошло {(DateTime.Now - _lastEntryTime).TotalSeconds:F1} секунд из {ENTRY_COOLDOWN_SECONDS}");
                return;
            }

            // ✅ ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: обновляем позицию перед принятием решения
            await UpdateCurrentPositionAsync();


            // Только если нет активной позиции и нет активного процесса входа
            if (_currentPosition == null &&
                _entryState != EntryState.MovingTPEntryActive &&
                _entryState != EntryState.OrderPending)
            {
                try
                {
                    // Для ВХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
                    if (_parameters.EntryOrderType == OrderType.LevelCrossingEntry)
                    {
                        // Сигнал на покупку (лонг) при пересечении перепроданности СНИЗУ ВВЕРХ
                        if (_indicatorValues.Signal.Contains("СИГНАЛ НА ПОКУПКУ") && _entryPass)
                        {
                            // ✅ ДВОЙНАЯ ПРОВЕРКА: убеждаемся, что позиция все еще отсутствует
                            await UpdateCurrentPositionAsync();
                            if (_currentPosition == null)
                            {
                                await ProcessEntrySignalAsync(PositionDirection.Long);
                                _entryPass = false;
                            }
                        }
                        // Сигнал на продажу (шорт) при пересечении перекупленности СВЕРХУ ВНИЗ
                        else if (_indicatorValues.Signal.Contains("СИГНАЛ НА ПРОДАЖУ") && _entryPass)
                        {
                            await UpdateCurrentPositionAsync();
                            if (_currentPosition == null)
                            {
                                await ProcessEntrySignalAsync(PositionDirection.Short);
                                _entryPass = false;
                            }
                        }
                    }
                    else
                    {
                        // Существующая логика для других типов входа
                        if (_indicatorValues.Signal.Contains("СИГНАЛ НА ПОКУПКУ") && _entryPass)
                        {
                            await ProcessEntrySignalAsync(PositionDirection.Long);
                            _entryPass = false;
                        }
                        else if (_indicatorValues.Signal.Contains("СИГНАЛ НА ПРОДАЖУ") && _entryPass)
                        {
                            await ProcessEntrySignalAsync(PositionDirection.Short);
                            _entryPass = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing trading signals");
                    _indicatorValues.LastAction = $"Ошибка обработки сигналов: {ex.Message}";

                    // В случае ошибки возвращаемся к мониторингу сигналов
                    _entryState = EntryState.NoSignal;
                    OnEntryStatusChanged?.Invoke("Ожидание сигнала (после ошибки)");
                }
            }
            else if (_currentPosition != null)
            {
                // ДОБАВЛЕНО: Логируем игнорирование сигналов при активной позиции
                //Debug.WriteLine($"Игнорируем сигналы входа: позиция активна (Direction={_currentPosition.Direction}, Quantity={_currentPosition.Quantity})");
                _logger.LogDebug($"Входные сигналы игнорируются: позиция активна (Direction={_currentPosition.Direction}, Quantity={_currentPosition.Quantity})");
                _entryPass = false;
            }
        }
        private async Task ProcessEntrySignalAsync(string direction)
        {
            try
            {
                // ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: обновляем позицию перед входом
                await UpdateCurrentPositionAsync();

                // ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: Если позиция появилась с момента генерации сигнала
                if (_currentPosition != null)
                {
                    Debug.WriteLine($"[{_currentInstrumentTicker}] Вход отменен: позиция уже существует (Direction={_currentPosition.Direction})");
                    _logger.LogInformation($"[{_currentInstrumentTicker}] Вход отменен: позиция уже существует");
                    _entryState = EntryState.NoSignal;
                    OnEntryStatusChanged?.Invoke("Вход отменен: позиция активна");
                    return;
                }



                // Проверяем все активные позиции на случай, если есть другие инструменты
                var allPositions = await _provider.GetPositionsAsync();
                var otherInstrumentPositions = allPositions?.Where(p => p.InstrumentUid != _currentInstrumentUid && p.Quantity != 0);

                if (otherInstrumentPositions?.Any() == true)
                {
                    Debug.WriteLine($"[{_currentInstrumentTicker}] Вход разрешен, позиции других инструментов не мешают");
                }


                // Обработка ВХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
                if (_parameters.EntryOrderType == OrderType.LevelCrossingEntry)
                {
                    // Активируем защитный стоп-лосс для входа
                    _levelCrossingEntryProtectiveStopActive = true;
                    _levelCrossingEntryStopLossPrice = CalculateLevelCrossingEntryStopLoss(direction, _lastPrice);
                    _levelCrossingEntryTakeProfitPrice = CalculateLevelCrossingEntryTakeProfit(direction, _lastPrice);

                    // Выполняем немедленный вход
                    await ExecuteLevelCrossingEntryAsync(direction);
                }
                // Активация скользящего тейка на входе, начальных его параметров,
                // по которым далее будет ориентироваться метод UpdateMovingTakeProfitEntryAsync и рассчитывать относительно этих данных и цены вход.
                else if (_parameters.EntryOrderType == OrderType.MovingTakeProfitEntry)
                {
                    // Инициализация скользящего тейк-профита на входе
                    _movingTPEntryStartPrice = _lastPrice;
                    _movingTPEntryCurrentLevel = _lastPrice;

                    // Рассчитываем начальный целевой уровень
                    _movingTPEntryTargetPrice = CalculateMovingTPEntryTargetLevel(direction, _lastPrice, _lastPrice);

                    _movingTPEntryStartTime = DateTime.Now;
                    _entryState = EntryState.MovingTPEntryActive;

                    _logger.LogInformation($"Moving TP entry activated for {direction} at {_lastPrice:F2}, target: {_movingTPEntryTargetPrice:F2}");
                }
                else
                {
                    await ExecuteImmediateEntryAsync(direction);
                }
            }
            catch (Exception ex)
            {
                _entryState = EntryState.EntryFailed;
                OnEntryStatusChanged?.Invoke($"Ошибка обработки сигнала: {ex.Message}");
                _logger.LogError(ex, $"Error processing {direction} entry signal");
                _indicatorValues.LastAction = $"Ошибка обработки сигнала входа: {ex.Message}";

                // После ошибки возвращаемся к мониторингу сигналов
                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    _entryState = EntryState.NoSignal;
                    OnEntryStatusChanged?.Invoke("Ожидание сигнала (после ошибки)");
                });
            }
        }
        #endregion

        #region Entry Methods
        private async Task ExecuteMovingTPEntryAsync(string direction, string reason)
        {
            // Добавляем информацию об инструменте в логи
            Debug.WriteLine($"[{_currentInstrumentTicker}] DEBUG - ExecuteMovingTPEntryAsync - ВХОД");



            // Проверяем таймаут после последнего входа
            if ((DateTime.Now - _lastEntryTime).TotalSeconds < ENTRY_COOLDOWN_SECONDS)
            {
                Debug.WriteLine($"Таймаут входа: прошло {(DateTime.Now - _lastEntryTime).TotalSeconds:F1} секунд из {ENTRY_COOLDOWN_SECONDS}");
                OnEntryStatusChanged?.Invoke($"Таймаут входа: {ENTRY_COOLDOWN_SECONDS - (int)(DateTime.Now - _lastEntryTime).TotalSeconds} секунд");
                return;
            }


            try
            {
                // Используем асинхронную версию
                var positionSize = await CalculatePositionSize();

                // ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: проверяем ВСЕ позиции
                var positions = await _provider.GetPositionsAsync();

                // Ищем позицию нашего инструмента
                var existingPos = positions?.FirstOrDefault(p => p.InstrumentUid == _currentInstrumentUid);


                if (existingPos != null && existingPos.Quantity != 0)
                {
                    Debug.WriteLine($"Вход отменен: обнаружена существующая позиция: {existingPos.Quantity} лотов");
                    _logger.LogInformation($"Вход отменен: обнаружена существующая позиция: {existingPos.Quantity} лотов");

                    // Обновляем информацию о позиции
                    await UpdateCurrentPositionAsync();
                    
                    return;
                }



                // ✅ ИСПОЛЬЗУЕМ TRANSACTIONS SERVICE
                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: direction == PositionDirection.Long ? "Buy" : "Sell",
                    quantity: Math.Abs((int)positionSize),
                    ticker: _instrument.Ticker,
                    accountId: _selectedAccountId,
                    isEntryOrder: true,
                    isExitOrder: false,
                    exitReason: null);

         

                Debug.WriteLine($"DEBUG - ExecuteMovingTPEntryAsync - !!!!!!!!!!!!! - ВХОД - Ticker={_instrument.Ticker}____!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                    Debug.WriteLine($"DEBUG - ExecuteMovingTPEntryAsync -  _entryPass={_entryPass}    _exitPass={_exitPass}  Ticker={_instrument.Ticker}____");
                    Debug.WriteLine($"DEBUG - ExecuteMovingTPEntryAsync - entryOrder.InstrumentUid={result.Order.InstrumentUid}   entryOrder.Direction={result.Order.Direction}   entryOrder.OrderType={result.Order.OrderType}  entryOrder.Quantity={result.Order.Quantity}  entryOrder.Price={result.Order.Price}  entryOrder.Status={result.Order.Status}   Ticker={_instrument.Ticker}____  ");
                    Debug.WriteLine($"Выставляем ордер входа: {result.Order.Direction} {result.Order.Quantity} по {result.Order.Price:F2}  Ticker={_instrument.Ticker}____");

                    // Обновляем время последнего входа
                    _lastEntryTime = DateTime.Now;

                    // Блокируем повторные входы
                    _entryPass = false;

               
                Debug.WriteLine($"DEBUG - ExecuteMovingTPEntryAsync -  _entryPass={_entryPass}    _exitPass={_exitPass}  result={result}   Ticker={_instrument.Ticker}____");

                await Task.Delay(1000); // Ждем 1 секунды перед проверкой баланса

                if (result.IsSuccess)
                {
                    _entryPass = false;
                    _exitPass = true;

                    _pendingOrder = result.Order;
                    _entryState = EntryState.OrderPending;
                    OnEntryStatusChanged?.Invoke($"Ордер на вход выставлен: {reason}");

                    _indicatorValues.LastAction = $"Ордер на вход по скользящему TP выставлен. Причина: {reason}";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _indicatorValues.OrderStatus = "ОРДЕР НА ВХОД ВЫСТАВЛЕН";

                    Debug.WriteLine($"ОРДЕР НА ВХОД ВЫСТАВЛЕН  Ticker={_instrument.Ticker}____ {direction}  entryOrder.Quantity={result.Order.Quantity}    {reason}  \n _entryPass={_entryPass}    _exitPass={_exitPass} ");
                    _logger.LogInformation($"Moving TP entry order placed: Ticker={_instrument.Ticker}____{reason}");



                    ///  тут прописывем в БД !!!!!!!!!!!!!!!!!!!!!!

                    return;
                }
                else
                {
                    _entryState = EntryState.EntryFailed;
                    OnEntryStatusChanged?.Invoke($"Ошибка входа:  Ticker={_instrument.Ticker}____ {result.ErrorMessage}");

                    _indicatorValues.LastAction = $"Ошибка входа по скользящему TP: {result.ErrorMessage}";
                    _indicatorValues.OrderStatus = "ОШИБКА ВХОДА";

                    Debug.WriteLine($"Ошибка входа по скользящему TP {_instrument.Ticker}: {result.ErrorMessage}");

                    // Разблокируем входы через 10 секунд
                    _ = Task.Delay(10000).ContinueWith(_ =>
                    {
                        _entryPass = true;
                        _entryState = EntryState.NoSignal;
                        OnEntryStatusChanged?.Invoke("Ожидание сигнала (после ошибки)");
                    });

                    Debug.WriteLine($"После ошибки возвращаемся к мониторингу сигналов через 10сек  \nи возвращаем ключи на вход Ticker={_instrument.Ticker}____ _entryPass={_entryPass}    _exitPass={_exitPass}");
                }
            }
            catch (Exception ex)
            {
                _entryState = EntryState.EntryFailed;
                OnEntryStatusChanged?.Invoke($"Ошибка: {ex.Message}");

                _logger.LogError(ex, "Error executing moving TP entry");
                _indicatorValues.LastAction = $"Ошибка входа по скользящему TP: {ex.Message}";

                // Разблокируем входы
                _entryPass = true;
            }
        }
        private async Task ExecuteImmediateEntryAsync(string direction)
        {
            try
            {

                // Проверяем таймаут после последнего входа
                if ((DateTime.Now - _lastEntryTime).TotalSeconds < ENTRY_COOLDOWN_SECONDS)
                {
                    Debug.WriteLine($"Таймаут входа: прошло {(DateTime.Now - _lastEntryTime).TotalSeconds:F1} секунд из {ENTRY_COOLDOWN_SECONDS}");
                    OnEntryStatusChanged?.Invoke($"Таймаут входа: {ENTRY_COOLDOWN_SECONDS - (int)(DateTime.Now - _lastEntryTime).TotalSeconds} секунд");
                    return;
                }


                // Используем асинхронную версию
                var positionSize = await CalculatePositionSize();

                // ✅ ИСПОЛЬЗУЕМ TRANSACTIONS SERVICE
                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: direction == PositionDirection.Long ? "Buy" : "Sell",
                    quantity: (int)positionSize,
                    ticker: _instrument.Ticker,
                    accountId: null,
                    isEntryOrder: true,
                    isExitOrder: false,
                    exitReason: null);

                

                

                if (result.IsSuccess)
                {
                    // Обновляем время последнего входа
                    _lastEntryTime = DateTime.Now;

                    _pendingOrder = result.Order;
                    _entryState = EntryState.OrderPending;
                    OnEntryStatusChanged?.Invoke($"Ордер на вход выставлен: {result.Order.Price:F2}");

                    _indicatorValues.LastAction = $"Выставлен ордер на {(direction == PositionDirection.Long ? "покупку" : "продажу")} " +
                            $"по цене {result.Order.Price:F2}. Ожидание исполнения...";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _indicatorValues.OrderStatus = "ОРДЕР ВЫСТАВЛЕН";

                    _logger.LogInformation($"Order placed: Ticker={_instrument.Ticker}____{direction} {_instrument.Ticker} at {result.Order.Price:F2}");
                }
                else
                {
                    _entryState = EntryState.EntryFailed;
                    OnEntryStatusChanged?.Invoke($"Ошибка выставления ордера: Ticker={_instrument.Ticker}____{result.ErrorMessage}");

                    _indicatorValues.LastAction = $"Ошибка выставления ордера: Ticker={_instrument.Ticker}____{result.ErrorMessage}";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _indicatorValues.OrderStatus = "ОШИБКА ВЫСТАВЛЕНИЯ";

                    // После ошибки возвращаемся к мониторингу сигналов
                    _ = Task.Delay(10000).ContinueWith(_ =>
                    {
                        _entryState = EntryState.NoSignal;
                        OnEntryStatusChanged?.Invoke("Ожидание сигнала (после ошибки)");
                    });
                }
            }
            catch (Exception ex)
            {
                _entryState = EntryState.EntryFailed;
                OnEntryStatusChanged?.Invoke($"Ошибка: {ex.Message}");

                _logger.LogError(ex, $"Error executing immediate entry for Ticker={_instrument.Ticker}____{direction}");
                _indicatorValues.LastAction = $"Ошибка исполнения сигнала: Ticker={_instrument.Ticker}____{ex.Message}";

                // После ошибки возвращаемся к мониторингу сигналов
                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    _entryState = EntryState.NoSignal;
                    OnEntryStatusChanged?.Invoke("Ожидание сигнала (после ошибки)");
                });
            }
        }
        #endregion

        #region Exit Methods
        private async Task ExecuteMovingTPExitAsync(string direction, string reason)
        {
            Debug.WriteLine($"ExecuteMovingTPExitAsync: before --- direction: {direction}____ reason={reason}");

            #region НАЛИЧИЯ ПОЗИЦИИ
            // ПРОВЕРКА НАЛИЧИЯ ПОЗИЦИИ И НАПРАВЛЕНИЯ
            if (_currentPosition == null || string.IsNullOrEmpty(_currentPosition.Direction))
            {
                Debug.WriteLine($"ExecuteMovingTPExitAsync: [{_currentInstrumentTicker}] ОШИБКА: Нет данных позиции для выхода.");
                return;
            }

            // КРИТИЧЕСКАЯ ПРОВЕРКА: убедимся, что позиция принадлежит нашему инструменту
            if (_currentPosition.InstrumentUid != _currentInstrumentUid)
            {
                Debug.WriteLine($"ExecuteMovingTPExitAsync: [{_currentInstrumentTicker}] ОШИБКА: Позиция принадлежит другому инструменту: {_currentPosition.InstrumentUid}");
                _logger.LogError($"ExecuteMovingTPExitAsync: [{_currentInstrumentTicker}] Позиция принадлежит другому инструменту: {_currentPosition.InstrumentUid}");

                // Сбрасываем позицию, так как она не наша
                _currentPosition = null;
                _exitState = ExitState.NoPosition;
                OnExitStatusChanged?.Invoke("Позиция не принадлежит инструменту");
                return;
            }



            // Проверяем таймаут после последнего выхода
            if ((DateTime.Now - _lastExitTime).TotalSeconds < EXIT_COOLDOWN_SECONDS)
            {
                Debug.WriteLine($"ExecuteMovingTPExitAsync: Таймаут выхода: прошло {(DateTime.Now - _lastExitTime).TotalSeconds:F1} секунд из {EXIT_COOLDOWN_SECONDS}");
                OnExitStatusChanged?.Invoke($"Таймаут выхода: {EXIT_COOLDOWN_SECONDS - (int)(DateTime.Now - _lastExitTime).TotalSeconds} секунд");
                return;
            }

            #endregion

            Debug.WriteLine($"ExecuteMovingTPExitAsync: after direction: {direction} reason={reason}");

            try
            {
                // ПРОВЕРКА НАЛИЧИЯ ПОЗИЦИИ И НАПРАВЛЕНИЯ
                if (_currentPosition == null || string.IsNullOrEmpty(_currentPosition.Direction))
                {
                    Debug.WriteLine($"ExecuteMovingTPExitAsync: ОШИБКА: Ticker={_instrument.Ticker}____ Нет данных позиции для выхода. Direction={_currentPosition?.Direction}");
                    _logger.LogError($"ExecuteMovingTPExitAsync: ОШИБКА: Ticker={_instrument.Ticker}____ Нет данных позиции для выхода. Position={_currentPosition}");

                   
                    return;
                }


                // Проверка, что направление выхода противоположно направлению позиции
                bool isExitValid = false;
                if (_currentPosition.Direction == PositionDirection.Long && direction == "Sell")
                {
                    isExitValid = true;
                    Debug.WriteLine($"ExecuteMovingTPExitAsync:  IF isExitValid: {_instrument.Ticker}____ isExitValid={isExitValid}");
                }
                else if (_currentPosition.Direction == PositionDirection.Short && direction == "Buy")
                {

                    isExitValid = true;
                    Debug.WriteLine($"ExecuteMovingTPExitAsync: ELSE IF isExitValid: {_instrument.Ticker}____ isExitValid={isExitValid}");
                }

                if (!isExitValid)
                {
                    Debug.WriteLine($"ExecuteMovingTPExitAsync: Ticker={_instrument.Ticker}____ Неверное направление выхода. Position={_currentPosition.Direction}, _currentPosition.Quantity={_currentPosition.Quantity}    Exit={direction}");
                    _logger.LogError($"ExecuteMovingTPExitAsync: Ticker={_instrument.Ticker}____ Неверное направление выхода. Position={_currentPosition.Direction}, _currentPosition.Quantity={_currentPosition.Quantity}    Exit={direction}");

                    
                    return;
                }


                // Определяем количество для выхода (абсолютное значение)
                int exitQuantity = Math.Abs(_currentPosition.Quantity);

                // ✅ ИСПОЛЬЗУЕМ TRANSACTIONS SERVICE
                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: direction,
                    quantity: exitQuantity,
                    ticker: _instrument.Ticker,
                    accountId: null,
                    isEntryOrder: false,
                    isExitOrder: true,
                    exitReason: reason);


                Debug.WriteLine($"DEBUG - ExecuteMovingTPExitAsync - !!!!!!!!!!!!! - ВЫХОД - Ticker={_instrument.Ticker}____!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                Debug.WriteLine($"DEBUG - ExecuteMovingTPExitAsync -  _entryPass={_entryPass}    _exitPass={_exitPass}   Ticker={_instrument.Ticker}____");
                Debug.WriteLine($"DEBUG - ExecuteMovingTPExitAsync - exitOrder.InstrumentUid={result.Order.InstrumentUid}   exitOrder.Direction={result.Order.Direction}   exitOrder.OrderType={result.Order.OrderType}  exitOrder.Quantity={result.Order.Quantity}   exitOrder.Price={result.Order.Price}    exitOrder.Status={result.Order.Status}   Ticker={_instrument.Ticker}____");
                Debug.WriteLine($"Выставляем ордер выхода: Ticker={_instrument.Ticker}____{result.Order.Direction} {result.Order.Quantity} по {result.Order.Price:F2}");
                _logger.LogInformation($"Выставляем ордер выхода: Ticker={_instrument.Ticker}____{result.Order.Direction} {result.Order.Quantity} по {result.Order.Price:F2}, Reason: {reason}");

                // Обновляем время последнего выхода
                _lastExitTime = DateTime.Now;


                if (result.IsSuccess)
                {


                    _pendingOrder = result.Order;
                    _exitState = ExitState.ExitPending;
                    OnExitStatusChanged?.Invoke($"Ордер на выход выставлен: {reason}");

                    _indicatorValues.LastAction = $"Ордер на выход по скользящему TP выставлен. Причина: {reason}";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _indicatorValues.OrderStatus = "ОРДЕР НА ВЫХОД ВЫСТАВЛЕН";

                    _logger.LogInformation($"Moving TP exit order placed: {reason}");

                    Debug.WriteLine($"DEBUG - Выход из позиции!  Ticker={_instrument.Ticker}____ {direction}   {reason}  \n _entryPass={_entryPass}    _exitPass={_exitPass} ");

                    // Обновляем флаги
                    _entryPass = false; // Разрешаем новые входы
                    _exitPass = false; // Блокируем повторные выходы

                }
                else
                {
                    _exitState = ExitState.ExitFailed;
                    OnExitStatusChanged?.Invoke($"Ошибка выхода: {result.ErrorMessage}");

                    _indicatorValues.LastAction = $"Ошибка выхода по скользящему TP: {result.ErrorMessage}";
                    _indicatorValues.OrderStatus = "ОШИБКА ВЫХОДА";

                    _logger.LogError($"Failed to place exit order: Ticker={_instrument.Ticker}____{result.ErrorMessage}");

                    _entryPass = false;
                    _exitPass = true;

                    // Через 10 секунд возвращаемся к мониторингу
                    _ = Task.Delay(10000).ContinueWith(_ =>
                    {
                        _exitState = ExitState.PositionActive;
                        OnExitStatusChanged?.Invoke("Позиция активна (после ошибки выхода)");
                        _entryPass = false;
                        _exitPass = true;
                    });

                }
            }
            catch (Exception ex)
            {
                _exitState = ExitState.ExitFailed;
                OnExitStatusChanged?.Invoke($"Ошибка: {ex.Message}");

                _logger.LogError(ex, "Error executing moving TP exit");
                _indicatorValues.LastAction = $"Ошибка выхода по скользящему TP: {ex.Message}";
                _entryPass = false;
                _exitPass = true;

                // Через 10 секунд возвращаемся к мониторингу
                _ = Task.Delay(10000).ContinueWith(_ =>
                {
                    _exitState = ExitState.PositionActive;
                    OnExitStatusChanged?.Invoke("Позиция активна (после ошибки выхода)");
                    _entryPass = false;
                    _exitPass = true;
                });
            }
        }
        private async Task ExecuteTrailingStopExitAsync(string direction, string reason)
        {
            // ✅ СНАЧАЛА ПРОВЕРКА ТАЙМАУТА
            if ((DateTime.Now - _lastExitTime).TotalSeconds < EXIT_COOLDOWN_SECONDS)
            {
                Debug.WriteLine($"Таймаут выхода: прошло {(DateTime.Now - _lastExitTime).TotalSeconds:F1} секунд");
                OnExitStatusChanged?.Invoke($"Таймаут выхода: {EXIT_COOLDOWN_SECONDS - (int)(DateTime.Now - _lastExitTime).TotalSeconds} секунд");
                return;
            }



            try
            {
                if ((DateTime.Now - _lastExitTime).TotalSeconds < EXIT_COOLDOWN_SECONDS)
                {
                    Debug.WriteLine($"DEBUG - ExecuteTrailingStopExitAsync - Таймаут выхода: прошло {(DateTime.Now - _lastExitTime).TotalSeconds:F1} секунд");
                    OnExitStatusChanged?.Invoke($"DEBUG - ExecuteTrailingStopExitAsync - Таймаут выхода: {EXIT_COOLDOWN_SECONDS - (int)(DateTime.Now - _lastExitTime).TotalSeconds} секунд");
                    return;
                }

                Debug.WriteLine($"DEBUG - ExecuteTrailingStopExitAsync - !!!!! ВЫХОД ПО ТРЕЙЛИНГ-СТОПУ !!!! через маркетную заявку.");

                // ✅ ИСПОЛЬЗУЕМ TRANSACTIONS SERVICE
                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: direction,
                    quantity: Math.Abs((int)_currentPosition.Quantity),
                    ticker:_instrument.Ticker,
                    accountId: null,
                    isEntryOrder: false,
                    isExitOrder: true,
                    exitReason: reason);

                if (result.IsSuccess)
                {
                    _lastExitTime = DateTime.Now;
                    _pendingOrder = result.Order;
                    _exitState = ExitState.ExitPending;
                    OnExitStatusChanged?.Invoke($"Ордер на выход выставлен: {reason}");



                    _indicatorValues.LastAction = $"Ордер на выход по трейлинг-стопу выставлен. Причина: {reason}";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _indicatorValues.OrderStatus = "ОРДЕР НА ВЫХОД ВЫСТАВЛЕН";

                    Debug.WriteLine($"DEBUG - ExecuteTrailingStopExitAsync - Ордер на выход по трейлинг-стопу выставлен. Причина: {reason}");


                    _logger.LogInformation($"Trailing stop exit order placed: {reason}");

                    _exitPass = false;
                    _entryPass = false;
                }
                else
                {
                    _exitState = ExitState.ExitFailed;
                    OnExitStatusChanged?.Invoke($"Ошибка выхода: {result.ErrorMessage}");

                    _indicatorValues.LastAction = $"Ошибка выхода по трейлинг-стопу: {result.ErrorMessage}";
                    _indicatorValues.OrderStatus = "ОШИБКА ВЫХОДА";

                    Debug.WriteLine($"DEBUG - ExecuteTrailingStopExitAsync - Ошибка выхода по трейлинг-стопу: {result.ErrorMessage}");

                    _ = Task.Delay(1000).ContinueWith(_ =>
                    {
                        _exitState = ExitState.PositionActive;
                        OnExitStatusChanged?.Invoke("Позиция активна (после ошибки)");
                        Debug.WriteLine($"DEBUG - ExecuteTrailingStopExitAsync - Позиция активна (после ошибки)");

                    });
                }
            }
            catch (Exception ex)
            {
                _exitState = ExitState.ExitFailed;
                OnExitStatusChanged?.Invoke($"Ошибка: {ex.Message}");
                _logger.LogError(ex, "Error executing trailing stop exit");
                _indicatorValues.LastAction = $"Ошибка выхода по трейлинг-стопу: {ex.Message}"; 
                Debug.WriteLine($"DEBUG - ExecuteTrailingStopExitAsync - Ошибка выхода по трейлинг-стопу: {ex.Message}");

                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    _exitState = ExitState.PositionActive;
                    OnExitStatusChanged?.Invoke("Позиция активна (после ошибки)");
                    Debug.WriteLine($"DEBUG - ExecuteTrailingStopExitAsync - Позиция активна (после ошибки)");
                });
            }
        }
         
        // Исправленный метод для проверки направления
        private async Task ExecuteRegularExitAsync(string direction, string reason)
        {
            try
            {
                // ✅ ПРОВЕРЯЕМ, ЧТО НАПРАВЛЕНИЕ ВЫХОДА ПРАВИЛЬНОЕ
                if (_currentPosition == null)
                {
                    Debug.WriteLine($"ExecuteRegularExitAsync: Ошибка - нет позиции для выхода");
                    return;
                }

                // Проверяем, что направление выхода соответствует позиции
                bool isValidExit = false;
                if (_currentPosition.Direction == PositionDirection.Long && direction == "Sell")
                {
                    isValidExit = true;
                }
                else if (_currentPosition.Direction == PositionDirection.Short && direction == "Buy")
                {
                    isValidExit = true;
                }

                if (!isValidExit)
                {
                    Debug.WriteLine($"ExecuteRegularExitAsync: ОШИБКА - Неверное направление выхода. Position={_currentPosition.Direction}, Exit={direction}");
                    _logger.LogError($"ExecuteRegularExitAsync: Неверное направление выхода. Position={_currentPosition.Direction}, Exit={direction}");
                    return;
                }

                if ((DateTime.Now - _lastExitTime).TotalSeconds < EXIT_COOLDOWN_SECONDS)
                {
                    Debug.WriteLine($"Таймаут выхода: прошло {(DateTime.Now - _lastExitTime).TotalSeconds:F1} секунд");
                    OnExitStatusChanged?.Invoke($"Таймаут выхода: {EXIT_COOLDOWN_SECONDS - (int)(DateTime.Now - _lastExitTime).TotalSeconds} секунд");
                    return;
                }


                // ✅ БЛОКИРУЕМ ВХОД ДО ВЫПОЛНЕНИЯ ВЫХОДА
                _entryPass = false;
                _exitPass = false;

                // ✅ СОХРАНЯЕМ ПОЗИЦИЮ ПЕРЕД ВЫХОДОМ
                var positionToClose = _currentPosition;
                int exitQuantity = Math.Abs((int)_currentPosition.Quantity);





                // ✅ ИСПОЛЬЗУЕМ TRANSACTIONS SERVICE
                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: direction, // Sell для лонга, Buy для шорта
                    quantity: exitQuantity,
                    ticker: _instrument.Ticker,
                    accountId: null,
                    isEntryOrder: false,
                    isExitOrder: true,
                    exitReason: reason);

                if (result.IsSuccess)
                {
                    // ✅ ОБНОВЛЯЕМ ВРЕМЯ ПОСЛЕДНЕГО ВЫХОДА
                    _lastExitTime = DateTime.Now;

                    // ✅ БЛОКИРУЕМ ВХОД НА ENTRY_COOLDOWN_SECONDS СЕКУНД
                    _lastEntryTime = DateTime.Now;

                    // ✅ БЛОКИРУЕМ ПОВТОРНЫЕ ВЫХОДЫ
                    _exitPass = false;

                    _pendingOrder = result.Order;
                    _exitState = ExitState.ExitPending;
                    OnExitStatusChanged?.Invoke($"Ордер на выход выставлен: {reason}");

                    _indicatorValues.LastAction = $"Ордер на выход выставлен. Причина: {reason}";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _indicatorValues.OrderStatus = "ОРДЕР НА ВЫХОД ВЫСТАВЛЕН";

                    _logger.LogInformation($"Exit order placed: {reason}");

                    // ✅ ЗАПУСКАЕМ ТАЙМЕР ДЛЯ РАЗБЛОКИРОВКИ ВХОДА
                    _ = Task.Delay(TimeSpan.FromSeconds(ENTRY_COOLDOWN_SECONDS)).ContinueWith(_ =>
                    {
                        _entryPass = true;
                        Debug.WriteLine($"[{_currentInstrumentTicker}] Вход разблокирован после таймаута ({ENTRY_COOLDOWN_SECONDS} сек)");

                        // ✅ Также сбрасываем флаг выхода, если позиция уже закрыта
                        if (_currentPosition == null)
                        {
                            _exitPass = true;
                        }
                    });
                }
                else
                {
                    _exitState = ExitState.ExitFailed;
                    OnExitStatusChanged?.Invoke($"Ошибка выхода: {result.ErrorMessage}");

                    _indicatorValues.LastAction = $"Ошибка выхода: {result.ErrorMessage}";
                    _indicatorValues.OrderStatus = "ОШИБКА ВЫХОДА";

                    // ✅ ПРИ ОШИБКЕ РАЗБЛОКИРУЕМ ВХОД
                    _entryPass = true;
                    _exitPass = true;

                    _ = Task.Delay(1000).ContinueWith(_ =>
                    {
                        _exitState = ExitState.PositionActive;
                        OnExitStatusChanged?.Invoke("Позиция активна (после ошибки)");
                    });
                }
            }
            catch (Exception ex)
            {
                _exitState = ExitState.ExitFailed;
                OnExitStatusChanged?.Invoke($"Ошибка: {ex.Message}");
                _logger.LogError(ex, "Error executing regular exit");
                _indicatorValues.LastAction = $"Ошибка выхода: {ex.Message}";

                // ✅ ПРИ ИСКЛЮЧЕНИИ РАЗБЛОКИРУЕМ ВХОД
                _entryPass = true;
                _exitPass = true;

                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    _exitState = ExitState.PositionActive;
                    OnExitStatusChanged?.Invoke("Позиция активна (после ошибки)");
                });
            }
        }
        #endregion

        #region Conditions and Moving TP Methods
        private async Task CheckExitConditionsAsync()
        {
            if (_currentPosition == null)
            {
                return;
            }

            bool shouldExit = false;
            string exitDirection = _currentPosition.Direction == PositionDirection.Long ? PositionDirection.Short : PositionDirection.Long;
            string exitReason = "";


            // ПРОВЕРКА ДЛЯ ВЫХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
            if (_parameters.ExitOrderType == OrderType.LevelCrossingExit)
            {
                // Обновляем значения осциллятора для проверки пересечений
                decimal currentOscillator = _indicatorValues.OscillatorValue;
                decimal previousOscillator = _indicatorValues.PreviousOscillatorValue;

                /*if (_currentPosition.Ticker == "ROSN")
                {
                    Debug.WriteLine($"DEBUG: previousOscillator={previousOscillator}  currentOscillator={currentOscillator}  Ticker={_currentPosition.Ticker} ");
                }*/



                // Проверяем пересечения
                bool crossingAboveOverbought = CheckOscillatorCrossingAboveOverbought(
                    currentOscillator,
                    previousOscillator,
                    _parameters.StochOverbought
                );

                bool crossingBelowOversold = CheckOscillatorCrossingBelowOversold(
                    currentOscillator,
                    previousOscillator,
                    _parameters.StochOversold
                );

                // Выход из ЛОНГА при пересечении перекупленности СВЕРХУ ВНИЗ
                if (_currentPosition.Direction == PositionDirection.Long && crossingAboveOverbought)
                {
                    shouldExit = true;
                    exitDirection = "Sell"; // Для выхода из лонга - Sell
                    exitReason = "Выход из лонга: пересечение уровня перекупленности СВЕРХУ ВНИЗ";
                    _logger.LogInformation($"Level crossing exit: LONG - Overbought crossing from above at {currentOscillator:F2}");
                }
                // Выход из ШОРТА при пересечении перепроданности СНИЗУ ВВЕРХ
                else if (_currentPosition.Direction == PositionDirection.Short && crossingBelowOversold)
                {
                    shouldExit = true;
                    exitDirection = "Buy"; // Для выхода из шорта - Buy
                    exitReason = "Выход из шорта: пересечение уровня перепроданности СНИЗУ ВВЕРХ";
                    _logger.LogInformation($"Level crossing exit: SHORT - Oversold crossing from below at {currentOscillator:F2}");
                }
                // Проверка защитного стоп-лосса для выхода по пересечению уровня
                else if (_levelCrossingExitProtectiveStopActive && _currentPosition.EntryPrice > 0)
                {
                    // Рассчитываем защитный стоп-лосс
                    _levelCrossingExitStopLossPrice = CalculateLevelCrossingExitStopLoss(
                        _currentPosition.Direction,
                        _currentPosition.EntryPrice
                    );

                    // Проверяем, не сработал ли защитный стоп-лосс
                    if (_currentPosition.Direction == PositionDirection.Long && _lastPrice <= _levelCrossingExitStopLossPrice)
                    {
                        shouldExit = true;
                        exitDirection = "Sell";
                        exitReason = $"Защитный стоп-лосс для выхода из лонга ({_parameters.LevelCrossingExitProtectiveStopPercent:F1}%)";
                        _logger.LogInformation($"Level crossing exit: LONG - Protective stop loss triggered at {_lastPrice:F2} (stop: {_levelCrossingExitStopLossPrice:F2})");
                    }
                    else if (_currentPosition.Direction == PositionDirection.Short && _lastPrice >= _levelCrossingExitStopLossPrice)
                    {
                        shouldExit = true;
                        exitDirection = "Buy";
                        exitReason = $"Защитный стоп-лосс для выхода из шорта ({_parameters.LevelCrossingExitProtectiveStopPercent:F1}%)";
                        _logger.LogInformation($"Level crossing exit: SHORT - Protective stop loss triggered at {_lastPrice:F2} (stop: {_levelCrossingExitStopLossPrice:F2})");
                    }
                }

                // Обновляем статус выхода
                if (!shouldExit)
                {
                    string statusMessage = "Ожидание пересечения уровня для выхода: \n";
                    if (_currentPosition.Direction == PositionDirection.Long)
                    {
                        statusMessage += $"пересечение {_parameters.StochOverbought:F1} СВЕРХУ ВНИЗ";
                    }
                    else
                    {
                        statusMessage += $"пересечение {_parameters.StochOversold:F1} СНИЗУ ВВЕРХ";
                    }

                    if (_levelCrossingExitProtectiveStopActive && _levelCrossingExitStopLossPrice > 0)
                    {
                        statusMessage += $"\nЗащитный стоп-лосс: {_levelCrossingExitStopLossPrice:F2}";
                    }

                    OnExitStatusChanged?.Invoke(statusMessage);
                }

                // ✅ ВАЖНО: Если shouldExit = true, выполняем выход и ВЫХОДИМ из метода
                if (shouldExit)
                {
                    await ExecuteRegularExitAsync(exitDirection, exitReason);
                    return;
                }

                // Для LevelCrossingExit больше ничего не проверяем
                return;
            }

            // ======================================================================
            // ДАЛЕЕ ИДЕТ ЛОГИКА ДЛЯ ДРУГИХ ТИПОВ ВЫХОДА (MovingTakeProfitExit, TrailingStopExit, Market)
            // ======================================================================

            // Для MovingTakeProfitExit с CloseOnSignalReversal
            if (_parameters.CloseOnSignalReversal && _exitState == ExitState.MovingTPExitActive)
            {
                bool isOversold = _indicatorValues.RsiValue < _parameters.RsiOversold && _indicatorValues.OscillatorValue < _parameters.StochOversold;
                bool isOverbought = _indicatorValues.RsiValue > _parameters.RsiOverbought && _indicatorValues.OscillatorValue > _parameters.StochOverbought;

                if (_currentPosition.Direction == PositionDirection.Long && isOverbought)
                {
                    shouldExitByMovingTPExitByChangeSignal = true;
                    shouldExitByMovingTPExit = false;
                    exitReason = "Смена сигнала на перекупленность";
                }
                else if (_currentPosition.Direction == PositionDirection.Short && isOversold)
                {
                    shouldExitByMovingTPExitByChangeSignal = true;
                    shouldExitByMovingTPExit = false;
                    exitReason = "Смена сигнала на перепроданность";
                }
                else if (_currentPosition != null && _currentPosition.Quantity != 0 && !shouldExitByMovingTPExitByChangeSignal)
                {
                    OnExitStatusChanged?.Invoke($"Ожидание противоположного сигнала \nдля начала расчета тейк-профита \nна выход из позиции");
                    _indicatorValues.LastAction = $"Ожидание противоположного сигнала \nдля начала расчета тейк-профита \nна выход из позиции";
                    _indicatorValues.LastActionTime = DateTime.Now;
                }
            }
            else if (!_parameters.CloseOnSignalReversal && _exitState == ExitState.MovingTPExitActive)
            {
                shouldExitByMovingTPExitByChangeSignal = false;
                shouldExitByMovingTPExit = true;
            }

            // ✅ ИСПРАВЛЕННОЕ УСЛОВИЕ: проверяем только MovingTakeProfitExit и TrailingStopExit
            if (_currentPosition == null ||
                (_parameters.ExitOrderType == OrderType.MovingTakeProfitExit && _exitState == ExitState.MovingTPExitActive) ||
                (_parameters.ExitOrderType == OrderType.TrailingStopExit && _exitState == ExitState.TrailingStopActive) ||
                _exitState == ExitState.ExitPending)
                return;

            // Проверка тейк-профита и стоп-лосса (для Market)
            if (!shouldExit && _currentPosition != null && _parameters.ExitOrderType == OrderType.Market)
            {
                if (_currentPosition.Direction == PositionDirection.Long &&
                    _lastPrice >= _currentPosition.TakeProfitPrice && _currentPosition.TakeProfitPrice > 0)
                {
                    shouldExit = true;
                    exitReason = "Достигнут тейк-профит";
                }
                else if (_currentPosition.Direction == PositionDirection.Short &&
                         _lastPrice <= _currentPosition.TakeProfitPrice && _currentPosition.TakeProfitPrice > 0)
                {
                    shouldExit = true;
                    exitReason = "Достигнут тейк-профит";
                }
                else if (_currentPosition.Direction == PositionDirection.Long &&
                         _lastPrice <= _currentPosition.StopLossPrice && _currentPosition.StopLossPrice > 0)
                {
                    shouldExit = true;
                    exitReason = "Сработал стоп-лосс";
                }
                else if (_currentPosition.Direction == PositionDirection.Short &&
                         _lastPrice >= _currentPosition.StopLossPrice && _currentPosition.StopLossPrice > 0)
                {
                    shouldExit = true;
                    exitReason = "Сработал стоп-лосс";
                }
            }

            if (shouldExit)
            {
                await ExecuteRegularExitAsync(exitDirection, exitReason);
            }
        }
        private async Task UpdateMovingTakeProfitEntryAsync()
        {
            if ((DateTime.Now - _lastEntryCheckTime).TotalMilliseconds < ENTRY_CHECK_INTERVAL_MS)
            {
                return;
            }

            _lastEntryCheckTime = DateTime.Now;

            if (_currentPosition != null || _pendingOrder != null)
            {
                return;
            }

            if (!_entryPass)
            {
                return;
            }

            try
            {
                bool shouldEnter = false;
                string direction = "";
                string entryReason = "Скользящий тейк-профит на входе";

                if (_indicatorValues.Signal.Contains("СИГНАЛ НА ПОКУПКУ"))
                {
                    direction = PositionDirection.Long;

                    if (_lastPrice < _movingTPEntryStartPrice)
                    {
                        await CalculatePositionSize();

                        _movingTPEntryStartPrice = _lastPrice;
                        _movingTPEntryTargetPrice = CalculateMovingTPEntryTargetLevel(
                            direction, _lastPrice, _movingTPEntryStartPrice);

                        _indicatorValues.LastAction = $"Скользящий TP на входе: новый минимум {_lastPrice:F2}, цель {_movingTPEntryTargetPrice:F2}";
                        _indicatorValues.LastActionTime = DateTime.Now;

                        OnEntryStatusChanged?.Invoke($"Скользящий TP на входе: новый минимум {_lastPrice:F2}, цель {_movingTPEntryTargetPrice:F2}");
                    }

                    if (_lastPrice >= _movingTPEntryTargetPrice && _lastPrice > _movingTPEntryStartPrice)
                    {
                        Debug.WriteLine("вход когда цена пересекает целевой уровень ВНИЗУ ВВЕРХ - РАЗРЕШЕН!");
                        shouldEnter = true;
                    }
                }
                else if (_indicatorValues.Signal.Contains("СИГНАЛ НА ПРОДАЖУ"))
                {
                    direction = PositionDirection.Short;

                    if (_lastPrice > _movingTPEntryStartPrice)
                    {
                        await CalculatePositionSize();

                        _movingTPEntryStartPrice = _lastPrice;
                        _movingTPEntryTargetPrice = CalculateMovingTPEntryTargetLevel(
                            direction, _lastPrice, _movingTPEntryStartPrice);

                        _indicatorValues.LastAction = $"Скользящий TP на входе: новый максимум {_lastPrice:F2}, цель {_movingTPEntryTargetPrice:F2}";
                        _indicatorValues.LastActionTime = DateTime.Now;

                        OnEntryStatusChanged?.Invoke($"Скользящий TP на входе: новый максимум {_lastPrice:F2}, цель {_movingTPEntryTargetPrice:F2}");
                    }

                    if (_lastPrice <= _movingTPEntryTargetPrice && _lastPrice < _movingTPEntryStartPrice)
                    {
                        shouldEnter = true;
                    }
                }
                else
                {
                    _entryState = EntryState.NoSignal;
                    OnEntryStatusChanged?.Invoke($"Ожидание торгового сигнала");
                    _indicatorValues.LastAction = $"Ожидание торгового сигнала";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    return;
                }

                if ((DateTime.Now - _movingTPEntryStartTime).TotalMinutes > _parameters.MovingTPEntryTimeoutMinutes)
                {
                    _entryState = EntryState.EntryFailed;
                    OnEntryStatusChanged?.Invoke("Тайм-аут скользящего TP на входе");
                    _indicatorValues.LastAction = "Скользящий TP на входе: тайм-аут, вход отменен";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _logger.LogWarning($"Moving TP entry timeout for {_instrument.Ticker}");
                    return;
                }

                if (!shouldEnter)
                {
                    string statusMessage = $"Скользящий TP на входе активен: " +
                                         $"\nНачальная цена: {_movingTPEntryStartPrice:F2}, " +
                                         $"\nТекущая цена: {_lastPrice:F2}, " +
                                         $"\nЦель: {_movingTPEntryTargetPrice:F2}, " +
                                         $"\nПланируемая позиция {lotPositionSize} лот на {positionValueMoney} руб, " +
                                         $"\nПрошло времени: {(DateTime.Now - _movingTPEntryStartTime).TotalMinutes:F1} мин";

                    if (_indicatorValues.EntryStatusDetails != statusMessage)
                    {
                        OnEntryStatusChanged?.Invoke(statusMessage);
                    }
                }

                if (shouldEnter && _entryPass)
                {
                    _entryPass = false;
                    _entryState = EntryState.OrderPending;

                    Debug.WriteLine($"UpdateMovingTakeProfitEntryAsync: вход в позицию shouldEnter=TRUE {direction} {entryReason}");

                    try
                    {
                        await ExecuteMovingTPEntryAsync(direction, entryReason);
                        Debug.WriteLine("UpdateMovingTakeProfitEntryAsync: вход ВЫПОЛНЕН!!!");
                    }
                    catch (Exception ex)
                    {
                        _entryPass = true;
                        _logger.LogError(ex, "Ошибка входа");
                        throw;
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                _entryState = EntryState.EntryFailed;
                OnEntryStatusChanged?.Invoke($"Ошибка скользящего TP на входе: {ex.Message}");
                _logger.LogError(ex, "Error updating moving take profit entry");
                _indicatorValues.LastAction = $"Ошибка обновления скользящего TP на входе: {ex.Message}";
            }
        }
        private async Task UpdateMovingTakeProfitExitAsync()
        {
            if (_exitState == ExitState.ExitPending || _pendingOrder != null)
            {
                //Debug.WriteLine($"UpdateMovingTakeProfitExitAsync: пропускаем, есть активный ордер");
                return;
            }

            if (_exitState != ExitState.MovingTPExitActive || _currentPosition == null)
                return;

            if (!IsPositionForCurrentInstrument(_currentPosition))
            {
                Debug.WriteLine($"UpdateMovingTakeProfitExitAsync: [{_currentInstrumentTicker}] Позиция не принадлежит инструменту");
                _currentPosition = null;
                _exitState = ExitState.NoPosition;
                return;
            }

            if (string.IsNullOrEmpty(_currentPosition.Direction) || _currentPosition.EntryPrice <= 0)
            {
                _logger.LogWarning($"Некорректные данные позиции. Сброс.");
                _currentPosition = null;
                _exitState = ExitState.NoPosition;
                ResetExitVariables();
                return;
            }

            if (_exitState != ExitState.MovingTPExitActive)
            {
                if (ShouldActivateMovingTPExit())
                {
                    InitializeMovingTPExit();
                }
                else
                {
                    return;
                }
            }

            if (_pendingOrder != null)
            {
                //Debug.WriteLine("UpdateMovingTakeProfitExitAsync: блокировка, обработка уже идет");
                return;
            }

            if (!_exitPass)
            {
                return;
            }

            try
            {
                bool shouldExit = false;
                string exitDirection = "";
                string exitReason = "Скользящий тейк-профит на выходе";

                string positionDirection = _currentPosition.Direction;

                if (positionDirection == PositionDirection.Long)
                {
                    if (_lastPrice > _movingTPExitCurrentLevel)
                    {
                        _movingTPExitCurrentLevel = _lastPrice;
                        _movingTPExitTargetPrice = CalculateMovingTPExitTargetLevel(
                            PositionDirection.Long, _lastPrice, _movingTPExitCurrentLevel);

                        Debug.WriteLine($"Лонг: новый максимум {_lastPrice:F2}, цель выхода {_movingTPExitTargetPrice:F2}");

                        OnExitStatusChanged?.Invoke($"Скользящий TP на выходе: новый максимум {_lastPrice:F2}, \nцель выхода {_movingTPExitTargetPrice:F2}");
                        _indicatorValues.LastAction = $"Скользящий TP на выходе: новый максимум {_lastPrice:F2}, \nцель выхода {_movingTPExitTargetPrice:F2}";
                        _indicatorValues.LastActionTime = DateTime.Now;
                    }

                    if (_lastPrice <= _movingTPExitTargetPrice)
                    {
                        shouldExit = true;
                        exitDirection = "Sell";
                    }
                }
                else if (positionDirection == PositionDirection.Short)
                {
                    if (_lastPrice < _movingTPExitCurrentLevel)
                    {
                        _movingTPExitCurrentLevel = _lastPrice;
                        _movingTPExitTargetPrice = CalculateMovingTPExitTargetLevel(
                            PositionDirection.Short, _lastPrice, _movingTPExitCurrentLevel);

                        Debug.WriteLine($"Шорт: новый минимум {_lastPrice:F2}, цель выхода {_movingTPExitTargetPrice:F2}");

                        OnExitStatusChanged?.Invoke($"Скользящий TP на выходе: новый минимум {_lastPrice:F2}, \nцель выхода {_movingTPExitTargetPrice:F2}");
                        _indicatorValues.LastAction = $"Скользящий TP на выходе: новый минимум {_lastPrice:F2}, \nцель выхода {_movingTPExitTargetPrice:F2}";
                        _indicatorValues.LastActionTime = DateTime.Now;
                    }

                    if (_lastPrice >= _movingTPExitTargetPrice)
                    {
                        shouldExit = true;
                        exitDirection = "Buy";
                    }
                }

                if ((DateTime.Now - _movingTPExitStartTime).TotalMinutes > _parameters.MovingTPExitTimeoutMinutes)
                {
                    _exitState = ExitState.ExitFailed;
                    OnExitStatusChanged?.Invoke("Тайм-аут скользящего TP на выходе");
                    _indicatorValues.LastAction = "Скользящий TP на выходе: тайм-аут, выход отменен";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _logger.LogWarning($"Moving TP exit timeout for {_instrument.Ticker}");
                    return;
                }

                if (shouldExit && _pendingOrder == null && _exitPass)
                {
                    _exitPass = false;

                    Debug.WriteLine($"UpdateMovingTakeProfitExitAsync: Триггер выхода: Цена={_lastPrice:F2}, Цель={_movingTPExitTargetPrice:F2}");
                    _logger.LogInformation($"Триггер выхода: Цена={_lastPrice:F2}, Цель={_movingTPExitTargetPrice:F2}");

                    try
                    {
                        await ExecuteMovingTPExitAsync(exitDirection, exitReason);
                        _entryPass = false;
                        _exitPass = false;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка при выполнении выхода");
                        _entryPass = false;
                        _exitPass = true;
                    }
                }
                else
                {
                    _entryPass = false;
                    _exitPass = true;
                }
            }
            catch (Exception ex)
            {
                _exitState = ExitState.ExitFailed;
                OnExitStatusChanged?.Invoke($"Ошибка скользящего TP на выходе: {ex.Message}");
                _logger.LogError(ex, "Error updating moving take profit exit");
                _indicatorValues.LastAction = $"Ошибка обновления скользящего TP на выходе: {ex.Message}";
            }
        }
        private async Task UpdateTrailingStopExitAsync()
        {
            if (_exitState != ExitState.TrailingStopActive || _currentPosition == null)
                return;

            // ✅ ПРОВЕРЯЕМ, ЧТО ЦЕНА ВХОДА КОРРЕКТНА
            if (_currentPosition.EntryPrice <= 0)
            {
                Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Некорректная цена входа: {_currentPosition.EntryPrice}");
                var dealFromDb = await GetOpenDealFromDatabaseAsync(_currentInstrumentUid);
                if (dealFromDb != null && dealFromDb.EntryPrice > 0)
                {
                    _currentPosition.EntryPrice = dealFromDb.EntryPrice;
                    _indicatorValues.EntryPrice = dealFromDb.EntryPrice;
                    Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Цена входа восстановлена из БД: {_currentPosition.EntryPrice}");
                }
                else
                {
                    var entryPriceFromHistory = await GetEntryPriceFromOperationsHistoryAsync(_currentInstrumentUid);
                    if (entryPriceFromHistory > 0)
                    {
                        _currentPosition.EntryPrice = entryPriceFromHistory;
                        _indicatorValues.EntryPrice = entryPriceFromHistory;
                        Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Цена входа восстановлена из истории: {_currentPosition.EntryPrice}");
                    }
                    else
                    {
                        Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} НЕ УДАЛОСЬ ВОССТАНОВИТЬ ЦЕНУ ВХОДА!");
                        return;
                    }
                }
            }

            // Расчет текущей прибыли в процентах
            decimal currentPnLPercent = 0;
            if (_currentPosition.EntryPrice > 0 && _lastPrice > 0)
            {
                if (_currentPosition.Direction == PositionDirection.Long)
                {
                    currentPnLPercent = (_lastPrice - _currentPosition.EntryPrice) / _currentPosition.EntryPrice * 100;
                }
                else if (_currentPosition.Direction == PositionDirection.Short)
                {
                    currentPnLPercent = (_currentPosition.EntryPrice - _lastPrice) / _currentPosition.EntryPrice * 100;
                }
            }

            // ✅ ИСПРАВЛЕНИЕ: Используем параметр из настроек, а не жестко закодированное значение!
            decimal protectiveStopPercent = _parameters.ProtectiveStopPercent;
            decimal protectiveStopLevel = 0;

            if (_currentPosition.Direction == PositionDirection.Long)
            {
                // Защитный стоп ниже цены входа
                protectiveStopLevel = _currentPosition.EntryPrice * (1 - protectiveStopPercent / 100);
                //Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} LONG - Entry={_currentPosition.EntryPrice:F4}, ProtectiveStop={protectiveStopLevel:F4} ({protectiveStopPercent}%)");
            }
            else if (_currentPosition.Direction == PositionDirection.Short)
            {
                // Защитный стоп ВЫШЕ цены входа (для шорта)
                protectiveStopLevel = _currentPosition.EntryPrice * (1 + protectiveStopPercent / 100);
                //Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} SHORT - Entry={_currentPosition.EntryPrice:F4}, ProtectiveStop={protectiveStopLevel:F4} ({protectiveStopPercent}%)");
            }

            // ✅ ПРОВЕРКА ЗАЩИТНОГО СТОП-ЛОССА
            bool shouldExitByProtectiveStop = false;
            string protectiveStopReason = "";

            if (_currentPosition.Direction == PositionDirection.Long && _lastPrice <= protectiveStopLevel)
            {
                shouldExitByProtectiveStop = true;
                protectiveStopReason = $"Защитный стоп-лосс (выход по {protectiveStopPercent}% от цены входа)";
                Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Сработал ЗАЩИТНЫЙ СТОП LONG! Price={_lastPrice:F4} <= {protectiveStopLevel:F4}");
            }
            else if (_currentPosition.Direction == PositionDirection.Short && _lastPrice >= protectiveStopLevel)
            {
                shouldExitByProtectiveStop = true;
                protectiveStopReason = $"Защитный стоп-лосс (выход по {protectiveStopPercent}% от цены входа)";
                Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Сработал ЗАЩИТНЫЙ СТОП SHORT! Price={_lastPrice:F4} >= {protectiveStopLevel:F4}");
            }

            // ✅ ЕСЛИ СРАБОТАЛ ЗАЩИТНЫЙ СТОП - ВЫХОДИМ НЕМЕДЛЕННО
            if (shouldExitByProtectiveStop)
            {
                OnExitStatusChanged?.Invoke($"🔴 {protectiveStopReason}");
                _indicatorValues.LastAction = protectiveStopReason;

                string exitDirection = _currentPosition.Direction == PositionDirection.Long ? "Sell" : "Buy";
                await ExecuteTrailingStopExitAsync(exitDirection, protectiveStopReason);
                return;
            }

            // ✅ ДАЛЕЕ ЛОГИКА АКТИВАЦИИ ТРЕЙЛИНГ-СТОПА
            if (!_trailingStopExitActivated)
            {
                if (currentPnLPercent >= _parameters.TrailingStopExitActivationPercent)
                {
                    _trailingStopExitActivated = true;
                    _trailingStopExitBestPrice = _lastPrice;
                    _trailingStopExitCurrentLevel = CalculateTrailingStopExitLevel(_currentPosition.Direction, _lastPrice);

                    _indicatorValues.LastAction = $"📈 Трейлинг-стоп АКТИВИРОВАН при прибыли {currentPnLPercent:F2}%";
                    OnExitStatusChanged?.Invoke($"✅ Трейлинг-стоп активирован! Уровень: {_trailingStopExitCurrentLevel:F2}");
                    Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Трейлинг-стоп АКТИВИРОВАН!");
                }
                else
                {
                    OnExitStatusChanged?.Invoke($"⏳ Ожидание активации трейлинг-стопа: {currentPnLPercent:F2}% / {_parameters.TrailingStopExitActivationPercent}% \n(защитный стоп: {protectiveStopLevel:F2})");
                    return;
                }
            }

            // ✅ ОСНОВНАЯ ЛОГИКА ТРЕЙЛИНГ-СТОПА (после активации)
            try
            {
                bool shouldExit = false;
                string exitDirection = "";
                string exitReason = "Трейлинг-стоп";

                if (_currentPosition.Direction == PositionDirection.Long)
                {
                    if (_lastPrice <= _trailingStopExitCurrentLevel)
                    {
                        shouldExit = true;
                        exitDirection = "Sell";
                        Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Триггер выхода LONG! Price={_lastPrice:F4} <= Stop={_trailingStopExitCurrentLevel:F4}");
                    }
                    else if (_lastPrice > _trailingStopExitBestPrice)
                    {
                        _trailingStopExitBestPrice = _lastPrice;
                        _trailingStopExitCurrentLevel = CalculateTrailingStopExitLevel(PositionDirection.Long, _lastPrice);

                        OnOrderStatusChanged?.Invoke($"📈 Трейлинг-стоп: уровень повышен до {_trailingStopExitCurrentLevel:F2}");
                        _indicatorValues.LastAction = $"Трейлинг-стоп: уровень повышен до {_trailingStopExitCurrentLevel:F2}";
                        _indicatorValues.LastActionTime = DateTime.Now;
                        Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Стоп повышен: {_trailingStopExitCurrentLevel:F4}");
                    }
                }
                else if (_currentPosition.Direction == PositionDirection.Short)
                {
                    if (_lastPrice >= _trailingStopExitCurrentLevel)
                    {
                        shouldExit = true;
                        exitDirection = "Buy";
                        Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Триггер выхода SHORT! Price={_lastPrice:F4} >= Stop={_trailingStopExitCurrentLevel:F4}");
                    }
                    else if (_lastPrice < _trailingStopExitBestPrice)
                    {
                        _trailingStopExitBestPrice = _lastPrice;
                        _trailingStopExitCurrentLevel = CalculateTrailingStopExitLevel(PositionDirection.Short, _lastPrice);

                        OnOrderStatusChanged?.Invoke($"📉 Трейлинг-стоп: уровень понижен до {_trailingStopExitCurrentLevel:F2}");
                        _indicatorValues.LastAction = $"Трейлинг-стоп: уровень понижен до {_trailingStopExitCurrentLevel:F2}";
                        _indicatorValues.LastActionTime = DateTime.Now;
                        Debug.WriteLine($"UpdateTrailingStopExitAsync: {_instrument.Ticker} Стоп понижен: {_trailingStopExitCurrentLevel:F4}");
                    }
                }

                if (shouldExit)
                {
                    await ExecuteTrailingStopExitAsync(exitDirection, exitReason);
                }
            }
            catch (Exception ex)
            {
                _exitState = ExitState.ExitFailed;
                OnExitStatusChanged?.Invoke($"Ошибка трейлинг-стопа: {ex.Message}");
                _logger.LogError(ex, "Error updating trailing stop exit");
                _indicatorValues.LastAction = $"Ошибка обновления трейлинг-стопа: {ex.Message}";
            }
        }
        private bool ShouldActivateMovingTPExit()
        {
            if (_currentPosition == null || _lastPrice <= 0 || _currentPosition.EntryPrice <= 0)
                return false;

            decimal currentPnLPercent = 0;

            if (_currentPosition.Direction == PositionDirection.Long)
            {
                currentPnLPercent = (_lastPrice - _currentPosition.EntryPrice) / _currentPosition.EntryPrice * 100;
            }
            else if (_currentPosition.Direction == PositionDirection.Short)
            {
                currentPnLPercent = (_currentPosition.EntryPrice - _lastPrice) / _currentPosition.EntryPrice * 100;
            }

            decimal minProfitPercent = 0.1m;
            return currentPnLPercent >= minProfitPercent;
        }
        private decimal CalculateMovingTPExitTargetLevel(string direction, decimal currentPrice, decimal bestPrice)
        {
            decimal targetLevel = 0;
            decimal offset = CalculateMovingTPExitOffset();

            Debug.WriteLine($"DEBUG: РАСЧЕТ TP выхода {_instrument.Ticker}: Direction={direction}, CurrentPrice={currentPrice:F2}, " +
                           $"BestPrice={bestPrice:F2}, Offset={offset:F4}, ATR={_indicatorValues.AtrValue:F4}");

            if (direction == PositionDirection.Long)
            {
                targetLevel = bestPrice - offset;
                Debug.WriteLine($"DEBUG: {_instrument.Ticker} Лонг exit: Best={bestPrice:F2} - Offset={offset:F4} = Target={targetLevel:F4}");
            }
            else if (direction == PositionDirection.Short)
            {
                targetLevel = bestPrice + offset;
                Debug.WriteLine($"DEBUG: {_instrument.Ticker} Шорт exit: Best={bestPrice:F2} + Offset={offset:F4} = Target={targetLevel:F4}");
            }
            else
            {
                _logger.LogError($"ОШИБКА: Неизвестное направление: {direction}");
                Debug.WriteLine($"ОШИБКА: Неизвестное направление: {direction}");
                return 0;
            }

            if (_parameters.MovingTPExitSlippage > 0)
            {
                decimal slippageAmount = targetLevel * (_parameters.MovingTPExitSlippage / 100);
                if (direction == PositionDirection.Long)
                    targetLevel -= slippageAmount;
                else if (direction == PositionDirection.Short)
                    targetLevel += slippageAmount;
            }

            return targetLevel;
        }
        private decimal CalculateMovingTPExitOffset()
        {
            decimal offset = 0;

            switch (_parameters.MovingTPExitCalculationType)
            {
                case PriceCalculationType.Percentage:
                    offset = _lastPrice * (_parameters.MovingTPExitStartPercent / 100);
                    break;

                case PriceCalculationType.Absolute:
                    offset = _parameters.MovingTPExitStartAbsolute;
                    break;

                case PriceCalculationType.ATR:
                    offset = _indicatorValues.AtrValue * _parameters.AtrMultiplier;
                    if (offset <= 0) offset = _lastPrice * 0.02m;
                    break;
            }

            decimal minOffset = _lastPrice * 0.001m;
            if (offset < minOffset)
                offset = minOffset;

            return offset;
        }
        #endregion

        #region Signal Generation and Calculation Methods
        private void GenerateTradingSignals()
        {
            // Проверка наличия данных
            if (_lastPrice <= 0)
            {
                _indicatorValues.Status = "ОЖИДАНИЕ ДАННЫХ";
                _indicatorValues.StatusColor = Brushes.Gray;
                _indicatorValues.Signal = "ОЖИДАНИЕ ЦЕНЫ";
                _indicatorValues.SignalColor = Brushes.Gray;
                _indicatorValues.SignalDescription = "Ожидание поступления рыночных данных";
                ClearOrderPrices();
                return;
            }

            // Сохраняем предыдущее значение осциллятора для определения пересечений
            // ✅ Используем значения, уже сохраненные в ProcessStrategyLogicAsync
            decimal currentOscillator = _indicatorValues.OscillatorValue;
            decimal previousOscillator = _indicatorValues.PreviousOscillatorValue;

            //Debug.WriteLine($"DEBUG: previousOscillator={previousOscillator:F4}  currentOscillator={currentOscillator:F4} {_currentInstrumentTicker} ");
            
            
            // ✅ КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ: проверяем, что у нас есть валидные значения
            // Если текущее значение или предыдущее значение равны 0 - это начальное состояние, 
            // пропускаем определение пересечений
            bool hasValidOscillatorValues = currentOscillator > 0 && previousOscillator > 0;

            // Проверяем пересечения уровней ТОЛЬКО если есть валидные значения
            bool crossingAboveOverbought = false;
            bool crossingBelowOversold = false;



            if (hasValidOscillatorValues)
            {
                crossingAboveOverbought = CheckOscillatorCrossingAboveOverbought(
                    currentOscillator,
                    previousOscillator,
                    _parameters.StochOverbought
                );

                crossingBelowOversold = CheckOscillatorCrossingBelowOversold(
                    currentOscillator,
                    previousOscillator,
                    _parameters.StochOversold
                );
            }


            // Обновляем состояние для UI
            if (crossingBelowOversold)
            {
                _indicatorValues.Status = "ПЕРЕСЕЧЕНИЕ ПЕРЕПРОДАННОСТИ СНИЗУ ВВЕРХ";
                _indicatorValues.StatusColor = Brushes.DarkGreen;
            }
            else if (crossingAboveOverbought)
            {
                _indicatorValues.Status = "ПЕРЕСЕЧЕНИЕ ПЕРЕКУПЛЕННОСТИ СВЕРХУ ВНИЗ";
                _indicatorValues.StatusColor = Brushes.DarkRed;
            }


            // ======================================================================
            // ЧАСТЬ 1: ОБРАБОТКА АКТИВНОЙ ПОЗИЦИИ (сигналы на выход)
            // ======================================================================
            if (_exitPass && _currentPosition != null)
            {
                if (_parameters.ExitOrderType == OrderType.LevelCrossingExit)
                {
                    // ✅ Используем hasValidOscillatorValues
                    if (hasValidOscillatorValues)
                    {
                        if (_currentPosition.Direction == PositionDirection.Long && crossingAboveOverbought)
                        {
                            _indicatorValues.Signal = "СИГНАЛ НА ВЫХОД ИЗ ЛОНГА";
                            _indicatorValues.SignalColor = Brushes.DarkRed;
                            _indicatorValues.SignalDescription = $"Пересечение перекупленности {_parameters.StochOverbought:F1} СВЕРХУ ВНИЗ";
                        }
                        else if (_currentPosition.Direction == PositionDirection.Short && crossingBelowOversold)
                        {
                            _indicatorValues.Signal = "СИГНАЛ НА ВЫХОД ИЗ ШОРТА";
                            _indicatorValues.SignalColor = Brushes.DarkGreen;
                            _indicatorValues.SignalDescription = $"Пересечение перепроданности {_parameters.StochOversold:F1} СНИЗУ ВВЕРХ";
                        }
                        else
                        {
                            _indicatorValues.Signal = $"ПОЗИЦИЯ АКТИВНА - ОЖИДАНИЕ ВЫХОДА";
                            _indicatorValues.SignalColor = Brushes.Gray;
                            _indicatorValues.SignalDescription = $"Текущий {_indicatorValues.OscillatorName}: {currentOscillator:F2}";
                        }
                    }
                    else
                    {
                        _indicatorValues.Signal = $"ПОЗИЦИЯ АКТИВНА - ИНИЦИАЛИЗАЦИЯ";
                        _indicatorValues.SignalColor = Brushes.Gray;
                        _indicatorValues.SignalDescription = $"Ожидание валидных значений осциллятора";
                    }
                }
                else
                {
                    CalculateExitPrices();
                    if (_currentPosition.Direction == PositionDirection.Long)
                    {
                        _indicatorValues.Signal = $"В ЛОНГ ПОЗИЦИИ {_currentPosition.Quantity} лотов";
                        _indicatorValues.SignalColor = Brushes.LightGreen;
                        _indicatorValues.SignalDescription = $"Позиция: {_currentPosition.Quantity} лотов, P&L: {_indicatorValues.CurrentPnL:F2}";
                    }
                    else if (_currentPosition.Direction == PositionDirection.Short)
                    {
                        _indicatorValues.Signal = $"В ШОРТ ПОЗИЦИИ -{Math.Abs(_currentPosition.Quantity)} лотов";
                        _indicatorValues.SignalColor = Brushes.LightCoral;
                        _indicatorValues.SignalDescription = $"Позиция: -{Math.Abs(_currentPosition.Quantity)} лотов, P&L: {_indicatorValues.CurrentPnL:F2}";
                    }
                }

                _indicatorValues.Status = "ПОЗИЦИЯ АКТИВНА";
                _indicatorValues.StatusColor = Brushes.Gray;
                return;
            }


            // ======================================================================
            // ЧАСТЬ 2: ОБРАБОТКА ОТСУТСТВИЯ ПОЗИЦИИ (сигналы на вход)
            // ======================================================================

            // ✅ ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: убеждаемся, что позиция действительно отсутствует
            // Это защита от ситуации, когда позиция уже есть, но _currentPosition еще не обновлен
            if (_currentPosition != null)
            {
                return;
            }

            // ✅ ВАЖНО: Для LevelCrossingEntry НЕ используем обычные условия isOversold/isOverbought
            // Только пересечения уровней!
            if (_parameters.EntryOrderType == OrderType.LevelCrossingEntry)
            {
                if (_entryPass)
                {

                    // ✅ Используем hasValidOscillatorValues
                    if (hasValidOscillatorValues)
                    {
                        // Сигнал на покупку (лонг) при пересечении перепроданности СНИЗУ ВВЕРХ
                        if (crossingBelowOversold)
                        {
                            _indicatorValues.Signal = "СИГНАЛ НА ПОКУПКУ (Лонг) - ПЕРЕСЕЧЕНИЕ УРОВНЯ";
                            _indicatorValues.SignalColor = Brushes.DarkGreen;
                            _indicatorValues.SignalDescription = $"Пересечение уровня перепроданности ({_parameters.StochOversold:F1}) СНИЗУ ВВЕРХ";
                            CalculateEntryPrices(PositionDirection.Long);

                            _levelCrossingEntryProtectiveStopActive = true;
                            _levelCrossingEntryStopLossPrice = CalculateLevelCrossingEntryStopLoss(PositionDirection.Long, _lastPrice);
                            _levelCrossingEntryTakeProfitPrice = CalculateLevelCrossingEntryTakeProfit(PositionDirection.Long, _lastPrice);
                        }
                        // Сигнал на продажу (шорт) при пересечении перекупленности СВЕРХУ ВНИЗ
                        else if (crossingAboveOverbought)
                        {
                            _indicatorValues.Signal = "СИГНАЛ НА ПРОДАЖУ (Шорт) - ПЕРЕСЕЧЕНИЕ УРОВНЯ";
                            _indicatorValues.SignalColor = Brushes.DarkRed;
                            _indicatorValues.SignalDescription = $"Пересечение уровня перекупленности ({_parameters.StochOverbought:F1}) СВЕРХУ ВНИЗ";
                            CalculateEntryPrices(PositionDirection.Short);

                            _levelCrossingEntryProtectiveStopActive = true;
                            _levelCrossingEntryStopLossPrice = CalculateLevelCrossingEntryStopLoss(PositionDirection.Short, _lastPrice);
                            _levelCrossingEntryTakeProfitPrice = CalculateLevelCrossingEntryTakeProfit(PositionDirection.Short, _lastPrice);
                        }
                        else
                        {
                            _indicatorValues.Signal = "ОЖИДАНИЕ ПЕРЕСЕЧЕНИЯ УРОВНЯ";
                            _indicatorValues.SignalColor = Brushes.Gray;
                            _indicatorValues.SignalDescription = $"Ожидание пересечения уровня перекупленности ({_parameters.StochOverbought:F1}) или перепроданности ({_parameters.StochOversold:F1})";
                            ClearOrderPrices();
                        }
                    }
                    else
                    {
                        // ✅ Простое сообщение об инициализации
                        _indicatorValues.Signal = "ИНИЦИАЛИЗАЦИЯ ИНДИКАТОРОВ";
                        _indicatorValues.SignalColor = Brushes.Gray;
                        _indicatorValues.SignalDescription = $"Ожидание валидных значений осциллятора (текущий: {currentOscillator:F2})";
                        ClearOrderPrices();
                    }
                }
                return; // ✅ ВАЖНО: выходим, чтобы не проверять другие условия
            }



            // ======================================================================
            // ЛОГИКА ДЛЯ ДРУГИХ ТИПОВ ВХОДА (Market, Limit, StopLimit, MovingTakeProfitEntry)
            // ======================================================================

            bool isOversold = _indicatorValues.RsiValue < _parameters.RsiOversold &&
                              _indicatorValues.OscillatorValue < _parameters.StochOversold;

            bool isOverbought = _indicatorValues.RsiValue > _parameters.RsiOverbought &&
                                _indicatorValues.OscillatorValue > _parameters.StochOverbought;




            // Обновляем статус
            if (isOversold)
            {
                _indicatorValues.Status = "ПЕРЕПРОДАННОСТЬ";
                _indicatorValues.StatusColor = Brushes.Red;
            }
            else if (isOverbought)
            {
                _indicatorValues.Status = "ПЕРЕКУПЛЕННОСТЬ";
                _indicatorValues.StatusColor = Brushes.Green;
            }
            else
            {
                _indicatorValues.Status = "НЕЙТРАЛЬНО";
                _indicatorValues.StatusColor = Brushes.Gray;
            }

            // Генерация торговых сигналов ТОЛЬКО если нет позиции
            if (_entryPass)
            {
                if (isOversold)
                {
                    _indicatorValues.Signal = "СИГНАЛ НА ПОКУПКУ (Лонг)";
                    _indicatorValues.SignalColor = Brushes.DarkGreen;
                    _indicatorValues.SignalDescription = "Сильная перепроданность";
                    CalculateEntryPrices(PositionDirection.Long);
                }
                else if (isOverbought)
                {
                    _indicatorValues.Signal = "СИГНАЛ НА ПРОДАЖУ (Шорт)";
                    _indicatorValues.SignalColor = Brushes.DarkRed;
                    _indicatorValues.SignalDescription = "Сильная перекупленность";
                    CalculateEntryPrices(PositionDirection.Short);
                }
                else
                {
                    _indicatorValues.Signal = "ОЖИДАНИЕ СИГНАЛА";
                    _indicatorValues.SignalColor = Brushes.Gray;
                    _indicatorValues.SignalDescription = "Ожидание условий для входа";
                    ClearOrderPrices();
                }
            }
        }
        private void CalculateEntryPrices(string direction)
        {
            decimal entryPrice = _lastPrice;
            decimal takeProfit = 0;
            decimal stopLoss = 0;

            if (_lastPrice <= 0)
            {
                ClearOrderPrices();
                return;
            }


            // Для ВХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ используем текущую цену
            if (_parameters.EntryOrderType == OrderType.LevelCrossingEntry)
            {
                entryPrice = _lastPrice;

                // Рассчитываем защитный стоп-лосс
                stopLoss = CalculateLevelCrossingEntryStopLoss(direction, entryPrice);
                takeProfit = CalculateLevelCrossingEntryTakeProfit(direction, entryPrice);

                _levelCrossingEntryStopLossPrice = stopLoss;
                _levelCrossingEntryTakeProfitPrice = takeProfit;
                _levelCrossingEntryProtectiveStopActive = true;
            }
            else
            {
                // Существующая логика для других типов входа
                switch (_parameters.EntryOrderType)
                {
                    case OrderType.Market:
                        entryPrice = _lastPrice;
                        break;

                    case OrderType.Limit:
                        if (direction == PositionDirection.Long)
                            entryPrice = CalculateLimitPrice(_lastPrice, direction, _parameters.EntryLimitOffsetPercent, true);
                        else
                            entryPrice = CalculateLimitPrice(_lastPrice, direction, _parameters.EntryLimitOffsetPercent, false);
                        break;

                    case OrderType.StopLimit:
                        if (direction == PositionDirection.Long)
                            entryPrice = CalculateStopLimitPrice(_lastPrice, direction, _parameters.EntryStopOffsetPercent, true);
                        else
                            entryPrice = CalculateStopLimitPrice(_lastPrice, direction, _parameters.EntryStopOffsetPercent, false);
                        break;

                    case OrderType.MovingTakeProfitEntry:
                        // Для скользящего тейк-профита на входе - инициализируем начальные значения
                        if (_entryState != EntryState.MovingTPEntryActive)
                        {
                            _movingTPEntryStartPrice = _lastPrice;
                            _movingTPEntryCurrentLevel = _lastPrice;
                            _movingTPEntryTargetPrice = CalculateMovingTPEntryTargetLevel(direction, _lastPrice, _lastPrice);
                            _movingTPEntryStartTime = DateTime.Now;

                            entryPrice = _movingTPEntryTargetPrice;
                            _entryState = EntryState.MovingTPEntryActive;

                            OnEntryStatusChanged?.Invoke($"Скользящий TP на входе: мониторинг цены {_lastPrice:F2} -> цель {_movingTPEntryTargetPrice:F2}");

                            _indicatorValues.LastAction = $"Активирован скользящий TP на входе: мониторинг от {_lastPrice:F2}";
                            _indicatorValues.LastActionTime = DateTime.Now;
                        }
                        else
                        {
                            entryPrice = _movingTPEntryTargetPrice;
                        }
                        break;

                    case OrderType.MovingTakeProfitExit:
                    case OrderType.TrailingStopExit:
                        // Эти типы не используются для входа
                        entryPrice = _lastPrice;
                        break;
                }
            }

            // Если не используется скользящий тейк-профит на входе, рассчитываем тейк-профит и стоп-лосс
            if (_parameters.EntryOrderType != OrderType.MovingTakeProfitEntry &&
        _parameters.EntryOrderType != OrderType.LevelCrossingEntry)
            {
                // Расчет тейк-профита для выхода
                takeProfit = CalculateTakeProfitPrice(entryPrice, direction, _parameters.TakeProfitCalculationType,
                    _parameters.TakeProfitPercent, _parameters.TakeProfitAbsolute, _parameters.AtrMultiplier,
                    _indicatorValues.AtrValue, _parameters.TakeProfitActivationPrice, _parameters.TakeProfitSlippage);

                // Расчет стоп-лосса
                stopLoss = CalculateStopLossPrice(entryPrice, direction, _parameters.StopLossCalculationType,
                   _parameters.StopLossPercent, _parameters.StopLossAbsolute, _parameters.AtrMultiplier,
                   _indicatorValues.AtrValue, _parameters.StopLossActivationPrice, _parameters.StopLossSlippage);

                // Учитываем проскальзывание
                if (_parameters.EntrySlippage > 0)
                {
                    decimal slippageAmount = entryPrice * (_parameters.EntrySlippage / 100);
                    if (direction == PositionDirection.Long)
                        entryPrice += slippageAmount;
                    else
                        entryPrice -= slippageAmount;
                }

                _indicatorValues.TakeProfitPrice = takeProfit;
                _indicatorValues.StopLossPrice = stopLoss;
                _indicatorValues.MovingTPExitPrice = 0;

                // Расчет потенциальной прибыли/убытка
                if (entryPrice > 0)
                {
                    if (direction == PositionDirection.Long)
                    {
                        _indicatorValues.PotentialProfit = (takeProfit - entryPrice) / entryPrice * 100;
                        _indicatorValues.PotentialLoss = (entryPrice - stopLoss) / entryPrice * 100;
                    }
                    else
                    {
                        _indicatorValues.PotentialProfit = (entryPrice - takeProfit) / entryPrice * 100;
                        _indicatorValues.PotentialLoss = (stopLoss - entryPrice) / entryPrice * 100;
                    }
                }
                else
                {
                    _indicatorValues.PotentialProfit = 0;
                    _indicatorValues.PotentialLoss = 0;
                }
            }
            else if (_parameters.EntryOrderType == OrderType.LevelCrossingEntry)
            {
                // Для входа по пересечению уровня - отображаем защитный стоп-лосс
                _indicatorValues.TakeProfitPrice = takeProfit;
                _indicatorValues.StopLossPrice = stopLoss;
                _indicatorValues.MovingTPExitPrice = 0;
                _indicatorValues.MovingTPEntryPrice = 0;

                if (entryPrice > 0 && stopLoss > 0)
                {
                    if (direction == PositionDirection.Long)
                    {
                        _indicatorValues.PotentialLoss = (entryPrice - stopLoss) / entryPrice * 100;
                        _indicatorValues.PotentialProfit = takeProfit > 0 ? (takeProfit - entryPrice) / entryPrice * 100 : 0;
                    }
                    else
                    {
                        _indicatorValues.PotentialLoss = (stopLoss - entryPrice) / entryPrice * 100;
                        _indicatorValues.PotentialProfit = takeProfit > 0 ? (entryPrice - takeProfit) / entryPrice * 100 : 0;
                    }
                }
            }
            else
            {
                // Для скользящего тейк-профита на входе
                _indicatorValues.TakeProfitPrice = 0;
                _indicatorValues.StopLossPrice = 0;
                _indicatorValues.MovingTPEntryPrice = _movingTPEntryTargetPrice;
                _indicatorValues.PotentialProfit = 0;
                _indicatorValues.PotentialLoss = 0;
            }

            _indicatorValues.EntryPrice = entryPrice;
        }
        private void CalculateExitPrices()
        {
            if (_currentPosition == null) return;

            decimal currentPrice = _lastPrice;

            // Если используется скользящий тейк-профит на выходе
            if (_parameters.ExitOrderType == OrderType.MovingTakeProfitExit && currentPrice > 0)
            {
                if (_currentPosition.Direction == PositionDirection.Long)
                {
                    if (_movingTPExitCurrentLevel <= 0)
                    {
                        // Инициализация при первом расчете
                        _movingTPExitCurrentLevel = _currentPosition.EntryPrice;
                        _movingTPExitTargetPrice = CalculateMovingTPExitStartLevel(
                            PositionDirection.Long, _currentPosition.EntryPrice, _currentPosition.EntryPrice);
                    }

                    if (currentPrice > _movingTPExitCurrentLevel)
                    {
                        _movingTPExitCurrentLevel = currentPrice;
                        _movingTPExitTargetPrice = CalculateMovingTPExitStartLevel(
                            PositionDirection.Long, currentPrice, currentPrice);
                    }

                    // Обновляем тейк-профит позиции
                    _currentPosition.TakeProfitPrice = _movingTPExitTargetPrice;
                    _indicatorValues.MovingTPExitPrice = _movingTPExitTargetPrice;
                }
                else if (_currentPosition.Direction == PositionDirection.Short)
                {
                    if (_movingTPExitCurrentLevel <= 0)
                    {
                        // Инициализация при первом расчете
                        _movingTPExitCurrentLevel = _currentPosition.EntryPrice;
                        _movingTPExitTargetPrice = CalculateMovingTPExitStartLevel(
                            PositionDirection.Short, _currentPosition.EntryPrice, _currentPosition.EntryPrice);
                    }

                    if (currentPrice < _movingTPExitCurrentLevel)
                    {
                        _movingTPExitCurrentLevel = currentPrice;
                        _movingTPExitTargetPrice = CalculateMovingTPExitStartLevel(
                            PositionDirection.Short, currentPrice, currentPrice);
                    }

                    // Обновляем тейк-профит позиции
                    _currentPosition.TakeProfitPrice = _movingTPExitTargetPrice;
                    _indicatorValues.MovingTPExitPrice = _movingTPExitTargetPrice;
                }
            }

            // Если используется трейлинг-стоп на выходе
            if (_parameters.ExitOrderType == OrderType.TrailingStopExit && currentPrice > 0)
            {
                if (_currentPosition.Direction == PositionDirection.Long)
                {
                    if (_trailingStopExitBestPrice <= 0)
                    {
                        _trailingStopExitBestPrice = _currentPosition.EntryPrice > 0 ?
                            Math.Max(_currentPosition.EntryPrice, currentPrice) : currentPrice;
                    }

                    if (currentPrice > _trailingStopExitBestPrice)
                    {
                        _trailingStopExitBestPrice = currentPrice;
                        _trailingStopExitCurrentLevel = CalculateTrailingStopExitLevel(
                            PositionDirection.Long, currentPrice);

                        // Обновляем стоп-лосс позиции
                        _currentPosition.StopLossPrice = _trailingStopExitCurrentLevel;
                        _indicatorValues.TrailingStopExitPrice = _trailingStopExitCurrentLevel;
                    }
                }
                else if (_currentPosition.Direction == PositionDirection.Short)
                {
                    if (_trailingStopExitBestPrice <= 0)
                    {
                        _trailingStopExitBestPrice = _currentPosition.EntryPrice > 0 ?
                            Math.Min(_currentPosition.EntryPrice, currentPrice) : currentPrice;
                    }

                    if (currentPrice < _trailingStopExitBestPrice)
                    {
                        _trailingStopExitBestPrice = currentPrice;
                        _trailingStopExitCurrentLevel = CalculateTrailingStopExitLevel(
                            PositionDirection.Short, currentPrice);

                        // Обновляем стоп-лосс позиции
                        _currentPosition.StopLossPrice = _trailingStopExitCurrentLevel;
                        _indicatorValues.TrailingStopExitPrice = _trailingStopExitCurrentLevel;
                    }
                }
            }

            _indicatorValues.TakeProfitPrice = _currentPosition.TakeProfitPrice;
            _indicatorValues.StopLossPrice = _currentPosition.StopLossPrice;

            // Расчет текущего P&L
            if (_currentPosition.EntryPrice > 0 && currentPrice > 0)
            {
                if (_currentPosition.Direction == PositionDirection.Long)
                {
                    _indicatorValues.CurrentPnL = (currentPrice - _indicatorValues.EntryPrice) * (Math.Abs(_currentPosition.Quantity) * _instrument.LotSize);

                    if (currentPrice > 0)
                    {
                        _indicatorValues.PotentialProfit = (_currentPosition.TakeProfitPrice - currentPrice) / currentPrice * 100;
                        _indicatorValues.PotentialLoss = (currentPrice - _currentPosition.StopLossPrice) / currentPrice * 100;
                    }
                }
                else
                {
                    _indicatorValues.CurrentPnL = (_indicatorValues.EntryPrice - currentPrice) * (Math.Abs(_currentPosition.Quantity) * _instrument.LotSize);

                    if (currentPrice > 0)
                    {
                        _indicatorValues.PotentialProfit = (currentPrice - _currentPosition.TakeProfitPrice) / currentPrice * 100;
                        _indicatorValues.PotentialLoss = (_currentPosition.StopLossPrice - currentPrice) / currentPrice * 100;
                    }
                }
            }
            else
            {
                _indicatorValues.CurrentPnL = 0;
                _indicatorValues.PotentialProfit = 0;
                _indicatorValues.PotentialLoss = 0;
            }
        }
        #endregion

        #region методоы для работы с пересечением уровней
        // Методы для определения пересечений уровней
        private bool CheckOscillatorCrossingAboveOverbought(decimal currentOscillator, decimal previousOscillator, decimal overboughtLevel)
        {
            // Пересечение СВЕРХУ ВНИЗ: предыдущее значение > уровень, текущее <= уровень
            return previousOscillator > overboughtLevel && currentOscillator <= overboughtLevel;
        }
        private bool CheckOscillatorCrossingBelowOversold(decimal currentOscillator, decimal previousOscillator, decimal oversoldLevel)
        {
            // Пересечение СНИЗУ ВВЕРХ: предыдущее значение < уровень, текущее >= уровень
            return previousOscillator < oversoldLevel && currentOscillator >= oversoldLevel;
        }

        // Методы расчета защитного стоп - лосса для входа по пересечению уровня
        private decimal CalculateLevelCrossingEntryStopLoss(string direction, decimal entryPrice)
        {
            if (entryPrice <= 0) return 0;

            decimal stopLossPrice = 0;
            decimal stopPercent = _parameters.LevelCrossingEntryProtectiveStopPercent;
            decimal distancePercent = _parameters.LevelCrossingEntryProtectiveStopDistancePercent;

            if (direction == PositionDirection.Long)
            {
                stopLossPrice = entryPrice * (1 - stopPercent / 100);

                decimal minDistance = entryPrice * (distancePercent / 100);
                decimal calculatedDistance = entryPrice - stopLossPrice;

                if (calculatedDistance < minDistance)
                {
                    stopLossPrice = entryPrice - minDistance;
                }
            }
            else if (direction == PositionDirection.Short)
            {
                stopLossPrice = entryPrice * (1 + stopPercent / 100);

                decimal minDistance = entryPrice * (distancePercent / 100);
                decimal calculatedDistance = stopLossPrice - entryPrice;

                if (calculatedDistance < minDistance)
                {
                    stopLossPrice = entryPrice + minDistance;
                }
            }

            return stopLossPrice;
        }
        private decimal CalculateLevelCrossingEntryTakeProfit(string direction, decimal entryPrice)
        {
            if (entryPrice <= 0) return 0;

            decimal takeProfitPrice = 0;
            decimal tpPercent = _parameters.TakeProfitPercent;

            if (direction == PositionDirection.Long)
            {
                takeProfitPrice = entryPrice * (1 + tpPercent / 100);
            }
            else if (direction == PositionDirection.Short)
            {
                takeProfitPrice = entryPrice * (1 - tpPercent / 100);
            }

            return takeProfitPrice;
        }

        // Методы расчета защитного стоп - лосса для выхода по пересечению уровня
        private decimal CalculateLevelCrossingExitStopLoss(string direction, decimal entryPrice)
        {
            if (entryPrice <= 0 || direction == null) return 0;

            decimal stopLossPrice = 0;
            decimal stopPercent = _parameters.LevelCrossingExitProtectiveStopPercent;
            decimal distancePercent = _parameters.LevelCrossingExitProtectiveStopDistancePercent;

            if (direction == PositionDirection.Long)
            {
                stopLossPrice = entryPrice * (1 - stopPercent / 100);

                decimal minDistance = entryPrice * (distancePercent / 100);
                decimal calculatedDistance = entryPrice - stopLossPrice;

                if (calculatedDistance < minDistance)
                {
                    stopLossPrice = entryPrice - minDistance;
                }
            }
            else if (direction == PositionDirection.Short)
            {
                stopLossPrice = entryPrice * (1 + stopPercent / 100);

                decimal minDistance = entryPrice * (distancePercent / 100);
                decimal calculatedDistance = stopLossPrice - entryPrice;

                if (calculatedDistance < minDistance)
                {
                    stopLossPrice = entryPrice + minDistance;
                }
            }

            return stopLossPrice;
        }

        private async Task ExecuteLevelCrossingEntryAsync(string direction)
        {
            try
            {
                // Проверяем таймаут после последнего входа
                if ((DateTime.Now - _lastEntryTime).TotalSeconds < ENTRY_COOLDOWN_SECONDS)
                {
                    Debug.WriteLine($"Таймаут входа: прошло {(DateTime.Now - _lastEntryTime).TotalSeconds:F1} секунд из {ENTRY_COOLDOWN_SECONDS}");
                    OnEntryStatusChanged?.Invoke($"Таймаут входа: {ENTRY_COOLDOWN_SECONDS - (int)(DateTime.Now - _lastEntryTime).TotalSeconds} секунд");
                    return;
                }

                // Используем асинхронную версию
                var positionSize = await CalculatePositionSize();

                // ✅ ИСПОЛЬЗУЕМ TRANSACTIONS SERVICE
                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: direction == PositionDirection.Long ? "Buy" : "Sell",
                    quantity: (int)positionSize,
                    ticker: _instrument.Ticker,
                    accountId: null,
                    isEntryOrder: true,
                    isExitOrder: false,
                    exitReason: null);

                if (result.IsSuccess)
                {
                    // Обновляем время последнего входа
                    _lastEntryTime = DateTime.Now;

                    _pendingOrder = result.Order;
                    _entryState = EntryState.OrderPending;

                    string signalDescription = direction == PositionDirection.Long
                        ? "пересечение перепроданности СНИЗУ ВВЕРХ"
                        : "пересечение перекупленности СВЕРХУ ВНИЗ";

                    OnEntryStatusChanged?.Invoke($"Ордер на вход по пересечению уровня выставлен: {signalDescription}");

                    // Сохраняем информацию о защитном стоп-лоссе
                    if (_levelCrossingEntryProtectiveStopActive)
                    {
                        _indicatorValues.StopLossPrice = _levelCrossingEntryStopLossPrice;
                        _indicatorValues.TakeProfitPrice = _levelCrossingEntryTakeProfitPrice;
                        _indicatorValues.LastAction = $"Вход по пересечению уровня. \nСтоп-лосс: {_levelCrossingEntryStopLossPrice:F2}, Тейк-профит: {_levelCrossingEntryTakeProfitPrice:F2}";
                    }
                    else
                    {
                        _indicatorValues.LastAction = $"Вход по пересечению уровня. Причина: {signalDescription}";
                    }

                    _indicatorValues.LastActionTime = DateTime.Now;
                    _indicatorValues.OrderStatus = "ОРДЕР НА ВХОД ВЫСТАВЛЕН";

                    _logger.LogInformation($"Level crossing entry order placed: {direction} {_instrument.Ticker} at {result.Order.Price:F2}");
                }
                else
                {
                    _entryState = EntryState.EntryFailed;
                    OnEntryStatusChanged?.Invoke($"Ошибка выставления ордера: {result.ErrorMessage}");

                    _indicatorValues.LastAction = $"Ошибка выставления ордера: {result.ErrorMessage}";
                    _indicatorValues.LastActionTime = DateTime.Now;
                    _indicatorValues.OrderStatus = "ОШИБКА ВЫСТАВЛЕНИЯ";

                    _ = Task.Delay(10000).ContinueWith(_ =>
                    {
                        _entryState = EntryState.NoSignal;
                        OnEntryStatusChanged?.Invoke("Ожидание сигнала (после ошибки)");
                    });
                }
            }
            catch (Exception ex)
            {
                _entryState = EntryState.EntryFailed;
                OnEntryStatusChanged?.Invoke($"Ошибка: {ex.Message}");

                _logger.LogError(ex, $"Error executing level crossing entry for {direction}");
                _indicatorValues.LastAction = $"Ошибка исполнения сигнала: {ex.Message}";

                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    _entryState = EntryState.NoSignal;
                    OnEntryStatusChanged?.Invoke("Ожидание сигнала (после ошибки)");
                });
            }
        }

        #endregion




        #region Price Calculation Methods
        private decimal CalculateLimitPrice(decimal currentPrice, string direction, decimal offsetPercent, bool isForLong)
        {
            decimal limitPrice;
            if (isForLong)
                limitPrice = currentPrice * (1 - offsetPercent / 100);
            else
                limitPrice = currentPrice * (1 + offsetPercent / 100);

            // Учитываем проскальзывание
            if (_parameters.EntrySlippage > 0)
            {
                decimal slippageAmount = limitPrice * (_parameters.EntrySlippage / 100);
                if (direction == PositionDirection.Long)
                    limitPrice += slippageAmount;
                else
                    limitPrice -= slippageAmount;
            }

            return limitPrice;
        }
        private decimal CalculateStopLimitPrice(decimal currentPrice, string direction, decimal offsetPercent, bool isForLong)
        {
            decimal stopLimitPrice;
            if (isForLong)
                stopLimitPrice = currentPrice * (1 + offsetPercent / 100);
            else
                stopLimitPrice = currentPrice * (1 - offsetPercent / 100);

            // Учитываем проскальзывание
            if (_parameters.EntrySlippage > 0)
            {
                decimal slippageAmount = stopLimitPrice * (_parameters.EntrySlippage / 100);
                if (direction == PositionDirection.Long)
                    stopLimitPrice += slippageAmount;
                else
                    stopLimitPrice -= slippageAmount;
            }

            return stopLimitPrice;
        }
        private decimal CalculateTakeProfitPrice(decimal entryPrice, string direction, PriceCalculationType calculationType,
            decimal percent, decimal absolute, decimal atrMultiplier, decimal atrValue,
            decimal activationPrice, decimal slippage)
        {
            decimal takeProfitPrice = 0;

            switch (calculationType)
            {
                case PriceCalculationType.Percentage:
                    if (direction == PositionDirection.Long)
                        takeProfitPrice = entryPrice * (1 + percent / 100);
                    else
                        takeProfitPrice = entryPrice * (1 - percent / 100);
                    break;

                case PriceCalculationType.Absolute:
                    if (direction == PositionDirection.Long)
                        takeProfitPrice = entryPrice + absolute;
                    else
                        takeProfitPrice = entryPrice - absolute;
                    break;

                case PriceCalculationType.ATR:
                    if (atrValue > 0)
                    {
                        decimal atrOffset = atrValue * atrMultiplier;
                        if (direction == PositionDirection.Long)
                            takeProfitPrice = entryPrice + atrOffset;
                        else
                            takeProfitPrice = entryPrice - atrOffset;
                    }
                    else
                    {
                        // Без ATR используем процентный расчет как fallback
                        if (direction == PositionDirection.Long)
                            takeProfitPrice = entryPrice * (1 + percent / 100);
                        else
                            takeProfitPrice = entryPrice * (1 - percent / 100);
                    }
                    break;
            }

            // Учитываем цену активации
            if (activationPrice > 0)
            {
                if (direction == PositionDirection.Long)
                    takeProfitPrice = Math.Max(takeProfitPrice, entryPrice + activationPrice);
                else
                    takeProfitPrice = Math.Min(takeProfitPrice, entryPrice - activationPrice);
            }

            // Учитываем проскальзывание
            if (slippage > 0)
            {
                decimal slippageAmount = takeProfitPrice * (slippage / 100);
                if (direction == PositionDirection.Long)
                    takeProfitPrice += slippageAmount;
                else
                    takeProfitPrice -= slippageAmount;
            }

            return takeProfitPrice;
        }
        private decimal CalculateStopLossPrice(decimal entryPrice, string direction, PriceCalculationType calculationType,
             decimal percent, decimal absolute, decimal atrMultiplier, decimal atrValue,
             decimal activationPrice, decimal slippage)
        {
            decimal stopLossPrice = 0;

            switch (calculationType)
            {
                case PriceCalculationType.Percentage:
                    if (direction == PositionDirection.Long)
                        stopLossPrice = entryPrice * (1 - percent / 100);
                    else
                        stopLossPrice = entryPrice * (1 + percent / 100);
                    break;

                case PriceCalculationType.Absolute:
                    if (direction == PositionDirection.Long)
                        stopLossPrice = entryPrice - absolute;
                    else
                        stopLossPrice = entryPrice + absolute;
                    break;

                case PriceCalculationType.ATR:
                    if (atrValue > 0)
                    {
                        decimal atrOffset = atrValue * atrMultiplier;
                        if (direction == PositionDirection.Long)
                            stopLossPrice = entryPrice - atrOffset;
                        else
                            stopLossPrice = entryPrice + atrOffset;
                    }
                    else
                    {
                        // Без ATR используем процентный расчет как fallback
                        if (direction == PositionDirection.Long)
                            stopLossPrice = entryPrice * (1 - percent / 100);
                        else
                            stopLossPrice = entryPrice * (1 + percent / 100);
                    }
                    break;
            }

            // Учитываем цену активации
            if (activationPrice > 0)
            {
                if (direction == PositionDirection.Long)
                    stopLossPrice = Math.Min(stopLossPrice, entryPrice - activationPrice);
                else
                    stopLossPrice = Math.Max(stopLossPrice, entryPrice + activationPrice);
            }

            // Учитываем проскальзывание
            if (slippage > 0)
            {
                decimal slippageAmount = stopLossPrice * (slippage / 100);
                if (direction == PositionDirection.Long)
                    stopLossPrice -= slippageAmount;
                else
                    stopLossPrice += slippageAmount;
            }

            return stopLossPrice;
        }
        private decimal CalculateMovingTPEntryTargetLevel(string direction, decimal currentPrice, decimal bestPrice)
        {
            decimal targetLevel = 0;
            decimal offset = 0;

            // Расчет отступа в зависимости от типа расчета
            switch (_parameters.MovingTPEntryCalculationType)
            {
                case PriceCalculationType.Percentage:
                    offset = bestPrice * (_parameters.MovingTPEntryTargetPercent / 100);
                    break;

                case PriceCalculationType.Absolute:
                    offset = _parameters.MovingTPEntryTargetAbsolute;
                    break;

                case PriceCalculationType.ATR:
                    offset = _indicatorValues.AtrValue * _parameters.AtrMultiplier;
                    break;
            }

            // Расчет целевого уровня в зависимости от направления
            if (direction == PositionDirection.Long)
            {
                // Для лонга: целевой уровень НИЖЕ лучшей цены на отступ
                targetLevel = bestPrice + offset;
            }
            else if (direction == PositionDirection.Short)
            {
                // Для шорта: целевой уровень ВЫШЕ лучшей цены на отступ
                targetLevel = bestPrice - offset;
            }

            // Учитываем проскальзывание
            if (_parameters.MovingTPEntrySlippage > 0)
            {
                decimal slippageAmount = targetLevel * (_parameters.MovingTPEntrySlippage / 100);
                if (direction == PositionDirection.Long)
                    targetLevel += slippageAmount; // Для лонга добавляем проскальзывание (лучшая цена для входа)
                else
                    targetLevel -= slippageAmount; // Для шорта вычитаем проскальзывание
            }

            return targetLevel;
        }
        // отслеживание нового экстремума  минимума или максимума для выхода по трейлинг тейку
        private decimal CalculateMovingTPExitStartLevel(string direction, decimal currentPrice, decimal bestPrice)
        {
            decimal targetLevel = 0;
            decimal offset = CalculateOffset();

            //Debug.WriteLine($"________________________РАССЧИТЫВАЕМ ВЫХОД____________________{_movingTPExitTargetPrice}_____{_lastPrice}__________________________");
            //Debug.WriteLine($"РАСЧЕТ TP выхода: Direction={direction}, CurrentPrice={currentPrice:F2}, BestPrice={bestPrice:F2}, Offset={offset:F4}, ATR={_indicatorValues.AtrValue:F4}    shouldExitByMovingTPExitByChangeSignal={shouldExitByMovingTPExitByChangeSignal}  shouldExitByMovingTPExit={shouldExitByMovingTPExit} _parameters.CloseOnSignalReversal={_parameters.CloseOnSignalReversal}  _exitState={_exitState} ");


            // ИСПРАВЛЕННАЯ ЛОГИКА:
            // Для выхода из ЛОНГА: цена выхода ДОЛЖНА БЫТЬ ВЫШЕ лучшей цены
            if (direction == PositionDirection.Long)
            {
                // Для выхода из ЛОНГА:
                // Отслеживаем МАКСИМУМ, цель = max - offset
                targetLevel = bestPrice - offset;

                Debug.WriteLine($"Лонг exit: Best={bestPrice:F2} - Offset={offset:F4} = Target={targetLevel:F4}");
            }
            else if (direction == PositionDirection.Short)
            {
                // Для выхода из ШОРТА:
                // Отслеживаем МИНИМУМ, цель = min + offset
                targetLevel = bestPrice + offset;

                Debug.WriteLine($"Шорт exit: Best={bestPrice:F2} + Offset={offset:F4} = Target={targetLevel:F4}");
            }
            else
            {
                _logger.LogError($"ОШИБКА: Неизвестное направление: {direction}");
                Debug.WriteLine($"ОШИБКА: Неизвестное направление: {direction}");
                return 0;
            }

            // Учитываем проскальзывание
            if (_parameters.MovingTPExitSlippage > 0)
            {
                decimal slippageAmount = targetLevel * (_parameters.MovingTPExitSlippage / 100);
                if (direction == PositionDirection.Long)
                    targetLevel -= slippageAmount; // Для лонга еще снижаем (лучше войти)
                else if (direction == PositionDirection.Short)
                    targetLevel += slippageAmount; // Для шорта еще повышаем
            }

            _logger.LogDebug($"DEBUG: CalculateMovingTPExitStartLevel {_instrument.Ticker}  Расчет скользящего TP на выход: Direction={direction}, BestPrice={bestPrice:F2}, Offset={offset:F4}, TargetLevel={targetLevel:F4}");
            Debug.WriteLine($"DEBUG: CalculateMovingTPExitStartLevel {_instrument.Ticker}  Расчет скользящего TP на выход: Direction={direction}, BestPrice={bestPrice:F2}, Offset={offset:F4}, TargetLevel={targetLevel:F4}");
            return targetLevel;

        }
        private decimal CalculateTrailingStopExitLevel(string direction, decimal currentPrice)
        {
            decimal stopLevel = 0;

            switch (_parameters.TrailingStopExitCalculationType)
            {
                case PriceCalculationType.Percentage:
                    if (direction == PositionDirection.Long)
                        stopLevel = currentPrice * (1 - _parameters.TrailingStopExitDistancePercent / 100);
                    else
                        stopLevel = currentPrice * (1 + _parameters.TrailingStopExitDistancePercent / 100);
                    break;

                case PriceCalculationType.Absolute:
                    if (direction == PositionDirection.Long)
                        stopLevel = currentPrice - _parameters.TrailingStopExitDistanceAbsolute;
                    else
                        stopLevel = currentPrice + _parameters.TrailingStopExitDistanceAbsolute;
                    break;

                case PriceCalculationType.ATR:
                    if (_indicatorValues.AtrValue > 0)
                    {
                        decimal atrOffset = _indicatorValues.AtrValue * _parameters.AtrMultiplier;
                        if (direction == PositionDirection.Long)
                            stopLevel = currentPrice - atrOffset;
                        else
                            stopLevel = currentPrice + atrOffset;
                    }
                    else
                    {
                        // Без ATR используем процентный расчет как fallback
                        if (direction == PositionDirection.Long)
                            stopLevel = currentPrice * (1 - _parameters.TrailingStopExitDistancePercent / 100);
                        else
                            stopLevel = currentPrice * (1 + _parameters.TrailingStopExitDistancePercent / 100);
                    }
                    break;
            }

            // Учитываем проскальзывание
            if (_parameters.TrailingStopExitSlippage > 0)
            {
                decimal slippageAmount = stopLevel * (_parameters.TrailingStopExitSlippage / 100);
                if (direction == PositionDirection.Long)
                    stopLevel -= slippageAmount;
                else
                    stopLevel += slippageAmount;
            }

            return stopLevel;
        }
        private decimal CalculateOffset()
        {
            decimal offset = 0;

            switch (_parameters.MovingTPExitCalculationType)
            {
                case PriceCalculationType.Percentage:
                    offset = (_movingTPExitCurrentLevel > 0 ? _movingTPExitCurrentLevel : _lastPrice) *
                            (_parameters.MovingTPExitStartPercent / 100);
                    break;

                case PriceCalculationType.Absolute:
                    offset = _parameters.MovingTPExitStartAbsolute;
                    break;

                case PriceCalculationType.ATR:
                    offset = _indicatorValues.AtrValue * _parameters.AtrMultiplier;
                    if (offset <= 0) offset = 1.0m; // Запасной вариант
                    break;
            }

            return offset;
        }
        #endregion

        #region Indicator Calculation Methods
        private void CalculateIndicators(List<Quote> quotes, List<Models.Candle> candles)
        {
            // RSI
            var rsiResults = quotes.GetRsi(_parameters.RsiPeriod).ToList();
            if (rsiResults.Any())
            {
                var latestRsi = rsiResults.Last();
                _indicatorValues.RsiValue = (decimal)(latestRsi.Rsi ?? 0);
                _indicatorValues.RsiStatus = GetRsiStatus(_indicatorValues.RsiValue);
            }

            // Выбор типа осциллятора
            if (_parameters.OscillatorType == OscillatorType.StochRSI)
            {
                var stochRsiResults = quotes.GetStochRsi(
                    _parameters.StochPeriod,
                    _parameters.StochSmoothK,
                    _parameters.StochSmoothD).ToList();

                if (stochRsiResults.Any())
                {
                    var latest = stochRsiResults.Last();
                    _indicatorValues.OscillatorValue = (decimal)(latest.StochRsi ?? 0);
                    _indicatorValues.OscillatorName = "StochRSI";
                    _indicatorValues.StochRSIK = _indicatorValues.OscillatorValue;
                    _indicatorValues.StochasticK = 0;
                    _indicatorValues.StochasticD = 0;
                }
            }
            else // Stochastic Oscillator
            {
                var stochResults = quotes.GetStoch(
                    _parameters.StochPeriod,
                    _parameters.StochSmoothK,
                    _parameters.StochSmoothD).ToList();

                if (stochResults.Any())
                {
                    var latest = stochResults.Last();
                    _indicatorValues.OscillatorValue = (decimal)(latest.K ?? 0);
                    _indicatorValues.OscillatorSignal = (decimal)(latest.D ?? 0);
                    _indicatorValues.OscillatorName = "Stochastic";
                    _indicatorValues.StochasticK = _indicatorValues.OscillatorValue;
                    _indicatorValues.StochasticD = _indicatorValues.OscillatorSignal;
                    _indicatorValues.StochRSIK = 0;

                    //Debug.WriteLine($"DEBUG:  {_instrument.Ticker}  CalculateIndicators - Stochastic Oscillator - {_indicatorValues.OscillatorValue}  {_indicatorValues.OscillatorSignal}  {_indicatorValues.OscillatorName}   {_indicatorValues.StochasticK}   {_indicatorValues.StochasticD}");
                }
            }

            // ✅ ГАРАНТИРУЕМ, что если осциллятор равен 0, мы его не используем
            if (_indicatorValues.OscillatorValue == 0)
            {
                // Оставляем как есть, но в GenerateTradingSignals будет проверка
                Debug.WriteLine($"CalculateIndicators: Осциллятор равен 0 для {_instrument.Ticker}");
            }





            // ATR
            if (candles.Count >= 14)
            {
                var atrResults = quotes.GetAtr(14).ToList();
                if (atrResults.Any())
                {
                    var latestAtr = atrResults.Last();
                    _indicatorValues.AtrValue = (decimal)(latestAtr.Atr ?? 0);
                }
            }

            // MACD (дополнительный индикатор)
            var macdResults = quotes.GetMacd().ToList();
            if (macdResults.Any())
            {
                var latestMacd = macdResults.Last();
                _indicatorValues.MacdValue = (decimal)(latestMacd.Macd ?? 0);
                _indicatorValues.MacdSignal = (decimal)(latestMacd.Signal ?? 0);
                _indicatorValues.MacdHistogram = (decimal)(latestMacd.Histogram ?? 0);
            }

            // Bollinger Bands
            var bbResults = quotes.GetBollingerBands(20).ToList();
            if (bbResults.Any())
            {
                var latestBB = bbResults.Last();
                _indicatorValues.BollingerUpper = (decimal)(latestBB.UpperBand ?? 0);
                _indicatorValues.BollingerMiddle = (decimal)(latestBB.Sma ?? 0);
                _indicatorValues.BollingerLower = (decimal)(latestBB.LowerBand ?? 0);
            }
        }
        private void UpdateIndicatorValues()
        {
            // Обновляем статус индикаторов
            _indicatorValues.RsiStatus = GetRsiStatus(_indicatorValues.RsiValue);
            _indicatorValues.RsiColor = _indicatorValues.RsiValue > 70 ? Brushes.Red :
                                       _indicatorValues.RsiValue < 30 ? Brushes.Green :
                                       _indicatorValues.RsiValue > 50 ? Brushes.LightGreen : Brushes.LightCoral;

            _indicatorValues.OscillatorStatus = GetOscillatorStatus(_indicatorValues.OscillatorValue);
            _indicatorValues.OscillatorColor = _indicatorValues.OscillatorValue > 80 ? Brushes.Red :
                                              _indicatorValues.OscillatorValue < 20 ? Brushes.Green :
                                              _indicatorValues.OscillatorValue > 50 ? Brushes.LightGreen : Brushes.LightCoral;
        }
        private string GetRsiStatus(decimal rsiValue)
        {
            if (rsiValue > 70) return "ПЕРЕКУПЛЕННОСТЬ";
            if (rsiValue < 30) return "ПЕРЕПРОДАННОСТЬ";
            if (rsiValue > 50) return "БЫЧЬИ";
            return "МЕДВЕЖЬИ";
        }
        private string GetOscillatorStatus(decimal oscValue)
        {
            if (oscValue > 80) return "ПЕРЕКУПЛЕННОСТЬ";
            if (oscValue < 20) return "ПЕРЕПРОДАННОСТЬ";
            if (oscValue > 50) return "ВВЕРХ";
            return "ВНИЗ";
        }
        #endregion

        #region Position Size Calculation
        private async Task<decimal> CalculatePositionSize()
        {
            // Расчет размера позиции на основе процента от депозита
            if (_parameters.OrderSizePercent > 0)
            {
                // Получаем баланс через API
                var balance = await _provider.GetAccountBalanceAsync();

                await Task.Delay(500); // Ждем пол секунды перед проверкой

                if (balance > 0)
                {
                    decimal positionValue = balance * (_parameters.OrderSizePercent / 100);
                    positionValueMoney = Math.Round(positionValue, 2);

                    if (_lastPrice > 0 && _instrument != null && _instrument.LotSize > 0)
                    {
                        lotPositionSize = Math.Floor((positionValue / _lastPrice) / _instrument.LotSize);
                    }

                    return lotPositionSize;
                }
            }

            // Фиксированный размер
            return _parameters.FixedOrderSize;
        }
        #endregion

        #region Data Management Methods
        private async Task<List<Models.Candle>> LoadCandlesAsync()
        {
            try
            {
                int count = Math.Max(_parameters.RsiPeriod, _parameters.StochPeriod) * 3;
                return await _strategyViewModel.GetHistoricalCandlesFromDbAsync(count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading candles");
                return new List<Models.Candle>();
            }
        }
        private async Task LoadHistoricalDataAsync()
        {
            try
            {
                var candles = await LoadCandlesAsync();
                foreach (var candle in candles)
                {
                    _candleBuffer.Enqueue(candle);
                }

                if (_candleBuffer.Any())
                {
                    _lastProcessedCandleTime = _candleBuffer.Last().Time;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading historical data");
            }
        }
        private List<Quote> ConvertToQuotes(IEnumerable<Models.Candle> candles)
        {
            return candles.Select(c => new Quote
            {
                Date = c.Time,
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume
            }).ToList();
        }
        #endregion

        #region Helper Methods
        public void ResetAllMovingTPVariables()
        {
            // Сброс переменных для скользящего тейк-профита на ВХОДЕ
            _movingTPEntryStartPrice = 0;
            _movingTPEntryCurrentLevel = 0;
            _movingTPEntryTargetPrice = 0;
            _movingTPEntryStartTime = DateTime.MinValue;

            // Сброс переменных для скользящего тейк-профита на ВЫХОДЕ
            _movingTPExitStartPrice = 0;
            _movingTPExitCurrentLevel = 0;
            _movingTPExitTargetPrice = 0;
            _movingTPExitStartTime = DateTime.MinValue;

            // Сброс переменных для трейлинг-стопа на выходе
            _trailingStopExitStartPrice = 0;
            _trailingStopExitCurrentLevel = 0;
            _trailingStopExitTargetPrice = 0;
            _trailingStopExitBestPrice = 0;
            _trailingStopExitActivated = false;

            // ✅ Сброс переменных для пересечения уровней
            _wasOscillatorAboveOverbought = false;
            _wasOscillatorBelowOversold = false;
            _previousOscillatorValueForCrossing = 0;
            _lastOscillatorValueForCrossing = 0;
            _hasPreviousOscillatorValue = false;
            _levelCrossingEntryProtectiveStopActive = false;
            _levelCrossingExitProtectiveStopActive = false;
            _levelCrossingEntryStopLossPrice = 0;
            _levelCrossingEntryTakeProfitPrice = 0;
            _levelCrossingExitStopLossPrice = 0;

            // Сброс флагов
            _entryPass = true;
            _exitPass = true;

            shouldExitByMovingTPExit = false;
            shouldExitByMovingTPExitByChangeSignal = false;
        }
        public void ResetExitVariables()
        {
            _movingTPExitStartPrice = 0;
            _movingTPExitCurrentLevel = 0;
            _movingTPExitTargetPrice = 0;
            _movingTPExitStartTime = DateTime.MinValue;

            _trailingStopExitStartPrice = 0;
            _trailingStopExitCurrentLevel = 0;
            _trailingStopExitTargetPrice = 0;
            _trailingStopExitBestPrice = 0;
            _trailingStopExitActivated = false;

            _entryPass = true;
            _exitPass = true;

            shouldExitByMovingTPExit = false;
            shouldExitByMovingTPExitByChangeSignal = false;
        }
        private void ClearOrderPrices()
        {
            _indicatorValues.EntryPrice = 0;
            _indicatorValues.TakeProfitPrice = 0;
            _indicatorValues.StopLossPrice = 0;
            _indicatorValues.MovingTPEntryPrice = 0;
            _indicatorValues.MovingTPExitPrice = 0;
            _indicatorValues.TrailingStopExitPrice = 0;
            _indicatorValues.PotentialProfit = 0;
            _indicatorValues.PotentialLoss = 0;
        }
        private string GetEntryStatusDetails()
        {
            if (_currentPosition != null)
            {
                return $"Позиция активна. Входные сигналы игнорируются. \nDirection: {_currentPosition.Direction}, Quantity: {Math.Abs(_currentPosition.Quantity)}";
            }

            // Добавляем информацию о таймауте
            if (_currentPosition == null && _entryState == EntryState.NoSignal)
            {
                var timeSinceLastEntry = (DateTime.Now - _lastEntryTime).TotalSeconds;
                if (timeSinceLastEntry < ENTRY_COOLDOWN_SECONDS)
                {
                    return $"Ожидание таймаута после последнего входа: {ENTRY_COOLDOWN_SECONDS - (int)timeSinceLastEntry} секунд";
                }
            }

            if (_entryState == EntryState.MovingTPEntryActive)
            {
                var timeElapsed = (DateTime.Now - _movingTPEntryStartTime).TotalMinutes;
                var timeRemaining = _parameters.MovingTPEntryTimeoutMinutes - timeElapsed;

                return $"Скользящий TP на входе активен. " +
                       $"\nНачальная цена: {_movingTPEntryStartPrice:F2}, " +
                       $"\nТекущая цена: {_lastPrice:F2}, " +
                       $"\nЦель: {_movingTPEntryTargetPrice:F2}, " +
                       $"\nПрошло: {timeElapsed:F1} мин, " +
                       $"Осталось: {timeRemaining:F1} мин";
            }
            else if (_entryState == EntryState.OrderPending && _pendingOrder != null)
            {
                return $"Ожидание исполнения ордера: {_pendingOrder.Direction} " +
                       $"\nпо цене {_pendingOrder.Price:F2}, " +
                       $"количество: {_pendingOrder.Quantity} лотов";
            }
            else if (_entryState == EntryState.WaitingForEntry)
            {
                return $"Ожидание условий для входа. " +
                       $"\nRSI: {_indicatorValues.RsiValue:F2}, " +
                       $"\n{_indicatorValues.OscillatorName}: {_indicatorValues.OscillatorValue:F2}";
            }
            else if (_entryState == EntryState.EntryFailed)
            {
                return "Последняя попытка входа не удалась";
            }

            return "Ожидание торгового сигнала";
        }
        private string GetExitStatusDetails()
        {
            if (_currentPosition == null)
            {
                return "Позиция отсутствует";
            }

            // Добавляем информацию о таймауте
            if (_currentPosition != null && _exitState == ExitState.PositionActive)
            {
                var timeSinceLastExit = (DateTime.Now - _lastExitTime).TotalSeconds;
                if (timeSinceLastExit < EXIT_COOLDOWN_SECONDS)
                {
                    return $"Позиция активна. Таймаут после последнего выхода: {EXIT_COOLDOWN_SECONDS - (int)timeSinceLastExit} секунд";
                }
            }


            // Для ВЫХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
            if (_parameters.ExitOrderType == OrderType.LevelCrossingExit && _exitState == ExitState.MovingTPExitActive)
            {
                string directionText = _currentPosition.Direction == PositionDirection.Long ? "лонга" : "шорта";
                string levelText = _currentPosition.Direction == PositionDirection.Long
                    ? $"пересечение {_parameters.StochOverbought:F1} СВЕРХУ ВНИЗ"
                    : $"пересечение {_parameters.StochOversold:F1} СНИЗУ ВВЕРХ";

                string status = $"Выход по пересечению уровня для {directionText}\n";
                status += $"├─ Ожидание: {levelText}\n";
                status += $"├─ Текущий {_indicatorValues.OscillatorName}: {_indicatorValues.OscillatorValue:F2}\n";

                if (_levelCrossingExitProtectiveStopActive && _levelCrossingExitStopLossPrice > 0)
                {
                    status += $"├─ Защитный стоп-лосс: {_levelCrossingExitStopLossPrice:F2}\n";

                    // Показываем текущее расстояние до стоп-лосса
                    if (_currentPosition.Direction == PositionDirection.Long)
                    {
                        decimal distance = (_lastPrice - _levelCrossingExitStopLossPrice) / _lastPrice * 100;
                        status += $"└─ Расстояние до стопа: {distance:F2}%";
                    }
                    else
                    {
                        decimal distance = (_levelCrossingExitStopLossPrice - _lastPrice) / _lastPrice * 100;
                        status += $"└─ Расстояние до стопа: {distance:F2}%";
                    }
                }
                else
                {
                    status += $"└─ Защитный стоп: не активирован";
                }

                return status;
            }

            // Существующая логика для других типов выхода
            if (_exitState == ExitState.MovingTPExitActive)
            {
                var timeElapsed = (DateTime.Now - _movingTPExitStartTime).TotalMinutes;
                var timeRemaining = _parameters.MovingTPExitTimeoutMinutes - timeElapsed;

                return $"Скользящий TP на выходе активен. " +
                       $"\nТекущий уровень: {_movingTPExitCurrentLevel:F2}, " +
                       $"\nЦель выхода: {_movingTPExitTargetPrice:F2}, " +
                       $"\nПрошло: {timeElapsed:F1} мин, " +
                       $"\nОсталось: {timeRemaining:F1} мин";
            }
            else if (_exitState == ExitState.TrailingStopActive)
            {
                CalculateExitPrices();

                // Рассчитываем защитный стоп для отображения
                decimal protectiveStopLevel = 0;
                decimal protectiveStopPercent = _parameters.ProtectiveStopPercent;

                if (_currentPosition != null && _currentPosition.EntryPrice > 0)
                {
                    if (_currentPosition.Direction == PositionDirection.Long)
                    {
                        protectiveStopLevel = _currentPosition.EntryPrice * (1 - protectiveStopPercent / 100);
                    }
                    else if (_currentPosition.Direction == PositionDirection.Short)
                    {
                        protectiveStopLevel = _currentPosition.EntryPrice * (1 + protectiveStopPercent / 100);
                    }
                }

                if (!_trailingStopExitActivated)
                {
                    return $"⏳ Ожидание активации трейлинг-стопа\n" +
                           $"├─ Текущая прибыль: {((_currentPosition.Direction == PositionDirection.Long ?
                               (_lastPrice - _currentPosition.EntryPrice) / _currentPosition.EntryPrice * 100 :
                               (_currentPosition.EntryPrice - _lastPrice) / _currentPosition.EntryPrice * 100)):F2}%\n" +
                           $"├─ Нужно для активации: {_parameters.TrailingStopExitActivationPercent}%\n" +
                           $"└─ Защитный стоп: {protectiveStopLevel:F2} (сработает при пробое)";
                }

                return $"📊 Трейлинг-стоп активен\n" +
                       $"├─ Лучшая цена: {_trailingStopExitBestPrice:F2}\n" +
                       $"├─ Текущий стоп: {_trailingStopExitCurrentLevel:F2}\n" +
                       $"├─ Дистанция: {Math.Abs((_trailingStopExitBestPrice - _trailingStopExitCurrentLevel) / _trailingStopExitBestPrice * 100):F2}%\n" +
                       $"└─ Текущая прибыль: {((_currentPosition.Direction == PositionDirection.Long ?
                           (_lastPrice - _currentPosition.EntryPrice) / _currentPosition.EntryPrice * 100 :
                           (_currentPosition.EntryPrice - _lastPrice) / _currentPosition.EntryPrice * 100)):F2}%";
            }
            else if (_exitState == ExitState.PositionActive)
            {
                return $"Позиция активна. Текущий P&L: {_indicatorValues.CurrentPnL:F2}";
            }
            else if (_exitState == ExitState.ExitPending)
            {
                return "Ожидание исполнения ордера на выход";
            }
            else if (_exitState == ExitState.ExitFailed)
            {
                return "Последняя попытка выхода не удалась";
            }

            return "Позиция активна";
        }
        // методы для проверки принадлежности инструмента
        private bool IsPositionForCurrentInstrument(Position position)
        {
            return position != null && position.InstrumentUid == _currentInstrumentUid;
        }
        #endregion

        #region Event Handlers
        private void OnParametersChanged(RsiStrategyParameters parameters)
        {
            _logger.LogInformation("RSI parameters updated");
            _ = Task.Run(async () =>
            {
                await LoadHistoricalDataAsync();
                if (State == StrategyState.Running)
                {
                    await CalculatePositionSize();
                    await ProcessStrategyLogicAsync();
                }
            });
        }
        #endregion

        #region UI and View Methods
        public object GetSettingsView() => CreateSettingsPanel();
        public object GetControlView() => CreateControlPanel();
        private StackPanel CreateSettingsPanel()
        {
            var panel = new StackPanel { };

            // Выбор осциллятора
            var oscillatorTypeGroup = CreateParameterGroup("Выбор осциллятора");
            var oscillatorTypePanel = new StackPanel();

            var oscillatorTypeRadioPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };

            var stochRsiRadio = new RadioButton
            {
                Content = "StochRSI",
                IsChecked = _parameters.OscillatorType == OscillatorType.StochRSI,
                Margin = new Thickness(0, 0, 10, 0)
            };
            stochRsiRadio.Checked += (s, e) =>
            {
                _parameters.OscillatorType = OscillatorType.StochRSI;
            };

            var stochasticRadio = new RadioButton
            {
                Content = "Stochastic Oscillator",
                IsChecked = _parameters.OscillatorType == OscillatorType.Stochastic,
                Margin = new Thickness(10, 0, 10, 0)
            };
            stochasticRadio.Checked += (s, e) =>
            {
                _parameters.OscillatorType = OscillatorType.Stochastic;
            };

            oscillatorTypeRadioPanel.Children.Add(stochRsiRadio);
            oscillatorTypeRadioPanel.Children.Add(stochasticRadio);
            oscillatorTypePanel.Children.Add(oscillatorTypeRadioPanel);

            oscillatorTypeGroup.Content = oscillatorTypePanel;
            panel.Children.Add(oscillatorTypeGroup);

            // Параметры RSI
            var rsiGroup = CreateParameterGroup("Параметры RSI");
            var rsiGrid = new Grid();
            rsiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rsiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            AddParameterRow(rsiGrid, "Период RSI:", nameof(RsiStrategyParameters.RsiPeriod), row++);
            AddParameterRow(rsiGrid, "Уровень перекупленности:", nameof(RsiStrategyParameters.RsiOverbought), row++, "F1");
            AddParameterRow(rsiGrid, "Уровень перепроданности:", nameof(RsiStrategyParameters.RsiOversold), row++, "F1");

            rsiGroup.Content = rsiGrid;
            panel.Children.Add(rsiGroup);

            // Параметры Stochastic
            var stochGroup = CreateParameterGroup(_parameters.OscillatorType == OscillatorType.StochRSI ?
                "Параметры StochRSI" : "Параметры Stochastic");
            var stochGrid = new Grid();
            stochGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stochGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row = 0;
            AddParameterRow(stochGrid, "Период:", nameof(RsiStrategyParameters.StochPeriod), row++);
            AddParameterRow(stochGrid, "Уровень перекупленности:", nameof(RsiStrategyParameters.StochOverbought), row++, "F1");
            AddParameterRow(stochGrid, "Уровень перепроданности:", nameof(RsiStrategyParameters.StochOversold), row++, "F1");
            AddParameterRow(stochGrid, "Сглаживание K:", nameof(RsiStrategyParameters.StochSmoothK), row++);
            AddParameterRow(stochGrid, "Сглаживание D:", nameof(RsiStrategyParameters.StochSmoothD), row++);

            stochGroup.Content = stochGrid;
            panel.Children.Add(stochGroup);

            // Параметры входа
            var entryGroup = CreateParameterGroup("Параметры входа");
            var entryPanel = new StackPanel();

            // Тип ордера на вход
            var entryOrderTypePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            entryOrderTypePanel.Children.Add(new TextBlock { Text = "Тип входа:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var entryOrderTypeCombo = new ComboBox
            {
                ItemsSource = new[] { OrderType.Market, OrderType.Limit, OrderType.StopLimit, OrderType.MovingTakeProfitEntry, OrderType.LevelCrossingEntry },
                SelectedItem = _parameters.EntryOrderType,
                Width = 200
            };
            entryOrderTypeCombo.SetBinding(ComboBox.SelectedItemProperty,
                new System.Windows.Data.Binding(nameof(RsiStrategyParameters.EntryOrderType))
                { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
            entryOrderTypePanel.Children.Add(entryOrderTypeCombo);
            entryPanel.Children.Add(entryOrderTypePanel);

            // Хранилище для динамических элементов входа
            var entryDynamicContainer = new StackPanel();
            entryPanel.Children.Add(entryDynamicContainer);

            // Функция для обновления UI входа
            void UpdateEntryUI()
            {
                entryDynamicContainer.Children.Clear();

                var entryType = _parameters.EntryOrderType;

                if (entryType == OrderType.Limit)
                {
                    var limitOffsetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    limitOffsetPanel.Children.Add(new TextBlock { Text = "Смещение (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var limitOffsetTextBox = new TextBox
                    {
                        Text = _parameters.EntryLimitOffsetPercent.ToString(),
                        Width = 80
                    };
                    limitOffsetTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.EntryLimitOffsetPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    limitOffsetPanel.Children.Add(limitOffsetTextBox);
                    entryDynamicContainer.Children.Add(limitOffsetPanel);
                }
                else if (entryType == OrderType.StopLimit)
                {
                    var stopOffsetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    stopOffsetPanel.Children.Add(new TextBlock { Text = "Смещение (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var stopOffsetTextBox = new TextBox
                    {
                        Text = _parameters.EntryStopOffsetPercent.ToString(),
                        Width = 80
                    };
                    stopOffsetTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.EntryStopOffsetPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    stopOffsetPanel.Children.Add(stopOffsetTextBox);
                    entryDynamicContainer.Children.Add(stopOffsetPanel);
                }
                else if (entryType == OrderType.MovingTakeProfitEntry)
                {
                    var movingTPEntryGroup = CreateParameterGroup("Скользящий тейк-профит на входе");
                    var movingTPEntryPanel = new StackPanel();

                    var movingTPEntryCalcPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    movingTPEntryCalcPanel.Children.Add(new TextBlock { Text = "Расчет цели:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var movingTPEntryCalcCombo = new ComboBox
                    {
                        ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                        SelectedItem = _parameters.MovingTPEntryCalculationType,
                        Width = 100
                    };
                    movingTPEntryCalcCombo.SetBinding(ComboBox.SelectedItemProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPEntryCalculationType))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                    movingTPEntryCalcPanel.Children.Add(movingTPEntryCalcCombo);
                    movingTPEntryPanel.Children.Add(movingTPEntryCalcPanel);

                    var targetPercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    targetPercentPanel.Children.Add(new TextBlock { Text = "Целевой уровень (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var targetPercentTextBox = new TextBox
                    {
                        Text = _parameters.MovingTPEntryTargetPercent.ToString(),
                        Width = 80
                    };
                    targetPercentTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPEntryTargetPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    targetPercentPanel.Children.Add(targetPercentTextBox);
                    movingTPEntryPanel.Children.Add(targetPercentPanel);

                    var atrOffsetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    atrOffsetPanel.Children.Add(new TextBlock { Text = "Отступ в АТР:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var atrOffsetTextBox = new TextBox
                    {
                        Text = _parameters.AtrMultiplier.ToString(),
                        Width = 80
                    };
                    atrOffsetTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.AtrMultiplier))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    atrOffsetPanel.Children.Add(atrOffsetTextBox);
                    movingTPEntryPanel.Children.Add(atrOffsetPanel);

                    var entrySlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    entrySlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var entrySlippageTextBox = new TextBox
                    {
                        Text = _parameters.MovingTPEntrySlippage.ToString(),
                        Width = 80
                    };
                    entrySlippageTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPEntrySlippage))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    entrySlippagePanel.Children.Add(entrySlippageTextBox);
                    movingTPEntryPanel.Children.Add(entrySlippagePanel);

                    var timeoutPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    timeoutPanel.Children.Add(new TextBlock { Text = "Тайм-аут (минуты):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var timeoutTextBox = new TextBox
                    {
                        Text = _parameters.MovingTPEntryTimeoutMinutes.ToString(),
                        Width = 80
                    };
                    timeoutTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPEntryTimeoutMinutes))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    timeoutPanel.Children.Add(timeoutTextBox);
                    movingTPEntryPanel.Children.Add(timeoutPanel);

                    movingTPEntryGroup.Content = movingTPEntryPanel;
                    entryDynamicContainer.Children.Add(movingTPEntryGroup);
                }
                else if (entryType == OrderType.LevelCrossingEntry)
                {
                    // Параметры для ВХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
                    var levelCrossingGroup = CreateParameterGroup("ВХОД ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ");
                    var levelCrossingPanel = new StackPanel();

                    // Информационное сообщение
                    var infoText = new TextBlock
                    {
                        Text = "Лонг: пересечение уровня перепроданности СНИЗУ ВВЕРХ\nШорт: пересечение уровня перекупленности СВЕРХУ ВНИЗ",
                        FontSize = 11,
                        Foreground = Brushes.DarkBlue,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    levelCrossingPanel.Children.Add(infoText);

                    // Защитный стоп-лосс для входа
                    var entryProtectiveStopPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    entryProtectiveStopPanel.Children.Add(new TextBlock { Text = "Защитный стоп входа (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var entryProtectiveStopTextBox = new TextBox
                    {
                        Text = _parameters.LevelCrossingEntryProtectiveStopPercent.ToString(),
                        Width = 80
                    };
                    entryProtectiveStopTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.LevelCrossingEntryProtectiveStopPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    entryProtectiveStopPanel.Children.Add(entryProtectiveStopTextBox);
                    levelCrossingPanel.Children.Add(entryProtectiveStopPanel);

                    var entryProtectiveStopDistancePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    entryProtectiveStopDistancePanel.Children.Add(new TextBlock { Text = "Мин. дистанция стопа (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var entryProtectiveStopDistanceTextBox = new TextBox
                    {
                        Text = _parameters.LevelCrossingEntryProtectiveStopDistancePercent.ToString(),
                        Width = 80
                    };
                    entryProtectiveStopDistanceTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.LevelCrossingEntryProtectiveStopDistancePercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    entryProtectiveStopDistancePanel.Children.Add(entryProtectiveStopDistanceTextBox);
                    levelCrossingPanel.Children.Add(entryProtectiveStopDistancePanel);

                    levelCrossingGroup.Content = levelCrossingPanel;
                    entryDynamicContainer.Children.Add(levelCrossingGroup);
                }

                // Проскальзывание для входа (для всех типов, кроме MovingTakeProfitEntry)
                if (entryType != OrderType.MovingTakeProfitEntry)
                {
                    var entrySlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    entrySlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание входа (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var entrySlippageTextBox = new TextBox
                    {
                        Text = _parameters.EntrySlippage.ToString(),
                        Width = 80
                    };
                    entrySlippageTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.EntrySlippage))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    entrySlippagePanel.Children.Add(entrySlippageTextBox);
                    entryDynamicContainer.Children.Add(entrySlippagePanel);
                }
            }

            // Подписываемся на изменение типа входа
            entryOrderTypeCombo.SelectionChanged += (s, e) =>
            {
                UpdateEntryUI();
            };

            // Первоначальное заполнение
            UpdateEntryUI();

            // Размер позиции
            var positionSizePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            positionSizePanel.Children.Add(new TextBlock { Text = "Размер позиции (% от депозита):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var positionSizeTextBox = new TextBox
            {
                Text = _parameters.OrderSizePercent.ToString(),
                Width = 80
            };
            positionSizeTextBox.SetBinding(TextBox.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiStrategyParameters.OrderSizePercent))
                { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
            positionSizePanel.Children.Add(positionSizeTextBox);
            entryPanel.Children.Add(positionSizePanel);

            entryGroup.Content = entryPanel;
            panel.Children.Add(entryGroup);

            // Параметры выхода
            var exitGroup = CreateParameterGroup("Параметры выхода");
            var exitPanel = new StackPanel();

            // Тип ордера на выход
            var exitOrderTypePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            exitOrderTypePanel.Children.Add(new TextBlock { Text = "Тип выхода:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var exitOrderTypeCombo = new ComboBox
            {
                ItemsSource = new[] { OrderType.Market, OrderType.MovingTakeProfitExit, OrderType.TrailingStopExit, OrderType.LevelCrossingExit },
                SelectedItem = _parameters.ExitOrderType,
                Width = 200
            };
            exitOrderTypeCombo.SetBinding(ComboBox.SelectedItemProperty,
                new System.Windows.Data.Binding(nameof(RsiStrategyParameters.ExitOrderType))
                { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
            exitOrderTypePanel.Children.Add(exitOrderTypeCombo);
            exitPanel.Children.Add(exitOrderTypePanel);

            // Хранилище для динамических элементов выхода
            var exitDynamicContainer = new StackPanel();
            exitPanel.Children.Add(exitDynamicContainer);

            // Функция для обновления UI выхода
            void UpdateExitUI()
            {
                exitDynamicContainer.Children.Clear();

                var exitType = _parameters.ExitOrderType;

                if (exitType == OrderType.MovingTakeProfitExit)
                {
                    var movingTPExitGroup = CreateParameterGroup("Скользящий тейк-профит на выходе");
                    var movingTPExitPanel = new StackPanel();

                    var movingTPExitCalcPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    movingTPExitCalcPanel.Children.Add(new TextBlock { Text = "Расчет старт. уровня:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var movingTPExitCalcCombo = new ComboBox
                    {
                        ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                        SelectedItem = _parameters.MovingTPExitCalculationType,
                        Width = 100
                    };
                    movingTPExitCalcCombo.SetBinding(ComboBox.SelectedItemProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPExitCalculationType))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                    movingTPExitCalcPanel.Children.Add(movingTPExitCalcCombo);
                    movingTPExitPanel.Children.Add(movingTPExitCalcPanel);

                    var startPercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    startPercentPanel.Children.Add(new TextBlock { Text = "Стартовый уровень (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var startPercentTextBox = new TextBox
                    {
                        Text = _parameters.MovingTPExitStartPercent.ToString(),
                        Width = 80
                    };
                    startPercentTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPExitStartPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    startPercentPanel.Children.Add(startPercentTextBox);
                    movingTPExitPanel.Children.Add(startPercentPanel);

                    var atrOffsetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    atrOffsetPanel.Children.Add(new TextBlock { Text = "Отступ в АТР:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var atrOffsetTextBox = new TextBox
                    {
                        Text = _parameters.AtrMultiplier.ToString(),
                        Width = 80
                    };
                    atrOffsetTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.AtrMultiplier))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    atrOffsetPanel.Children.Add(atrOffsetTextBox);
                    movingTPExitPanel.Children.Add(atrOffsetPanel);

                    var exitSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    exitSlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var exitSlippageTextBox = new TextBox
                    {
                        Text = _parameters.MovingTPExitSlippage.ToString(),
                        Width = 80
                    };
                    exitSlippageTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPExitSlippage))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    exitSlippagePanel.Children.Add(exitSlippageTextBox);
                    movingTPExitPanel.Children.Add(exitSlippagePanel);

                    var timeoutPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    timeoutPanel.Children.Add(new TextBlock { Text = "Тайм-аут (минуты):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var timeoutTextBox = new TextBox
                    {
                        Text = _parameters.MovingTPExitTimeoutMinutes.ToString(),
                        Width = 80
                    };
                    timeoutTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPExitTimeoutMinutes))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    timeoutPanel.Children.Add(timeoutTextBox);
                    movingTPExitPanel.Children.Add(timeoutPanel);

                    movingTPExitGroup.Content = movingTPExitPanel;
                    exitDynamicContainer.Children.Add(movingTPExitGroup);
                }
                else if (exitType == OrderType.TrailingStopExit)
                {
                    var trailingStopGroup = CreateParameterGroup("Трейлинг-стоп на выходе");
                    var trailingStopPanel = new StackPanel();

                    var trailingStopCalcPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    trailingStopCalcPanel.Children.Add(new TextBlock { Text = "Расчет дистанции:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var trailingStopCalcCombo = new ComboBox
                    {
                        ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                        SelectedItem = _parameters.TrailingStopExitCalculationType,
                        Width = 100
                    };
                    trailingStopCalcCombo.SetBinding(ComboBox.SelectedItemProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TrailingStopExitCalculationType))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                    trailingStopCalcPanel.Children.Add(trailingStopCalcCombo);
                    trailingStopPanel.Children.Add(trailingStopCalcPanel);

                    var distancePercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    distancePercentPanel.Children.Add(new TextBlock { Text = "Дистанция (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var distancePercentTextBox = new TextBox
                    {
                        Text = _parameters.TrailingStopExitDistancePercent.ToString(),
                        Width = 80
                    };
                    distancePercentTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TrailingStopExitDistancePercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    distancePercentPanel.Children.Add(distancePercentTextBox);
                    trailingStopPanel.Children.Add(distancePercentPanel);

                    var protectiveStopPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    protectiveStopPanel.Children.Add(new TextBlock
                    {
                        Text = "Защитный стоп (%):",
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "Стоп-лосс, срабатывающий до активации трейлинг-стопа"
                    });
                    var protectiveStopTextBox = new TextBox
                    {
                        Text = _parameters.ProtectiveStopPercent.ToString(),
                        Width = 80
                    };
                    protectiveStopTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.ProtectiveStopPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    protectiveStopPanel.Children.Add(protectiveStopTextBox);
                    trailingStopPanel.Children.Add(protectiveStopPanel);

                    var trailingSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    trailingSlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var trailingSlippageTextBox = new TextBox
                    {
                        Text = _parameters.TrailingStopExitSlippage.ToString(),
                        Width = 80
                    };
                    trailingSlippageTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TrailingStopExitSlippage))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    trailingSlippagePanel.Children.Add(trailingSlippageTextBox);
                    trailingStopPanel.Children.Add(trailingSlippagePanel);

                    var activationPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    activationPanel.Children.Add(new TextBlock { Text = "Активация после (% прибыли):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var activationTextBox = new TextBox
                    {
                        Text = _parameters.TrailingStopExitActivationPercent.ToString(),
                        Width = 80
                    };
                    activationTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TrailingStopExitActivationPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    activationPanel.Children.Add(activationTextBox);
                    trailingStopPanel.Children.Add(activationPanel);

                    trailingStopGroup.Content = trailingStopPanel;
                    exitDynamicContainer.Children.Add(trailingStopGroup);
                }
                else if (exitType == OrderType.LevelCrossingExit)
                {
                    // Параметры для ВЫХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
                    var levelCrossingExitGroup = CreateParameterGroup("ВЫХОД ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ");
                    var levelCrossingExitPanel = new StackPanel();

                    // Информационное сообщение
                    var infoText = new TextBlock
                    {
                        Text = "Из лонга: пересечение уровня перекупленности СВЕРХУ ВНИЗ\nИз шорта: пересечение уровня перепроданности СНИЗУ ВВЕРХ",
                        FontSize = 11,
                        Foreground = Brushes.DarkBlue,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 5)
                    };
                    levelCrossingExitPanel.Children.Add(infoText);

                    // Защитный стоп-лосс для выхода
                    var exitProtectiveStopPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    exitProtectiveStopPanel.Children.Add(new TextBlock { Text = "Защитный стоп выхода (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var exitProtectiveStopTextBox = new TextBox
                    {
                        Text = _parameters.LevelCrossingExitProtectiveStopPercent.ToString(),
                        Width = 80
                    };
                    exitProtectiveStopTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.LevelCrossingExitProtectiveStopPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    exitProtectiveStopPanel.Children.Add(exitProtectiveStopTextBox);
                    levelCrossingExitPanel.Children.Add(exitProtectiveStopPanel);

                    var exitProtectiveStopDistancePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    exitProtectiveStopDistancePanel.Children.Add(new TextBlock { Text = "Мин. дистанция стопа (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var exitProtectiveStopDistanceTextBox = new TextBox
                    {
                        Text = _parameters.LevelCrossingExitProtectiveStopDistancePercent.ToString(),
                        Width = 80
                    };
                    exitProtectiveStopDistanceTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.LevelCrossingExitProtectiveStopDistancePercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    exitProtectiveStopDistancePanel.Children.Add(exitProtectiveStopDistanceTextBox);
                    levelCrossingExitPanel.Children.Add(exitProtectiveStopDistancePanel);

                    levelCrossingExitGroup.Content = levelCrossingExitPanel;
                    exitDynamicContainer.Children.Add(levelCrossingExitGroup);
                }
                else if (exitType == OrderType.Market)
                {
                    // Обычные параметры выхода для Market
                    var exitParamsGroup = CreateParameterGroup("Параметры выхода");
                    var exitParamsPanel = new StackPanel();

                    // Комбобокс для выбора типа расчета тейк-профита
                    var tpCalcTypePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    tpCalcTypePanel.Children.Add(new TextBlock { Text = "Расчет тейк-профита:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var tpCalcTypeCombo = new ComboBox
                    {
                        ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                        SelectedItem = _parameters.TakeProfitCalculationType,
                        Width = 100
                    };
                    tpCalcTypeCombo.SetBinding(ComboBox.SelectedItemProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitCalculationType))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                    tpCalcTypePanel.Children.Add(tpCalcTypeCombo);
                    exitParamsPanel.Children.Add(tpCalcTypePanel);

                    // Поля для ввода значений тейк-профита
                    if (_parameters.TakeProfitCalculationType == PriceCalculationType.Percentage)
                    {
                        var tpPercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                        tpPercentPanel.Children.Add(new TextBlock { Text = "Тейк-профит (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                        var tpPercentTextBox = new TextBox
                        {
                            Text = _parameters.TakeProfitPercent.ToString(),
                            Width = 80
                        };
                        tpPercentTextBox.SetBinding(TextBox.TextProperty,
                            new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitPercent))
                            { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                        tpPercentPanel.Children.Add(tpPercentTextBox);
                        exitParamsPanel.Children.Add(tpPercentPanel);
                    }
                    else if (_parameters.TakeProfitCalculationType == PriceCalculationType.Absolute)
                    {
                        var tpAbsolutePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                        tpAbsolutePanel.Children.Add(new TextBlock { Text = "Тейк-профит (абс.):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                        var tpAbsoluteTextBox = new TextBox
                        {
                            Text = _parameters.TakeProfitAbsolute.ToString(),
                            Width = 80
                        };
                        tpAbsoluteTextBox.SetBinding(TextBox.TextProperty,
                            new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitAbsolute))
                            { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                        tpAbsolutePanel.Children.Add(tpAbsoluteTextBox);
                        exitParamsPanel.Children.Add(tpAbsolutePanel);
                    }
                    else if (_parameters.TakeProfitCalculationType == PriceCalculationType.ATR)
                    {
                        var tpAtrPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                        tpAtrPanel.Children.Add(new TextBlock { Text = "Множитель ATR:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                        var tpAtrTextBox = new TextBox
                        {
                            Text = _parameters.AtrMultiplier.ToString(),
                            Width = 80
                        };
                        tpAtrTextBox.SetBinding(TextBox.TextProperty,
                            new System.Windows.Data.Binding(nameof(RsiStrategyParameters.AtrMultiplier))
                            { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                        tpAtrPanel.Children.Add(tpAtrTextBox);
                        exitParamsPanel.Children.Add(tpAtrPanel);
                    }

                    // Цена активации тейк-профита
                    var tpActivationPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    tpActivationPanel.Children.Add(new TextBlock { Text = "Цена активации:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var tpActivationTextBox = new TextBox
                    {
                        Text = _parameters.TakeProfitActivationPrice.ToString(),
                        Width = 80
                    };
                    tpActivationTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitActivationPrice))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    tpActivationPanel.Children.Add(tpActivationTextBox);
                    exitParamsPanel.Children.Add(tpActivationPanel);

                    // Проскальзывание тейк-профита
                    var tpSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    tpSlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var tpSlippageTextBox = new TextBox
                    {
                        Text = _parameters.TakeProfitSlippage.ToString(),
                        Width = 80
                    };
                    tpSlippageTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitSlippage))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    tpSlippagePanel.Children.Add(tpSlippageTextBox);
                    exitParamsPanel.Children.Add(tpSlippagePanel);

                    // Комбобокс для выбора типа расчета стоп-лосса
                    var slCalcTypePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    slCalcTypePanel.Children.Add(new TextBlock { Text = "Расчет стоп-лосса:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var slCalcTypeCombo = new ComboBox
                    {
                        ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                        SelectedItem = _parameters.StopLossCalculationType,
                        Width = 100
                    };
                    slCalcTypeCombo.SetBinding(ComboBox.SelectedItemProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossCalculationType))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                    slCalcTypePanel.Children.Add(slCalcTypeCombo);
                    exitParamsPanel.Children.Add(slCalcTypePanel);

                    // Поля для ввода значений стоп-лосса
                    if (_parameters.StopLossCalculationType == PriceCalculationType.Percentage)
                    {
                        var slPercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                        slPercentPanel.Children.Add(new TextBlock { Text = "Стоп-лосс (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                        var slPercentTextBox = new TextBox
                        {
                            Text = _parameters.StopLossPercent.ToString(),
                            Width = 80
                        };
                        slPercentTextBox.SetBinding(TextBox.TextProperty,
                            new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossPercent))
                            { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                        slPercentPanel.Children.Add(slPercentTextBox);
                        exitParamsPanel.Children.Add(slPercentPanel);
                    }
                    else if (_parameters.StopLossCalculationType == PriceCalculationType.Absolute)
                    {
                        var slAbsolutePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                        slAbsolutePanel.Children.Add(new TextBlock { Text = "Стоп-лосс (абс.):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                        var slAbsoluteTextBox = new TextBox
                        {
                            Text = _parameters.StopLossAbsolute.ToString(),
                            Width = 80
                        };
                        slAbsoluteTextBox.SetBinding(TextBox.TextProperty,
                            new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossAbsolute))
                            { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                        slAbsolutePanel.Children.Add(slAbsoluteTextBox);
                        exitParamsPanel.Children.Add(slAbsolutePanel);
                    }
                    else if (_parameters.StopLossCalculationType == PriceCalculationType.ATR)
                    {
                        var slAtrPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                        slAtrPanel.Children.Add(new TextBlock { Text = "Множитель ATR:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                        var slAtrTextBox = new TextBox
                        {
                            Text = _parameters.AtrMultiplier.ToString(),
                            Width = 80
                        };
                        slAtrTextBox.SetBinding(TextBox.TextProperty,
                            new System.Windows.Data.Binding(nameof(RsiStrategyParameters.AtrMultiplier))
                            { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                        slAtrPanel.Children.Add(slAtrTextBox);
                        exitParamsPanel.Children.Add(slAtrPanel);
                    }

                    // Цена активации стоп-лосса
                    var slActivationPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    slActivationPanel.Children.Add(new TextBlock { Text = "Цена активации:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var slActivationTextBox = new TextBox
                    {
                        Text = _parameters.StopLossActivationPrice.ToString(),
                        Width = 80
                    };
                    slActivationTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossActivationPrice))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    slActivationPanel.Children.Add(slActivationTextBox);
                    exitParamsPanel.Children.Add(slActivationPanel);

                    // Проскальзывание стоп-лосса
                    var slSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    slSlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var slSlippageTextBox = new TextBox
                    {
                        Text = _parameters.StopLossSlippage.ToString(),
                        Width = 80
                    };
                    slSlippageTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossSlippage))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    slSlippagePanel.Children.Add(slSlippageTextBox);
                    exitParamsPanel.Children.Add(slSlippagePanel);

                    exitParamsGroup.Content = exitParamsPanel;
                    exitDynamicContainer.Children.Add(exitParamsGroup);

                    // Общее проскальзывание для выхода
                    var exitSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    exitSlippagePanel.Children.Add(new TextBlock { Text = "Общее проскальзывание выхода (%):", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                    var exitSlippageTextBox = new TextBox
                    {
                        Text = _parameters.ExitSlippage.ToString(),
                        Width = 80
                    };
                    exitSlippageTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.ExitSlippage))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    exitSlippagePanel.Children.Add(exitSlippageTextBox);
                    exitDynamicContainer.Children.Add(exitSlippagePanel);
                }
            }

            // Подписываемся на изменение типа выхода
            exitOrderTypeCombo.SelectionChanged += (s, e) =>
            {
                UpdateExitUI();
            };

            // Первоначальное заполнение
            UpdateExitUI();

            // Закрытие при смене сигнала
            var closeOnSignalPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            closeOnSignalPanel.Children.Add(new TextBlock { Text = "Закрывать при смене сигнала:", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var closeOnSignalCheckBox = new CheckBox
            {
                IsChecked = _parameters.CloseOnSignalReversal,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeOnSignalCheckBox.SetBinding(CheckBox.IsCheckedProperty,
                new System.Windows.Data.Binding(nameof(RsiStrategyParameters.CloseOnSignalReversal))
                { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
            closeOnSignalPanel.Children.Add(closeOnSignalCheckBox);
            exitPanel.Children.Add(closeOnSignalPanel);

            exitGroup.Content = exitPanel;
            panel.Children.Add(exitGroup);

            // Кнопки управления
            var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };

            var applyButton = new Button
            {
                Content = "Применить",
                Width = 100,
                Height = 20,
                Margin = new Thickness(5)
            };
            applyButton.Click += (s, e) => _parameters.ApplyParameters();

            var resetButton = new Button
            {
                Content = "Сброс",
                Width = 100,
                Height = 20,
                Margin = new Thickness(5)
            };
            resetButton.Click += (s, e) => _parameters.ResetParameters();

            buttonsPanel.Children.Add(applyButton);
            buttonsPanel.Children.Add(resetButton);
            panel.Children.Add(buttonsPanel);

            return panel;
        }
        private StackPanel CreateControlPanel()
        {
            var panel = new StackPanel { };

            // Текущие значения индикаторов
            var indicatorsGroup = CreateParameterGroup("Текущие значения индикаторов");
            var indicatorsGrid = new Grid();
            indicatorsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            indicatorsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            AddIndicatorRow(indicatorsGrid, "RSI:", nameof(RsiIndicatorValues.RsiValue), "{0:F2}", row++);
            AddIndicatorRow(indicatorsGrid, "StochRSI:", nameof(RsiIndicatorValues.StochRSIK), "{0:F2}", row++);

            if (_parameters.OscillatorType == OscillatorType.StochRSI)
            {
                AddIndicatorRow(indicatorsGrid, "StochRSI:", nameof(RsiIndicatorValues.StochRSIK), "{0:F2}", row++);
            }
            else
            {

                AddIndicatorRow(indicatorsGrid, "Stochastic K:", nameof(RsiIndicatorValues.StochasticK), "{0:F2}", row++);
                AddIndicatorRow(indicatorsGrid, "Stochastic D:", nameof(RsiIndicatorValues.StochasticD), "{0:F2}", row++);
            }

            AddIndicatorRow(indicatorsGrid, "MACD:", nameof(RsiIndicatorValues.MacdValue), "{0:F4}", row++);
            AddIndicatorRow(indicatorsGrid, "MACD Signal:", nameof(RsiIndicatorValues.MacdSignal), "{0:F4}", row++);
            AddIndicatorRow(indicatorsGrid, "MACD Hist:", nameof(RsiIndicatorValues.MacdHistogram), "{0:F4}", row++);
            AddIndicatorRow(indicatorsGrid, "ATR:", nameof(RsiIndicatorValues.AtrValue), "{0:F4}", row++);
            AddIndicatorRow(indicatorsGrid, "Текущая цена:", nameof(RsiIndicatorValues.LastPrice), "{0:F2}", row++);

            indicatorsGroup.Content = indicatorsGrid;
            panel.Children.Add(indicatorsGroup);

            // Детальный статус входа
            var entryStatusGroup = CreateParameterGroup("Детальный статус входа");
            var entryStatusPanel = new StackPanel();

            var entryStatusText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = Brushes.DarkBlue
            };
            entryStatusText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.EntryStatus))
                { Source = _indicatorValues });

            var entryStatusDetails = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 5),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DarkSlateGray
            };
            entryStatusDetails.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.EntryStatusDetails))
                { Source = _indicatorValues });

            entryStatusPanel.Children.Add(entryStatusText);
            entryStatusPanel.Children.Add(entryStatusDetails);
            entryStatusGroup.Content = entryStatusPanel;
            panel.Children.Add(entryStatusGroup);

            // Детальный статус выхода из позиции
            var exitStatusGroup = CreateParameterGroup("Детальный статус выхода из позиции");
            var exitStatusPanel = new StackPanel();

            var exitStatusText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = Brushes.DarkGreen
            };
            exitStatusText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.ExitStatus))
                { Source = _indicatorValues });

            var exitStatusDetails = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 5),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DarkSlateGray
            };
            exitStatusDetails.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.ExitStatusDetails))
                { Source = _indicatorValues });

            exitStatusPanel.Children.Add(exitStatusText);
            exitStatusPanel.Children.Add(exitStatusDetails);
            exitStatusGroup.Content = exitStatusPanel;
            panel.Children.Add(exitStatusGroup);






            // Статус ордеров и расчетов - какая то не информативная панель, решил ее убрать.
            /*var orderStatusGroup = CreateParameterGroup("Статус ордеров и расчетов");
            var orderStatusPanel = new StackPanel();

            var orderStatusText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = Brushes.DarkRed
            };
            orderStatusText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.OrderStatus))
                { Source = _indicatorValues });

            // Показываем расчетные цены
            var pricePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

            var entryPriceText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2)
            };
            entryPriceText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.EntryPrice))
                {
                    Source = _indicatorValues,
                    StringFormat = "Расчетная цена входа: {0:F2}"
                });

            var stopLossText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2)
            };
            stopLossText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.StopLossPrice))
                {
                    Source = _indicatorValues,
                    StringFormat = "Расчетный стоп-лосс: {0:F2}"
                });

            var takeProfitText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2)
            };
            takeProfitText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.TakeProfitPrice))
                {
                    Source = _indicatorValues,
                    StringFormat = "Расчетный тейк-профит: {0:F2}"
                });

            var movingTPEntryText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2)
            };
            movingTPEntryText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.MovingTPEntryPrice))
                {
                    Source = _indicatorValues,
                    StringFormat = "Скользящий TP вход: {0:F2}"
                });

            var movingTPExitText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2)
            };
            movingTPExitText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.MovingTPExitPrice))
                {
                    Source = _indicatorValues,
                    StringFormat = "Скользящий TP выход: {0:F2}"
                });

            var trailingStopText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 2)
            };
            trailingStopText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.TrailingStopExitPrice))
                {
                    Source = _indicatorValues,
                    StringFormat = "Трейлинг-стоп: {0:F2}"
                });

            pricePanel.Children.Add(entryPriceText);
            pricePanel.Children.Add(stopLossText);
            pricePanel.Children.Add(takeProfitText);
            pricePanel.Children.Add(movingTPEntryText);
            pricePanel.Children.Add(movingTPExitText);
            pricePanel.Children.Add(trailingStopText);

            orderStatusPanel.Children.Add(orderStatusText);
            orderStatusPanel.Children.Add(pricePanel);
            orderStatusGroup.Content = orderStatusPanel;
            panel.Children.Add(orderStatusGroup);*/










            #region Информация о позиции
            // Информация о позиции
            var positionGroup = CreateParameterGroup("Информация о позиции");
            var positionPanel = new StackPanel();


            var signalText = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 5)
            };
            signalText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.Signal))
                { Source = _indicatorValues });

            var signalDescription = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5),
                TextWrapping = TextWrapping.Wrap
            };
            signalDescription.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.SignalDescription))
                { Source = _indicatorValues });

            var statusText = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            };
            statusText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.Status))
                { Source = _indicatorValues });





            var positionText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5)
            };
            positionText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.CurrentPosition))
                { Source = _indicatorValues });

            var pnlText = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(0, 5, 0, 5)
            };
            pnlText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.CurrentPnL))
                {
                    Source = _indicatorValues,
                    StringFormat = "Текущий P&L: {0:F2}"
                });

            /*var profitLossText = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 5)
            };*/

            // Создаем MultiBinding для отображения прибыли и убытка
            /*var multiBinding = new System.Windows.Data.MultiBinding();
            multiBinding.StringFormat = "Потенциальная прибыль: {0:F2}%, убыток: {1:F2}%";
            multiBinding.Bindings.Add(new System.Windows.Data.Binding(nameof(RsiIndicatorValues.PotentialProfit))
            {
                Source = _indicatorValues
            });
            multiBinding.Bindings.Add(new System.Windows.Data.Binding(nameof(RsiIndicatorValues.PotentialLoss))
            {
                Source = _indicatorValues
            });
            profitLossText.SetBinding(TextBlock.TextProperty, multiBinding);*/

            positionPanel.Children.Add(signalText);
            positionPanel.Children.Add(signalDescription);
            positionPanel.Children.Add(statusText);


            positionPanel.Children.Add(positionText);
            positionPanel.Children.Add(pnlText);
            //positionPanel.Children.Add(profitLossText);
            positionGroup.Content = positionPanel;
            panel.Children.Add(positionGroup);
            #endregion









            #region Торговые сигналы
            // Торговые сигналы
            var signalGroup = CreateParameterGroup("Торговые сигналы");
            var signalPanel = new StackPanel();



            var actionText = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.DarkSlateGray,
                FontStyle = FontStyles.Italic
            };
            actionText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.LastAction))
                { Source = _indicatorValues });

            var lastUpdateText = new TextBlock
            {
                FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 5, 0, 0)
            };
            lastUpdateText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.LastUpdate))
                {
                    Source = _indicatorValues,
                    StringFormat = "Последнее обновление: {0:HH:mm:ss}"
                });

            //signalPanel.Children.Add(signalText);
            //signalPanel.Children.Add(signalDescription);
            //signalPanel.Children.Add(statusText);
            signalPanel.Children.Add(actionText);
            signalPanel.Children.Add(lastUpdateText);
            signalGroup.Content = signalPanel;
            panel.Children.Add(signalGroup);
            #endregion

            #region Статус стратегии
            // Статус стратегии
            var strategyGroup = CreateParameterGroup("Статус стратегии");
            var strategyPanel = new StackPanel();

            var strategyStatusText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5)
            };
            strategyStatusText.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.StrategyStatus))
                { Source = _indicatorValues });

            var strategyStatusColor = new Rectangle
            {
                Height = 10,
                Margin = new Thickness(0, 0, 0, 5)
            };
            strategyStatusColor.SetBinding(Rectangle.FillProperty,
                new System.Windows.Data.Binding(nameof(RsiIndicatorValues.StrategyStatusColor))
                { Source = _indicatorValues });

            strategyPanel.Children.Add(strategyStatusText);
            strategyPanel.Children.Add(strategyStatusColor);
            strategyGroup.Content = strategyPanel;
            #endregion

            panel.Children.Add(strategyGroup);
            return panel;
        }
        private void AddParameterRow(Grid grid, string label, string property, int row, string stringFormat = null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelControl = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelControl, 0);
            Grid.SetRow(labelControl, row);

            var textBox = new TextBox
            {
                Margin = new Thickness(0, 5, 0, 5),
                Width = 80
            };

            var binding = new System.Windows.Data.Binding(property)
            {
                Source = _parameters,
                Mode = System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            };

            if (!string.IsNullOrEmpty(stringFormat))
            {
                binding.StringFormat = "{0:" + stringFormat + "}";
            }

            textBox.SetBinding(TextBox.TextProperty, binding);

            Grid.SetColumn(textBox, 1);
            Grid.SetRow(textBox, row);

            grid.Children.Add(labelControl);
            grid.Children.Add(textBox);
        }
        private void AddIndicatorRow(Grid grid, string label, string property, string format, int row)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelControl = new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 10, 5)
            };
            Grid.SetColumn(labelControl, 0);
            Grid.SetRow(labelControl, row);

            var valueControl = new TextBlock
            {
                Margin = new Thickness(0, 5, 0, 5)
            };
            valueControl.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding(property)
                {
                    Source = _indicatorValues,
                    StringFormat = format
                });
            Grid.SetColumn(valueControl, 1);
            Grid.SetRow(valueControl, row);

            grid.Children.Add(labelControl);
            grid.Children.Add(valueControl);
        }
        private GroupBox CreateParameterGroup(string headerText)
        {
            return new GroupBox
            {
                Header = headerText,
                Margin = new Thickness(0, 0, 0, 3),
                Padding = new Thickness(3),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1)
            };
        }
        private void UpdateEntryTypeUI(StackPanel entryPanel, OrderType entryType)
        {
            // Находим контейнер для динамических элементов
            StackPanel dynamicContainer = null;
            foreach (var child in entryPanel.Children)
            {
                if (child is StackPanel panel && panel.Children.Count > 0)
                {
                    // Ищем контейнер, который не содержит ComboBox (это наш динамический контейнер)
                    bool hasComboBox = false;
                    foreach (var innerChild in panel.Children)
                    {
                        if (innerChild is ComboBox)
                        {
                            hasComboBox = true;
                            break;
                        }
                    }
                    if (!hasComboBox)
                    {
                        dynamicContainer = panel;
                        break;
                    }
                }
            }

            if (dynamicContainer == null)
            {
                // Если контейнер не найден, создаем новый
                dynamicContainer = new StackPanel();
                // Находим позицию для вставки
                int index = 0;
                for (int i = 0; i < entryPanel.Children.Count; i++)
                {
                    if (entryPanel.Children[i] is StackPanel panel &&
                        panel.Children.OfType<ComboBox>().Any())
                    {
                        index = i + 1;
                        break;
                    }
                }
                entryPanel.Children.Insert(index, dynamicContainer);
            }

            // Очищаем контейнер
            dynamicContainer.Children.Clear();

            if (entryType == OrderType.Limit)
            {
                var limitOffsetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                limitOffsetPanel.Children.Add(new TextBlock { Text = "Смещение (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var limitOffsetTextBox = new TextBox
                {
                    Text = _parameters.EntryLimitOffsetPercent.ToString(),
                    Width = 80
                };
                limitOffsetTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.EntryLimitOffsetPercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                limitOffsetPanel.Children.Add(limitOffsetTextBox);
                dynamicContainer.Children.Add(limitOffsetPanel);
            }
            else if (entryType == OrderType.StopLimit)
            {
                var stopOffsetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                stopOffsetPanel.Children.Add(new TextBlock { Text = "Смещение (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var stopOffsetTextBox = new TextBox
                {
                    Text = _parameters.EntryStopOffsetPercent.ToString(),
                    Width = 80
                };
                stopOffsetTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.EntryStopOffsetPercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                stopOffsetPanel.Children.Add(stopOffsetTextBox);
                dynamicContainer.Children.Add(stopOffsetPanel);
            }
            else if (entryType == OrderType.MovingTakeProfitEntry)
            {
                var movingTPEntryGroup = CreateParameterGroup("Скользящий тейк-профит на входе");
                var movingTPEntryPanel = new StackPanel();

                var movingTPEntryCalcPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                movingTPEntryCalcPanel.Children.Add(new TextBlock { Text = "Расчет цели:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var movingTPEntryCalcCombo = new ComboBox
                {
                    ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                    SelectedItem = _parameters.MovingTPEntryCalculationType,
                    Width = 100
                };
                movingTPEntryCalcCombo.SetBinding(ComboBox.SelectedItemProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPEntryCalculationType))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                movingTPEntryCalcPanel.Children.Add(movingTPEntryCalcCombo);
                movingTPEntryPanel.Children.Add(movingTPEntryCalcPanel);

                var targetPercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                targetPercentPanel.Children.Add(new TextBlock { Text = "Целевой уровень (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var targetPercentTextBox = new TextBox
                {
                    Text = _parameters.MovingTPEntryTargetPercent.ToString(),
                    Width = 80
                };
                targetPercentTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPEntryTargetPercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                targetPercentPanel.Children.Add(targetPercentTextBox);
                movingTPEntryPanel.Children.Add(targetPercentPanel);

                var atrOffsetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                atrOffsetPanel.Children.Add(new TextBlock { Text = "Отступ в АТР:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var atrOffsetTextBox = new TextBox
                {
                    Text = _parameters.AtrMultiplier.ToString(),
                    Width = 80
                };
                atrOffsetTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.AtrMultiplier))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                atrOffsetPanel.Children.Add(atrOffsetTextBox);
                movingTPEntryPanel.Children.Add(atrOffsetPanel);

                var entrySlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                entrySlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var entrySlippageTextBox = new TextBox
                {
                    Text = _parameters.MovingTPEntrySlippage.ToString(),
                    Width = 80
                };
                entrySlippageTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPEntrySlippage))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                entrySlippagePanel.Children.Add(entrySlippageTextBox);
                movingTPEntryPanel.Children.Add(entrySlippagePanel);

                var timeoutPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                timeoutPanel.Children.Add(new TextBlock { Text = "Тайм-аут (минуты):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var timeoutTextBox = new TextBox
                {
                    Text = _parameters.MovingTPEntryTimeoutMinutes.ToString(),
                    Width = 80
                };
                timeoutTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPEntryTimeoutMinutes))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                timeoutPanel.Children.Add(timeoutTextBox);
                movingTPEntryPanel.Children.Add(timeoutPanel);

                movingTPEntryGroup.Content = movingTPEntryPanel;
                dynamicContainer.Children.Add(movingTPEntryGroup);
            }
            else if (entryType == OrderType.LevelCrossingEntry)
            {
                var levelCrossingGroup = CreateParameterGroup("ВХОД ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ");
                var levelCrossingPanel = new StackPanel();

                var infoText = new TextBlock
                {
                    Text = "Лонг: пересечение уровня перепроданности СНИЗУ ВВЕРХ\nШорт: пересечение уровня перекупленности СВЕРХУ ВНИЗ",
                    FontSize = 11,
                    Foreground = Brushes.DarkBlue,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                levelCrossingPanel.Children.Add(infoText);

                var entryProtectiveStopPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                entryProtectiveStopPanel.Children.Add(new TextBlock { Text = "Защитный стоп входа (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var entryProtectiveStopTextBox = new TextBox
                {
                    Text = _parameters.LevelCrossingEntryProtectiveStopPercent.ToString(),
                    Width = 80
                };
                entryProtectiveStopTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.LevelCrossingEntryProtectiveStopPercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                entryProtectiveStopPanel.Children.Add(entryProtectiveStopTextBox);
                levelCrossingPanel.Children.Add(entryProtectiveStopPanel);

                var entryProtectiveStopDistancePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                entryProtectiveStopDistancePanel.Children.Add(new TextBlock { Text = "Мин. дистанция стопа (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var entryProtectiveStopDistanceTextBox = new TextBox
                {
                    Text = _parameters.LevelCrossingEntryProtectiveStopDistancePercent.ToString(),
                    Width = 80
                };
                entryProtectiveStopDistanceTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.LevelCrossingEntryProtectiveStopDistancePercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                entryProtectiveStopDistancePanel.Children.Add(entryProtectiveStopDistanceTextBox);
                levelCrossingPanel.Children.Add(entryProtectiveStopDistancePanel);

                levelCrossingGroup.Content = levelCrossingPanel;
                dynamicContainer.Children.Add(levelCrossingGroup);
            }

            // Добавляем проскальзывание для всех типов кроме MovingTakeProfitEntry
            if (entryType != OrderType.MovingTakeProfitEntry)
            {
                var entrySlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                entrySlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание входа (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var entrySlippageTextBox = new TextBox
                {
                    Text = _parameters.EntrySlippage.ToString(),
                    Width = 80
                };
                entrySlippageTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.EntrySlippage))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                entrySlippagePanel.Children.Add(entrySlippageTextBox);
                dynamicContainer.Children.Add(entrySlippagePanel);
            }
        }

        private void UpdateExitTypeUI(StackPanel exitPanel, OrderType exitType)
        {
            // Находим контейнер для динамических элементов
            StackPanel dynamicContainer = null;
            foreach (var child in exitPanel.Children)
            {
                if (child is StackPanel panel && panel.Children.Count > 0)
                {
                    bool hasComboBox = false;
                    foreach (var innerChild in panel.Children)
                    {
                        if (innerChild is ComboBox)
                        {
                            hasComboBox = true;
                            break;
                        }
                    }
                    if (!hasComboBox)
                    {
                        dynamicContainer = panel;
                        break;
                    }
                }
            }

            if (dynamicContainer == null)
            {
                dynamicContainer = new StackPanel();
                int index = 0;
                for (int i = 0; i < exitPanel.Children.Count; i++)
                {
                    if (exitPanel.Children[i] is StackPanel panel &&
                        panel.Children.OfType<ComboBox>().Any())
                    {
                        index = i + 1;
                        break;
                    }
                }
                exitPanel.Children.Insert(index, dynamicContainer);
            }

            dynamicContainer.Children.Clear();

            if (exitType == OrderType.MovingTakeProfitExit)
            {
                var movingTPExitGroup = CreateParameterGroup("Скользящий тейк-профит на выходе");
                var movingTPExitPanel = new StackPanel();

                var movingTPExitCalcPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                movingTPExitCalcPanel.Children.Add(new TextBlock { Text = "Расчет старт. уровня:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var movingTPExitCalcCombo = new ComboBox
                {
                    ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                    SelectedItem = _parameters.MovingTPExitCalculationType,
                    Width = 100
                };
                movingTPExitCalcCombo.SetBinding(ComboBox.SelectedItemProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPExitCalculationType))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                movingTPExitCalcPanel.Children.Add(movingTPExitCalcCombo);
                movingTPExitPanel.Children.Add(movingTPExitCalcPanel);

                var startPercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                startPercentPanel.Children.Add(new TextBlock { Text = "Стартовый уровень (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var startPercentTextBox = new TextBox
                {
                    Text = _parameters.MovingTPExitStartPercent.ToString(),
                    Width = 80
                };
                startPercentTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPExitStartPercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                startPercentPanel.Children.Add(startPercentTextBox);
                movingTPExitPanel.Children.Add(startPercentPanel);

                var atrOffsetPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                atrOffsetPanel.Children.Add(new TextBlock { Text = "Отступ в АТР:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var atrOffsetTextBox = new TextBox
                {
                    Text = _parameters.AtrMultiplier.ToString(),
                    Width = 80
                };
                atrOffsetTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.AtrMultiplier))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                atrOffsetPanel.Children.Add(atrOffsetTextBox);
                movingTPExitPanel.Children.Add(atrOffsetPanel);

                var exitSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                exitSlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var exitSlippageTextBox = new TextBox
                {
                    Text = _parameters.MovingTPExitSlippage.ToString(),
                    Width = 80
                };
                exitSlippageTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPExitSlippage))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                exitSlippagePanel.Children.Add(exitSlippageTextBox);
                movingTPExitPanel.Children.Add(exitSlippagePanel);

                var timeoutPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                timeoutPanel.Children.Add(new TextBlock { Text = "Тайм-аут (минуты):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var timeoutTextBox = new TextBox
                {
                    Text = _parameters.MovingTPExitTimeoutMinutes.ToString(),
                    Width = 80
                };
                timeoutTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.MovingTPExitTimeoutMinutes))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                timeoutPanel.Children.Add(timeoutTextBox);
                movingTPExitPanel.Children.Add(timeoutPanel);

                movingTPExitGroup.Content = movingTPExitPanel;
                dynamicContainer.Children.Add(movingTPExitGroup);
            }
            else if (exitType == OrderType.TrailingStopExit)
            {
                var trailingStopGroup = CreateParameterGroup("Трейлинг-стоп на выходе");
                var trailingStopPanel = new StackPanel();

                var trailingStopCalcPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                trailingStopCalcPanel.Children.Add(new TextBlock { Text = "Расчет дистанции:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var trailingStopCalcCombo = new ComboBox
                {
                    ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                    SelectedItem = _parameters.TrailingStopExitCalculationType,
                    Width = 100
                };
                trailingStopCalcCombo.SetBinding(ComboBox.SelectedItemProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TrailingStopExitCalculationType))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                trailingStopCalcPanel.Children.Add(trailingStopCalcCombo);
                trailingStopPanel.Children.Add(trailingStopCalcPanel);

                var distancePercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                distancePercentPanel.Children.Add(new TextBlock { Text = "Дистанция (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var distancePercentTextBox = new TextBox
                {
                    Text = _parameters.TrailingStopExitDistancePercent.ToString(),
                    Width = 80
                };
                distancePercentTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TrailingStopExitDistancePercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                distancePercentPanel.Children.Add(distancePercentTextBox);
                trailingStopPanel.Children.Add(distancePercentPanel);

                var protectiveStopPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                protectiveStopPanel.Children.Add(new TextBlock
                {
                    Text = "Защитный стоп (%):",
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Стоп-лосс, срабатывающий до активации трейлинг-стопа"
                });
                var protectiveStopTextBox = new TextBox
                {
                    Text = _parameters.ProtectiveStopPercent.ToString(),
                    Width = 80
                };
                protectiveStopTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.ProtectiveStopPercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                protectiveStopPanel.Children.Add(protectiveStopTextBox);
                trailingStopPanel.Children.Add(protectiveStopPanel);

                var trailingSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                trailingSlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var trailingSlippageTextBox = new TextBox
                {
                    Text = _parameters.TrailingStopExitSlippage.ToString(),
                    Width = 80
                };
                trailingSlippageTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TrailingStopExitSlippage))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                trailingSlippagePanel.Children.Add(trailingSlippageTextBox);
                trailingStopPanel.Children.Add(trailingSlippagePanel);

                var activationPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                activationPanel.Children.Add(new TextBlock { Text = "Активация после (% прибыли):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var activationTextBox = new TextBox
                {
                    Text = _parameters.TrailingStopExitActivationPercent.ToString(),
                    Width = 80
                };
                activationTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TrailingStopExitActivationPercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                activationPanel.Children.Add(activationTextBox);
                trailingStopPanel.Children.Add(activationPanel);

                trailingStopGroup.Content = trailingStopPanel;
                dynamicContainer.Children.Add(trailingStopGroup);
            }
            else if (exitType == OrderType.LevelCrossingExit)
            {
                var levelCrossingExitGroup = CreateParameterGroup("ВЫХОД ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ");
                var levelCrossingExitPanel = new StackPanel();

                var infoText = new TextBlock
                {
                    Text = "Из лонга: пересечение уровня перекупленности СВЕРХУ ВНИЗ\nИз шорта: пересечение уровня перепроданности СНИЗУ ВВЕРХ",
                    FontSize = 11,
                    Foreground = Brushes.DarkBlue,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                levelCrossingExitPanel.Children.Add(infoText);

                var exitProtectiveStopPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                exitProtectiveStopPanel.Children.Add(new TextBlock { Text = "Защитный стоп выхода (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var exitProtectiveStopTextBox = new TextBox
                {
                    Text = _parameters.LevelCrossingExitProtectiveStopPercent.ToString(),
                    Width = 80
                };
                exitProtectiveStopTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.LevelCrossingExitProtectiveStopPercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                exitProtectiveStopPanel.Children.Add(exitProtectiveStopTextBox);
                levelCrossingExitPanel.Children.Add(exitProtectiveStopPanel);

                var exitProtectiveStopDistancePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                exitProtectiveStopDistancePanel.Children.Add(new TextBlock { Text = "Мин. дистанция стопа (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var exitProtectiveStopDistanceTextBox = new TextBox
                {
                    Text = _parameters.LevelCrossingExitProtectiveStopDistancePercent.ToString(),
                    Width = 80
                };
                exitProtectiveStopDistanceTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.LevelCrossingExitProtectiveStopDistancePercent))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                exitProtectiveStopDistancePanel.Children.Add(exitProtectiveStopDistanceTextBox);
                levelCrossingExitPanel.Children.Add(exitProtectiveStopDistancePanel);

                levelCrossingExitGroup.Content = levelCrossingExitPanel;
                dynamicContainer.Children.Add(levelCrossingExitGroup);
            }
            else if (exitType == OrderType.Market)
            {
                // Обычные параметры выхода для Market
                var exitParamsGroup = CreateParameterGroup("Параметры выхода");
                var exitParamsPanel = new StackPanel();

                // Комбобокс для выбора типа расчета тейк-профита
                var tpCalcTypePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                tpCalcTypePanel.Children.Add(new TextBlock { Text = "Расчет тейк-профита:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var tpCalcTypeCombo = new ComboBox
                {
                    ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                    SelectedItem = _parameters.TakeProfitCalculationType,
                    Width = 100
                };
                tpCalcTypeCombo.SetBinding(ComboBox.SelectedItemProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitCalculationType))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                tpCalcTypePanel.Children.Add(tpCalcTypeCombo);
                exitParamsPanel.Children.Add(tpCalcTypePanel);

                // Поля для ввода значений тейк-профита
                if (_parameters.TakeProfitCalculationType == PriceCalculationType.Percentage)
                {
                    var tpPercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    tpPercentPanel.Children.Add(new TextBlock { Text = "Тейк-профит (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                    var tpPercentTextBox = new TextBox
                    {
                        Text = _parameters.TakeProfitPercent.ToString(),
                        Width = 80
                    };
                    tpPercentTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    tpPercentPanel.Children.Add(tpPercentTextBox);
                    exitParamsPanel.Children.Add(tpPercentPanel);
                }
                else if (_parameters.TakeProfitCalculationType == PriceCalculationType.Absolute)
                {
                    var tpAbsolutePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    tpAbsolutePanel.Children.Add(new TextBlock { Text = "Тейк-профит (абс.):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                    var tpAbsoluteTextBox = new TextBox
                    {
                        Text = _parameters.TakeProfitAbsolute.ToString(),
                        Width = 80
                    };
                    tpAbsoluteTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitAbsolute))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    tpAbsolutePanel.Children.Add(tpAbsoluteTextBox);
                    exitParamsPanel.Children.Add(tpAbsolutePanel);
                }
                else if (_parameters.TakeProfitCalculationType == PriceCalculationType.ATR)
                {
                    var tpAtrPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    tpAtrPanel.Children.Add(new TextBlock { Text = "Множитель ATR:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                    var tpAtrTextBox = new TextBox
                    {
                        Text = _parameters.AtrMultiplier.ToString(),
                        Width = 80
                    };
                    tpAtrTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.AtrMultiplier))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    tpAtrPanel.Children.Add(tpAtrTextBox);
                    exitParamsPanel.Children.Add(tpAtrPanel);
                }

                // Цена активации тейк-профита
                var tpActivationPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                tpActivationPanel.Children.Add(new TextBlock { Text = "Цена активации:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var tpActivationTextBox = new TextBox
                {
                    Text = _parameters.TakeProfitActivationPrice.ToString(),
                    Width = 80
                };
                tpActivationTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitActivationPrice))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                tpActivationPanel.Children.Add(tpActivationTextBox);
                exitParamsPanel.Children.Add(tpActivationPanel);

                // Проскальзывание тейк-профита
                var tpSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                tpSlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var tpSlippageTextBox = new TextBox
                {
                    Text = _parameters.TakeProfitSlippage.ToString(),
                    Width = 80
                };
                tpSlippageTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.TakeProfitSlippage))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                tpSlippagePanel.Children.Add(tpSlippageTextBox);
                exitParamsPanel.Children.Add(tpSlippagePanel);

                // Комбобокс для выбора типа расчета стоп-лосса
                var slCalcTypePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                slCalcTypePanel.Children.Add(new TextBlock { Text = "Расчет стоп-лосса:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var slCalcTypeCombo = new ComboBox
                {
                    ItemsSource = Enum.GetValues(typeof(PriceCalculationType)),
                    SelectedItem = _parameters.StopLossCalculationType,
                    Width = 100
                };
                slCalcTypeCombo.SetBinding(ComboBox.SelectedItemProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossCalculationType))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay });
                slCalcTypePanel.Children.Add(slCalcTypeCombo);
                exitParamsPanel.Children.Add(slCalcTypePanel);

                // Поля для ввода значений стоп-лосса
                if (_parameters.StopLossCalculationType == PriceCalculationType.Percentage)
                {
                    var slPercentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    slPercentPanel.Children.Add(new TextBlock { Text = "Стоп-лосс (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                    var slPercentTextBox = new TextBox
                    {
                        Text = _parameters.StopLossPercent.ToString(),
                        Width = 80
                    };
                    slPercentTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossPercent))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    slPercentPanel.Children.Add(slPercentTextBox);
                    exitParamsPanel.Children.Add(slPercentPanel);
                }
                else if (_parameters.StopLossCalculationType == PriceCalculationType.Absolute)
                {
                    var slAbsolutePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    slAbsolutePanel.Children.Add(new TextBlock { Text = "Стоп-лосс (абс.):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                    var slAbsoluteTextBox = new TextBox
                    {
                        Text = _parameters.StopLossAbsolute.ToString(),
                        Width = 80
                    };
                    slAbsoluteTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossAbsolute))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    slAbsolutePanel.Children.Add(slAbsoluteTextBox);
                    exitParamsPanel.Children.Add(slAbsolutePanel);
                }
                else if (_parameters.StopLossCalculationType == PriceCalculationType.ATR)
                {
                    var slAtrPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    slAtrPanel.Children.Add(new TextBlock { Text = "Множитель ATR:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                    var slAtrTextBox = new TextBox
                    {
                        Text = _parameters.AtrMultiplier.ToString(),
                        Width = 80
                    };
                    slAtrTextBox.SetBinding(TextBox.TextProperty,
                        new System.Windows.Data.Binding(nameof(RsiStrategyParameters.AtrMultiplier))
                        { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                    slAtrPanel.Children.Add(slAtrTextBox);
                    exitParamsPanel.Children.Add(slAtrPanel);
                }

                // Цена активации стоп-лосса
                var slActivationPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                slActivationPanel.Children.Add(new TextBlock { Text = "Цена активации:", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var slActivationTextBox = new TextBox
                {
                    Text = _parameters.StopLossActivationPrice.ToString(),
                    Width = 80
                };
                slActivationTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossActivationPrice))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                slActivationPanel.Children.Add(slActivationTextBox);
                exitParamsPanel.Children.Add(slActivationPanel);

                // Проскальзывание стоп-лосса
                var slSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                slSlippagePanel.Children.Add(new TextBlock { Text = "Проскальзывание (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var slSlippageTextBox = new TextBox
                {
                    Text = _parameters.StopLossSlippage.ToString(),
                    Width = 80
                };
                slSlippageTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.StopLossSlippage))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                slSlippagePanel.Children.Add(slSlippageTextBox);
                exitParamsPanel.Children.Add(slSlippagePanel);

                exitParamsGroup.Content = exitParamsPanel;
                dynamicContainer.Children.Add(exitParamsGroup);

                // Общее проскальзывание для выхода
                var exitSlippagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                exitSlippagePanel.Children.Add(new TextBlock { Text = "Общее проскальзывание выхода (%):", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var exitSlippageTextBox = new TextBox
                {
                    Text = _parameters.ExitSlippage.ToString(),
                    Width = 80
                };
                exitSlippageTextBox.SetBinding(TextBox.TextProperty,
                    new System.Windows.Data.Binding(nameof(RsiStrategyParameters.ExitSlippage))
                    { Source = _parameters, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                exitSlippagePanel.Children.Add(exitSlippageTextBox);
                dynamicContainer.Children.Add(exitSlippagePanel);
            }
        }
        #endregion

        #region конвертер для отображения прибыли/убытка
        // конвертер для отображения прибыли/убытка
        public class MultiValueConverterForProfitLoss : System.Windows.Data.IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (values.Length >= 2 && values[0] is decimal profit && values[1] is decimal loss)
                {
                    return $"Потенциальная прибыль: {profit:F2}%, убыток: {loss:F2}%";
                }
                return "Потенциальная прибыль/убыток: нет данных";
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        #endregion
    }
}

#region Supporting Classes
public class RsiIndicatorValues : ObservableObject
{
    // Основные индикаторы
    private decimal _rsiValue;
    public decimal RsiValue
    {
        get => _rsiValue;
        set => SetProperty(ref _rsiValue, value);
    }

    private decimal _oscillatorValue;
    public decimal OscillatorValue
    {
        get => _oscillatorValue;
        set => SetProperty(ref _oscillatorValue, value);
    }

    private decimal _oscillatorSignal;
    public decimal OscillatorSignal
    {
        get => _oscillatorSignal;
        set => SetProperty(ref _oscillatorSignal, value);
    }

    private string _oscillatorName = "StochRSI";
    public string OscillatorName
    {
        get => _oscillatorName;
        set => SetProperty(ref _oscillatorName, value);
    }

    // Все индикаторы
    private decimal _stochasticK;
    public decimal StochasticK
    {
        get => _stochasticK;
        set => SetProperty(ref _stochasticK, value);
    }

    private decimal _stochasticD;
    public decimal StochasticD
    {
        get => _stochasticD;
        set => SetProperty(ref _stochasticD, value);
    }

    private decimal _stochRSIK;
    public decimal StochRSIK
    {
        get => _stochRSIK;
        set => SetProperty(ref _stochRSIK, value);
    }

    private decimal _macdValue;
    public decimal MacdValue
    {
        get => _macdValue;
        set => SetProperty(ref _macdValue, value);
    }

    private decimal _macdSignal;
    public decimal MacdSignal
    {
        get => _macdSignal;
        set => SetProperty(ref _macdSignal, value);
    }

    private decimal _macdHistogram;
    public decimal MacdHistogram
    {
        get => _macdHistogram;
        set => SetProperty(ref _macdHistogram, value);
    }

    private decimal _bollingerUpper;
    public decimal BollingerUpper
    {
        get => _bollingerUpper;
        set => SetProperty(ref _bollingerUpper, value);
    }

    private decimal _bollingerMiddle;
    public decimal BollingerMiddle
    {
        get => _bollingerMiddle;
        set => SetProperty(ref _bollingerMiddle, value);
    }

    private decimal _bollingerLower;
    public decimal BollingerLower
    {
        get => _bollingerLower;
        set => SetProperty(ref _bollingerLower, value);
    }

    private decimal _atrValue;
    public decimal AtrValue
    {
        get => _atrValue;
        set => SetProperty(ref _atrValue, value);
    }

    private decimal _lastPrice;
    public decimal LastPrice
    {
        get => _lastPrice;
        set => SetProperty(ref _lastPrice, value);
    }

    // Цены ордеров
    private decimal _entryPrice;
    public decimal EntryPrice
    {
        get => _entryPrice;
        set => SetProperty(ref _entryPrice, value);
    }

    private decimal _takeProfitPrice;
    public decimal TakeProfitPrice
    {
        get => _takeProfitPrice;
        set => SetProperty(ref _takeProfitPrice, value);
    }

    private decimal _stopLossPrice;
    public decimal StopLossPrice
    {
        get => _stopLossPrice;
        set => SetProperty(ref _stopLossPrice, value);
    }

    // Скользящий тейк-профит на ВХОДЕ
    private decimal _movingTPEntryPrice;
    public decimal MovingTPEntryPrice
    {
        get => _movingTPEntryPrice;
        set => SetProperty(ref _movingTPEntryPrice, value);
    }

    // Скользящий тейк-профит на ВЫХОДЕ
    private decimal _movingTPExitPrice;
    public decimal MovingTPExitPrice
    {
        get => _movingTPExitPrice;
        set => SetProperty(ref _movingTPExitPrice, value);
    }

    // Трейлинг-стоп на выходе
    private decimal _trailingStopExitPrice;
    public decimal TrailingStopExitPrice
    {
        get => _trailingStopExitPrice;
        set => SetProperty(ref _trailingStopExitPrice, value);
    }

    private decimal _potentialProfit;
    public decimal PotentialProfit
    {
        get => _potentialProfit;
        set => SetProperty(ref _potentialProfit, value);
    }

    private decimal _potentialLoss;
    public decimal PotentialLoss
    {
        get => _potentialLoss;
        set => SetProperty(ref _potentialLoss, value);
    }

    // Для отслеживания пересечений уровней
    private bool _previousOscillatorAboveOverbought;
    public bool PreviousOscillatorAboveOverbought
    {
        get => _previousOscillatorAboveOverbought;
        set => SetProperty(ref _previousOscillatorAboveOverbought, value);
    }

    private bool _previousOscillatorBelowOversold;
    public bool PreviousOscillatorBelowOversold
    {
        get => _previousOscillatorBelowOversold;
        set => SetProperty(ref _previousOscillatorBelowOversold, value);
    }

    private decimal _previousOscillatorValue;
    public decimal PreviousOscillatorValue
    {
        get => _previousOscillatorValue;
        set => SetProperty(ref _previousOscillatorValue, value);
    }



    // Состояние
    private string _status = "Ожидание данных";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private Brush _statusColor = Brushes.Gray;
    public Brush StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    private string _rsiStatus = "Нет данных";
    public string RsiStatus
    {
        get => _rsiStatus;
        set => SetProperty(ref _rsiStatus, value);
    }

    private Brush _rsiColor = Brushes.Gray;
    public Brush RsiColor
    {
        get => _rsiColor;
        set => SetProperty(ref _rsiColor, value);
    }

    private string _oscillatorStatus = "Нет данных";
    public string OscillatorStatus
    {
        get => _oscillatorStatus;
        set => SetProperty(ref _oscillatorStatus, value);
    }

    private Brush _oscillatorColor = Brushes.Gray;
    public Brush OscillatorColor
    {
        get => _oscillatorColor;
        set => SetProperty(ref _oscillatorColor, value);
    }

    // Статусы входа и выхода
    private string _entryStatus = "Ожидание сигнала";
    public string EntryStatus
    {
        get => _entryStatus;
        set => SetProperty(ref _entryStatus, value);
    }

    private string _entryStatusDetails = "";
    public string EntryStatusDetails
    {
        get => _entryStatusDetails;
        set => SetProperty(ref _entryStatusDetails, value);
    }

    private string _exitStatus = "Нет позиции";
    public string ExitStatus
    {
        get => _exitStatus;
        set => SetProperty(ref _exitStatus, value);
    }

    private string _exitStatusDetails = "";
    public string ExitStatusDetails
    {
        get => _exitStatusDetails;
        set => SetProperty(ref _exitStatusDetails, value);
    }

    // Торговые сигналы
    private string _signal = "ОЖИДАНИЕ";
    public string Signal
    {
        get => _signal;
        set => SetProperty(ref _signal, value);
    }

    private Brush _signalColor = Brushes.Gray;
    public Brush SignalColor
    {
        get => _signalColor;
        set => SetProperty(ref _signalColor, value);
    }

    private string _signalDescription = "";
    public string SignalDescription
    {
        get => _signalDescription;
        set => SetProperty(ref _signalDescription, value);
    }

    // Позиция
    private string _currentPosition = "Нет позиции";
    public string CurrentPosition
    {
        get => _currentPosition;
        set => SetProperty(ref _currentPosition, value);
    }

    private decimal _currentPnL;
    public decimal CurrentPnL
    {
        get => _currentPnL;
        set => SetProperty(ref _currentPnL, value);
    }

    // Действия
    private string _lastAction = "";
    public string LastAction
    {
        get => _lastAction;
        set => SetProperty(ref _lastAction, value);
    }

    private DateTime? _lastActionTime;
    public DateTime? LastActionTime
    {
        get => _lastActionTime;
        set => SetProperty(ref _lastActionTime, value);
    }

    private string _orderStatus = "";
    public string OrderStatus
    {
        get => _orderStatus;
        set => SetProperty(ref _orderStatus, value);
    }

    // Стратегия
    private string _strategyStatus = "ОСТАНОВЛЕНА";
    public string StrategyStatus
    {
        get => _strategyStatus;
        set => SetProperty(ref _strategyStatus, value);
    }

    private Brush _strategyStatusColor = Brushes.Red;
    public Brush StrategyStatusColor
    {
        get => _strategyStatusColor;
        set => SetProperty(ref _strategyStatusColor, value);
    }

    private DateTime _lastUpdate = DateTime.Now;
    public DateTime LastUpdate
    {
        get => _lastUpdate;
        set => SetProperty(ref _lastUpdate, value);
    }
}

public class RsiStrategyParameters : ObservableObject
{
    // Параметры индикаторов
    private OscillatorType _oscillatorType = OscillatorType.Stochastic;
    public OscillatorType OscillatorType
    {
        get => _oscillatorType;
        set => SetProperty(ref _oscillatorType, value);
    }

    private int _rsiPeriod = 14;
    public int RsiPeriod
    {
        get => _rsiPeriod;
        set => SetProperty(ref _rsiPeriod, value);
    }

    private decimal _rsiOverbought = 70;
    public decimal RsiOverbought
    {
        get => _rsiOverbought;
        set => SetProperty(ref _rsiOverbought, value);
    }

    private decimal _rsiOversold = 30;
    public decimal RsiOversold
    {
        get => _rsiOversold;
        set => SetProperty(ref _rsiOversold, value);
    }

    private int _stochPeriod = 14;
    public int StochPeriod
    {
        get => _stochPeriod;
        set => SetProperty(ref _stochPeriod, value);
    }

    private decimal _stochOverbought = 80;
    public decimal StochOverbought
    {
        get => _stochOverbought;
        set => SetProperty(ref _stochOverbought, value);
    }

    private decimal _stochOversold = 20;
    public decimal StochOversold
    {
        get => _stochOversold;
        set => SetProperty(ref _stochOversold, value);
    }

    private int _stochSmoothK = 3;
    public int StochSmoothK
    {
        get => _stochSmoothK;
        set => SetProperty(ref _stochSmoothK, value);
    }

    private int _stochSmoothD = 3;
    public int StochSmoothD
    {
        get => _stochSmoothD;
        set => SetProperty(ref _stochSmoothD, value);
    }

    // Параметры входа
    private MoneyGenerator_v5.Strategies.OrderType _entryOrderType = MoneyGenerator_v5.Strategies.OrderType.LevelCrossingEntry;
    public MoneyGenerator_v5.Strategies.OrderType EntryOrderType
    {
        get => _entryOrderType;
        set => SetProperty(ref _entryOrderType, value);
    }

    private decimal _entryLimitOffsetPercent = 0.1m;
    public decimal EntryLimitOffsetPercent
    {
        get => _entryLimitOffsetPercent;
        set => SetProperty(ref _entryLimitOffsetPercent, value);
    }

    private decimal _entryStopOffsetPercent = 0.2m;
    public decimal EntryStopOffsetPercent
    {
        get => _entryStopOffsetPercent;
        set => SetProperty(ref _entryStopOffsetPercent, value);
    }

    private decimal _entrySlippage = 0.01m;
    public decimal EntrySlippage
    {
        get => _entrySlippage;
        set => SetProperty(ref _entrySlippage, value);
    }

    // Параметры скользящего тейк-профита на ВХОДЕ
    private PriceCalculationType _movingTPEntryCalculationType = PriceCalculationType.ATR;
    public PriceCalculationType MovingTPEntryCalculationType
    {
        get => _movingTPEntryCalculationType;
        set => SetProperty(ref _movingTPEntryCalculationType, value);
    }

    private decimal _movingTPEntryTargetPercent = 2.5m;
    public decimal MovingTPEntryTargetPercent
    {
        get => _movingTPEntryTargetPercent;
        set => SetProperty(ref _movingTPEntryTargetPercent, value);
    }

    private decimal _movingTPEntryTargetAbsolute = 10.0m;
    public decimal MovingTPEntryTargetAbsolute
    {
        get => _movingTPEntryTargetAbsolute;
        set => SetProperty(ref _movingTPEntryTargetAbsolute, value);
    }

    private decimal _movingTPEntrySlippage = 0.01m;
    public decimal MovingTPEntrySlippage
    {
        get => _movingTPEntrySlippage;
        set => SetProperty(ref _movingTPEntrySlippage, value);
    }

    private int _movingTPEntryTimeoutMinutes = 6000;
    public int MovingTPEntryTimeoutMinutes
    {
        get => _movingTPEntryTimeoutMinutes;
        set => SetProperty(ref _movingTPEntryTimeoutMinutes, value);
    }


    // Параметры для ВХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
    private decimal _levelCrossingEntryProtectiveStopPercent = 0.25m;
    public decimal LevelCrossingEntryProtectiveStopPercent
    {
        get => _levelCrossingEntryProtectiveStopPercent;
        set => SetProperty(ref _levelCrossingEntryProtectiveStopPercent, value);
    }

    private decimal _levelCrossingEntryProtectiveStopDistancePercent = 0.25m;
    public decimal LevelCrossingEntryProtectiveStopDistancePercent
    {
        get => _levelCrossingEntryProtectiveStopDistancePercent;
        set => SetProperty(ref _levelCrossingEntryProtectiveStopDistancePercent, value);
    }

    // Параметры для ВЫХОДА ПО ПЕРЕСЕЧЕНИЮ УРОВНЯ
    private decimal _levelCrossingExitProtectiveStopPercent = 0.25m;
    public decimal LevelCrossingExitProtectiveStopPercent
    {
        get => _levelCrossingExitProtectiveStopPercent;
        set => SetProperty(ref _levelCrossingExitProtectiveStopPercent, value);
    }

    private decimal _levelCrossingExitProtectiveStopDistancePercent = 0.25m;
    public decimal LevelCrossingExitProtectiveStopDistancePercent
    {
        get => _levelCrossingExitProtectiveStopDistancePercent;
        set => SetProperty(ref _levelCrossingExitProtectiveStopDistancePercent, value);
    }






    // Параметры скользящего тейк-профита на ВЫХОДЕ
    private PriceCalculationType _movingTPExitCalculationType = PriceCalculationType.ATR;
    public PriceCalculationType MovingTPExitCalculationType
    {
        get => _movingTPExitCalculationType;
        set => SetProperty(ref _movingTPExitCalculationType, value);
    }

    private decimal _movingTPExitStartPercent = 2.0m;
    public decimal MovingTPExitStartPercent
    {
        get => _movingTPExitStartPercent;
        set => SetProperty(ref _movingTPExitStartPercent, value);
    }

    private decimal _movingTPExitStartAbsolute = 10.0m;
    public decimal MovingTPExitStartAbsolute
    {
        get => _movingTPExitStartAbsolute;
        set => SetProperty(ref _movingTPExitStartAbsolute, value);
    }

    private decimal _movingTPExitSlippage = 0.01m;
    public decimal MovingTPExitSlippage
    {
        get => _movingTPExitSlippage;
        set => SetProperty(ref _movingTPExitSlippage, value);
    }

    private int _movingTPExitTimeoutMinutes = 6000;
    public int MovingTPExitTimeoutMinutes
    {
        get => _movingTPExitTimeoutMinutes;
        set => SetProperty(ref _movingTPExitTimeoutMinutes, value);
    }

    // Параметры трейлинг-стопа на выходе
    private MoneyGenerator_v5.Strategies.OrderType _exitOrderType = MoneyGenerator_v5.Strategies.OrderType.LevelCrossingExit;
    public MoneyGenerator_v5.Strategies.OrderType ExitOrderType
    {
        get => _exitOrderType;
        set => SetProperty(ref _exitOrderType, value);
    }

    private PriceCalculationType _trailingStopExitCalculationType = PriceCalculationType.ATR;
    public PriceCalculationType TrailingStopExitCalculationType
    {
        get => _trailingStopExitCalculationType;
        set => SetProperty(ref _trailingStopExitCalculationType, value);
    }

    private decimal _trailingStopExitDistancePercent = 0.5m;
    public decimal TrailingStopExitDistancePercent
    {
        get => _trailingStopExitDistancePercent;
        set => SetProperty(ref _trailingStopExitDistancePercent, value);
    }

    private decimal _trailingStopExitDistanceAbsolute = 2.0m;
    public decimal TrailingStopExitDistanceAbsolute
    {
        get => _trailingStopExitDistanceAbsolute;
        set => SetProperty(ref _trailingStopExitDistanceAbsolute, value);
    }

    private decimal _trailingStopExitSlippage = 0.01m;
    public decimal TrailingStopExitSlippage
    {
        get => _trailingStopExitSlippage;
        set => SetProperty(ref _trailingStopExitSlippage, value);
    }

    private decimal _trailingStopExitActivationPercent = 1m;
    public decimal TrailingStopExitActivationPercent
    {
        get => _trailingStopExitActivationPercent;
        set => SetProperty(ref _trailingStopExitActivationPercent, value);
    }

    // Параметры тейк-профита (для обычного выхода)
    private PriceCalculationType _takeProfitCalculationType = PriceCalculationType.ATR;
    public PriceCalculationType TakeProfitCalculationType
    {
        get => _takeProfitCalculationType;
        set => SetProperty(ref _takeProfitCalculationType, value);
    }

    private decimal _takeProfitPercent = 2.0m;
    public decimal TakeProfitPercent
    {
        get => _takeProfitPercent;
        set => SetProperty(ref _takeProfitPercent, value);
    }

    private decimal _takeProfitAbsolute = 10.0m;
    public decimal TakeProfitAbsolute
    {
        get => _takeProfitAbsolute;
        set => SetProperty(ref _takeProfitAbsolute, value);
    }

    private decimal _takeProfitActivationPrice = 0m;
    public decimal TakeProfitActivationPrice
    {
        get => _takeProfitActivationPrice;
        set => SetProperty(ref _takeProfitActivationPrice, value);
    }

    private decimal _takeProfitSlippage = 0.01m;
    public decimal TakeProfitSlippage
    {
        get => _takeProfitSlippage;
        set => SetProperty(ref _takeProfitSlippage, value);
    }

    // Параметры стоп-лосса (для обычного выхода)
    private PriceCalculationType _stopLossCalculationType = PriceCalculationType.ATR;
    public PriceCalculationType StopLossCalculationType
    {
        get => _stopLossCalculationType;
        set => SetProperty(ref _stopLossCalculationType, value);
    }

    private decimal _stopLossPercent = 1.0m;
    public decimal StopLossPercent
    {
        get => _stopLossPercent;
        set => SetProperty(ref _stopLossPercent, value);
    }

    private decimal _stopLossAbsolute = 5.0m;
    public decimal StopLossAbsolute
    {
        get => _stopLossAbsolute;
        set => SetProperty(ref _stopLossAbsolute, value);
    }

    private decimal _stopLossActivationPrice = 0m;
    public decimal StopLossActivationPrice
    {
        get => _stopLossActivationPrice;
        set => SetProperty(ref _stopLossActivationPrice, value);
    }

    private decimal _stopLossSlippage = 0.01m;
    public decimal StopLossSlippage
    {
        get => _stopLossSlippage;
        set => SetProperty(ref _stopLossSlippage, value);
    }

    // Общие параметры
    private decimal _atrMultiplier = 1.5m;
    public decimal AtrMultiplier
    {
        get => _atrMultiplier;
        set => SetProperty(ref _atrMultiplier, value);
    }

    private decimal _protectiveStopPercent = 0.5m;
    public decimal ProtectiveStopPercent
    {
        get => _protectiveStopPercent;
        set => SetProperty(ref _protectiveStopPercent, value);
    }

    private decimal _exitSlippage = 0.01m;
    public decimal ExitSlippage
    {
        get => _exitSlippage;
        set => SetProperty(ref _exitSlippage, value);
    }

    // Дополнительные параметры
    private bool _closeOnSignalReversal = false;
    public bool CloseOnSignalReversal
    {
        get => _closeOnSignalReversal;
        set => SetProperty(ref _closeOnSignalReversal, value);
    }

    // Размер позиции
    private decimal _orderSizePercent = 10m;
    public decimal OrderSizePercent
    {
        get => _orderSizePercent;
        set => SetProperty(ref _orderSizePercent, value);
    }

    private decimal _fixedOrderSize = 10;
    public decimal FixedOrderSize
    {
        get => _fixedOrderSize;
        set => SetProperty(ref _fixedOrderSize, value);
    }

    public MoneyGenerator_v5.Strategies.TakeProfitType TakeProfitType { get; internal set; }

    public event Action<RsiStrategyParameters> OnParametersChanged;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        OnParametersChanged?.Invoke(this);
    }

    public void ApplyParameters()
    {
        OnParametersChanged?.Invoke(this);
    }

    public void ResetParameters()
    {
        // Сброс к значениям по умолчанию
        OscillatorType = OscillatorType.Stochastic;
        RsiPeriod = 14;
        RsiOverbought = 70;
        RsiOversold = 30;
        StochPeriod = 14;
        StochOverbought = 80;
        StochOversold = 20;
        StochSmoothK = 3;
        StochSmoothD = 3;

        EntryOrderType = MoneyGenerator_v5.Strategies.OrderType.LevelCrossingEntry;
        EntryLimitOffsetPercent = 0.1m;
        EntryStopOffsetPercent = 0.2m;
        EntrySlippage = 0.01m;

        MovingTPEntryCalculationType = PriceCalculationType.ATR;
        MovingTPEntryTargetPercent = 2.0m;
        MovingTPEntrySlippage = 0.01m;
        MovingTPEntryTimeoutMinutes = 6000;

        MovingTPExitCalculationType = PriceCalculationType.ATR;
        MovingTPExitStartPercent = 2.0m;
        MovingTPExitSlippage = 0.01m;
        MovingTPExitTimeoutMinutes = 6000;

        ExitOrderType = MoneyGenerator_v5.Strategies.OrderType.LevelCrossingExit;
        TrailingStopExitCalculationType = PriceCalculationType.ATR;
        TrailingStopExitDistancePercent = 0.3m;
        TrailingStopExitSlippage = 0.01m;
        TrailingStopExitActivationPercent = 0.5m;
        ProtectiveStopPercent = 0.5m;

        TakeProfitCalculationType = PriceCalculationType.ATR;
        TakeProfitPercent = 2.0m;
        TakeProfitActivationPrice = 0m;
        TakeProfitSlippage = 0.01m;

        StopLossCalculationType = PriceCalculationType.ATR;
        StopLossPercent = 1.0m;
        StopLossActivationPrice = 0m;
        StopLossSlippage = 0.01m;

        AtrMultiplier = 2m;
        ExitSlippage = 0.01m;

        OrderSizePercent = 10.0m;
        CloseOnSignalReversal = false;

        OnParametersChanged?.Invoke(this);
    }
}
#endregion
