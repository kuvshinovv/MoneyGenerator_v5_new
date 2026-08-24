// Models/Deal.cs
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MoneyGenerator_v5.Models
{
    public class Deal : INotifyPropertyChanged
    {
        private long _id;
        private string _ticker;
        private string? _instrumentUid;
        private string? _strategy;
        private DateTime _entryTime;
        private decimal _entryPrice;
        private int _entryQuantity;
        private string? _entryOrderId;
        private string? _direction;
        private DateTime? _exitTime;
        private decimal? _exitPrice;
        private string? _exitOrderId;
        private DealStatus _status;
        private decimal? _closedPnL;
        private decimal? _closedPnLPercent;
        private string? _comment;
        private DateTime _createdAt;
        private DateTime _updatedAt;
        private decimal _currentPnL;
        private decimal _currentPnLPercent;
        private string? _entryReason;
        private string? _exitReason;

        public long Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string Ticker
        {
            get => _ticker;
            set { _ticker = value; OnPropertyChanged(); }
        }

        public string? InstrumentUid
        {
            get => _instrumentUid;
            set { _instrumentUid = value; OnPropertyChanged(); }
        }

        public string? Strategy
        {
            get => _strategy;
            set { _strategy = value; OnPropertyChanged(); }
        }

        public DateTime EntryTime
        {
            get => _entryTime;
            set { _entryTime = value; OnPropertyChanged(); }
        }

        public decimal EntryPrice
        {
            get => _entryPrice;
            set { _entryPrice = value; OnPropertyChanged(); }
        }

        public int EntryQuantity
        {
            get => _entryQuantity;
            set { _entryQuantity = value; OnPropertyChanged(); }
        }

        public string? EntryOrderId
        {
            get => _entryOrderId;
            set { _entryOrderId = value; OnPropertyChanged(); }
        }

        public string? Direction
        {
            get => _direction;
            set { _direction = value; OnPropertyChanged(); }
        }

        public DateTime? ExitTime
        {
            get => _exitTime;
            set { _exitTime = value; OnPropertyChanged(); }
        }

        public decimal? ExitPrice
        {
            get => _exitPrice;
            set { _exitPrice = value; OnPropertyChanged(); }
        }

        public string? ExitOrderId
        {
            get => _exitOrderId;
            set { _exitOrderId = value; OnPropertyChanged(); }
        }

        public DealStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public decimal? ClosedPnL
        {
            get => _closedPnL;
            set { _closedPnL = value; OnPropertyChanged(); }
        }

        public decimal? ClosedPnLPercent
        {
            get => _closedPnLPercent;
            set { _closedPnLPercent = value; OnPropertyChanged(); }
        }

        public string? Comment
        {
            get => _comment;
            set { _comment = value; OnPropertyChanged(); }
        }

        public DateTime CreatedAt
        {
            get => _createdAt;
            set { _createdAt = value; OnPropertyChanged(); }
        }

        public DateTime UpdatedAt
        {
            get => _updatedAt;
            set { _updatedAt = value; OnPropertyChanged(); }
        }

        // Эти поля обновляются автоматически из БД через UpdateOpenDealsPnLAsync
        public decimal CurrentPnL
        {
            get => _currentPnL;
            set { _currentPnL = value; OnPropertyChanged(); }
        }

        public decimal CurrentPnLPercent
        {
            get => _currentPnLPercent;
            set { _currentPnLPercent = value; OnPropertyChanged(); }
        }

        public string? EntryReason
        {
            get => _entryReason;
            set { _entryReason = value; OnPropertyChanged(); }
        }

        public string? ExitReason
        {
            get => _exitReason;
            set { _exitReason = value; OnPropertyChanged(); }
        }

        public decimal CurrentPrice { get; internal set; }
        public decimal LotSize { get; internal set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public enum DealStatus
    {
        Open,
        Closed
    }
}