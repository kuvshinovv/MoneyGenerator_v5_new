using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public class DealMarker
    {
        public DateTime EntryTime { get; set; }
        public double EntryPrice { get; set; }
        public string Direction { get; set; }
        public int Quantity { get; set; }
        public bool IsClosed { get; set; }
        public DateTime? ExitTime { get; set; }
        public double? ExitPrice { get; set; }
    }
}
