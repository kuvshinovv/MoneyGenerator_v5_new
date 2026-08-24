using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Common;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Strategies;
using MoneyGenerator_v5.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MoneyGenerator_v5.Strategies
{
    public partial class ManualStrategy : ObservableObject
    {
        public string Name => "Мануальная торговля";
        public string Type => "Manual";

        protected readonly ILogger _logger;
        protected IProvirerService _provider;
        protected readonly ConnectionManager _connectionManager;
        protected readonly TransactionsService _transactionsService;
        protected Models.Instrument _instrument;
        protected string _selectedAccountId;
        private DateTime _lastAtrUpdate = DateTime.MinValue;
        private readonly StrategyViewModel _strategyViewModel;
        private readonly MainViewModel _mainViewModel;
        Position _restoredPositionFromDB = new Position();

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

        public StrategyState State { get; set; } = StrategyState.Stopped;

        // Команды
        public IRelayCommand BuyMarketCommand { get; }
        public IRelayCommand SellMarketCommand { get; }
        public IRelayCommand PlaceLimitOrderCommand { get; }
        public IRelayCommand PlaceStopOrderCommand { get; }
        public IRelayCommand PlaceTakeProfitOrderCommand { get; }
        public IRelayCommand CancelAllOrdersCommand { get; }
        public IRelayCommand CalculateTakeProfitCommand { get; }
        public bool IsTrading { get; private set; }

        // Параметры
        [ObservableProperty] private int _quantity = 1;
        [ObservableProperty] private decimal _limitPrice;
        [ObservableProperty] private decimal _stopPrice;
        [ObservableProperty] private decimal _takeProfitPrice;
        [ObservableProperty] private string _orderDirection = "Buy";
        [ObservableProperty] private string _stopDirection = "Sell";
        [ObservableProperty] private string _takeProfitDirection = "Sell";
        [ObservableProperty] private decimal _offsetValue = 0.5m;
        [ObservableProperty] private string _offsetType = "%";
        [ObservableProperty] private decimal _slippageValue = 0.1m;
        [ObservableProperty] private string _slippageType = "%";
        [ObservableProperty] private decimal _currentPrice;
        [ObservableProperty] private decimal _atrValue;
        [ObservableProperty] private string _orderType = "Limit";

        // Логи
        [ObservableProperty] private ObservableCollection<string> _tradeLogs = new();
        [ObservableProperty] private ObservableCollection<Models.Order> _activeOrders = new();
        [ObservableProperty] private decimal _currentPosition = 0;

        public ManualStrategy(
            ILogger<ManualStrategy> logger,
            IProvirerService provider,
            ConnectionManager connectionManager,
            TransactionsService transactionsService,
            StrategyViewModel strategyViewModel = null,
            MainViewModel mainViewModel = null)
        {
            _logger = logger;
            _provider = provider;
            _connectionManager = connectionManager;
            _transactionsService = transactionsService;
            _strategyViewModel = strategyViewModel;
            _mainViewModel = mainViewModel;


            BuyMarketCommand = new RelayCommand(async () => await ExecuteBuyMarketAsync());
            SellMarketCommand = new RelayCommand(async () => await ExecuteSellMarketAsync());
/*            PlaceLimitOrderCommand = new RelayCommand(async () => await PlaceLimitOrderAsync());
            PlaceStopOrderCommand = new RelayCommand(async () => await PlaceStopOrderAsync());
            PlaceTakeProfitOrderCommand = new RelayCommand(async () => await PlaceTakeProfitOrderAsync());*/
            CancelAllOrdersCommand = new RelayCommand(async () => await CancelAllOrdersAsync());
            CalculateTakeProfitCommand = new RelayCommand(async () => await CalculateTakeProfitPriceAsync());



            // Инициализация начальных цен
            _ = InitializePricesAsync();
        }

        private async Task InitializePricesAsync()
        {
            try
            {
                if (_instrument != null)
                {
                    _currentPrice = await _provider.GetCurrentPriceAsync(_instrument.Uid);
                    _atrValue = await _provider.CalculateATRAsync(_instrument.Uid);
                    OnPropertyChanged(nameof(CurrentPrice));
                    OnPropertyChanged(nameof(AtrValue));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing prices");
            }
        }
        public async Task InitializeAsync(Models.Instrument instrument)
        {
            _instrument = instrument;

            // Получаем первый доступный счет
            var accounts = await _provider.GetAccountsAsync();
            if (accounts.Any())
            {
                _selectedAccountId = accounts.First().Id;
            }

            await UpdateCurrentPositionAsync();
            await UpdateActiveOrdersAsync();
            await InitializePricesAsync();
        }

        protected async Task ProcessStrategyLogicAsync()
        {
            await UpdateCurrentPositionAsync();
            await UpdateActiveOrdersAsync();

            // Обновляем ATR не чаще 3 раз в секунду
            if (_instrument != null && (DateTime.UtcNow - _lastAtrUpdate).TotalMilliseconds > 333)
            {
                _atrValue = await _provider.CalculateATRAsync(_instrument.Uid);
                _lastAtrUpdate = DateTime.UtcNow;
                OnPropertyChanged(nameof(AtrValue));
            }
        }

        private async Task UpdateCurrentPositionAsync()
        {
            Debug.WriteLine($"DEBUG --- UpdateCurrentPositionAsync   START ... ----------------------------------------");

            try
            {
                if (!string.IsNullOrEmpty(_selectedAccountId) && _instrument != null)
                {
                    // ✅ Используем GetPositionObjectAsync для получения более точной информации
                    //var position = await _provider.GetPositionObjectAsync(_selectedAccountId, _instrument.Uid);

                    var position = await _provider.GetPositionQuantity(_selectedAccountId, _instrument.Uid);


                    



                    if (position != null)
                    {
                        // Учитываем направление позиции
                        if (position.Quantity > 0)
                        {
                            CurrentPosition = position.Quantity;
                            Debug.WriteLine($"DEBUG --- ManualStrategy - UpdateCurrentPositionAsync: Long позиция {CurrentPosition} лотов {_instrument.Ticker}");
                        }
                        else if (position.Quantity < 0)
                        {
                            CurrentPosition = position.Quantity;
                            Debug.WriteLine($"DEBUG --- ManualStrategy - UpdateCurrentPositionAsync: Short позиция {CurrentPosition} лотов {_instrument.Ticker}");
                        }
                        else
                        {
                            CurrentPosition = 0;
                            Debug.WriteLine($"DEBUG --- ManualStrategy - UpdateCurrentPositionAsync: Нет позиции по {_instrument.Ticker}");
                        }
                    }
                    else
                    {
                        CurrentPosition = 0;
                        Debug.WriteLine($"DEBUG --- ManualStrategy - UpdateCurrentPositionAsync: position вернул 0   position={position} position.Quantity={position?.Quantity} CurrentPosition={CurrentPosition} лотов {_instrument.Ticker}");
                    }
                }
                else
                {
                    Debug.WriteLine($"DEBUG --- ManualStrategy - UpdateCurrentPositionAsync: НЕ СРАБОТАЛО УСЛОВИЕ:  ---===!string.IsNullOrEmpty(_selectedAccountId) && _instrument != null===---  _selectedAccountId={_selectedAccountId} _instrument={_instrument}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, " DEBUG --- ManualStrategy - UpdateCurrentPositionAsync: Error updating position");
            }

            Debug.WriteLine($"DEBUG --- UpdateCurrentPositionAsync END... CurrentPosition={CurrentPosition} ----------------------------------------");
        }
        private async Task UpdateActiveOrdersAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_selectedAccountId) && _instrument != null)
                {
                    var orders = await _transactionsService.GetActiveOrdersAsync(_selectedAccountId, _instrument.Uid);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ActiveOrders.Clear();
                        foreach (var order in orders)
                        {
                            ActiveOrders.Add(order);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating active orders");
            }
        }

        /// <summary>
        /// ИЗМЕНЕННЫЙ МЕТОД: Покупка по рынку через TransactionsService
        /// </summary>
        private async Task ExecuteBuyMarketAsync()
        {
            Debug.WriteLine($"------------- ПОКУПАЕМ {Quantity} лотов {_instrument?.Ticker}");

            try
            {
                if (Quantity <= 0)
                {
                    AddTradeLog("Укажите количество лотов");
                    return;
                }

                if (string.IsNullOrEmpty(_selectedAccountId))
                {
                    AddTradeLog("Не выбран торговый счет");
                    return;
                }

                // ✅ ИСПРАВЛЕНИЕ: Правильная логика определения входа/выхода
                // Buy для Long позиции:
                // - Если CurrentPosition >= 0 (нет позиции или Long позиция) и мы покупаем -> это вход (увеличение Long)
                // - Если CurrentPosition < 0 (Short позиция) и мы покупаем -> это выход (закрытие Short)

                bool isEntryOrder;
                bool isExitOrder;
                string exitReason = null;

                if (CurrentPosition == 0)
                {
                    // Нет позиции или Long позиция - покупка это вход
                    isEntryOrder = true;
                    isExitOrder = false;
                    AddTradeLog($"Покупка {Quantity} лотов - ВХОД в Long позицию");
                    Debug.WriteLine($"Покупка {Quantity} лотов - ВХОД в Long позицию");
                }
                else if (CurrentPosition < 0)
                {

                    // Short позиция - покупка это выход (закрытие Short)
                    isEntryOrder = false;
                    isExitOrder = true;
                    exitReason = "Ручной выход из Short позиции";
                    AddTradeLog($"Покупка {Quantity} лотов - ВЫХОД из Short позиции");
                    Debug.WriteLine($"Покупка {Quantity} лотов - ВЫХОД из Short позиции");
                }
                else 
                {
                    isEntryOrder = false;
                    isExitOrder = false;
                    Debug.WriteLine($"DEBUG --- ManualStrategy - ExecuteBuyMarketAsync - isEntryOrder={isEntryOrder}  isExitOrder={isExitOrder}  CurrentPosition={CurrentPosition}  ");
                }





                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: "Buy",
                    quantity: Quantity,
                    ticker: _instrument.Ticker,
                    isEntryOrder: isEntryOrder,
                    isExitOrder: isExitOrder,
                    exitReason: isExitOrder ? "Ручной выход" : null,
                    accountId: _selectedAccountId);

                if (result.IsSuccess)
                {
                    AddTradeLog($"Заявка исполнена успешно. OrderId: {result.OrderId}");
                    Debug.WriteLine($"Заявка исполнена успешно. OrderId: {result.OrderId}");

                    // Обработка входа/выхода в БД
                    if (isEntryOrder)
                    {
                        await HandleEntryOrderAsync(result.OrderId, "Buy", "Ручной вход");
                    }
                    else if (isExitOrder)
                    {
                        await HandleExitOrderAsync("Buy", result.Order?.Price ?? CurrentPrice, result.OrderId);
                    }

                    await UpdateCurrentPositionAsync();
                    await UpdateActiveOrdersAsync();
                }
                else
                {
                    AddTradeLog($"Ошибка: {result.ErrorMessage}");
                    _logger.LogError($"Ошибка покупки: {result.ErrorMessage}");
                }

                

            }
            catch (Exception ex)
            {
                AddTradeLog($"Ошибка покупки: {ex.Message}");
                _logger.LogError(ex, "Error executing buy market");
            }

            await UpdateCurrentPositionAsync();
            await UpdateActiveOrdersAsync();

        }

        /// <summary>
        /// ИЗМЕНЕННЫЙ МЕТОД: Продажа по рынку через TransactionsService
        /// </summary>
        private async Task ExecuteSellMarketAsync()
        {
            Debug.WriteLine($"------------- ПРОДАЕМ {Quantity} лотов {_instrument?.Ticker}, CurrentPosition={CurrentPosition}");

            try
            {
                if (Quantity <= 0)
                {
                    AddTradeLog("Укажите количество лотов");
                    return;
                }

                if (string.IsNullOrEmpty(_selectedAccountId))
                {
                    AddTradeLog("Не выбран торговый счет");
                    return;
                }

                // ✅ ИСПРАВЛЕНИЕ: Правильная логика определения входа/выхода
                // Sell для Short позиции:
                // - Если CurrentPosition <= 0 (нет позиции или Short позиция) и мы продаем -> это вход (открытие Short)
                // - Если CurrentPosition > 0 (Long позиция) и мы продаем -> это выход (закрытие Long)

                bool isEntryOrder;
                bool isExitOrder;
                string exitReason = null;

                if (CurrentPosition == 0)
                {
                    // Нет позиции или Short позиция - продажа это вход
                    isEntryOrder = true;
                    isExitOrder = false;
                    AddTradeLog($"Продажа {Quantity} лотов - ВХОД в Short позицию");
                    Debug.WriteLine($"Продажа {Quantity} лотов - ВХОД в Short позицию");
                }
                else if (CurrentPosition > 0)
                {
                    // Long позиция - продажа это выход (закрытие Long)
                    isEntryOrder = false;
                    isExitOrder = true;
                    exitReason = "Ручной выход из Long позиции";
                    AddTradeLog($"Продажа {Quantity} лотов - ВЫХОД из Long позиции");
                    Debug.WriteLine($"Продажа {Quantity} лотов - ВЫХОД из Long позиции");
                }
                else 
                {
                    isEntryOrder = false;
                    isExitOrder = false;
                    Debug.WriteLine($"DEBUG --- ManualStrategy - ExecuteSellMarketAsync - isEntryOrder={isEntryOrder}  isExitOrder={isExitOrder}  CurrentPosition={CurrentPosition}  ");

                }




                var result = await _transactionsService.SendMarketOrderAsync(
                    instrumentUid: _instrument.Uid,
                    direction: "Sell",
                    quantity: Quantity,
                    ticker: _instrument.Ticker,
                    isEntryOrder: isEntryOrder,
                    isExitOrder: isExitOrder,
                    exitReason: isExitOrder ? "Ручной выход" : null,
                    accountId: _selectedAccountId);

                if (result.IsSuccess)
                {
                    AddTradeLog($"Заявка исполнена успешно. OrderId: {result.OrderId}");

                    // Обработка входа/выхода в БД
                    if (isEntryOrder)
                    {
                        await HandleEntryOrderAsync(result.OrderId, "Sell", "Ручной вход");
                    }
                    else if (isExitOrder)
                    {
                        await HandleExitOrderAsync("Sell", result.Order?.Price ?? CurrentPrice, result.OrderId);
                    }

                    
                }
                else
                {
                    AddTradeLog($"Ошибка: {result.ErrorMessage}");
                    _logger.LogError($"Ошибка продажи: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                AddTradeLog($"Ошибка продажи: {ex.Message}");
                _logger.LogError(ex, "Error executing sell market");
            }

            await UpdateCurrentPositionAsync();
            await UpdateActiveOrdersAsync();

        }

        /*private async Task PlaceLimitOrderAsync()
        {
            try
            {
                if (LimitPrice <= 0)
                {
                    AddTradeLog("Укажите цену лимитной заявки");
                    return;
                }

                if (Quantity <= 0)
                {
                    AddTradeLog("Укажите количество лотов");
                    return;
                }

                if (string.IsNullOrEmpty(_selectedAccountId))
                {
                    AddTradeLog("Не выбран торговый счет");
                    return;
                }

                AddTradeLog($"Выставление лимитной заявки: {OrderDirection} {Quantity} лотов по {LimitPrice}");

                var order = new Models.Order
                {
                    AccountId = _selectedAccountId,
                    InstrumentUid = _instrument.Uid,
                    Quantity = Quantity,
                    Price = LimitPrice,
                    Direction = OrderDirection.ToLower(), // "buy" или "sell"
                    OrderType = "limit"
                };

                var result = await _provider.PlaceOrderAsync(order);

                if (result.IsSuccess)
                {
                    AddTradeLog($"Лимитная заявка выставлена успешно");
                    await UpdateActiveOrdersAsync();
                }
                else
                {
                    AddTradeLog($"Ошибка: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                AddTradeLog($"Ошибка выставления лимитной заявки: {ex.Message}");
                _logger.LogError(ex, "Error placing limit order");
            }
        }*/
        /*private async Task PlaceStopOrderAsync()
        {
            try
            {
                if (StopPrice <= 0)
                {
                    AddTradeLog("Укажите цену стоп-заявки");
                    return;
                }

                if (Quantity <= 0)
                {
                    AddTradeLog("Укажите количество лотов");
                    return;
                }

                if (string.IsNullOrEmpty(_selectedAccountId))
                {
                    AddTradeLog("Не выбран торговый счет");
                    return;
                }

                // Проверяем логику стоп-заявки
                if (StopDirection == "Buy" && StopPrice <= CurrentPrice)
                {
                    AddTradeLog($"Стоп-цена должна быть выше текущей цены");
                    return;
                }

                if (StopDirection == "Sell" && StopPrice >= CurrentPrice)
                {
                    AddTradeLog($"Стоп-цена должна быть ниже текущей цены");
                    return;
                }

                AddTradeLog($"Выставление стоп-заявки: {StopDirection} {Quantity} лотов по {StopPrice}");

                // В Tinkoff API используется stop-limit, отправляем как limit ордер
                var order = new Models.Order
                {
                    AccountId = _selectedAccountId,
                    InstrumentUid = _instrument.Uid,
                    Quantity = Quantity,
                    Price = StopPrice,
                    Direction = StopDirection.ToLower(), // "buy" или "sell"
                    OrderType = "stoplimit"
                };

                var result = await _provider.PlaceOrderAsync(order);

                if (result.IsSuccess)
                {
                    AddTradeLog($"Стоп-заявка выставлена успешно");
                    await UpdateActiveOrdersAsync();
                }
                else
                {
                    AddTradeLog($"Ошибка: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                AddTradeLog($"Ошибка выставления стоп-заявки: {ex.Message}");
                _logger.LogError(ex, "Error placing stop order");
            }
        }*/
        /* private async Task PlaceTakeProfitOrderAsync()
        {
            try
            {
                if (TakeProfitPrice <= 0)
                {
                    // Рассчитываем цену тейк-профита автоматически
                    await CalculateTakeProfitPriceAsync();
                }

                if (TakeProfitPrice <= 0)
                {
                    AddTradeLog("Не удалось рассчитать цену тейк-профита");
                    return;
                }

                if (Quantity <= 0)
                {
                    AddTradeLog("Укажите количество лотов");
                    return;
                }

                if (string.IsNullOrEmpty(_selectedAccountId))
                {
                    AddTradeLog("Не выбран торговый счет");
                    return;
                }

                // Проверяем логику тейк-профита
                if (TakeProfitDirection == "Buy" && TakeProfitPrice >= CurrentPrice)
                {
                    AddTradeLog($"Цена тейк-профита должна быть ниже текущей цены");
                    return;
                }

                if (TakeProfitDirection == "Sell" && TakeProfitPrice <= CurrentPrice)
                {
                    AddTradeLog($"Цена тейк-профита должна быть выше текущей цены");
                    return;
                }

                AddTradeLog($"Выставление тейк-профита: {TakeProfitDirection} {Quantity} лотов по {TakeProfitPrice}");

                // Тейк-профит реализуем как лимитную заявку
                var order = new Models.Order
                {
                    AccountId = _selectedAccountId,
                    InstrumentUid = _instrument.Uid,
                    Quantity = Quantity,
                    Price = TakeProfitPrice,
                    Direction = TakeProfitDirection.ToLower(), // "buy" или "sell"
                    OrderType = "limit"
                };

                var result = await _provider.PlaceOrderAsync(order);

                if (result.IsSuccess)
                {
                    AddTradeLog($"Тейк-профит выставлен успешно");
                    await UpdateActiveOrdersAsync();
                }
                else
                {
                    AddTradeLog($"Ошибка: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                AddTradeLog($"Ошибка выставления тейк-профита: {ex.Message}");
                _logger.LogError(ex, "Error placing take profit order");
            }
        }*/


        /// <summary>
        /// НОВЫЙ МЕТОД: Отмена всех ордеров через TransactionsService
        /// </summary>
        private async Task CancelAllOrdersAsync()
        {
            try
            {
                AddTradeLog($"Отмена всех активных заявок для {_instrument?.Ticker}...");

                var result = await _transactionsService.CancelAllOrdersAsync(_instrument?.Uid);

                if (result)
                {
                    AddTradeLog("Все заявки успешно отменены");
                    await UpdateActiveOrdersAsync();
                }
                else
                {
                    AddTradeLog("Не удалось отменить заявки или нет активных заявок");
                }
            }
            catch (Exception ex)
            {
                AddTradeLog($"Ошибка отмены заявок: {ex.Message}");
                _logger.LogError(ex, "Error cancelling all orders");
            }
        }
        private async Task CalculateTakeProfitPriceAsync()
        {
            try
            {
                if (CurrentPrice <= 0)
                {
                    CurrentPrice = await _provider.GetCurrentPriceAsync(_instrument.Uid);
                    if (CurrentPrice <= 0) return;
                }

                decimal offset = OffsetValue;

                // Конвертируем отступ в абсолютное значение
                switch (OffsetType)
                {
                    case "%":
                        offset = CurrentPrice * OffsetValue / 100;
                        break;
                    case "ATR":
                        if (AtrValue <= 0)
                            AtrValue = await _provider.CalculateATRAsync(_instrument.Uid);
                        offset = AtrValue * OffsetValue;
                        break;
                }

                // Рассчитываем проскальзывание
                decimal slippage = SlippageValue;
                switch (SlippageType)
                {
                    case "%":
                        slippage = CurrentPrice * SlippageValue / 100;
                        break;
                    case "ATR":
                        if (AtrValue <= 0)
                            AtrValue = await _provider.CalculateATRAsync(_instrument.Uid);
                        slippage = AtrValue * SlippageValue;
                        break;
                }

                if (TakeProfitDirection == "Sell")
                {
                    TakeProfitPrice = CurrentPrice + offset - slippage;
                }
                else if (TakeProfitDirection == "Buy")
                {
                    TakeProfitPrice = CurrentPrice - offset + slippage;
                }

                // Округляем до шага цены
                if (_instrument?.PriceStep > 0)
                {
                    TakeProfitPrice = Math.Round(TakeProfitPrice / _instrument.PriceStep) * _instrument.PriceStep;
                }

                OnPropertyChanged(nameof(TakeProfitPrice));
                AddTradeLog($"Рассчитана цена Тейк-профита: {TakeProfitPrice:F2}");
            }
            catch (Exception ex)
            {
                AddTradeLog($"Ошибка расчета тейк-профита: {ex.Message}");
                _logger.LogError(ex, "Error calculating take profit price");
            }
        }
        /// <summary>
        /// НОВЫЙ МЕТОД: Обработка входа в позицию (запись в БД)
        /// </summary>
        private async Task HandleEntryOrderAsync(string orderId, string direction, string reason)
        {
            try
            {
                var position = new Position()
                {
                    AccountId = _selectedAccountId,
                    EntryOrderId = orderId,
                    InstrumentUid = _instrument.Uid,
                    Ticker = _instrument.Ticker,
                    EntryDateTime = DateTime.Now,
                    EntryPrice = CurrentPrice,
                    EntryReason = reason,
                    Direction = direction,
                    Quantity = Quantity,
                    Strategy = Type,
                };

                CurrentPosition = position.Quantity;
                _logger.LogInformation($"Entry order placed successfully: {direction} {Quantity} lots");

                if (_strategyViewModel != null)
                {
                    await _transactionsService.AddOpenDealAsync(
                        _instrument.Ticker,
                        _instrument.Uid,
                        this.Type,
                        _strategyViewModel.CurrentTimeframe,
                        DateTime.Now,
                        CurrentPrice,
                        Quantity,
                        orderId,
                        direction,
                        reason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling entry order");
            }
        }
        /// <summary>
        /// НОВЫЙ МЕТОД: Обработка выхода из позиции (закрытие сделки в БД)
        /// </summary>
        private async Task HandleExitOrderAsync(string direction, decimal exitPrice, string exitOrderId)
        {
            try
            {
                // Восстанавливаем позицию из БД
                await RestorePositionFromDbAsync();

                if (_restoredPositionFromDB == null)
                {
                    _logger.LogWarning("No position found in DB for exit");
                    return;
                }

                // Рассчитываем P&L
                decimal pnl = 0;
                decimal pnlPercent = 0;
                decimal priceDiff = 0;

                // Определяем направление позиции
                bool isLongPosition = (_restoredPositionFromDB?.Direction == PositionDirection.Long ||
                                       _restoredPositionFromDB?.Direction == "Long" ||
                                       _restoredPositionFromDB?.Direction == "Buy");

                if (isLongPosition)
                {
                    priceDiff = exitPrice - _restoredPositionFromDB.EntryPrice;
                    pnl = priceDiff * _restoredPositionFromDB.Quantity * _instrument.LotSize;
                    pnlPercent = _restoredPositionFromDB.EntryPrice > 0
                        ? priceDiff / _restoredPositionFromDB.EntryPrice * 100
                        : 0;
                }
                else // Short position
                {
                    priceDiff = _restoredPositionFromDB.EntryPrice - exitPrice;
                    pnl = priceDiff * _restoredPositionFromDB.Quantity * _instrument.LotSize;
                    pnlPercent = _restoredPositionFromDB.EntryPrice > 0
                        ? priceDiff / _restoredPositionFromDB.EntryPrice * 100
                        : 0;
                }

                if (_strategyViewModel != null)
                {
                    await _transactionsService.CloseDealAsync(
                        _instrument.Uid,
                        _restoredPositionFromDB.EntryOrderId,
                        DateTime.Now,
                        exitPrice,
                        exitOrderId,
                        pnl,
                        pnlPercent,
                        "ЗАКРЫТА пользователем вручную");
                }

                _logger.LogInformation($"Exit order processed: P&L={pnl:F2} ({pnlPercent:F2}%)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling exit order");
            }
        }
        /// <summary>
        /// Восстановление позиции из БД (оставляем без изменений)
        /// </summary>
        private async Task RestorePositionFromDbAsync()
        {
            try
            {
                if (_strategyViewModel == null)
                {
                    _restoredPositionFromDB = null;
                    return;
                }

                var positionsFromDb = await _transactionsService.ReadDBOpenDealsAsync();
                var position = positionsFromDb.FirstOrDefault(p => p.Ticker == _instrument.Ticker);

                if (position != null)
                {
                    _restoredPositionFromDB = new Position()
                    {
                        AccountId = _selectedAccountId,
                        EntryOrderId = position.EntryOrderId,
                        InstrumentUid = _instrument.Uid,
                        Ticker = _instrument.Ticker,
                        EntryDateTime = position.EntryDateTime,
                        EntryPrice = position.EntryPrice,
                        EntryReason = position.EntryReason,
                        Direction = position.Direction,
                        Quantity = position.Quantity,
                        Strategy = Type,
                    };
                    _logger.LogInformation($"Restored position from DB: {position.Quantity} lots at {position.EntryPrice}");
                }
                else
                {
                    _restoredPositionFromDB = null;
                    _logger.LogInformation($"No position found in DB for restoration");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring position from DB");
            }
        }
        private void AddTradeLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                TradeLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
                if (TradeLogs.Count > 100)
                {
                    TradeLogs.RemoveAt(TradeLogs.Count - 1);
                }
            });
        }
        public async Task StartAsync()
        {
            State = StrategyState.Running;
            AddTradeLog("Стратегия запущена");
            await UpdateCurrentPositionAsync();
            await UpdateActiveOrdersAsync();
        }
        public async Task StopAsync()
        {
            State = StrategyState.Stopped;
            AddTradeLog("Стратегия остановлена");
        }

        public async Task ProcessMarketData(MarketData marketData)
        {
            // Debug.WriteLine($"----State={State}----_instrument={_instrument}----InstrumentUid={_instrument.Uid}------marketData.InstrumentUid={marketData.InstrumentUid}---!!!!!!!!!!!-");

            if (State != StrategyState.Running || _instrument == null)
                return;

            try
            {
                // Обновляем текущую цену
                CurrentPrice = marketData.LastPrice;
                IsTrading = marketData.TradingStatus == "NormalTrading";

                // Обновляем P/L
                if (_strategyViewModel != null)
                {
                    await _transactionsService.UpdateOpenDealsPnLAsync(_instrument.Uid, CurrentPrice);
                }

                // Обновляем ATR не чаще 3 раз в секунду
                if ((DateTime.UtcNow - _lastAtrUpdate).TotalMilliseconds > 333)
                {
                    _atrValue = await _provider.CalculateATRAsync(_instrument.Uid);
                    _lastAtrUpdate = DateTime.UtcNow;
                    OnPropertyChanged(nameof(AtrValue));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing market data");
            }
        }

        public async Task RestoreAsync()
        {
            await UpdateCurrentPositionAsync();
            await UpdateActiveOrdersAsync();
            AddTradeLog("Состояние восстановлено после реконнекта");
        }



        public object GetSettingsView()
        {
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Левая колонка - параметры
            var settingsPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10)
            };

            // Количество лотов
            var quantityLabel = new TextBlock { Text = "Количество лотов:", Margin = new Thickness(0, 0, 0, 5) };
            var quantityTextBox = new TextBox
            {
                Text = Quantity.ToString(),
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10)
            };
            quantityTextBox.TextChanged += (s, e) =>
            {
                if (int.TryParse(quantityTextBox.Text, out int qty) && qty > 0)
                    Quantity = qty;
            };

            // Текущая цена и ATR
            var priceLabel = new TextBlock
            {
                Text = $"Текущая цена: {CurrentPrice:F2}",
                Margin = new Thickness(0, 0, 0, 5)
            };
            var atrLabel = new TextBlock
            {
                Text = $"ATR(14): {AtrValue:F4}",
                Margin = new Thickness(0, 0, 0, 10)
            };

            settingsPanel.Children.Add(quantityLabel);
            settingsPanel.Children.Add(quantityTextBox);
            settingsPanel.Children.Add(priceLabel);
            settingsPanel.Children.Add(atrLabel);

            Grid.SetColumn(settingsPanel, 0);
            mainGrid.Children.Add(settingsPanel);

            // Правая колонка - управление
            var controlPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var buyButton = new Button
            {
                Content = "КУПИТЬ по рынку",
                Command = BuyMarketCommand,
                Background = Brushes.LightGreen,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                Height = 40,
                Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var sellButton = new Button
            {
                Content = "ПРОДАТЬ по рынку",
                Command = SellMarketCommand,
                Background = Brushes.LightCoral,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                Height = 40,
                Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var cancelButton = new Button
            {
                Content = "СНЯТЬ ВСЕ ЗАЯВКИ",
                Command = CancelAllOrdersCommand,
                Background = Brushes.LightYellow,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                Height = 40,
                Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var positionText = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var binding = new Binding("CurrentPosition")
            {
                Source = this,
                StringFormat = "Позиция: {0} лотов"
            };
            positionText.SetBinding(TextBlock.TextProperty, binding);

            controlPanel.Children.Add(buyButton);
            controlPanel.Children.Add(sellButton);
            controlPanel.Children.Add(cancelButton);
            controlPanel.Children.Add(positionText);

            Grid.SetColumn(controlPanel, 1);
            mainGrid.Children.Add(controlPanel);

            return mainGrid;
        }
        public object GetControlView()
        {
            var tabControl = new TabControl();

            // Вкладка 1: Рыночные ордера
            var marketTab = new TabItem { Header = "Рынок" };
            marketTab.Content = CreateMarketOrderPanel();
            tabControl.Items.Add(marketTab);

            // Вкладка 2: Лимитные ордера
            var limitTab = new TabItem { Header = "Лимит" };
            limitTab.Content = CreateLimitOrderPanel();
            tabControl.Items.Add(limitTab);

            // Вкладка 3: Стоп-ордера
            var stopTab = new TabItem { Header = "Стоп" };
            stopTab.Content = CreateStopOrderPanel();
            tabControl.Items.Add(stopTab);

            // Вкладка 4: Тейк-профит
            var takeProfitTab = new TabItem { Header = "Тейк-профит" };
            takeProfitTab.Content = CreateTakeProfitPanel();
            tabControl.Items.Add(takeProfitTab);

            return tabControl;
        }


        private Grid CreateMarketOrderPanel()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Количество
            var quantityPanel = CreateLabeledInput("Количество лотов:", "Quantity", 0, 0);
            grid.Children.Add(quantityPanel);

            // Кнопки
            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(buttonStack, 1);

            var buyButton = new Button
            {
                Content = "КУПИТЬ",
                Command = BuyMarketCommand,
                Background = Brushes.LightGreen,
                Margin = new Thickness(5),
                Width = 100
            };

            var sellButton = new Button
            {
                Content = "ПРОДАТЬ",
                Command = SellMarketCommand,
                Background = Brushes.LightCoral,
                Margin = new Thickness(5),
                Width = 100
            };

            buttonStack.Children.Add(buyButton);
            buttonStack.Children.Add(sellButton);
            grid.Children.Add(buttonStack);

            return grid;
        }
        private Grid CreateLimitOrderPanel()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Количество
            var quantityPanel = CreateLabeledInput("Количество лотов:", "Quantity", 0, 0);
            grid.Children.Add(quantityPanel);

            // Цена
            var pricePanel = CreateLabeledInput("Цена:", "LimitPrice", 1, 1);
            grid.Children.Add(pricePanel);

            // Направление
            var directionPanel = CreateComboBox("Направление:", "OrderDirection",
                new[] { "Buy", "Sell" }, 2, 2);
            grid.Children.Add(directionPanel);

            // Кнопка
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(buttonPanel, 3);

            var placeButton = new Button
            {
                Content = "ВЫСТАВИТЬ",
                Command = PlaceLimitOrderCommand,
                Background = Brushes.LightBlue,
                Margin = new Thickness(5),
                Width = 150
            };

            buttonPanel.Children.Add(placeButton);
            grid.Children.Add(buttonPanel);

            return grid;
        }
        private Grid CreateStopOrderPanel()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Количество
            var quantityPanel = CreateLabeledInput("Количество лотов:", "Quantity", 0, 0);
            grid.Children.Add(quantityPanel);

            // Стоп-цена
            var pricePanel = CreateLabeledInput("Стоп-цена:", "StopPrice", 1, 1);
            grid.Children.Add(pricePanel);

            // Направление
            var directionPanel = CreateComboBox("Направление:", "StopDirection",
                new[] { "Buy", "Sell" }, 2, 2);
            grid.Children.Add(directionPanel);

            // Кнопка
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(buttonPanel, 3);

            var placeButton = new Button
            {
                Content = "ВЫСТАВИТЬ СТОП",
                Command = PlaceStopOrderCommand,
                Background = Brushes.Orange,
                Margin = new Thickness(5),
                Width = 150
            };

            buttonPanel.Children.Add(placeButton);
            grid.Children.Add(buttonPanel);

            return grid;
        }
        private Grid CreateTakeProfitPanel()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < 8; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Количество
            var quantityPanel = CreateLabeledInput("Количество лотов:", "Quantity", 0, 0);
            grid.Children.Add(quantityPanel);

            // Направление
            var directionPanel = CreateComboBox("Направление:", "TakeProfitDirection",
                new[] { "Buy", "Sell" }, 1, 1);
            grid.Children.Add(directionPanel);

            // Цена тейк-профита
            var pricePanel = CreateLabeledInput("Цена тейк-профита:", "TakeProfitPrice", 2, 2);
            grid.Children.Add(pricePanel);

            // Отступ
            var offsetPanel = CreateLabeledInput("Отступ:", "OffsetValue", 3, 3);
            grid.Children.Add(offsetPanel);

            // Тип отступа
            var offsetTypePanel = CreateComboBox("Тип отступа:", "OffsetType",
                new[] { "%", "Absolute", "ATR" }, 4, 4);
            grid.Children.Add(offsetTypePanel);

            // Проскальзывание
            var slippagePanel = CreateLabeledInput("Проскальзывание:", "SlippageValue", 5, 5);
            grid.Children.Add(slippagePanel);

            // Тип проскальзывания
            var slippageTypePanel = CreateComboBox("Тип проскальзывания:", "SlippageType",
                new[] { "%", "Absolute", "ATR" }, 6, 6);
            grid.Children.Add(slippageTypePanel);

            // Кнопки
            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(buttonStack, 7);

            var calculateButton = new Button
            {
                Content = "РАССЧИТАТЬ",
                Command = new RelayCommand(async () => await CalculateTakeProfitPriceAsync()),
                Background = Brushes.LightGray,
                Margin = new Thickness(5),
                Width = 120
            };

            var placeButton = new Button
            {
                Content = "ВЫСТАВИТЬ ТП",
                Command = PlaceTakeProfitOrderCommand,
                Background = Brushes.LightGreen,
                Margin = new Thickness(5),
                Width = 120
            };

            buttonStack.Children.Add(calculateButton);
            buttonStack.Children.Add(placeButton);
            grid.Children.Add(buttonStack);

            return grid;
        }
        private Grid CreateLabeledInput(string label, string propertyName, int row, int column)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelControl = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            Grid.SetColumn(labelControl, 0);

            var textBox = new TextBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 100
            };

            var binding = new Binding(propertyName)
            {
                Source = this,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
            textBox.SetBinding(TextBox.TextProperty, binding);

            Grid.SetColumn(textBox, 1);
            grid.Children.Add(labelControl);
            grid.Children.Add(textBox);

            Grid.SetRow(grid, row);
            Grid.SetColumn(grid, column);

            return grid;
        }
        private Grid CreateComboBox(string label, string propertyName, string[] items, int row, int column)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelControl = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            Grid.SetColumn(labelControl, 0);

            var comboBox = new ComboBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 100
            };

            foreach (var item in items)
                comboBox.Items.Add(item);

            var binding = new Binding(propertyName)
            {
                Source = this,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
            comboBox.SetBinding(ComboBox.SelectedItemProperty, binding);

            Grid.SetColumn(comboBox, 1);
            grid.Children.Add(labelControl);
            grid.Children.Add(comboBox);

            Grid.SetRow(grid, row);
            Grid.SetColumn(grid, column);

            return grid;
        }








    }


}