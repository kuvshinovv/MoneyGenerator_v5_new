using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using ScottPlot;
using ScottPlot.WPF;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MoneyGenerator_v5.ViewModels
{
    public partial class EquityChartViewModel : ObservableObject, IDisposable
    {
        private readonly List<EquityRecord> _allRecords;
        private List<EquityRecord> _currentRecords;
        private WpfPlot _plotControl;
        private readonly string _provider;
        private readonly string _accountId;
        private readonly string _accountName;
        private double _lastBalance; // Для хранения последнего значения баланса
        private DispatcherTimer _refreshTimer;
        private bool _isDisposed;





        [ObservableProperty]
        private string _accountNameDisplay;

        [ObservableProperty]
        private string _periodText = "Последние 30 дней";

        public ICommand SetPeriodCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand RefreshCommand { get; }

        public WpfPlot PlotControl
        {
            get
            {
                if (_plotControl == null)
                {
                    _plotControl = new WpfPlot();
                    ConfigurePlot();
                    RenderChart();
                }
                return _plotControl;
            }
        }

        public EquityChartViewModel(List<EquityRecord> records, string accountName, WpfPlot plotControl, string provider, string accountId)
        {
            _allRecords = records.OrderBy(r => r.RecordTime).ToList();
            _accountName = accountName;
            _accountNameDisplay = accountName;
            _plotControl = plotControl;
            _provider = provider;
            _accountId = accountId;

            if (_allRecords.Any())
            {
                _lastBalance = (double)_allRecords.Last().Balance;
            }

            SetPeriodCommand = new RelayCommand<string>(SetPeriod);
            ExportCommand = new RelayCommand(ExportChart);
            RefreshCommand = new RelayCommand(RefreshChart);

            // Подписываемся на обновления данных
            EquityService.OnEquityDataUpdated += OnEquityDataUpdated;

            // Настраиваем таймер для периодического обновления (каждые 30 секунд)
            //_refreshTimer = new DispatcherTimer();
            //_refreshTimer.Interval = TimeSpan.FromSeconds(30);
            //_refreshTimer.Tick += async (s, e) => await RefreshDataAsync();
            //_refreshTimer.Start();


            // Настраиваем график
            ConfigurePlot();

            // ✅ ПОСЛЕ НАСТРОЙКИ ГРАФИКА ОБНОВЛЯЕМ ДАННЫЕ
            UpdateCurrentPeriod();
            RenderChart(); // <-- ЭТО ВАЖНО!


            // По умолчанию показываем последние 30 дней
            SetPeriod("30");
        }

        private void ConfigurePlot()
        {
            _plotControl.Plot.Title($"График эквити - {_accountName}");
            _plotControl.Plot.YLabel("Баланс (₽)");
            _plotControl.Plot.XLabel("Дата");
            _plotControl.Plot.ShowLegend();

            // Настройка сетки
            _plotControl.Plot.Grid.MajorLineColor = ScottPlot.Colors.Gray.WithAlpha(0.2);
            _plotControl.Plot.Grid.MajorLineWidth = 1;

            _plotControl.Refresh();
        }

        private void RenderChart()
        {
            if (!_currentRecords.Any()) return;

            _plotControl.Plot.Clear();

            // Подготовка данных
            var xs = _currentRecords.Select((r, i) => (double)i).ToArray();
            var ys = _currentRecords.Select(r => (double)r.Balance).ToArray();
            var dates = _currentRecords.Select(r => r.RecordTime.ToString("dd.MM HH:mm")).ToArray();

            // Сохраняем последнее значение
            if (ys.Length > 0)
            {
                _lastBalance = ys.Last();
            }

            // График эквити
            var line = _plotControl.Plot.Add.Scatter(xs, ys);
            line.LegendText = "Эквити";
            line.LineStyle = new ScottPlot.LineStyle
            {
                Color = ScottPlot.Colors.Blue,
                Width = 2
            };
            line.MarkerStyle = MarkerStyle.Default;
            line.MarkerSize = 3;
            line.MarkerFillColor = ScottPlot.Colors.Blue;

            // Добавляем начальный баланс как базовую линию
            if (ys.Length > 0)
            {
                var baseLine = _plotControl.Plot.Add.HorizontalLine(ys[0]);
                baseLine.LegendText = $"Начальный баланс: {ys[0]:C2}";
                baseLine.LineStyle = new ScottPlot.LineStyle
                {
                    Color = ScottPlot.Colors.Gray.WithAlpha(0.5),
                    Width = 1,
                    Pattern = LinePattern.Dashed
                };
            }

            // Добавляем пунктирную линию последнего значения
            if (ys.Length > 0)
            {
                var lastValueLine = _plotControl.Plot.Add.HorizontalLine(_lastBalance);
                lastValueLine.LegendText = $"Текущий баланс: {_lastBalance:C2}";
                lastValueLine.LineStyle = new ScottPlot.LineStyle
                {
                    Color = ScottPlot.Colors.Green.WithAlpha(0.7),
                    Width = 2,
                    Pattern = LinePattern.Dashed
                };
            }


            // Настройка оси X с датами
            int tickCount = Math.Min(10, dates.Length);
            int tickStep = Math.Max(1, dates.Length / tickCount);

            var tickPositions = new List<double>();
            var tickLabels = new List<string>();

            for (int i = 0; i < dates.Length; i += tickStep)
            {
                tickPositions.Add(i);
                tickLabels.Add(dates[i]);
            }

            if ((dates.Length - 1) % tickStep != 0 && dates.Length > 0)
            {
                tickPositions.Add(dates.Length - 1);
                tickLabels.Add(dates.Last());
            }

            if (tickPositions.Any())
            {
                var tickGen = new ScottPlot.TickGenerators.NumericManual(tickPositions.ToArray(), tickLabels.ToArray());
                _plotControl.Plot.Axes.Bottom.TickGenerator = tickGen;
            }

            // Автомасштабирование
            //_plotControl.Plot.Axes.AutoScale();
            _plotControl.Plot.Title($"График эквити - {_lastBalance:C2}");
            _plotControl.Refresh();
        }

        private async void OnEquityDataUpdated(string provider, string accountId)
        {
            // Проверяем, что обновление относится к нашему провайдеру и счету
            if (provider == _provider && accountId == _accountId)
            {
                await RefreshDataAsync();
            }
        }

        private async Task RefreshDataAsync()
        {
            try
            {
                // Загружаем новые данные
                var newRecords = await EquityService.GetHistoryAsync(_provider, _accountId, 0);

                if (newRecords.Any())
                {
                    // Проверяем, изменились ли данные
                    var lastRecord = newRecords.Last();
                    var hasNewData = !_allRecords.Any() || _allRecords.Last().RecordTime != lastRecord.RecordTime;

                    if (hasNewData)
                    {
                        // Обновляем все записи
                        _allRecords.Clear();
                        _allRecords.AddRange(newRecords.OrderBy(r => r.RecordTime));

                        // Обновляем текущий период
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            UpdateCurrentPeriod();
                            RenderChart();
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обновления данных эквити: {ex.Message}");
            }
        }

        private void UpdateCurrentPeriod()
        {
            if (PeriodText == "Вся история")
            {
                _currentRecords = _allRecords.ToList();
            }
            else
            {
                // Извлекаем количество дней из PeriodText
                var match = System.Text.RegularExpressions.Regex.Match(PeriodText, @"\d+");
                if (match.Success)
                {
                    int days = int.Parse(match.Value);
                    var cutoff = DateTime.Now.AddDays(-days);
                    _currentRecords = _allRecords.Where(r => r.RecordTime >= cutoff).ToList();
                }
                else
                {
                    _currentRecords = _allRecords.ToList();
                }
            }
        }

        private void RefreshChart()
        {
            if (_currentRecords.Any())
            {
                RenderChart();
            }
            else
            {
                MessageBox.Show("Нет данных для отображения",
                              "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }













        private void SetPeriod(string period)
        {
            int days = int.Parse(period);

            if (days <= 0)
            {
                _currentRecords = _allRecords.ToList();
                PeriodText = "Вся история";
            }
            else
            {
                var cutoff = DateTime.Now.AddDays(-days);
                _currentRecords = _allRecords.Where(r => r.RecordTime >= cutoff).ToList();
                PeriodText = $"Последние {days} дней";
            }

            if (_currentRecords.Any())
            {
                RenderChart();
            }
            else
            {
                MessageBox.Show("Нет данных за выбранный период",
                              "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ExportChart()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PNG Image|*.png|JPEG Image|*.jpg",
                DefaultExt = "png",
                FileName = $"Equity_{_accountName}_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() == true)
            {
                _plotControl.Plot.SavePng(dialog.FileName, 1200, 800);
                MessageBox.Show($"График сохранен: {dialog.FileName}",
                              "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            //_refreshTimer?.Stop();
            //_refreshTimer = null;
            EquityService.OnEquityDataUpdated -= OnEquityDataUpdated;
        }


    }
}