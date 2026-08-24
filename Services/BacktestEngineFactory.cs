// Файл: Services/BacktestEngineFactory.cs (обновленный)
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Strategies;
using MoneyGenerator_v5.ViewModels;
using System;

namespace MoneyGenerator_v5.Services
{
    public class BacktestEngineFactory : IBacktestEngineFactory
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IProvirerService _provider;

        public BacktestEngineFactory(ILoggerFactory loggerFactory, IProvirerService provider)
        {
            _loggerFactory = loggerFactory;
            _provider = provider;
        }

        public IBacktestEngine CreateEngine(string strategyType)
        {
            if (string.IsNullOrEmpty(strategyType))
                throw new ArgumentException("Strategy type cannot be null or empty", nameof(strategyType));

            switch (strategyType)
            {
                case "PairsTrading":
                    return new PairsTradingBacktestEngine(_loggerFactory?.CreateLogger<PairsTradingBacktestEngine>(), _provider);

                case "MA":
                    return new MaBacktestEngine();

                case "RSI":
                    // TODO: Реализовать RsiBacktestEngine
                    throw new NotSupportedException($"RSI стратегия пока не поддерживается для оптимизации");

                case "Rating":
                    // TODO: Реализовать RatingBacktestEngine
                    throw new NotSupportedException($"Rating стратегия пока не поддерживается для оптимизации");

                default:
                    throw new NotSupportedException($"Стратегия '{strategyType}' не поддерживается для оптимизации");
            }
        }
    }
}