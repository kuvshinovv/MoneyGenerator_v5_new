// Models/ProcessedOperation.cs
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace MoneyGenerator_v5.Models
{
    public class ProcessedOperation : ObservableObject
    {
        private string _id;
        private string _ticker;
        private string _instrumentUid;
        private string _strategy;
        private string _comment;
        private string _status;
        private DateTime _openDate;
        private decimal _openPrice;
        private decimal _quantity;
        private decimal _buyAmount;
        private DateTime? _closeDate;
        private decimal? _closePrice;
        private decimal? _sellAmount;
        private decimal _buyFee;
        private decimal _sellFee;
        private decimal _totalFee;
        private decimal _grossProfit;
        private decimal _netProfit;
        private decimal _netProfitPercent;
        private string _displayDirection;
        private decimal _currentPrice;
        private string _buyOperationId;
        private string _sellOperationId;
        private string _direction;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string Ticker
        {
            get => _ticker;
            set => SetProperty(ref _ticker, value);
        }

        public string InstrumentUid
        {
            get => _instrumentUid;
            set => SetProperty(ref _instrumentUid, value);
        }

        public string Strategy
        {
            get => _strategy;
            set => SetProperty(ref _strategy, value);
        }

        public string Comment
        {
            get => _comment;
            set => SetProperty(ref _comment, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public DateTime OpenDate
        {
            get => _openDate;
            set => SetProperty(ref _openDate, value);
        }

        public decimal OpenPrice
        {
            get => _openPrice;
            set => SetProperty(ref _openPrice, value);
        }

        public decimal Quantity
        {
            get => _quantity;
            set => SetProperty(ref _quantity, value);
        }

        public decimal BuyAmount
        {
            get => _buyAmount;
            set => SetProperty(ref _buyAmount, value);
        }

        public DateTime? CloseDate
        {
            get => _closeDate;
            set => SetProperty(ref _closeDate, value);
        }

        public decimal? ClosePrice
        {
            get => _closePrice;
            set => SetProperty(ref _closePrice, value);
        }

        public decimal? SellAmount
        {
            get => _sellAmount;
            set => SetProperty(ref _sellAmount, value);
        }

        public decimal BuyFee
        {
            get => _buyFee;
            set => SetProperty(ref _buyFee, value);
        }

        public decimal SellFee
        {
            get => _sellFee;
            set => SetProperty(ref _sellFee, value);
        }

        public decimal TotalFee
        {
            get => _totalFee;
            set => SetProperty(ref _totalFee, value);
        }

        public decimal GrossProfit
        {
            get => _grossProfit;
            set => SetProperty(ref _grossProfit, value);
        }

        public decimal NetProfit
        {
            get => _netProfit;
            set => SetProperty(ref _netProfit, value);
        }

        public decimal NetProfitPercent
        {
            get => _netProfitPercent;
            set => SetProperty(ref _netProfitPercent, value);
        }

        public string DisplayDirection
        {
            get
            {
                if (Direction == "Long") return "📈 Long";
                if (Direction == "Short") return "📉 Short";
                if (Status?.Contains("Open") == true) return "🟡 Открыта";
                return "✅ Закрыта";
            }
            set => SetProperty(ref _displayDirection, value);
        }

        public decimal CurrentPrice
        {
            get => _currentPrice;
            set => SetProperty(ref _currentPrice, value);
        }

        public string BuyOperationId
        {
            get => _buyOperationId;
            set => SetProperty(ref _buyOperationId, value);
        }

        public string SellOperationId
        {
            get => _sellOperationId;
            set => SetProperty(ref _sellOperationId, value);
        }

        public string Direction
        {
            get => _direction;
            set => SetProperty(ref _direction, value);
        }




        


        


        // Вычисляемые свойства для отображения
        public string StatusSymbol => Status?.Contains("Open") == true ? "🟢" : "🔴";
        public string StatusDisplay => Status?.Contains("Open") == true ? "Открыта" : "Закрыта";
        public string DirectionSymbol => Direction == "Long" ? "▲" : "▼";
        public string DirectionColor => Direction == "Long" ? "#FF4CAF50" : "#FFFF5252";
        public string ProfitColor => NetProfit >= 0 ? "DarkGreen" : "Red";
        public string ProfitBgColor => NetProfit >= 0 ? "#E8F5E9" : "#FFEBEE";

        // Форматированные строки
        public string FormattedOpenDate => OpenDate.ToString("dd.MM.yyyy HH:mm");
        public string FormattedCloseDate => CloseDate?.ToString("dd.MM.yyyy HH:mm") ?? "—";
        public string FormattedOpenPrice => OpenPrice.ToString("F2");
        public string FormattedClosePrice => ClosePrice?.ToString("F2") ?? "—";
        public string FormattedQuantity => Quantity.ToString("F0");
        public string FormattedBuyAmount => BuyAmount.ToString("F2");
        public string FormattedSellAmount => SellAmount?.ToString("F2") ?? "—";
        public string FormattedBuyFee => BuyFee.ToString("F2");
        public string FormattedSellFee => SellFee.ToString("F2");
        public string FormattedTotalFee => TotalFee.ToString("F2");
        public string FormattedGrossProfit => GrossProfit.ToString("F2");
        public string FormattedNetProfit => NetProfit.ToString("F2");
        public string FormattedNetProfitPercent => NetProfitPercent.ToString("F2") + "%";
        public string FormattedCurrentPrice => CurrentPrice > 0 ? CurrentPrice.ToString("F2") : "—";
    }
}