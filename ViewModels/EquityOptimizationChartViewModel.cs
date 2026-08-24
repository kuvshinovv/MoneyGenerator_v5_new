// ViewModels/EquityOptimizationChartViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MoneyGenerator_v5.Models;
using ScottPlot;
using ScottPlot.WPF;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace MoneyGenerator_v5.ViewModels
{
    public partial class EquityOptimizationChartViewModel : ObservableObject, IDisposable
    {

        private string _tickerName;
        private string _timeFrame;

        private DateTime _currentData;


        private readonly OptimizationResult _result;
        private readonly WpfPlot _plot;
        private bool _disposed = false;

        [ObservableProperty]
        private string _parametersSummary;

        [ObservableProperty]
        private decimal _initialCapital;

        [ObservableProperty]
        private decimal _finalCapital;

        [ObservableProperty]
        private decimal _totalProfit;

        [ObservableProperty]
        private string _totalDuration;

        [ObservableProperty]
        private double _maxDrawdown;

        [ObservableProperty]
        private Brush _profitColor;

        [ObservableProperty]
        private Brush _finalCapitalColor;

        [ObservableProperty]
        private int _selectedDays = 0;

        public IRelayCommand SetFullHistoryCommand { get; }
        public IRelayCommand RefreshCommand { get; }
        public IRelayCommand ExportCommand { get; }

        public EquityOptimizationChartViewModel(OptimizationResult result, WpfPlot plot)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));
            _plot = plot ?? throw new ArgumentNullException(nameof(plot));
            _tickerName = result.Ticker;
            _timeFrame = result.TimeFrame;
            _currentData = result.EndDate;


            SetFullHistoryCommand = new RelayCommand(SetFullHistory);
            RefreshCommand = new RelayCommand(Refresh);
            ExportCommand = new RelayCommand(Export);

            InitializeData();

            // Настраиваем график
            ConfigurePlot();
            PlotEquity();
        }

        private void InitializeData()
        {
            if (_result.EquityHistory == null || _result.EquityHistory.Count == 0)
            {
                ParametersSummary = "Нет данных эквити";
                return;
            }

            // Параметры
            ParametersSummary = string.Join(" | ", _result.Parameters.Select(p => $"{p.Key}={p.Value:F2}"));


            // Капитал
            InitialCapital = _result.EquityHistory.FirstOrDefault();
            FinalCapital = _result.EquityHistory.LastOrDefault();
            TotalProfit = FinalCapital - InitialCapital;

            ProfitColor = TotalProfit >= 0 ? new SolidColorBrush(System.Windows.Media.Colors.Green) : new SolidColorBrush(System.Windows.Media.Colors.Red);
            FinalCapitalColor = FinalCapital >= InitialCapital ? new SolidColorBrush(System.Windows.Media.Colors.Green) : new SolidColorBrush(System.Windows.Media.Colors.Red);

            // Просадка
            decimal peak = InitialCapital;
            double maxDrawdown = 0;
            foreach (var equity in _result.EquityHistory)
            {
                if (equity > peak)
                    peak = equity;
                decimal drawdown = peak > 0 ? (peak - equity) / peak * 100 : 0;
                if ((double)drawdown > maxDrawdown)
                    maxDrawdown = (double)drawdown;
            }
            MaxDrawdown = maxDrawdown;

            // Длительность
            if (_result.EquityDates != null && _result.EquityDates.Count >= 2)
            {
                var duration = _result.EquityDates.Last() - _result.EquityDates.First();
                TotalDuration = FormatDuration(duration);
            }
            else
            {
                TotalDuration = "Нет данных";
            }
        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalDays < 1)
                return $"{duration.Hours}ч {duration.Minutes}м";
            else if (duration.TotalDays < 30)
                return $"{duration.Days}д {duration.Hours}ч";
            else if (duration.TotalDays < 365)
                return $"{duration.Days / 30}м {duration.Days % 30}д";
            else
                return $"{duration.Days / 365}г {duration.Days % 365 / 30}м";
        }

        private void ConfigurePlot()
        {
            _plot.Plot.Title($"График эквити оптимизации");
            _plot.Plot.YLabel("Баланс (₽)");
            _plot.Plot.XLabel("Дата");
            _plot.Plot.ShowLegend();

            // Настройка сетки
            _plot.Plot.Grid.MajorLineColor = ScottPlot.Colors.Gray.WithAlpha(0.2);
            _plot.Plot.Grid.MajorLineWidth = 1;

            _plot.Refresh();
        }

        private void PlotEquity()
        {
            if (_result.EquityHistory == null || _result.EquityHistory.Count < 2)
                return;

            try
            {
                _plot.Plot.Clear();

                // Подготовка данных
                var data = _result.EquityHistory.ToArray();
                var dates = _result.EquityDates?.ToArray();

                // Используем индексы как X значения
                var xs = Enumerable.Range(0, data.Length).Select(i => (double)i).ToArray();
                var ys = data.Select(d => (double)d).ToArray();

                // Сохраняем даты для подписей
                var dateStrings = dates?.Select(d => d.ToString("dd.MM HH:mm")).ToArray();

                // Основная линия эквити
                var scatter = _plot.Plot.Add.Scatter(xs, ys);
                scatter.Label = "Эквити";
                scatter.Color = ScottPlot.Color.FromHex("#3498DB");
                scatter.LineWidth = 2;
                scatter.MarkerSize = 0;

                // Добавляем начальный уровень
                var initialLine = _plot.Plot.Add.HorizontalLine((double)InitialCapital);
                initialLine.LabelText = $"Нач. капитал: {InitialCapital:F2}";
                initialLine.Color = ScottPlot.Color.FromHex("#E74C3C");
                initialLine.LineStyle = new ScottPlot.LineStyle
                {
                    Color = ScottPlot.Colors.Gray.WithAlpha(0.5),
                    Width = 1,
                    Pattern = LinePattern.Dashed
                };

                // Добавляем текущий уровень (финальный капитал)
                var finalLine = _plot.Plot.Add.HorizontalLine((double)FinalCapital);
                finalLine.LabelText = $"Текущий капитал: {FinalCapital:F2}";
                finalLine.Color = ScottPlot.Color.FromHex("#2ECC71");
                finalLine.LineStyle = new ScottPlot.LineStyle
                {
                    Color = ScottPlot.Colors.Green.WithAlpha(0.7),
                    Width = 2,
                    Pattern = LinePattern.Dashed
                };

                // Настройка оси X с датами
                if (dateStrings != null && dateStrings.Length > 0)
                {
                    int tickCount = Math.Min(10, dateStrings.Length);
                    int tickStep = Math.Max(1, dateStrings.Length / tickCount);

                    var tickPositions = new List<double>();
                    var tickLabels = new List<string>();

                    for (int i = 0; i < dateStrings.Length; i += tickStep)
                    {
                        tickPositions.Add(i);
                        tickLabels.Add(dateStrings[i]);
                    }

                    if ((dateStrings.Length - 1) % tickStep != 0 && dateStrings.Length > 0)
                    {
                        tickPositions.Add(dateStrings.Length - 1);
                        tickLabels.Add(dateStrings.Last());
                    }

                    if (tickPositions.Any())
                    {
                        var tickGen = new ScottPlot.TickGenerators.NumericManual(tickPositions.ToArray(), tickLabels.ToArray());
                        _plot.Plot.Axes.Bottom.TickGenerator = tickGen;
                    }
                }

                // Настройки графика
                _plot.Plot.Title($"График эквити ({_tickerName}, {_timeFrame}) - P&L: {TotalProfit:F2} ₽     {_currentData}      ");

                // Автомасштабирование
                _plot.Plot.Axes.AutoScale();

                // Добавляем легенду
                _plot.Plot.Add.Legend();

                // Обновляем график
                _plot.Refresh();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка построения графика: {ex.Message}");
            }
        }

        private void SetFullHistory()
        {
            PlotEquity();
        }

        private void Refresh()
        {
            PlotEquity();
        }

        /// <summary>
        /// Экспортирует ВСЕ окно как изображение
        /// </summary>
        private void Export()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "PNG Image|*.png|JPEG Image|*.jpg",
                    DefaultExt = "png",
                    FileName = $"Equity_Optimization_{DateTime.Now:yyyyMMdd_HHmmss}_{_tickerName}_{_timeFrame}"
                };

                if (dialog.ShowDialog() == true)
                {
                    // ✅ Получаем окно через визуальное дерево
                    var window = Application.Current.Windows
                        .OfType<Window>()
                        .FirstOrDefault(w => w.DataContext == this);

                    if (window != null)
                    {
                        // ✅ Сохраняем ВСЕ окно целиком
                        SaveWindowAsImage(window, dialog.FileName);
                        MessageBox.Show($"График сохранен в: {dialog.FileName}", "Экспорт",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        // ✅ Если окно не найдено - сохраняем только график как запасной вариант
                        _plot.Plot.SavePng(dialog.FileName, 1200, 800);
                        MessageBox.Show($"График сохранен в: {dialog.FileName}", "Экспорт",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка экспорта: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Сохраняет все окно как изображение
        /// </summary>
        private void SaveWindowAsImage(Window window, string fileName)
        {
            try
            {
                // ✅ Получаем размеры окна
                double width = window.ActualWidth;
                double height = window.ActualHeight;

                // ✅ Если окно еще не отрендерено - используем размеры по умолчанию
                if (width <= 0 || height <= 0)
                {
                    width = 900;
                    height = 550;
                }

                // ✅ Создаем RenderTargetBitmap для захвата всего окна
                var renderBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)width,
                    (int)height,
                    96d,
                    96d,
                    System.Windows.Media.PixelFormats.Pbgra32);

                // ✅ Рендерим все окно
                renderBitmap.Render(window);

                // ✅ Создаем PNG энкодер
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(renderBitmap));

                // ✅ Сохраняем в файл
                using (var fileStream = new System.IO.FileStream(fileName, System.IO.FileMode.Create))
                {
                    encoder.Save(fileStream);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения окна: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _plot?.Plot?.Clear();
        }
    }
}