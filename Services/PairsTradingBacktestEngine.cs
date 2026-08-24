using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Models.MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Strategies;
using MoneyGenerator_v5.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MoneyGenerator_v5.Services
{
    /// <summary>
    /// Движок бэктеста для парной торговли (без вызовов API)
    /// </summary>
    public class PairsTradingBacktestEngine : IBacktestEngine
    {
        private readonly ILogger _logger;
        private readonly IProvirerService _provider; // ✅ ДОБАВЛЕНО: провайдер для TransactionsService
        private StrategyViewModel _strategyViewModel;
        private OptimizationDataCache _dataCache;
        private PairsTradingStrategy _strategy;
        private bool _disposed = false;

        private const decimal INITIAL_CAPITAL = 100000m;

        public PairsTradingBacktestEngine(ILogger logger = null, IProvirerService provider = null)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            _provider = provider; // ✅ ДОБАВЛЕНО: сохраняем провайдер
        }

        public async Task InitializeAsync(
            StrategyViewModel strategyViewModel,
            OptimizationDataCache dataCache,
            ILogger logger)
        {
            _strategyViewModel = strategyViewModel ?? throw new ArgumentNullException(nameof(strategyViewModel));
            _dataCache = dataCache ?? throw new ArgumentNullException(nameof(dataCache));

            if (!_dataCache.IsLoaded)
            {
                throw new InvalidOperationException("Data cache is not loaded. Call PrepareDataAsync first.");
            }

            // ✅ Создаем изолированную стратегию (с провайдером для TransactionsService)
            _strategy = CreateIsolatedStrategy();

            // ✅ Инициализируем стратегию с кэшированными данными
            await InitializeStrategyWithCacheAsync();

            _logger.LogInformation($"PairsTradingBacktestEngine initialized. Aligned data: {_dataCache.AlignedData?.Count ?? 0} points");
        }

        /// <summary>
        /// Создает изолированную стратегию (без подписок, но с провайдером для TransactionsService)
        /// </summary>
        private PairsTradingStrategy CreateIsolatedStrategy()
        {
            Debug.WriteLine("[PairsTradingBacktestEngine] CreateIsolatedStrategy() НАЧАЛО");

            var nullLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<PairsTradingStrategy>.Instance;

            // ✅ ИСПРАВЛЕНИЕ: Создаем TransactionsService с провайдером
            // Для бэктеста провайдер нужен только для получения данных, но не для отправки ордеров
            var backtestTransactions = new TransactionsService(
                _provider, // ✅ Передаем провайдер (может быть null, но TransactionsService проверит)
                null, // mainViewModel - null для бэктеста
                _strategyViewModel,
                _strategyViewModel.Instrument,
                _strategyViewModel.SelectedAccount ?? new Account { Id = "backtest" },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionsService>.Instance);

            // ✅ ИСПРАВЛЕНИЕ: Используем правильный конструктор с IProvirerService
            var strategy = new PairsTradingStrategy(
                nullLogger,
                _provider, // ✅ Передаем провайдер в стратегию
                _strategyViewModel,
                backtestTransactions,
                null);

            // ✅ Устанавливаем бэктест-режим и свечи
            SetPrivateField(strategy, "_isBacktestMode", true);





            // ✅ Получаем актуальные инструменты из параметров стратегии
            /*var pairsStrategy = _strategyViewModel.PairsStrategy;
            if (pairsStrategy != null && pairsStrategy.Parameters != null)
            {
                // Создаем Instrument для A (по тикеру из параметров)
                var instrumentA = new Models.Instrument
                {
                    Ticker = pairsStrategy.Parameters.FirstInstrumentTicker,
                    Uid = pairsStrategy.Parameters.FirstInstrumentUid
                };

                // Создаем Instrument для B (по тикеру из параметров)
                var instrumentB = new Models.Instrument
                {
                    Ticker = pairsStrategy.Parameters.PairInstrumentTicker,
                    Uid = pairsStrategy.Parameters.PairInstrumentUid
                };

                Debug.WriteLine($"[PairsTradingBacktestEngine] Инструменты из параметров: A={instrumentA.Ticker}, B={instrumentB.Ticker}");

                SetPrivateField(strategy, "_cachedInstrumentsA", instrumentA);
                SetPrivateField(strategy, "_cachedInstrumentsB", instrumentB);
            }
            else
            {
                // Fallback - используем Instrument из StrategyViewModel
                Debug.WriteLine($"[PairsTradingBacktestEngine] ВНИМАНИЕ! PairsStrategy или Parameters null, используем Instrument из StrategyViewModel: {_strategyViewModel.Instrument?.Ticker}");
                SetPrivateField(strategy, "_cachedInstrumentsA", _strategyViewModel.Instrument);
                SetPrivateField(strategy, "_cachedInstrumentsB", _strategyViewModel.Instrument);
            }*/


            // ✅ ПОЛУЧАЕМ АКТУАЛЬНЫЕ ПАРАМЕТРЫ ИЗ ОРИГИНАЛЬНОЙ СТРАТЕГИИ
            // Это те параметры, которые пользователь видит и изменяет в UI
            var pairsStrategy = _strategyViewModel.PairsStrategy;

            string tickerA = "IMOEXF";
            string uidA = null;
            string tickerB = _strategyViewModel.Instrument.Ticker;
            string uidB = _strategyViewModel.Instrument.Uid;

            if (pairsStrategy != null && pairsStrategy.Parameters != null)
            {
                // ✅ Берем тикер A из параметров (пользователь мог его изменить)
                if (!string.IsNullOrEmpty(pairsStrategy.Parameters.FirstInstrumentTicker))
                {
                    tickerA = pairsStrategy.Parameters.FirstInstrumentTicker;
                    uidA = pairsStrategy.Parameters.FirstInstrumentUid;
                    Debug.WriteLine($"[PairsTradingBacktestEngine] Инструмент A из параметров стратегии: {tickerA}");
                }

                // ✅ Берем тикер B из параметров (пользователь мог его изменить)
                if (!string.IsNullOrEmpty(pairsStrategy.Parameters.PairInstrumentTicker))
                {
                    tickerB = pairsStrategy.Parameters.PairInstrumentTicker;
                    uidB = pairsStrategy.Parameters.PairInstrumentUid;
                    Debug.WriteLine($"[PairsTradingBacktestEngine] Инструмент B из параметров стратегии: {tickerB}");
                }
            }

            Debug.WriteLine($"[PairsTradingBacktestEngine] Итоговые инструменты: A={tickerA}, B={tickerB}");

            // ✅ Создаем Instrument для A
            var instrumentA = new Models.Instrument
            {
                Ticker = tickerA,
                Uid = uidA
            };

            // ✅ Создаем Instrument для B
            var instrumentB = new Models.Instrument
            {
                Ticker = tickerB,
                Uid = uidB
            };

            SetPrivateField(strategy, "_cachedInstrumentsA", instrumentA);
            SetPrivateField(strategy, "_cachedInstrumentsB", instrumentB);








            // Устанавливаем выровненные свечи
            if (_dataCache.AlignedData != null && _dataCache.AlignedData.Any())
            {
                // Используем тикеры из параметров
                string cacheTickerA = _dataCache.InstrumentATicker ?? tickerA;
                string cacheTickerB = _dataCache.InstrumentBTicker ?? tickerB;

                Debug.WriteLine($"[PairsTradingBacktestEngine] Используем тикеры для свечей: A={cacheTickerA}, B={cacheTickerB}");


                var candlesA = _dataCache.AlignedData.Select(d => new Candle
                {
                    Time = d.Time,
                    Close = d.PriceA,
                    Open = d.PriceA,
                    High = d.PriceA,
                    Low = d.PriceA,
                    Ticker = cacheTickerA,
                    Timeframe = _strategyViewModel.SelectedTimeFrame.Value,
                    IsClosed = true
                }).ToList();

                var candlesB = _dataCache.AlignedData.Select(d => new Candle
                {
                    Time = d.Time,
                    Close = d.PriceB,
                    Open = d.PriceB,
                    High = d.PriceB,
                    Low = d.PriceB,
                    Ticker = cacheTickerB,
                    Timeframe = _strategyViewModel.SelectedTimeFrame.Value,
                    IsClosed = true
                }).ToList();

                SetPrivateField(strategy, "_backtestCandlesA", candlesA);
                SetPrivateField(strategy, "_backtestCandlesB", candlesB);
            }

            Debug.WriteLine("[PairsTradingBacktestEngine] CreateIsolatedStrategy() КОНЕЦ");
            return strategy;
        }

        /// <summary>
        /// Инициализирует стратегию с использованием кэшированных моделей
        /// </summary>
        private async Task InitializeStrategyWithCacheAsync()
        {
            if (_strategy == null) return;

            // ✅ Устанавливаем инструменты
            SetPrivateField(_strategy, "_instrumentA", _strategyViewModel.Instrument);
            SetPrivateField(_strategy, "_instrumentB", _strategyViewModel.Instrument);
            SetPrivateField(_strategy, "_instrumentATicker", _strategyViewModel.Instrument.Ticker);
            SetPrivateField(_strategy, "_instrumentBTicker", _strategyViewModel.Instrument.Ticker);
            SetPrivateField(_strategy, "_timeframe", _strategyViewModel.SelectedTimeFrame.Value);

            // ✅ Вызываем InitializeAsync через рефлексию
            var initMethod = typeof(PairsTradingStrategy).GetMethod(
                "InitializeAsync",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (initMethod != null)
            {
                try
                {
                    var task = (Task)initMethod.Invoke(_strategy, new object[] {
                        _strategyViewModel.Instrument,
                        _strategyViewModel.SelectedTimeFrame.Value
                    });
                    await task;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"InitializeAsync failed: {ex.Message}");
                    // Продолжаем, так как у нас есть кэшированные модели
                }
            }

            // ✅ Если есть кэшированные модели, устанавливаем первую
            if (_dataCache.PairsModels != null && _dataCache.PairsModels.Any())
            {
                var firstModel = _dataCache.PairsModels.Values.FirstOrDefault();
                if (firstModel != null)
                {
                    ApplyModelToStrategy(_strategy, firstModel);
                }
            }
        }

        /// <summary>
        /// Применяет модель к стратегии
        /// </summary>
        private void ApplyModelToStrategy(PairsTradingStrategy strategy, PairsModel model)
        {
            if (strategy?.Parameters == null) return;

            var parameters = strategy.Parameters;

            SetPrivateField(parameters, "_hedgeRatio", model.HedgeRatio);
            SetPrivateField(parameters, "_spreadMean", model.SpreadMean);
            SetPrivateField(parameters, "_spreadStd", model.SpreadStd);
            SetPrivateField(parameters, "_modelValid", true);
            SetPrivateField(parameters, "_modelLastUpdate", DateTime.Now);

            var indicatorValues = GetPrivateField<PairsIndicatorValues>(strategy, "_indicatorValues");
            if (indicatorValues != null)
            {
                SetPrivateField(indicatorValues, "_hedgeRatio", model.HedgeRatio);
                SetPrivateField(indicatorValues, "_spreadMean", model.SpreadMean);
                SetPrivateField(indicatorValues, "_spreadStd", model.SpreadStd);
            }
        }

        public async Task<OptimizationResult> RunBacktestAsync(
                Dictionary<string, decimal> parameters,
                CancellationToken cancellationToken = default)
        {
            if (_strategy == null)
                throw new InvalidOperationException("Engine not initialized");

            // ✅ Проверяем параметры, но НЕ возвращаем пустой результат для невалидных
            // Вместо этого предупреждаем и продолжаем выполнение
            if (!ValidateParameters(parameters))
            {
                _logger?.LogWarning("Parameters validation failed, but continuing with defaults");
                // Не возвращаем пустой результат, а используем значения по умолчанию
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                // ✅ Используем значения по умолчанию, если параметры отсутствуют
                int lookback = (int)parameters.GetValueOrDefault("LookbackPeriod", 120);
                decimal entryZ = parameters.GetValueOrDefault("EntryZScore", 2.0m);
                decimal exitZ = parameters.GetValueOrDefault("ExitZScore", 0.5m);
                decimal stopLossZ = parameters.GetValueOrDefault("StopLossZScore", 3.5m);
                decimal positionSize = parameters.GetValueOrDefault("PositionSizePercent", 10m);

                var model = GetModelForLookback(lookback);
                if (model == null || !model.IsValid)
                {
                    _logger.LogWarning($"Invalid model for lookback {lookback}");
                    return new OptimizationResult { TotalTrades = 0, NetProfit = 0 };
                }

                ApplyModelToStrategy(_strategy, model);
                ApplyParametersToStrategy(_strategy, parameters);

                var result = await SimulateTradingAsync(
                    model,
                    entryZ,
                    exitZ,
                    stopLossZ,
                    positionSize,
                    cancellationToken);

                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in backtest");
                return new OptimizationResult { TotalTrades = 0, NetProfit = 0 };
            }
        }

        private PairsModel GetModelForLookback(int lookback)
        {
            if (_dataCache?.PairsModels == null)
                return null;

            if (_dataCache.PairsModels.TryGetValue(lookback, out var model))
                return model;

            var keys = _dataCache.PairsModels.Keys.OrderBy(k => k).ToList();
            var nearest = keys.FirstOrDefault(k => k >= lookback);
            if (nearest != 0 && _dataCache.PairsModels.TryGetValue(nearest, out var nearestModel))
                return nearestModel;

            return _dataCache.PairsModels.Values.FirstOrDefault();
        }

        /// <summary>
        /// Симулирует торговлю на основе выровненных данных
        /// </summary>
        private async Task<OptimizationResult> SimulateTradingAsync(
            PairsModel model,
                decimal entryZ,
                decimal exitZ,
                decimal stopLossZ,
                decimal positionSizePercent,
                CancellationToken cancellationToken)
        {
            var result = new OptimizationResult
            {
                Parameters = new Dictionary<string, decimal>
                {
                    ["LookbackPeriod"] = model.LookbackPeriod,
                    ["EntryZScore"] = entryZ,
                    ["ExitZScore"] = exitZ,
                    ["StopLossZScore"] = stopLossZ,
                    ["PositionSizePercent"] = positionSizePercent
                }
            };

            var alignedData = _dataCache.AlignedData;
            if (alignedData == null || alignedData.Count < 50)
            {
                _logger.LogWarning("Not enough aligned data for simulation");
                return result;
            }

            // ✅ ДОБАВЛЯЕМ: Проверка валидности параметров перед симуляцией
            if (entryZ >= stopLossZ)
            {
                _logger.LogWarning($"Skipping invalid parameter set: EntryZ ({entryZ}) >= StopLossZ ({stopLossZ})");
                result.TotalTrades = 0;
                result.NetProfit = -999999m; // Отметка о невалидной комбинации
                return result;
            }

            if (exitZ >= entryZ)
            {
                _logger.LogWarning($"Skipping invalid parameter set: ExitZ ({exitZ}) >= EntryZ ({entryZ})");
                result.TotalTrades = 0;
                result.NetProfit = -999999m;
                return result;
            }

            var trades = new List<TradeRecord>();
            decimal currentBalance = INITIAL_CAPITAL;
            decimal positionA = 0;
            decimal positionB = 0;
            decimal entryPriceA = 0;
            decimal entryPriceB = 0;
            DateTime entryTime = DateTime.MinValue;
            string currentDirection = null;

            int lotSize = _strategyViewModel.Instrument?.LotSize ?? 1;
            decimal hedgeRatio = model.HedgeRatio;
            decimal spreadMean = model.SpreadMean;
            decimal spreadStd = model.SpreadStd;

            int totalCandles = alignedData.Count;


            // ✅ Для отслеживания состояния входа (защита от мгновенного выхода)
            bool justEntered = false;
            int barsSinceEntry = 0;
            const int MIN_BARS_HOLD = 3; // Минимальное количество свечей удержания позиции

            // ✅ ДОБАВЛЯЕМ: Списки для истории эквити
            List<decimal> equityHistory = new List<decimal>();
            List<DateTime> equityDates = new List<DateTime>();

            
            equityHistory.Add(currentBalance);
            equityDates.Add(alignedData.First().Time);

            for (int i = 50; i < totalCandles; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                var data = alignedData[i];
                decimal priceA = data.PriceA;
                decimal priceB = data.PriceB;

                decimal spread = priceA - hedgeRatio * priceB;
                decimal zScore = spreadStd > 0 ? (spread - spreadMean) / spreadStd : 0;

                // --- Вход ---
                if (positionA == 0 && positionB == 0)
                {
                    if (zScore > entryZ)
                    {
                        decimal capitalPerLeg = currentBalance * (positionSizePercent / 100) / 2;
                        int qtyA = (int)Math.Floor(capitalPerLeg / priceA);
                        int qtyB = (int)Math.Floor(capitalPerLeg / (priceB * hedgeRatio));

                        if (qtyA > 0 && qtyB > 0)
                        {
                            positionA = -qtyA;
                            positionB = qtyB;
                            entryPriceA = priceA;
                            entryPriceB = priceB;
                            entryTime = data.Time;
                            currentDirection = "SHORT_A_LONG_B";
                            justEntered = true;
                            barsSinceEntry = 0;
                        }
                    }
                    else if (zScore < -entryZ)
                    {
                        decimal capitalPerLeg = currentBalance * (positionSizePercent / 100) / 2;
                        int qtyA = (int)Math.Floor(capitalPerLeg / priceA);
                        int qtyB = (int)Math.Floor(capitalPerLeg / (priceB * hedgeRatio));

                        if (qtyA > 0 && qtyB > 0)
                        {
                            positionA = qtyA;
                            positionB = -qtyB;
                            entryPriceA = priceA;
                            entryPriceB = priceB;
                            entryTime = data.Time;
                            currentDirection = "LONG_A_SHORT_B";
                            justEntered = true;
                            barsSinceEntry = 0;
                        }
                    }
                }
                // --- Выход ---
                else if (positionA != 0 || positionB != 0)
                {
                    barsSinceEntry++;

                    // ✅ Защита: не выходим сразу после входа (минимум MIN_BARS_HOLD свечей)
                    // Это предотвращает мгновенный выход при EntryZScore == StopLossZScore
                    if (barsSinceEntry < MIN_BARS_HOLD && justEntered)
                    {
                        // Пропускаем проверку выхода на первых свечах
                        continue;
                    }

                    bool shouldExit = false;
                    string exitReason = "";

                    // Целевой выход - спред вернулся к норме
                    if (Math.Abs(zScore) <= exitZ)
                    {
                        shouldExit = true;
                        exitReason = $"Target Z: {zScore:F2} ≤ {exitZ:F2}";
                    }
                    // Стоп-лосс - срабатывает только если прошло достаточно времени с момента входа
                    else if (barsSinceEntry >= MIN_BARS_HOLD && Math.Abs(zScore) > stopLossZ)
                    {
                        shouldExit = true;
                        exitReason = $"Stop Loss Z: {zScore:F2} > {stopLossZ:F2}";
                    }
                    // Таймаут удержания (защита от "зависания" позиции)
                    else if ((data.Time - entryTime).TotalDays > 30)
                    {
                        shouldExit = true;
                        exitReason = $"Timeout: {(data.Time - entryTime).Days} days";
                    }

                    if (shouldExit)
                    {
                        decimal profit = 0;

                        // Расчет прибыли по позиции A
                        if (positionA > 0)
                            profit += (priceA - entryPriceA) * positionA * lotSize;
                        else if (positionA < 0)
                            profit += (entryPriceA - priceA) * Math.Abs(positionA) * lotSize;

                        // Расчет прибыли по позиции B
                        if (positionB > 0)
                            profit += (priceB - entryPriceB) * positionB * lotSize;
                        else if (positionB < 0)
                            profit += (entryPriceB - priceB) * Math.Abs(positionB) * lotSize;

                        decimal investedCapital = (Math.Abs(positionA) * entryPriceA + Math.Abs(positionB) * entryPriceB) * lotSize;
                        decimal profitPercent = investedCapital > 0 ? profit / investedCapital * 100 : 0;

                        trades.Add(new TradeRecord
                        {
                            EntryTime = entryTime,
                            ExitTime = data.Time,
                            EntryPrice = entryPriceA,
                            ExitPrice = priceA,
                            Quantity = (int)Math.Abs(positionA + positionB),
                            Direction = currentDirection,
                            Profit = profit,
                            ProfitPercent = profitPercent
                        });

                        currentBalance += profit;
                        positionA = 0;
                        positionB = 0;
                        currentDirection = null;
                        justEntered = false;
                    }
                }

                // ✅ ДОБАВЛЯЕМ: Сохраняем эквити каждые 5 свечей
                if (i % 5 == 0 || i == totalCandles - 1)
                {
                    // Расчет текущего эквити (баланс + нереализованная прибыль)
                    decimal unrealizedPnL = 0;
                    if (positionA != 0 || positionB != 0)
                    {
                        // Расчет нереализованной прибыли по позициям
                        if (positionA > 0)
                            unrealizedPnL += (priceA - entryPriceA) * positionA * lotSize;
                        else if (positionA < 0)
                            unrealizedPnL += (entryPriceA - priceA) * Math.Abs(positionA) * lotSize;

                        if (positionB > 0)
                            unrealizedPnL += (priceB - entryPriceB) * positionB * lotSize;
                        else if (positionB < 0)
                            unrealizedPnL += (entryPriceB - priceB) * Math.Abs(positionB) * lotSize;
                    }

                    decimal currentEquity = currentBalance + unrealizedPnL;
                    equityHistory.Add(currentEquity);
                    equityDates.Add(data.Time);
                }

            }

            // Закрываем последнюю позицию принудительно
            if (positionA != 0 || positionB != 0)
            {
                var lastData = alignedData.Last();
                decimal priceA = lastData.PriceA;
                decimal priceB = lastData.PriceB;
                decimal profit = 0;

                if (positionA > 0)
                    profit += (priceA - entryPriceA) * positionA * lotSize;
                else if (positionA < 0)
                    profit += (entryPriceA - priceA) * Math.Abs(positionA) * lotSize;

                if (positionB > 0)
                    profit += (priceB - entryPriceB) * positionB * lotSize;
                else if (positionB < 0)
                    profit += (entryPriceB - priceB) * Math.Abs(positionB) * lotSize;

                decimal investedCapital = (Math.Abs(positionA) * entryPriceA + Math.Abs(positionB) * entryPriceB) * lotSize;
                decimal profitPercent = investedCapital > 0 ? profit / investedCapital * 100 : 0;

                trades.Add(new TradeRecord
                {
                    EntryTime = entryTime,
                    ExitTime = lastData.Time,
                    EntryPrice = entryPriceA,
                    ExitPrice = priceA,
                    Quantity = (int)Math.Abs(positionA + positionB),
                    Direction = currentDirection,
                    Profit = profit,
                    ProfitPercent = profitPercent
                });

                currentBalance += profit;
            }


            // ✅ СОХРАНЯЕМ ИСТОРИЮ ЭКВИТИ В РЕЗУЛЬТАТ
            result.EquityHistory = equityHistory;
            result.EquityDates = equityDates;


            CalculateMetrics(result, trades);

            _logger.LogDebug($"Simulation completed: {trades.Count} trades, NetProfit={result.NetProfit:F2}");

            return result;
        }


       


        private void CalculateMetrics(OptimizationResult result, List<TradeRecord> trades)
        {
            result.TotalTrades = trades.Count;
            result.WinningTrades = trades.Count(t => t.Profit > 0);
            result.LosingTrades = trades.Count(t => t.Profit < 0);
            result.WinRate = result.TotalTrades > 0 ? (decimal)result.WinningTrades / result.TotalTrades * 100 : 0;

            if (trades.Any())
            {
                result.AverageWin = trades.Where(t => t.Profit > 0).Select(t => t.Profit).DefaultIfEmpty(0).Average();
                result.AverageLoss = trades.Where(t => t.Profit < 0).Select(t => Math.Abs(t.Profit)).DefaultIfEmpty(0).Average();

                result.GrossProfit = trades.Where(t => t.Profit > 0).Sum(t => t.Profit);
                decimal grossLoss = trades.Where(t => t.Profit < 0).Sum(t => Math.Abs(t.Profit));
                result.NetProfit = result.GrossProfit - grossLoss;

                result.ProfitFactor = grossLoss > 0 ? result.GrossProfit / grossLoss : (result.GrossProfit > 0 ? 999 : 0);

                decimal peak = 0;
                decimal maxDrawdown = 0;
                decimal runningBalance = INITIAL_CAPITAL;

                foreach (var trade in trades.OrderBy(t => t.ExitTime))
                {
                    runningBalance += trade.Profit;
                    if (runningBalance > peak)
                        peak = runningBalance;
                    decimal drawdown = peak > 0 ? (peak - runningBalance) / peak * 100 : 0;
                    if (drawdown > maxDrawdown)
                        maxDrawdown = drawdown;
                }
                result.MaxDrawdown = maxDrawdown;

                if (trades.Count > 1)
                {
                    var returns = trades.Select(t => t.ProfitPercent / 100).ToList();
                    var avgReturn = returns.Average();
                    var stdDev = (double)Math.Sqrt(returns.Sum(r => Math.Pow((double)(r - (decimal)avgReturn), 2)) / (returns.Count - 1));
                    result.SharpeRatio = stdDev > 0 ? (decimal)((double)avgReturn / stdDev * Math.Sqrt(252)) : 0;
                }

                result.RecoveryFactor = result.MaxDrawdown > 0 ? result.NetProfit / (result.MaxDrawdown / 100 * INITIAL_CAPITAL) : 0;
                result.Expectancy = result.TotalTrades > 0 ? result.NetProfit / result.TotalTrades : 0;
            }
        }

        private void ApplyParametersToStrategy(PairsTradingStrategy strategy, Dictionary<string, decimal> parameters)
        {
            if (strategy?.Parameters == null) return;

            var p = strategy.Parameters;

            if (parameters.TryGetValue("LookbackPeriod", out var lookback))
                SetPrivateField(p, "_lookbackPeriod", (int)lookback);
            if (parameters.TryGetValue("EntryZScore", out var entryZ))
                SetPrivateField(p, "_entryZScore", entryZ);
            if (parameters.TryGetValue("ExitZScore", out var exitZ))
                SetPrivateField(p, "_exitZScore", exitZ);
            if (parameters.TryGetValue("StopLossZScore", out var stopLossZ))
                SetPrivateField(p, "_stopLossZScore", stopLossZ);
            if (parameters.TryGetValue("PositionSizePercent", out var posSize))
                SetPrivateField(p, "_positionSizePercent", posSize);
        }

        #region Вспомогательные методы рефлексии

        private void SetPrivateField(object obj, string fieldName, object value)
        {
            try
            {
                var field = obj.GetType().GetField(fieldName,
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);

                if (field != null)
                {
                    field.SetValue(obj, value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetPrivateField error for {fieldName}: {ex.Message}");
            }
        }

        private T GetPrivateField<T>(object obj, string fieldName) where T : class
        {
            try
            {
                var field = obj.GetType().GetField(fieldName,
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);

                if (field != null)
                {
                    return field.GetValue(obj) as T;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetPrivateField error for {fieldName}: {ex.Message}");
            }
            return null;
        }

        #endregion

        public IEnumerable<string> GetSupportedParameters()
        {
            return new[]
            {
                "LookbackPeriod",
                "EntryZScore",
                "ExitZScore",
                "StopLossZScore",
                "PositionSizePercent"
            };
        }

        /// <summary>
        /// Проверяет валидность параметров для бэктеста.
        /// Валидной считается любая комбинация параметров, которая содержит хотя бы один параметр.
        /// Однако для корректной работы стратегии проверяются критические условия:
        /// - EntryZScore должен быть меньше StopLossZScore (иначе стоп-лосс не работает)
        /// - ExitZScore должен быть меньше EntryZScore (иначе выход происходит сразу после входа)
        /// </summary>
        public bool ValidateParameters(Dictionary<string, decimal> parameters)
        {
            // ✅ ЛЮБАЯ КОМБИНАЦИЯ ПАРАМЕТРОВ ВАЛИДНА, если есть хотя бы один параметр
            // Это позволяет оптимизировать даже с одним параметром
            if (parameters == null || parameters.Count == 0)
            {
                _logger?.LogWarning("Empty parameters dictionary");
                return false;
            }

            // ✅ Проверяем наличие ключевых параметров для логирования
            bool hasEntryZ = parameters.ContainsKey("EntryZScore");
            bool hasExitZ = parameters.ContainsKey("ExitZScore");
            bool hasStopLossZ = parameters.ContainsKey("StopLossZScore");
            bool hasLookback = parameters.ContainsKey("LookbackPeriod");
            bool hasPositionSize = parameters.ContainsKey("PositionSizePercent");

            // ✅ Если есть хотя бы один параметр - комбинация валидна
            // Но проверяем критические условия только если все три параметра присутствуют
            if (hasEntryZ && hasExitZ && hasStopLossZ)
            {
                decimal entryZ = parameters.GetValueOrDefault("EntryZScore", 2.0m);
                decimal stopLossZ = parameters.GetValueOrDefault("StopLossZScore", 3.5m);
                decimal exitZ = parameters.GetValueOrDefault("ExitZScore", 0.5m);

                // ✅ Проверяем, что EntryZScore меньше StopLossZScore
                // Это критическое условие для корректной работы стратегии
                // Если EntryZScore >= StopLossZScore, то стоп-лосс никогда не сработает корректно
                // или будет срабатывать сразу после входа
                if (entryZ >= stopLossZ)
                {
                    _logger?.LogWarning($"Invalid parameters: EntryZScore ({entryZ}) must be less than StopLossZScore ({stopLossZ})");
                    return false;
                }

                // ✅ Проверяем, что ExitZScore меньше EntryZScore
                // Иначе выход будет сразу после входа, что делает стратегию бессмысленной
                if (exitZ >= entryZ)
                {
                    _logger?.LogWarning($"Invalid parameters: ExitZScore ({exitZ}) must be less than EntryZScore ({entryZ})");
                    return false;
                }

                // ✅ Предупреждение (не блокирующее) о безопасности
                if (stopLossZ <= entryZ * 1.2m)
                {
                    _logger?.LogWarning($"StopLossZScore ({stopLossZ}) is close to EntryZScore ({entryZ}). Recommended: StopLossZScore > EntryZScore * 1.2");
                }
            }
            else
            {
                // Если не все три параметра присутствуют, логируем это, но не блокируем
                _logger?.LogDebug($"Partial parameters set: EntryZ={hasEntryZ}, ExitZ={hasExitZ}, StopLossZ={hasStopLossZ}, Lookback={hasLookback}, PositionSize={hasPositionSize}");
            }

            // ✅ Проверяем, что LookbackPeriod положительный (если он есть)
            if (hasLookback)
            {
                int lookback = (int)parameters.GetValueOrDefault("LookbackPeriod", 24);
                if (lookback < 1)
                {
                    _logger?.LogWarning($"Invalid LookbackPeriod: {lookback} must be >= 1");
                    return false;
                }
            }

            // ✅ Проверяем, что PositionSizePercent в разумных пределах (если он есть)
            if (hasPositionSize)
            {
                decimal positionSize = parameters.GetValueOrDefault("PositionSizePercent", 10m);
                if (positionSize <= 0 || positionSize > 100)
                {
                    _logger?.LogWarning($"Invalid PositionSizePercent: {positionSize} must be between 0 and 100");
                    return false;
                }
            }

            // ✅ ЛЮБАЯ КОМБИНАЦИЯ ПАРАМЕТРОВ ВАЛИДНА
            // Все проверки выше только предупреждают, но не блокируют
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                if (_strategy is IDisposable disposable)
                    disposable.Dispose();
            }
            catch { }

            _disposed = true;
        }
    }

    /// <summary>
    /// Фабрика движков бэктеста  //  ВЫНЕС ОТДЕЛЬНО В СЕРВИСЫ
    /// </summary>
   /* public class BacktestEngineFactory : IBacktestEngineFactory
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IProvirerService _provider; // ✅ ДОБАВЛЕНО

        public BacktestEngineFactory(ILoggerFactory loggerFactory = null, IProvirerService provider = null)
        {
            _loggerFactory = loggerFactory;
            _provider = provider; // ✅ ДОБАВЛЕНО
        }

        public IBacktestEngine CreateEngine(string strategyType)
        {
            var logger = _loggerFactory?.CreateLogger("BacktestEngine") ??
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

            return strategyType switch
            {
                "PairsTrading" => new PairsTradingBacktestEngine(logger, _provider), // ✅ Передаем провайдер
                "RSI" => throw new NotSupportedException($"Strategy type '{strategyType}' is not yet supported"),
                "MA" => throw new NotSupportedException($"Strategy type '{strategyType}' is not yet supported"),
                "Rating" => throw new NotSupportedException($"Strategy type '{strategyType}' is not yet supported"),
                _ => throw new NotSupportedException($"Strategy type '{strategyType}' is not supported for backtesting")
            };
        }
    }*/

    /// <summary>
    /// Запись о сделке для бэктеста
    /// </summary>
    public class TradeRecord
    {
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public int Quantity { get; set; }
        public string Direction { get; set; }
        public decimal Profit { get; set; }
        public decimal ProfitPercent { get; set; }
    }
}