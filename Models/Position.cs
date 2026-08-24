using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public class Position
    {
        public string? AccountId { get; set; }
        public string? InstrumentUid { get; set; }
        public string? Figi { get; set; }
        public string? Ticker { get; set; }
        public string? Name { get; set; }
        public string? Currency { get; set; }
        public int Quantity { get; set; } // Количество в лотах
        public decimal CurrentPrice { get; set; }
        public decimal AveragePrice { get; set; }
        public decimal ExpectedYield { get; set; }
        public decimal CurrentNkd { get; set; }
        public string? InstrumentType { get; set; }
        public DateTime LastUpdate { get; set; }
        public DealStatus? Status { get; set; }
        public string? Comment { get; set; }
        public int LotSize { get; set; } = 1;

        // Для отображения в UI
        public decimal CurrentValue => CurrentPrice * Quantity * LotSize;
        public decimal TotalValue => CurrentValue + CurrentNkd;
        public decimal PnL => ExpectedYield;
        public decimal PnLPercent => AveragePrice > 0 ? (PnL / (AveragePrice * Quantity * LotSize)) * 100 : 0;

        public decimal TakeProfitPrice { get;  set; }
        public decimal StopLossPrice { get;  set; }
        public decimal BestPrice { get;  set; }
        public decimal EntryPrice { get;  set; }
        public DateTime? EntryDateTime { get; set; }
        public decimal? ExitPrice { get; set; }
        public DateTime? ExitDateTime { get; set; }
        public string? Direction { get;  set; }
        public decimal UnrealizedPnL { get;  set; }
        public string? EntryOrderId { get; set; }
        public string? ExitOrderId { get; set; }  // ID ордера выхода (опционально)
        public object? Id { get;  set; }
        public string? EntryReason { get; set; }
        public string? ExitReason { get; set; }



        public string? Strategy { get; set; }

        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public decimal? ClosedPnL { get; set; }

        public decimal? ClosedPnLPercent { get; set; }






        public override string ToString() =>
            $"{Ticker}: {Quantity} лотов по {CurrentPrice}";

        
        
    }
}
