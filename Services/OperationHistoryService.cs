using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Tinkoff.InvestApi.V1;

namespace MoneyGenerator_v5.Services
{
    public class OperationHistoryService
    {
        private readonly ILogger _logger;
        private readonly string _connectionString;
        private const int BASE_DAYS = 30;
        private const int MAX_DAYS = 730; // 2 года максимум
        private const int STEP_DAYS = 30; // Шаг догрузки
        private static bool _isInitialized = false;
        private static readonly object _initLock = new object();
        private static Task _initializationTask = null;


        /// <summary>
        /// Проверка, инициализирована ли история
        /// </summary>
        public bool IsInitialized() => _isInitialized;


        public OperationHistoryService(ILogger logger)
        {
            _logger = logger;
            var dbPath = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "market_dataMG5.db");
            _connectionString = $"Data Source={dbPath}";
            EnsureTableExists();
        }


        /// <summary>
        /// Инициализация истории операций (выполняется один раз)
        /// </summary>
        public async Task InitializeHistoryAsync(
            IProvirerService provider,
            string accountId,
            List<Models.Position> currentPositions,
            DateTime endDate,
            int initialDays = BASE_DAYS,
            int maxDays = MAX_DAYS)
        {
            lock (_initLock)
            {
                if (_isInitialized)
                {
                    Debug.WriteLine("OperationHistoryService: История уже инициализирована");
                    return;
                }

                if (_initializationTask != null && !_initializationTask.IsCompleted)
                {
                    Debug.WriteLine("OperationHistoryService: Инициализация уже выполняется");
                    return;
                }
            }

            lock (_initLock)
            {
                _initializationTask = Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine("OperationHistoryService: Начало фоновой инициализации...");

                        var result = await LoadHistoryWithAutoBalanceAsync(
                            provider, accountId, currentPositions, endDate, initialDays, maxDays);

                        _isInitialized = true;
                        Debug.WriteLine($"OperationHistoryService: Инициализация завершена. Баланс: {result.IsBalanced}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OperationHistoryService: Ошибка инициализации: {ex.Message}");
                    }
                });
            }

            // Не ждем завершения, чтобы не блокировать UI
            await Task.CompletedTask;
        }


        /// <summary>
        /// Сброс флага инициализации (для тестирования)
        /// </summary>
        public static void ResetInitialization()
        {
            lock (_initLock)
            {
                _isInitialized = false;
                _initializationTask = null;
            }
        }


        /// <summary>
        /// Обновление истории операций (добавление новых операций)
        /// </summary>
        public async Task UpdateHistoryAsync(
            IProvirerService provider,
            string accountId,
            DateTime? from = null)
        {
            if (!_isInitialized)
            {
                Debug.WriteLine("OperationHistoryService: История не инициализирована, пропускаем обновление");
                return;
            }

            try
            {
                var startDate = from ?? DateTime.Now.AddDays(-1);
                var endDate = DateTime.Now;

                Debug.WriteLine($"OperationHistoryService: Обновление истории с {startDate:yyyy-MM-dd}");

                // Загружаем только новые операции
                var operations = await provider.GetOperationsHistoryAsync(accountId, startDate, endDate);

                if (operations.Any())
                {
                    await SaveOperationsAsync(operations);
                    Debug.WriteLine($"OperationHistoryService: Добавлено {operations.Count} новых операций");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OperationHistoryService: Ошибка обновления: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление истории операций (добавление новых операций) с последующей перегруппировкой
        /// </summary>
        public async Task<List<ProcessedOperation>> UpdateHistoryAndReprocessAsync(
            IProvirerService provider,
            string accountId,
            DateTime? from = null)
        {
            if (!_isInitialized)
            {
                Debug.WriteLine("OperationHistoryService: История не инициализирована, пропускаем обновление");
                return new List<ProcessedOperation>();
            }

            try
            {
                var startDate = from ?? DateTime.Now.AddDays(-1);
                var endDate = DateTime.Now;

                Debug.WriteLine($"OperationHistoryService: Обновление истории с {startDate:yyyy-MM-dd}");

                // Загружаем только новые операции
                var operations = await provider.GetOperationsHistoryAsync(accountId, startDate, endDate);

                if (operations.Any())
                {
                    await SaveOperationsAsync(operations);
                    Debug.WriteLine($"OperationHistoryService: Добавлено {operations.Count} новых операций");
                }

                // Всегда перегружаем все операции за последние 30 дней для актуальной группировки
                var allOps = await LoadOperationsAsync(DateTime.Now.AddDays(-1000), DateTime.Now);
                var processedOps = await ProcessOperationsAsync(allOps);

                Debug.WriteLine($"OperationHistoryService: Перегруппировано {processedOps.Count} сделок");
                return processedOps;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OperationHistoryService: Ошибка обновления: {ex.Message}");
                return new List<ProcessedOperation>();
            }
        }

        private void EnsureTableExists()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS OperationsJournal (
                    Id TEXT PRIMARY KEY,
                    ParentOperationId TEXT,
                    Currency TEXT,
                    InstrumentUid TEXT,
                    InstrumentType TEXT,
                    Figi TEXT,
                    InstrumentUidFrom TEXT,
                    InstrumentUidTo TEXT,
                    PositionUid TEXT,
                    Ticker TEXT,
                    AssetUid TEXT,
                    AssetType TEXT,
                    OperationType TEXT,
                    State TEXT,
                    Quantity REAL,
                    QuantityRest REAL,
                    Price REAL,
                    Payment REAL,
                    Commission REAL,
                    Date TEXT,
                    OperationTypeName TEXT,
                    Yield REAL,
                    YieldRelative REAL,
                    AveragePositionPrice REAL,
                    OperationId TEXT,
                    CreatedAt TEXT
                )";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка создания таблицы: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Основной метод загрузки истории с циклической догрузкой до совпадения с текущими позициями
        /// </summary>
        public async Task<(List<Models.Operation> Operations, List<ProcessedOperation> ProcessedOps, bool IsBalanced)>
            LoadHistoryWithAutoBalanceAsync(
            IProvirerService provider,
            string accountId,
            List<Models.Position> currentPositions,
            DateTime endDate,
            int initialDays = BASE_DAYS,
            int maxDays = MAX_DAYS)
        {
            var allOperations = new List<Models.Operation>();
            var allProcessedOps = new List<ProcessedOperation>();
            int currentDays = initialDays;
            bool isBalanced = false;

            Debug.WriteLine($"=== Начало загрузки истории с балансировкой ===");
            Debug.WriteLine($"Текущих позиций: {currentPositions?.Count ?? 0}");

            // Очищаем таблицу перед загрузкой
            await ClearOperationsAsync();

            while (currentDays <= maxDays && !isBalanced)
            {
                var from = endDate.AddDays(-currentDays);
                Debug.WriteLine($"\n--- Загрузка операций за {currentDays} дней (с {from:yyyy-MM-dd} по {endDate:yyyy-MM-dd}) ---");

                try
                {
                    // 1. Загружаем операции из API
                    var operations = await provider.GetOperationsHistoryAsync(accountId, from, endDate);
                    Debug.WriteLine($"Загружено операций из API: {operations.Count}");

                    // 2. Сохраняем в БД
                    await SaveOperationsAsync(operations);
                    Debug.WriteLine($"Операции сохранены в БД");

                    // 3. Загружаем из БД для обработки
                    var loadedOps = await LoadOperationsAsync(from, endDate);
                    Debug.WriteLine($"Загружено из БД: {loadedOps.Count} операций");

                    // 4. Обрабатываем операции (группируем в сделки)
                    var processedOps = await ProcessOperationsAsync(loadedOps);
                    Debug.WriteLine($"Обработано сделок: {processedOps.Count}");

                    // 5. Проверяем баланс с текущими позициями
                    isBalanced = CheckBalance(processedOps, currentPositions);

                    if (isBalanced)
                    {
                        Debug.WriteLine($"✅ БАЛАНС СОШЕЛСЯ за {currentDays} дней!");
                        allOperations = loadedOps;
                        allProcessedOps = processedOps;
                    }
                    else
                    {
                        Debug.WriteLine($"❌ БАЛАНС НЕ СОШЕЛСЯ за {currentDays} дней, увеличиваем период...");
                        currentDays += STEP_DAYS;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка при загрузке за {currentDays} дней: {ex.Message}");
                    currentDays += STEP_DAYS;
                }
            }

            if (!isBalanced)
            {
                Debug.WriteLine($"⚠️ Не удалось достичь баланса даже за {maxDays} дней");
                // Показываем что есть
                var loadedOps = await LoadOperationsAsync(endDate.AddDays(-maxDays), endDate);
                allOperations = loadedOps;
                allProcessedOps = await ProcessOperationsAsync(loadedOps);
            }

            Debug.WriteLine($"\n=== Итог загрузки ===");
            Debug.WriteLine($"Операций: {allOperations.Count}");
            Debug.WriteLine($"Сделок: {allProcessedOps.Count}");
            Debug.WriteLine($"Баланс достигнут: {isBalanced}");

            return (allOperations, allProcessedOps, isBalanced);
        }


        /// <summary>
        /// Проверяет соответствие обработанных операций текущим позициям
        /// </summary>
        private bool CheckBalance(List<ProcessedOperation> processedOps, List<Models.Position> currentPositions)
        {
            if (currentPositions == null || !currentPositions.Any())
            {
                // Если нет текущих позиций, проверяем что все сделки закрыты
                return processedOps.All(o => o.Status == "Closed");
            }

            // Группируем обработанные сделки по инструменту
            var processedByTicker = processedOps
                .Where(o => o.Status.Contains("Open"))
                .GroupBy(o => o.Ticker)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(o => o.Direction == "Long" ? o.Quantity : -o.Quantity)
                );

            // Группируем текущие позиции
            var currentByTicker = currentPositions
                .Where(p => p.Quantity != 0)
                .GroupBy(p => p.Ticker)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(p => p.Quantity)
                );

            Debug.WriteLine("Сравнение балансов:");
            Debug.WriteLine($"  Обработано открытых позиций: {processedByTicker.Count}");
            Debug.WriteLine($"  Текущих позиций: {currentByTicker.Count}");

            // Сравниваем тикеры
            var allTickers = processedByTicker.Keys.Union(currentByTicker.Keys).ToList();

            bool isBalanced = true;
            foreach (var ticker in allTickers)
            {
                var processedQty = processedByTicker.GetValueOrDefault(ticker, 0);
                var currentQty = currentByTicker.GetValueOrDefault(ticker, 0);

                Debug.WriteLine($"  {ticker}: Обработано={processedQty:F0}, Текущая={currentQty:F0}, Разница={Math.Abs(processedQty - currentQty):F0}");

                if (Math.Abs(processedQty - currentQty) > 0.01m)
                {
                    isBalanced = false;
                    Debug.WriteLine($"    ⚠️ РАЗНИЦА для {ticker}: {Math.Abs(processedQty - currentQty):F0}");
                }
            }

            return isBalanced;
        }

        /// <summary>
        /// Получение нескомпенсированных позиций по операциям в БД
        /// </summary>
        public async Task<List<(string Ticker, string InstrumentUid, decimal NetPosition)>> GetUnbalancedPositionsAsync(DateTime startDate, DateTime endDate)
        {
            var unbalanced = new List<(string, string, decimal)>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Ticker, InstrumentUid,
                           SUM(CASE WHEN OperationType = 'BUY' THEN Quantity ELSE 0 END) as TotalBuy,
                           SUM(CASE WHEN OperationType = 'SELL' THEN ABS(Quantity) ELSE 0 END) as TotalSell
                    FROM OperationsJournal 
                    WHERE (OperationType = 'BUY' OR OperationType = 'SELL')
                      AND Date >= @startDate AND Date <= @endDate
                      AND Ticker IS NOT NULL AND Ticker != ''
                    GROUP BY Ticker, InstrumentUid
                    HAVING ABS(TotalBuy - TotalSell) > 0.01";

                cmd.Parameters.AddWithValue("@startDate", startDate.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@endDate", endDate.ToString("yyyy-MM-dd HH:mm:ss"));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var ticker = reader.GetString(0);
                    var instrumentUid = reader.GetString(1);
                    var totalBuy = reader.GetDecimal(2);
                    var totalSell = reader.GetDecimal(3);
                    var netPosition = totalBuy - totalSell;

                    unbalanced.Add((ticker, instrumentUid, netPosition));
                    Debug.WriteLine($"Нескомпенсированная позиция: {ticker} = {netPosition:F0}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка получения нескомпенсированных позиций: {ex.Message}");
            }

            return unbalanced;
        }





        /// <summary>
        /// Сохранение операций в БД
        /// </summary>
        public async Task SaveOperationsAsync(IEnumerable<Models.Operation> operations)
        {
            if (operations == null || !operations.Any())
                return;

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = await connection.BeginTransactionAsync();

            var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = @"
            INSERT OR REPLACE INTO OperationsJournal (
                Id, ParentOperationId, Currency, InstrumentUid, InstrumentType, Figi,
                InstrumentUidFrom, InstrumentUidTo, PositionUid, Ticker, AssetUid, AssetType,
                OperationType, State, Quantity, QuantityRest, Price, Payment, Commission,
                Date, OperationTypeName, Yield, YieldRelative, AveragePositionPrice, OperationId
            ) VALUES (
                @Id, @ParentOperationId, @Currency, @InstrumentUid, @InstrumentType, @Figi,
                @InstrumentUidFrom, @InstrumentUidTo, @PositionUid, @Ticker, @AssetUid, @AssetType,
                @OperationType, @State, @Quantity, @QuantityRest, @Price, @Payment, @Commission,
                @Date, @OperationTypeName, @Yield, @YieldRelative, @AveragePositionPrice, @OperationId
            )";

            insertCommand.Parameters.AddWithValue("@Id", "");
            insertCommand.Parameters.AddWithValue("@ParentOperationId", "");
            insertCommand.Parameters.AddWithValue("@Currency", "");
            insertCommand.Parameters.AddWithValue("@InstrumentUid", "");
            insertCommand.Parameters.AddWithValue("@InstrumentType", "");
            insertCommand.Parameters.AddWithValue("@Figi", "");
            insertCommand.Parameters.AddWithValue("@InstrumentUidFrom", "");
            insertCommand.Parameters.AddWithValue("@InstrumentUidTo", "");
            insertCommand.Parameters.AddWithValue("@PositionUid", "");
            insertCommand.Parameters.AddWithValue("@Ticker", "");
            insertCommand.Parameters.AddWithValue("@AssetUid", "");
            insertCommand.Parameters.AddWithValue("@AssetType", "");
            insertCommand.Parameters.AddWithValue("@OperationType", "");
            insertCommand.Parameters.AddWithValue("@State", "");
            insertCommand.Parameters.AddWithValue("@Quantity", 0);
            insertCommand.Parameters.AddWithValue("@QuantityRest", 0);
            insertCommand.Parameters.AddWithValue("@Price", 0);
            insertCommand.Parameters.AddWithValue("@Payment", 0);
            insertCommand.Parameters.AddWithValue("@Commission", 0);
            insertCommand.Parameters.AddWithValue("@Date", "");
            insertCommand.Parameters.AddWithValue("@OperationTypeName", "");
            insertCommand.Parameters.AddWithValue("@Yield", 0);
            insertCommand.Parameters.AddWithValue("@YieldRelative", 0);
            insertCommand.Parameters.AddWithValue("@AveragePositionPrice", 0);
            insertCommand.Parameters.AddWithValue("@OperationId", "");

            int savedCount = 0;
            foreach (var op in operations)
            {
                insertCommand.Parameters["@Id"].Value = op.Id ?? "";
                insertCommand.Parameters["@ParentOperationId"].Value = op.ParentOperationId ?? "";
                insertCommand.Parameters["@Currency"].Value = op.Currency ?? "";
                insertCommand.Parameters["@InstrumentUid"].Value = op.InstrumentUid ?? "";
                insertCommand.Parameters["@InstrumentType"].Value = op.InstrumentType ?? "";
                insertCommand.Parameters["@Figi"].Value = op.Figi ?? "";
                insertCommand.Parameters["@InstrumentUidFrom"].Value = op.InstrumentUidFrom ?? "";
                insertCommand.Parameters["@InstrumentUidTo"].Value = op.InstrumentUidTo ?? "";
                insertCommand.Parameters["@PositionUid"].Value = op.PositionUid ?? "";
                insertCommand.Parameters["@Ticker"].Value = op.Ticker ?? "";
                insertCommand.Parameters["@AssetUid"].Value = op.AssetUid ?? "";
                insertCommand.Parameters["@AssetType"].Value = op.AssetType ?? "";
                insertCommand.Parameters["@OperationType"].Value = op.OperationType ?? "";
                insertCommand.Parameters["@State"].Value = op.State ?? "";
                insertCommand.Parameters["@Quantity"].Value = op.Quantity;
                insertCommand.Parameters["@QuantityRest"].Value = op.QuantityRest;
                insertCommand.Parameters["@Price"].Value = op.Price;
                insertCommand.Parameters["@Payment"].Value = op.Payment;
                insertCommand.Parameters["@Commission"].Value = op.Commission;
                insertCommand.Parameters["@Date"].Value = op.Date.ToString("yyyy-MM-dd HH:mm:ss");
                insertCommand.Parameters["@OperationTypeName"].Value = op.OperationTypeName ?? "";
                insertCommand.Parameters["@Yield"].Value = op.Yield;
                insertCommand.Parameters["@YieldRelative"].Value = op.YieldRelative;
                insertCommand.Parameters["@AveragePositionPrice"].Value = op.AveragePositionPrice;
                insertCommand.Parameters["@OperationId"].Value = op.OperationId ?? "";

                savedCount += await insertCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            Debug.WriteLine($"Сохранено операций: {savedCount}");
        }

        /// <summary>
        /// Загрузка операций из БД
        /// </summary>
        public async Task<List<Models.Operation>> LoadOperationsAsync(DateTime? from = null, DateTime? to = null, int limit = 10000)
        {
            var operations = new List<Models.Operation>();
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
            SELECT Id, ParentOperationId, Currency, InstrumentUid, InstrumentType, Figi,
                   InstrumentUidFrom, InstrumentUidTo, PositionUid, Ticker, AssetUid, AssetType,
                   OperationType, State, Quantity, QuantityRest, Price, Payment, Commission,
                   Date, OperationTypeName, Yield, YieldRelative, AveragePositionPrice, OperationId
            FROM OperationsJournal 
            WHERE (@from IS NULL OR Date >= @from)
              AND (@to IS NULL OR Date <= @to)
            ORDER BY Date ASC
            LIMIT @limit";

            cmd.Parameters.AddWithValue("@from", from?.ToString("yyyy-MM-dd HH:mm:ss") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@to", to?.ToString("yyyy-MM-dd HH:mm:ss") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var op = new Models.Operation
                {
                    Id = reader.GetString(0),
                    ParentOperationId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Currency = reader.IsDBNull(2) ? null : reader.GetString(2),
                    InstrumentUid = reader.IsDBNull(3) ? null : reader.GetString(3),
                    InstrumentType = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Figi = reader.IsDBNull(5) ? null : reader.GetString(5),
                    InstrumentUidFrom = reader.IsDBNull(6) ? null : reader.GetString(6),
                    InstrumentUidTo = reader.IsDBNull(7) ? null : reader.GetString(7),
                    PositionUid = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Ticker = reader.IsDBNull(9) ? null : reader.GetString(9),
                    AssetUid = reader.IsDBNull(10) ? null : reader.GetString(10),
                    AssetType = reader.IsDBNull(11) ? null : reader.GetString(11),
                    OperationType = reader.IsDBNull(12) ? null : reader.GetString(12),
                    State = reader.IsDBNull(13) ? null : reader.GetString(13),
                    Quantity = reader.GetDecimal(14),
                    QuantityRest = reader.GetDecimal(15),
                    Price = reader.GetDecimal(16),
                    Payment = reader.GetDecimal(17),
                    Commission = reader.GetDecimal(18),
                    Date = reader.GetDateTime(19),
                    OperationTypeName = reader.IsDBNull(20) ? null : reader.GetString(20),
                    Yield = reader.GetDecimal(21),
                    YieldRelative = reader.GetDecimal(22),
                    AveragePositionPrice = reader.GetDecimal(23),
                    OperationId = reader.IsDBNull(24) ? null : reader.GetString(24)
                };
                operations.Add(op);
            }
            return operations;
        }

        /// <summary>
        /// Обработка операций в сделки
        /// </summary>
        public async Task<List<ProcessedOperation>> ProcessOperationsAsync(List<Models.Operation> operations)
        {
            var service = new OperationProcessingService();
            return await service.ProcessOperationsAsync(operations);
        }


        










        public async Task<decimal> CalculateTotalPnLAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT SUM(Payment) as TotalPayment 
                FROM OperationsJournal 
                WHERE OperationType IN ('BUY', 'SELL') 
                  AND State = 'EXECUTED'";

            var result = await cmd.ExecuteScalarAsync();
            return result != DBNull.Value ? Convert.ToDecimal(result) : 0;
        }


        #region Догрузка компенсационных сделок по лишним тикерам
        /// <summary>
        /// Получает список тикеров с нескомпенсированными позициями
        /// </summary>
        public async Task<List<(string Ticker, string InstrumentUid, decimal NetPosition)>> GetUnbalancedTickersAsync(DateTime startDate, DateTime endDate)
        {
            var unbalanced = new List<(string, string, decimal)>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Ticker, InstrumentUid,
                           SUM(CASE WHEN OperationType = 'BUY' THEN Quantity ELSE 0 END) as TotalBuy,
                           SUM(CASE WHEN OperationType = 'SELL' THEN ABS(Quantity) ELSE 0 END) as TotalSell
                    FROM OperationsJournal 
                    WHERE (OperationType = 'BUY' OR OperationType = 'SELL')
                      AND Date >= @startDate AND Date <= @endDate
                      AND Ticker IS NOT NULL
                    GROUP BY Ticker, InstrumentUid
                    HAVING ABS(TotalBuy - TotalSell) > 0.01";

                cmd.Parameters.AddWithValue("@startDate", startDate.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@endDate", endDate.ToString("yyyy-MM-dd HH:mm:ss"));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var ticker = reader.GetString(0);
                    var instrumentUid = reader.GetString(1);
                    var totalBuy = reader.GetDecimal(2);
                    var totalSell = reader.GetDecimal(3);
                    var netPosition = totalBuy - totalSell;

                    Debug.WriteLine($"Нескомпенсированная позиция для {ticker}: BUY={totalBuy:F0}, SELL={totalSell:F0}, Net={netPosition:F0}");
                    unbalanced.Add((ticker, instrumentUid, netPosition));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка получения нескомпенсированных тикеров: {ex.Message}");
            }

            return unbalanced;
        }

        /// <summary>
        /// Получает недостающие операции для балансировки позиции
        /// </summary>
        public async Task<List<Models.Operation>> GetMissingOperationsFromExtendedHistoryAsync(
            string ticker,
            string instrumentUid,
            DateTime startDate,
            List<Models.Operation> extendedOperations)
        {
            var missingOps = new List<Models.Operation>();

            try
            {
                // Фильтруем операции по тикеру
                var tickerOps = extendedOperations
                    .Where(o => o.Ticker == ticker && (o.OperationType == "BUY" || o.OperationType == "SELL"))
                    .OrderBy(o => o.Date)
                    .ToList();

                // Вычисляем позицию до начальной даты
                decimal positionBeforeStart = 0;
                var beforeStartOps = tickerOps.Where(o => o.Date < startDate).ToList();

                foreach (var op in beforeStartOps)
                {
                    if (op.OperationType == "BUY")
                        positionBeforeStart += op.Quantity;
                    else if (op.OperationType == "SELL")
                        positionBeforeStart -= Math.Abs(op.Quantity);
                }

                Debug.WriteLine($"Позиция до {startDate:yyyy-MM-dd} для {ticker}: {positionBeforeStart:F0}");

                // Если позиция не нулевая, ищем недостающие операции
                if (Math.Abs(positionBeforeStart) > 0.01m)
                {
                    // Ищем операции, которые балансируют позицию (свежие перед стартовой датой)
                    decimal remainingPosition = positionBeforeStart;

                    foreach (var op in beforeStartOps.OrderByDescending(o => o.Date))
                    {
                        if (Math.Abs(remainingPosition) < 0.01m) break;

                        var qty = Math.Abs(op.Quantity);
                        var opType = op.OperationType;

                        // Проверяем, подходит ли операция для балансировки
                        if ((remainingPosition > 0 && opType == "BUY") ||
                            (remainingPosition < 0 && opType == "SELL"))
                        {
                            var addQty = Math.Min(qty, Math.Abs(remainingPosition));

                            // Создаем копию операции для балансировки
                            var balanceOp = new Models.Operation
                            {
                                Id = $"{op.Id}_balance",
                                Ticker = op.Ticker,
                                InstrumentUid = op.InstrumentUid,
                                Date = op.Date,
                                Price = op.Price,
                                Quantity = opType == "BUY" ? addQty : -addQty,
                                OperationType = opType,
                                Payment = op.Payment * (addQty / qty),
                                Commission = op.Commission * (addQty / qty),
                                OperationTypeName = op.OperationTypeName,
                                State = op.State,
                                Currency = op.Currency,
                                Figi = op.Figi,
                                InstrumentType = op.InstrumentType
                            };

                            missingOps.Add(balanceOp);
                            remainingPosition -= opType == "BUY" ? addQty : -addQty;

                            Debug.WriteLine($"Добавлена балансирующая операция для {ticker}: {opType} {addQty:F0} по {op.Price:F2}");
                        }
                    }

                    // Если все еще есть остаток, используем фиктивную операцию с нулевой ценой
                    if (Math.Abs(remainingPosition) > 0.01m)
                    {
                        var balanceOp = new Models.Operation
                        {
                            Id = $"BALANCE_{ticker}_{Guid.NewGuid():N}",
                            Ticker = ticker,
                            InstrumentUid = instrumentUid,
                            Date = startDate.AddSeconds(-1),
                            Price = 0,
                            Quantity = remainingPosition,
                            OperationType = remainingPosition > 0 ? "BUY" : "SELL",
                            Payment = 0,
                            Commission = 0,
                            OperationTypeName = "BALANCE",
                            State = "EXECUTED",
                            Currency = "RUB"
                        };
                        missingOps.Add(balanceOp);
                        Debug.WriteLine($"Добавлена фиктивная балансирующая операция для {ticker}: {balanceOp.OperationType} {Math.Abs(remainingPosition):F0} по 0");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка поиска недостающих операций для {ticker}: {ex.Message}");
            }

            return missingOps;
        }

        /// <summary>
        /// Очистка таблицы операций
        /// </summary>
        public async Task ClearOperationsAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM OperationsJournal";
            await cmd.ExecuteNonQueryAsync();

            Debug.WriteLine("Таблица OperationsJournal очищена");
        }
        #endregion







    }
}