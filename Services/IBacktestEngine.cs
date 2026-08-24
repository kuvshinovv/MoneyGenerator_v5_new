using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Models.MoneyGenerator_v5.Models;
using MoneyGenerator_v5.ViewModels;

namespace MoneyGenerator_v5.Services
{
    /// <summary>
    /// Интерфейс движка бэктеста для стратегий
    /// </summary>
    public interface IBacktestEngine : IDisposable
    {
        /// <summary>
        /// Инициализирует движок с данными
        /// </summary>
        Task InitializeAsync(
            StrategyViewModel strategyViewModel,
            OptimizationDataCache dataCache,
            ILogger logger);

        /// <summary>
        /// Выполняет бэктест с указанными параметрами
        /// </summary>
        Task<OptimizationResult> RunBacktestAsync(
            Dictionary<string, decimal> parameters,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Возвращает список поддерживаемых параметров
        /// </summary>
        IEnumerable<string> GetSupportedParameters();

        /// <summary>
        /// Проверяет валидность набора параметров
        /// </summary>
        bool ValidateParameters(Dictionary<string, decimal> parameters);
    }

    /// <summary>
    /// Фабрика для создания движков бэктеста
    /// </summary>
    public interface IBacktestEngineFactory
    {
        IBacktestEngine CreateEngine(string strategyType);
    }
}