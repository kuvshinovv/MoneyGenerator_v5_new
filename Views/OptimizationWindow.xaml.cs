// Views/OptimizationWindow.xaml.cs
using MoneyGenerator_v5.ViewModels;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static MoneyGenerator_v5.Strategies.MaStrategy;

namespace MoneyGenerator_v5.Views
{
    public partial class OptimizationWindow : Window
    {
        public OptimizationViewModel ViewModel { get; }

        public OptimizationWindow(OptimizationViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;

            // Подписываемся на закрытие
            Closed += (s, e) =>
            {
                ViewModel.StopOptimization();
            };

            // ✅ При загрузке окна подписываемся на событие выбора в DataGrid
            Loaded += (s, e) =>
            {
                var dataGrid = FindName("ResultsDataGrid") as DataGrid;
                if (dataGrid != null)
                {
                    dataGrid.SelectionChanged += (sender, args) =>
                    {
                        // При выборе строки принудительно обновляем команды
                        CommandManager.InvalidateRequerySuggested();

                        // Явно вызываем обновление команды ApplyParametersCommand
                        ViewModel.RefreshCommands();
                    };
                }
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsOptimizationRunning)
            {
                var result = MessageBox.Show("Оптимизация выполняется. Остановить и закрыть окно?",
                    "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    ViewModel.StopOptimization();
                    Close();
                }
            }
            else
            {
                Close();
            }
        }



        /// <summary>
        /// Открытие графика эквити для выбранного результата
        /// </summary>
        private void ViewEquity_Click(object sender, RoutedEventArgs e)
        {

           


            var selectedResult = ViewModel.SelectedResult;
            if (selectedResult == null)
            {
                
                MessageBox.Show("Не выбран результат оптимизации", "Информация",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!selectedResult.HasEquityData)
            {
                MessageBox.Show("Для этого результата нет данных эквити.",
                    "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {

                Debug.WriteLine($"---------------------------------- TimeframeInfo={ViewModel.TimeframeInfo}  InstrumentInfo={ViewModel.InstrumentInfo}   PeriodEnd={ViewModel.PeriodEnd}");
                selectedResult.Ticker = ViewModel.InstrumentInfo;
                selectedResult.TimeFrame = ViewModel.TimeframeInfo;
                selectedResult.EndDate = ViewModel.PeriodEnd;
                

                 var chartWindow = new EquityOptimizationChartWindow(selectedResult);
                chartWindow.Owner = this;
                chartWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка открытия графика: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Копирование параметров в буфер обмена
        /// </summary>
        private void CopyParameters_Click(object sender, RoutedEventArgs e)
        {
            var selectedResult = ViewModel.SelectedResult;
            if (selectedResult == null) return;

            var paramsText = string.Join("\n", selectedResult.Parameters.Select(p => $"{p.Key}: {p.Value:F2}"));
            Clipboard.SetText(paramsText);

            ShowNotification("Параметры скопированы в буфер обмена");
        }


        /// <summary>
        /// Копирование результата в буфер обмена
        /// </summary>
        private void CopyResult_Click(object sender, RoutedEventArgs e)
        {
            var selectedResult = ViewModel.SelectedResult;
            if (selectedResult == null) return;

            var sb = new StringBuilder();
            sb.AppendLine("=== Результат оптимизации ===");
            sb.AppendLine($"Чистая P/L: {selectedResult.FormattedNetProfit}");
            sb.AppendLine($"Валовая P/L: {selectedResult.GrossProfit:F2}");
            sb.AppendLine($"Фактор прибыли: {selectedResult.FormattedProfitFactor}");
            sb.AppendLine($"Коэф. Шарпа: {selectedResult.FormattedSharpeRatio}");
            sb.AppendLine($"Макс. просадка: {selectedResult.FormattedMaxDrawdown}");
            sb.AppendLine($"% успешных: {selectedResult.FormattedWinRate}");
            sb.AppendLine($"Всего сделок: {selectedResult.FormattedTotalTrades}");
            sb.AppendLine($"Выигрышные: {selectedResult.WinningTrades}");
            sb.AppendLine($"Проигрышные: {selectedResult.LosingTrades}");
            sb.AppendLine($"Ср. выигрыш: {selectedResult.AverageWin:F2}");
            sb.AppendLine($"Ср. проигрыш: {selectedResult.AverageLoss:F2}");
            sb.AppendLine($"Ожидание: {selectedResult.FormattedExpectancy}");
            sb.AppendLine($"Фактор восстановления: {selectedResult.FormattedRecoveryFactor}");
            sb.AppendLine($"Годовая доходность: {selectedResult.FormattedAnnualReturn}"); // ✅ ДОБАВЛЕНО
            sb.AppendLine($"Название стратегии: {selectedResult.StrategyType}"); // ✅ ДОБАВЛЕНО
            sb.AppendLine("=== Параметры ===");
            foreach (var p in selectedResult.Parameters)
            {
                sb.AppendLine($"{p.Key}: {p.Value:F2}");
            }

            Clipboard.SetText(sb.ToString());

            ShowNotification("Результат скопирован в буфер обмена");
        }


        private void ShowNotification(string message)
        {
            var tooltip = new System.Windows.Controls.ToolTip
            {
                Content = message,
                IsOpen = true,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
                StaysOpen = false
            };
            tooltip.Opened += (s, e) =>
            {
                var timer = new System.Timers.Timer(1500);
                timer.Elapsed += (t, ev) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    Dispatcher.Invoke(() => tooltip.IsOpen = false);
                };
                timer.Start();
            };
        }





    }
}