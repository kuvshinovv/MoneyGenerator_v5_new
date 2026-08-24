using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.ViewModels;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MoneyGenerator_v5.Services
{
    /// <summary>
    /// Универсальный сервис для отправки и управления ордерами через любого провайдера
    /// </summary>
    public partial class TransactionsService
    {
        private readonly IProvirerService _provider;
        private readonly MainViewModel _mainViewModel;
        private readonly StrategyViewModel _strategyViewModel;
        private readonly Models.Instrument _instrument;
        private readonly ILogger/*<TransactionsService>*/ _logger;
        private readonly string _dbPath;
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private Account _selectedAccount;


        // Блокировки для предотвращения дублирования ордеров
        private readonly object _orderLock = new object();
        private string _currentOrderId = null;
        private DateTime _lastOrderTime = DateTime.MinValue;
        private const int ORDER_COOLDOWN_MS = 500; // 500 мс между ордерами





        public TransactionsService(
            IProvirerService provider,
            MainViewModel mainViewModel,
            StrategyViewModel strategyViewModel,
            Models.Instrument instrument,
            Account selectedAccount,
            ILogger logger)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _mainViewModel = mainViewModel;
            _strategyViewModel = strategyViewModel;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _selectedAccount = selectedAccount;
            _instrument = instrument;

            if (mainViewModel == null)
            {
                //Debug.WriteLine("WARNING: TransactionsService created with null MainViewModel");
            }


            // Инициализация БД
            _dbPath = "market_dataMG5.db";
            _connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");


            _connection.Open();



            /// DEBUG CHEK
            /// 
            /*Debug.WriteLine($"DBUG-----=====-----   " +
                $"_provider={_provider}\n " +
                $"_mainViewModel={_mainViewModel}\n " +
                $"_strategyViewModel={_strategyViewModel}\n " +
                $"_logger={_logger}\n  " +
                $"_selectedAccount.DisplayName={_selectedAccount.DisplayName}\n  " +
                $"_selectedAccount.Id={_selectedAccount.Id}\n");
*/

        }



        /// <summary>
        /// Проверяет возможность отправки ордера (блокировка от дублей)
        /// </summary>
        private bool CanSendOrder()
        {





            lock (_orderLock)
            {
                if (_currentOrderId != null)
                {
                    _logger.LogWarning("Предыдущий ордер {OrderId} еще не обработан", _currentOrderId);
                    return false;
                }

                if ((DateTime.Now - _lastOrderTime).TotalMilliseconds < ORDER_COOLDOWN_MS)
                {
                    _logger.LogWarning("Слишком частая отправка ордеров");
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Регистрирует отправленный ордер
        /// </summary>
        private void RegisterOrder(string orderId)
        {


            lock (_orderLock)
            {
                _currentOrderId = orderId;
                _lastOrderTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Освобождает блокировку ордера
        /// </summary>
        private void ReleaseOrder()
        {
            lock (_orderLock)
            {
                _currentOrderId = null;
            }
        }

        /// <summary>
        /// Получает инструмент по InstrumentUid
        /// </summary>
        private Models.Instrument GetInstrument(string instrumentUid)
        {
            return _mainViewModel?.Instruments?.FirstOrDefault(i => i.Uid == instrumentUid);
        }

        /// <summary>
        /// Получает минимальный шаг цены для инструмента
        /// </summary>
        private async Task<decimal> GetMinPriceIncrementAsync(string instrumentUid)
        {



            var instrument = GetInstrument(instrumentUid);
            if (instrument != null && instrument.MinStepPrice > 0)
            {
                return instrument.MinStepPrice;
            }

            // Значение по умолчанию для акций MOEX
            return 0.01m;
        }

        /// <summary>
        /// Округляет цену до минимального шага с учетом направления
        /// </summary>
        private decimal RoundPrice(decimal price, string direction, decimal minIncrement)
        {
            if (minIncrement <= 0) return price;

            if (direction == "Buy")
            {
                // Для покупки округляем вниз (лучшая цена)
                return Math.Floor(price / minIncrement) * minIncrement;
            }
            else // Sell
            {
                // Для продажи округляем вверх (лучшая цена)
                return Math.Ceiling(price / minIncrement) * minIncrement;
            }
        }

        #region 1. Рыночный ордер

        /// <summary>
        /// Отправляет рыночный ордер (исполняется по текущей рыночной цене)
        /// </summary>
        /// <param name="instrumentUid">UID инструмента</param>
        /// <param name="direction">Направление: Buy или Sell</param>
        /// <param name="quantity">Количество в лотах</param>
        /// <param name="accountId">ID счета (если null - используется первый доступный)</param>
        /// <param name="isEntryOrder">Является ли ордер входом в позицию</param>
        /// <param name="isExitOrder">Является ли ордер выходом из позиции</param>
        /// <param name="exitReason">Причина выхода (для exit ордеров)</param>
        /// <returns>Результат исполнения ордера</returns>
        public async Task<OrderResult> SendMarketOrderAsync(
            string instrumentUid,
            string direction,
            int quantity,
            string ticker,
            bool isEntryOrder ,
            bool isExitOrder,
            string exitReason,
            string accountId = null)
        {


            // ✅ Если провайдер null или MainViewModel в бэктест-режиме
            if (_provider == null || (_mainViewModel != null && _mainViewModel.IsBacktestMode))
            {
                Debug.WriteLine($"TransactionsService: БЭКТЕСТ-РЕЖИМ - пропускаем отправку ордера {ticker} {direction} {quantity}");
                return new OrderResult
                {
                    IsSuccess = true,
                    Order = new Order
                    {
                        Id = "BACKTEST_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        OrderId = "BACKTEST_" + Guid.NewGuid().ToString("N").Substring(0, 8)
                    }
                };
            }

            if (_provider == null)
            {
                return new OrderResult { IsSuccess = false, ErrorMessage = "Provider not available" };
            }


            if (!CanSendOrder())
            {
                return new OrderResult { IsSuccess = false, ErrorMessage = "Система занята обработкой предыдущего ордера" };
            }


            // ✅ ДОБАВИТЬ: Логирование для отладки
            Debug.WriteLine($"SendMarketOrderAsync CALLED: {direction} {quantity} {ticker}, IsEntry={isEntryOrder}, IsExit={isExitOrder}");

            try
            {
                // Получаем текущую цену для рыночного ордера
                decimal currentPrice = await _provider.GetCurrentPriceAsync(instrumentUid);
                if (currentPrice <= 0)
                {
                    return new OrderResult { IsSuccess = false, ErrorMessage = "Не удалось получить текущую цену" };
                }

                // Получаем минимальный шаг цены
                decimal minIncrement = await GetMinPriceIncrementAsync(instrumentUid);
                decimal roundedPrice = RoundPrice(currentPrice, direction, minIncrement);

                var order = new Order
                {
                    InstrumentUid = instrumentUid,
                    Direction = direction,
                    OrderType = "Market",
                    Quantity = quantity,
                    Price = roundedPrice,
                    Time = DateTime.Now,
                    Status = "Pending",
                    IsEntryOrder = isEntryOrder,
                    IsExitOrder = isExitOrder,
                    ExitReason = exitReason,
                    AccountId = accountId
                };

                RegisterOrder(order.OrderId ?? Guid.NewGuid().ToString());

                _logger.LogInformation($"Отправка рыночного ордера: {direction} {quantity} лотов по {roundedPrice:F2}");
                Debug.WriteLine($"Отправка рыночного ордера: {direction} {quantity} лотов по {roundedPrice:F2}");

                var result = await _provider.PlaceOrderAsync(order);



                if (result.IsSuccess)
                {

                    _logger.LogInformation($"Рыночный ордер отправлен успешно. OrderId: {result.OrderId}");
                    Debug.WriteLine($"Рыночный ордер отправлен успешно. OrderId: {result.OrderId}");


                    // Ожидаем исполнения ордера
                   OrderStatus status =  await WaitForOrderExecutionAsync(result.OrderId, order.Quantity, ticker, order.InstrumentUid, isEntryOrder, isExitOrder);

                    if (status == OrderStatus.Filled)
                    {
                        _logger.LogInformation($"Рыночный ордер {direction} {quantity} лотов исполнен  status={status}");
                        Debug.WriteLine($"Рыночный ордер {direction} {quantity} лотов исполнен  status={status}");

                        return new OrderResult
                        {
                            IsSuccess = true,
                            OrderId = result.OrderId,
                            Order = order,
                            Message = $"Рыночный ордер {direction} {quantity} лотов исполнен  status={status}"
                        };
                    }
                    else
                    {
                        _logger.LogInformation($"Рыночный ордер {direction} {quantity} лотов НЕ исполнен  status={status}");
                        Debug.WriteLine($"Рыночный ордер {direction} {quantity} лотов НЕ исполнен  status={status}");

                        return new OrderResult
                        {
                            IsSuccess = true,
                            OrderId = result.OrderId,
                            Order = order,
                            Message = $"Рыночный ордер {direction} {quantity} лотов НЕ исполнен  status={status}"
                        };
                    }
                }
                else
                {
                    _logger.LogError($"Ошибка отправки рыночного ордера: {result.ErrorMessage}");
                    Debug.WriteLine($"Ошибка отправки рыночного ордера: {result.ErrorMessage}");
                    return new OrderResult { IsSuccess = false, ErrorMessage = result.ErrorMessage };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Исключение при отправке рыночного ордера");
                Debug.WriteLine(ex, "Исключение при отправке рыночного ордера");
                return new OrderResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
            finally
            {
                ReleaseOrder();
            }
        }

        #endregion

        #region 2. Лимитный ордер

        /// <summary>
        /// Отправляет лимитный ордер (исполняется по указанной цене или лучше)
        /// </summary>
        /// <param name="instrumentUid">UID инструмента</param>
        /// <param name="direction">Направление: Buy или Sell</param>
        /// <param name="quantity">Количество в лотах</param>
        /// <param name="limitPrice">Лимитная цена</param>
        /// <param name="accountId">ID счета (если null - используется первый доступный)</param>
        /// <param name="isEntryOrder">Является ли ордер входом в позицию</param>
        /// <param name="isExitOrder">Является ли ордер выходом из позиции</param>
        /// <param name="exitReason">Причина выхода (для exit ордеров)</param>
        /// <returns>Результат размещения лимитного ордера</returns>
        public async Task<OrderResult> SendLimitOrderAsync(
            string instrumentUid,
            string direction,
            int quantity,
            decimal limitPrice,
            string accountId = null,
            bool isEntryOrder = false,
            bool isExitOrder = false,
            string exitReason = null)
        {
            if (!CanSendOrder())
            {
                return new OrderResult { IsSuccess = false, ErrorMessage = "Система занята обработкой предыдущего ордера" };
            }

            if (limitPrice <= 0)
            {
                return new OrderResult { IsSuccess = false, ErrorMessage = "Некорректная лимитная цена" };
            }

            try
            {
                // Получаем минимальный шаг цены и округляем
                decimal minIncrement = await GetMinPriceIncrementAsync(instrumentUid);
                decimal roundedPrice = RoundPrice(limitPrice, direction, minIncrement);

                var order = new Order
                {
                    InstrumentUid = instrumentUid,
                    Direction = direction,
                    OrderType = "Limit",
                    Quantity = quantity,
                    Price = roundedPrice,
                    Time = DateTime.Now,
                    Status = "Pending",
                    IsEntryOrder = isEntryOrder,
                    IsExitOrder = isExitOrder,
                    ExitReason = exitReason,
                    AccountId = accountId
                };

                RegisterOrder(order.OrderId ?? Guid.NewGuid().ToString());

                _logger.LogInformation($"Отправка лимитного ордера: {direction} {quantity} лотов по {roundedPrice:F2}");

                var result = await _provider.PlaceOrderAsync(order);

                if (result.IsSuccess)
                {
                    _logger.LogInformation($"Лимитный ордер отправлен успешно. OrderId: {result.OrderId}");
                    return new OrderResult
                    {
                        IsSuccess = true,
                        OrderId = result.OrderId,
                        Order = order,
                        Message = $"Лимитный ордер {direction} {quantity} лотов по {roundedPrice:F2} отправлен"
                    };
                }
                else
                {
                    _logger.LogError($"Ошибка отправки лимитного ордера: {result.ErrorMessage}");
                    return new OrderResult { IsSuccess = false, ErrorMessage = result.ErrorMessage };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Исключение при отправке лимитного ордера");
                return new OrderResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
            finally
            {
                ReleaseOrder();
            }
        }

        #endregion

        #region 3. Стоп-лосс ордер

        /// <summary>
        /// Отправляет стоп-лосс ордер (активируется при достижении указанной цены)
        /// </summary>
        /// <param name="instrumentUid">UID инструмента</param>
        /// <param name="direction">Направление: Buy или Sell</param>
        /// <param name="quantity">Количество в лотах</param>
        /// <param name="stopPrice">Цена активации стоп-лосса</param>
        /// <param name="accountId">ID счета (если null - используется первый доступный)</param>
        /// <param name="isEntryOrder">Является ли ордер входом в позицию</param>
        /// <param name="isExitOrder">Является ли ордер выходом из позиции</param>
        /// <param name="exitReason">Причина выхода (для exit ордеров)</param>
        /// <returns>Результат размещения стоп-лосс ордера</returns>
        public async Task<OrderResult> SendStopLossOrderAsync(
             string instrumentUid,
             string direction,
             int quantity,
             decimal stopPrice,
             string accountId = null,
             bool isEntryOrder = false,
             bool isExitOrder = false,
             string exitReason = null)
        {
            if (!CanSendOrder())
            {
                return new OrderResult { IsSuccess = false, ErrorMessage = "Система занята обработкой предыдущего ордера" };
            }

            if (stopPrice <= 0)
            {
                return new OrderResult { IsSuccess = false, ErrorMessage = "Некорректная цена стоп-лосса" };
            }

            try
            {
                // Получаем минимальный шаг цены и округляем
                decimal minIncrement = await GetMinPriceIncrementAsync(instrumentUid);
                decimal roundedPrice = RoundPrice(stopPrice, direction, minIncrement);

                var order = new Order
                {
                    InstrumentUid = instrumentUid,
                    Direction = direction,
                    OrderType = "StopLimit",
                    Quantity = quantity,
                    Price = roundedPrice,
                    Time = DateTime.Now,
                    Status = "Pending",
                    IsEntryOrder = isEntryOrder,
                    IsExitOrder = isExitOrder,
                    ExitReason = exitReason,
                    AccountId = accountId
                };

                RegisterOrder(order.OrderId ?? Guid.NewGuid().ToString());

                _logger.LogInformation($"Отправка стоп-лосс ордера: {direction} {quantity} лотов по {roundedPrice:F2}");

                var result = await _provider.PlaceOrderAsync(order);

                if (result.IsSuccess)
                {
                    _logger.LogInformation($"Стоп-лосс ордер отправлен успешно. OrderId: {result.OrderId}");
                    return new OrderResult
                    {
                        IsSuccess = true,
                        OrderId = result.OrderId,
                        Order = order,
                        Message = $"Стоп-лосс ордер {direction} {quantity} лотов по {roundedPrice:F2} отправлен"
                    };
                }
                else
                {
                    _logger.LogError($"Ошибка отправки стоп-лосс ордера: {result.ErrorMessage}");
                    return new OrderResult { IsSuccess = false, ErrorMessage = result.ErrorMessage };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Исключение при отправке стоп-лосс ордера");
                return new OrderResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
            finally
            {
                ReleaseOrder();
            }
        }

        #endregion

        #region 4. Комбинированный ордер (Тейк-профит + Стоп-лосс)

        /// <summary>
        /// Отправляет комбинированный ордер с тейк-профитом и стоп-лоссом
        /// </summary>
        /// <param name="instrumentUid">UID инструмента</param>
        /// <param name="direction">Направление основной позиции: Buy или Sell</param>
        /// <param name="quantity">Количество в лотах</param>
        /// <param name="entryPrice">Цена входа в позицию</param>
        /// <param name="takeProfitPercent">Тейк-профит в процентах от цены входа</param>
        /// <param name="stopLossPercent">Стоп-лосс в процентах от цены входа</param>
        /// <param name="accountId">ID счета (если null - используется первый доступный)</param>
        /// <returns>Результат размещения ордеров с рассчитанными ценами</returns>
        public async Task<TakeProfitStopLossResult> SendTakeProfitStopLossOrderAsync(
            string instrumentUid,
            string direction,
            int quantity,
            decimal entryPrice,
            decimal takeProfitPercent,
            decimal stopLossPercent,
            string accountId = null)
        {
            var result = new TakeProfitStopLossResult();

            if (entryPrice <= 0)
            {
                result.ErrorMessage = "Некорректная цена входа";
                return result;
            }

            try
            {
                decimal minIncrement = await GetMinPriceIncrementAsync(instrumentUid);

                // Расчет цен тейк-профита и стоп-лосса
                if (direction == "Buy" || direction == "Long")
                {
                    result.TakeProfitPrice = entryPrice * (1 + takeProfitPercent / 100);
                    result.StopLossPrice = entryPrice * (1 - stopLossPercent / 100);
                    result.TakeProfitDirection = "Sell";
                    result.StopLossDirection = "Sell";
                }
                else
                {
                    result.TakeProfitPrice = entryPrice * (1 - takeProfitPercent / 100);
                    result.StopLossPrice = entryPrice * (1 + stopLossPercent / 100);
                    result.TakeProfitDirection = "Buy";
                    result.StopLossDirection = "Buy";
                }

                // Округление цен
                result.TakeProfitPrice = RoundPrice(result.TakeProfitPrice, result.TakeProfitDirection, minIncrement);
                result.StopLossPrice = RoundPrice(result.StopLossPrice, result.StopLossDirection, minIncrement);

                // Отправка тейк-профит ордера
                var tpOrder = new Order
                {
                    InstrumentUid = instrumentUid,
                    Direction = result.TakeProfitDirection,
                    OrderType = "Limit",
                    Quantity = quantity,
                    Price = result.TakeProfitPrice,
                    Time = DateTime.Now,
                    Status = "Pending",
                    IsEntryOrder = false,
                    IsExitOrder = true,
                    ExitReason = "Тейк-профит",
                    AccountId = accountId
                };

                // Отправка стоп-лосс ордера
                var slOrder = new Order
                {
                    InstrumentUid = instrumentUid,
                    Direction = result.StopLossDirection,
                    OrderType = "StopLimit",
                    Quantity = quantity,
                    Price = result.StopLossPrice,
                    Time = DateTime.Now,
                    Status = "Pending",
                    IsEntryOrder = false,
                    IsExitOrder = true,
                    ExitReason = "Стоп-лосс",
                    AccountId = accountId
                };

                RegisterOrder(Guid.NewGuid().ToString());

                // Отправляем тейк-профит
                var tpResult = await _provider.PlaceOrderAsync(tpOrder);
                result.TakeProfitOrderId = tpResult.OrderId;
                result.TakeProfitSuccess = tpResult.IsSuccess;

                if (!tpResult.IsSuccess)
                {
                    result.ErrorMessage = $"Ошибка тейк-профита: {tpResult.ErrorMessage}";
                }


                // Небольшая задержка между ордерами
                await Task.Delay(200);

                // Отправляем стоп-лосс
                var slResult = await _provider.PlaceOrderAsync(slOrder);
                result.StopLossOrderId = slResult.OrderId;
                result.StopLossSuccess = slResult.IsSuccess;

                if (!slResult.IsSuccess)
                {
                    result.ErrorMessage += (string.IsNullOrEmpty(result.ErrorMessage) ? "" : "; ") +
                                           $"Ошибка стоп-лосса: {slResult.ErrorMessage}";
                }

                result.IsSuccess = result.TakeProfitSuccess || result.StopLossSuccess;

                _logger.LogInformation($"Тейк-профит/Стоп-лосс ордера отправлены. TP: {result.TakeProfitPrice:F2}, SL: {result.StopLossPrice:F2}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Исключение при отправке тейк-профит/стоп-лосс ордеров");
                result.ErrorMessage = ex.Message;
                return result;
            }
            finally
            {
                ReleaseOrder();
            }
        }

        #endregion

        #region 5. Вспомогательные методы для работы с активными заявками

        /// <summary>
        /// Получает список активных ордеров для указанного счета и инструмента
        /// </summary>
        /// <param name="accountId">ID счета</param>
        /// <param name="instrumentUid">UID инструмента (опционально)</param>
        /// <returns>Список активных ордеров</returns>
        public async Task<List<Order>> GetActiveOrdersAsync(string accountId, string instrumentUid = null)
        {
            try
            {
                if (string.IsNullOrEmpty(accountId))
                {
                    var accounts = await _provider.GetAccountsAsync();
                    if (!accounts.Any())
                    {
                        return new List<Order>();
                    }
                    accountId = accounts.First().Id;
                }

                return await _provider.GetActiveOrdersAsync(accountId, instrumentUid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения активных ордеров");
                return new List<Order>();
            }
        }

        /// <summary>
        /// Проверяет, есть ли активные ордера для указанного инструмента
        /// </summary>
        /// <param name="instrumentUid">UID инструмента</param>
        /// <returns>True если есть активные ордера</returns>
        public async Task<bool> HasActiveOrdersAsync(string instrumentUid)
        {
            try
            {
                var accounts = await _provider.GetAccountsAsync();
                if (!accounts.Any())
                {
                    return false;
                }

                foreach (var account in accounts)
                {
                    var orders = await GetActiveOrdersAsync(account.Id, instrumentUid);
                    if (orders.Any())
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка проверки активных ордеров");
                return false;
            }
        }

        /// <summary>
        /// Отменяет все активные ордера для указанного счета и инструмента
        /// </summary>
        /// <param name="instrumentUid">UID инструмента (опционально)</param>
        /// <returns>True если все ордера успешно отменены</returns>
        public async Task<bool> CancelAllOrdersAsync(string instrumentUid = null)
        {
            try
            {
                var accounts = await _provider.GetAccountsAsync();
                if (!accounts.Any())
                {
                    return false;
                }

                bool allCancelled = true;
                foreach (var account in accounts)
                {
                    var result = await _provider.CancelAllOrdersAsync(account.Id, instrumentUid);
                    if (!result)
                    {
                        allCancelled = false;
                    }
                }

                return allCancelled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка отмены всех ордеров");
                return false;
            }
        }

        /// <summary>
        /// Отменяет конкретный ордер по ID
        /// </summary>
        /// <param name="orderId">ID ордера</param>
        /// <param name="accountId">ID счета (опционально)</param>
        /// <returns>True если ордер успешно отменен</returns>
        public async Task<bool> CancelOrderAsync(string orderId, string accountId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(accountId))
                {
                    var accounts = await _provider.GetAccountsAsync();
                    foreach (var account in accounts)
                    {
                        var result = await _provider.CancelOrderAsync(orderId, account.Id);
                        if (result)
                        {
                            return true;
                        }
                    }
                    return false;
                }

                return await _provider.CancelOrderAsync(orderId, accountId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка отмены ордера {OrderId}", orderId);
                return false;
            }
        }

        /// <summary>
        /// Получает статус ордера
        /// </summary>
        /// <param name="orderId">ID ордера</param>
        /// <returns>Статус ордера</returns>
        public async Task<OrderStatus> GetOrderStatusAsync(string orderId)
        {
            try
            {
                return await _provider.GetOrderStatusAsync(orderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения статуса ордера {OrderId}", orderId);
                return OrderStatus.Unknown;
            }
        }

        /// <summary>
        /// Ожидает исполнения ордера с таймаутом
        /// </summary>
        /*public async Task<OrderStatus> WaitForOrderExecutionAsync(string orderId, int quantity, string ticker, string instrumentUId, int timeoutSeconds = 30)
        {
            var startTime = DateTime.Now;
            OrderStatus status = OrderStatus.Pending;
            int lastQuantity = 0;
            int noChangeCount = 0;

            while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
            {
                try
                {
                    status = await GetOrderStatusAsync(orderId);


                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - status {status} ");

                    if (status == OrderStatus.Filled)
                    {
                        // ✅ ИСПРАВЛЕНИЕ: Проверяем _mainViewModel на null
                        if (_selectedAccount != null && !string.IsNullOrEmpty(_selectedAccount.Id))
                        {
                            try
                            {
                                var positionInstrument = await _provider.GetPositionQuantity(
                                    _selectedAccount.Id,
                                    instrumentUId,
                                    ticker);

                                int currentQuantity = positionInstrument?.Quantity ?? 0;

                                if (currentQuantity >= quantity)
                                {
                                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - Заявка {ticker} ({quantity} лот) полностью исполнена! Текущая позиция: {currentQuantity}");
                                    return OrderStatus.Filled;
                                }

                                // Проверяем, не застрял ли процесс
                                if (currentQuantity == lastQuantity)
                                {
                                    noChangeCount++;
                                    if (noChangeCount > 10) // 5 секунд без изменений
                                    {
                                        Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - Заявка {ticker} частично исполнена: {currentQuantity} из {quantity}");
                                        return OrderStatus.PartiallyFilled;
                                    }
                                }
                                else
                                {
                                    lastQuantity = currentQuantity;
                                    noChangeCount = 0;
                                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - Заявка {ticker} исполняется: {currentQuantity} из {quantity}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - Ошибка проверки позиции для {ticker}: {ex.Message}");
                                // Продолжаем ожидание, возможно временная ошибка
                            }
                        }
                        else
                        {
                            // Если MainViewModel недоступен, считаем ордер исполненным
                            Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - Заявка {ticker} ({quantity} лот) - MainViewModel недоступен, считаем исполненным");
                            return OrderStatus.Filled;

                           // Debug.WriteLine($"Заявка {ticker} ({quantity} лот) - MainViewModel недоступен, считаем НЕ исполненным");
                            //return OrderStatus.Cancelled;
                        }
                    }
                    else if (status == OrderStatus.Rejected || status == OrderStatus.Cancelled)
                    {
                        Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - Заявка {ticker} ({quantity} лот) отклонена или отменена. Статус: {status}");
                        return status;
                    }

                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - Ошибка в WaitForOrderExecutionAsync для {ticker}: {ex.Message}");
                    await Task.Delay(500);
                }
            }

            Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync - Заявка {ticker} ({quantity} лот) не исполнена за {timeoutSeconds} секунд. Последний статус: {status}");
            return status;
        }*/
        public async Task<OrderStatus> WaitForOrderExecutionAsync(string orderId, int quantity, string ticker, string instrumentUId, bool isEntryOrder, bool isExitOrder, int timeoutSeconds = 30)
        {
            var startTime = DateTime.Now;
            OrderStatus status = OrderStatus.Pending;
            int lastQuantity = -1;
            int noChangeCount = 0;
            const int MAX_NO_CHANGE_COUNT = 10; // 10 секунд без изменений

            // Для exit ордера мы ожидаем, что позиция станет 0
            int targetQuantity = isExitOrder ? 0 : quantity;
            bool isCompleted = false;

            Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync START: Ticker={ticker}, IsEntry={isEntryOrder}, IsExit={isExitOrder}, TargetQuantity={targetQuantity}");

            while (!isCompleted && (DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
            {
                try
                {
                    // Получаем статус ордера
                    status = await GetOrderStatusAsync(orderId);

                    // Если ордер отменен или отклонен - выходим
                    if (status == OrderStatus.Rejected || status == OrderStatus.Cancelled)
                    {
                        Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Ордер {ticker} отменен или отклонен. Статус: {status}");
                        return status;
                    }

                    // Получаем текущую позицию
                    if (_selectedAccount != null && !string.IsNullOrEmpty(_selectedAccount.Id))
                    {
                        try
                        {
                            var positionInstrument = await _provider.GetPositionQuantity(
                                _selectedAccount.Id,
                                instrumentUId,
                                ticker);

                            int currentQuantity = positionInstrument?.Quantity ?? 0;
                            int absCurrentQuantity = Math.Abs(currentQuantity);
                            int absTargetQuantity = Math.Abs(targetQuantity);

                            Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Ticker={ticker}, Current={currentQuantity}, Target={targetQuantity}, IsExit={isExitOrder}");

                            // Проверяем условия завершения
                            if (isExitOrder)
                            {
                                // Для выхода: позиция должна стать 0
                                if (currentQuantity == 0)
                                {
                                    isCompleted = true;
                                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Заявка {ticker} - выход выполнен! Позиция: {currentQuantity}");
                                    return OrderStatus.Filled;
                                }
                                // Проверяем, изменилась ли позиция
                                else if (currentQuantity != lastQuantity)
                                {
                                    lastQuantity = currentQuantity;
                                    noChangeCount = 0;
                                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Заявка {ticker} - позиция изменяется: {currentQuantity}");
                                }
                                else
                                {
                                    noChangeCount++;
                                    if (noChangeCount > MAX_NO_CHANGE_COUNT)
                                    {
                                        Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Заявка {ticker} - позиция не меняется {noChangeCount} секунд. Текущая: {currentQuantity}");
                                        // Если позиция не 0, но не меняется - возможно частичное исполнение
                                        if (currentQuantity != 0)
                                        {
                                            Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Заявка {ticker} - частичное исполнение. Остаток: {currentQuantity}");
                                            return OrderStatus.PartiallyFilled;
                                        }
                                    }
                                }
                            }
                            else // Entry order
                            {
                                // Для входа: позиция должна достичь целевого количества
                                if (absCurrentQuantity >= absTargetQuantity)
                                {
                                    isCompleted = true;
                                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Заявка {ticker} ({quantity} лот) полностью исполнена! Текущая позиция: {currentQuantity}");
                                    return OrderStatus.Filled;
                                }
                                else if (currentQuantity != lastQuantity)
                                {
                                    lastQuantity = currentQuantity;
                                    noChangeCount = 0;
                                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Заявка {ticker} исполняется: {absCurrentQuantity} из {absTargetQuantity}");
                                }
                                else
                                {
                                    noChangeCount++;
                                    if (noChangeCount > MAX_NO_CHANGE_COUNT)
                                    {
                                        Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Заявка {ticker} - позиция не меняется. Текущая: {absCurrentQuantity} из {absTargetQuantity}");
                                        if (absCurrentQuantity > 0 && absCurrentQuantity < absTargetQuantity)
                                        {
                                            return OrderStatus.PartiallyFilled;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Ошибка проверки позиции для {ticker}: {ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: _selectedAccount не доступен для {ticker}");
                    }

                    // Ждем перед следующей проверкой
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Ошибка в цикле для {ticker}: {ex.Message}");
                    await Task.Delay(500);
                }
            }

            // Таймаут или выход по другой причине
            Debug.WriteLine($"DEBUG - WaitForOrderExecutionAsync: Заявка {ticker} не исполнена за {timeoutSeconds} секунд. Последний статус: {status}");
            return status;
        }
        #endregion




        #region Управление журналом сделок
        /// <summary>
        /// Создание таблицы DealsJournal если не существует
        /// </summary>
        private async Task EnsureDealsJournalTableExistsAsync( )
        {
            try
            {
                var command = _connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS DealsJournal (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Ticker TEXT NOT NULL,
                        InstrumentUid TEXT NOT NULL,
                        Strategy TEXT NOT NULL,
                
                        -- Данные входа
                        EntryTime DATETIME NOT NULL,
                        EntryPrice DECIMAL(18,8) NOT NULL,
                        EntryQuantity INTEGER NOT NULL,
                        EntryOrderId TEXT NOT NULL,
                        Direction TEXT NOT NULL,
                
                        -- Данные выхода
                        ExitTime DATETIME,
                        ExitPrice DECIMAL(18,8),
                        ExitOrderId TEXT,
                
                        -- Статус
                        Status TEXT NOT NULL,
                
                        -- P&U (сохраняем на момент закрытия, для открытых рассчитывается динамически)
                        ClosedPnL DECIMAL(18,2),
                        ClosedPnLPercent DECIMAL(18,2),
                
                        -- Комментарий
                        Comment TEXT,
                
                        -- Служебные поля
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
            
                    CREATE INDEX IF NOT EXISTS idx_DealsJournal_Ticker ON DealsJournal(Ticker);
                    CREATE INDEX IF NOT EXISTS idx_DealsJournal_Status ON DealsJournal(Status);
                    CREATE INDEX IF NOT EXISTS idx_DealsJournal_EntryTime ON DealsJournal(EntryTime DESC);
                ";

                await command.ExecuteNonQueryAsync();
                //Debug.WriteLine($"DEBUG: Таблица DealsJournal создана/проверена для {_instrument.Ticker}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка создания таблицы DealsJournal: {ex.Message}");
            }
        }

        /// <summary>
        /// Запись новой открытой сделки в журнал
        /// </summary>
        public async Task<long> AddOpenDealAsync(string ticker, string instrumentUid, string strategy, string timeFrame,
            DateTime entryTime, decimal entryPrice, int entryQuantity, string entryOrderId, string direction, string comment = "")
        {
            try
            {
                await EnsureDealsJournalTableExistsAsync();

                var command = _connection.CreateCommand();
                command.CommandText = @"
            INSERT INTO DealsJournal 
            (Ticker, InstrumentUid, Strategy, EntryTime, EntryPrice, EntryQuantity, EntryOrderId, Direction, Status, Comment, CreatedAt, UpdatedAt)
            VALUES 
            (@ticker, @instrumentUid, @strategy, @entryTime, @entryPrice, @entryQuantity, @entryOrderId, @direction, @status, @comment, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            
            SELECT last_insert_rowid();
        ";

                command.Parameters.AddWithValue("@ticker", ticker);
                command.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                command.Parameters.AddWithValue("@strategy", $"{strategy} - {timeFrame}");
                command.Parameters.AddWithValue("@entryTime", entryTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                command.Parameters.AddWithValue("@entryPrice", entryPrice);
                command.Parameters.AddWithValue("@entryQuantity", entryQuantity);
                command.Parameters.AddWithValue("@entryOrderId", entryOrderId);
                command.Parameters.AddWithValue("@direction", direction);
                command.Parameters.AddWithValue("@status", DealStatus.Open.ToString());
                command.Parameters.AddWithValue("@comment", comment ?? "");

                var dealId = (long)await command.ExecuteScalarAsync();

                Debug.WriteLine($"DEBUG: Открытая сделка #{dealId} записана в журнал: {ticker} {direction} {entryQuantity} лотов по {entryPrice}");


                // В методе AddOpenDealAsync или CloseDealAsync после успешной записи:
                if (_mainViewModel != null)
                {
                    await _mainViewModel.OnDealChangedAsync();
                }

                return dealId;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка записи открытой сделки: {ex.Message}");
                return -1;
            }

          
        }

        /// <summary>
        /// Обновление сделки при закрытии
        /// </summary>
        public async Task<bool> CloseDealAsync(string instrumentUid, string entryOrderId,
            DateTime exitTime, decimal? exitPrice, string exitOrderId, decimal? closedPnL, decimal? closedPnLPercent, string comment = "")
        {

            Debug.WriteLine($"DEBUG: Сделка для закрытия P/L: -------------------------------------------{closedPnL}");
            Debug.WriteLine($"DEBUG: Closing deal - InstrumentUid={instrumentUid}, EntryOrderId={entryOrderId}, ExitOrderId={exitOrderId}");

            try
            {
                var command = _connection.CreateCommand();
                command.CommandText = @"
                    UPDATE DealsJournal 
                    SET ExitTime = @exitTime,
                        ExitPrice = @exitPrice,
                        ExitOrderId = @exitOrderId,
                        Status = @status,
                        ClosedPnL = @closedPnL,
                        ClosedPnLPercent = @closedPnLPercent,
                        Comment = CASE 
                            WHEN @comment IS NOT NULL AND @comment != '' THEN @comment 
                            ELSE Comment || ' Закрыта: ' || @comment 
                        END,
                        UpdatedAt = CURRENT_TIMESTAMP
                    WHERE InstrumentUid = @instrumentUid 
                      AND EntryOrderId = @entryOrderId 
                      AND Status = @openStatus";

                command.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                command.Parameters.AddWithValue("@entryOrderId", entryOrderId);
                command.Parameters.AddWithValue("@exitTime", exitTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                command.Parameters.AddWithValue("@exitPrice", exitPrice);
                command.Parameters.AddWithValue("@exitOrderId", exitOrderId);
                command.Parameters.AddWithValue("@status", DealStatus.Closed.ToString());
                command.Parameters.AddWithValue("@openStatus", DealStatus.Open.ToString());
                command.Parameters.AddWithValue("@closedPnL", closedPnL);
                command.Parameters.AddWithValue("@closedPnLPercent", closedPnLPercent);
                command.Parameters.AddWithValue("@comment", comment ?? "");

                var rowsAffected = await command.ExecuteNonQueryAsync();

                Debug.WriteLine($"DEBUG: CloseDealAsync rows affected: {rowsAffected}");

                if (rowsAffected > 0)
                {
                    Debug.WriteLine($"DEBUG: Сделка по {instrumentUid} закрыта: P&L={closedPnL:F2} ({closedPnLPercent:F2}%)");
                    Debug.WriteLine($"DEBUG: Deal closed successfully: {instrumentUid}/{entryOrderId}");

                    // После успешного обновления БД, уведомляем главное окно
                    if (Application.Current.MainWindow?.DataContext is MainViewModel mainVM)
                    {
                        // Используем Dispatcher для безопасного вызова
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            mainVM.NotifyDealsUpdated();
                        });
                    }

                    return true;
                }


                // Если не нашли сделку, проверим какие есть открытые сделки
                var checkCommand = _connection.CreateCommand();
                checkCommand.CommandText = @"
            SELECT Id, EntryOrderId, Status FROM DealsJournal 
            WHERE InstrumentUid = @instrumentUid AND Status = @openStatus";
                checkCommand.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                checkCommand.Parameters.AddWithValue("@openStatus", DealStatus.Open.ToString());

                using var reader = await checkCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    Debug.WriteLine($"DEBUG: Found open deal - Id={reader.GetInt64(0)}, EntryOrderId={reader.GetString(1)}, Status={reader.GetString(2)}");
                }

                Debug.WriteLine($"DEBUG: Сделка для закрытия не найдена: {instrumentUid}/{entryOrderId}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка закрытия сделки: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Обновление текущей P&U для открытых сделок и запись в БД
        /// Вызывается при каждом обновлении цены
        /// </summary>
        public async Task UpdateOpenDealsPnLAsync(string instrumentUid, decimal currentPrice)
        {
            try
            {
                // Сначала убеждаемся, что таблица существует
                await EnsureDealsJournalTableExistsAsync();

                // Получаем все открытые сделки по данному инструменту
                var command = _connection.CreateCommand();
                command.CommandText = @"
            SELECT Id, EntryPrice, EntryQuantity, Direction, EntryOrderId, Ticker, Strategy, Comment
            FROM DealsJournal 
            WHERE InstrumentUid = @instrumentUid AND Status = @status
            ORDER BY EntryTime DESC";

                command.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                command.Parameters.AddWithValue("@status", DealStatus.Open.ToString());

                var openDeals = new List<(long Id, decimal EntryPrice, int Quantity, string Direction,
                                          string EntryOrderId, string Ticker, string Strategy, string Comment)>();

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        openDeals.Add((
                            reader.GetInt64(0),
                            reader.GetDecimal(1),
                            reader.GetInt32(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5),
                            reader.GetString(6),
                            reader.IsDBNull(7) ? "" : reader.GetString(7)
                        ));
                    }
                }

                if (!openDeals.Any())
                {
                    //Debug.WriteLine($"DEBUG: Нет открытых сделок для инструмента {instrumentUid}");
                    return;
                }

                // Обновляем P&L для каждой открытой сделки
                foreach (var deal in openDeals)
                {
                    decimal pnl = 0;
                    decimal pnlPercent = 0;


                    if (_instrument.Type == Models.InstrumentType.Share)
                    {
                        //Debug.WriteLine($"DEBUG: UpdateOpenDealsPnLAsync - АКЦИИ - _instrument.Type={_instrument.Type} <-- (ПРОВЕРНКА)  Надо разграничить с фьючерсами" +
                        //   $"т.к. при вычислении P&L в акции используется шаг цены, и он учтен тут, а у фьючерса стоимость пункта и там это не учтено");

                        // Рассчитываем P&L в зависимости от направления
                        //if (deal.Direction == "Short" || deal.Direction == "Sell")
                        if (deal.Direction == "Long" || deal.Direction == "Buy")
                        {
                            pnl = (currentPrice - deal.EntryPrice) * deal.Quantity * _instrument.LotSize;
                            pnlPercent = deal.EntryPrice > 0 ? (currentPrice - deal.EntryPrice) / deal.EntryPrice * 100 : 0;
                            //Debug.WriteLine($"DEBUG: ----------------- P&L: {_instrument.Ticker} pnl={pnl}");
                        }
                        //else if (deal.Direction == "Long" || deal.Direction == "Buy")  // Short или Sell
                        else if (deal.Direction == "Short" || deal.Direction == "Sell")  // Short или Sell
                        {
                            pnl = (deal.EntryPrice - currentPrice) * deal.Quantity * _instrument.LotSize;
                            pnlPercent = deal.EntryPrice > 0 ? (deal.EntryPrice - currentPrice) / deal.EntryPrice * 100 : 0;
                            //Debug.WriteLine($"DEBUG: ----------------- P&L: {_instrument.Ticker} pnl={pnl}");
                        }
                    }

                    if (_instrument.Type == Models.InstrumentType.Future)
                    {
                        //Debug.WriteLine($"DEBUG: UpdateOpenDealsPnLAsync - ФЬЮЧЕРСЫ - _instrument.Type={_instrument.Type}  <-- (ПРОВЕРНКА) Надо разграничить с акциями " +
                        //    $"т.к. при вычислении P&L во фьючерса используется не шаг цены, а стоимость пункта");

                        // Рассчитываем P&L в зависимости от направления
                        if (deal.Direction == "Long" || deal.Direction == "Buy")
                        //if (deal.Direction == "Short" || deal.Direction == "Sell")
                        {
                            pnl = (currentPrice - deal.EntryPrice) * deal.Quantity * _instrument.LotSize;   // тут должн быть не размер лота а стоимость пункта
                            pnlPercent = deal.EntryPrice > 0 ? (currentPrice - deal.EntryPrice) / deal.EntryPrice * 100 : 0;
                        }
                        else if (deal.Direction == "Short" || deal.Direction == "Sell")  // Short или Sell
                        //else if (deal.Direction == "Long" || deal.Direction == "Buy")
                        {
                            pnl = (deal.EntryPrice - currentPrice) * deal.Quantity * _instrument.LotSize;  // тут должн быть не размер лота а стоимость пункта
                            pnlPercent = deal.EntryPrice > 0 ? (deal.EntryPrice - currentPrice) / deal.EntryPrice * 100 : 0;
                        }
                    }




                    // Обновляем запись в БД с текущим P&L
                    // Для этого добавим временные поля или обновляем комментарий
                    // Так как в таблице нет полей для текущего P&L, будем обновлять комментарий
                    var updateCommand = _connection.CreateCommand();
                    updateCommand.CommandText = @"
                UPDATE DealsJournal 
                SET Comment = @comment, ClosedPnL = @closedPnL, ClosedPnLPercent = @closedPnLPercent, UpdatedAt = CURRENT_TIMESTAMP WHERE Id = @dealId";

                    // Формируем обновленный комментарий с текущим P&L
                    string baseComment = string.IsNullOrEmpty(deal.Comment) ? "" : deal.Comment;
                    string pnlComment = $"Текущий P&L: {pnl:F2} ({pnlPercent:F2}%)";

                    // Если в комментарии уже есть информация о P&L, заменяем её
                    if (baseComment.Contains("Текущий P&L:"))
                    {
                        // Заменяем старую информацию о P&L
                        int startIndex = baseComment.IndexOf("Текущий P&L:");
                        int endIndex = baseComment.IndexOf(")", startIndex);
                        if (endIndex > startIndex)
                        {
                            baseComment = baseComment.Remove(startIndex, endIndex - startIndex + 1).Trim();
                        }
                        baseComment = (baseComment + " " + pnlComment).Trim();
                    }
                    else
                    {
                        // Добавляем новую информацию о P&L
                        baseComment = (baseComment + " " + pnlComment).Trim();
                    }

                    updateCommand.Parameters.AddWithValue("@dealId", deal.Id);
                    updateCommand.Parameters.AddWithValue("@closedPnL", pnl);
                    updateCommand.Parameters.AddWithValue("@closedPnLPercent", Math.Round(pnlPercent, 2));
                    updateCommand.Parameters.AddWithValue("@comment", baseComment);

                    await updateCommand.ExecuteNonQueryAsync();

                    //Debug.WriteLine($"DEBUG: Сделка #{deal.Id} ({deal.Ticker}): текущий P&L = {pnl:F2} ({pnlPercent:F2}%)");

                    // После успешного обновления БД, уведомляем главное окно
                    if (Application.Current.MainWindow?.DataContext is MainViewModel mainVM)
                    {
                        // Используем Dispatcher для безопасного вызова
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            mainVM.NotifyDealsUpdated();
                        });
                    }

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка обновления P&L: {ex.Message}");
                Debug.WriteLine($"DEBUG: StackTrace: {ex.StackTrace}");
            }
        }



        /// <summary>
        ///  Если программа перезагружена и _currentPosition в стратегии не существет, то надо обратиться к БД и достать оттуда открытую сделку и пересоздать _currentPosition и перезаписать в него входные данныеделаем LIST  с позициями на случай ели стратегия предполагает множество входов
        /// Вызывается Если в стратегии _currentPosition или _dealForExt == NULL
        /// </summary>
        public async Task<List<Position>> ReadDBOpenDealsAsync()
        {


            List<Position> _positionsList = new List<Position>();

            // Если программа перезагружена и _currentPosition в стратегии не существет, то надо обратиться к БД
            // и достать оттуда открытую сделку и пересоздать _currentPosition и перезаписать в него входные данные
            // делаем LIST  с позициями на случай ели стратегия предполагает множество входов

            // достаем из базы входные данные
            try
            {
                string dbPath = System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory,
                    "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = @"
                                        SELECT Id, Ticker, InstrumentUid, Strategy, EntryTime, EntryPrice, EntryQuantity, EntryOrderId, Direction, ExitTime, ExitPrice, ExitOrderId, Status, 
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
                    var posTemp = new Position
                    {
                        Id = reader.GetInt64(0),
                        Ticker = reader.GetString(1),
                        InstrumentUid = reader.GetString(2),
                        Strategy = $"{reader.GetString(3)}",
                        EntryDateTime = reader.GetDateTime(4),
                        EntryPrice = reader.GetDecimal(5),
                        Quantity = reader.GetInt32(6),
                        EntryOrderId = reader.GetString(7),
                        Direction = reader.GetString(8),
                        ExitDateTime = reader.IsDBNull(9) ? null : (DateTime?)reader.GetDateTime(9),
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

                    //Debug.WriteLine($"✅ Найдена открытая сделка для {_instrument.Ticker}: EntryOrderId={posTemp.EntryOrderId}, EntryPrice={posTemp.EntryPrice}");

                    _positionsList.Add(posTemp);  // Добавляем сделку в лист
                }
                else
                {
                    //Debug.WriteLine($"❌ Не найдена открытая сделка для {_instrument.Ticker}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка загрузки сделок: {ex.Message}");
                Debug.WriteLine($"DEBUG: StackTrace: {ex.StackTrace}");
            }

            return _positionsList;
        }










        #endregion


    }

    #region Результаты операций

    /// <summary>
    /// Результат выполнения ордера
    /// </summary>
    public class OrderResult
    {
        public bool IsSuccess { get; set; }
        public string? OrderId { get; set; }
        public Order? Order { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Результат выполнения тейк-профит и стоп-лосс ордеров
    /// </summary>
    public class TakeProfitStopLossResult
    {
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }

        // Рассчитанные цены
        public decimal TakeProfitPrice { get; set; }
        public decimal StopLossPrice { get; set; }

        // Направления ордеров
        public string? TakeProfitDirection { get; set; }
        public string? StopLossDirection { get; set; }

        // ID ордеров
        public string? TakeProfitOrderId { get; set; }
        public string? StopLossOrderId { get; set; }

        // Статусы отправки
        public bool TakeProfitSuccess { get; set; }
        public bool StopLossSuccess { get; set; }
    }

    #endregion
}