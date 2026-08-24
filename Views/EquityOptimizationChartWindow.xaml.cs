// Views/EquityOptimizationChartWindow.xaml.cs
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.ViewModels;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Strategies;
using System.Windows;

namespace MoneyGenerator_v5.Views
{
    public partial class EquityOptimizationChartWindow : Window
    {
        private EquityOptimizationChartViewModel _viewModel;

        public EquityOptimizationChartWindow(OptimizationResult result)
        {
            InitializeComponent();

            _viewModel = new EquityOptimizationChartViewModel(result, EquityPlot);
            DataContext = _viewModel;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            _viewModel?.Dispose();
            _viewModel = null;
        }
    }
}