using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
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
