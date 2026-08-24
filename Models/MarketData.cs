using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public class MarketData
    {
        public string? InstrumentId { get; set; }
        public string? TradingStatus { get; set; }
        public bool IsTrading { get; set; }
        public decimal LastPrice { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;


        // Дополнительные поля для свечей
        public decimal CandleOpen { get; set; }
        public decimal CandleHigh { get; set; }
        public decimal CandleLow { get; set; }
        public decimal CandleClose { get; set; }
        public long CandleVolume { get; set; }
        public DateTime CandleTime { get; set; }
        public bool CandleIsComplete { get; set; }

        // Поля для стакана
        public int OrderBookDepth { get; set; }
        public DateTime OrderBookTime { get; set; }
        public List<decimal>? OrderBookBids { get; set; }
        public List<decimal>? OrderBookAsks { get; set; }

        // Поля для тиков
        public long TradeQuantity { get; set; }
        public string? TradeDirection { get; set; }
        public DateTime TradeTime { get; set; }
        public DateTime Time { get; internal set; }


        public string? InstrumentUid { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal Volume { get; set; }

    

    }
}
