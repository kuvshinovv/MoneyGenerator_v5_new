using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public class CandleUpdate
    {
        public string? InstrumentId { get; set; }
        public decimal LastPrice { get; set; }
        public decimal Volume { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Open { get; set; }
        public decimal Close { get; set; }
        public DateTime Time { get; set; }
        public DateTime LastTradeTime { get; set; }
        public bool IsComplete { get; set; }
        public string? Timeframe { get; set; }
    }
}
