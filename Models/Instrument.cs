using System;

namespace MoneyGenerator_v5.Models
{
    public class Instrument
    {
        public string? Id { get; set; }
        public string? Ticker { get; set; }
        public string? Name { get; set; }
        public string? Exchange { get; set; }
        public string? Currency { get; set; }
        public InstrumentType Type { get; set; }

        public string DisplayName => $"{Ticker} ({Exchange}) - {Name}";

        public string Uid { get; internal set; }
        public string ClassCode { get; internal set; }
        public string Figi { get; internal set; }
        public int PriceStep { get; internal set; }
        public int LotSize { get; internal set; }
        public Tinkoff.InvestApi.V1.Quotation MinStepPrice { get; internal set; }


        public Tinkoff.InvestApi.V1.MoneyValue? InitialMarginOnBuy  { get; internal set; } // гарантиное обеспечение при покупке
        public Tinkoff.InvestApi.V1.MoneyValue? InitialMarginOnSell { get; internal set; }// гарантиное обеспечение при продаже
        public Tinkoff.InvestApi.V1.Quotation? MinPriceIncrementAmount { get; internal set; } // Стоимость шага цены
        public Tinkoff.InvestApi.V1.Quotation? MinPriceIncrement { get; internal set; }  // Шаг цены
        public object MinLotSize { get; internal set; }
    }

    public enum InstrumentType
    {
        Share,
        Future,
        Currency
    }
}