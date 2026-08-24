using System;

namespace MoneyGenerator_v5.Models
{
    public class EquityRecord
    {
        public int Id { get; set; }
        public string? Provider { get; set; } // "Тинькофф", "Финам", "Алор"
        public string? AccountId { get; set; } // ID счета
        public string? AccountType { get; set; } // "Sandbox" или "Real"
        public decimal Balance { get; set; }
        public DateTime RecordTime { get; set; }
        public string? Currency { get; set; } // "RUB", "USD" и т.д.
    }
}