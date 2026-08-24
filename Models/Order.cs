using MoneyGenerator_v5.Strategies;
using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public class Order
    {
        public string? Id { get; set; } 
        public string? OrderId { get; set; }
        public string?Type { get; set; } // Market, Limit, Stop
        public string? Direction { get; set; } // Buy, Sell
        public int Quantity { get; set; }
        public int ExecutedQuantity { get; set; }
        public decimal Price { get; set; }
        public DateTime Time { get; set; }
        public string? Status { get; set; } // Active, Filled, Cancelled
        public string? InstrumentUid { get; internal set; }
        public decimal TakeProfitPrice { get; internal set; }
        public decimal StopLossPrice { get; internal set; }
        public string? OrderType { get; internal set; }
        public string? AccountId { get; internal set; }
        public string? Figi { get; internal set; }
        public int AveragePrice { get; internal set; }
        public decimal Commission { get; internal set; }
        public object Message { get; internal set; }
        public DateTime? LastUpdateTime { get; internal set; }
        public string? Ticker { get; internal set; }
        public string? InstrumentName { get; internal set; }

        public bool IsEntryOrder { get; set; } = false;
        public bool IsExitOrder { get; set; } = false;
        public string? EntryReason { get; set; }
        public string? ExitReason { get; set; }


    }
}
