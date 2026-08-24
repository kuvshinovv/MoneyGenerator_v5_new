using MoneyGenerator_v5.ViewModels;
using System;
using System.Transactions;
using System.Windows;

namespace MoneyGenerator_v5.Views
{
    public partial class ChartWindow : Window
    {
        private ChartWindowViewModel _viewModel;

        public ChartWindow(StrategyViewModel strategyViewModel, 
            Models.Instrument instrument, string timeframe, 
            Services.TransactionsService transactionService = null, 
            Microsoft.Extensions.Logging.ILogger<ChartWindowViewModel> logger = null, 
            Services.IProvirerService provirerService = null, 
            MainViewModel mainViewModel = null)
        {
            InitializeComponent();

            _viewModel = new ChartWindowViewModel(strategyViewModel, instrument, timeframe, transactionService=null, logger=null, provirerService=null, mainViewModel=null);
            DataContext = _viewModel;

            // Регистрируем ViewModel в стратегии для получения обновлений
            strategyViewModel.RegisterChartViewModel(_viewModel);

            Closed += (s, e) => _viewModel.Dispose();
        }
    }
}