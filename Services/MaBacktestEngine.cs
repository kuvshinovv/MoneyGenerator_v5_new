// Файл: Services/MaBacktestEngine.cs
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Models.MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Strategies;
using MoneyGenerator_v5.ViewModels;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace MoneyGenerator_v5.Services
{
    /// <summary>
    /// Движок бэктеста для MA стратегии
    /// ИСПРАВЛЕННАЯ ВЕРСИЯ с ФИКСИРОВАННЫМ размером позиции
    /// </summary>
    public class MaBacktestEngine : IBacktestEngine
    {
        private StrategyViewModel _strategyViewModel;
        private OptimizationDataCache _dataCache;
        private ILogger _logger;
        private bool _disposed = false;

        // Кэш свечей для бэктеста
        private List<Candle> _candles;

        // ✅ КОНСТАНТЫ
        private const decimal INITIAL_BALANCE = 100000m;  // Начальный баланс 100,000 RUB
        private const decimal COMMISSION_RATE = 0.0005m;  // 0.05% комиссия
        private const decimal DEFAULT_LOT_SIZE = 1m;      // Стандартный лот

        // ✅ ✅ ✅ ФИКСИРОВАННЫЙ РАЗМЕР ПОЗИЦИИ (устанавливается при инициализации)
        private decimal _fixedPositionValue = 0m;

        // ✅ ПЕРЕМЕННЫЕ ДЛЯ ОТЛАДКИ
        private int _totalEntryAttempts = 0;
        private int _totalEntries = 0;
        private int _totalExits = 0;

        public async Task InitializeAsync(
            StrategyViewModel strategyViewModel,
            OptimizationDataCache dataCache,
            ILogger logger)
        {
            _strategyViewModel = strategyViewModel ?? throw new ArgumentNullException(nameof(strategyViewModel));
            _dataCache = dataCache ?? throw new ArgumentNullException(nameof(dataCache));
            _logger = logger;

            // Получаем свечи из кэша
            var instrument = _strategyViewModel.Instrument;
            if (_dataCache.Candles != null && _dataCache.Candles.TryGetValue(instrument.Ticker, out var candles))
            {
                _candles = candles?.ToList() ?? new List<Candle>();
                _logger?.LogDebug($"[MaBacktestEngine] Загружено {_candles.Count} свечей для {instrument.Ticker}");

                if (_strategyViewModel.Instrument != null)
                {
                    var inst = _strategyViewModel.Instrument;
                    _logger?.LogDebug($"[MaBacktestEngine] Инструмент: {inst.Ticker}, LotSize: {inst.LotSize}, MinLotSize: {inst.MinLotSize}");
                }
            }
            else
            {
                _candles = new List<Candle>();
                _logger?.LogWarning($"[MaBacktestEngine] Не найдены свечи для {instrument.Ticker}");
            }

            await Task.CompletedTask;
        }

        public async Task<OptimizationResult> RunBacktestAsync(
            Dictionary<string, decimal> parameters,
            CancellationToken cancellationToken = default)
        {
            var result = new OptimizationResult
            {
                Parameters = new Dictionary<string, decimal>(parameters),
                StartDate = DateTime.Now,
                EndDate = DateTime.Now
            };

            try
            {
                if (_candles == null || _candles.Count < 50)
                {
                    _logger?.LogWarning($"[MaBacktestEngine] ❌ Недостаточно данных: {_candles?.Count ?? 0} свечей");
                    result.TotalTrades = 0;
                    result.NetProfit = -999999;
                    return result;
                }

                if (!TryParseParameters(parameters, out var strategyParams, out string errorMsg))
                {
                    _logger?.LogWarning($"[MaBacktestEngine] ❌ Ошибка парсинга параметров: {errorMsg}");
                    result.TotalTrades = 0;
                    result.NetProfit = -999999;
                    return result;
                }


              

                _logger?.LogInformation($"[MaBacktestEngine] Параметры: SMA=[{string.Join(",", strategyParams.SmaPeriods)}], " +
                                       $"EMA=[{string.Join(",", strategyParams.EmaPeriods)}], " +
                                       $"PositionSize={strategyParams.PositionSizePercent}%, " +
                                       $"FilterSMA={strategyParams.FilterSmaPeriod}------------------------------  {result.Parameters.Count}");

                //Debug.WriteLine($"[MaBacktestEngine] Параметры: SMA=[{string.Join(",", strategyParams.SmaPeriods)}], " +
                //                       $"EMA=[{string.Join(",", strategyParams.EmaPeriods)}], " +
                //                       $"PositionSize={strategyParams.PositionSizePercent}%, " + 
                //                       $"FilterSMA={strategyParams.FilterSmaPeriod}   ------------------------------  ");

                // ✅ ✅ ✅ УСТАНАВЛИВАЕМ ФИКСИРОВАННЫЙ РАЗМЕР ПОЗИЦИИ
                // Берем процент от НАЧАЛЬНОГО баланса (не от текущего!)
                _fixedPositionValue = INITIAL_BALANCE * (strategyParams.PositionSizePercent / 100);
                _logger?.LogInformation($"[MaBacktestEngine] Фиксированный размер позиции: {_fixedPositionValue:F2} RUB ({strategyParams.PositionSizePercent}% от {INITIAL_BALANCE:F0})");
                //Debug.WriteLine($"[MaBacktestEngine] Фиксированный размер позиции: {_fixedPositionValue:F2} RUB ({strategyParams.PositionSizePercent}% от {INITIAL_BALANCE:F0})");


                var simulationResult = await SimulateTradingAsync(strategyParams, cancellationToken);

                result.NetProfit = simulationResult.NetProfit;
                result.GrossProfit = simulationResult.GrossProfit;
                result.ProfitFactor = simulationResult.ProfitFactor;
                result.SharpeRatio = simulationResult.SharpeRatio;
                result.MaxDrawdown = simulationResult.MaxDrawdown;
                result.WinRate = simulationResult.WinRate;
                result.TotalTrades = simulationResult.TotalTrades;
                result.WinningTrades = simulationResult.WinningTrades;
                result.LosingTrades = simulationResult.LosingTrades;
                result.AverageWin = simulationResult.AverageWin;
                result.AverageLoss = simulationResult.AverageLoss;
                result.RecoveryFactor = simulationResult.RecoveryFactor;
                result.Expectancy = simulationResult.Expectancy;
                result.StartDate = _candles.FirstOrDefault()?.Time ?? DateTime.Now;
                result.EndDate = _candles.LastOrDefault()?.Time ?? DateTime.Now;
                result.EquityHistory = simulationResult.EquityHistory ?? new List<decimal>();
                result.EquityDates = simulationResult.EquityDates ?? new List<DateTime>();
                result.AnnualReturn = simulationResult.AnnualReturn;

                _logger?.LogInformation($"[MaBacktestEngine] ✅ Результат: P&L={result.NetProfit:F2}, Trades={result.TotalTrades}, WinRate={result.WinRate:F1}%");

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning($"[MaBacktestEngine] ⚠️ Бэктест отменен");
                result.TotalTrades = 0;
                result.NetProfit = -999999;
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"[MaBacktestEngine] ❌ Ошибка выполнения бэктеста");
                result.TotalTrades = 0;
                result.NetProfit = -999999;
                return result;
            }
        }

        private bool TryParseParameters(Dictionary<string, decimal> parameters, out MaStrategyParams result, out string error)
        {
            result = new MaStrategyParams();
            error = "";

            try
            {
                // ✅ Выводим все полученные параметры для отладки
                //Debug.WriteLine($"[MaBacktestEngine] TryParseParameters: Получено {parameters.Count} параметров");
                foreach (var kvp in parameters)
                {
                   // Debug.WriteLine($"  {kvp.Key} = {kvp.Value}");
                }

                // ПАРСИНГ SMA ПЕРИОДОВ
                if (parameters.TryGetValue("SmaShort", out var smaShort) &&
                    parameters.TryGetValue("SmaMedium", out var smaMedium) &&
                    parameters.TryGetValue("SmaLong", out var smaLong))
                {
                    if (smaShort <= 0 || smaMedium <= 0 || smaLong <= 0)
                    {
                        error = "SMA периоды должны быть больше 0";
                        return false;
                    }
                    result.SmaPeriods = new List<int> { (int)smaShort, (int)smaMedium, (int)smaLong };
                    //Debug.WriteLine($"[MaBacktestEngine] SMA из отдельных параметров: [{string.Join(",", result.SmaPeriods)}]");
                }
                else if (parameters.TryGetValue("SmaPeriods", out var smaPeriodsStr))
                {
                    var periods = smaPeriodsStr.ToString()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => int.TryParse(p.Trim(), out var val) ? val : 0)
                        .Where(p => p > 0)
                        .ToList();

                    if (periods.Count >= 3)
                    {
                        result.SmaPeriods = periods.OrderBy(p => p).Take(3).ToList();
                    }
                    else
                    {
                        error = $"Недостаточно SMA периодов: {periods.Count}";
                        return false;
                    }
                }
                else
                {
                    result.SmaPeriods = new List<int> { 10, 20, 30 };
                   // Debug.WriteLine($"[MaBacktestEngine] SMA по умолчанию: [{string.Join(",", result.SmaPeriods)}]");
                }

                // ПАРСИНГ EMA ПЕРИОДОВ
                if (parameters.TryGetValue("EmaPeriods", out var emaPeriodsStr))
                {
                    var periods = emaPeriodsStr.ToString()
                         .Split(',', StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => int.TryParse(p.Trim(), out var val) ? val : 0)
                         .Where(p => p > 0)
                         .ToList();

                    if (periods.Count >= 3)
                    {
                        result.EmaPeriods = periods.OrderBy(p => p).ToList();
                    }
                    else
                    {
                        error = $"Недостаточно EMA периодов: {periods.Count}";
                        return false;
                    }
                }
                else if (parameters.TryGetValue("EmaShort", out var emaShort) &&
                         parameters.TryGetValue("EmaMedium", out var emaMedium) &&
                         parameters.TryGetValue("EmaLong", out var emaLong))
                {
                    if (emaShort <= 0 || emaMedium <= 0 || emaLong <= 0)
                    {
                        error = "EMA периоды должны быть больше 0";
                        return false;
                    }
                    result.EmaPeriods = new List<int> { (int)emaShort, (int)emaMedium, (int)emaLong };
                   // Debug.WriteLine($"[MaBacktestEngine] EMA из отдельных параметров: [{string.Join(",", result.EmaPeriods)}]");
                }
                else
                {
                    result.EmaPeriods = new List<int> { 10, 20, 90 };
                    //Debug.WriteLine($"[MaBacktestEngine] EMA по умолчанию: [{string.Join(",", result.EmaPeriods)}]");
                }

                // ✅ РАЗМЕР ПОЗИЦИИ - ИСПОЛЬЗУЕМ ЗНАЧЕНИЕ ИЗ ПАРАМЕТРОВ
                if (parameters.TryGetValue("PositionSizePercent", out var posSize))
                {
                    result.PositionSizePercent = Math.Clamp(posSize, 0.1m, 100m);
                    //Debug.WriteLine($"[MaBacktestEngine] PositionSizePercent = {result.PositionSizePercent}% (из параметров)");
                }
                else
                {
                    result.PositionSizePercent = 10m;
                    //Debug.WriteLine($"[MaBacktestEngine] PositionSizePercent = 10% (по умолчанию)");
                }

                // ФИЛЬТР SMA
                if (parameters.TryGetValue("FilterSmaPeriod", out var filterSma))
                {
                    result.FilterSmaPeriod = (int)Math.Clamp(filterSma, 1, 200);
                    //Debug.WriteLine($"[MaBacktestEngine] FilterSmaPeriod = {result.FilterSmaPeriod} (из параметров)");
                }
                else
                {
                    result.FilterSmaPeriod = 20;
                    //Debug.WriteLine($"[MaBacktestEngine] FilterSmaPeriod = 20 (по умолчанию)");
                }

                // ✅ ATR ПАРАМЕТРЫ - ИСПОЛЬЗУЕМ ЗНАЧЕНИЯ ИЗ ПАРАМЕТРОВ
                if (parameters.TryGetValue("StopLossATRMultiplier", out var slMultiplier))
                {
                    result.StopLossATRMultiplier = Math.Clamp(slMultiplier, 0.5m, 5.0m);
                    //Debug.WriteLine($"[MaBacktestEngine] StopLossATRMultiplier = {result.StopLossATRMultiplier} (из параметров)");
                }
                else
                {
                    result.StopLossATRMultiplier = 1.0m;
                    //Debug.WriteLine($"[MaBacktestEngine] StopLossATRMultiplier = 2.0 (по умолчанию)");
                }

                if (parameters.TryGetValue("TakeProfitATRMultiplier", out var tpMultiplier))
                {
                    result.TakeProfitATRMultiplier = Math.Clamp(tpMultiplier, 1.0m, 8.0m);
                    //Debug.WriteLine($"[MaBacktestEngine] TakeProfitATRMultiplier = {result.TakeProfitATRMultiplier} (из параметров)");
                }
                else
                {
                    result.TakeProfitATRMultiplier = 2.0m;
                    //Debug.WriteLine($"[MaBacktestEngine] TakeProfitATRMultiplier = 4.0 (по умолчанию)");
                }

                if (parameters.TryGetValue("TrailingStopATRMultiplier", out var tsMultiplier))
                {
                    result.TrailingStopATRMultiplier = Math.Clamp(tsMultiplier, 0.5m, 4.0m);
                    //Debug.WriteLine($"[MaBacktestEngine] TrailingStopATRMultiplier = {result.TrailingStopATRMultiplier} (из параметров)");
                }
                else
                {
                    result.TrailingStopATRMultiplier = 1.0m;
                    //Debug.WriteLine($"[MaBacktestEngine] TrailingStopATRMultiplier = 2.0 (по умолчанию)");
                }

                //Debug.WriteLine($"[MaBacktestEngine] Итоговые ATR: SL={result.StopLossATRMultiplier}, TP={result.TakeProfitATRMultiplier}, TS={result.TrailingStopATRMultiplier}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"Ошибка парсинга: {ex.Message}";
                _logger?.LogError(ex, $"[MaBacktestEngine] Ошибка парсинга параметров");
                Debug.WriteLine($"[MaBacktestEngine] Ошибка парсинга: {ex.Message}");
                return false;
            }
        }
        /// <summary>
        /// ОСНОВНОЙ МЕТОД СИМУЛЯЦИИ ТОРГОВЛИ
        /// Использует ФИКСИРОВАННЫЙ размер позиции (не реинвестирует!)
        /// </summary>
        private async Task<SimulationResult> SimulateTradingAsync(MaStrategyParams parameters, CancellationToken cancellationToken)
        {
            _logger?.LogInformation($"[MaBacktestEngine] SimulateTradingAsync: _candles = {_candles?.Count ?? 0} свечей");

            var result = new SimulationResult();
            var stopwatch = Stopwatch.StartNew();

            // ✅ ПОЛУЧАЕМ ЛОТ ИЗ КЭША (а не из инструмента, который может быть null)
            decimal lotSize = _dataCache?.LotSize ?? DEFAULT_LOT_SIZE;

            try
            {
                if (_candles == null || _candles.Count < 50)
                {
                    _logger?.LogWarning($"[MaBacktestEngine] Недостаточно данных для симуляции");
                    return result;
                }

                int minCandles = Math.Max(parameters.SmaPeriods.Max(), parameters.EmaPeriods.Max()) + 50;
                if (_candles.Count < minCandles)
                {
                    _logger?.LogWarning($"[MaBacktestEngine] Недостаточно данных: {_candles.Count} < {minCandles}");
                    return result;
                }

                // ПРЕДВАРИТЕЛЬНЫЙ РАСЧЕТ ИНДИКАТОРОВ
                _logger?.LogDebug($"[MaBacktestEngine] Расчет индикаторов для {_candles.Count} свечей...");

                var quotes = _candles.Select(c => new Quote
                {
                    Date = c.Time,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume
                }).ToList();

                // РАСЧЕТ SMA
                var smaValues = new Dictionary<int, List<decimal>>();
                foreach (var period in parameters.SmaPeriods)
                {
                    var sma = quotes.GetSma(period).ToList();
                    var values = sma.Where(x => x.Sma.HasValue)
                                    .Select(x => (decimal)x.Sma.Value)
                                    .ToList();
                    smaValues[period] = values;
                }

                // РАСЧЕТ EMA
                var emaValues = new Dictionary<int, List<decimal>>();
                foreach (var period in parameters.EmaPeriods)
                {
                    var ema = quotes.GetEma(period).ToList();
                    var values = ema.Where(x => x.Ema.HasValue)
                                    .Select(x => (decimal)x.Ema.Value)
                                    .ToList();
                    emaValues[period] = values;
                }

                // ✅ РАСЧЕТ ATR
                var atrValues = quotes.GetAtr(14).ToList();
                var atrList = atrValues.Where(x => x.Atr.HasValue)
                                       .Select(x => (decimal)x.Atr.Value)
                                       .ToList();

                int lookback = parameters.SmaPeriods.Max();

                // ИНИЦИАЛИЗАЦИЯ ПЕРЕМЕННЫХ
                List<decimal> trades = new List<decimal>();
                decimal balance = INITIAL_BALANCE;
                decimal maxEquity = balance;
                decimal maxDrawdown = 0;

                // ПЕРЕМЕННЫЕ ДЛЯ ПОЗИЦИИ
                bool inPosition = false;
                decimal entryPrice = 0;
                int entryIndex = 0;
                string positionDirection = "";
                decimal positionLots = 0;
                decimal positionCost = 0;

                // СТАТИСТИКА
                int winningTrades = 0;
                int losingTrades = 0;
                decimal totalProfit = 0;
                decimal totalLoss = 0;
                int totalEntryAttempts = 0;
                int totalSkippedDueToLotSize = 0;

                // ИСТОРИЯ ЭКВИТИ
                List<decimal> equityHistory = new List<decimal>();
                List<DateTime> equityDates = new List<DateTime>();

                int startIndex = Math.Min(lookback + 10, _candles.Count - 1);
                equityHistory.Add(balance);
                equityDates.Add(_candles[startIndex].Time);

                // ПОЛУЧАЕМ РАЗМЕР ЛОТА ИЗ ИНСТРУМЕНТА
                 lotSize = GetLotSizeFromInstrument();
                _logger?.LogInformation($"[MaBacktestEngine] Используем lotSize={lotSize} для расчета позиций");
                _logger?.LogInformation($"[MaBacktestEngine] Фиксированная сумма позиции: {_fixedPositionValue:F2} RUB");
                _logger?.LogInformation($"[MaBacktestEngine] ATR параметры: SL={parameters.StopLossATRMultiplier}, TP={parameters.TakeProfitATRMultiplier}, TS={parameters.TrailingStopATRMultiplier}");

                bool entryAttemptedThisCandle = false;

                // ОСНОВНОЙ ЦИКЛ СИМУЛЯЦИИ
                for (int i = startIndex; i < _candles.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var candle = _candles[i];
                        decimal price = candle.Close;
                        int idx = i - lookback - 10;

                        if (idx < 0 || idx >= emaValues.First().Value.Count)
                            continue;

                        // ПРОВЕРКА НАЛИЧИЯ ВСЕХ ЗНАЧЕНИЙ
                        bool hasAllValues = true;
                        foreach (var kvp in emaValues)
                        {
                            if (idx >= kvp.Value.Count) { hasAllValues = false; break; }
                        }
                        foreach (var kvp in smaValues)
                        {
                            if (idx >= kvp.Value.Count) { hasAllValues = false; break; }
                        }
                        if (!hasAllValues) continue;

                        // ✅ ПОЛУЧЕНИЕ ТЕКУЩЕГО ATR
                        decimal currentAtr = 0;
                        if (idx < atrList.Count && idx > 0)
                        {
                            currentAtr = atrList[idx];
                        }
                        else if (atrList.Count > 0)
                        {
                            currentAtr = atrList.Last();
                        }

                        // ПОЛУЧЕНИЕ ЗНАЧЕНИЙ ИНДИКАТОРОВ
                        var smaShort = smaValues[parameters.SmaPeriods[0]][idx];
                        var smaMedium = smaValues[parameters.SmaPeriods[1]][idx];
                        var smaLong = smaValues[parameters.SmaPeriods[2]][idx];

                        var emaShort = emaValues[parameters.EmaPeriods[0]][idx];
                        var emaMedium = emaValues[parameters.EmaPeriods[1]][idx];
                        var emaLong = emaValues[parameters.EmaPeriods[2]][idx];

                        // ✅ ПРЕДЫДУЩИЕ ЗНАЧЕНИЯ EMA для определения пересечения
                        decimal prevEmaShort = idx > 0 ? emaValues[parameters.EmaPeriods[0]][idx - 1] : emaShort;
                        decimal prevEmaMedium = idx > 0 ? emaValues[parameters.EmaPeriods[1]][idx - 1] : emaMedium;

                        bool isBullish = smaShort > smaMedium && smaMedium > smaLong;
                        bool isBearish = smaShort < smaMedium && smaMedium < smaLong;

                        decimal smaFilter = 0;
                        if (parameters.FilterSmaPeriod > 0 && smaValues.TryGetValue(parameters.FilterSmaPeriod, out var filterValues))
                        {
                            if (idx < filterValues.Count)
                                smaFilter = filterValues[idx];
                        }

                        entryAttemptedThisCandle = false;

                        // ============================================================
                        // ЛОГИКА ВХОДА
                        // ============================================================
                        if (!inPosition)
                        {
                            bool entrySignal = false;
                            string signal = "";

                            // ПРОВЕРКА СИГНАЛА НА ПОКУПКУ (LONG)
                            if (isBullish && (smaFilter == 0 || price > smaFilter))
                            {
                                bool emaBullish = emaShort > emaMedium && emaMedium > emaLong;
                                if (emaBullish)
                                {
                                    bool priceAtEmaLong = Math.Abs(price - emaLong) / emaLong < 0.005m;
                                    bool priceAtEmaMedium = Math.Abs(price - emaMedium) / emaMedium < 0.003m;

                                    if (priceAtEmaLong || priceAtEmaMedium)
                                    {
                                        entrySignal = true;
                                        signal = "LONG";
                                    }
                                }
                            }
                            // ПРОВЕРКА СИГНАЛА НА ПРОДАЖУ (SHORT)
                            else if (isBearish && (smaFilter == 0 || price < smaFilter))
                            {
                                bool emaBearish = emaShort < emaMedium && emaMedium < emaLong;
                                if (emaBearish)
                                {
                                    bool priceAtEmaLong = Math.Abs(price - emaLong) / emaLong < 0.005m;
                                    bool priceAtEmaMedium = Math.Abs(price - emaMedium) / emaMedium < 0.003m;

                                    if (priceAtEmaLong || priceAtEmaMedium)
                                    {
                                        entrySignal = true;
                                        signal = "SHORT";
                                    }
                                }
                            }

                            if (entrySignal && !entryAttemptedThisCandle)
                            {
                                entryAttemptedThisCandle = true;
                                totalEntryAttempts++;

                                decimal positionValueRub = _fixedPositionValue;
                                decimal calculatedLots = Math.Floor(positionValueRub / (price * lotSize));

                                if (calculatedLots <= 0)
                                {
                                    totalSkippedDueToLotSize++;
                                    continue;
                                }

                                positionLots = calculatedLots;
                                positionCost = positionLots * price * lotSize;

                                if (balance < positionCost)
                                {
                                    positionLots = Math.Floor(balance / (price * lotSize));
                                    positionCost = positionLots * price * lotSize;
                                    if (positionLots <= 0) continue;
                                }

                                entryPrice = price;
                                entryIndex = i;
                                positionDirection = signal;

                                // ✅ ИСПРАВЛЕНИЕ: ТОЛЬКО списываем стоимость позиции
                                balance -= positionCost;

                                // Комиссия при входе (сохраняем для статистики)
                                decimal entryCommission = positionCost * COMMISSION_RATE;
                                balance -= entryCommission;  // Списываем комиссию при входе

                                inPosition = true;
                                _totalEntries++;

                                //Debug.WriteLine($"[MaBacktestEngine] 📈 ВХОД {signal}: " +
                                //                $"позиция={positionLots} лотов, " +
                                //                $"стоимость={positionCost:F2}, " +
                                 //               $"комиссия входа={entryCommission:F2}, " +
                                 //               $"баланс={balance:F2}");
                            }
                        }
                        // ============================================================
                        // ЛОГИКА ВЫХОДА (С ИСПОЛЬЗОВАНИЕМ ATR)
                        // ============================================================
                        else if (inPosition)
                        {
                            bool shouldExit = false;
                            string exitReason = "";

                            // ✅ ОТЛАДКА: Выводим текущее состояние
                            //Debug.WriteLine($"[MaBacktestEngine] 🔍 Проверка выхода: индекс={i}, время в позиции={i - entryIndex} свечей, ATR={currentAtr:F4}");

                            // Минимальное время удержания (5 свечей)
                            if (i - entryIndex < 5)
                            {
                               // Debug.WriteLine($"[MaBacktestEngine] ⏳ Минимальное время удержания: {i - entryIndex}/5 свечей");
                                // ✅ НЕ ПРОДОЛЖАЕМ, а просто пропускаем проверку
                            }
                            else if (currentAtr > 0)
                            {
                                // ✅ УРОВНИ НА ОСНОВЕ ATR
                                decimal stopLossPrice;
                                decimal takeProfitPrice;

                                if (positionDirection == "LONG")
                                {
                                    stopLossPrice = entryPrice - currentAtr * parameters.StopLossATRMultiplier;
                                    takeProfitPrice = entryPrice + currentAtr * parameters.TakeProfitATRMultiplier;

                                    //Debug.WriteLine($"[MaBacktestEngine] LONG: SL={stopLossPrice:F4}, TP={takeProfitPrice:F4}, цена={price:F4}");
                                }
                                else // SHORT
                                {
                                    stopLossPrice = entryPrice + currentAtr * parameters.StopLossATRMultiplier;
                                    takeProfitPrice = entryPrice - currentAtr * parameters.TakeProfitATRMultiplier;

                                   // Debug.WriteLine($"[MaBacktestEngine] SHORT: SL={stopLossPrice:F4}, TP={takeProfitPrice:F4}, цена={price:F4}");
                                }

                                // ✅ Проверка СТОП-ЛОССА
                                if (positionDirection == "LONG" && price <= stopLossPrice)
                                {
                                    shouldExit = true;
                                    exitReason = $"Стоп-лосс по ATR (SL={parameters.StopLossATRMultiplier}xATR)";
                                    //Debug.WriteLine($"[MaBacktestEngine] 🛑 СТОП-ЛОСС LONG: цена={price:F4} <= {stopLossPrice:F4}");
                                }
                                else if (positionDirection == "SHORT" && price >= stopLossPrice)
                                {
                                    shouldExit = true;
                                    exitReason = $"Стоп-лосс по ATR (SL={parameters.StopLossATRMultiplier}xATR)";
                                    //Debug.WriteLine($"[MaBacktestEngine] 🛑 СТОП-ЛОСС SHORT: цена={price:F4} >= {stopLossPrice:F4}");
                                }
                                // ✅ Проверка ТЕЙК-ПРОФИТА
                                else if (positionDirection == "LONG" && price >= takeProfitPrice)
                                {
                                    shouldExit = true;
                                    exitReason = $"Тейк-профит по ATR (TP={parameters.TakeProfitATRMultiplier}xATR)";
                                    //Debug.WriteLine($"[MaBacktestEngine] 🎯 ТЕЙК-ПРОФИТ LONG: цена={price:F4} >= {takeProfitPrice:F4}");
                                }
                                else if (positionDirection == "SHORT" && price <= takeProfitPrice)
                                {
                                    shouldExit = true;
                                    exitReason = $"Тейк-профит по ATR (TP={parameters.TakeProfitATRMultiplier}xATR)";
                                    //Debug.WriteLine($"[MaBacktestEngine] 🎯 ТЕЙК-ПРОФИТ SHORT: цена={price:F4} <= {takeProfitPrice:F4}");
                                }
                                // ✅ Проверка ПРОБОЯ EMA
                                else if (positionDirection == "LONG" && price < emaMedium * 0.995m)
                                {
                                    shouldExit = true;
                                    exitReason = "Пробой средней EMA вниз";
                                    //Debug.WriteLine($"[MaBacktestEngine] 📉 ПРОБОЙ EMA LONG: цена={price:F4} < {emaMedium * 0.995m:F4}");
                                }
                                else if (positionDirection == "SHORT" && price > emaMedium * 1.005m)
                                {
                                    shouldExit = true;
                                    exitReason = "Пробой средней EMA вверх";
                                    //Debug.WriteLine($"[MaBacktestEngine] 📈 ПРОБОЙ EMA SHORT: цена={price:F4} > {emaMedium * 1.005m:F4}");
                                }
                                // ✅ Проверка ПЕРЕСЕЧЕНИЯ EMA
                                else if (positionDirection == "LONG" && emaShort < emaMedium && prevEmaShort > prevEmaMedium)
                                {
                                    shouldExit = true;
                                    exitReason = "Пересечение EMA (короткая ниже средней)";
                                    //Debug.WriteLine($"[MaBacktestEngine] 🔀 ПЕРЕСЕЧЕНИЕ EMA LONG: emaShort={emaShort:F4} < emaMedium={emaMedium:F4}");
                                }
                                else if (positionDirection == "SHORT" && emaShort > emaMedium && prevEmaShort < prevEmaMedium)
                                {
                                    shouldExit = true;
                                    exitReason = "Пересечение EMA (короткая выше средней)";
                                    //Debug.WriteLine($"[MaBacktestEngine] 🔀 ПЕРЕСЕЧЕНИЕ EMA SHORT: emaShort={emaShort:F4} > emaMedium={emaMedium:F4}");
                                }
                                // ✅ Проверка СМЕНЫ ТРЕНДА
                                else if (positionDirection == "LONG" && isBearish)
                                {
                                    shouldExit = true;
                                    exitReason = "Смена тренда на медвежий";
                                    //Debug.WriteLine($"[MaBacktestEngine] 🔄 СМЕНА ТРЕНДА LONG -> медвежий");
                                }
                                else if (positionDirection == "SHORT" && isBullish)
                                {
                                    shouldExit = true;
                                    exitReason = "Смена тренда на бычий";
                                    //Debug.WriteLine($"[MaBacktestEngine] 🔄 СМЕНА ТРЕНДА SHORT -> бычий");
                                }
                                // ✅ ТРЕЙЛИНГ-СТОП ПО ATR
                                else
                                {
                                    decimal priceChangePercent = 0;
                                    if (entryPrice > 0)
                                    {
                                        if (positionDirection == "LONG")
                                        {
                                            priceChangePercent = (price - entryPrice) / entryPrice * 100;
                                        }
                                        else
                                        {
                                            priceChangePercent = (entryPrice - price) / entryPrice * 100;
                                        }
                                    }

                                    //Debug.WriteLine($"[MaBacktestEngine] 📊 Прибыль: {priceChangePercent:F2}%");

                                    if (priceChangePercent > 1.0m)
                                    {
                                        decimal trailingStopPrice;
                                        if (positionDirection == "LONG")
                                        {
                                            decimal highestPrice = Math.Max(price, entryPrice);
                                            trailingStopPrice = highestPrice - currentAtr * parameters.TrailingStopATRMultiplier;
                                            if (price < trailingStopPrice)
                                            {
                                                shouldExit = true;
                                                exitReason = $"Трейлинг-стоп (TS={parameters.TrailingStopATRMultiplier}xATR)";
                                                //Debug.WriteLine($"[MaBacktestEngine] 🏃 ТРЕЙЛИНГ-СТОП LONG: цена={price:F4} < {trailingStopPrice:F4}");
                                            }
                                        }
                                        else // SHORT
                                        {
                                            decimal lowestPrice = Math.Min(price, entryPrice);
                                            trailingStopPrice = lowestPrice + currentAtr * parameters.TrailingStopATRMultiplier;
                                            if (price > trailingStopPrice)
                                            {
                                                shouldExit = true;
                                                exitReason = $"Трейлинг-стоп (TS={parameters.TrailingStopATRMultiplier}xATR)";
                                                //Debug.WriteLine($"[MaBacktestEngine] 🏃 ТРЕЙЛИНГ-СТОП SHORT: цена={price:F4} > {trailingStopPrice:F4}");
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Debug.WriteLine($"[MaBacktestEngine] ⚠️ ATR = 0, пропускаем проверку");
                            }

                            if (shouldExit)
                            {
                                // ✅ РАСЧЕТ P&L
                                decimal pnl;
                                if (positionDirection == "LONG")
                                    pnl = (price - entryPrice) * positionLots * lotSize;
                                else
                                    pnl = (entryPrice - price) * positionLots * lotSize;

                                // ✅ Комиссия при выходе
                                decimal exitValue = positionLots * price * lotSize;
                                decimal exitCommission = exitValue * COMMISSION_RATE;
                                decimal entryCommission = positionCost * COMMISSION_RATE;
                                decimal totalCommission = entryCommission + exitCommission;

                                // ✅ ИТОГОВЫЙ P&L (с учетом ВСЕХ комиссий)
                                decimal pnlAfterCommission = pnl - totalCommission;

                                // ✅ ИСПРАВЛЕНИЕ: ВОЗВРАЩАЕМ стоимость позиции + P&L - комиссия выхода
                                balance += positionCost + pnl - exitCommission;

                                //Debug.WriteLine($"[MaBacktestEngine] 💰 Комиссия: вход={entryCommission:F2}, выход={exitCommission:F2}, всего={totalCommission:F2}");
                               // Debug.WriteLine($"[MaBacktestEngine] 📊 P&L: до комиссии={pnl:F2}, после={pnlAfterCommission:F2}");
                                //Debug.WriteLine($"[MaBacktestEngine] 💰 Баланс: {balance:F2}");

                                // ✅ Для статистики используем pnl (ДО комиссии)
                                trades.Add(pnlAfterCommission);  // Для расчета NetProfit
                                if (pnl > 0)
                                {
                                    winningTrades++;
                                    totalProfit += pnl;  // GrossProfit = сумма положительных ДО комиссий
                                }
                                else
                                {
                                    losingTrades++;
                                    totalLoss += Math.Abs(pnl);  // GrossLoss = сумма отрицательных ДО комиссий
                                }

                                // Обновляем максимальную просадку
                                if (balance > maxEquity)
                                    maxEquity = balance;
                                decimal drawdown = (maxEquity - balance) / maxEquity * 100;
                                if (drawdown > maxDrawdown)
                                    maxDrawdown = drawdown;

                                _totalExits++;
                                _logger?.LogDebug($"[MaBacktestEngine] 📉 ВЫХОД {positionDirection}: " +
                                                  $"P&L до комиссии={pnl:F2}, " +
                                                  $"комиссия={totalCommission:F2}, " +
                                                  $"P&L после={pnlAfterCommission:F2}, " +
                                                  $"баланс {balance:F2}, " +
                                                  $"причина: {exitReason}");

                                // Сбрасываем позицию
                                inPosition = false;
                                positionDirection = "";
                                entryPrice = 0;
                                entryIndex = 0;
                                positionLots = 0;
                                positionCost = 0;
                            }
                        }

                        // Сохраняем эквити каждые 5 свечей
                        if (i % 10 == 0 || i == _candles.Count - 1)
                        {
                            decimal currentEquity = inPosition ? balance + positionCost : balance;
                            equityHistory.Add(currentEquity);
                            equityDates.Add(_candles[i].Time);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, $"[MaBacktestEngine] ⚠️ Ошибка на итерации {i}: {ex.Message}");
                    }
                }

                // ЗАКРЫВАЕМ ОТКРЫТУЮ ПОЗИЦИЮ В КОНЦЕ
                if (inPosition && _candles.Count > 0 && positionLots > 0)
                {
                    var lastCandle = _candles.Last();
                    decimal closePrice = lastCandle.Close;

                    decimal pnl;
                    if (positionDirection == "LONG")
                        pnl = (closePrice - entryPrice) * positionLots * lotSize;
                    else
                        pnl = (entryPrice - closePrice) * positionLots * lotSize;

                    decimal exitValue = positionLots * closePrice * lotSize;
                    decimal exitCommission = exitValue * COMMISSION_RATE;
                    decimal entryCommission = positionCost * COMMISSION_RATE;
                    decimal totalCommission = entryCommission + exitCommission;
                    decimal pnlAfterCommission = pnl - totalCommission;

                    // ✅ ИСПРАВЛЕНИЕ: ВОЗВРАЩАЕМ стоимость позиции + P&L - комиссия выхода
                    balance += positionCost + pnl - exitCommission;

                    trades.Add(pnlAfterCommission);
                    if (pnl > 0)
                    {
                        winningTrades++;
                        totalProfit += pnl;
                    }
                    else
                    {
                        losingTrades++;
                        totalLoss += Math.Abs(pnl);
                    }

                    _logger?.LogDebug($"[MaBacktestEngine] 📉 Принудительный выход: " +
                                      $"P&L={pnlAfterCommission:F2}, " +
                                      $"баланс={balance:F2}");
                }

                // ЛОГИРУЕМ СТАТИСТИКУ
                _logger?.LogInformation($"[MaBacktestEngine] 📊 Статистика симуляции:");
                _logger?.LogInformation($"[MaBacktestEngine]    - Фиксированная позиция: {_fixedPositionValue:F2} RUB");
                _logger?.LogInformation($"[MaBacktestEngine]    - ATR параметры: SL={parameters.StopLossATRMultiplier}, TP={parameters.TakeProfitATRMultiplier}, TS={parameters.TrailingStopATRMultiplier}");
                _logger?.LogInformation($"[MaBacktestEngine]    - Всего попыток входа: {totalEntryAttempts}");
                _logger?.LogInformation($"[MaBacktestEngine]    - Пропущено из-за размера: {totalSkippedDueToLotSize}");
                _logger?.LogInformation($"[MaBacktestEngine]    - Успешных входов: {_totalEntries}");
                _logger?.LogInformation($"[MaBacktestEngine]    - Сделок: {trades.Count}");
                _logger?.LogInformation($"[MaBacktestEngine]    - Итоговый баланс: {balance:F2} RUB");
                _logger?.LogInformation($"[MaBacktestEngine]    - P&L: {balance - INITIAL_BALANCE:F2} RUB");
                _logger?.LogInformation($"[MaBacktestEngine]    - WinRate: {(trades.Count > 0 ? (decimal)winningTrades / trades.Count * 100 : 0):F1}%");
                _logger?.LogInformation($"[MaBacktestEngine]    - Макс. просадка: {maxDrawdown:F1}%");
                _logger?.LogInformation($"[MaBacktestEngine]    - Время выполнения: {stopwatch.ElapsedMilliseconds} мс");

                // ФОРМИРУЕМ РЕЗУЛЬТАТ
                result.NetProfit = balance - INITIAL_BALANCE;
                result.GrossProfit = totalProfit;
                result.TotalTrades = trades.Count;
                result.WinningTrades = winningTrades;
                result.LosingTrades = losingTrades;
                result.WinRate = trades.Count > 0 ? (decimal)winningTrades / trades.Count * 100 : 0;
                result.AverageWin = winningTrades > 0 ? totalProfit / winningTrades : 0;
                result.AverageLoss = losingTrades > 0 ? totalLoss / losingTrades : 0;
                result.ProfitFactor = totalLoss > 0 ? totalProfit / totalLoss : (totalProfit > 0 ? 999 : 0);
                result.MaxDrawdown = maxDrawdown;
                result.Expectancy = trades.Count > 0 ? trades.Average() : 0;
                result.RecoveryFactor = result.MaxDrawdown > 0 ? result.NetProfit / (result.MaxDrawdown / 100 * INITIAL_BALANCE) : 0;
                result.EquityHistory = equityHistory;
                result.EquityDates = equityDates;



                // ✅ ✅ ✅ РАСЧЕТ ГОДОВОЙ ДОХОДНОСТИ
                if (_candles != null && _candles.Count > 0 && INITIAL_BALANCE > 0)
                {
                   // Debug.WriteLine($"[MaBacktestEngine] --------------------   ");

                    var firstCandle = _candles.FirstOrDefault();
                    var lastCandle = _candles.LastOrDefault();

                    if (firstCandle != null && lastCandle != null)
                    {
                        double days = (lastCandle.Time - firstCandle.Time).TotalDays;
                        if (days < 1) days = 1;

                        double totalReturn = (double)(balance / INITIAL_BALANCE);
                        double annualReturn = (Math.Pow(totalReturn, 365.0 / days) - 1) * 100;

                        result.AnnualReturn = (decimal)annualReturn;

                    }
                }



                // РАСЧЕТ КОЭФФИЦИЕНТА ШАРПА
                if (trades.Count > 1)
                {
                    decimal avgReturn = trades.Average();
                    double sumSquaredDiffs = 0;
                    foreach (var trade in trades)
                    {
                        double diff = (double)(trade - avgReturn);
                        sumSquaredDiffs += diff * diff;
                    }
                    double variance = sumSquaredDiffs / trades.Count;
                    double stdDevDouble = Math.Sqrt(variance);
                    decimal stdDev = (decimal)stdDevDouble;

                    if (stdDev > 0)
                    {
                        double sharpeDouble = ((double)avgReturn / stdDevDouble) * Math.Sqrt(252);
                        result.SharpeRatio = (decimal)sharpeDouble;
                    }
                    else
                    {
                        result.SharpeRatio = 0m;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"[MaBacktestEngine] ❌ Критическая ошибка в SimulateTradingAsync");
                return result;
            }
        }

        /// <summary>
        /// Получение размера лота из инструмента
        /// </summary>
        private decimal GetLotSizeFromInstrument()
        {
            try
            {
                // ✅ СНАЧАЛА ПРОВЕРЯЕМ КЭШ
                if (_dataCache != null && _dataCache.LotSize > 0)
                {
                    _logger?.LogDebug($"[MaBacktestEngine] Используем LotSize={_dataCache.LotSize} из кэша");
                    return _dataCache.LotSize;
                }

                // Если в кэше нет - пробуем из инструмента
                if (_strategyViewModel?.Instrument != null)
                {
                    var instrument = _strategyViewModel.Instrument;
                    if (instrument.LotSize > 0)
                    {
                        _logger?.LogDebug($"[MaBacktestEngine] Получен LotSize={instrument.LotSize} из инструмента {instrument.Ticker}");
                        return instrument.LotSize;
                    }
                }

                _logger?.LogDebug($"[MaBacktestEngine] Используем DEFAULT_LOT_SIZE={DEFAULT_LOT_SIZE}");
                return DEFAULT_LOT_SIZE;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"[MaBacktestEngine] Ошибка получения LotSize");
                return DEFAULT_LOT_SIZE;
            }
        }

        public IEnumerable<string> GetSupportedParameters()
        {
            return new[]
            {
                "SmaShort",
                "SmaMedium",
                "SmaLong",
                "EmaShort",
                "EmaMedium",
                "EmaLong",
                "EmaPeriods",
                "PositionSizePercent",
                "FilterSmaPeriod",
                // ✅ НОВЫЕ ПАРАМЕТРЫ
                "StopLossATRMultiplier",
                "TakeProfitATRMultiplier",
                "TrailingStopATRMultiplier"
            };
        }

        public bool ValidateParameters(Dictionary<string, decimal> parameters)
        {
            if (parameters == null || !parameters.Any())
                return false;

            try
            {
                return TryParseParameters(parameters, out _, out _);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _candles = null;
            _strategyViewModel = null;
            _logger = null;
            _dataCache = null;
            GC.SuppressFinalize(this);
        }

        private class MaStrategyParams
        {
            public List<int> SmaPeriods { get; set; } = new List<int>();
            public List<int> EmaPeriods { get; set; } = new List<int>();
            public decimal PositionSizePercent { get; set; } = 10m;
            public int FilterSmaPeriod { get; set; } = 20;
            public decimal StopLossATRMultiplier { get; internal set; } = 1.0m;
            public decimal TakeProfitATRMultiplier { get; internal set; } = 2.0m;
            public decimal TrailingStopATRMultiplier { get; internal set; } = 1.0m;
        }

        private class SimulationResult
        {
            public decimal NetProfit { get; set; }
            public decimal GrossProfit { get; set; }
            public decimal ProfitFactor { get; set; }
            public decimal SharpeRatio { get; set; }
            public decimal MaxDrawdown { get; set; }
            public decimal WinRate { get; set; }
            public int TotalTrades { get; set; }
            public int WinningTrades { get; set; }
            public int LosingTrades { get; set; }
            public decimal AverageWin { get; set; }
            public decimal AverageLoss { get; set; }
            public decimal RecoveryFactor { get; set; }
            public decimal Expectancy { get; set; }
            public List<decimal> EquityHistory { get; set; } = new List<decimal>();
            public List<DateTime> EquityDates { get; set; } = new List<DateTime>();

            public decimal AnnualReturn { get; set; }
            public string FormattedAnnualReturn => AnnualReturn != 0 ? $"{AnnualReturn:F2}%" : "0.00%";
        }
    }
}