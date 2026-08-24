using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    namespace MoneyGenerator_v5.Models
    {
        /// <summary>
        /// Кэшированная модель для парной торговли
        /// </summary>
        public class PairsModel
        {
            public int LookbackPeriod { get; set; }
            public decimal HedgeRatio { get; set; }
            public decimal SpreadMean { get; set; }
            public decimal SpreadStd { get; set; }
            public decimal Correlation { get; set; }
            public DateTime BuildTime { get; set; }
            public bool IsValid => HedgeRatio > 0 && SpreadStd > 0;
        }

        /// <summary>
        /// Кэшированные данные для оптимизации
        /// </summary>
        public class OptimizationDataCache
        {
            public Dictionary<string, List<Candle>> Candles { get; set; } = new();
            public Dictionary<string, object> Models { get; set; } = new();
            public DateTime LoadTime { get; set; }
            public bool IsLoaded { get; set; }

            // Для парной торговли
            public List<Candle> CandlesA { get; set; }
            public List<Candle> CandlesB { get; set; }
            public List<AlignedCandleData> AlignedData { get; set; }
            public Dictionary<int, PairsModel> PairsModels { get; set; } = new();

            // ✅ ДОБАВЛЯЕМ: Хранение тикеров инструментов для бэктеста
            public string InstrumentATicker { get; set; }
            public string InstrumentBTicker { get; set; }
            public decimal LotSize { get; set; } = 1m;
        }

        /// <summary>
        /// Выровненные данные для парной торговли
        /// </summary>
        public class AlignedCandleData
        {
            public DateTime Time { get; set; }
            public decimal PriceA { get; set; }
            public decimal PriceB { get; set; }
        }
    }
}
