using MoneyGenerator_v5.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace MoneyGenerator_v5.Services
{
    public class OperationProcessingService
    {
        private readonly string _connectionString;

        public OperationProcessingService()
        {
            var dbPath = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "market_dataMG5.db");
            _connectionString = $"Data Source={dbPath}";
        }

        /// <summary>
        /// Обрабатывает операции: связывает покупки/продажи по FIFO с учетом частичных закрытий
        /// </summary>
        public async Task<List<ProcessedOperation>> ProcessOperationsAsync(List<Operation> operations)
        {
            var processed = new List<ProcessedOperation>();

            if (operations == null || !operations.Any())
            {
                Debug.WriteLine("ProcessOperationsAsync: Нет операций для обработки");
                return processed;
            }

            //Debug.WriteLine($"ProcessOperationsAsync: Получено {operations.Count} операций");

            // Фильтруем только торговые операции BUY/SELL
            var tradeOperations = operations
                .Where(o => o.OperationType?.ToUpper() == "BUY" || o.OperationType?.ToUpper() == "SELL")
                .OrderBy(o => o.Date)
                .ToList();

            //Debug.WriteLine($"ProcessOperationsAsync: Найдено {tradeOperations.Count} торговых операций");

            // Группируем по инструменту
            var groupedByInstrument = tradeOperations
                .GroupBy(o => new { o.Ticker, o.InstrumentUid })
                .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Date).ToList());

            foreach (var instrumentGroup in groupedByInstrument)
            {
                var ticker = instrumentGroup.Key.Ticker;
                var instrumentUid = instrumentGroup.Key.InstrumentUid;
                var ops = instrumentGroup.Value;

               // Debug.WriteLine($"\n=== Обработка инструмента {ticker}, операций: {ops.Count} ===");

                // Очереди для LONG и SHORT позиций (FIFO)
                var longQueue = new Queue<Operation>();   // BUY операции
                var shortQueue = new Queue<Operation>();  // SELL операции

                // Текущая чистая позиция
                decimal netPosition = 0;

                foreach (var op in ops)
                {
                    var opType = op.OperationType?.ToUpper() ?? "";
                    var quantity = Math.Abs(op.Quantity);
                    var price = op.Price;

                    // Пропускаем фиктивные балансирующие операции с ценой 0
                    bool isBalanceOp = op.Id?.StartsWith("BALANCE_") == true;

                    if (opType == "BUY")
                    {
                        // Закрываем SHORT
                        while (quantity > 0 && shortQueue.Any())
                        {
                            var shortOp = shortQueue.Dequeue();
                            var shortQty = Math.Abs(shortOp.Quantity);
                            var closeQty = Math.Min(quantity, shortQty);

                            if (!isBalanceOp && !shortOp.Id?.StartsWith("BALANCE_") == true)
                            {
                                var closedDeal = CreateClosedDeal(shortOp, op, closeQty, operations);
                                if (closedDeal != null)
                                {
                                    processed.Add(closedDeal);
                                }
                            }

                            quantity -= closeQty;
                            netPosition += closeQty;

                            if (closeQty < shortQty)
                            {
                                var remainingShort = new Operation
                                {
                                    Id = shortOp.Id,
                                    Ticker = shortOp.Ticker,
                                    InstrumentUid = shortOp.InstrumentUid,
                                    Date = shortOp.Date,
                                    Price = shortOp.Price,
                                    Quantity = -(shortQty - closeQty),
                                    OperationType = shortOp.OperationType,
                                    Payment = shortOp.Payment * (1 - closeQty / shortQty),
                                    Commission = shortOp.Commission * (1 - closeQty / shortQty)
                                };
                                shortQueue.Enqueue(remainingShort);
                            }
                        }

                        // Открываем LONG (только если не балансирующая операция)
                        if (quantity > 0 && !isBalanceOp)
                        {
                            var longOp = new Operation
                            {
                                Id = op.Id,
                                Ticker = op.Ticker,
                                InstrumentUid = op.InstrumentUid,
                                Date = op.Date,
                                Price = op.Price,
                                Quantity = quantity,
                                OperationType = op.OperationType,
                                Payment = op.Payment * (quantity / Math.Abs(op.Quantity)),
                                Commission = op.Commission * (quantity / Math.Abs(op.Quantity))
                            };
                            longQueue.Enqueue(longOp);
                            netPosition += quantity;
                        }
                    }
                    else if (opType == "SELL")
                    {
                        // Закрываем LONG
                        while (quantity > 0 && longQueue.Any())
                        {
                            var longOp = longQueue.Dequeue();
                            var longQty = Math.Abs(longOp.Quantity);
                            var closeQty = Math.Min(quantity, longQty);

                            if (!isBalanceOp && !longOp.Id?.StartsWith("BALANCE_") == true)
                            {
                                var closedDeal = CreateClosedDeal(longOp, op, closeQty, operations);
                                if (closedDeal != null)
                                {
                                    processed.Add(closedDeal);
                                }
                            }

                            quantity -= closeQty;
                            netPosition -= closeQty;

                            if (closeQty < longQty)
                            {
                                var remainingLong = new Operation
                                {
                                    Id = longOp.Id,
                                    Ticker = longOp.Ticker,
                                    InstrumentUid = longOp.InstrumentUid,
                                    Date = longOp.Date,
                                    Price = longOp.Price,
                                    Quantity = longQty - closeQty,
                                    OperationType = longOp.OperationType,
                                    Payment = longOp.Payment * (1 - closeQty / longQty),
                                    Commission = longOp.Commission * (1 - closeQty / longQty)
                                };
                                longQueue.Enqueue(remainingLong);
                            }
                        }

                        // Открываем SHORT (только если не балансирующая операция)
                        if (quantity > 0 && !isBalanceOp)
                        {
                            var shortOp = new Operation
                            {
                                Id = op.Id,
                                Ticker = op.Ticker,
                                InstrumentUid = op.InstrumentUid,
                                Date = op.Date,
                                Price = op.Price,
                                Quantity = -quantity,
                                OperationType = op.OperationType,
                                Payment = op.Payment * (quantity / Math.Abs(op.Quantity)),
                                Commission = op.Commission * (quantity / Math.Abs(op.Quantity))
                            };
                            shortQueue.Enqueue(shortOp);
                            netPosition -= quantity;
                        }
                    }
                }

                // Добавляем оставшиеся открытые позиции (только не балансирующие)
                while (longQueue.Any())
                {
                    var longOp = longQueue.Dequeue();
                    if (!longOp.Id?.StartsWith("BALANCE_") == true && Math.Abs(longOp.Quantity) > 0.01m)
                    {
                        var openDeal = CreateOpenDeal(longOp, operations, isShort: false);
                        if (openDeal != null)
                        {
                            processed.Add(openDeal);
                        }
                    }
                }

                while (shortQueue.Any())
                {
                    var shortOp = shortQueue.Dequeue();
                    if (!shortOp.Id?.StartsWith("BALANCE_") == true && Math.Abs(shortOp.Quantity) > 0.01m)
                    {
                        var openDeal = CreateOpenDeal(shortOp, operations, isShort: true);
                        if (openDeal != null)
                        {
                            processed.Add(openDeal);
                        }
                    }
                }

                //Debug.WriteLine($"\n  Итоговая чистая позиция для {ticker}: {netPosition:F0}");
            }

            // Удаляем дублирующиеся открытые позиции
            var result = RemoveDuplicateOpenPositions(processed);

            Debug.WriteLine($"\nProcessOperationsAsync: Итоговых сделок: {result.Count}");
            Debug.WriteLine($"  Закрытых: {result.Count(p => p.Status == "Closed")}");
            Debug.WriteLine($"  Открытых: {result.Count(p => p.Status.Contains("Open"))}");

            return result
                .OrderByDescending(p => p.CloseDate ?? p.OpenDate)
                .ToList();
        }

        private ProcessedOperation CreateClosedDeal(
    Operation entryOp,
    Operation exitOp,
    decimal quantity,
    List<Operation> allOps)
        {
            try
            {
                // Определяем направление
                bool isShort = entryOp.OperationType?.ToUpper() == "SELL";
                bool isLong = entryOp.OperationType?.ToUpper() == "BUY";

                if (!isShort && !isLong)
                {
                    Debug.WriteLine($"  Ошибка: неизвестный тип операции {entryOp.OperationType}");
                    return null;
                }

                var entryFee = FindFeeForOperation(entryOp, allOps);
                var exitFee = FindFeeForOperation(exitOp, allOps);

                decimal entryPrice = entryOp.Price;
                decimal exitPrice = exitOp.Price;
                decimal entryAmount = entryPrice * quantity;
                decimal exitAmount = exitPrice * quantity;
                decimal grossProfit;

                if (isShort)
                {
                    // SHORT: продажа (entry) -> покупка (exit)
                    grossProfit = entryAmount - exitAmount;
                }
                else
                {
                    // LONG: покупка (entry) -> продажа (exit)
                    grossProfit = exitAmount - entryAmount;
                }

                var totalFee = (entryFee?.Payment ?? 0) + (exitFee?.Payment ?? 0);
                var netProfit = grossProfit + totalFee; // Комиссии отрицательные
                var netProfitPercent = entryAmount > 0 ? (netProfit / entryAmount) * 100 : 0;

                return new ProcessedOperation
                {
                    Id = $"{entryOp.Id}_{exitOp.Id}_{Guid.NewGuid():N}".SafeSubstring(0, 50),
                    Ticker = entryOp.Ticker ?? exitOp.Ticker,
                    InstrumentUid = entryOp.InstrumentUid ?? exitOp.InstrumentUid,
                    OpenDate = entryOp.Date,
                    CloseDate = exitOp.Date,
                    OpenPrice = entryPrice,
                    ClosePrice = exitPrice,
                    Quantity = (int)quantity,
                    BuyAmount = isShort ? exitAmount : entryAmount,
                    SellAmount = isShort ? entryAmount : exitAmount,
                    GrossProfit = grossProfit,
                    BuyFee = isShort ? (exitFee?.Payment ?? 0) : (entryFee?.Payment ?? 0),
                    SellFee = isShort ? (entryFee?.Payment ?? 0) : (exitFee?.Payment ?? 0),
                    TotalFee = totalFee,
                    NetProfit = Math.Round(netProfit, 2),
                    NetProfitPercent = Math.Round(netProfitPercent, 2),
                    Status = "Closed",
                    BuyOperationId = isShort ? exitOp.Id : entryOp.Id,
                    SellOperationId = isShort ? entryOp.Id : exitOp.Id,
                    Direction = isShort ? "Short" : "Long",
                    Strategy = GetStrategyForInstrument(entryOp.Ticker),
                    Comment = $"Вход: {FormatOperationType(entryOp.OperationType)} по {entryPrice:F2}, Выход: {FormatOperationType(exitOp.OperationType)} по {exitPrice:F2}"
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка создания ProcessedOperation: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Группирует операции по сделкам с учетом стратегий и комментариев
        /// </summary>
        public async Task<List<ProcessedOperation>> GroupOperationsByDealsAsync(List<Operation> operations)
        {
            var processed = new List<ProcessedOperation>();

            if (operations == null || !operations.Any())
            {
                Debug.WriteLine("GroupOperationsByDealsAsync: Нет операций для обработки");
                return processed;
            }

            Debug.WriteLine($"GroupOperationsByDealsAsync: Получено {operations.Count} операций");

            // Фильтруем только торговые операции BUY/SELL
            var tradeOperations = operations
                .Where(o => o.OperationType?.ToUpper() == "BUY" || o.OperationType?.ToUpper() == "SELL")
                .OrderBy(o => o.Date)
                .ToList();

            Debug.WriteLine($"GroupOperationsByDealsAsync: Найдено {tradeOperations.Count} торговых операций");

            // Группируем по инструменту
            var groupedByInstrument = tradeOperations
                .GroupBy(o => new { o.Ticker, o.InstrumentUid })
                .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Date).ToList());

            foreach (var instrumentGroup in groupedByInstrument)
            {
                var ticker = instrumentGroup.Key.Ticker;
                var instrumentUid = instrumentGroup.Key.InstrumentUid;
                var ops = instrumentGroup.Value;

                Debug.WriteLine($"\n=== Обработка инструмента {ticker}, операций: {ops.Count} ===");

                // Очереди для LONG и SHORT позиций (FIFO)
                var longQueue = new Queue<Operation>();   // BUY операции
                var shortQueue = new Queue<Operation>();  // SELL операции

                foreach (var op in ops)
                {
                    var opType = op.OperationType?.ToUpper() ?? "";
                    var quantity = Math.Abs(op.Quantity);
                    var price = op.Price;

                    if (opType == "BUY")
                    {
                        // Закрываем SHORT
                        while (quantity > 0 && shortQueue.Any())
                        {
                            var shortOp = shortQueue.Dequeue();
                            var shortQty = Math.Abs(shortOp.Quantity);
                            var closeQty = Math.Min(quantity, shortQty);

                            var closedDeal = CreateClosedDeal(shortOp, op, closeQty, operations);
                            if (closedDeal != null)
                            {
                                processed.Add(closedDeal);
                            }

                            quantity -= closeQty;

                            if (closeQty < shortQty)
                            {
                                var remainingShort = new Operation
                                {
                                    Id = shortOp.Id,
                                    Ticker = shortOp.Ticker,
                                    InstrumentUid = shortOp.InstrumentUid,
                                    Date = shortOp.Date,
                                    Price = shortOp.Price,
                                    Quantity = -(shortQty - closeQty),
                                    OperationType = shortOp.OperationType,
                                    Payment = shortOp.Payment * (1 - closeQty / shortQty),
                                    Commission = shortOp.Commission * (1 - closeQty / shortQty)
                                };
                                shortQueue.Enqueue(remainingShort);
                            }
                        }

                        // Открываем LONG
                        if (quantity > 0)
                        {
                            var longOp = new Operation
                            {
                                Id = op.Id,
                                Ticker = op.Ticker,
                                InstrumentUid = op.InstrumentUid,
                                Date = op.Date,
                                Price = op.Price,
                                Quantity = quantity,
                                OperationType = op.OperationType,
                                Payment = op.Payment * (quantity / Math.Abs(op.Quantity)),
                                Commission = op.Commission * (quantity / Math.Abs(op.Quantity))
                            };
                            longQueue.Enqueue(longOp);
                        }
                    }
                    else if (opType == "SELL")
                    {
                        // Закрываем LONG
                        while (quantity > 0 && longQueue.Any())
                        {
                            var longOp = longQueue.Dequeue();
                            var longQty = Math.Abs(longOp.Quantity);
                            var closeQty = Math.Min(quantity, longQty);

                            var closedDeal = CreateClosedDeal(longOp, op, closeQty, operations);
                            if (closedDeal != null)
                            {
                                processed.Add(closedDeal);
                            }

                            quantity -= closeQty;

                            if (closeQty < longQty)
                            {
                                var remainingLong = new Operation
                                {
                                    Id = longOp.Id,
                                    Ticker = longOp.Ticker,
                                    InstrumentUid = longOp.InstrumentUid,
                                    Date = longOp.Date,
                                    Price = longOp.Price,
                                    Quantity = longQty - closeQty,
                                    OperationType = longOp.OperationType,
                                    Payment = longOp.Payment * (1 - closeQty / longQty),
                                    Commission = longOp.Commission * (1 - closeQty / longQty)
                                };
                                longQueue.Enqueue(remainingLong);
                            }
                        }

                        // Открываем SHORT
                        if (quantity > 0)
                        {
                            var shortOp = new Operation
                            {
                                Id = op.Id,
                                Ticker = op.Ticker,
                                InstrumentUid = op.InstrumentUid,
                                Date = op.Date,
                                Price = op.Price,
                                Quantity = -quantity,
                                OperationType = op.OperationType,
                                Payment = op.Payment * (quantity / Math.Abs(op.Quantity)),
                                Commission = op.Commission * (quantity / Math.Abs(op.Quantity))
                            };
                            shortQueue.Enqueue(shortOp);
                        }
                    }
                }

                // Добавляем оставшиеся открытые позиции
                while (longQueue.Any())
                {
                    var longOp = longQueue.Dequeue();
                    if (Math.Abs(longOp.Quantity) > 0.01m)
                    {
                        var openDeal = CreateOpenDeal(longOp, operations, isShort: false);
                        if (openDeal != null)
                        {
                            processed.Add(openDeal);
                        }
                    }
                }

                while (shortQueue.Any())
                {
                    var shortOp = shortQueue.Dequeue();
                    if (Math.Abs(shortOp.Quantity) > 0.01m)
                    {
                        var openDeal = CreateOpenDeal(shortOp, operations, isShort: true);
                        if (openDeal != null)
                        {
                            processed.Add(openDeal);
                        }
                    }
                }
            }

            // Удаляем дублирующиеся открытые позиции
            var result = RemoveDuplicateOpenPositions(processed);

            Debug.WriteLine($"\nGroupOperationsByDealsAsync: Итоговых сделок: {result.Count}");
            Debug.WriteLine($"  Закрытых: {result.Count(p => p.Status == "Closed")}");
            Debug.WriteLine($"  Открытых: {result.Count(p => p.Status.Contains("Open"))}");

            return result
                .OrderByDescending(p => p.CloseDate ?? p.OpenDate)
                .ToList();
        }

        private ProcessedOperation CreateOpenDeal(Operation op, List<Operation> allOps, bool isShort)
        {
            try
            {
                var fee = FindFeeForOperation(op, allOps);
                var quantity = Math.Abs(op.Quantity);
                var amount = op.Price * quantity;

                return new ProcessedOperation
                {
                    Id = $"{op.Id}_open_{Guid.NewGuid():N}".SafeSubstring(0, 50),
                    Ticker = op.Ticker,
                    InstrumentUid = op.InstrumentUid,
                    OpenDate = op.Date,
                    CloseDate = null,
                    OpenPrice = op.Price,
                    ClosePrice = null,
                    Quantity = (int)quantity,
                    BuyAmount = isShort ? 0 : amount,
                    SellAmount = isShort ? amount : 0,
                    GrossProfit = 0, // Будет обновлено при получении текущей цены
                    BuyFee = isShort ? 0 : (fee?.Payment ?? 0),
                    SellFee = isShort ? (fee?.Payment ?? 0) : 0,
                    TotalFee = fee?.Payment ?? 0,
                    NetProfit = 0, // Будет обновлено при получении текущей цены
                    NetProfitPercent = 0, // Будет обновлено при получении текущей цены
                    Status = isShort ? "Open (Short)" : "Open (Long)",
                    BuyOperationId = isShort ? null : op.Id,
                    SellOperationId = isShort ? op.Id : null,
                    Direction = isShort ? "Short" : "Long",
                    Strategy = GetStrategyForInstrument(op.Ticker),
                    Comment = $"Открыта: {FormatOperationType(op.OperationType)}, Цена: {op.Price:F2}"
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка создания открытой позиции: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Получает стратегию для инструмента из базы данных или возвращает "Manual"
        /// </summary>
        private string GetStrategyForInstrument(string ticker)
        {
            try
            {
                // Пытаемся получить стратегию из таблицы DealsJournal
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
            SELECT Strategy 
            FROM DealsJournal 
            WHERE Ticker = @ticker 
            ORDER BY EntryTime DESC 
            LIMIT 1";

                cmd.Parameters.AddWithValue("@ticker", ticker);

                var result = cmd.ExecuteScalar();
                if (result != null && !string.IsNullOrEmpty(result.ToString()))
                {
                    return result.ToString();
                }

                return "Manual";
            }
            catch
            {
                return "Manual";
            }
        }

        /// <summary>
        /// Форматирует тип операции для отображения
        /// </summary>
        private string FormatOperationType(string operationType)
        {
            if (string.IsNullOrEmpty(operationType))
                return "Unknown";

            return operationType.ToUpper() switch
            {
                "BUY" => "Покупка",
                "SELL" => "Продажа",
                _ => operationType
            };
        }

        /// <summary>
        /// Обновляет P&L для открытых позиций с использованием текущей цены
        /// </summary>
        public async Task UpdateOpenPositionsPnLAsync(List<ProcessedOperation> operations, IProvirerService provider)
        {
            if (operations == null || !operations.Any() || provider == null)
                return;

            foreach (var op in operations.Where(o => o.Status.Contains("Open")))
            {
                try
                {
                    // Получаем текущую цену через провайдера
                    decimal currentPrice = await provider.GetCurrentPriceAsync(op.InstrumentUid);

                    if (currentPrice <= 0)
                        continue;

                    // Рассчитываем GrossProfit
                    if (op.Direction == "Long")
                    {
                        op.GrossProfit = (currentPrice - op.OpenPrice) * op.Quantity;
                    }
                    else // Short
                    {
                        op.GrossProfit = (op.OpenPrice - currentPrice) * op.Quantity;
                    }

                    // Рассчитываем NetProfit
                    op.NetProfit = op.GrossProfit + op.TotalFee; // TotalFee уже включает комиссии
                    op.NetProfitPercent = op.OpenPrice > 0 ? (op.NetProfit / (op.OpenPrice * op.Quantity)) * 100 : 0;

                    // Обновляем текущую цену в объекте
                    op.CurrentPrice = currentPrice;

                    Debug.WriteLine($"Обновлена P&L для {op.Ticker}: Gross={op.GrossProfit:F2}, Net={op.NetProfit:F2}, Price={currentPrice:F2}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка обновления P&L для {op.Ticker}: {ex.Message}");
                }
            }
        }



        /// <summary>
        /// Получает стратегию из комментария операции
        /// </summary>
        private string GetStrategyFromComment(Operation op, List<Operation> allOps)
        {
            try
            {
                if (op == null || string.IsNullOrEmpty(op.OperationTypeName))
                    return null;

                // Пытаемся извлечь стратегию из OperationTypeName или других полей
                var name = op.OperationTypeName?.ToUpper() ?? "";

                if (name.Contains("RSI")) return "RSI";
                if (name.Contains("MA")) return "MA";
                if (name.Contains("SMA")) return "SMA";
                if (name.Contains("EMA")) return "EMA";
                if (name.Contains("MACD")) return "MACD";
                if (name.Contains("BOLLINGER")) return "Bollinger Bands";
                if (name.Contains("RATING")) return "Rating";
                if (name.Contains("MANUAL")) return "Manual";

                // Если не нашли, проверяем другие операции
                var relatedOp = allOps.FirstOrDefault(o =>
                    o.Id != op.Id &&
                    Math.Abs((o.Date - op.Date).TotalSeconds) < 60 &&
                    !string.IsNullOrEmpty(o.OperationTypeName) &&
                    o.OperationTypeName.ToUpper().Contains("STRATEGY"));

                if (relatedOp != null && !string.IsNullOrEmpty(relatedOp.OperationTypeName))
                {
                    var relName = relatedOp.OperationTypeName.ToUpper();
                    if (relName.Contains("RSI")) return "RSI";
                    if (relName.Contains("MA")) return "MA";
                    if (relName.Contains("SMA")) return "SMA";
                    if (relName.Contains("EMA")) return "EMA";
                    if (relName.Contains("MACD")) return "MACD";
                    if (relName.Contains("BOLLINGER")) return "Bollinger Bands";
                    if (relName.Contains("RATING")) return "Rating";
                }

                return "Manual";
            }
            catch
            {
                return "Manual";
            }
        }


        /// <summary>
        /// Получает текущую цену для инструмента
        /// </summary>
        private async Task<decimal> GetCurrentPriceForInstrument(string instrumentUid)
        {
            try
            {
                // Здесь нужно получить цену из вашего провайдера
                // Это заглушка - в реальном коде используйте ваш IProvirerService
                return 0m; // Временно
            }
            catch
            {
                return 0m;
            }
        }


        private Operation FindFeeForOperation(Operation operation, List<Operation> allOps)
        {
            if (operation == null) return null;

            try
            {
                // Ищем комиссию в течение 10 секунд до или после операции
                var fee = allOps.FirstOrDefault(o =>
                {
                    string opType = o.OperationType?.ToUpper() ?? "";
                    return (opType.Contains("BROKER") ||
                            opType.Contains("FEE") ||
                            opType.Contains("COMMISSION")) &&
                           Math.Abs(o.Date.Subtract(operation.Date).TotalSeconds) <= 10 &&
                           Math.Abs(o.Payment) < 100 &&
                           o.OperationType != operation.OperationType &&
                           o.Id != operation.Id;
                });
                return fee;
            }
            catch
            {
                return null;
            }
        }

        private List<ProcessedOperation> RemoveDuplicateOpenPositions(List<ProcessedOperation> operations)
        {
            var closed = operations.Where(o => o.Status == "Closed").ToList();
            var open = operations.Where(o => o.Status.Contains("Open")).ToList();

            // Группируем открытые по инструменту и направлению
            var grouped = open
                .GroupBy(o => new { o.Ticker, o.InstrumentUid, o.Direction })
                .Select(g =>
                {
                    // Объединяем несколько открытых позиций в одну
                    if (g.Count() > 1)
                    {
                        var first = g.First();
                        var totalQuantity = g.Sum(o => o.Quantity);
                        var totalBuyAmount = g.Sum(o => o.BuyAmount);
                        var totalSellAmount = g.Sum(o => o.SellAmount);
                        var totalFee = g.Sum(o => o.TotalFee);

                        // Рассчитываем средневзвешенную цену
                        decimal avgPrice = 0;
                        if (totalBuyAmount > 0)
                            avgPrice = totalBuyAmount / totalQuantity;
                        else if (totalSellAmount > 0)
                            avgPrice = (decimal)(totalSellAmount / totalQuantity);

                        return new ProcessedOperation
                        {
                            Id = $"{first.Ticker}_{first.Direction}_{Guid.NewGuid():N}".SafeSubstring(0, 50),
                            Ticker = first.Ticker,
                            InstrumentUid = first.InstrumentUid,
                            OpenDate = g.Min(o => o.OpenDate),
                            CloseDate = null,
                            OpenPrice = avgPrice,
                            ClosePrice = null,
                            Quantity = (int)totalQuantity,
                            BuyAmount = totalBuyAmount,
                            SellAmount = totalSellAmount,
                            GrossProfit = 0,
                            BuyFee = g.Sum(o => o.BuyFee),
                            SellFee = g.Sum(o => o.SellFee),
                            TotalFee = totalFee,
                            NetProfit = 0,
                            NetProfitPercent = 0,
                            Status = first.Status,
                            BuyOperationId = string.Join(",", g.Select(o => o.BuyOperationId).Where(id => !string.IsNullOrEmpty(id))),
                            SellOperationId = string.Join(",", g.Select(o => o.SellOperationId).Where(id => !string.IsNullOrEmpty(id))),
                            Direction = first.Direction,
                            Strategy = first.Strategy,
                            Comment = string.Join("; ", g.Select(o => o.Comment).Where(c => !string.IsNullOrEmpty(c)))
                        };
                    }
                    return g.First();
                })
                .ToList();

            closed.AddRange(grouped);
            return closed;
        }

        #region Диагностические методы

        /// <summary>
        /// Получает сырые операции для указанного тикера из БД
        /// </summary>
        public async Task<List<Operation>> GetRawOperationsForTickerAsync(string ticker)
        {
            var operations = new List<Operation>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, Ticker, OperationType, Quantity, Price, Date, Payment, Commission
                    FROM OperationsJournal 
                    WHERE Ticker = @ticker
                    ORDER BY Date ASC";

                cmd.Parameters.AddWithValue("@ticker", ticker);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    operations.Add(new Operation
                    {
                        Id = reader.GetString(0),
                        Ticker = reader.GetString(1),
                        OperationType = reader.GetString(2),
                        Quantity = reader.GetDecimal(3),
                        Price = reader.GetDecimal(4),
                        Date = reader.GetDateTime(5),
                        Payment = reader.GetDecimal(6),
                        Commission = reader.GetDecimal(7)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка получения операций для {ticker}: {ex.Message}");
            }

            return operations;
        }

        /// <summary>
        /// Диагностика операций для указанного тикера
        /// </summary>
        public async Task DiagnoseTickerAsync(string ticker)
        {
            var ops = await GetRawOperationsForTickerAsync(ticker);

            Debug.WriteLine($"\n=== ДИАГНОСТИКА ДЛЯ {ticker} ===");
            Debug.WriteLine($"Всего операций: {ops.Count}");

            if (!ops.Any())
            {
                Debug.WriteLine($"Нет операций для {ticker}");
                return;
            }

            decimal totalBuy = 0;
            decimal totalSell = 0;
            decimal netPosition = 0;
            decimal totalBuyAmount = 0;
            decimal totalSellAmount = 0;
            decimal totalBuyFee = 0;
            decimal totalSellFee = 0;

            Debug.WriteLine("\nОперации по порядку:");
            foreach (var op in ops)
            {
                if (op.OperationType?.ToUpper() == "BUY")
                {
                    totalBuy += op.Quantity;
                    totalBuyAmount += op.Price * op.Quantity;
                    totalBuyFee += op.Commission;
                    netPosition += op.Quantity;
                    Debug.WriteLine($"BUY:  {op.Quantity:F0} x {op.Price:F2} = {op.Payment:F2} (комиссия: {op.Commission:F2})");
                }
                else if (op.OperationType?.ToUpper() == "SELL")
                {
                    var qty = Math.Abs(op.Quantity);
                    totalSell += qty;
                    totalSellAmount += op.Price * qty;
                    totalSellFee += op.Commission;
                    netPosition -= qty;
                    Debug.WriteLine($"SELL: {qty:F0} x {op.Price:F2} = {op.Payment:F2} (комиссия: {op.Commission:F2})");
                }
                else
                {
                    Debug.WriteLine($"ДРУГОЕ: {op.OperationType} {op.Quantity:F0} x {op.Price:F2} = {op.Payment:F2}");
                }
            }

            Debug.WriteLine($"\n--- ИТОГО ---");
            Debug.WriteLine($"Всего BUY:  {totalBuy:F0} (сумма: {totalBuyAmount:F2})");
            Debug.WriteLine($"Всего SELL: {totalSell:F0} (сумма: {totalSellAmount:F2})");
            Debug.WriteLine($"Чистая позиция: {netPosition:F0} ({(netPosition > 0 ? "LONG" : netPosition < 0 ? "SHORT" : "Закрыта")})");
            Debug.WriteLine($"Средняя цена BUY:  {(totalBuy > 0 ? totalBuyAmount / totalBuy : 0):F2}");
            Debug.WriteLine($"Средняя цена SELL: {(totalSell > 0 ? totalSellAmount / totalSell : 0):F2}");
            Debug.WriteLine($"Комиссии BUY:  {totalBuyFee:F2}");
            Debug.WriteLine($"Комиссии SELL: {totalSellFee:F2}");
            Debug.WriteLine($"Общие комиссии: {totalBuyFee + totalSellFee:F2}");

            if (Math.Abs(netPosition) > 0.01m)
            {
                Debug.WriteLine($"\n⚠️ ВНИМАНИЕ: Есть открытая позиция {Math.Abs(netPosition):F0} {(netPosition > 0 ? "LONG" : "SHORT")}");
            }
            else
            {
                Debug.WriteLine($"\n✅ Позиция полностью закрыта");
            }

            Debug.WriteLine($"\n=== КОНЕЦ ДИАГНОСТИКИ ДЛЯ {ticker} ===\n");
        }

        /// <summary>
        /// Диагностика всех инструментов с открытыми позициями
        /// </summary>
        public async Task DiagnoseAllOpenPositionsAsync()
        {
            Debug.WriteLine("\n=== ДИАГНОСТИКА ВСЕХ ОТКРЫТЫХ ПОЗИЦИЙ ===\n");

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT DISTINCT Ticker 
                    FROM OperationsJournal 
                    WHERE OperationType IN ('BUY', 'SELL')
                    ORDER BY Ticker";

                var tickers = new List<string>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tickers.Add(reader.GetString(0));
                }

                Debug.WriteLine($"Найдено инструментов с операциями: {tickers.Count}");

                foreach (var ticker in tickers)
                {
                    await DiagnoseTickerAsync(ticker);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка диагностики: {ex.Message}");
            }
        }

        #endregion


    }

    /// <summary>
    /// Extension method для безопасного Substring
    /// </summary>
    public static class StringExtensions
    {
        public static string SafeSubstring(this string str, int startIndex, int length)
        {
            if (string.IsNullOrEmpty(str))
                return str ?? string.Empty;

            if (startIndex >= str.Length)
                return string.Empty;

            if (startIndex + length > str.Length)
                length = str.Length - startIndex;

            return str.Substring(startIndex, length);
        }
    }

   /* public class ProcessedOperation
    {
        public string Id { get; set; }
        public string Ticker { get; set; }
        public string InstrumentUid { get; set; }
        public DateTime OpenDate { get; set; }
        public DateTime? CloseDate { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal? ClosePrice { get; set; }
        public int Quantity { get; set; }
        public decimal BuyAmount { get; set; }
        public decimal SellAmount { get; set; }
        public decimal GrossProfit { get; set; }
        public decimal BuyFee { get; set; }
        public decimal SellFee { get; set; }
        public decimal TotalFee { get; set; }
        public decimal NetProfit { get; set; }
        public decimal NetProfitPercent { get; set; }
        public string Status { get; set; }
        public string BuyOperationId { get; set; }
        public string SellOperationId { get; set; }
        public string Direction { get; set; }

        public string DisplayDirection => Direction == "Long" ? "📈 Long" :
                                          (Direction == "Short" ? "📉 Short" :
                                          (CloseDate.HasValue ? "✅ Закрыта" : "🟡 Открыта"));

        public string DisplayProfit => NetProfit >= 0 ? $"+{NetProfit:F2}" : $"{NetProfit:F2}";
        public string DisplayProfitPercent => NetProfitPercent >= 0 ? $"+{NetProfitPercent:F2}%" : $"{NetProfitPercent:F2}%";
    }*/
}