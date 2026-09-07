// Файл: Services/RsiBacktestEngine.cs
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
using System.Threading;
using System.Threading.Tasks;

namespace MoneyGenerator_v5.Services
{
    /// <summary>
    /// Движок бэктеста для RSI стратегии
    /// Поддерживает все типы входа и выхода из RSI стратегии
    /// </summary>
    public class RsiBacktestEngine : IBacktestEngine
    {
        #region Поля и константы

        private StrategyViewModel _strategyViewModel;
        private OptimizationDataCache _dataCache;
        private ILogger _logger;
        private bool _disposed = false;

        // Кэш свечей для бэктеста
        private List<Candle> _candles;

        // Константы
        private const decimal INITIAL_BALANCE = 100000m;  // Начальный баланс 100,000 RUB
        private const decimal COMMISSION_RATE = 0.0005m;  // 0.05% комиссия
        private const decimal DEFAULT_LOT_SIZE = 1m;      // Стандартный лот

        // Фиксированный размер позиции
        private decimal _fixedPositionValue = 0m;

        // Для отслеживания пересечений уровней в бэктесте
        private decimal _previousOscillatorValue = 0;
        private bool _hasPreviousOscillatorValue = false;

        #endregion

        #region IBacktestEngine Implementation

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
                _logger?.LogDebug($"[RsiBacktestEngine] Загружено {_candles.Count} свечей для {instrument.Ticker}");
            }
            else
            {
                _candles = new List<Candle>();
                _logger?.LogWarning($"[RsiBacktestEngine] Не найдены свечи для {instrument.Ticker}");
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
                    _logger?.LogWarning($"[RsiBacktestEngine] ❌ Недостаточно данных: {_candles?.Count ?? 0} свечей");
                    result.TotalTrades = 0;
                    result.NetProfit = -999999;
                    return result;
                }

                if (!TryParseParameters(parameters, out var strategyParams, out string errorMsg))
                {
                    _logger?.LogWarning($"[RsiBacktestEngine] ❌ Ошибка парсинга параметров: {errorMsg}");
                    result.TotalTrades = 0;
                    result.NetProfit = -999999;
                    return result;
                }

                _logger?.LogInformation($"[RsiBacktestEngine] Параметры RSI: Period={strategyParams.RsiPeriod}, " +
                                       $"Overbought={strategyParams.RsiOverbought}, Oversold={strategyParams.RsiOversold}, " +
                                       $"StochPeriod={strategyParams.StochPeriod}, StochOverbought={strategyParams.StochOverbought}, " +
                                       $"StochOversold={strategyParams.StochOversold}, " +
                                       $"EntryType={strategyParams.EntryOrderType}, ExitType={strategyParams.ExitOrderType}, " +
                                       $"PositionSize={strategyParams.OrderSizePercent}%");

                // Устанавливаем фиксированный размер позиции
                _fixedPositionValue = INITIAL_BALANCE * (strategyParams.OrderSizePercent / 100);
                _logger?.LogInformation($"[RsiBacktestEngine] Фиксированный размер позиции: {_fixedPositionValue:F2} RUB");

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

                _logger?.LogInformation($"[RsiBacktestEngine] ✅ Результат: P&L={result.NetProfit:F2}, Trades={result.TotalTrades}, WinRate={result.WinRate:F1}%");

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning($"[RsiBacktestEngine] ⚠️ Бэктест отменен");
                result.TotalTrades = 0;
                result.NetProfit = -999999;
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"[RsiBacktestEngine] ❌ Ошибка выполнения бэктеста");
                result.TotalTrades = 0;
                result.NetProfit = -999999;
                return result;
            }
        }

        public IEnumerable<string> GetSupportedParameters()
        {
            return new[]
            {
                // Параметры RSI
                "RsiPeriod",
                "RsiOverbought",
                "RsiOversold",
                // Параметры Stochastic/StochRSI
                "StochPeriod",
                "StochOverbought",
                "StochOversold",
                "StochSmoothK",
                "StochSmoothD",
                "OscillatorType",
                // Параметры входа
                "EntryOrderType",
                "EntryLimitOffsetPercent",
                "EntryStopOffsetPercent",
                "EntrySlippage",
                // Параметры выхода
                "ExitOrderType",
                "ExitSlippage",
                // Moving Take Profit Entry
                "MovingTPEntryCalculationType",
                "MovingTPEntryTargetPercent",
                "MovingTPEntrySlippage",
                "MovingTPEntryTimeoutMinutes",
                // Moving Take Profit Exit
                "MovingTPExitCalculationType",
                "MovingTPExitStartPercent",
                "MovingTPExitSlippage",
                "MovingTPExitTimeoutMinutes",
                // Trailing Stop Exit
                "TrailingStopExitCalculationType",
                "TrailingStopExitDistancePercent",
                "TrailingStopExitSlippage",
                "TrailingStopExitActivationPercent",
                "ProtectiveStopPercent",
                // Take Profit / Stop Loss (для Market выхода)
                "TakeProfitCalculationType",
                "TakeProfitPercent",
                "TakeProfitActivationPrice",
                "TakeProfitSlippage",
                "StopLossCalculationType",
                "StopLossPercent",
                "StopLossActivationPrice",
                "StopLossSlippage",
                // Общие
                "AtrMultiplier",
                "OrderSizePercent",
                "CloseOnSignalReversal",
                // Level Crossing Entry/Exit
                "LevelCrossingEntryProtectiveStopPercent",
                "LevelCrossingEntryProtectiveStopDistancePercent",
                "LevelCrossingExitProtectiveStopPercent",
                "LevelCrossingExitProtectiveStopDistancePercent"
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

        #endregion

        #region Парсинг параметров

        private bool TryParseParameters(Dictionary<string, decimal> parameters, out RsiStrategyParams result, out string error)
        {
            result = new RsiStrategyParams();
            error = "";

            try
            {
                // Параметры RSI
                result.RsiPeriod = (int)GetParam(parameters, "RsiPeriod", 14, 5, 50);
                result.RsiOverbought = GetParam(parameters, "RsiOverbought", 70, 60, 90);
                result.RsiOversold = GetParam(parameters, "RsiOversold", 30, 10, 40);

                // Параметры Stochastic
                result.StochPeriod = (int)GetParam(parameters, "StochPeriod", 14, 5, 50);
                result.StochOverbought = GetParam(parameters, "StochOverbought", 80, 60, 90);
                result.StochOversold = GetParam(parameters, "StochOversold", 20, 10, 40);
                result.StochSmoothK = (int)GetParam(parameters, "StochSmoothK", 3, 1, 10);
                result.StochSmoothD = (int)GetParam(parameters, "StochSmoothD", 3, 1, 10);

                // OscillatorType: 0 = StochRSI, 1 = Stochastic
                result.OscillatorType = (OscillatorType)(int)GetParam(parameters, "OscillatorType", 1, 0, 1);

                // Параметры входа
                result.EntryOrderType = (MoneyGenerator_v5.Strategies.OrderType)(int)GetParam(parameters, "EntryOrderType", 4, 0, 4);
                result.EntryLimitOffsetPercent = GetParam(parameters, "EntryLimitOffsetPercent", 0.1m, 0.01m, 5m);
                result.EntryStopOffsetPercent = GetParam(parameters, "EntryStopOffsetPercent", 0.2m, 0.01m, 5m);
                result.EntrySlippage = GetParam(parameters, "EntrySlippage", 0.01m, 0m, 1m);

                // Параметры выхода
                result.ExitOrderType = (MoneyGenerator_v5.Strategies.OrderType)(int)GetParam(parameters, "ExitOrderType", 3, 0, 3);
                result.ExitSlippage = GetParam(parameters, "ExitSlippage", 0.01m, 0m, 1m);

                // Moving Take Profit Entry
                result.MovingTPEntryCalculationType = (PriceCalculationType)(int)GetParam(parameters, "MovingTPEntryCalculationType", 2, 0, 2);
                result.MovingTPEntryTargetPercent = GetParam(parameters, "MovingTPEntryTargetPercent", 2.0m, 0.1m, 10m);
                result.MovingTPEntrySlippage = GetParam(parameters, "MovingTPEntrySlippage", 0.01m, 0m, 1m);
                result.MovingTPEntryTimeoutMinutes = (int)GetParam(parameters, "MovingTPEntryTimeoutMinutes", 60, 1, 1440);

                // Moving Take Profit Exit
                result.MovingTPExitCalculationType = (PriceCalculationType)(int)GetParam(parameters, "MovingTPExitCalculationType", 2, 0, 2);
                result.MovingTPExitStartPercent = GetParam(parameters, "MovingTPExitStartPercent", 2.0m, 0.1m, 10m);
                result.MovingTPExitSlippage = GetParam(parameters, "MovingTPExitSlippage", 0.01m, 0m, 1m);
                result.MovingTPExitTimeoutMinutes = (int)GetParam(parameters, "MovingTPExitTimeoutMinutes", 60, 1, 1440);

                // Trailing Stop Exit
                result.TrailingStopExitCalculationType = (PriceCalculationType)(int)GetParam(parameters, "TrailingStopExitCalculationType", 2, 0, 2);
                result.TrailingStopExitDistancePercent = GetParam(parameters, "TrailingStopExitDistancePercent", 0.5m, 0.1m, 5m);
                result.TrailingStopExitSlippage = GetParam(parameters, "TrailingStopExitSlippage", 0.01m, 0m, 1m);
                result.TrailingStopExitActivationPercent = GetParam(parameters, "TrailingStopExitActivationPercent", 1.0m, 0.1m, 10m);
                result.ProtectiveStopPercent = GetParam(parameters, "ProtectiveStopPercent", 0.5m, 0.1m, 5m);

                // Take Profit / Stop Loss (для Market выхода)
                result.TakeProfitCalculationType = (PriceCalculationType)(int)GetParam(parameters, "TakeProfitCalculationType", 2, 0, 2);
                result.TakeProfitPercent = GetParam(parameters, "TakeProfitPercent", 2.0m, 0.1m, 10m);
                result.TakeProfitActivationPrice = GetParam(parameters, "TakeProfitActivationPrice", 0m, 0m, 100m);
                result.TakeProfitSlippage = GetParam(parameters, "TakeProfitSlippage", 0.01m, 0m, 1m);

                result.StopLossCalculationType = (PriceCalculationType)(int)GetParam(parameters, "StopLossCalculationType", 2, 0, 2);
                result.StopLossPercent = GetParam(parameters, "StopLossPercent", 1.0m, 0.1m, 5m);
                result.StopLossActivationPrice = GetParam(parameters, "StopLossActivationPrice", 0m, 0m, 100m);
                result.StopLossSlippage = GetParam(parameters, "StopLossSlippage", 0.01m, 0m, 1m);

                // Общие
                result.AtrMultiplier = GetParam(parameters, "AtrMultiplier", 1.5m, 0.5m, 5m);
                result.OrderSizePercent = GetParam(parameters, "OrderSizePercent", 10m, 0.1m, 50m);
                result.CloseOnSignalReversal = (int)GetParam(parameters, "CloseOnSignalReversal", 0, 0, 1) == 1;

                // Level Crossing Entry/Exit
                result.LevelCrossingEntryProtectiveStopPercent = GetParam(parameters, "LevelCrossingEntryProtectiveStopPercent", 0.25m, 0.05m, 2m);
                result.LevelCrossingEntryProtectiveStopDistancePercent = GetParam(parameters, "LevelCrossingEntryProtectiveStopDistancePercent", 0.25m, 0.05m, 2m);
                result.LevelCrossingExitProtectiveStopPercent = GetParam(parameters, "LevelCrossingExitProtectiveStopPercent", 0.25m, 0.05m, 2m);
                result.LevelCrossingExitProtectiveStopDistancePercent = GetParam(parameters, "LevelCrossingExitProtectiveStopDistancePercent", 0.25m, 0.05m, 2m);

                return true;
            }
            catch (Exception ex)
            {
                error = $"Ошибка парсинга: {ex.Message}";
                _logger?.LogError(ex, $"[RsiBacktestEngine] Ошибка парсинга параметров");
                return false;
            }
        }

        private decimal GetParam(Dictionary<string, decimal> parameters, string key, decimal defaultValue, decimal minValue, decimal maxValue)
        {
            if (parameters.TryGetValue(key, out var value))
            {
                return Math.Clamp(value, minValue, maxValue);
            }
            return defaultValue;
        }

        #endregion

        #region Основной метод симуляции

        private async Task<SimulationResult> SimulateTradingAsync(RsiStrategyParams parameters, CancellationToken cancellationToken)
        {
            _logger?.LogInformation($"[RsiBacktestEngine] SimulateTradingAsync: _candles = {_candles?.Count ?? 0} свечей");

            var result = new SimulationResult();
            var stopwatch = Stopwatch.StartNew();

            decimal lotSize = _dataCache?.LotSize ?? DEFAULT_LOT_SIZE;

            try
            {
                if (_candles == null || _candles.Count < 50)
                {
                    _logger?.LogWarning($"[RsiBacktestEngine] Недостаточно данных для симуляции");
                    return result;
                }

                // Минимальное количество свечей для расчета индикаторов
                int minCandles = Math.Max(parameters.RsiPeriod, parameters.StochPeriod) + 50;
                if (_candles.Count < minCandles)
                {
                    _logger?.LogWarning($"[RsiBacktestEngine] Недостаточно данных: {_candles.Count} < {minCandles}");
                    return result;
                }

                // Конвертация в Quotes для расчета индикаторов
                var quotes = _candles.Select(c => new Quote
                {
                    Date = c.Time,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume
                }).ToList();

                // Расчет RSI
                var rsiResults = quotes.GetRsi(parameters.RsiPeriod).ToList();
                var rsiValues = rsiResults.Where(x => x.Rsi.HasValue)
                                          .Select(x => (decimal)x.Rsi.Value)
                                          .ToList();

                // Расчет Stochastic или StochRSI
                List<decimal> oscillatorValues = new List<decimal>();
                List<decimal> oscillatorSignalValues = new List<decimal>();

                if (parameters.OscillatorType == OscillatorType.StochRSI)
                {
                    var stochRsiResults = quotes.GetStochRsi(
                        parameters.StochPeriod,
                        parameters.StochSmoothK,
                        parameters.StochSmoothD).ToList();

                    oscillatorValues = stochRsiResults.Where(x => x.StochRsi.HasValue)
                                                      .Select(x => (decimal)x.StochRsi.Value)
                                                      .ToList();
                }
                else // Stochastic
                {
                    var stochResults = quotes.GetStoch(
                        parameters.StochPeriod,
                        parameters.StochSmoothK,
                        parameters.StochSmoothD).ToList();

                    oscillatorValues = stochResults.Where(x => x.K.HasValue)
                                                   .Select(x => (decimal)x.K.Value)
                                                   .ToList();

                    oscillatorSignalValues = stochResults.Where(x => x.D.HasValue)
                                                         .Select(x => (decimal)x.D.Value)
                                                         .ToList();
                }

                // Расчет ATR
                var atrResults = quotes.GetAtr(14).ToList();
                var atrValues = atrResults.Where(x => x.Atr.HasValue)
                                          .Select(x => (decimal)x.Atr.Value)
                                          .ToList();

                // Инициализация переменных симуляции
                List<decimal> trades = new List<decimal>();
                decimal balance = INITIAL_BALANCE;
                decimal maxEquity = balance;
                decimal maxDrawdown = 0;

                // Переменные для позиции
                bool inPosition = false;
                decimal entryPrice = 0;
                int entryIndex = 0;
                string positionDirection = "";
                decimal positionLots = 0;
                decimal positionCost = 0;

                // Для отслеживания движения цены
                decimal highestPrice = 0;
                decimal lowestPrice = 0;

                // Для скользящего тейк-профита на входе
                decimal movingTPEntryStartPrice = 0;
                decimal movingTPEntryTargetPrice = 0;
                DateTime movingTPEntryStartTime = DateTime.MinValue;
                bool movingTPEntryActive = false;

                // Для скользящего тейк-профита на выходе
                decimal movingTPExitStartPrice = 0;
                decimal movingTPExitCurrentLevel = 0;
                decimal movingTPExitTargetPrice = 0;
                DateTime movingTPExitStartTime = DateTime.MinValue;
                bool movingTPExitActive = false;

                // Для трейлинг-стопа
                decimal trailingStopBestPrice = 0;
                decimal trailingStopCurrentLevel = 0;
                bool trailingStopActivated = false;

                // Для защитного стоп-лосса при пересечении уровня
                decimal protectiveStopPrice = 0;

                // Статистика
                int winningTrades = 0;
                int losingTrades = 0;
                decimal totalProfit = 0;
                decimal totalLoss = 0;

                // История эквити
                List<decimal> equityHistory = new List<decimal>();
                List<DateTime> equityDates = new List<DateTime>();

                int startIndex = Math.Min(minCandles, _candles.Count - 1);
                equityHistory.Add(balance);
                equityDates.Add(_candles[startIndex].Time);

                // Сброс состояния для пересечений
                _previousOscillatorValue = 0;
                _hasPreviousOscillatorValue = false;

                // ОСНОВНОЙ ЦИКЛ СИМУЛЯЦИИ
                for (int i = startIndex; i < _candles.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var candle = _candles[i];
                        decimal price = candle.Close;

                        // Индекс для индикаторов
                        int idx = i - minCandles;
                        if (idx < 0) continue;

                        // Проверка наличия значений индикаторов
                        if (idx >= rsiValues.Count || idx >= oscillatorValues.Count)
                            continue;

                        decimal rsi = rsiValues[idx];
                        decimal oscillator = oscillatorValues[idx];
                        decimal previousOscillator = _hasPreviousOscillatorValue ? _previousOscillatorValue : oscillator;

                        // Текущий ATR
                        decimal currentAtr = idx < atrValues.Count ? atrValues[idx] : (atrValues.LastOrDefault());

                        // Определяем пересечения уровней
                        bool crossingAboveOverbought = previousOscillator > parameters.StochOverbought &&
                                                       oscillator <= parameters.StochOverbought;

                        bool crossingBelowOversold = previousOscillator < parameters.StochOversold &&
                                                     oscillator >= parameters.StochOversold;

                        // Сохраняем текущее значение для следующей итерации
                        _previousOscillatorValue = oscillator;
                        _hasPreviousOscillatorValue = true;

                        bool isOversold = rsi < parameters.RsiOversold && oscillator < parameters.StochOversold;
                        bool isOverbought = rsi > parameters.RsiOverbought && oscillator > parameters.StochOverbought;

                        // ============================================================
                        // ЛОГИКА ВХОДА
                        // ============================================================
                        if (!inPosition && !movingTPEntryActive)
                        {
                            bool entrySignal = false;
                            string signal = "";
                            decimal entryPriceCandidate = price;
                            bool isLevelCrossingEntry = parameters.EntryOrderType == MoneyGenerator_v5.Strategies.OrderType.LevelCrossingEntry;

                            // Проверка сигнала в зависимости от типа входа
                            if (isLevelCrossingEntry)
                            {
                                // Вход по пересечению уровня
                                if (crossingBelowOversold)
                                {
                                    entrySignal = true;
                                    signal = "LONG (Level Crossing)";
                                    // Защитный стоп для входа
                                    protectiveStopPrice = entryPriceCandidate * (1 - parameters.LevelCrossingEntryProtectiveStopPercent / 100);
                                }
                                else if (crossingAboveOverbought)
                                {
                                    entrySignal = true;
                                    signal = "SHORT (Level Crossing)";
                                    protectiveStopPrice = entryPriceCandidate * (1 + parameters.LevelCrossingEntryProtectiveStopPercent / 100);
                                }
                            }
                            else if (parameters.EntryOrderType == MoneyGenerator_v5.Strategies.OrderType.MovingTakeProfitEntry)
                            {
                                // Скользящий тейк-профит на входе
                                if (isOversold)
                                {
                                    movingTPEntryStartPrice = price;
                                    movingTPEntryTargetPrice = CalculateMovingTPEntryTarget(price, "LONG", currentAtr, parameters);
                                    movingTPEntryStartTime = DateTime.Now;
                                    movingTPEntryActive = true;
                                    entrySignal = false; // Не входим сразу, ждем достижения цели
                                    signal = "LONG (Moving TP Entry)";
                                    //Debug.WriteLine($"[RsiBacktestEngine] 📊 Moving TP Entry LONG: старт={price:F2}, цель={movingTPEntryTargetPrice:F2}");
                                }
                                else if (isOverbought)
                                {
                                    movingTPEntryStartPrice = price;
                                    movingTPEntryTargetPrice = CalculateMovingTPEntryTarget(price, "SHORT", currentAtr, parameters);
                                    movingTPEntryStartTime = DateTime.Now;
                                    movingTPEntryActive = true;
                                    entrySignal = false;
                                    signal = "SHORT (Moving TP Entry)";
                                    //Debug.WriteLine($"[RsiBacktestEngine] 📊 Moving TP Entry SHORT: старт={price:F2}, цель={movingTPEntryTargetPrice:F2}");
                                }
                            }
                            else
                            {
                                // Обычные сигналы (Market, Limit, StopLimit)
                                if (isOversold)
                                {
                                    entrySignal = true;
                                    signal = "LONG";
                                    // Расчет цены входа для Limit/StopLimit
                                    if (parameters.EntryOrderType == MoneyGenerator_v5.Strategies.OrderType.Limit)
                                    {
                                        entryPriceCandidate = price * (1 - parameters.EntryLimitOffsetPercent / 100);
                                    }
                                    else if (parameters.EntryOrderType == MoneyGenerator_v5.Strategies.OrderType.StopLimit)
                                    {
                                        entryPriceCandidate = price * (1 + parameters.EntryStopOffsetPercent / 100);
                                    }
                                }
                                else if (isOverbought)
                                {
                                    entrySignal = true;
                                    signal = "SHORT";
                                    if (parameters.EntryOrderType == MoneyGenerator_v5.Strategies.OrderType.Limit)
                                    {
                                        entryPriceCandidate = price * (1 + parameters.EntryLimitOffsetPercent / 100);
                                    }
                                    else if (parameters.EntryOrderType == MoneyGenerator_v5.Strategies.OrderType.StopLimit)
                                    {
                                        entryPriceCandidate = price * (1 - parameters.EntryStopOffsetPercent / 100);
                                    }
                                }
                            }

                            // Выполнение входа
                            if (entrySignal && entryPriceCandidate > 0)
                            {
                                decimal positionValueRub = _fixedPositionValue;
                                decimal calculatedLots = Math.Floor(positionValueRub / (entryPriceCandidate * lotSize));

                                if (calculatedLots <= 0) continue;

                                positionLots = calculatedLots;
                                positionCost = positionLots * entryPriceCandidate * lotSize;

                                if (balance < positionCost)
                                {
                                    positionLots = Math.Floor(balance / (entryPriceCandidate * lotSize));
                                    positionCost = positionLots * entryPriceCandidate * lotSize;
                                    if (positionLots <= 0) continue;
                                }

                                // ✅ ВХОД
                                entryPrice = entryPriceCandidate;
                                entryIndex = i;
                                positionDirection = signal.Contains("LONG") ? "LONG" : "SHORT";
                                highestPrice = entryPrice;
                                lowestPrice = entryPrice;

                                // Списываем стоимость позиции + комиссию
                                decimal entryCommission = positionCost * COMMISSION_RATE;
                                balance -= positionCost + entryCommission;

                                inPosition = true;

                                // Для Level Crossing Entry - активируем защитный стоп
                                if (isLevelCrossingEntry)
                                {
                                    // Стоп уже рассчитан
                                }

                                // Для Moving TP Entry - если вдруг цена сразу достигла цели
                                if (parameters.EntryOrderType == MoneyGenerator_v5.Strategies.OrderType.MovingTakeProfitEntry)
                                {
                                    movingTPEntryActive = false;
                                }

                                _logger?.LogDebug($"[RsiBacktestEngine] 📈 ВХОД {signal}: " +
                                                  $"позиция={positionLots} лотов, цена={entryPriceCandidate:F2}, " +
                                                  $"стоимость={positionCost:F2}, баланс={balance:F2}");
                            }
                        }

                        // ============================================================
                        // ОБРАБОТКА СКОЛЬЗЯЩЕГО ТЕЙК-ПРОФИТА НА ВХОДЕ
                        // ============================================================
                        if (movingTPEntryActive && !inPosition)
                        {
                            bool entryTriggered = false;
                            string signal = "";

                            if (movingTPEntryStartPrice > 0)
                            {
                                // Для LONG: вход когда цена достигает цели (выше)
                                if (price >= movingTPEntryTargetPrice && price > movingTPEntryStartPrice)
                                {
                                    entryTriggered = true;
                                    signal = "LONG (Moving TP)";
                                }
                                // Для SHORT: вход когда цена достигает цели (ниже)
                                else if (price <= movingTPEntryTargetPrice && price < movingTPEntryStartPrice)
                                {
                                    entryTriggered = true;
                                    signal = "SHORT (Moving TP)";
                                }

                                // Обновляем экстремум для скользящей цели
                                if (movingTPEntryTargetPrice > 0)
                                {
                                    if (price < movingTPEntryStartPrice && movingTPEntryTargetPrice > 0)
                                    {
                                        // Для LONG: новый минимум
                                        if (price < movingTPEntryStartPrice)
                                        {
                                            movingTPEntryStartPrice = price;
                                            movingTPEntryTargetPrice = CalculateMovingTPEntryTarget(price, "LONG", currentAtr, parameters);
                                        }
                                    }
                                    else if (price > movingTPEntryStartPrice && movingTPEntryTargetPrice > 0)
                                    {
                                        // Для SHORT: новый максимум
                                        if (price > movingTPEntryStartPrice)
                                        {
                                            movingTPEntryStartPrice = price;
                                            movingTPEntryTargetPrice = CalculateMovingTPEntryTarget(price, "SHORT", currentAtr, parameters);
                                        }
                                    }
                                }

                                // Проверка таймаута
                                if ((DateTime.Now - movingTPEntryStartTime).TotalMinutes > parameters.MovingTPEntryTimeoutMinutes)
                                {
                                    movingTPEntryActive = false;
                                    //Debug.WriteLine($"[RsiBacktestEngine] ⏰ Таймаут Moving TP Entry");
                                }
                            }

                            if (entryTriggered)
                            {
                                decimal positionValueRub = _fixedPositionValue;
                                decimal calculatedLots = Math.Floor(positionValueRub / (price * lotSize));

                                if (calculatedLots > 0)
                                {
                                    positionLots = calculatedLots;
                                    positionCost = positionLots * price * lotSize;

                                    if (balance >= positionCost)
                                    {
                                        entryPrice = price;
                                        entryIndex = i;
                                        positionDirection = signal.Contains("LONG") ? "LONG" : "SHORT";
                                        highestPrice = entryPrice;
                                        lowestPrice = entryPrice;

                                        decimal entryCommission = positionCost * COMMISSION_RATE;
                                        balance -= positionCost + entryCommission;

                                        inPosition = true;
                                        movingTPEntryActive = false;

                                        _logger?.LogDebug($"[RsiBacktestEngine] 📈 ВХОД {signal}: " +
                                                          $"позиция={positionLots} лотов, цена={price:F2}, баланс={balance:F2}");
                                    }
                                }
                            }
                        }

                        // ============================================================
                        // ЛОГИКА ВЫХОДА
                        // ============================================================
                        if (inPosition)
                        {
                            bool shouldExit = false;
                            string exitReason = "";
                            decimal exitPrice = price;

                            // Проверяем тип выхода
                            switch (parameters.ExitOrderType)
                            {
                                case MoneyGenerator_v5.Strategies.OrderType.LevelCrossingExit:
                                    // Выход по пересечению уровня
                                    if (positionDirection == "LONG" && crossingAboveOverbought)
                                    {
                                        shouldExit = true;
                                        exitReason = "Level Crossing Exit (Overbought)";
                                    }
                                    else if (positionDirection == "SHORT" && crossingBelowOversold)
                                    {
                                        shouldExit = true;
                                        exitReason = "Level Crossing Exit (Oversold)";
                                    }
                                    // Проверка защитного стоп-лосса для Level Crossing Exit
                                    else if (positionDirection == "LONG" && price <= entryPrice * (1 - parameters.LevelCrossingExitProtectiveStopPercent / 100))
                                    {
                                        shouldExit = true;
                                        exitReason = "Level Crossing Protective Stop";
                                    }
                                    else if (positionDirection == "SHORT" && price >= entryPrice * (1 + parameters.LevelCrossingExitProtectiveStopPercent / 100))
                                    {
                                        shouldExit = true;
                                        exitReason = "Level Crossing Protective Stop";
                                    }
                                    break;

                                case MoneyGenerator_v5.Strategies.OrderType.MovingTakeProfitExit:
                                    // Скользящий тейк-профит на выходе
                                    if (!movingTPExitActive)
                                    {
                                        // Инициализация
                                        movingTPExitStartPrice = entryPrice;
                                        movingTPExitCurrentLevel = entryPrice;
                                        movingTPExitTargetPrice = CalculateMovingTPExitTarget(entryPrice, positionDirection, currentAtr, parameters);
                                        movingTPExitStartTime = DateTime.Now;
                                        movingTPExitActive = true;
                                        //Debug.WriteLine($"[RsiBacktestEngine] 📊 Moving TP Exit активирован: цель={movingTPExitTargetPrice:F2}");
                                    }
                                    else
                                    {
                                        // Обновление уровня
                                        if (positionDirection == "LONG" && price > movingTPExitCurrentLevel)
                                        {
                                            movingTPExitCurrentLevel = price;
                                            movingTPExitTargetPrice = CalculateMovingTPExitTarget(price, "LONG", currentAtr, parameters);
                                            //Debug.WriteLine($"[RsiBacktestEngine] 📈 Moving TP Exit LONG: новый уровень={price:F2}, цель={movingTPExitTargetPrice:F2}");
                                        }
                                        else if (positionDirection == "SHORT" && price < movingTPExitCurrentLevel)
                                        {
                                            movingTPExitCurrentLevel = price;
                                            movingTPExitTargetPrice = CalculateMovingTPExitTarget(price, "SHORT", currentAtr, parameters);
                                            //Debug.WriteLine($"[RsiBacktestEngine] 📉 Moving TP Exit SHORT: новый уровень={price:F2}, цель={movingTPExitTargetPrice:F2}");
                                        }

                                        // Проверка достижения цели
                                        if (positionDirection == "LONG" && price <= movingTPExitTargetPrice)
                                        {
                                            shouldExit = true;
                                            exitReason = "Moving TP Exit (LONG)";
                                        }
                                        else if (positionDirection == "SHORT" && price >= movingTPExitTargetPrice)
                                        {
                                            shouldExit = true;
                                            exitReason = "Moving TP Exit (SHORT)";
                                        }

                                        // Проверка таймаута
                                        if ((DateTime.Now - movingTPExitStartTime).TotalMinutes > parameters.MovingTPExitTimeoutMinutes)
                                        {
                                            shouldExit = true;
                                            exitReason = "Moving TP Exit Timeout";
                                        }

                                        // Закрытие при смене сигнала
                                        if (parameters.CloseOnSignalReversal)
                                        {
                                            if (positionDirection == "LONG" && isOverbought)
                                            {
                                                shouldExit = true;
                                                exitReason = "Signal Reversal (Overbought)";
                                            }
                                            else if (positionDirection == "SHORT" && isOversold)
                                            {
                                                shouldExit = true;
                                                exitReason = "Signal Reversal (Oversold)";
                                            }
                                        }
                                    }
                                    break;

                                case MoneyGenerator_v5.Strategies.OrderType.TrailingStopExit:
                                    // Трейлинг-стоп на выходе
                                    if (!trailingStopActivated)
                                    {
                                        // Проверяем, достигнута ли активационная прибыль
                                        decimal currentPnLPercent = positionDirection == "LONG" ?
                                            (price - entryPrice) / entryPrice * 100 :
                                            (entryPrice - price) / entryPrice * 100;

                                        if (currentPnLPercent >= parameters.TrailingStopExitActivationPercent)
                                        {
                                            trailingStopActivated = true;
                                            trailingStopBestPrice = price;
                                            trailingStopCurrentLevel = CalculateTrailingStopLevel(price, positionDirection, currentAtr, parameters);
                                            //Debug.WriteLine($"[RsiBacktestEngine] 🚀 Трейлинг-стоп активирован: {trailingStopCurrentLevel:F2}");
                                        }
                                        // Защитный стоп до активации трейлинга
                                        else if (positionDirection == "LONG" && price <= entryPrice * (1 - parameters.ProtectiveStopPercent / 100))
                                        {
                                            shouldExit = true;
                                            exitReason = "Protective Stop (pre-trailing)";
                                        }
                                        else if (positionDirection == "SHORT" && price >= entryPrice * (1 + parameters.ProtectiveStopPercent / 100))
                                        {
                                            shouldExit = true;
                                            exitReason = "Protective Stop (pre-trailing)";
                                        }
                                    }
                                    else
                                    {
                                        // Обновление трейлинг-стопа
                                        if (positionDirection == "LONG" && price > trailingStopBestPrice)
                                        {
                                            trailingStopBestPrice = price;
                                            trailingStopCurrentLevel = CalculateTrailingStopLevel(price, "LONG", currentAtr, parameters);
                                            //Debug.WriteLine($"[RsiBacktestEngine] 📈 Трейлинг-стоп повышен: {trailingStopCurrentLevel:F2}");
                                        }
                                        else if (positionDirection == "SHORT" && price < trailingStopBestPrice)
                                        {
                                            trailingStopBestPrice = price;
                                            trailingStopCurrentLevel = CalculateTrailingStopLevel(price, "SHORT", currentAtr, parameters);
                                            //Debug.WriteLine($"[RsiBacktestEngine] 📉 Трейлинг-стоп понижен: {trailingStopCurrentLevel:F2}");
                                        }

                                        // Проверка срабатывания трейлинг-стопа
                                        if (positionDirection == "LONG" && price <= trailingStopCurrentLevel)
                                        {
                                            shouldExit = true;
                                            exitReason = "Trailing Stop (LONG)";
                                        }
                                        else if (positionDirection == "SHORT" && price >= trailingStopCurrentLevel)
                                        {
                                            shouldExit = true;
                                            exitReason = "Trailing Stop (SHORT)";
                                        }
                                    }
                                    break;

                                case MoneyGenerator_v5.Strategies.OrderType.Market:
                                default:
                                    // Обычный выход по тейк-профиту и стоп-лоссу
                                    decimal takeProfitPrice = 0;
                                    decimal stopLossPrice = 0;

                                    // Расчет тейк-профита
                                    if (positionDirection == "LONG")
                                    {
                                        takeProfitPrice = entryPrice + CalculateTakeProfitDistance(entryPrice, currentAtr, parameters);
                                        stopLossPrice = entryPrice - CalculateStopLossDistance(entryPrice, currentAtr, parameters);
                                    }
                                    else
                                    {
                                        takeProfitPrice = entryPrice - CalculateTakeProfitDistance(entryPrice, currentAtr, parameters);
                                        stopLossPrice = entryPrice + CalculateStopLossDistance(entryPrice, currentAtr, parameters);
                                    }

                                    // Проверка тейк-профита
                                    if (positionDirection == "LONG" && price >= takeProfitPrice)
                                    {
                                        shouldExit = true;
                                        exitReason = "Take Profit";
                                    }
                                    else if (positionDirection == "SHORT" && price <= takeProfitPrice)
                                    {
                                        shouldExit = true;
                                        exitReason = "Take Profit";
                                    }
                                    // Проверка стоп-лосса
                                    else if (positionDirection == "LONG" && price <= stopLossPrice)
                                    {
                                        shouldExit = true;
                                        exitReason = "Stop Loss";
                                    }
                                    else if (positionDirection == "SHORT" && price >= stopLossPrice)
                                    {
                                        shouldExit = true;
                                        exitReason = "Stop Loss";
                                    }
                                    // Закрытие при смене сигнала
                                    else if (parameters.CloseOnSignalReversal)
                                    {
                                        if (positionDirection == "LONG" && isOverbought)
                                        {
                                            shouldExit = true;
                                            exitReason = "Signal Reversal (Overbought)";
                                        }
                                        else if (positionDirection == "SHORT" && isOversold)
                                        {
                                            shouldExit = true;
                                            exitReason = "Signal Reversal (Oversold)";
                                        }
                                    }
                                    break;
                            }

                            // ============================================================
                            // ВЫПОЛНЕНИЕ ВЫХОДА
                            // ============================================================
                            if (shouldExit)
                            {
                                // Расчет P&L
                                decimal pnl;
                                if (positionDirection == "LONG")
                                    pnl = (price - entryPrice) * positionLots * lotSize;
                                else
                                    pnl = (entryPrice - price) * positionLots * lotSize;

                                decimal exitValue = positionLots * price * lotSize;
                                decimal exitCommission = exitValue * COMMISSION_RATE;
                                decimal entryCommission = positionCost * COMMISSION_RATE;
                                decimal totalCommission = entryCommission + exitCommission;
                                decimal pnlAfterCommission = pnl - totalCommission;

                                // Возвращаем стоимость позиции + P&L - комиссия выхода
                                balance += positionCost + pnl - exitCommission;

                                // Статистика
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

                                // Обновляем максимальную просадку
                                if (balance > maxEquity)
                                    maxEquity = balance;
                                decimal drawdown = (maxEquity - balance) / maxEquity * 100;
                                if (drawdown > maxDrawdown)
                                    maxDrawdown = drawdown;

                                _logger?.LogDebug($"[RsiBacktestEngine] 📉 ВЫХОД: {exitReason}, " +
                                                  $"P&L={pnlAfterCommission:F2}, баланс={balance:F2}");

                                // Сбрасываем позицию
                                inPosition = false;
                                positionDirection = "";
                                entryPrice = 0;
                                entryIndex = 0;
                                positionLots = 0;
                                positionCost = 0;
                                movingTPExitActive = false;
                                trailingStopActivated = false;
                            }
                        }

                        // Сохраняем эквити
                        if (i % 10 == 0 || i == _candles.Count - 1)
                        {
                            decimal currentEquity = inPosition ? balance + positionCost : balance;
                            equityHistory.Add(currentEquity);
                            equityDates.Add(_candles[i].Time);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, $"[RsiBacktestEngine] ⚠️ Ошибка на итерации {i}: {ex.Message}");
                    }
                }

                // Закрываем открытую позицию в конце
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

                    _logger?.LogDebug($"[RsiBacktestEngine] 📉 Принудительный выход: P&L={pnlAfterCommission:F2}, баланс={balance:F2}");
                }

                // ЛОГИРУЕМ СТАТИСТИКУ
                _logger?.LogInformation($"[RsiBacktestEngine] 📊 Статистика симуляции:");
                _logger?.LogInformation($"    - Фиксированная позиция: {_fixedPositionValue:F2} RUB");
                _logger?.LogInformation($"    - Сделок: {trades.Count}");
                _logger?.LogInformation($"    - Итоговый баланс: {balance:F2} RUB");
                _logger?.LogInformation($"    - P&L: {balance - INITIAL_BALANCE:F2} RUB");
                _logger?.LogInformation($"    - WinRate: {(trades.Count > 0 ? (decimal)winningTrades / trades.Count * 100 : 0):F1}%");
                _logger?.LogInformation($"    - Макс. просадка: {maxDrawdown:F1}%");
                _logger?.LogInformation($"    - Время выполнения: {stopwatch.ElapsedMilliseconds} мс");

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

                // Расчет годовой доходности
                if (_candles != null && _candles.Count > 0 && INITIAL_BALANCE > 0)
                {
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

                // Расчет коэффициента Шарпа
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
                _logger?.LogError(ex, $"[RsiBacktestEngine] ❌ Критическая ошибка в SimulateTradingAsync");
                return result;
            }
        }

        #endregion

        #region Вспомогательные методы расчета

        private decimal CalculateTakeProfitDistance(decimal entryPrice, decimal atr, RsiStrategyParams parameters)
        {
            switch (parameters.TakeProfitCalculationType)
            {
                case PriceCalculationType.Percentage:
                    return entryPrice * (parameters.TakeProfitPercent / 100);
                case PriceCalculationType.ATR:
                    return atr * parameters.AtrMultiplier;
                default:
                    return entryPrice * 0.02m;
            }
        }

        private decimal CalculateStopLossDistance(decimal entryPrice, decimal atr, RsiStrategyParams parameters)
        {
            switch (parameters.StopLossCalculationType)
            {
                case PriceCalculationType.Percentage:
                    return entryPrice * (parameters.StopLossPercent / 100);
                case PriceCalculationType.ATR:
                    return atr * parameters.AtrMultiplier;
                default:
                    return entryPrice * 0.01m;
            }
        }

        private decimal CalculateMovingTPEntryTarget(decimal price, string direction, decimal atr, RsiStrategyParams parameters)
        {
            decimal offset = parameters.MovingTPEntryCalculationType switch
            {
                PriceCalculationType.Percentage => price * (parameters.MovingTPEntryTargetPercent / 100),
                PriceCalculationType.ATR => atr * parameters.AtrMultiplier,
                _ => price * 0.02m
            };

            return direction == "LONG" ? price + offset : price - offset;
        }

        private decimal CalculateMovingTPExitTarget(decimal price, string direction, decimal atr, RsiStrategyParams parameters)
        {
            decimal offset = parameters.MovingTPExitCalculationType switch
            {
                PriceCalculationType.Percentage => price * (parameters.MovingTPExitStartPercent / 100),
                PriceCalculationType.ATR => atr * parameters.AtrMultiplier,
                _ => price * 0.02m
            };

            return direction == "LONG" ? price - offset : price + offset;
        }

        private decimal CalculateTrailingStopLevel(decimal price, string direction, decimal atr, RsiStrategyParams parameters)
        {
            decimal distance = parameters.TrailingStopExitCalculationType switch
            {
                PriceCalculationType.Percentage => price * (parameters.TrailingStopExitDistancePercent / 100),
                PriceCalculationType.ATR => atr * parameters.AtrMultiplier,
                _ => price * 0.01m
            };

            return direction == "LONG" ? price - distance : price + distance;
        }

        #endregion

        #region Вспомогательные классы

        private class RsiStrategyParams
        {
            // RSI параметры
            public int RsiPeriod { get; set; } = 14;
            public decimal RsiOverbought { get; set; } = 70;
            public decimal RsiOversold { get; set; } = 30;

            // Stochastic параметры
            public int StochPeriod { get; set; } = 14;
            public decimal StochOverbought { get; set; } = 80;
            public decimal StochOversold { get; set; } = 20;
            public int StochSmoothK { get; set; } = 3;
            public int StochSmoothD { get; set; } = 3;
            public OscillatorType OscillatorType { get; set; } = OscillatorType.Stochastic;

            // Параметры входа
            public MoneyGenerator_v5.Strategies.OrderType EntryOrderType { get; set; } = MoneyGenerator_v5.Strategies.OrderType.Market;
            public decimal EntryLimitOffsetPercent { get; set; } = 0.1m;
            public decimal EntryStopOffsetPercent { get; set; } = 0.2m;
            public decimal EntrySlippage { get; set; } = 0.01m;

            // Параметры выхода
            public MoneyGenerator_v5.Strategies.OrderType ExitOrderType { get; set; } = MoneyGenerator_v5.Strategies.OrderType.Market;
            public decimal ExitSlippage { get; set; } = 0.01m;

            // Moving Take Profit Entry
            public PriceCalculationType MovingTPEntryCalculationType { get; set; } = PriceCalculationType.ATR;
            public decimal MovingTPEntryTargetPercent { get; set; } = 2.0m;
            public decimal MovingTPEntrySlippage { get; set; } = 0.01m;
            public int MovingTPEntryTimeoutMinutes { get; set; } = 60;

            // Moving Take Profit Exit
            public PriceCalculationType MovingTPExitCalculationType { get; set; } = PriceCalculationType.ATR;
            public decimal MovingTPExitStartPercent { get; set; } = 2.0m;
            public decimal MovingTPExitSlippage { get; set; } = 0.01m;
            public int MovingTPExitTimeoutMinutes { get; set; } = 60;

            // Trailing Stop Exit
            public PriceCalculationType TrailingStopExitCalculationType { get; set; } = PriceCalculationType.ATR;
            public decimal TrailingStopExitDistancePercent { get; set; } = 0.5m;
            public decimal TrailingStopExitSlippage { get; set; } = 0.01m;
            public decimal TrailingStopExitActivationPercent { get; set; } = 1.0m;
            public decimal ProtectiveStopPercent { get; set; } = 0.5m;

            // Take Profit / Stop Loss
            public PriceCalculationType TakeProfitCalculationType { get; set; } = PriceCalculationType.ATR;
            public decimal TakeProfitPercent { get; set; } = 2.0m;
            public decimal TakeProfitActivationPrice { get; set; } = 0m;
            public decimal TakeProfitSlippage { get; set; } = 0.01m;

            public PriceCalculationType StopLossCalculationType { get; set; } = PriceCalculationType.ATR;
            public decimal StopLossPercent { get; set; } = 1.0m;
            public decimal StopLossActivationPrice { get; set; } = 0m;
            public decimal StopLossSlippage { get; set; } = 0.01m;

            // Общие
            public decimal AtrMultiplier { get; set; } = 1.5m;
            public decimal OrderSizePercent { get; set; } = 10m;
            public bool CloseOnSignalReversal { get; set; } = false;

            // Level Crossing
            public decimal LevelCrossingEntryProtectiveStopPercent { get; set; } = 0.25m;
            public decimal LevelCrossingEntryProtectiveStopDistancePercent { get; set; } = 0.25m;
            public decimal LevelCrossingExitProtectiveStopPercent { get; set; } = 0.25m;
            public decimal LevelCrossingExitProtectiveStopDistancePercent { get; set; } = 0.25m;
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

        #endregion
    }
}