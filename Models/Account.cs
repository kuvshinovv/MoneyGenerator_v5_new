using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public class Account : INotifyPropertyChanged
    {
        private string _id;
        private string _name;
        private string _currency;
        private decimal _balance;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string Currency
        {
            get => _currency;
            set { _currency = value; OnPropertyChanged(); }
        }

        public decimal Balance
        {
            get => _balance;
            set
            {
                if (_balance != value)
                {
                    _balance = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayBalance));
                    OnPropertyChanged(nameof(DisplayName));

                    // Логируем изменения для отладки
                    //Debug.WriteLine($"DEBUG: Account {Name} баланс изменен: {value:F2}");
                }
            }
        }












        public string? Type { get; set; }

        public string DisplayName => $"{Name} - Баланс: {Balance:C}";

        public string DisplayBalance => $"{Balance:F2} {Currency}";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }



    }
}
