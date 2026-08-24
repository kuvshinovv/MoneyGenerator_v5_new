using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MoneyGenerator_v5.Models
{
    public class Operation : INotifyPropertyChanged
    {
        private string _id;
        private string _parentOperationId;
        private string _currency;
        private string _instrumentUid;
        private string _instrumentType;
        private string _figi;
        private string _instrumentUidFrom;
        private string _instrumentUidTo;
        private string _positionUid;
        private string _ticker;
        private string _assetUid;
        private string _assetType;
        private string _operationType;
        private string _state;
        private decimal _quantity;
        private decimal _quantityRest;
        private decimal _price;
        private decimal _payment;
        private decimal _commission;
        private DateTime _date;
        private string _operationTypeName;
        private decimal _yield;
        private decimal _yieldRelative;
        private decimal _averagePositionPrice;
        private string _operationId;
        private decimal _netProfit;
        private bool _isBuyOperation;
        private bool _isSellOperation;
        private bool _isBrokerFee;
        private string _parentTradeId;
        private decimal _feeAmount;
        private decimal _tradeAmount;
        private string _tradePairId;


        public string Id { get => _id; set { _id = value; OnPropertyChanged(); } }
        public string ParentOperationId { get => _parentOperationId; set { _parentOperationId = value; OnPropertyChanged(); } }
        public string Currency { get => _currency; set { _currency = value; OnPropertyChanged(); } }
        public string InstrumentUid { get => _instrumentUid; set { _instrumentUid = value; OnPropertyChanged(); } }
        public string InstrumentType { get => _instrumentType; set { _instrumentType = value; OnPropertyChanged(); } }
        public string Figi { get => _figi; set { _figi = value; OnPropertyChanged(); } }
        public string InstrumentUidFrom { get => _instrumentUidFrom; set { _instrumentUidFrom = value; OnPropertyChanged(); } }
        public string InstrumentUidTo { get => _instrumentUidTo; set { _instrumentUidTo = value; OnPropertyChanged(); } }
        public string PositionUid { get => _positionUid; set { _positionUid = value; OnPropertyChanged(); } }
        public string Ticker { get => _ticker; set { _ticker = value; OnPropertyChanged(); } }
        public string AssetUid { get => _assetUid; set { _assetUid = value; OnPropertyChanged(); } }
        public string AssetType { get => _assetType; set { _assetType = value; OnPropertyChanged(); } }
        public string OperationType { get => _operationType; set { _operationType = value; OnPropertyChanged(); } }
        public string State { get => _state; set { _state = value; OnPropertyChanged(); } }
        public decimal Quantity { get => _quantity; set { _quantity = value; OnPropertyChanged(); } }
        public decimal QuantityRest { get => _quantityRest; set { _quantityRest = value; OnPropertyChanged(); } }
        public decimal Price { get => _price; set { _price = value; OnPropertyChanged(); } }
        public decimal Payment { get => _payment; set { _payment = value; OnPropertyChanged(); } }
        public decimal Commission { get => _commission; set { _commission = value; OnPropertyChanged(); } }
        public DateTime Date { get => _date; set { _date = value; OnPropertyChanged(); } }
        public string OperationTypeName { get => _operationTypeName; set { _operationTypeName = value; OnPropertyChanged(); } }
        public decimal Yield { get => _yield; set { _yield = value; OnPropertyChanged(); } }
        public decimal YieldRelative { get => _yieldRelative; set { _yieldRelative = value; OnPropertyChanged(); } }
        public decimal AveragePositionPrice { get => _averagePositionPrice; set { _averagePositionPrice = value; OnPropertyChanged(); } }
        public string OperationId { get => _operationId; set { _operationId = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Чистая прибыль по сделке (с учетом комиссии)
        /// </summary>
        public decimal NetProfit
        {
            get => _netProfit;
            set { _netProfit = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Является ли операция покупкой
        /// </summary>
        public bool IsBuyOperation
        {
            get => _isBuyOperation;
            set { _isBuyOperation = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Является ли операция продажей
        /// </summary>
        public bool IsSellOperation
        {
            get => _isSellOperation;
            set { _isSellOperation = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Является ли операция комиссией
        /// </summary>
        public bool IsBrokerFee
        {
            get => _isBrokerFee;
            set { _isBrokerFee = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// ID родительской сделки (для связывания buy/sell в пару)
        /// </summary>
        public string ParentTradeId
        {
            get => _parentTradeId;
            set { _parentTradeId = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Сумма комиссии для этой операции/сделки
        /// </summary>
        public decimal FeeAmount
        {
            get => _feeAmount;
            set { _feeAmount = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Сумма операции (без комиссии)
        /// </summary>
        public decimal TradeAmount
        {
            get => _tradeAmount;
            set { _tradeAmount = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// ID пары сделок (для группировки buy+sell)
        /// </summary>
        public string TradePairId
        {
            get => _tradePairId;
            set { _tradePairId = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Отображаемое имя типа операции (локализованное)
        /// </summary>
        public string DisplayOperationType
        {
            get
            {
                return OperationType switch
                {
                    "BUY" => "Покупка",
                    "SELL" => "Продажа",
                    "BROKER_FEE" => "Комиссия",
                    "DIVIDEND" => "Дивиденды",
                    "COUPON" => "Купон",
                    "TAX" => "Налог",
                    _ => OperationTypeName ?? OperationType
                };
            }
        }
    }
}