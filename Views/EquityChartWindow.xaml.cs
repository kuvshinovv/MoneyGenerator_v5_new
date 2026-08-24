using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.ViewModels;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace MoneyGenerator_v5.Views
{
    /// <summary>
    /// Логика взаимодействия для EquityChartWindow.xaml
    /// </summary>
    public partial class EquityChartWindow : Window, IDisposable
    {
        private EquityChartViewModel _viewModel;
        private bool _isDisposed;


        public EquityChartWindow()
        {
            InitializeComponent();
        }

        public EquityChartWindow(List<EquityRecord> records, string accountName, string provider, string accountId)
        {
            InitializeComponent();

            // Создаем ViewModel и передаем ей существующий WpfPlot
            _viewModel = new EquityChartViewModel(records, accountName, EquityPlot, provider, accountId);
            DataContext = _viewModel;

        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Dispose();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _viewModel?.Dispose();
            _viewModel = null;
        }
    }
}
