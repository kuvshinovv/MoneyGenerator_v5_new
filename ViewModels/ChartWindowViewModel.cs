using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Strategies;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tinkoff.InvestApi.V1;
using static MoneyGenerator_v5.Strategies.MaStrategy;

namespace MoneyGenerator_v5.ViewModels
{
    public partial class ChartWindowViewModel : ObservableObject, IDisposable
    {
        private readonly StrategyViewModel _strategyViewModel;
        private readonly Models.Instrument _instrument;
        private readonly string _timeframe;
        private WpfPlot _plotControl;
        private List<ScottPlot.OHLC> _ohlcData = new();
        private readonly object _lockObject = new object();

        private readonly ILogger _logger;
        private readonly IProvirerService _provider;
        private readonly TransactionsService _transactionsService;


        // Для отслеживания изменений в БД
        private DateTime _lastCandleTime = DateTime.MinValue;
        private bool _isUpdating = false;

        // Сохранение пользовательского масштаба
        private AxisLimits? _userAxisLimits = null;
        private bool _isUserZoomed = false;

        // Коллекции для хранения сделок
        public ObservableCollection<DealMarker> Deals { get; } = new();

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusText = "Готов";

        [ObservableProperty]
        private double _lastPrice;

        [ObservableProperty]
        private string _lastPriceText = "0.00";

        // Для отображения дат на оси X (индексы вместо реального времени)
        private double[] _xPositions; // Позиции свечей (0, 1, 2, 3...)
        private DateTime[] _dateTimes; // Соответствующие даты для подписей


        private ObservableCollection<IndicatorConfig> _indicators;
        public ObservableCollection<IndicatorConfig> Indicators
        {
            get
            {
                Debug.WriteLine("Indicators: ленивая инициализация0");
                if (_indicators == null)
                {
                    Debug.WriteLine("Indicators: ленивая инициализация1");
                    _indicators = new ObservableCollection<IndicatorConfig>();
                    InitializeIndicators(); // Заполняем коллекцию
                }
                return _indicators;
            }
            set => SetProperty(ref _indicators, value);
        }




   


        [ObservableProperty]
        private bool _showIndicatorPanel = false;

        [ObservableProperty]
        private double _indicatorPanelWidth = 250;




        // Команды
        public ICommand RefreshCommand { get; }
        public ICommand ClearDrawingsCommand { get; }
        public ICommand ExportImageCommand { get; }
        public ICommand ResetZoomCommand { get; }

        // Команды для управления индикаторами
        public ICommand ToggleAllIndicatorsCommand { get; }
        public ICommand ResetIndicatorsCommand { get; }
        public ICommand ApplyIndicatorChangesCommand { get; }


        public ChartWindowViewModel(StrategyViewModel strategyViewModel, Models.Instrument instrument, string timeframe, TransactionsService transactionsService, ILogger<ChartWindowViewModel> logger,
            IProvirerService provider, MainViewModel mainViewModel)
        {
            _strategyViewModel = strategyViewModel;
            _instrument = instrument;
            _timeframe = timeframe;
            _logger = logger;
            _provider = provider;




            // Инициализация команд
            RefreshCommand = new AsyncRelayCommand(LoadHistoricalDataAsync);
            ClearDrawingsCommand = new RelayCommand(ClearDrawings);
            ExportImageCommand = new RelayCommand(ExportImage);
            ResetZoomCommand = new RelayCommand(ResetZoom);


            try
            {
                // ✅ СОЗДАЕМ TransactionsService
                var transactionsLogger = logger as ILogger<TransactionsService> ??
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionsService>.Instance;
                _transactionsService = new TransactionsService(
                    provider,
                    mainViewModel,
                    strategyViewModel,
                    _strategyViewModel.Instrument,
                    mainViewModel.SelectedAccount,
                    transactionsLogger);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG:  Конструктор ChartWindowViewModel  Ошибка открытия графика: {ex.Message} \n {ex.StackTrace}  \n {ex}");
            }
           




            // Команды для индикаторов
            Debug.WriteLine("=== Инициализация команд индикаторов ===");
            Debug.WriteLine($"ToggleAllIndicatorsCommand до инициализации: {ToggleAllIndicatorsCommand}");

            ToggleAllIndicatorsCommand = new RelayCommand<bool>(ToggleAllIndicators);
            ResetIndicatorsCommand = new RelayCommand(ResetIndicatorsToDefault);
            ApplyIndicatorChangesCommand = new RelayCommand(ApplyIndicatorChanges);

            Debug.WriteLine($"ToggleAllIndicatorsCommand после инициализации: {ToggleAllIndicatorsCommand}");
            Debug.WriteLine($"ResetIndicatorsCommand: {ResetIndicatorsCommand}");
            Debug.WriteLine($"ApplyIndicatorChangesCommand: {ApplyIndicatorChangesCommand}");

            // Инициализируем список индикаторов
            //InitializeIndicators();

            // Загружаем исторические данные
            _ = LoadHistoricalDataAsync();

            // Подписываемся на обновления цены из стратегии
            if (_strategyViewModel is StrategyViewModel svm)
            {
                Debug.WriteLine($"Подписываемся на обновления цены из стратегии: ----------------------");
                // Используем событие обновления цены, если оно есть в StrategyViewModel
                // В зависимости от вашей реализации, может потребоваться другой подход
                PropertyChangedEventManager.AddHandler(svm, OnStrategyPropertyChanged, "");
            }

            Debug.WriteLine($"Завершили конструктор: ----------------------");

        }

        private void InitializeIndicators()
        {



            Debug.WriteLine($"InitializeIndicators: ----------------------");











            Debug.WriteLine("InitializeIndicators");
            Indicators.Clear();

            // Трендовые индикаторы
            Indicators.Add(new IndicatorConfig
            {
                Name = "SMA",
                Parameters = "20",
                Description = "Simple Moving Average",
                IsEnabled = true
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "EMA",
                Parameters = "20",
                Description = "Exponential Moving Average",
                IsEnabled = true
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "WMA",
                Parameters = "20",
                Description = "Weighted Moving Average",
                IsEnabled = false
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "HMA",
                Parameters = "20",
                Description = "Hull Moving Average",
                IsEnabled = false
            });

            // Осцилляторы
            Indicators.Add(new IndicatorConfig
            {
                Name = "RSI",
                Parameters = "14",
                Description = "Relative Strength Index",
                IsEnabled = true
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "CCI",
                Parameters = "20",
                Description = "Commodity Channel Index",
                IsEnabled = false
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "STOCH",
                Parameters = "14,3,3",
                Description = "Stochastic Oscillator",
                IsEnabled = false
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "WILLIAMS",
                Parameters = "14",
                Description = "Williams %R",
                IsEnabled = false
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "MFI",
                Parameters = "14",
                Description = "Money Flow Index",
                IsEnabled = false
            });

            // Индикаторы волатильности
            Indicators.Add(new IndicatorConfig
            {
                Name = "ATR",
                Parameters = "14",
                Description = "Average True Range",
                IsEnabled = false
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "BBANDS",
                Parameters = "20,2",
                Description = "Bollinger Bands",
                IsEnabled = true
            });

            // Объемные индикаторы
            Indicators.Add(new IndicatorConfig
            {
                Name = "OBV",
                Parameters = "",
                Description = "On-Balance Volume",
                IsEnabled = false
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "ADL",
                Parameters = "",
                Description = "Accumulation/Distribution Line",
                IsEnabled = false
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "CMF",
                Parameters = "20",
                Description = "Chaikin Money Flow",
                IsEnabled = false
            });
            Indicators.Add(new IndicatorConfig
            {
                Name = "VWAP",
                Parameters = "",
                Description = "Volume Weighted Average Price",
                IsEnabled = false
            });
        }

        private void ToggleAllIndicators(bool enable)
        {
            try
            {
                Debug.WriteLine($"=== ToggleAllIndicators вызван с параметром: {enable} ===");
                Debug.WriteLine($"Indicators.Count: {Indicators?.Count ?? 0}");

                if (Indicators == null)
                {
                    Debug.WriteLine("Indicators is null!");
                    return;
                }

                foreach (var indicator in Indicators)
                {
                    if (indicator != null)
                    {
                        indicator.IsEnabled = enable;
                        Debug.WriteLine($"  {indicator.Name} -> {enable}");
                    }
                }

                Debug.WriteLine("=== ToggleAllIndicators завершен ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ToggleAllIndicators ОШИБКА: {ex.Message}");
            }
        }

        private void ResetIndicatorsToDefault()
        {
            try
            {
                Debug.WriteLine("=== ResetIndicatorsToDefault вызван ===");
                InitializeIndicators();
                Debug.WriteLine("=== ResetIndicatorsToDefault завершен ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResetIndicatorsToDefault ОШИБКА: {ex.Message}");
            }
        }

        private void ApplyIndicatorChanges()
        {
            try
            {
                Debug.WriteLine("=== ApplyIndicatorChanges вызван ===");
                RenderChart();
                Debug.WriteLine("=== ApplyIndicatorChanges завершен ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyIndicatorChanges ОШИБКА: {ex.Message}");
            }
        }






























        private void OnStrategyPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StrategyViewModel.CurrentPrice))
            {
                // Обновляем последнюю свечу при изменении цены
                _ = UpdateLastCandleAsync();
            }
        }

        public WpfPlot PlotControl
        {
            get
            {
                if (_plotControl == null)
                {
                    _plotControl = new WpfPlot();
                    ConfigurePlot();

                    // Подписываемся на события масштабирования
                    _plotControl.MouseUp += OnPlotMouseUpForZoom;
                    _plotControl.MouseWheel += OnPlotMouseWheel;
                }
                return _plotControl;
            }
        }

        private void ConfigurePlot()
        {
            // Настройка стиля графика
            _plotControl.Plot.Title($"{_instrument.Ticker} - {_timeframe}");
            _plotControl.Plot.YLabel("Цена");

            // Убираем DateTimeTicksBottom - будем использовать числовую ось с кастомными метками
            // _plotControl.Plot.Axes.DateTimeTicksBottom(); // <-- УДАЛИТЕ ЭТУ СТРОКУ

            // Добавляем правую ось Y для отображения цены
            var rightAxis = _plotControl.Plot.Axes.AddRightAxis();
            rightAxis.Label.Text = "Цена";
            rightAxis.Label.ForeColor = ScottPlot.Colors.Black;
            rightAxis.TickLabelStyle.ForeColor = ScottPlot.Colors.Black;

            // Включаем легенду
            _plotControl.Plot.ShowLegend();

            // Настройка интерактивности
            _plotControl.UserInputProcessor.IsEnabled = true;

            // Включаем рисование линий
            EnableDrawingTools();

            _plotControl.Refresh();
        }

        private void OnPlotMouseUpForZoom(object sender, MouseButtonEventArgs e)
        {
            // Сохраняем пользовательский масштаб после взаимодействия
            _userAxisLimits = _plotControl.Plot.Axes.GetLimits();
            _isUserZoomed = true;
        }

        private void OnPlotMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Сохраняем пользовательский масштаб после изменения колесом
            _userAxisLimits = _plotControl.Plot.Axes.GetLimits();
            _isUserZoomed = true;
        }

        private void ResetZoom()
        {
            _isUserZoomed = false;
            _userAxisLimits = null;
            RenderChart();
        }

        private void EnableDrawingTools()
        {
            _plotControl.MouseDown += OnPlotMouseDown;
            _plotControl.MouseMove += OnPlotMouseMove;
            _plotControl.MouseUp += OnPlotMouseUp;
        }

        // Состояние для рисования
        private bool _isDrawing;
        private ScottPlot.Pixel _drawStartPixel;
        private ScottPlot.Coordinates? _drawStartCoordinate;
        private ScottPlot.Plottables.Scatter? _currentDrawingLine;
        private double[] _drawXs = new double[2];
        private double[] _drawYs = new double[2];

        private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed &&
                (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)))
            {
                var mousePixel = GetMousePixel(e);
                _drawStartPixel = mousePixel;
                _drawStartCoordinate = _plotControl.Plot.GetCoordinates(mousePixel);
                _isDrawing = true;

                _drawXs[0] = _drawStartCoordinate.Value.X;
                _drawYs[0] = _drawStartCoordinate.Value.Y;
                _drawXs[1] = _drawStartCoordinate.Value.X;
                _drawYs[1] = _drawStartCoordinate.Value.Y;
            }
        }

        private void OnPlotMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDrawing && _drawStartCoordinate.HasValue)
            {
                var currentPixel = GetMousePixel(e);
                var currentCoordinate = _plotControl.Plot.GetCoordinates(currentPixel);
                var snappedCoordinate = SnapToNearestHighLow(currentCoordinate);

                if (_currentDrawingLine == null)
                {
                    double[] xs = { _drawStartCoordinate.Value.X, snappedCoordinate.X };
                    double[] ys = { _drawStartCoordinate.Value.Y, snappedCoordinate.Y };
                    _currentDrawingLine = _plotControl.Plot.Add.Scatter(xs, ys);
                    _currentDrawingLine.LineStyle = new LineStyle
                    {
                        Color = ScottPlot.Colors.Blue,
                        Width = 2,
                        Pattern = LinePattern.Dashed
                    };
                    _currentDrawingLine.LegendText = "Аналитическая линия";
                    _currentDrawingLine.MarkerStyle = MarkerStyle.None;

                    _drawXs[1] = snappedCoordinate.X;
                    _drawYs[1] = snappedCoordinate.Y;
                }
                else
                {
                    _plotControl.Plot.Remove(_currentDrawingLine);
                    double[] xs = { _drawStartCoordinate.Value.X, snappedCoordinate.X };
                    double[] ys = { _drawStartCoordinate.Value.Y, snappedCoordinate.Y };
                    _currentDrawingLine = _plotControl.Plot.Add.Scatter(xs, ys);
                    _currentDrawingLine.LineStyle = new LineStyle
                    {
                        Color = ScottPlot.Colors.Blue,
                        Width = 2,
                        Pattern = LinePattern.Dashed
                    };
                    _currentDrawingLine.LegendText = "Аналитическая линия";
                    _currentDrawingLine.MarkerStyle = MarkerStyle.None;

                    _drawXs[1] = snappedCoordinate.X;
                    _drawYs[1] = snappedCoordinate.Y;
                }

                _plotControl.Refresh();
            }
        }

        private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawing)
            {
                _isDrawing = false;
                _currentDrawingLine = null;
            }
        }

        private ScottPlot.Pixel GetMousePixel(MouseEventArgs e)
        {
            var position = e.GetPosition(_plotControl);
            return new ScottPlot.Pixel((float)position.X, (float)position.Y);
        }

        private ScottPlot.Coordinates SnapToNearestHighLow(ScottPlot.Coordinates coord)
        {
            lock (_lockObject)
            {
                if (!_ohlcData.Any())
                    return coord;

                // Находим ближайший индекс свечи
                int index = (int)Math.Round(coord.X);
                if (index < 0) index = 0;
                if (index >= _ohlcData.Count) index = _ohlcData.Count - 1;

                var ohlc = _ohlcData[index];

                // Привязываемся к High или Low в зависимости от того, что ближе
                double highDist = Math.Abs(ohlc.High - coord.Y);
                double lowDist = Math.Abs(ohlc.Low - coord.Y);

                if (highDist < lowDist)
                    return new Coordinates(index, ohlc.High);
                else
                    return new Coordinates(index, ohlc.Low);
            }
        }

        private void ClearDrawings()
        {
            var plottables = _plotControl.Plot.GetPlottables().ToList();
            var plottablesToRemove = plottables
                .Where(p => p is Scatter scatter && scatter.LegendText == "Аналитическая линия")
                .ToList();

            foreach (var plottable in plottablesToRemove)
            {
                _plotControl.Plot.Remove(plottable);
            }

            _plotControl.Refresh();
        }

        private void ExportImage()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PNG Image|*.png|JPEG Image|*.jpg|BMP Image|*.bmp|SVG Image|*.svg",
                DefaultExt = "png",
                FileName = $"{_instrument.Ticker}_{_timeframe}_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() == true)
            {
                _plotControl.Plot.SavePng(dialog.FileName, 1920, 1080);
                StatusText = $"График сохранен: {System.IO.Path.GetFileName(dialog.FileName)}";
            }
        }

        private async Task LoadHistoricalDataAsync()
        {
            Debug.WriteLine($"LoadHistoricalDataAsync: ----------------------");


            try
            {
                IsLoading = true;
                StatusText = "Загрузка всех исторических данных...";

                // Загружаем ВСЕ свечи из БД
                var candles = await _strategyViewModel.GetHistoricalCandlesFromDbAsync(10000000);

                if (!candles.Any())
                {
                    StatusText = "Нет данных для отображения";
                    return;
                }

                lock (_lockObject)
                {
                    // Сортируем свечи по времени
                    var sortedCandles = candles.OrderBy(c => c.Time).ToList();

                    // Создаем OHLC данные с правильным TimeSpan для таймфрейма
                    _ohlcData = sortedCandles.Select(c => new OHLC
                    {
                        Open = (double)c.Open,
                        High = (double)c.High,
                        Low = (double)c.Low,
                        Close = (double)c.Close,
                        DateTime = c.Time,
                        TimeSpan = GetTimeSpanFromTimeframe(_timeframe)
                    }).ToList();

                    if (_ohlcData.Any())
                    {
                        _lastCandleTime = _ohlcData.Last().DateTime;
                        LastPrice = (double)_ohlcData.Last().Close;
                        LastPriceText = LastPrice.ToString("F2");
                    }
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    RenderChart();
                    LoadDealsFromDatabase();
                });

                StatusText = $"Загружено {candles.Count} свечей";
            }
            catch (Exception ex)
            {
                StatusText = $"Ошибка загрузки: {ex.Message}";
                Debug.WriteLine($"Ошибка загрузки: {ex}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private TimeSpan GetTimeSpanFromTimeframe(string timeframe)
        {
            return timeframe?.ToLower() switch
            {
                "1min" => TimeSpan.FromMinutes(1),
                "5min" => TimeSpan.FromMinutes(5),
                "15min" => TimeSpan.FromMinutes(15),
                "30min" => TimeSpan.FromMinutes(30),
                "1hour" => TimeSpan.FromHours(1),
                "2hour" => TimeSpan.FromHours(2),
                "4hour" => TimeSpan.FromHours(4),
                "1day" => TimeSpan.FromDays(1),
                _ => TimeSpan.FromMinutes(1)
            };
        }

        private async Task UpdateLastCandleAsync()
        {
            if (_isUpdating) return;

            try
            {
                _isUpdating = true;

                var lastCandle = await _strategyViewModel.GetLastCandleAsync(_instrument.Ticker, _timeframe);
                if (lastCandle == null) return;

                lock (_lockObject)
                {
                    if (!_ohlcData.Any()) return;

                    var lastOhlc = _ohlcData.LastOrDefault();

                    // Если это новая свеча (по времени)
                    if (lastOhlc.DateTime != lastCandle.Time)
                    {
                        // Добавляем новую свечу
                        _ohlcData.Add(new OHLC
                        {
                            Open = (double)lastCandle.Open,
                            High = (double)lastCandle.High,
                            Low = (double)lastCandle.Low,
                            Close = (double)lastCandle.Close,
                            DateTime = lastCandle.Time,
                            TimeSpan = GetTimeSpanFromTimeframe(_timeframe)
                        });

                        _lastCandleTime = lastCandle.Time;
                    }
                    else
                    {
                        // Обновляем существующую свечу
                        var index = _ohlcData.Count - 1;
                        _ohlcData[index] = new OHLC
                        {
                            Open = (double)lastCandle.Open,
                            High = (double)lastCandle.High,
                            Low = (double)lastCandle.Low,
                            Close = (double)lastCandle.Close,
                            DateTime = lastCandle.Time,
                            TimeSpan = GetTimeSpanFromTimeframe(_timeframe)
                        };
                    }

                    LastPrice = (double)lastCandle.Close;
                    LastPriceText = LastPrice.ToString("F2");
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    RenderChart();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обновления свечи: {ex.Message}");
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void RenderChart()
        {
            lock (_lockObject)
            {
                if (!_ohlcData.Any()) return;

                // Сохраняем нарисованные линии
                var drawnLines = _plotControl.Plot.GetPlottables()
                    .Where(p => p is Scatter scatter && scatter.LegendText == "Аналитическая линия")
                    .ToList();

                // Очищаем все
                _plotControl.Plot.Clear();

                // Восстанавливаем нарисованные линии
                foreach (var line in drawnLines)
                {
                    _plotControl.Plot.Add.Plottable(line);
                }

                // СОЗДАЕМ СВЕЧНОЙ ГРАФИК С SEQUENTIAL РЕЖИМОМ
                var financePlot = _plotControl.Plot.Add.Candlestick(_ohlcData);

                // ВКЛЮЧАЕМ SEQUENTIAL РЕЖИМ - это убирает gaps между свечами!
                financePlot.Sequential = true;

                // Настраиваем использование правой оси для цен
                financePlot.Axes.YAxis = _plotControl.Plot.Axes.Right;
                financePlot.Axes.YAxis.Label.Text = "Цена";

                // Настраиваем цвета
                financePlot.RisingColor = new Color(System.Drawing.Color.Green);
                financePlot.FallingColor = new Color(System.Drawing.Color.Red);

                // Настраиваем метки оси X для отображения дат
                if (_ohlcData.Any())
                {
                    // Определяем сколько меток показывать (например, 10)
                    int tickCount = Math.Min(10, _ohlcData.Count);

                    // Вычисляем шаг между метками
                    int tickStep = _ohlcData.Count / tickCount;
                    if (tickStep < 1) tickStep = 1;

                    var tickPositions = new List<double>();
                    var tickLabels = new List<string>();

                    for (int i = 0; i < _ohlcData.Count; i += tickStep)
                    {
                        tickPositions.Add(i);
                        tickLabels.Add(_ohlcData[i].DateTime.ToString("dd.MM HH:mm"));
                    }

                    // Добавляем последнюю метку если не попала в шаг
                    if ((_ohlcData.Count - 1) % tickStep != 0)
                    {
                        tickPositions.Add(_ohlcData.Count - 1);
                        tickLabels.Add(_ohlcData.Last().DateTime.ToString("dd.MM HH:mm"));
                    }

                    // Создаем ручной генератор тиков
                    var tickGen = new ScottPlot.TickGenerators.NumericManual(tickPositions.ToArray(), tickLabels.ToArray());
                    _plotControl.Plot.Axes.Bottom.TickGenerator = tickGen;
                }

                // Добавляем индикаторы
                AddIndicators();

                // Добавляем сделки
                AddDealsToChart();

                // Добавляем маркер последней цены
                AddPriceMarker();

                // Восстанавливаем пользовательский масштаб или авто-масштабируем
                if (_isUserZoomed && _userAxisLimits.HasValue)
                {
                    _plotControl.Plot.Axes.SetLimits(_userAxisLimits.Value);
                }
                else
                {
                    _plotControl.Plot.Axes.AutoScale();
                }

                _plotControl.Refresh();
            }
        }

        private void AddPriceMarker()
        {
            if (!_ohlcData.Any() || _xPositions == null) return;

            var lastOhlc = _ohlcData.Last();
            var lastIndex = _xPositions.Last();

            // Горизонтальная линия на уровне последней цены
            var hLine = _plotControl.Plot.Add.HorizontalLine(lastOhlc.Close);
            hLine.LineStyle = new LineStyle
            {
                Color = ScottPlot.Colors.Blue.WithAlpha(0.5),
                Width = 1,
                Pattern = LinePattern.Dotted
            };
            hLine.LegendText = $"Посл. цена: {lastOhlc.Close:F2}";

            // Текст с ценой справа
            var priceText = _plotControl.Plot.Add.Text(
                $" {lastOhlc.Close:F2}",
                lastIndex + 0.5, // Немного правее последней свечи
                lastOhlc.Close);
            priceText.LabelStyle = new LabelStyle
            {
                FontSize = 12,
                Bold = true,
                ForeColor = ScottPlot.Colors.Blue,
                BorderColor = ScottPlot.Colors.White,
                BorderWidth = 2,
                Padding = 2
            };
        }

        private void AddIndicators()
        {
            try
            {
                // Проверяем, что есть данные для расчета
                if (_ohlcData == null || !_ohlcData.Any())
                {
                    Debug.WriteLine("AddIndicators: нет данных для расчета");
                    return;
                }

                var quotes = _ohlcData.Select(o => new Quote
                {
                    Date = o.DateTime,
                    Open = (decimal)o.Open,
                    High = (decimal)o.High,
                    Low = (decimal)o.Low,
                    Close = (decimal)o.Close,
                    Volume = 0
                }).ToList();

                Debug.WriteLine($"AddIndicators: начинаем расчет для {Indicators.Count(i => i.IsEnabled)} индикаторов");

                foreach (var indicator in Indicators.Where(i => i.IsEnabled))
                {
                    try
                    {
                        Debug.WriteLine($"AddIndicators: расчет {indicator.Name} с параметрами {indicator.Parameters}");

                        switch (indicator.Name)
                        {
                            case "SMA":
                                AddSMA(quotes, indicator);
                                break;
                            case "EMA":
                                AddEMA(quotes, indicator);
                                break;
                            case "WMA":
                                AddWMA(quotes, indicator);
                                break;
                            case "HMA":
                                AddHMA(quotes, indicator);
                                break;
                            case "RSI":
                                AddRSI(quotes, indicator);
                                break;
                            case "CCI":
                                AddCCI(quotes, indicator);
                                break;
                            case "STOCH":
                                AddSTOCH(quotes, indicator);
                                break;
                            case "WILLIAMS":
                                AddWILLIAMS(quotes, indicator);
                                break;
                            case "MFI":
                                AddMFI(quotes, indicator);
                                break;
                            case "ATR":
                                AddATR(quotes, indicator);
                                break;
                            case "BBANDS":
                                AddBBANDS(quotes, indicator);
                                break;
                            case "OBV":
                                AddOBV(quotes, indicator);
                                break;
                            case "ADL":
                                AddADL(quotes, indicator);
                                break;
                            case "CMF":
                                AddCMF(quotes, indicator);
                                break;
                            case "VWAP":
                                AddVWAP(quotes, indicator);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка расчета индикатора {indicator.Name}: {ex.Message}");
                    }
                }

                Debug.WriteLine("AddIndicators: расчет завершен");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AddIndicators: общая ошибка: {ex.Message}");
            }
        }

        #region Методы добавления индикаторов
        private void AddSMA(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetSma(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Sma ?? double.NaN).ToArray();
                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, values);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 2 };
                    plot.MarkerStyle = MarkerStyle.None;

                    Debug.WriteLine($"  {config.Name} {period}: добавлено {values.Length - period + 1} значений");
                }
            }
        }

        private void AddEMA(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetEma(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Ema ?? double.NaN).ToArray();
                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, values);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 2 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddWMA(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetWma(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Wma ?? double.NaN).ToArray();
                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, values);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 2 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddHMA(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetHma(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Hma ?? double.NaN).ToArray();
                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, values);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 2 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddRSI(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetRsi(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Rsi ?? 50.0).ToArray();

                    // Масштабируем RSI для отображения на основном графике
                    var priceMin = _ohlcData.Min(x => x.Low);
                    var priceMax = _ohlcData.Max(x => x.High);
                    var priceRange = priceMax - priceMin;

                    var scaledValues = values.Select(v => priceMin + (v - 30) / 70 * priceRange).ToArray();

                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, scaledValues);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1, Pattern = LinePattern.Dashed };
                    plot.MarkerStyle = MarkerStyle.None;

                    // Добавляем уровни перекупленности/перепроданности
                    var overbought = priceMin + (70 - 30) / 70 * priceRange;
                    var oversold = priceMin + (30 - 30) / 70 * priceRange;

                    var hLineOverbought = _plotControl.Plot.Add.HorizontalLine(overbought);
                    hLineOverbought.LineStyle = new LineStyle { Color = ScottPlot.Colors.Red.WithAlpha(0.3), Width = 1, Pattern = LinePattern.Dashed };
                    hLineOverbought.LegendText = "RSI 70";

                    var hLineOversold = _plotControl.Plot.Add.HorizontalLine(oversold);
                    hLineOversold.LineStyle = new LineStyle { Color = ScottPlot.Colors.Green.WithAlpha(0.3), Width = 1, Pattern = LinePattern.Dashed };
                    hLineOversold.LegendText = "RSI 30";
                }
            }
        }

        private void AddCCI(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetCci(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Cci ?? 0).ToArray();

                    // Масштабируем CCI
                    var priceMin = _ohlcData.Min(x => x.Low);
                    var priceMax = _ohlcData.Max(x => x.High);
                    var priceRange = priceMax - priceMin;

                    var scaledValues = values.Select(v => priceMin + (v + 200) / 400 * priceRange).ToArray();

                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, scaledValues);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddSTOCH(List<Quote> quotes, IndicatorConfig config)
        {
            var parts = config.Parameters.Split(',');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out int period) &&
                int.TryParse(parts[1], out int signal) &&
                int.TryParse(parts[2], out int smooth))
            {
                var results = quotes.GetStoch(period, signal, smooth).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Oscillator ?? 50).ToArray();

                    // Масштабируем Stochastic
                    var priceMin = _ohlcData.Min(x => x.Low);
                    var priceMax = _ohlcData.Max(x => x.High);
                    var priceRange = priceMax - priceMin;

                    var scaledValues = values.Select(v => priceMin + v / 100 * priceRange).ToArray();

                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, scaledValues);
                    plot.LegendText = $"{config.Name}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddWILLIAMS(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetWilliamsR(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.WilliamsR ?? 0).ToArray();

                    // Масштабируем Williams %R (обычно от -100 до 0)
                    var priceMin = _ohlcData.Min(x => x.Low);
                    var priceMax = _ohlcData.Max(x => x.High);
                    var priceRange = priceMax - priceMin;

                    var scaledValues = values.Select(v => priceMin + (v + 100) / 100 * priceRange).ToArray();

                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, scaledValues);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddMFI(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetMfi(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Mfi ?? 50).ToArray();

                    // Масштабируем MFI
                    var priceMin = _ohlcData.Min(x => x.Low);
                    var priceMax = _ohlcData.Max(x => x.High);
                    var priceRange = priceMax - priceMin;

                    var scaledValues = values.Select(v => priceMin + v / 100 * priceRange).ToArray();

                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, scaledValues);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddATR(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetAtr(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Atr ?? 0).ToArray();

                    // ATR отображаем как отдельную линию внизу графика
                    // Создаем новую ось Y для ATR
                    var atrAxis = _plotControl.Plot.Axes.AddLeftAxis();
                    atrAxis.Label.Text = "ATR";
                    atrAxis.Label.ForeColor = GetColorForIndicator(config.Name);

                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, values);
                    plot.Axes.YAxis = atrAxis;
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddBBANDS(List<Quote> quotes, IndicatorConfig config)
        {
            var parts = config.Parameters.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int period) &&
                double.TryParse(parts[1], out double stdDev))
            {
                var results = quotes.GetBollingerBands(period, stdDev).ToList();
                if (results.Any())
                {
                    var upper = results.Select(x => (double?)x.UpperBand ?? double.NaN).ToArray();
                    var lower = results.Select(x => (double?)x.LowerBand ?? double.NaN).ToArray();
                    var sma = results.Select(x => (double?)x.Sma ?? double.NaN).ToArray();
                    var xs = Enumerable.Range(0, upper.Length).Select(i => (double)i).ToArray();

                    var upperPlot = _plotControl.Plot.Add.Scatter(xs, upper);
                    upperPlot.LegendText = $"{config.Name} Upper";
                    upperPlot.LineStyle = new LineStyle { Color = GetColorForIndicator($"{config.Name} Upper"), Pattern = LinePattern.Dashed };
                    upperPlot.MarkerStyle = MarkerStyle.None;

                    var lowerPlot = _plotControl.Plot.Add.Scatter(xs, lower);
                    lowerPlot.LegendText = $"{config.Name} Lower";
                    lowerPlot.LineStyle = new LineStyle { Color = GetColorForIndicator($"{config.Name} Lower"), Pattern = LinePattern.Dashed };
                    lowerPlot.MarkerStyle = MarkerStyle.None;

                    var smaPlot = _plotControl.Plot.Add.Scatter(xs, sma);
                    smaPlot.LegendText = $"{config.Name} SMA";
                    smaPlot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                    smaPlot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddOBV(List<Quote> quotes, IndicatorConfig config)
        {
            var results = quotes.GetObv().ToList();
            if (results.Any())
            {
                var values = results.Select(x => (double?)x.Obv ?? 0).ToArray();

                // Масштабируем OBV для отображения на ценовом графике
                var min = values.Min();
                var max = values.Max();
                var priceMin = _ohlcData.Min(x => x.Low);
                var priceMax = _ohlcData.Max(x => x.High);
                var priceRange = priceMax - priceMin;
                var obvRange = max - min;

                var scaledValues = values.Select(v => priceMin + (v - min) / obvRange * priceRange * 0.5 + priceRange * 0.25).ToArray();

                var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                var plot = _plotControl.Plot.Add.Scatter(xs, scaledValues);
                plot.LegendText = $"{config.Name}";
                plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                plot.MarkerStyle = MarkerStyle.None;
            }
        }

        private void AddADL(List<Quote> quotes, IndicatorConfig config)
        {
            var results = quotes.GetAdl().ToList();
            if (results.Any())
            {
                var values = results.Select(x => (double?)x.Adl ?? 0).ToArray();

                // Масштабируем ADL
                var min = values.Min();
                var max = values.Max();
                var priceMin = _ohlcData.Min(x => x.Low);
                var priceMax = _ohlcData.Max(x => x.High);
                var priceRange = priceMax - priceMin;
                var adlRange = max - min;

                var scaledValues = values.Select(v => priceMin + (v - min) / adlRange * priceRange * 0.5 + priceRange * 0.25).ToArray();

                var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                var plot = _plotControl.Plot.Add.Scatter(xs, scaledValues);
                plot.LegendText = $"{config.Name}";
                plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                plot.MarkerStyle = MarkerStyle.None;
            }
        }

        private void AddCMF(List<Quote> quotes, IndicatorConfig config)
        {
            if (int.TryParse(config.Parameters, out int period))
            {
                var results = quotes.GetCmf(period).ToList();
                if (results.Any())
                {
                    var values = results.Select(x => (double?)x.Cmf ?? 0).ToArray();

                    // Масштабируем CMF (обычно от -1 до 1)
                    var priceMin = _ohlcData.Min(x => x.Low);
                    var priceMax = _ohlcData.Max(x => x.High);
                    var priceRange = priceMax - priceMin;

                    var scaledValues = values.Select(v => priceMin + (v + 1) / 2 * priceRange).ToArray();

                    var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                    var plot = _plotControl.Plot.Add.Scatter(xs, scaledValues);
                    plot.LegendText = $"{config.Name} {period}";
                    plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 1 };
                    plot.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private void AddVWAP(List<Quote> quotes, IndicatorConfig config)
        {
            var results = quotes.GetVwap().ToList();
            if (results.Any())
            {
                var values = results.Select(x => (double?)x.Vwap ?? double.NaN).ToArray();
                var xs = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
                var plot = _plotControl.Plot.Add.Scatter(xs, values);
                plot.LegendText = $"{config.Name}";
                plot.LineStyle = new LineStyle { Color = GetColorForIndicator(config.Name), Width = 2 };
                plot.MarkerStyle = MarkerStyle.None;
            }
        }
        #endregion

        private Color GetColorForIndicator(string indicatorName)
        {
            return indicatorName switch
            {
                "SMA" => ScottPlot.Colors.Blue,
                "EMA" => ScottPlot.Colors.Orange,
                "WMA" => ScottPlot.Colors.Purple,
                "HMA" => ScottPlot.Colors.Magenta,
                "RSI" => ScottPlot.Colors.Purple,
                "CCI" => ScottPlot.Colors.Brown,
                "STOCH" => ScottPlot.Colors.Teal,
                "WILLIAMS" => ScottPlot.Colors.Cyan,
                "MFI" => ScottPlot.Colors.Pink,
                "ATR" => ScottPlot.Colors.DarkCyan,
                "BBANDS" => ScottPlot.Colors.Gray,
                "BBANDS Upper" => ScottPlot.Colors.Gray,
                "BBANDS Lower" => ScottPlot.Colors.Gray,
                "OBV" => ScottPlot.Colors.DarkOrange,
                "ADL" => ScottPlot.Colors.DarkGreen,
                "CMF" => ScottPlot.Colors.DarkRed,
                "VWAP" => ScottPlot.Colors.DarkBlue,
                _ => ScottPlot.Colors.Black
            };
        }






        private async void LoadDealsFromDatabase()
        {
            try
            {
                var deals = await _transactionsService.ReadDBOpenDealsAsync();
                Deals.Clear();

                foreach (var deal in deals)
                {
                    Deals.Add(new DealMarker
                    {
                        EntryTime = (DateTime)deal.EntryDateTime,
                        EntryPrice = (double)deal.EntryPrice,
                        Direction = deal.Direction,
                        Quantity = deal.Quantity,
                        IsClosed = deal.Status == DealStatus.Closed,
                        ExitTime = deal.ExitDateTime,
                        ExitPrice = deal.ExitPrice.HasValue ? (double)deal.ExitPrice.Value : null
                    });
                }

                AddDealsToChart();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки сделок: {ex.Message}");
            }
        }

        private void AddDealsToChart()
        {
            if (_dateTimes == null) return;

            foreach (var deal in Deals)
            {
                // Находим индекс свечи, ближайший ко времени входа
                int entryIndex = FindNearestCandleIndex(deal.EntryTime);
                if (entryIndex < 0) continue;

                // Маркер входа
                var entryMarker = _plotControl.Plot.Add.Marker(
                    entryIndex,
                    deal.EntryPrice);
                entryMarker.LegendText = $"Вход {deal.Direction}";
                entryMarker.Color = deal.Direction == "Long" || deal.Direction == "Buy"
                    ? ScottPlot.Colors.Green
                    : ScottPlot.Colors.Red;

                // Используем доступные стили маркеров
                if (deal.Direction == "Long" || deal.Direction == "Buy")
                    entryMarker.MarkerStyle = MarkerStyle.Default/*FilledSquare*/;
                else
                    entryMarker.MarkerStyle = MarkerStyle.Default/*FilledCircle*/;

                entryMarker.MarkerSize = 10;

                if (deal.IsClosed && deal.ExitTime.HasValue && deal.ExitPrice.HasValue)
                {
                    // Находим индекс свечи для выхода
                    int exitIndex = FindNearestCandleIndex(deal.ExitTime.Value);
                    if (exitIndex < 0) continue;

                    var exitMarker = _plotControl.Plot.Add.Marker(
                        exitIndex,
                        deal.ExitPrice.Value);
                    exitMarker.LegendText = $"Выход {deal.Direction}";
                    exitMarker.Color = deal.Direction == "Long" || deal.Direction == "Buy"
                        ? ScottPlot.Colors.Green
                        : ScottPlot.Colors.Red;
                    exitMarker.MarkerStyle = MarkerStyle.Default/*FilledCircle*/;
                    exitMarker.MarkerSize = 10;

                    double[] xs = { entryIndex, exitIndex };
                    double[] ys = { deal.EntryPrice, deal.ExitPrice.Value };
                    var line = _plotControl.Plot.Add.Scatter(xs, ys);
                    line.LegendText = deal.Direction == "Long" ? "Long сделка" : "Short сделка";
                    line.LineStyle = new LineStyle
                    {
                        Color = deal.Direction == "Long" || deal.Direction == "Buy"
                            ? ScottPlot.Colors.Green
                            : ScottPlot.Colors.Red,
                        Width = 2,
                        Pattern = LinePattern.Dashed
                    };
                    line.MarkerStyle = MarkerStyle.None;
                }
            }
        }

        private int FindNearestCandleIndex(DateTime targetTime)
        {
            if (_ohlcData == null || _ohlcData.Count == 0)
                return -1;

            // Извлекаем массив дат для бинарного поиска
            var dates = _ohlcData.Select(o => o.DateTime).ToArray();

            // Бинарный поиск для быстрого нахождения ближайшего индекса
            int index = Array.BinarySearch(dates, targetTime);

            if (index >= 0)
                return index;

            // Если точное время не найдено, BinarySearch возвращает отрицательное число -
            // это побитовое дополнение индекса следующего элемента
            int nextIndex = ~index;

            if (nextIndex == 0)
                return 0;

            if (nextIndex >= dates.Length)
                return dates.Length - 1;

            // Выбираем ближайший индекс
            double prevDiff = (targetTime - dates[nextIndex - 1]).TotalSeconds;
            double nextDiff = (dates[nextIndex] - targetTime).TotalSeconds;

            return prevDiff < nextDiff ? nextIndex - 1 : nextIndex;
        }



        public void NotifyPriceUpdate()
        {
            _ = UpdateLastCandleAsync();
        }

        public void Dispose()
        {
            if (_plotControl != null)
            {
                _plotControl.MouseDown -= OnPlotMouseDown;
                _plotControl.MouseMove -= OnPlotMouseMove;
                _plotControl.MouseUp -= OnPlotMouseUp;
                _plotControl.MouseUp -= OnPlotMouseUpForZoom;
                _plotControl.MouseWheel -= OnPlotMouseWheel;
            }

            if (_strategyViewModel is StrategyViewModel svm)
            {
                PropertyChangedEventManager.RemoveHandler(svm, OnStrategyPropertyChanged, "");
            }

            _ohlcData?.Clear();
            Deals?.Clear();
        }
    }

    public class DealMarker
    {
        public DateTime EntryTime { get; set; }
        public double EntryPrice { get; set; }
        public string Direction { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public bool IsClosed { get; set; }
        public DateTime? ExitTime { get; set; }
        public double? ExitPrice { get; set; }
    }

    public class IndicatorConfig : ObservableObject
    {
        private string _name;
        private string _parameters;
        private string _description;
        private bool _isEnabled;
        private string _color;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Parameters
        {
            get => _parameters;
            set => SetProperty(ref _parameters, value);
        }

        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        public string Color
        {
            get => _color;
            set => SetProperty(ref _color, value);
        }
    }
}