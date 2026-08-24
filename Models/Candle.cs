using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MoneyGenerator_v5.Models
{
    public class Candle
    {
        public DateTime Time { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public bool IsClosed { get; set; }
        public bool IsEmpty { get; set; } // Новое свойство для пустых свечей
        public string? Timeframe { get; internal set; }
        public string? Ticker { get; internal set; }
        public string? InstrumentUid { get; internal set; }
        public long Id { get; internal set; }
    }
}
