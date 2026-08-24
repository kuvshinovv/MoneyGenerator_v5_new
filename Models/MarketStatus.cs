using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MoneyGenerator_v5.Models
{
    public class MarketStatus : INotifyPropertyChanged
    {
        private string _name;
        private string _status;
        private bool _isTrading;
        private DateTime _lastUpdate;
       

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }


        private string _color;
        public string Color
        {
            get
            {
                // Вычисляем цвет на основе IsTrading
                return IsTrading ? "Green" : "Red";
            }
            set
            {
                if (_color != value)
                {
                    _color = value;
                    OnPropertyChanged();
                }
            }
        }


        public bool IsTrading
        {
            get => _isTrading;
            set
            {
                if (_isTrading != value)
                {
                    _isTrading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Color)); // Уведомляем об изменении Color
                }
            }
        }

        public DateTime LastUpdate
        {
            get => _lastUpdate;
            set
            {
                if (_lastUpdate != value)
                {
                    _lastUpdate = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}