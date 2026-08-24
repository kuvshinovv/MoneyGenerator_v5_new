using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.WellKnownTypes;
using HarfBuzzSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Strategies;
using MoneyGenerator_v5.Views;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq; // если еще нет
using System.Linq;
using System.Linq; // если еще нет
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json; // Для сериализации
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Tinkoff.InvestApi.V1;
using StrategyOrderType = MoneyGenerator_v5.Strategies.OrderType;


namespace MoneyGenerator_v5.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        
        private readonly Func<string, IProvirerService> _providerFactory;
        private IProvirerService _currentProvider;
        private readonly TokenManager _tokenManager;
        private readonly ILogger<MainViewModel> _logger;
        //private MainViewModel _mainViewModel;
        private ICollectionView _instrumentsView;
        private readonly ConnectionManager _connectionManager;
        private DateTime _lastChekAccountTime;
        private int _lastChekAccountTime_COOLDOWN_SECONDS = 30;
        private DateTime _lastWriteEqityTime;
        private int _lastWriteEqityTime_COOLDOWN_SECONDS = 1;
        //  поле для хранения представления коллекции
        private ICollectionView _filteredDealsView;

        /// <summary>
        /// Фильтрация сделок
        /// </summary>
        public ICollectionView FilteredDeals
        {
            get
            {
                // ✅ ИСПРАВЛЕНИЕ: Создаем представление только один раз
                if (_filteredDealsView == null)
                {
                    _filteredDealsView = CollectionViewSource.GetDefaultView(Deals);
                    _filteredDealsView.Filter = FilterDeals;
                }
                return _filteredDealsView;
            }
        }




        [ObservableProperty]
        private bool _isConnected;

        [ObservableProperty]
        private bool _isSandbox = true;

        [ObservableProperty]
        private string _connectionStatus = "Подключить?";

        [ObservableProperty]
        private string _connectionStatusColor = "Red";

        [ObservableProperty]
        private ObservableCollection<DataSource> _dataSources;

        [ObservableProperty]
        private DataSource _selectedDataSource;

        [ObservableProperty]
        private ObservableCollection<Models.Account> _accounts;

        [ObservableProperty]
        private Models.Account _selectedAccount;

        [ObservableProperty]
        private ObservableCollection<Models.Instrument> _instruments;

        [ObservableProperty]
        private Models.Instrument _selectedInstrument;

        [ObservableProperty]
        private string _searchText = "";

        [ObservableProperty]
        private ObservableCollection<MarketStatus> _marketStatuses;

        [ObservableProperty]
        private ObservableCollection<TimeFrame> _timeFrames;

        [ObservableProperty]
        private TimeFrame _selectedTimeFrame;

        [ObservableProperty]
        private ObservableCollection<TradingStrategy> _strategies;

        [ObservableProperty]
        private TradingStrategy _selectedStrategy;

        [ObservableProperty]
        private string _statusMessage = "Готово";

        [ObservableProperty]
        private bool _isReconnecting;

        [ObservableProperty]
        private string _connectionIcon = "🔴"; // 🔴 для отключено, 🟡 для реконнект, 🟢 для подключено

        [ObservableProperty]
        private bool _isNetworkAvailable = true;

        [ObservableProperty]
        private ObservableCollection<Models.Position> _positions = new();

        [ObservableProperty]
        private Models.Position _selectedPosition;  // Новое свойство для выбранной позиции (для контекстного меню в таблице позиций)


        #region Свойства для сделок

        [ObservableProperty]
        private ObservableCollection<Deal> _deals = new();

        [ObservableProperty]
        private Deal _selectedDeal;

        [ObservableProperty]
        private string _dealsFilterText = "";

        [ObservableProperty]
        private bool _showOnlyOpenDeals = false;

        [ObservableProperty]
        private string _dealsStatus = "Загрузка...";

        // Для принудительного обновления коллекции
        private readonly object _dealsLock = new object();

        // НОВЫЕ СВОЙСТВА: Итоговые значения P&L
        [ObservableProperty]
        private decimal _totalPnL;

        [ObservableProperty]
        private decimal _totalPnLPercent;

        [ObservableProperty]
        private string _totalPnLColor = "Gray";

        #region Свойства для сохранения стратегий

        [ObservableProperty]
        private string _selectedStrategyType;

        [ObservableProperty]
        private string _selectedTimeframe;

        [ObservableProperty]
        private decimal _capital = 100000;

        [ObservableProperty]
        private int _maxConcurrentTrades = 1;

        [ObservableProperty]
        private bool _useGlobalStopLoss;

        [ObservableProperty]
        private decimal _globalStopLossPercent = 2;

        [ObservableProperty]
        private bool _useGlobalTakeProfit;

        [ObservableProperty]
        private decimal _globalTakeProfitPercent = 5;

        [ObservableProperty]
        private int _lotSize = 1;

        [ObservableProperty]
        private decimal _maxRiskPercent = 2;

        [ObservableProperty]
        private bool _useTrailingStop;

        [ObservableProperty]
        private decimal _trailingStopPercent = 1;

        // Параметры SMA Cross
        [ObservableProperty]
        private int _smaFastPeriod = 10;

        [ObservableProperty]
        private int _smaSlowPeriod = 30;

        // Параметры RSI
        [ObservableProperty]
        private int _rsiPeriod = 14;

        [ObservableProperty]
        private int _rsiOversold = 30;

        [ObservableProperty]
        private int _rsiOverbought = 70;

        // Параметры MACD
        [ObservableProperty]
        private int _macdFastPeriod = 12;

        [ObservableProperty]
        private int _macdSlowPeriod = 26;

        [ObservableProperty]
        private int _macdSignalPeriod = 9;

        // Параметры Bollinger Bands
        [ObservableProperty]
        private int _bbPeriod = 20;

        [ObservableProperty]
        private double _bbStdDev = 2.0;

        // Параметры Volume Strategy
        [ObservableProperty]
        private long _volumeThreshold = 1000000;

        [ObservableProperty]
        private double _minVolumeRatio = 1.5;

        [ObservableProperty]
        private bool _isStrategyRunning;

        [ObservableProperty]
        private string _loadingMessage = "";


        // Добавьте в начало класса MainViewModel:
        private readonly OperationHistoryService _operationHistoryService;
        private Timer _operationsRefreshTimer;

        // Новые свойства:
        [ObservableProperty]
        private ObservableCollection<Models.Operation> _operations = new();  

        [ObservableProperty]
        private string _operationsStatus = "Загрузка...";

        [ObservableProperty]
        private decimal _totalOperationsPnL;

        [ObservableProperty]
        private string _totalOperationsPnLColor = "Gray";

        public ICommand RefreshOperationsCommand { get; }
        [ObservableProperty]
        private ObservableCollection<ProcessedOperation> _processedOperations = new();

        [ObservableProperty]
        private string _processedOperationsStatus = "Загрузка...";

        [ObservableProperty]
        private bool _showOnlyOpenProcessedOps = false;

        [ObservableProperty]
        private decimal _totalProcessedNetProfit;

        [ObservableProperty]
        private string _totalProcessedNetProfitColor = "Gray";

        [ObservableProperty]
        private decimal _totalProcessedGrossProfit;

        [ObservableProperty]
        private string _totalProcessedGrossProfitColor = "Gray";

        [ObservableProperty]
        private string _operationsSearchText = "";

        [ObservableProperty]
        private bool _showOnlyOpenOperations = false;

        [ObservableProperty]
        private ProcessedOperation _selectedProcessedOperation;

        private bool _isBacktestMode = false;
        public bool IsBacktestMode
        {
            get => _isBacktestMode;
            set => _isBacktestMode = value;
        }










        // Новая команда для закрытия сделки из таблицы операций
        public ICommand CloseOperationCommand { get; }
        public ICommand FlipOperationCommand { get; }

        private ICollectionView _filteredProcessedOpsView;
        private readonly OperationProcessingService _operationProcessingService;

        public ICollectionView FilteredProcessedOperations
        {
            get
            {
                if (_filteredProcessedOpsView == null)
                {
                    _filteredProcessedOpsView = CollectionViewSource.GetDefaultView(ProcessedOperations);
                    _filteredProcessedOpsView.Filter = FilterProcessedOps;
                }
                return _filteredProcessedOpsView;
            }
        }

        #endregion



        #endregion











        [RelayCommand]
        private async Task ManualReconnectAsync()
        {
            StatusMessage = "Ручное переподключение...";

            if (_currentProvider != null)
            {
                await _currentProvider.DisconnectAsync();
                await Task.Delay(2000);

                var success = await _currentProvider.ConnectAsync(_isSandbox);
                if (success)
                {
                    StatusMessage = "Ручное переподключение успешно";
                }
                else
                {
                    StatusMessage = "Ручное переподключение не удалось";
                }
            }
        }

        [ObservableProperty]
        private bool _canRestoreSubscriptions = false;

        [RelayCommand]
        private async Task RestoreSubscriptionsAsync()
        {
            StatusMessage = "Восстановление подписок...";

            try
            {
                await ForceRestoreAllSubscriptionsAsync();
                StatusMessage = "Подписки восстановлены";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Ошибка восстановления: {ex.Message}";
            }
        }








        /// <summary>
        /// Команда для ручного обновления сделок
        /// </summary>
        public ICommand RefreshDealsCommand { get; }
        public ICommand DiagnoseCommand { get; }



        public ICommand ConnectCommand { get; }
        public ICommand OpenStrategyCommand { get; }
        public ICommand UPDPositionsManualyCommand { get; }
        public ICommand ClearDatabaseCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand AboutCommand { get; }

        // Команда для открытия графика эквити
        public ICommand ShowEquityChartCommand { get; }

        // Команды для работы со сделками
        public ICommand CloseDealCommand { get; }
        public ICommand FlipDealCommand { get; }

        public ICommand LoadSavedStrategiesCommand { get; }

        //  команды для для контекстного меню в таблице позиций
        public ICommand ClosePositionCommand { get; }
        public ICommand FlipPositionCommand { get; }



        public MainViewModel(
            Func<string, IProvirerService> providerFactory,
            TokenManager tokenManager,
            ConnectionManager connectionManager,
            ILogger<MainViewModel> logger)
        {
            _providerFactory = providerFactory;
            _tokenManager = tokenManager;
            _logger = logger;
            _currentProvider = _providerFactory("Тинькофф"); // По умолчанию
            _connectionManager = connectionManager;



            InitializeData();

            ConnectCommand = new RelayCommand(async () => await ToggleConnection());
            OpenStrategyCommand = new RelayCommand(OpenStrategyWindow, () => CanOpenStrategy());
            UPDPositionsManualyCommand = new RelayCommand(async () => await UdatePositionsManualy());
            RefreshDealsCommand = new RelayCommand(async () => await LoadDealsAsync());
            ClearDatabaseCommand = new RelayCommand(async () => await ClearDatabaseAsync(), () => !IsConnected);
            ExitCommand = new RelayCommand(ExitApplication);
            AboutCommand = new RelayCommand(ShowAbout);
            ShowEquityChartCommand = new RelayCommand(ShowEquityChart);
            CloseDealCommand = new RelayCommand<Deal>(async (deal) => await CloseDealAsync(deal),(deal) => deal != null && deal.Status == DealStatus.Open);
            FlipDealCommand = new RelayCommand<Deal>(async (deal) => await FlipDealAsync(deal),(deal) => deal != null && deal.Status == DealStatus.Open);
            LoadSavedStrategiesCommand = new RelayCommand(async () => await LoadSavedStrategiesAsync(), () => IsConnected);
            ClosePositionCommand = new RelayCommand<Models.Position>(async (position) => await ClosePositionAsync(position), position => position != null);
            FlipPositionCommand = new RelayCommand<Models.Position>(async (position) => await FlipPositionAsync(position), position => position != null);
            DiagnoseCommand = new RelayCommand(async () => await DiagnoseAsync());
            CloseOperationCommand = new RelayCommand<ProcessedOperation>(async (op) => await CloseOperationAsync(op), op => op != null && op.Status.Contains("Open"));
            FlipOperationCommand = new RelayCommand<ProcessedOperation>(async (op) => await FlipOperationAsync(op), op => op != null && op.Status.Contains("Open"));
            _operationHistoryService = new OperationHistoryService(_logger);
            _operationProcessingService = new OperationProcessingService();
            RefreshOperationsCommand = new RelayCommand(async () => await RefreshProcessedOperationsAsync());   //ПОКА ОТКЛЮЧИЛ!!!!!!!!!!!!!!!!!!

            // ✅ ИСПРАВЛЕННАЯ ПОДПИСКА НА СОБЫТИЯ
            _connectionManager.OnConnectionStateChanged += OnConnectionStateChanged;

            // Регистрируем провайдера при подключении
            _connectionManager.RegisterProvider(_currentProvider);

            // Подписываемся на события обновления позиций (они уже есть)
            if (_currentProvider is TinkoffApiService tinkoffService)
            {
                tinkoffService.OnPositionsUpdated += UpdatePositions;
                // Добавляем подписку на обновление сделок
                tinkoffService.OnDealsUpdated += OnDealsUpdated;

                // ✅ ПОДПИСЫВАЕМСЯ НА ОБНОВЛЕНИЯ БАЛАНСА
                tinkoffService.OnAccountBalanceUpdated += OnAccountBalanceUpdated;

                tinkoffService.OnPositionsUpdated += async (positions) => await OnPortfolioChanged();
                tinkoffService.OnAccountBalanceUpdated += async (account) => await OnPortfolioChanged();
            }



            


        }

        public MainViewModel()
        {
        }

        private void InitializeData()
        {
            // Источники данных
            DataSources = new ObservableCollection<DataSource>
            {
                new DataSource { Name = "Тинькофф", IsEnabled = true },
                new DataSource { Name = "Финам", IsEnabled = true },
                new DataSource { Name = "Алор", IsEnabled = true }
            };
            SelectedDataSource = DataSources.First();

            // Таймфреймы
            TimeFrames = new ObservableCollection<TimeFrame>
            {
                new TimeFrame("1 мин", "1min"),
                new TimeFrame("5 мин", "5min"),
                new TimeFrame("10 мин", "10min"),
                new TimeFrame("15 мин", "15min"),
                new TimeFrame("30 мин", "30min"),
                new TimeFrame("1 час", "1hour"),
                new TimeFrame("2 часа", "2hour"),
                new TimeFrame("4 часа", "4hour"),
                new TimeFrame("1 день", "1day"),
                new TimeFrame("1 неделя", "1week")
            };
            SelectedTimeFrame = TimeFrames.First();

            // Стратегии
            Strategies = new ObservableCollection<TradingStrategy>
            {
                new TradingStrategy("Мануал", "Manual"),
                new TradingStrategy("RSI", "RSI"),
                new TradingStrategy("MA", "MA"),
                new TradingStrategy("Рейтинговая", "Rating"),
                new TradingStrategy("Стат. Арбитраж", "PairsTrading") 
            };
            SelectedStrategy = Strategies.First();

            // Статусы рынков - изначально неизвестны
            MarketStatuses = new ObservableCollection<MarketStatus>
            {
                new MarketStatus { Name = "Фондовый рынок MOEX", Status = "Нет данных", IsTrading = false, Color = "Gray" },
                new MarketStatus { Name = "Срочный рынок MOEX", Status = "Нет данных", IsTrading = false, Color = "Gray" }
            };

            Accounts = new ObservableCollection<Models.Account>();
            Instruments = new ObservableCollection<Models.Instrument>();

            // Инициализируем статус операций
            ProcessedOperationsStatus = "Ожидание данных...";
            OperationsStatus = "Ожидание данных...";

        }

        private async Task ToggleConnection()
        {
            if (IsConnected)
            {
                await Disconnect();
            }
            else
            {
                await Connect();
            }
        }

        private async Task Connect()
        {
            try
            {
                StatusMessage = "Подключение...";

                var providerName = SelectedDataSource.Name;
                _currentProvider = _providerFactory(providerName);

                // Регистрируем провайдера в менеджере соединений
                _connectionManager.RegisterProvider(_currentProvider);

                // Подписываемся на обновления статусов рынков
                if (_currentProvider is TinkoffApiService tinkoffService)
                {
                    
                    tinkoffService.OnMarketStatusesUpdated += UpdateMarketStatuses;

                    // Подписываемся на обновления позиций
                    tinkoffService.OnPositionsUpdated += UpdatePositions;
                }

                var success = await _currentProvider.ConnectAsync(_isSandbox);

                if (success)
                {
                    IsConnected = true;
                    ConnectionStatus = "Подключено";
                    ConnectionStatusColor = "Green";

                    await LoadAccounts();
                    await LoadInstruments();

                    if (_isSandbox)
                    {
                        SelectedAccount.Name = "Песочница";
                        OnPropertyChanged(SelectedAccount.Name);
                    }
                    else
                    {
                        SelectedAccount.Name = "Реальный счет";
                        OnPropertyChanged(SelectedAccount.Name);
                    }

                    StatusMessage = $"Успешно подключено к {providerName}";

                    // ✅ Загружаем сделки и инициализируем историю операций ФОНОМ
                    await LoadDealsAsync();

                    // ✅ Фоновая инициализация истории операций (без блокировки UI)
                    _ = InitializeOperationsHistoryAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка подключения");
                StatusMessage = $"Ошибка: {ex.Message}";
            }

            
        }

        private async Task Disconnect()
        {
            try
            {
                // Отписываемся от событий
                if (_currentProvider is TinkoffApiService tinkoffService)
                {
                    tinkoffService.OnMarketStatusesUpdated -= UpdateMarketStatuses;
                    tinkoffService.OnPositionsUpdated -= UpdatePositions;

                    // ✅ ОТПИСЫВАЕМСЯ - не будем отписываться чтобы не создавать новую подписку..  пусть работат и после реконнекта продолжает принимать изменения
                    //tinkoffService.OnAccountBalanceUpdated -= OnAccountBalanceUpdated;
                }

                await _currentProvider.DisconnectAsync();

                IsConnected = false;
                ConnectionStatus = "Отключено\nподключить?";
                ConnectionStatusColor = "Red";

                Accounts.Clear();
                Instruments.Clear();
                Positions.Clear(); // Очищаем позиции

                // Сбрасываем статусы рынков
                foreach (var status in MarketStatuses)
                {
                    status.Status = "Нет данных";
                    status.IsTrading = false;
                }

                StatusMessage = "Отключено";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка отключения");
                StatusMessage = $"Ошибка: {ex.Message}";
            }
        }

        private async Task LoadAccounts()
        {
            try
            {
                var accounts = await _currentProvider.GetAccountsAsync();

                Accounts.Clear();
                foreach (var account in accounts)
                {
                    Accounts.Add(account);
                }

                if (Accounts.Any())
                {
                    SelectedAccount = Accounts.First();
                }

                if (_isSandbox)
                {
                    SelectedAccount.Name = "Песочница";
                }
                else
                {
                    SelectedAccount.Name = "Реальный счет";
                }


                Debug.WriteLine($"DEBUG: LoadAccounts -------------SelectedAccount: {SelectedAccount}  ");


                OnPropertyChanged(SelectedAccount.Name);
                // Явно обновляем отображение
                OnPropertyChanged(nameof(SelectedAccount));

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки счетов");
                StatusMessage = $"Ошибка загрузки счетов: {ex.Message}";
            }
        }

        private async Task LoadInstruments()
        {
            try
            {
                var instruments = await _currentProvider.GetInstrumentsAsync();

                Instruments.Clear();
                foreach (var instrument in instruments)
                {
                    Instruments.Add(instrument);
                    Debug.WriteLine($"[LoadInstruments] DEBUG  instrument - Name:{instrument.Name}  PriceStep:{instrument.PriceStep}  LotSize:{instrument.LotSize}  MinLotSize:{instrument.MinLotSize}  MinStepPrice:{instrument.MinStepPrice}");
                }

                _instrumentsView = CollectionViewSource.GetDefaultView(Instruments);
                _instrumentsView.Filter = null; // Сбрасываем фильтр

                StatusMessage = $"Загружено {Instruments.Count} инструментов";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки инструментов");
                StatusMessage = $"Ошибка загрузки инструментов: {ex.Message}";
            }
        }

        partial void OnIsSandboxChanged(bool value)
        {
            if (IsConnected)
            {
                StatusMessage = "Переключение режима. Переподключитесь.";
                _ = Disconnect();
            }
        }

        partial void OnSearchTextChanged(string value)
        {
            if (_instrumentsView != null)
            {
                _instrumentsView.Filter = item =>
                {
                    if (string.IsNullOrWhiteSpace(value))
                        return true;

                    var instrument = item as Models.Instrument;
                    return instrument?.DisplayName?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
                };
            }
        }

        partial void OnIsConnectedChanged(bool value)
        {
            // Уведомить команду об изменении состояния
            CanRestoreSubscriptions = value;
            (OpenStrategyCommand as RelayCommand)?.NotifyCanExecuteChanged();

            // ✅ ДОБАВЛЯЕМ: Обновляем состояние команды загрузки стратегий
            (LoadSavedStrategiesCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

       

        /// <summary>
        /// Обработчик обновления баланса счета
        /// </summary>
        private void OnAccountBalanceUpdated(Models.Account updatedAccount)
        {
            try
            {
                if (System.Windows.Application.Current == null) return;

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    // Находим существующий аккаунт в коллекции
                    var existingAccount = Accounts.FirstOrDefault(a => a.Id == updatedAccount.Id);

                    if (existingAccount != null)
                    {
                        // Обновляем баланс (модель сама вызовет PropertyChanged)
                        existingAccount.Balance = updatedAccount.Balance;

                        //Debug.WriteLine($"DEBUG: ---------------------=================Баланс счета {existingAccount.Name} обновлен в UI: Balance={updatedAccount.Balance:F2}         DisplayBalance={existingAccount.DisplayBalance}      ");

                        // Если это выбранный счет, обновляем заголовок
                        if (SelectedAccount?.Id == updatedAccount.Id)
                        {
                            // Принудительно обновляем отображение выбранного счета
                            OnPropertyChanged(SelectedAccount.Name);
                            // Явно обновляем отображение
                            OnPropertyChanged(nameof(SelectedAccount));

                            // Обновляем статусную строку
                            StatusMessage = $"Баланс: {updatedAccount.Balance:F2} {updatedAccount.Currency}";

                            // Сохраняем баланс в таблицу эквити, но не чаще чем раз в 60 секунды.
                            if ((DateTime.Now - _lastWriteEqityTime).TotalSeconds > _lastWriteEqityTime_COOLDOWN_SECONDS)
                            {
                                // Сохраняем баланс в таблицу эквити
                                _ = Task.Run(async () =>
                                {
                                    // Записываем 
                                    await SaveBalanceToEquityAsync(SelectedAccount?.Id, updatedAccount.Balance);
                                });

                                // Обновляем время последнего входа
                                _lastWriteEqityTime = DateTime.Now;
                            }



                        }
                    }
                    else
                    {
                        // Если аккаунта нет в коллекции, добавляем его
                        Accounts.Add(updatedAccount);
                        Debug.WriteLine($"DEBUG: Добавлен новый счет {updatedAccount.Id} с балансом {updatedAccount.Balance:F2}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка обновления баланса в UI: {ex.Message}");
            }
        }




        partial void OnSelectedInstrumentChanged(Models.Instrument value)
        {
            // Уведомить команду об изменении выбранного инструмента
            (OpenStrategyCommand as RelayCommand)?.NotifyCanExecuteChanged();

            // сохраняем состояние подписок при открытии нового окна
            if (IsConnected && _currentProvider is TinkoffApiService tinkoffService)
            {
                // Не отключаем подписки при открытии нового окна
                Debug.WriteLine($"Открытие StrategyViewModel. Подписки на статусы рынков остаются активными.");
            }
        }

        private bool CanOpenStrategy()
        {
            return IsConnected && SelectedInstrument != null;
        }

        private void OpenStrategyWindow()
        {


            try
            {
                // Создаем StrategyViewModel с tradingService
                var strategyVM = new StrategyViewModel(
                    SelectedStrategy,
                    SelectedInstrument,
                    SelectedTimeFrame,
                    SelectedAccount,
                    _currentProvider,
                    _connectionManager,                   
                    null); // Логгер опционален

                var strategyWindow = new StrategyWindow
                {
                    DataContext = strategyVM,
                    Owner = Application.Current.MainWindow
                };

                // ✅ СОХРАНЯЕМ ПАРАМЕТРЫ СТРАТЕГИИ ПОСЛЕ ЗАПУСКА
                strategyVM.StrategyStarted += async () =>
                {
                    await SaveStrategyParametersAsync(strategyVM);
                };

                strategyWindow.Closed += async (s, e) =>
                {
                    try
                    {
                        await strategyVM.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка при закрытии окна стратегии: {ex.Message}");
                    }
                };

                strategyWindow.Show();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка при открытии окна стратегии: {ex.Message}");
                StatusMessage = $"Ошибка открытия стратегии: {ex.Message}";
            }
        }

        // Метод обновления статусов рынков
        private void UpdateMarketStatuses(List<MarketStatus> statuses)
        {
            try
            {
                if (statuses == null || statuses.Count == 0)
                {
                    Debug.WriteLine("DEBUG: MainViewModel: UpdateMarketStatuses: получен пустой список статусов");
                    return;
                }

                Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: UpdateMarketStatuses вызван. Получено статусов: {statuses.Count}");

                // Проверяем, есть ли UI приложение
                if (System.Windows.Application.Current == null)
                {
                    Debug.WriteLine("DEBUG: MainViewModel: UpdateMarketStatuses: UI приложение не доступно");
                    return;
                }

                // Выполняем в UI потоке
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: UI поток. MarketStatuses.Count: {MarketStatuses?.Count}");

                        if (MarketStatuses == null)
                        {
                            Debug.WriteLine("DEBUG: MainViewModel: UpdateMarketStatuses: MarketStatuses коллекция null");
                            return;
                        }

                        foreach (var status in statuses)
                        {
                            Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: Обработка статуса: {status.Name} = {status.Status}, Trading: {status.IsTrading}");

                            var existingStatus = MarketStatuses.FirstOrDefault(s => s.Name == status.Name);
                            if (existingStatus != null)
                            {
                                // Обновляем только если статус действительно изменился
                                if (existingStatus.Status != status.Status ||
                                    existingStatus.IsTrading != status.IsTrading)
                                {
                                    Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: Обновляем статус для {status.Name}");

                                    existingStatus.Status = status.Status;
                                    existingStatus.IsTrading = status.IsTrading;
                                    existingStatus.LastUpdate = status.LastUpdate;

                                    // Установите цвет в зависимости от статуса торгов
                                    if (status.Status == "Нет данных")
                                    {
                                        existingStatus.Color = "Gray";
                                    }
                                    else
                                    {
                                        existingStatus.Color = existingStatus.IsTrading ? "Green" : "Red";
                                    }

                                    // Вызываем уведомления для конкретного объекта
                                    existingStatus.OnPropertyChanged(nameof(existingStatus.Status));
                                    existingStatus.OnPropertyChanged(nameof(existingStatus.IsTrading));
                                    existingStatus.OnPropertyChanged(nameof(existingStatus.LastUpdate));
                                    existingStatus.OnPropertyChanged(nameof(existingStatus.Color));

                                    Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: Статус обновлен: {status.Name} -> {status.Status} (Торги: {status.IsTrading})");
                                }
                                else
                                {
                                    Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: Статус не изменился для {status.Name}");
                                }
                            }
                            else
                            {
                                Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: Статус {status.Name} не найден в коллекции. Добавляем...");

                                // Добавляем новый статус, если его нет
                                var newStatus = new MarketStatus
                                {
                                    Name = status.Name,
                                    Status = status.Status,
                                    IsTrading = status.IsTrading,
                                    LastUpdate = status.LastUpdate,
                                    Color = status.Status == "Нет данных" ? "Gray" :
                                           (status.IsTrading ? "Green" : "Red")
                                };

                                MarketStatuses.Add(newStatus);
                                OnPropertyChanged(nameof(MarketStatuses));
                            }
                        }

                        Debug.WriteLine("DEBUG: MainViewModel: UpdateMarketStatuses: Обновление завершено");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: Ошибка в UI потоке UpdateMarketStatuses: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: MainViewModel: UpdateMarketStatuses: Ошибка в UpdateMarketStatuses: {ex.Message}");
                _logger.LogError(ex, "Ошибка обновления статусов рынков");
            }
        }


        // Добавить обработчики событий
        private void OnConnectionStateChanged(bool isConnected)
        {
            Debug.WriteLine($"DEBUG: MainViewModel: ConnectionStateChanged: {isConnected}");

            ConnectionStatus = isConnected ? "Подключен" : "Отключен";
            ConnectionStatusColor = isConnected ? "Green" : "Red";
            ConnectionIcon = isConnected ? "🟢" : "🔴";

            if (isConnected)
            {
                IsConnected = true;
                StatusMessage = "Соединение восстановлено";
            }
            else
            {
                IsConnected = false;
                StatusMessage = "Потеряно соединение";
                ConnectionStatusColor = "Orange";
                ConnectionIcon = "🟡";
            }

            // Обновляем состояние команды очистки БД
            (ClearDatabaseCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        // При изменении выбранного счета обновляем отображение позиций
        partial void OnSelectedAccountChanged(Models.Account value)
        {
            if (value != null && IsConnected && _currentProvider is TinkoffApiService tinkoffService)
            {
                // Получаем текущие позиции для выбранного счета
                var positions = tinkoffService.Positions
                    .Where(p => p.AccountId == value.Id)
                    .ToList();

                Positions.Clear();
                foreach (var position in positions)
                {
                    Positions.Add(position);
                }

                StatusMessage = $"Позиций: {positions.Count}";
            }
        }
       


        public async Task ForceRestoreAllSubscriptionsAsync()
        {
            try
            {
                Debug.WriteLine($"DEBUG: MainViewModel:  ForceRestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Принудительное восстановление всех подписок");

                // Восстанавливаем подписки текущего провайдера
                if (_currentProvider is TinkoffApiService tinkoffService)
                {
                    try
                    {
                        // ПРОВЕРЯЕМ, что мы действительно отключены перед восстановлением
                        if (!IsConnected)
                        {
                            Debug.WriteLine($"DEBUG: MainViewModel:  ForceRestoreAllSubscriptionsAsync: Соединение не активно, пропускаем восстановление");
                            return;
                        }








                        // Сначала принудительно обновляем статусы рынков
                        //await tinkoffService.UpdateMarketStatusesAsync();
                        //Debug.WriteLine($"DEBUG: MainViewModel:  ForceRestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Статусы рынков принудительно обновлены");

                        // Затем восстанавливаем остальные подписки
                        await tinkoffService.RestoreSubscriptionsAsync();
                        Debug.WriteLine($"DEBUG: MainViewModel:  ForceRestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Подписки провайдера восстановлены");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: MainViewModel:  ForceRestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка восстановления подписок провайдера: {ex.Message}");

                        // Пробуем еще раз обновить статусы
                        //try
                        //{
                        //    await tinkoffService.UpdateMarketStatusesAsync();
                        //}
                        //catch { }
                    }
                }


               

                Debug.WriteLine($"DEBUG: MainViewModel:  ForceRestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Подписки всех стратегий через ConnectionManager восстановлены");
            }
            catch (Exception ex)
            {   
                Debug.WriteLine($"DEBUG: MainViewModel:  ForceRestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка при восстановлении подписок: {ex.Message}");
            }
        }

        // Метод обновления позиций
        private async void UpdatePositions(List<Models.Position> positions)
        {

            // ✅ ПРОПУСКАЕМ если бэктест-режим
            if (_isBacktestMode)
            {
                // Не обновляем UI во время бэктеста
                return;
            }


            Debug.WriteLine($"DEBUG - UpdatePositions - -------------------------------------------------------");



            try
            {
                if (System.Windows.Application.Current == null) return;

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // Фильтруем позиции по выбранному счету
                        var filteredPositions = positions
                            .Where(p => SelectedAccount?.Id == p.AccountId)
                            .ToList();

                        Positions.Clear();
                        foreach (var position in filteredPositions)
                        {
                            Positions.Add(position);
                        }

                        // Обновляем статус
                        StatusMessage = $"Позиций: {filteredPositions.Count} | Обновлено: {DateTime.Now:HH:mm:ss}";

                        Debug.WriteLine($"Обновлено позиций: {filteredPositions.Count}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка обновления позиций в UI: {ex.Message}");
                    }
                });

                //await UdatePositionsManualy();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка в UpdatePositions: {ex.Message}");
            }

            /*try
            {
                if (System.Windows.Application.Current == null) return;

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        //SelectedAccount

                        // Фильтруем позиции по выбранному счету
                        var allPositions = _currentProvider.RefreshPositionsAsync(SelectedAccount?.Id);

                        *//*Positions.Clear();
                        foreach (var position in allPositions)
                        {
                            Positions.Add(position);
                        }

                        // Обновляем статус
                        StatusMessage = $"Позиций: {allPositions.Count} | Обновлено: {DateTime.Now:HH:mm:ss}";

                        Debug.WriteLine($"Обновлено позиций: {allPositions.Count}");*//*


                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка обновления позиций в UI: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка в UdatePositionsManualy: {ex.Message}  {ex.StackTrace}");
            }*/


        }

        private async Task UdatePositionsManualy()
        {
            Debug.WriteLine($"DEBUG - UdatePositionsManualy - -------------------------------------------------------");

            try
            {
                if (System.Windows.Application.Current == null) return;

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        //SelectedAccount

                        // Фильтруем позиции по выбранному счету
                        var allPositions = _currentProvider.RefreshPositionsAsync(SelectedAccount?.Id);

                        /*Positions.Clear();
                        foreach (var position in allPositions)
                        {
                            Positions.Add(position);
                        }

                        // Обновляем статус
                        StatusMessage = $"Позиций: {allPositions.Count} | Обновлено: {DateTime.Now:HH:mm:ss}";

                        Debug.WriteLine($"Обновлено позиций: {allPositions.Count}");*/
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка обновления позиций в UI: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка в UdatePositionsManualy: {ex.Message}  {ex.StackTrace}");
            }

        }


        #region Управление сделками
        /// <summary>
        /// Загрузка сделок из БД
        /// </summary>
        public async Task LoadDealsAsync()
        {
            // ✅ ДОБАВИТЬ
            if (_isBacktestMode)
            {
                Debug.WriteLine("MainViewModel: LoadDealsAsync пропущен (бэктест-режим)");
                return;
            }


            if (!IsConnected) return;

            // ✅ Сохраняем текущую выбранную сделку перед обновлением
            var currentSelectedDeal = SelectedDeal;
            string selectedDealId = currentSelectedDeal?.Id.ToString();

            try
            {
                string dbPath = System.IO.Path.Combine(
                   System.AppDomain.CurrentDomain.BaseDirectory,
                   "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Id, Ticker, InstrumentUid, Strategy, EntryTime, EntryPrice, EntryQuantity, 
                           EntryOrderId, Direction, ExitTime, ExitPrice, ExitOrderId, Status, 
                           ClosedPnL, ClosedPnLPercent, Comment, CreatedAt, UpdatedAt
                    FROM DealsJournal
                    ORDER BY EntryTime DESC
                    LIMIT 500"; // Последние 500 сделок

                var deals = new List<Deal>();

                decimal totalPnL = 0;
                decimal totalPnLPercent = 0;
                int closedDealsCount = 0;

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var deal = new Deal
                    {
                        Id = reader.GetInt64(0),
                        Ticker = reader.GetString(1),
                        InstrumentUid = reader.GetString(2),
                        Strategy = reader.GetString(3),
                        EntryTime = reader.GetDateTime(4),
                        EntryPrice = reader.GetDecimal(5),
                        EntryQuantity = reader.GetInt32(6),
                        EntryOrderId = reader.GetString(7),
                        Direction = reader.GetString(8),
                        ExitTime = reader.IsDBNull(9) ? null : (DateTime?)reader.GetDateTime(9),
                        ExitPrice = reader.IsDBNull(10) ? null : (decimal?)reader.GetDecimal(10),
                        ExitOrderId = reader.IsDBNull(11) ? null : reader.GetString(11),
                        Status = System.Enum.Parse<DealStatus>(reader.GetString(12)),
                        ClosedPnL = reader.IsDBNull(13) ? null : (decimal?)reader.GetDecimal(13),
                        ClosedPnLPercent = reader.IsDBNull(14) ? null : (decimal?)reader.GetDecimal(14),
                        Comment = reader.IsDBNull(15) ? null : reader.GetString(15),
                        CreatedAt = reader.GetDateTime(16),
                        UpdatedAt = reader.GetDateTime(17),
                    };

                    deals.Add(deal);


                    // Суммируем P&L всех сделок
                    if (deal.Status == DealStatus.Closed && deal.ClosedPnL.HasValue ||
                deal.Status == DealStatus.Open && deal.ClosedPnL.HasValue)
                    {
                        totalPnL += deal.ClosedPnL.Value;
                        totalPnLPercent += deal.ClosedPnLPercent.Value;
                    }

                    if (deal.Status == DealStatus.Closed && deal.ClosedPnL.HasValue)
                    {
                        closedDealsCount++;
                    }

                }



                // Обновляем коллекцию в UI потоке
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    lock (_dealsLock)
                    {
                        Deals.Clear();
                        foreach (var deal in deals)
                        {
                            Deals.Add(deal);
                        }
                        DealsStatus = $"Сделок: {deals.Count} (закрыто: {closedDealsCount})";

                        TotalPnL = totalPnL;
                        TotalPnLPercent = totalPnLPercent;
                        TotalPnLColor = totalPnL >= 0 ? "DarkGreen" : "Red";

                        // ✅ ВОССТАНАВЛИВАЕМ ВЫБРАННУЮ СДЕЛКУ
                        if (!string.IsNullOrEmpty(selectedDealId))
                        {
                            var restoredDeal = Deals.FirstOrDefault(d => d.Id.ToString() == selectedDealId);
                            if (restoredDeal != null)
                            {
                                SelectedDeal = restoredDeal;
                            }
                        }

                        // ✅ ДОБАВИТЬ: обновляем итоги после загрузки
                        UpdateTotalFromFilteredDeals();
                    }

                    // ✅ ИСПРАВЛЕНИЕ: Обновляем фильтр, если представление существует
                    _filteredDealsView?.Refresh();
                });

                //Debug.WriteLine($"DEBUG: Загружено {deals.Count} сделок, Итого P&L: {totalPnL:F2}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка загрузки сделок: {ex.Message}");
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    DealsStatus = $"Ошибка: {ex.Message}";
                });
            }



            // ✅ УБИРАЕМ ВЫЗОВ GetAccountBalanceAsync() - баланс обновляется через OnAccountBalanceUpdated
            // Баланс обновляется автоматически через события TinkoffApiService

            /*// тут обновим счет, но не чаще чем раз в 5 секунды.
            // Проверяем таймаут для не частого опроса АПИ
            if ((DateTime.Now - _lastChekAccountTime).TotalSeconds > _lastChekAccountTime_COOLDOWN_SECONDS)
            {
                await _currentProvider.GetAccountBalanceAsync();
                // Обновляем время последнего входа
                _lastChekAccountTime = DateTime.Now;
            }
            else
            {
                //Debug.WriteLine($"Таймаут на проверке счета чтобы не грузить АПИ: прошло {(DateTime.Now - _lastChekAccountTime).TotalSeconds:F1} секунд из {_lastChekAccountTime_COOLDOWN_SECONDS}");
            }*/





        }


        /// <summary>
        /// Обновление таблицы операций после добавления новых операций
        /// </summary>
        public async Task RefreshProcessedOperationsAsync()
        {
            // ✅ ПРОПУСКАЕМ если бэктест-режим
            if (_isBacktestMode)
            {
                Debug.WriteLine("MainViewModel: RefreshProcessedOperationsAsync пропущен (бэктест-режим)");
                return;
            }


            try
            {
                if (!IsConnected || SelectedAccount == null || !_operationHistoryService.IsInitialized())
                    return;

                // Обновляем историю и получаем перегруппированные сделки
                var processedOps = await _operationHistoryService.UpdateHistoryAndReprocessAsync(
                    _currentProvider,
                    SelectedAccount.Id,
                    DateTime.Now.AddHours(-24)
                );

                // Обновляем UI
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProcessedOperations.Clear();
                    foreach (var p in processedOps)
                    {
                        // Добавляем стратегию и комментарий из Deals, если есть
                        var deal = Deals.FirstOrDefault(d => d.InstrumentUid == p.InstrumentUid &&
                                                              d.EntryTime == p.OpenDate);
                        if (deal != null)
                        {
                            if (string.IsNullOrEmpty(p.Strategy))
                                p.Strategy = deal.Strategy;
                            if (string.IsNullOrEmpty(p.Comment))
                                p.Comment = deal.Comment;
                        }

                        if (string.IsNullOrEmpty(p.Strategy))
                            p.Strategy = "Manual";

                        ProcessedOperations.Add(p);
                    }

                    UpdateTotalFromOperations();
                    _filteredProcessedOpsView?.Refresh();
                });

                // ✅ Обновляем P&L для открытых позиций
                await UpdateOpenPositionsPnLAsync();

                Debug.WriteLine($"MainViewModel: Таблица операций обновлена. Сделок: {processedOps.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainViewModel: Ошибка обновления операций: {ex.Message}");
            }
        }



        /// <summary>
        /// Обновление итоговых значений на основе отфильтрованных сделок
        /// </summary>
        private void UpdateTotalFromFilteredDeals()
        {
            try
            {
                // ✅ ИСПРАВЛЕНИЕ: Используем _filteredDealsView вместо создания нового
                if (_filteredDealsView == null) return;

                var filteredDeals = _filteredDealsView.Cast<Deal>().ToList();
                decimal totalPnL = 0;
                decimal totalPnLPercent = 0;

                foreach (var deal in filteredDeals)
                {
                    if ((deal.Status == DealStatus.Closed && deal.ClosedPnL.HasValue) ||
                        (deal.Status == DealStatus.Open && deal.ClosedPnL.HasValue))
                    {
                        totalPnL += deal.ClosedPnL.Value;
                        totalPnLPercent += deal.ClosedPnLPercent.Value;
                    }
                }

                TotalPnL = totalPnL;
                TotalPnLPercent = totalPnLPercent;
                TotalPnLColor = totalPnL >= 0 ? "DarkGreen" : "Red";

                
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: Ошибка обновления итогов: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработчик обновления сделок (вызывается из TinkoffApiService)
        /// </summary>
        private async void OnDealsUpdated()
        {
            // ✅ ПРОПУСКАЕМ если бэктест-режим
            if (_isBacktestMode)
            {
                //Debug.WriteLine("MainViewModel: OnDealsUpdated пропущен (бэктест-режим)");
                return;
            }

            await LoadDealsAsync();

            if (_operationHistoryService.IsInitialized())
            {
                await RefreshProcessedOperationsAsync();
                // ✅ Обновляем P&L для открытых позиций
                await UpdateOpenPositionsPnLAsync();
            }
        }

        // Выносим логику фильтрации в отдельный метод
        private bool FilterDeals(object item)
        {
            var deal = item as Deal;
            if (deal == null) return false;

            // Фильтр по статусу
            if (ShowOnlyOpenDeals && deal.Status != DealStatus.Open)
                return false;

            // Текстовый фильтр
            if (!string.IsNullOrWhiteSpace(DealsFilterText))
            {
                var search = DealsFilterText.ToLower();
                return (deal.Ticker?.ToLower().Contains(search) == true ||
                        deal.Strategy?.ToLower().Contains(search) == true ||
                        deal.Comment?.ToLower().Contains(search) == true ||
                        Convert.ToString(deal.Status)?.ToLower().Contains(search) == true ||
                        deal.Direction?.ToLower().Contains(search) == true);
            }

            return true;
        }

        partial void OnDealsFilterTextChanged(string value)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // ✅ ИСПРАВЛЕНИЕ: Обновляем фильтр, а не создаем новый
                _filteredDealsView?.Refresh();
                UpdateTotalFromFilteredDeals();
            });
        }
        partial void OnShowOnlyOpenDealsChanged(bool value)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // ✅ ИСПРАВЛЕНИЕ: Обновляем фильтр, а не создаем новый
                _filteredDealsView?.Refresh();
                UpdateTotalFromFilteredDeals();
            });
        }

        public async void NotifyDealsUpdated()
        {
            await LoadDealsAsync();

            // ✅ ИСПРАВЛЕНИЕ: Принудительно обновляем представление
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _filteredDealsView?.Refresh();
            });
        }

        /// <summary>
        /// Закрытие сделки вручную
        /// </summary>
        private async Task CloseDealAsync(Deal deal)
        {
            // ✅ ПРОВЕРКА НА NULL С БОЛЕЕ ПОДРОБНЫМ СООБЩЕНИЕМ
            if (deal == null)
            {
                // Проверяем, возможно SelectedDeal не установлен
                if (SelectedDeal == null)
                {
                    MessageBox.Show("Сделка не выбрана. Пожалуйста, выберите сделку в таблице.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    // Если параметр null, но SelectedDeal есть, используем его
                    deal = SelectedDeal;
                }

                if (deal == null)
                {
                    return;
                }
            }

            if (deal.Status != DealStatus.Open)
            {
                MessageBox.Show($"Сделка {deal.Ticker} уже закрыта. Статус: {deal.Status}",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Закрыть сделку {deal.Ticker} ({deal.Direction})?\n\n" +
                $"Цена входа: {deal.EntryPrice:F2}\n" +
                $"Количество: {deal.EntryQuantity}\n\n" +
                $"Текущая цена: {deal.CurrentPrice:F2}",
                "Подтверждение закрытия сделки",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;


            try
            {
                StatusMessage = $"Закрытие сделки {deal.Ticker}...";

                // Получаем текущую цену с проверкой
                decimal currentPrice = 0;
                if (deal.CurrentPrice > 0)
                {
                    currentPrice = (decimal)deal.CurrentPrice;
                }

                if (currentPrice <= 0)
                {
                    currentPrice = await _currentProvider.GetCurrentPriceAsync(deal.InstrumentUid);
                }


                // Проверка выбранного счета
                if (_selectedAccount == null)
                {
                    StatusMessage = "Ошибка: счет не выбран";
                    MessageBox.Show("Счет не выбран. Пожалуйста, выберите счет в главном окне.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }



                // Создаем ордер на закрытие
                var exitDirection = deal.Direction == "Buy" || deal.Direction == "Long" ? "Sell" : "Buy";

                var order = new Models.Order
                {
                    AccountId = _selectedAccount.Id,
                    InstrumentUid = deal.InstrumentUid,
                    Quantity = deal.EntryQuantity,
                    Direction = exitDirection,
                    OrderType = "market",
                    Price = currentPrice,
                    Time = DateTime.Now,
                    IsEntryOrder = false,
                    IsExitOrder = true,
                    ExitReason = "ЗАКРЫТА пользователем вручную"
                };

                var resultOrder = await _currentProvider.PlaceOrderAsync(order);

                if (resultOrder.IsSuccess)
                {
                    // Рассчитываем P&L
                    decimal priceDiff = 0;
                    decimal pnl = 0;
                    decimal pnlPercent = 0;

                    if (deal.Direction == "Buy" || deal.Direction == "Long")
                    {
                        priceDiff = currentPrice - deal.EntryPrice;
                    }
                    else
                    {
                        priceDiff = deal.EntryPrice - currentPrice;
                    }

                    // Получаем размер лота для инструмента
                    var instrument = Instruments?.FirstOrDefault(i => i.Uid == deal.InstrumentUid);
                    decimal lotSize = instrument?.LotSize ?? 1;

                    pnl = priceDiff * deal.EntryQuantity * lotSize;
                    pnlPercent = deal.EntryPrice > 0 ? priceDiff / deal.EntryPrice * 100 : 0;

                    // Закрываем сделку в БД
                    await CloseDealInDatabaseAsync(
                        deal.InstrumentUid,
                        deal.EntryOrderId,
                        DateTime.Now,
                        currentPrice,
                        resultOrder.OrderId,
                        pnl,
                        pnlPercent,
                        order.ExitReason);

                    StatusMessage = $"Сделка {deal.Ticker} успешно закрыта";
                    await LoadDealsAsync();

                    MessageBox.Show(
                        $"Сделка {deal.Ticker} закрыта успешно!\n\n" +
                        $"Результат: {(pnl >= 0 ? "✅ ПРИБЫЛЬ" : "❌ УБЫТОК")}\n" +
                        $"P&L: {pnl:F2} руб. ({pnlPercent:F2}%)",
                        "Закрытие сделки",
                        MessageBoxButton.OK,
                        pnl >= 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                else
                {
                    StatusMessage = $"Ошибка закрытия: {resultOrder.ErrorMessage}";
                    MessageBox.Show($"Ошибка закрытия сделки:\n{resultOrder.ErrorMessage}",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка закрытия сделки");
                StatusMessage = $"Ошибка: {ex.Message}";
                MessageBox.Show($"Ошибка закрытия сделки:\n{ex.Message}",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // <summary>
        /// Переворот сделки (закрыть текущую и открыть противоположную)
        /// </summary>
        private async Task FlipDealAsync(Deal deal)
        {
            // ✅ ПРОВЕРКА НА NULL С БОЛЕЕ ПОДРОБНЫМ СООБЩЕНИЕМ
            if (deal == null)
            {
                // Проверяем, возможно SelectedDeal не установлен
                if (SelectedDeal == null)
                {
                    MessageBox.Show("Сделка не выбрана. Пожалуйста, выберите сделку в таблице.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    // Если параметр null, но SelectedDeal есть, используем его
                    deal = SelectedDeal;
                }

                if (deal == null)
                {
                    return;
                }
            }

            if (deal.Status != DealStatus.Open)
            {
                MessageBox.Show($"Сделка {deal.Ticker} уже закрыта. Статус: {deal.Status}",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"ПЕРЕВЕРНУТЬ сделку {deal.Ticker} ({deal.Direction})?\n\n" +
                $"⚠️ ВНИМАНИЕ! Это действие:\n" +
                $"• Закроет текущую позицию\n" +
                $"• Откроет позицию в противоположном направлении\n" +
                $"• Размер позиции останется тем же\n\n" +
                $"Цена входа: {deal.EntryPrice:F2}\n" +
                $"Количество: {deal.EntryQuantity}\n" +
                $"Текущая цена: {deal.CurrentPrice:F2}",
                "Подтверждение переворота сделки",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                StatusMessage = $"Переворот сделки {deal.Ticker}...";

                // Получаем текущую цену
                decimal currentPrice = 0;
                if (deal.CurrentPrice > 0)
                {
                    currentPrice = (decimal)deal.CurrentPrice;
                }

                if (currentPrice <= 0)
                {
                    currentPrice = await _currentProvider.GetCurrentPriceAsync(deal.InstrumentUid);
                }

                // Проверка выбранного счета
                if (_selectedAccount == null)
                {
                    StatusMessage = "Ошибка: счет не выбран";
                    MessageBox.Show("Счет не выбран. Пожалуйста, выберите счет в главном окне.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 1. Закрываем текущую позицию
                var exitDirection = deal.Direction == "Buy" || deal.Direction == "Long" ? "Sell" : "Buy";

                var closeOrder = new Models.Order
                {
                    AccountId = _selectedAccount.Id,
                    InstrumentUid = deal.InstrumentUid,
                    Quantity = deal.EntryQuantity,
                    Direction = exitDirection,
                    OrderType = "market",
                    Price = currentPrice,
                    Time = DateTime.Now,
                    IsEntryOrder = false,
                    IsExitOrder = true,
                    ExitReason = "ПЕРЕВЕРНУТА пользователем вручную (закрытие)"
                };

                var closeResult = await _currentProvider.PlaceOrderAsync(closeOrder);

                if (!closeResult.IsSuccess)
                {
                    StatusMessage = $"Ошибка закрытия: {closeResult.ErrorMessage}";
                    MessageBox.Show($"Ошибка закрытия позиции:\n{closeResult.ErrorMessage}",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Рассчитываем P&L для закрытия
                decimal priceDiffClose = 0;
                decimal pnlClose = 0;
                decimal pnlPercentClose = 0;

                if (deal.Direction == "Buy" || deal.Direction == "Long")
                {
                    priceDiffClose = currentPrice - deal.EntryPrice;
                }
                else
                {
                    priceDiffClose = deal.EntryPrice - currentPrice;
                }

                // Получаем размер лота
                var instrument = Instruments?.FirstOrDefault(i => i.Uid == deal.InstrumentUid);
                decimal lotSize = instrument?.LotSize ?? 1;

                pnlClose = priceDiffClose * deal.EntryQuantity * lotSize;
                pnlPercentClose = deal.EntryPrice > 0 ? priceDiffClose / deal.EntryPrice * 100 : 0;

                // Закрываем сделку в БД
                await CloseDealInDatabaseAsync(
                    deal.InstrumentUid,
                    deal.EntryOrderId,
                    DateTime.Now,
                    currentPrice,
                    closeResult.OrderId,
                    pnlClose,
                    pnlPercentClose,
                    "ПЕРЕВЕРНУТА пользователем вручную (закрытие)");

                // Небольшая задержка между операциями
                await Task.Delay(500);

                // 2. Открываем позицию в противоположном направлении
                var newDirection = deal.Direction == "Buy" || deal.Direction == "Long" ? "Sell" : "Buy";

                var openOrder = new Models.Order
                {
                    AccountId = _selectedAccount.Id,
                    InstrumentUid = deal.InstrumentUid,
                    Quantity = deal.EntryQuantity,
                    Direction = newDirection,
                    OrderType = "market",
                    Price = currentPrice,
                    Time = DateTime.Now,
                    IsEntryOrder = true,
                    IsExitOrder = false,
                    EntryReason = "ПЕРЕВЕРНУТА пользователем вручную (открытие)"
                };

                var openResult = await _currentProvider.PlaceOrderAsync(openOrder);

                if (openResult.IsSuccess)
                {
                    // Добавляем новую сделку в БД
                    await AddDealToDatabaseAsync(
                        deal.Ticker,
                        deal.InstrumentUid,
                        "Manual",
                        DateTime.Now,
                        currentPrice,
                        deal.EntryQuantity,
                        openResult.OrderId,
                        newDirection,
                        "ПЕРЕВЕРНУТА пользователем вручную");

                    StatusMessage = $"Сделка {deal.Ticker} перевернута успешно";
                    await LoadDealsAsync();

                    MessageBox.Show(
                        $"Сделка {deal.Ticker} перевернута успешно!\n\n" +
                        $"📊 Результат закрытия: {(pnlClose >= 0 ? "✅ ПРИБЫЛЬ" : "❌ УБЫТОК")} {pnlClose:F2} руб. ({pnlPercentClose:F2}%)\n" +
                        $"🔄 Новая позиция: {newDirection} {deal.EntryQuantity} лотов по {currentPrice:F2}",
                        "Переворот сделки",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    StatusMessage = $"Ошибка открытия: {openResult.ErrorMessage}";
                    MessageBox.Show($"Позиция закрыта, но не удалось открыть новую:\n{openResult.ErrorMessage}\n\n" +
                                   $"Старая сделка закрыта, новая не создана.",
                                  "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка переворота сделки");
                StatusMessage = $"Ошибка: {ex.Message}";
                MessageBox.Show($"Ошибка переворота сделки:\n{ex.Message}",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        /// <summary>
        /// Закрытие сделки в базе данных
        /// </summary>
        private async Task CloseDealInDatabaseAsync(string instrumentUid, string entryOrderId, DateTime exitTime,
    decimal exitPrice, string exitOrderId, decimal pnl, decimal pnlPercent, string comment)
        {
            try
            {
                //var connection = DatabaseService.GetConnection();


                string dbPath = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = @"
            UPDATE DealsJournal 
            SET ExitTime = @exitTime,
                ExitPrice = @exitPrice,
                ExitOrderId = @exitOrderId,
                Status = @status,
                ClosedPnL = @pnl,
                ClosedPnLPercent = @pnlPercent,
                Comment = @comment,
                UpdatedAt = @updatedAt
            WHERE InstrumentUid = @instrumentUid AND EntryOrderId = @entryOrderId AND Status = 'Open'
        ";

                command.Parameters.AddWithValue("@exitTime", exitTime.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@exitPrice", exitPrice);
                command.Parameters.AddWithValue("@exitOrderId", exitOrderId);
                command.Parameters.AddWithValue("@status", "Closed");
                command.Parameters.AddWithValue("@pnl", pnl);
                command.Parameters.AddWithValue("@pnlPercent", pnlPercent);
                command.Parameters.AddWithValue("@comment", comment);
                command.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                command.Parameters.AddWithValue("@entryOrderId", entryOrderId);
                command.Parameters.AddWithValue("@updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                int rowsAffected = await command.ExecuteNonQueryAsync();
                Debug.WriteLine($"CloseDealInDatabaseAsync rows affected: {rowsAffected}");

                if (rowsAffected > 0)
                {
                    Debug.WriteLine($"Сделка по {instrumentUid} закрыта: P&L={pnl:F2} ({pnlPercent:F2}%)");
                    await Application.Current.Dispatcher.InvokeAsync(() => NotifyDealsUpdated());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка закрытия сделки в БД: {ex.Message}");
                throw;
            }
        }
        /// <summary>
        /// Добавление новой сделки в базу данных
        /// </summary>
        private async Task AddDealToDatabaseAsync(string ticker, string instrumentUid, string strategy,
    DateTime entryTime, decimal entryPrice, int quantity, string entryOrderId, string direction, string comment)
        {
            try
            {
                //var connection = DatabaseService.GetConnection();


                string dbPath = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = @"
            INSERT INTO DealsJournal (
                Ticker, InstrumentUid, Strategy, EntryTime, EntryPrice, EntryQuantity,
                EntryOrderId, Direction, Status, Comment, CreatedAt, UpdatedAt
            ) VALUES (
                @ticker, @instrumentUid, @strategy, @entryTime, @entryPrice, @entryQuantity,
                @entryOrderId, @direction, @status, @comment, @createdAt, @updatedAt
            )
        ";

                command.Parameters.AddWithValue("@ticker", ticker);
                command.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                command.Parameters.AddWithValue("@strategy", strategy);
                command.Parameters.AddWithValue("@entryTime", entryTime.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@entryPrice", entryPrice);
                command.Parameters.AddWithValue("@entryQuantity", quantity);
                command.Parameters.AddWithValue("@entryOrderId", entryOrderId);
                command.Parameters.AddWithValue("@direction", direction);
                command.Parameters.AddWithValue("@status", "Open");
                command.Parameters.AddWithValue("@comment", comment ?? "");
                command.Parameters.AddWithValue("@createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                await command.ExecuteNonQueryAsync();
                Debug.WriteLine($"Сделка добавлена: {ticker} {direction} {quantity} лотов");
                await Application.Current.Dispatcher.InvokeAsync(() => NotifyDealsUpdated());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка добавления сделки в БД: {ex.Message}");
                throw;
            }
        }






        #endregion

        #region Системные команды

        /// <summary>
        /// Очистка базы данных
        /// </summary>
        private async Task ClearDatabaseAsync()
        {
            if (IsConnected)
            {
                MessageBox.Show("Очистка БД возможна только в отключенном состоянии!",
                               "Предупреждение",
                               MessageBoxButton.OK,
                               MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                "Вы уверены, что хотите очистить всю базу данных?\n\n" +
                "Будут удалены:\n" +
                "• Все исторические данные свечей\n" +
                "• Все сделки из журнала\n" +
                "• Метаданные таблиц\n\n" +
                "Это действие необратимо!",
                "Подтверждение очистки БД",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                StatusMessage = "Очистка базы данных...";

                string dbPath = System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory,
                    "market_dataMG5.db");

                if (!System.IO.File.Exists(dbPath))
                {
                    StatusMessage = "Файл базы данных не найден";
                    return;
                }

                // Создаем бэкап перед очисткой
                string backupPath = $"{dbPath}.backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                System.IO.File.Copy(dbPath, backupPath);
                Debug.WriteLine($"Создан бэкап БД: {backupPath}");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                // Получаем список всех таблиц
                var tables = new List<string>();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }

                // Очищаем каждую таблицу
                foreach (var table in tables)
                {
                    try
                    {
                        var clearCommand = connection.CreateCommand();
                        clearCommand.CommandText = $"DELETE FROM {table};";
                        clearCommand.CommandText += $"DELETE FROM sqlite_sequence WHERE name='{table}';"; // Сброс автоинкремента
                        await clearCommand.ExecuteNonQueryAsync();
                        Debug.WriteLine($"Очищена таблица: {table}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка очистки таблицы {table}: {ex.Message}");
                    }
                }

                // Очищаем коллекции в памяти
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Deals.Clear();
                    Positions.Clear();
                    Instruments.Clear();
                    // Очищаем другие коллекции если нужно
                });

                StatusMessage = $"База данных успешно очищена. Создан бэкап: {System.IO.Path.GetFileName(backupPath)}";

                MessageBox.Show(
                    $"База данных успешно очищена!\n\nСоздан бэкап: {System.IO.Path.GetFileName(backupPath)}",
                    "Очистка завершена",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка очистки базы данных");
                StatusMessage = $"Ошибка очистки БД: {ex.Message}";

                MessageBox.Show(
                    $"Ошибка при очистке базы данных:\n\n{ex.Message}",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Выход из приложения
        /// </summary>
        private void ExitApplication()
        {
            var result = MessageBox.Show(
                "Вы уверены, что хотите выйти?",
                "Подтверждение выхода",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                Application.Current.Shutdown();
            }
        }

        /// <summary>
        /// Информация о программе
        /// </summary>
        private void ShowAbout()
        {
            string version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName()?.Version?.ToString() ?? "1.0.0";

            MessageBox.Show(
                $"MoneyGenerator v5\n\n" +
                $"Версия: {version}\n" +
                $"© 2026 Все права защищены\n\n" +
                $"Торговый терминал с поддержкой автоматических стратегий\n" +
                $"и журнала сделок.",
                "О программе",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        #endregion


        #region "ЭКВИТИ"
        /// <summary>
        /// Метод сохранения\обновления баланса в БД - эквити
        /// </summary>
        private async Task SaveBalanceToEquityAsync(string accountId, decimal balance)
        {
            try
            {
                // Получаем имя провайдера из самого провайдера (универсально!)
                string provider = _currentProvider?.ProviderName ?? SelectedDataSource?.Name ?? "Unknown";
                string accountType = _isSandbox ? "Sandbox" : "Real";

                // Сохраняем запись в БД
                await EquityService.SaveRecordAsync(provider, accountId, accountType, balance);
                //Debug.WriteLine($"Сохранен баланс: {provider}/{accountType} - {balance:F2}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения баланса в Equity: {ex.Message}");
            }
        }



        /// <summary>
        /// открытие графика эквити
        /// </summary>
        private async void ShowEquityChart()
        {
            try
            {
                if (SelectedAccount == null)
                {
                    MessageBox.Show("Выберите счет для отображения графика эквити",
                                  "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Получаем имя провайдера из текущего провайдера
                string provider = _currentProvider?.ProviderName ?? SelectedDataSource?.Name ?? "Unknown";
                string accountId = SelectedAccount.Id;

                Debug.WriteLine($"Запрос графика эквити: провайдер={provider}, счет={accountId}, тип={(IsSandbox ? "Sandbox" : "Real")}");

                // Загружаем историю эквити (0 = вся история)
                var equityHistory = await EquityService.GetHistoryAsync(provider, accountId, 0);

                if (!equityHistory.Any())
                {
                    MessageBox.Show($"Нет данных для отображения графика эквити.\n\n" +
                                   $"Провайдер: {provider}\n" +
                                   $"Счет: {SelectedAccount.DisplayName}\n" +
                                   $"Тип: {(IsSandbox ? "Sandbox" : "Real")}\n\n" +
                                   $"Данные начнут накапливаться после обновления баланса.\n" +
                                   $"Попробуйте подождать несколько минут.",
                                  "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Открываем окно с графиком, передавая провайдера и ID счета
                var equityWindow = new EquityChartWindow(equityHistory, SelectedAccount.DisplayName, provider, accountId);
                equityWindow.Owner = Application.Current.MainWindow;
                equityWindow.Show();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка открытия графика эквити: {ex.Message}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        #endregion

        /// <summary>
        /// Закрытие позиции вручную
        /// </summary>
        private async Task ClosePositionAsync(Models.Position position)
        {
            if (position == null)
            {
                if (SelectedPosition == null)
                {
                    MessageBox.Show("Позиция не выбрана. Пожалуйста, выберите позицию в таблице.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                position = SelectedPosition;
            }

            var result = MessageBox.Show(
                $"Закрыть позицию {position.Ticker}?\n\n" +
                $"Тикер: {position.Ticker}\n" +
                $"Количество: {position.Quantity}\n" +
                $"Текущая цена: {position.CurrentPrice:F2}\n" +
                $"P&L: {position.PnL:F2}",
                "Подтверждение закрытия позиции",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;


            

            try
            {
                StatusMessage = $"Закрытие позиции {position.Ticker}...";

                Debug.WriteLine($"DEBUG - ClosePositionAsync - Закрытие позиции {position.Ticker}...");

                // Проверка выбранного счета
                if (_selectedAccount == null)
                {
                    StatusMessage = "Ошибка: счет не выбран";
                    MessageBox.Show("Счет не выбран. Пожалуйста, выберите счет в главном окне.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }


                Debug.WriteLine($"DEBUG - ClosePositionAsync - position.Direction {position.Direction}...");


                string exitDirection = "";


                if (position.Quantity > 0)
                {
                    exitDirection = "Sell";
                }
                else if (position.Quantity < 0)
                {
                    exitDirection = "Buy";
                }



                // Определяем направление для закрытия
               // var exitDirection = position.Direction == "Buy" || position.Direction == "Long" ? "Sell" : "Buy";


                Debug.WriteLine($"DEBUG - ClosePositionAsync - exitDirection {exitDirection}...");


                var order = new Models.Order
                {
                    AccountId = _selectedAccount.Id,
                    InstrumentUid = position.InstrumentUid,
                    Quantity = position.Quantity,
                    Direction = exitDirection,
                    OrderType = "market",
                    Price = (decimal)position.CurrentPrice,
                    Time = DateTime.Now,
                    IsEntryOrder = false,
                    IsExitOrder = true,
                    ExitReason = "ЗАКРЫТА пользователем вручную"
                };

                Debug.WriteLine($"DEBUG - ClosePositionAsync - order.Direction = {order.Direction}  IsEntryOrder={order.IsEntryOrder}   IsExitOrder={order.IsExitOrder}...");

                var resultOrder = await _currentProvider.PlaceOrderAsync(order);

                if (resultOrder.IsSuccess)
                {
                    StatusMessage = $"Позиция {position.Ticker} успешно закрыта";

                    // Обновляем позиции после закрытия
                    await Task.Delay(500);
                    await LoadDealsAsync(); // Обновляем журнал сделок

                    MessageBox.Show(
                        $"Позиция {position.Ticker} закрыта успешно!\n\n" +
                        $"Результат: {(position.PnL >= 0 ? "✅ ПРИБЫЛЬ" : "❌ УБЫТОК")}\n" +
                        $"P&L: {position.PnL:F2} руб.",
                        "Закрытие позиции",
                        MessageBoxButton.OK,
                        position.PnL >= 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                else
                {
                    StatusMessage = $"Ошибка закрытия: {resultOrder.ErrorMessage}";
                    MessageBox.Show($"Ошибка закрытия позиции:\n{resultOrder.ErrorMessage}",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка закрытия позиции");
                StatusMessage = $"Ошибка: {ex.Message}";
                MessageBox.Show($"Ошибка закрытия позиции:\n{ex.Message}",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Переворот позиции
        /// </summary>
        private async Task FlipPositionAsync(Models.Position position)
        {
            if (position == null)
            {
                if (SelectedPosition == null)
                {
                    MessageBox.Show("Позиция не выбрана. Пожалуйста, выберите позицию в таблице.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                position = SelectedPosition;
            }

            var result = MessageBox.Show(
                $"ПЕРЕВЕРНУТЬ позицию {position.Ticker} ({position.Direction})?\n\n" +
                $"⚠️ ВНИМАНИЕ! Это действие:\n" +
                $"• Закроет текущую позицию\n" +
                $"• Откроет позицию в противоположном направлении\n" +
                $"• Размер позиции останется тем же\n\n" +
                $"Количество: {position.Quantity}\n" +
                $"Текущая цена: {position.CurrentPrice:F2}\n" +
                $"P&L: {position.PnL:F2}",
                "Подтверждение переворота позиции",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                StatusMessage = $"Переворот позиции {position.Ticker}...";

                // Проверка выбранного счета
                if (_selectedAccount == null)
                {
                    StatusMessage = "Ошибка: счет не выбран";
                    MessageBox.Show("Счет не выбран. Пожалуйста, выберите счет в главном окне.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 1. Закрываем текущую позицию
                var exitDirection = position.Direction == "Buy" || position.Direction == "Long" ? "Sell" : "Buy";

                var closeOrder = new Models.Order
                {
                    AccountId = _selectedAccount.Id,
                    InstrumentUid = position.InstrumentUid,
                    Quantity = position.Quantity,
                    Direction = exitDirection,
                    OrderType = "market",
                    Price = (decimal)position.CurrentPrice,
                    Time = DateTime.Now,
                    IsEntryOrder = false,
                    IsExitOrder = true,
                    ExitReason = "ПЕРЕВЕРНУТА пользователем вручную (закрытие)"
                };

                var closeResult = await _currentProvider.PlaceOrderAsync(closeOrder);

                if (!closeResult.IsSuccess)
                {
                    StatusMessage = $"Ошибка закрытия: {closeResult.ErrorMessage}";
                    MessageBox.Show($"Ошибка закрытия позиции:\n{closeResult.ErrorMessage}",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Небольшая задержка между операциями
                await Task.Delay(500);

                // 2. Открываем позицию в противоположном направлении
                var newDirection = position.Direction == "Buy" || position.Direction == "Long" ? "Sell" : "Buy";

                var openOrder = new Models.Order
                {
                    AccountId = _selectedAccount.Id,
                    InstrumentUid = position.InstrumentUid,
                    Quantity = position.Quantity,
                    Direction = newDirection,
                    OrderType = "market",
                    Price = (decimal)position.CurrentPrice,
                    Time = DateTime.Now,
                    IsEntryOrder = true,
                    IsExitOrder = false,
                    EntryReason = "ПЕРЕВЕРНУТА пользователем вручную (открытие)"
                };

                var openResult = await _currentProvider.PlaceOrderAsync(openOrder);

                if (openResult.IsSuccess)
                {
                    StatusMessage = $"Позиция {position.Ticker} перевернута успешно";

                    // Обновляем данные
                    await Task.Delay(500);
                    await LoadDealsAsync();

                    MessageBox.Show(
                        $"Позиция {position.Ticker} перевернута успешно!\n\n" +
                        $"🔄 Новая позиция: {newDirection} {position.Quantity} лотов по {position.CurrentPrice:F2}",
                        "Переворот позиции",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    StatusMessage = $"Ошибка открытия: {openResult.ErrorMessage}";
                    MessageBox.Show($"Позиция закрыта, но не удалось открыть новую:\n{openResult.ErrorMessage}\n\n" +
                                   $"Старая позиция закрыта, новая не создана.",
                                  "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка переворота позиции");
                StatusMessage = $"Ошибка: {ex.Message}";
                MessageBox.Show($"Ошибка переворота позиции:\n{ex.Message}",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }







        #region Загрузка сохраненных стратегий

        /// <summary>
        /// Сохранение параметров запущенной стратегии в БД
        /// </summary>
        /*private async Task SaveStrategyParametersAsync(StrategyViewModel strategyVM)
        {
            try
            {
                if (strategyVM == null) return;

                string parametersJson = "";

                // Получаем параметры в зависимости от типа стратегии
                switch (strategyVM.SelectedStrategy.Type)
                {
                    case "RSI":
                        if (strategyVM.RsiStrategy != null)
                        {
                            var rsiParams = strategyVM.RsiStrategy.Parameters;
                            parametersJson = JsonSerializer.Serialize(new
                            {
                                rsiParams.RsiPeriod,
                                rsiParams.RsiOverbought,
                                rsiParams.RsiOversold,
                                rsiParams.StochPeriod,
                                rsiParams.StochOverbought,
                                rsiParams.StochOversold,
                                rsiParams.StochSmoothK,
                                rsiParams.StochSmoothD,
                                rsiParams.OscillatorType,
                                rsiParams.EntryOrderType,
                                rsiParams.EntrySlippage,
                                rsiParams.ExitOrderType,
                                rsiParams.ExitSlippage,
                                rsiParams.CloseOnSignalReversal,
                                rsiParams.OrderSizePercent,
                                rsiParams.AtrMultiplier,
                                rsiParams.MovingTPEntryCalculationType,
                                rsiParams.MovingTPEntryTargetPercent,
                                rsiParams.MovingTPEntrySlippage,
                                rsiParams.MovingTPEntryTimeoutMinutes,
                                rsiParams.MovingTPExitCalculationType,
                                rsiParams.MovingTPExitStartPercent,
                                rsiParams.MovingTPExitSlippage,
                                rsiParams.MovingTPExitTimeoutMinutes,
                                rsiParams.TrailingStopExitCalculationType,
                                rsiParams.TrailingStopExitDistancePercent,
                                rsiParams.TrailingStopExitSlippage,
                                rsiParams.TrailingStopExitActivationPercent,
                                rsiParams.TakeProfitCalculationType,
                                rsiParams.TakeProfitPercent,
                                rsiParams.TakeProfitActivationPrice,
                                rsiParams.TakeProfitSlippage,
                                rsiParams.StopLossCalculationType,
                                rsiParams.StopLossPercent,
                                rsiParams.StopLossActivationPrice,
                                rsiParams.StopLossSlippage
                            });
                        }
                        break;

                    case "MA":
                        if (strategyVM.MaStrategy != null)
                        {
                            var maParams = strategyVM.MaStrategy.Parameters;
                            parametersJson = JsonSerializer.Serialize(new
                            {
                                maParams.SmaPeriods,
                                maParams.EmaPeriods,
                                maParams.PositionSizeType,
                                maParams.PositionSizePercent,
                                maParams.PositionSizeAbsolute
                            });
                        }
                        break;

                    case "Manual":
                        parametersJson = JsonSerializer.Serialize(new { type = "Manual" });
                        break;

                    case "Rating":
                        if (strategyVM.RatingStrategy != null)
                        {
                            var ratingParams = strategyVM.RatingStrategy.Parameters;
                            parametersJson = JsonSerializer.Serialize(new
                            {
                                ratingParams.TrendPeriods,
                                ratingParams.OscillatorPeriods,
                                ratingParams.VolumePeriods,
                                ratingParams.EntryThreshold,
                                ratingParams.MatchTolerance,
                                ratingParams.MinMatchPercentage,
                                ratingParams.PositionSizeType,
                                ratingParams.PositionSizePercent,
                                ratingParams.PositionSizeAbsolute
                            });
                        }
                        break;
                }

                await SavedStrategiesService.SaveStrategyAsync(
                    strategyVM.SelectedStrategy.Type,
                    strategyVM.Instrument.Uid,
                    strategyVM.Instrument.Ticker,
                    strategyVM.Instrument.Name,
                    strategyVM.SelectedTimeFrame.Value,
                    parametersJson,
                    true
                );

                Debug.WriteLine($"Стратегия {strategyVM.SelectedStrategy.Type} для {strategyVM.Instrument.Ticker} сохранена в БД");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения стратегии: {ex.Message}");
            }
        }*/
        /// <summary>
        /// Сохранение параметров запущенной стратегии в БД
        /// </summary>
        private async Task SaveStrategyParametersAsync(StrategyViewModel strategyVM)
        {
            try
            {
                if (strategyVM == null || strategyVM.SelectedStrategy.Type == "Manual") return;  //  не сохрааняем мануальную стратегию

                // ✅ Создаем словарь для всех параметров
                var parameters = new Dictionary<string, object>();

                // === ГЛОБАЛЬНЫЕ ПАРАМЕТРЫ (ВАЖНО!) ===
                //parameters["UseGlobalStopLoss"] = strategyVM.UseGlobalStopLoss;
                //parameters["GlobalStopLossPercent"] = strategyVM.GlobalStopLossValue;
                parameters["UseGlobalTakeProfit"] = strategyVM.UseGlobalTakeProfit;
                parameters["GlobalTakeProfitPercent"] = strategyVM.GlobalTakeProfitValue;

                // === ОБЩИЕ ПАРАМЕТРЫ СТРАТЕГИИ ===
                parameters["Capital"] = Capital;
                parameters["MaxConcurrentTrades"] = MaxConcurrentTrades;
                parameters["LotSize"] = LotSize;
                parameters["MaxRiskPercent"] = MaxRiskPercent;
                parameters["UseTrailingStop"] = UseTrailingStop;
                parameters["TrailingStopPercent"] = TrailingStopPercent;

                // Получаем параметры в зависимости от типа стратегии
                switch (strategyVM.SelectedStrategy.Type)
                {
                    case "RSI":
                        if (strategyVM.RsiStrategy != null)
                        {
                            var rsiParams = strategyVM.RsiStrategy.Parameters;
                            parameters["RsiPeriod"] = rsiParams.RsiPeriod;
                            parameters["RsiOverbought"] = rsiParams.RsiOverbought;
                            parameters["RsiOversold"] = rsiParams.RsiOversold;
                            parameters["StochPeriod"] = rsiParams.StochPeriod;
                            parameters["StochOverbought"] = rsiParams.StochOverbought;
                            parameters["StochOversold"] = rsiParams.StochOversold;
                            parameters["StochSmoothK"] = rsiParams.StochSmoothK;
                            parameters["StochSmoothD"] = rsiParams.StochSmoothD;
                            parameters["OscillatorType"] = (int)rsiParams.OscillatorType;
                            parameters["EntryOrderType"] = (int)rsiParams.EntryOrderType;
                            parameters["EntrySlippage"] = rsiParams.EntrySlippage;
                            parameters["ExitOrderType"] = (int)rsiParams.ExitOrderType;
                            parameters["ExitSlippage"] = rsiParams.ExitSlippage;
                            parameters["CloseOnSignalReversal"] = rsiParams.CloseOnSignalReversal;
                            parameters["OrderSizePercent"] = rsiParams.OrderSizePercent;
                            parameters["AtrMultiplier"] = rsiParams.AtrMultiplier;
                            parameters["MovingTPEntryCalculationType"] = (int)rsiParams.MovingTPEntryCalculationType;
                            parameters["MovingTPEntryTargetPercent"] = rsiParams.MovingTPEntryTargetPercent;
                            parameters["MovingTPEntrySlippage"] = rsiParams.MovingTPEntrySlippage;
                            parameters["MovingTPEntryTimeoutMinutes"] = rsiParams.MovingTPEntryTimeoutMinutes;
                            parameters["MovingTPExitCalculationType"] = (int)rsiParams.MovingTPExitCalculationType;
                            parameters["MovingTPExitStartPercent"] = rsiParams.MovingTPExitStartPercent;
                            parameters["MovingTPExitSlippage"] = rsiParams.MovingTPExitSlippage;
                            parameters["MovingTPExitTimeoutMinutes"] = rsiParams.MovingTPExitTimeoutMinutes;
                            parameters["TrailingStopExitCalculationType"] = (int)rsiParams.TrailingStopExitCalculationType;
                            parameters["TrailingStopExitDistancePercent"] = rsiParams.TrailingStopExitDistancePercent;
                            parameters["TrailingStopExitSlippage"] = rsiParams.TrailingStopExitSlippage;
                            parameters["TrailingStopExitActivationPercent"] = rsiParams.TrailingStopExitActivationPercent;
                            parameters["TakeProfitCalculationType"] = (int)rsiParams.TakeProfitCalculationType;
                            parameters["TakeProfitPercent"] = rsiParams.TakeProfitPercent;
                            parameters["TakeProfitActivationPrice"] = rsiParams.TakeProfitActivationPrice;
                            parameters["TakeProfitSlippage"] = rsiParams.TakeProfitSlippage;
                            parameters["StopLossCalculationType"] = (int)rsiParams.StopLossCalculationType;
                            parameters["StopLossPercent"] = rsiParams.StopLossPercent;
                            parameters["StopLossActivationPrice"] = rsiParams.StopLossActivationPrice;
                            parameters["StopLossSlippage"] = rsiParams.StopLossSlippage;
                        }
                        break;

                    case "MA":
                        if (strategyVM.MaStrategy != null)
                        {
                            var maParams = strategyVM.MaStrategy.Parameters;
                            parameters["SmaPeriods"] = maParams.SmaPeriods;
                            parameters["EmaPeriods"] = maParams.EmaPeriods;
                            parameters["PositionSizeType"] = maParams.PositionSizeType;
                            parameters["PositionSizePercent"] = maParams.PositionSizePercent;
                            parameters["PositionSizeAbsolute"] = maParams.PositionSizeAbsolute;
                        }
                        break;

                    case "Manual":
                        parameters["type"] = "Manual";
                        break;

                    case "Rating":
                        if (strategyVM.RatingStrategy != null)
                        {
                            var ratingParams = strategyVM.RatingStrategy.Parameters;
                            parameters["TrendPeriods"] = ratingParams.TrendPeriods;
                            parameters["OscillatorPeriods"] = ratingParams.OscillatorPeriods;
                            parameters["VolumePeriods"] = ratingParams.VolumePeriods;
                            parameters["EntryThreshold"] = ratingParams.EntryThreshold;
                            parameters["MatchTolerance"] = ratingParams.MatchTolerance;
                            parameters["MinMatchPercentage"] = ratingParams.MinMatchPercentage;
                            parameters["PositionSizeType"] = ratingParams.PositionSizeType;
                            parameters["PositionSizePercent"] = ratingParams.PositionSizePercent;
                            parameters["PositionSizeAbsolute"] = ratingParams.PositionSizeAbsolute;
                        }
                        break;
                }

                var parametersJson = JsonSerializer.Serialize(parameters);

                // ✅ ОТЛАДОЧНЫЙ ВЫВОД
                Debug.WriteLine($"=== Сохранение стратегии {strategyVM.SelectedStrategy.Type} ===");
                ///Debug.WriteLine($"  UseGlobalStopLoss={strategyVM.UseGlobalStopLoss}");
                //Debug.WriteLine($"  GlobalStopLossPercent={strategyVM.GlobalStopLossValue}%");
                Debug.WriteLine($"  UseGlobalTakeProfit={strategyVM.UseGlobalTakeProfit}");
                Debug.WriteLine($"  GlobalTakeProfitPercent={strategyVM.GlobalTakeProfitValue}%");
                Debug.WriteLine($"  JSON: {parametersJson}");

                await SavedStrategiesService.SaveStrategyAsync(
                    strategyVM.SelectedStrategy.Type,
                    strategyVM.Instrument.Uid,
                    strategyVM.Instrument.Ticker,
                    strategyVM.Instrument.Name,
                    strategyVM.SelectedTimeFrame.Value,
                    parametersJson,
                    true
                );

                Debug.WriteLine($"Стратегия {strategyVM.SelectedStrategy.Type} для {strategyVM.Instrument.Ticker} сохранена в БД");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения стратегии: {ex.Message}");
            }
        }

        /// <summary>
        /// Сохранить стратегию с текущими параметрами (вызывается при запуске)
        /// </summary>
        private async Task<int> SaveCurrentStrategyWithParametersAsync()
        {
            try
            {
                if (SelectedInstrument == null)
                {
                    Debug.WriteLine("Не выбран инструмент для сохранения стратегии");
                    return -1;
                }

                if (string.IsNullOrEmpty(SelectedStrategyType))
                {
                    Debug.WriteLine("Не выбран тип стратегии для сохранения");
                    return -1;
                }

                // ✅ Собираем ВСЕ текущие параметры из UI
                var parameters = new Dictionary<string, object>();

                // === ОБЩИЕ ПАРАМЕТРЫ ===
                parameters["Capital"] = Capital;
                parameters["MaxConcurrentTrades"] = MaxConcurrentTrades;

                // Глобальные стоп-лосс и тейк-профит
                parameters["UseGlobalStopLoss"] = UseGlobalStopLoss;
                parameters["GlobalStopLossPercent"] = GlobalStopLossPercent;
                parameters["UseGlobalTakeProfit"] = UseGlobalTakeProfit;
                parameters["GlobalTakeProfitPercent"] = GlobalTakeProfitPercent;

                // Параметры управления рисками
                parameters["LotSize"] = LotSize;
                parameters["MaxRiskPercent"] = MaxRiskPercent;
                parameters["UseTrailingStop"] = UseTrailingStop;
                parameters["TrailingStopPercent"] = TrailingStopPercent;

                // === ПАРАМЕТРЫ В ЗАВИСИМОСТИ ОТ ТИПА СТРАТЕГИИ ===
                switch (SelectedStrategyType)
                {
                    case "SMA Cross":
                        parameters["FastPeriod"] = SmaFastPeriod;
                        parameters["SlowPeriod"] = SmaSlowPeriod;
                        break;

                    case "RSI":
                        parameters["RsiPeriod"] = RsiPeriod;
                        parameters["RsiOversold"] = RsiOversold;
                        parameters["RsiOverbought"] = RsiOverbought;
                        break;

                    case "MACD":
                        parameters["MacdFastPeriod"] = MacdFastPeriod;
                        parameters["MacdSlowPeriod"] = MacdSlowPeriod;
                        parameters["MacdSignalPeriod"] = MacdSignalPeriod;
                        break;

                    case "Bollinger Bands":
                        parameters["BbPeriod"] = BbPeriod;
                        parameters["BbStdDev"] = BbStdDev;
                        break;

                    case "Volume Strategy":
                        parameters["VolumeThreshold"] = VolumeThreshold;
                        parameters["MinVolumeRatio"] = MinVolumeRatio;
                        break;
                }

                // Сериализуем параметры
                var parametersJson = JsonSerializer.Serialize(parameters);

                // Сохраняем стратегию
                var strategyId = await SavedStrategiesService.SaveStrategyAsync(
                    SelectedStrategyType,
                    SelectedInstrument.Uid,
                    SelectedInstrument.Ticker,
                    SelectedInstrument.Name,
                    SelectedTimeframe,
                    parametersJson,
                    true); // IsAutoStart = true (так как запускаем)

                if (strategyId > 0)
                {
                    Debug.WriteLine($"Стратегия успешно сохранена с ID: {strategyId}");
                    Debug.WriteLine($"Сохраненные параметры: UseGlobalStopLoss={UseGlobalStopLoss}, " +
                                  $"GlobalStopLossPercent={GlobalStopLossPercent}%, " +
                                  $"UseGlobalTakeProfit={UseGlobalTakeProfit}, " +
                                  $"GlobalTakeProfitPercent={GlobalTakeProfitPercent}%");
                }

                return strategyId;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка сохранения стратегии при запуске");
                return -1;
            }
        }

        private async Task ExecuteStrategyAsync()
        {
            try
            {
                // Проверки перед запуском
                if (SelectedInstrument == null)
                {
                    await ShowMessageAsync("Выберите инструмент", "Ошибка");
                    return;
                }

                if (string.IsNullOrEmpty(SelectedStrategyType))
                {
                    await ShowMessageAsync("Выберите тип стратегии", "Ошибка");
                    return;
                }

                if (!IsConnected)
                {
                    await ShowMessageAsync("Нет подключения к бирже. Сначала подключитесь.", "Ошибка");
                    return;
                }

                // Проверка, не запущена ли уже стратегия для этого инструмента
                if (IsStrategyRunning)
                {
                   // await ShowMessageAsync("Стратегия уже запущена", "Предупреждение");
                    return;
                }

                IsStrategyRunning = true;
                LoadingMessage = "Запуск стратегии...";

                // ✅ СОХРАНЯЕМ СТРАТЕГИЮ С ТЕКУЩИМИ ПАРАМЕТРАМИ
                var savedStrategyId = await SaveCurrentStrategyWithParametersAsync();

                if (savedStrategyId > 0)
                {
                    Debug.WriteLine($"Стратегия сохранена перед запуском (ID: {savedStrategyId})");

                    // Можно показать уведомление о сохранении
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var result = MessageBox.Show(
                            $"Стратегия сохранена с текущими параметрами.\n\n" +
                            $"Глобальный стоп-лосс: {(UseGlobalStopLoss ? $"Включен ({GlobalStopLossPercent}%)" : "Выключен")}\n" +
                            $"Глобальный тейк-профит: {(UseGlobalTakeProfit ? $"Включен ({GlobalTakeProfitPercent}%)" : "Выключен")}\n\n" +
                            $"Запустить стратегию?",
                            "Стратегия сохранена",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result != MessageBoxResult.Yes)
                        {
                            IsStrategyRunning = false;
                            LoadingMessage = "";
                            return;
                        }
                    });
                }

                // Здесь код фактического запуска стратегии
                await StartStrategyExecutionAsync();

                LoadingMessage = "Стратегия запущена";
                Debug.WriteLine($"Стратегия {SelectedStrategyType} запущена с параметрами:");
                Debug.WriteLine($"- Капитал: {Capital}");
                Debug.WriteLine($"- Использовать глобальный стоп-лосс: {UseGlobalStopLoss}");
                if (UseGlobalStopLoss)
                    Debug.WriteLine($"- Глобальный стоп-лосс: {GlobalStopLossPercent}%");
                Debug.WriteLine($"- Использовать глобальный тейк-профит: {UseGlobalTakeProfit}");
                if (UseGlobalTakeProfit)
                    Debug.WriteLine($"- Глобальный тейк-профит: {GlobalTakeProfitPercent}%");
                Debug.WriteLine($"- Размер лота: {LotSize}");
                Debug.WriteLine($"- Максимальный риск: {MaxRiskPercent}%");
                Debug.WriteLine($"- Использовать трейлинг-стоп: {UseTrailingStop}");
                if (UseTrailingStop)
                    Debug.WriteLine($"- Трейлинг-стоп: {TrailingStopPercent}%");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка запуска стратегии");
                await ShowMessageAsync($"Ошибка запуска: {ex.Message}", "Ошибка", MessageBoxImage.Error);
                IsStrategyRunning = false;
                LoadingMessage = "";
            }
        }
        /// <summary>
        /// Запуск стратегии, загруженной из сохраненных
        /// </summary>
        public async void StartLoadedStrategy(SavedStrategyInfo strategy)
        {
            try
            {
                if (!IsConnected)
                {
                    await ShowMessageAsync("Нет подключения к бирже", "Ошибка");
                    return;
                }

                if (IsStrategyRunning)
                {
                    //await ShowMessageAsync("Стратегия уже запущена", "Предупреждение");
                    return;
                }

                IsStrategyRunning = true;
                LoadingMessage = $"Запуск стратегии {strategy.DisplayName}...";

                // Здесь код фактического запуска стратегии с уже загруженными параметрами
                await StartStrategyExecutionAsync();

                LoadingMessage = $"Стратегия {strategy.DisplayName} запущена";
                Debug.WriteLine($"Запущена стратегия {strategy.DisplayName}");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Ошибка запуска загруженной стратегии {strategy.DisplayName}");
                await ShowMessageAsync($"Ошибка запуска: {ex.Message}", "Ошибка", MessageBoxImage.Error);
                IsStrategyRunning = false;
                LoadingMessage = "";
            }
        }
        /// <summary>
        /// Показать сообщение пользователю
        /// </summary>
        private async Task ShowMessageAsync(string message, string title, MessageBoxImage icon = MessageBoxImage.Information)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, icon);
            });
        }

        /// <summary>
        /// Запуск стратегии (основной метод)
        /// </summary>
        private async Task StartStrategyExecutionAsync()
        {
            try
            {
                Debug.WriteLine($"Запуск стратегии {SelectedStrategyType}...");

                // Здесь ваша логика запуска стратегии
                // Например, открытие окна стратегии с переданными параметрами

                // Если нужно открыть окно стратегии с текущими параметрами
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Находим стратегию по типу
                    var strategy = Strategies.FirstOrDefault(s => s.Type == SelectedStrategyType);
                    if (strategy != null && SelectedInstrument != null && SelectedTimeframe != null)
                    {
                        var timeframe = TimeFrames.FirstOrDefault(t => t.Value == SelectedTimeframe);

                        var strategyVM = new StrategyViewModel(
                            strategy,
                            SelectedInstrument,
                            timeframe,
                            _selectedAccount,
                            _currentProvider,
                            _connectionManager,
                            null);

                        var strategyWindow = new StrategyWindow
                        {
                            DataContext = strategyVM,
                            Owner = Application.Current.MainWindow
                        };

                        // Подписываемся на событие запуска для сохранения параметров
                        strategyVM.StrategyStarted += async () =>
                        {
                            await SaveStrategyParametersAsync(strategyVM);
                        };

                        strategyWindow.Show();
                    }
                });

                await Task.Delay(100); // Имитация работы
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка при запуске стратегии");
                throw;
            }
        }

        /// <summary>
        /// Загрузка сохраненных стратегий из БД
        /// </summary>
        private async Task LoadSavedStrategiesAsync()
        {
            try
            {
                // Проверяем подключение
                if (!IsConnected)
                {
                    MessageBox.Show("Нет подключения к бирже. Сначала подключитесь.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                StatusMessage = "Загрузка сохраненных стратегий...";

                // Создаем ViewModel для загрузки с передачей this
                var loadViewModel = new LoadSavedStrategiesViewModel(
                    _providerFactory,
                    _tokenManager,
                    _connectionManager,
                    _logger,
                    this); // Передаем текущий MainViewModel

                // Подписываемся на событие загрузки стратегии
                loadViewModel.StrategyLoadRequested += async (strategyInfo) =>
                {
                    return await LoadStrategyFromInfo(strategyInfo);
                };

                var loadWindow = new LoadSavedStrategiesWindow(loadViewModel);
                loadWindow.Owner = Application.Current.MainWindow;
                loadWindow.ShowDialog();

                StatusMessage = "Готово";
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ошибка открытия окна загрузки стратегий");
                StatusMessage = $"Ошибка: {ex.Message}";
                MessageBox.Show($"Ошибка открытия окна загрузки: {ex.Message}",
                               "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Загрузка стратегии из сохраненной информации
        /// </summary>
        private async Task<bool> LoadStrategyFromInfo(SavedStrategyInfo strategyInfo)
        {
            try
            {
                // Находим инструмент в коллекции
                var instrument = Instruments.FirstOrDefault(i => i.Uid == strategyInfo.InstrumentUid);

                if (instrument == null)
                {
                    MessageBox.Show($"Инструмент {strategyInfo.InstrumentTicker} не найден в списке доступных.\n" +
                                   "Возможно, список инструментов не загружен или инструмент недоступен.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Находим стратегию
                var strategy = Strategies.FirstOrDefault(s => s.Type == strategyInfo.StrategyType);

                if (strategy == null)
                {
                    MessageBox.Show($"Стратегия {strategyInfo.StrategyType} не найдена.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Находим таймфрейм
                var timeframe = TimeFrames.FirstOrDefault(t => t.Value == strategyInfo.Timeframe);

                if (timeframe == null)
                {
                    MessageBox.Show($"Таймфрейм {strategyInfo.Timeframe} не найден.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // ✅ Создаем логгер правильного типа
                ILogger<StrategyViewModel> strategyLogger = null;

                // Если есть фабрика логгеров
                if (App.ServiceProvider?.GetService<ILoggerFactory>() is ILoggerFactory loggerFactory)
                {
                    strategyLogger = loggerFactory.CreateLogger<StrategyViewModel>();
                }
                // Или создаем пустой логгер
                else
                {
                    strategyLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<StrategyViewModel>.Instance;
                }


                //_mainViewModel = new MainViewModel();
                //Debug.WriteLine($"DEBUG---------------------------=====----==--==--- {_mainViewModel.SelectedAccount.Name}     {_mainViewModel.SelectedAccount.Id} {_mainViewModel.SelectedAccount.DisplayBalance}   {_mainViewModel.SelectedAccount.DisplayName}");

                var strategyVM = new StrategyViewModel(
                    strategy,
                    instrument,
                    timeframe,
                    _selectedAccount,
                    _currentProvider,
                    _connectionManager,
                    strategyLogger);

                // Восстанавливаем параметры из JSON
                await RestoreStrategyParameters(strategyVM, strategyInfo);

                // Подписываемся на событие запуска для сохранения параметров
                strategyVM.StrategyStarted += async () =>
                {
                    await SaveStrategyParametersAsync(strategyVM);
                };

                var strategyWindow = new StrategyWindow
                {
                    DataContext = strategyVM,
                    Owner = Application.Current.MainWindow
                };

                strategyWindow.Closed += async (s, e) =>
                {
                    try
                    {
                        await strategyVM.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка при закрытии окна стратегии: {ex.Message}");
                    }
                };

                strategyWindow.Show();

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Ошибка загрузки стратегии {strategyInfo.DisplayName}");
                return false;
            }
        }

        
        /// <summary>
        /// Восстановление параметров стратегии из JSON
        /// </summary>
        private async Task RestoreStrategyParameters(StrategyViewModel strategyVM, SavedStrategyInfo strategyInfo)
        {
            try
            {
                if (string.IsNullOrEmpty(strategyInfo.ParametersJson)) return;

                using var jsonDoc = JsonDocument.Parse(strategyInfo.ParametersJson);
                var root = jsonDoc.RootElement;

                // ✅ ВОССТАНАВЛИВАЕМ ГЛОБАЛЬНЫЕ ПАРАМЕТРЫ В MainViewModel
                // В методе RestoreStrategyParameters, добавьте отладку:
                if (root.TryGetProperty("UseGlobalStopLoss", out var useGlobalStopLoss))
                {
                    UseGlobalStopLoss = useGlobalStopLoss.GetBoolean();
                    Debug.WriteLine($"  Restored UseGlobalStopLoss: {UseGlobalStopLoss}");
                }
                else
                {
                    Debug.WriteLine($"  Property 'UseGlobalStopLoss' not found in JSON");
                }

                if (root.TryGetProperty("GlobalStopLossPercent", out var globalStopLossPercent))
                {
                    GlobalStopLossPercent = globalStopLossPercent.GetDecimal();
                    Debug.WriteLine($"  Restored GlobalStopLossPercent: {GlobalStopLossPercent}%");
                }
                else
                {
                    Debug.WriteLine($"  Property 'GlobalStopLossPercent' not found in JSON");
                }

                if (root.TryGetProperty("UseGlobalTakeProfit", out var useGlobalTakeProfit))
                    UseGlobalTakeProfit = useGlobalTakeProfit.GetBoolean();

                if (root.TryGetProperty("GlobalTakeProfitPercent", out var globalTakeProfitPercent))
                    GlobalTakeProfitPercent = globalTakeProfitPercent.GetDecimal();

                if (root.TryGetProperty("Capital", out var capital))
                    Capital = capital.GetDecimal();

                if (root.TryGetProperty("MaxConcurrentTrades", out var maxTrades))
                    MaxConcurrentTrades = maxTrades.GetInt32();

                if (root.TryGetProperty("LotSize", out var lotSize))
                    LotSize = lotSize.GetInt32();

                if (root.TryGetProperty("MaxRiskPercent", out var maxRisk))
                    MaxRiskPercent = maxRisk.GetDecimal();

                if (root.TryGetProperty("UseTrailingStop", out var useTrailing))
                    UseTrailingStop = useTrailing.GetBoolean();

                if (root.TryGetProperty("TrailingStopPercent", out var trailingPercent))
                    TrailingStopPercent = trailingPercent.GetDecimal();

                // ✅ Сохраняем параметры в SavedStrategyInfo для отображения в окне загрузки
                strategyInfo.UseGlobalStopLoss = UseGlobalStopLoss;
                strategyInfo.GlobalStopLossPercent = GlobalStopLossPercent;
                strategyInfo.UseGlobalTakeProfit = UseGlobalTakeProfit;
                strategyInfo.GlobalTakeProfitPercent = GlobalTakeProfitPercent;
                strategyInfo.Capital = Capital;
                strategyInfo.MaxConcurrentTrades = MaxConcurrentTrades;
                strategyInfo.LotSize = LotSize;
                strategyInfo.MaxRiskPercent = MaxRiskPercent;
                strategyInfo.UseTrailingStop = UseTrailingStop;
                strategyInfo.TrailingStopPercent = TrailingStopPercent;











                // ✅ Восстанавливаем параметры стратегии в зависимости от типа
                switch (strategyInfo.StrategyType)
                {
                    case "RSI":
                        if (strategyVM.RsiStrategy != null)
                        {
                            var rsiParams = strategyVM.RsiStrategy.Parameters;

                            if (root.TryGetProperty("RsiPeriod", out var rsiPeriod))
                                rsiParams.RsiPeriod = rsiPeriod.GetInt32();
                            if (root.TryGetProperty("RsiOverbought", out var rsiOverbought))
                                rsiParams.RsiOverbought = rsiOverbought.GetDecimal();
                            if (root.TryGetProperty("RsiOversold", out var rsiOversold))
                                rsiParams.RsiOversold = rsiOversold.GetDecimal();
                            if (root.TryGetProperty("StochPeriod", out var stochPeriod))
                                rsiParams.StochPeriod = stochPeriod.GetInt32();
                            if (root.TryGetProperty("StochOverbought", out var stochOverbought))
                                rsiParams.StochOverbought = stochOverbought.GetDecimal();
                            if (root.TryGetProperty("StochOversold", out var stochOversold))
                                rsiParams.StochOversold = stochOversold.GetDecimal();
                            if (root.TryGetProperty("StochSmoothK", out var stochSmoothK))
                                rsiParams.StochSmoothK = stochSmoothK.GetInt32();
                            if (root.TryGetProperty("StochSmoothD", out var stochSmoothD))
                                rsiParams.StochSmoothD = stochSmoothD.GetInt32();
                            if (root.TryGetProperty("OscillatorType", out var oscillatorType))
                                rsiParams.OscillatorType = (OscillatorType)oscillatorType.GetInt32();
                            if (root.TryGetProperty("EntryOrderType", out var entryOrderType))
                                rsiParams.EntryOrderType = (StrategyOrderType)entryOrderType.GetInt32();
                            if (root.TryGetProperty("EntrySlippage", out var entrySlippage))
                                rsiParams.EntrySlippage = entrySlippage.GetDecimal();
                            if (root.TryGetProperty("ExitOrderType", out var exitOrderType))
                                rsiParams.ExitOrderType = (StrategyOrderType)exitOrderType.GetInt32();
                            if (root.TryGetProperty("ExitSlippage", out var exitSlippage))
                                rsiParams.ExitSlippage = exitSlippage.GetDecimal();
                            if (root.TryGetProperty("CloseOnSignalReversal", out var closeOnSignal))
                                rsiParams.CloseOnSignalReversal = closeOnSignal.GetBoolean();
                            if (root.TryGetProperty("OrderSizePercent", out var orderSize))
                                rsiParams.OrderSizePercent = orderSize.GetDecimal();
                            if (root.TryGetProperty("AtrMultiplier", out var atrMultiplier))
                                rsiParams.AtrMultiplier = atrMultiplier.GetDecimal();

                            // Параметры скользящего TP на входе
                            if (root.TryGetProperty("MovingTPEntryCalculationType", out var movingTPEntryCalc))
                                rsiParams.MovingTPEntryCalculationType = (PriceCalculationType)movingTPEntryCalc.GetInt32();
                            if (root.TryGetProperty("MovingTPEntryTargetPercent", out var movingTPEntryPercent))
                                rsiParams.MovingTPEntryTargetPercent = movingTPEntryPercent.GetDecimal();
                            if (root.TryGetProperty("MovingTPEntrySlippage", out var movingTPEntrySlippage))
                                rsiParams.MovingTPEntrySlippage = movingTPEntrySlippage.GetDecimal();
                            if (root.TryGetProperty("MovingTPEntryTimeoutMinutes", out var movingTPEntryTimeout))
                                rsiParams.MovingTPEntryTimeoutMinutes = movingTPEntryTimeout.GetInt32();

                            // Параметры скользящего TP на выходе
                            if (root.TryGetProperty("MovingTPExitCalculationType", out var movingTPExitCalc))
                                rsiParams.MovingTPExitCalculationType = (PriceCalculationType)movingTPExitCalc.GetInt32();
                            if (root.TryGetProperty("MovingTPExitStartPercent", out var movingTPExitPercent))
                                rsiParams.MovingTPExitStartPercent = movingTPExitPercent.GetDecimal();
                            if (root.TryGetProperty("MovingTPExitSlippage", out var movingTPExitSlippage))
                                rsiParams.MovingTPExitSlippage = movingTPExitSlippage.GetDecimal();
                            if (root.TryGetProperty("MovingTPExitTimeoutMinutes", out var movingTPExitTimeout))
                                rsiParams.MovingTPExitTimeoutMinutes = movingTPExitTimeout.GetInt32();

                            // Параметры трейлинг-стопа
                            if (root.TryGetProperty("TrailingStopExitCalculationType", out var trailingCalc))
                                rsiParams.TrailingStopExitCalculationType = (PriceCalculationType)trailingCalc.GetInt32();
                            if (root.TryGetProperty("TrailingStopExitDistancePercent", out var trailingDistance))
                                rsiParams.TrailingStopExitDistancePercent = trailingDistance.GetDecimal();
                            if (root.TryGetProperty("TrailingStopExitSlippage", out var trailingSlippage))
                                rsiParams.TrailingStopExitSlippage = trailingSlippage.GetDecimal();
                            if (root.TryGetProperty("TrailingStopExitActivationPercent", out var trailingActivation))
                                rsiParams.TrailingStopExitActivationPercent = trailingActivation.GetDecimal();

                            // Параметры тейк-профита и стоп-лосса
                            if (root.TryGetProperty("TakeProfitCalculationType", out var tpCalc))
                                rsiParams.TakeProfitCalculationType = (PriceCalculationType)tpCalc.GetInt32();
                            if (root.TryGetProperty("TakeProfitPercent", out var tpPercent))
                                rsiParams.TakeProfitPercent = tpPercent.GetDecimal();
                            if (root.TryGetProperty("TakeProfitActivationPrice", out var tpActivation))
                                rsiParams.TakeProfitActivationPrice = tpActivation.GetDecimal();
                            if (root.TryGetProperty("TakeProfitSlippage", out var tpSlippage))
                                rsiParams.TakeProfitSlippage = tpSlippage.GetDecimal();
                            if (root.TryGetProperty("StopLossCalculationType", out var slCalc))
                                rsiParams.StopLossCalculationType = (PriceCalculationType)slCalc.GetInt32();
                            if (root.TryGetProperty("StopLossPercent", out var slPercent))
                                rsiParams.StopLossPercent = slPercent.GetDecimal();
                            if (root.TryGetProperty("StopLossActivationPrice", out var slActivation))
                                rsiParams.StopLossActivationPrice = slActivation.GetDecimal();
                            if (root.TryGetProperty("StopLossSlippage", out var slSlippage))
                                rsiParams.StopLossSlippage = slSlippage.GetDecimal();

                            rsiParams.ApplyParameters();

                            // ✅ ОБНОВЛЯЕМ UI СТРАТЕГИИ
                            /*if (rsiParams is INotifyPropertyChanged notify)
                            {
                                // Вызываем обновление всех свойств
                                var properties = rsiParams.GetType().GetProperties();
                                foreach (var prop in properties)
                                {
                                    notify.PropertyChanged?.Invoke(rsiParams, new PropertyChangedEventArgs(prop.Name));
                                }
                            }*/


                            // ✅ ОБНОВЛЯЕМ UI ЧЕРЕЗ StrategyViewModel
                            strategyVM.RaisePropertyChanged(nameof(strategyVM.StrategySettingsControl));
                            strategyVM.RaisePropertyChanged(nameof(strategyVM.StrategyControlView));

                            Debug.WriteLine($"RSI параметры восстановлены: Period={rsiParams.RsiPeriod}, Overbought={rsiParams.RsiOverbought}, Oversold={rsiParams.RsiOversold}");
                        }
                        break;

                    case "MA":
                        if (strategyVM.MaStrategy != null)
                        {
                            var maParams = strategyVM.MaStrategy.Parameters;

                            if (root.TryGetProperty("SmaPeriods", out var smaPeriods))
                                maParams.SmaPeriods = smaPeriods.GetString();
                            if (root.TryGetProperty("EmaPeriods", out var emaPeriods))
                                maParams.EmaPeriods = emaPeriods.GetString();
                            if (root.TryGetProperty("PositionSizeType", out var posSizeType))
                                maParams.PositionSizeType = posSizeType.GetString();
                            if (root.TryGetProperty("PositionSizePercent", out var posSizePercent))
                                maParams.PositionSizePercent = posSizePercent.GetDecimal();
                            if (root.TryGetProperty("PositionSizeAbsolute", out var posSizeAbsolute))
                                maParams.PositionSizeAbsolute = posSizeAbsolute.GetDecimal();

                            maParams.ApplyParameters();
                        }
                        break;

                    case "Manual":
                        // Для ручной стратегии нет специальных параметров
                        break;

                    case "Rating":
                        if (strategyVM.RatingStrategy != null)
                        {
                            var ratingParams = strategyVM.RatingStrategy.Parameters;

                            if (root.TryGetProperty("TrendPeriods", out var trendPeriods))
                                ratingParams.TrendPeriods = trendPeriods.GetString();
                            if (root.TryGetProperty("OscillatorPeriods", out var oscPeriods))
                                ratingParams.OscillatorPeriods = oscPeriods.GetString();
                            if (root.TryGetProperty("VolumePeriods", out var volPeriods))
                                ratingParams.VolumePeriods = volPeriods.GetString();
                            if (root.TryGetProperty("EntryThreshold", out var entryThreshold))
                                ratingParams.EntryThreshold = entryThreshold.GetInt32();
                            if (root.TryGetProperty("MatchTolerance", out var matchTolerance))
                                ratingParams.MatchTolerance = matchTolerance.GetDecimal();
                            if (root.TryGetProperty("MinMatchPercentage", out var minMatch))
                                ratingParams.MinMatchPercentage = minMatch.GetInt32();
                            if (root.TryGetProperty("PositionSizeType", out var posType))
                                ratingParams.PositionSizeType = posType.GetString();
                            if (root.TryGetProperty("PositionSizePercent", out var posPercent))
                                ratingParams.PositionSizePercent = posPercent.GetDecimal();
                            if (root.TryGetProperty("PositionSizeAbsolute", out var posAbsolute))
                                ratingParams.PositionSizeAbsolute = posAbsolute.GetDecimal();

                            ratingParams.ApplyParameters();
                        }
                        break;
                }

                // ✅ ОБНОВЛЯЕМ ГЛОБАЛЬНЫЕ ПАРАМЕТРЫ В STRATEGYVIEWMODEL
                //strategyVM.UpdateGlobalParameters(UseGlobalStopLoss, GlobalStopLossPercent, UseGlobalTakeProfit, GlobalTakeProfitPercent);

                // ✅ ПРИНУДИТЕЛЬНО ОБНОВЛЯЕМ UI В STRATEGYVIEWMODEL
                strategyVM.RaisePropertyChanged(nameof(strategyVM.StrategySettingsControl));
                strategyVM.RaisePropertyChanged(nameof(strategyVM.StrategyControlView));

                Debug.WriteLine($"Параметры стратегии {strategyInfo.StrategyType} успешно восстановлены из БД");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка восстановления параметров стратегии: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление глобальных параметров из MainViewModel
        /// </summary>
        public void UpdateGlobalParameters(bool useStopLoss, decimal stopLossPercent, bool useTakeProfit, decimal takeProfitPercent)
        {
            // Обновляем свойства в MainViewModel (они уже есть)
            UseGlobalStopLoss = useStopLoss;
            GlobalStopLossPercent = stopLossPercent;  // ✅ Правильное имя свойства
            UseGlobalTakeProfit = useTakeProfit;
            GlobalTakeProfitPercent = takeProfitPercent;  // ✅ Правильное имя свойства

            Debug.WriteLine($"MainViewModel: Global parameters updated - StopLoss={useStopLoss}({stopLossPercent}%), TakeProfit={useTakeProfit}({takeProfitPercent}%)");
        }
        #endregion



        #region История операций  ПОКА ОТКЛЮЧИЛ!!!!!!!!!!!!!!!!!!
        //ПОКА ОТКЛЮЧИЛ!!!!!!!!!!!!!!!!!!
        // Новые методы:   ПОКА ОТКЛЮЧИЛ!!!!!!!!!!!!!!!!!!
        
        
        // ✅ ИЗМЕНЕНИЕ: Метод OnPortfolioChanged() теперь не перезагружает всю историю
        private async Task OnPortfolioChanged()
        {
            // ✅ ПРОПУСКАЕМ если бэктест-режим
            if (_isBacktestMode)
            {
                //Debug.WriteLine("MainViewModel: OnPortfolioChanged пропущен (бэктест-режим)");
                return;
            }

            // Обновляем только новые операции, не перезагружая всю историю
            if (_operationHistoryService.IsInitialized() && IsConnected && SelectedAccount != null)
            {
                // Проверяем, не происходит ли сейчас инициализация
                await _operationHistoryService.UpdateHistoryAsync(
                    _currentProvider,
                    SelectedAccount.Id,
                    DateTime.Now.AddHours(-24)
                );

                Debug.WriteLine("MainViewModel: Обновление портфеля, обновляем операции...");
                await RefreshProcessedOperationsAsync();

            }
        }

        /// <summary>
        /// Принудительное обновление операций (вызывается извне при добавлении новой сделки)
        /// </summary>
        public async Task ForceRefreshOperationsAsync()
        {
            await RefreshProcessedOperationsAsync();
        }


        /// <summary>
        /// Вызывается при открытии или закрытии сделки стратегией
        /// </summary>
        public async Task OnDealChangedAsync()
        {
            if (_operationHistoryService.IsInitialized() && IsConnected)
            {
                await RefreshProcessedOperationsAsync();
            }
        }

        /// <summary>
        /// Фоновая инициализация истории операций
        /// </summary>
        private async Task InitializeOperationsHistoryAsync()
        {
            try
            {
                if (SelectedAccount == null || _currentProvider == null)
                    return;

                if (_operationHistoryService.IsInitialized())
                {
                    Debug.WriteLine("MainViewModel: История операций уже инициализирована");
                    // ✅ ДОБАВИТЬ: Если уже инициализирована, все равно загружаем данные
                    await LoadProcessedOperationsAsync();
                    return;
                }

                var endDate = DateTime.Now;
                var currentPositions = Positions?.ToList() ?? new List<Models.Position>();

                Debug.WriteLine("MainViewModel: Запуск фоновой инициализации истории операций...");

                await _operationHistoryService.InitializeHistoryAsync(
                    _currentProvider,
                    SelectedAccount.Id,
                    currentPositions,
                    endDate,
                    initialDays: 100000,   // 30
                    maxDays: 100000         //730
                );

                // ✅ ИЗМЕНИТЬ: Вызываем RefreshProcessedOperationsAsync вместо LoadProcessedOperationsAsync
                await RefreshProcessedOperationsAsync();

                Debug.WriteLine("MainViewModel: Инициализация истории операций завершена");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainViewModel: Ошибка инициализации истории: {ex.Message}");
                _logger.LogError(ex, "Ошибка инициализации истории операций");
            }
        }

        /// <summary>
        /// Загрузка обработанных операций из БД
        /// </summary>
        public async Task LoadProcessedOperationsAsync()
        {
            try
            {
                var from = DateTime.Now.AddDays(-730);
                var to = DateTime.Now;

                var ops = await _operationHistoryService.LoadOperationsAsync(from, to);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProcessedOperations.Clear();

                    if (ops.Any())
                    {
                        // Группируем операции по сделкам через OperationProcessingService
                        var groupedOps = _operationProcessingService.ProcessOperationsAsync(ops).Result;

                        foreach (var p in groupedOps)
                        {
                            // ✅ Добавляем стратегию и комментарий из Deals, если есть
                            var deal = Deals.FirstOrDefault(d => d.InstrumentUid == p.InstrumentUid &&
                                                                  d.EntryTime == p.OpenDate);
                            if (deal != null)
                            {
                                if (string.IsNullOrEmpty(p.Strategy))
                                    p.Strategy = deal.Strategy;
                                if (string.IsNullOrEmpty(p.Comment))
                                    p.Comment = deal.Comment;
                            }

                            // ✅ Если стратегия все еще пустая, устанавливаем "Manual"
                            if (string.IsNullOrEmpty(p.Strategy))
                                p.Strategy = "Manual";

                            ProcessedOperations.Add(p);
                        }
                    }

                    // Обновляем итоги
                    UpdateTotalFromOperations();

                    // Обновляем представление
                    _filteredProcessedOpsView?.Refresh();

                    Debug.WriteLine($"Загружено {ProcessedOperations.Count} операций");
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadProcessedOperationsAsync error: {ex.Message}");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProcessedOperationsStatus = $"Ошибка: {ex.Message}";
                });
            }
        }




        /// <summary>
        /// Загрузка истории операций с автоматической балансировкой до совпадения с текущими позициями
        /// </summary>
        private async Task LoadOperationsHistoryAsync()
        {
            if (!IsConnected || SelectedAccount == null) return;

            try
            {
                OperationsStatus = "Загрузка истории операций...";
                ProcessedOperationsStatus = "Загрузка...";

                var endDate = DateTime.Now;

                // Получаем текущие позиции
                var currentPositions = Positions.ToList();
                Debug.WriteLine($"Текущих позиций: {currentPositions.Count}");

                // Вызываем метод с циклической догрузкой
                var result = await _operationHistoryService.LoadHistoryWithAutoBalanceAsync(
                    _currentProvider,
                    SelectedAccount.Id,
                    currentPositions,
                    endDate,
                    initialDays: 30,
                    maxDays: 730
                );

                // Обновляем UI
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Operations.Clear();
                    foreach (var op in result.Operations)
                    {
                        Operations.Add(op);
                    }

                    ProcessedOperations.Clear();
                    foreach (var procOp in result.ProcessedOps)
                    {
                        ProcessedOperations.Add(procOp);
                    }

                    var closedOps = result.ProcessedOps.Where(o => o.Status == "Closed").ToList();
                    var openOps = result.ProcessedOps.Where(o => o.Status.Contains("Open")).ToList();

                    TotalProcessedNetProfit = closedOps.Sum(o => o.NetProfit);
                    TotalProcessedNetProfitColor = TotalProcessedNetProfit >= 0 ? "DarkGreen" : "Red";
                    TotalProcessedGrossProfit = closedOps.Sum(o => o.GrossProfit);
                    TotalProcessedGrossProfitColor = TotalProcessedGrossProfit >= 0 ? "DarkGreen" : "Red";

                    OperationsStatus = $"Операций: {Operations.Count} | Сделок: {ProcessedOperations.Count} (закрыто: {closedOps.Count}, открыто: {openOps.Count})";
                    ProcessedOperationsStatus = $"Сделок: {ProcessedOperations.Count} (закрыто: {closedOps.Count}, открыто: {openOps.Count})";

                    if (result.IsBalanced)
                    {
                        ProcessedOperationsStatus += " ✅ Баланс сошелся";
                    }
                    else
                    {
                        ProcessedOperationsStatus += " ⚠️ Баланс не полный (попробуйте увеличить период)";
                    }

                    _filteredProcessedOpsView?.Refresh();
                });

                Debug.WriteLine($"Загрузка истории завершена. Баланс достигнут: {result.IsBalanced}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки истории операций");
                OperationsStatus = $"Ошибка: {ex.Message}";
                ProcessedOperationsStatus = $"Ошибка: {ex.Message}";
            }
        }

        /// <summary>
        /// Загружает операции за расширенный период для балансировки позиций
        /// </summary>
        public async Task<List<Models.Operation>> LoadExtendedOperationsAsync(string accountId, string ticker, DateTime fromDate, int extendedDays = 60)
        {
            var operations = new List<Models.Operation>();

            try
            {
                // Рассчитываем расширенный период (на 60 дней раньше)
                var extendedFrom = fromDate.AddDays(-extendedDays);
                var to = DateTime.Now;

                Debug.WriteLine($"Загрузка расширенной истории для {ticker} с {extendedFrom:yyyy-MM-dd} по {to:yyyy-MM-dd}");

                // Получаем операции из API
                var ops = await _currentProvider.GetOperationsHistoryAsync(accountId, extendedFrom, to);

                // Фильтруем по тикеру
                var tickerOps = ops
                    .Where(o => o.Ticker == ticker && (o.OperationType == "BUY" || o.OperationType == "SELL"))
                    .OrderBy(o => o.Date)
                    .ToList();

                // Вычисляем позицию до начальной даты
                decimal positionBeforeStart = 0;
                var beforeStartOps = tickerOps.Where(o => o.Date < fromDate).ToList();

                foreach (var op in beforeStartOps)
                {
                    if (op.OperationType == "BUY")
                        positionBeforeStart += op.Quantity;
                    else if (op.OperationType == "SELL")
                        positionBeforeStart -= Math.Abs(op.Quantity);
                }

                Debug.WriteLine($"Позиция до {fromDate:yyyy-MM-dd} для {ticker}: {positionBeforeStart:F0}");

                // Если позиция не нулевая, добавляем недостающие операции
                if (Math.Abs(positionBeforeStart) > 0.01m)
                {
                    // Находим операции, которые балансируют позицию
                    // Ищем одну операцию, которая создает текущую позицию
                    var balancingOps = new List<Models.Operation>();
                    decimal remainingPosition = positionBeforeStart;

                    // Ищем операции в обратном порядке (самые свежие перед стартовой датой)
                    foreach (var op in beforeStartOps.OrderByDescending(o => o.Date))
                    {
                        if (Math.Abs(remainingPosition) < 0.01m) break;

                        var qty = Math.Abs(op.Quantity);
                        var opType = op.OperationType;

                        // Проверяем, подходит ли операция для балансировки
                        if ((remainingPosition > 0 && opType == "BUY") ||
                            (remainingPosition < 0 && opType == "SELL"))
                        {
                            var addQty = Math.Min(qty, Math.Abs(remainingPosition));
                            var newOp = new Models.Operation
                            {
                                Id = $"{op.Id}_balance",
                                Ticker = op.Ticker,
                                InstrumentUid = op.InstrumentUid,
                                Date = op.Date,
                                Price = op.Price,
                                Quantity = opType == "BUY" ? addQty : -addQty,
                                OperationType = opType,
                                Payment = op.Payment * (addQty / qty),
                                Commission = op.Commission * (addQty / qty),
                                OperationTypeName = op.OperationTypeName,
                                State = op.State,
                                Currency = op.Currency
                            };
                            balancingOps.Add(newOp);
                            remainingPosition -= opType == "BUY" ? addQty : -addQty;

                            Debug.WriteLine($"Добавлена балансирующая операция для {ticker}: {opType} {addQty:F0} по {op.Price:F2}");
                        }
                    }

                    // Сохраняем балансирующие операции в БД
                    if (balancingOps.Any())
                    {
                        await _operationHistoryService.SaveOperationsAsync(balancingOps);
                        operations.AddRange(balancingOps);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки расширенной истории для {ticker}: {ex.Message}");
            }

            return operations;
        }


        private bool FilterProcessedOps(object item)
        {
            if (item is ProcessedOperation op)
            {
                // Фильтр по статусу (открытые/закрытые)
                if (ShowOnlyOpenOperations && !op.Status.Contains("Open"))
                    return false;

                // Текстовый поиск
                if (!string.IsNullOrWhiteSpace(OperationsSearchText))
                {
                    var search = OperationsSearchText.ToLower();
                    return (op.Ticker?.ToLower().Contains(search) == true ||
                            op.Strategy?.ToLower().Contains(search) == true ||
                            op.Comment?.ToLower().Contains(search) == true ||
                            op.StatusDisplay?.ToLower().Contains(search) == true ||
                            op.DisplayDirection?.ToLower().Contains(search) == true);
                }

                return true;
            }
            return false;
        }

        // Обработчики изменения фильтров
        partial void OnOperationsSearchTextChanged(string value)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _filteredProcessedOpsView?.Refresh();
                UpdateTotalFromOperations();
            });
        }

        partial void OnShowOnlyOpenOperationsChanged(bool value)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _filteredProcessedOpsView?.Refresh();
                UpdateTotalFromOperations();
            });
        }


        /// <summary>
        /// Обновление итоговых значений на основе отфильтрованных операций
        /// </summary>
        private void UpdateTotalFromOperations()
        {
            try
            {
                if (_filteredProcessedOpsView == null) return;

                var filteredOps = _filteredProcessedOpsView.Cast<ProcessedOperation>().ToList();

                // ✅ Все сделки для итогов (и закрытые, и открытые)
                decimal totalNetProfit = 0;
                decimal totalGrossProfit = 0;

                foreach (var op in filteredOps)
                {
                    totalNetProfit += op.NetProfit;
                    totalGrossProfit += op.GrossProfit;
                }

                TotalProcessedNetProfit = totalNetProfit;
                TotalProcessedNetProfitColor = totalNetProfit >= 0 ? "DarkGreen" : "Red";
                TotalProcessedGrossProfit = totalGrossProfit;
                TotalProcessedGrossProfitColor = totalGrossProfit >= 0 ? "DarkGreen" : "Red";

                // Обновляем статус
                var closedOps = filteredOps.Where(o => o.Status == "Closed").ToList();
                var openOps = filteredOps.Where(o => o.Status.Contains("Open")).ToList();
                ProcessedOperationsStatus = $"Сделок: {filteredOps.Count} (закрыто: {closedOps.Count}, открыто: {openOps.Count})";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обновления итогов: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет P&L для открытых позиций в таблице операций
        /// </summary>
        public async Task UpdateOpenPositionsPnLAsync()
        {
            // ✅ ПРОПУСКАЕМ если бэктест-режим
            if (_isBacktestMode)
            {
                Debug.WriteLine("MainViewModel: UpdateOpenPositionsPnLAsync пропущен (бэктест-режим)");
                return;
            }

            try
            {
                if (!IsConnected || _currentProvider == null)
                    return;

                // ✅ ИСПРАВЛЕНИЕ: Безопасная фильтрация с проверкой на null
                var openPositions = ProcessedOperations
                    .Where(o => o != null && !string.IsNullOrEmpty(o.Status) && o.Status.Contains("Open"))
                    .ToList();

                if (!openPositions.Any())
                    return;

                foreach (var op in openPositions)
                {
                    try
                    {
                        // Проверка на null перед использованием
                        if (op == null || string.IsNullOrEmpty(op.InstrumentUid))
                            continue;

                        decimal currentPrice = await _currentProvider.GetCurrentPriceAsync(op.InstrumentUid);
                        if (currentPrice <= 0)
                            continue;

                        // Обновляем CurrentPrice в объекте
                        op.CurrentPrice = currentPrice;

                        // Рассчитываем GrossProfit
                        if (op.Direction == "Long")
                        {
                            op.GrossProfit = (currentPrice - op.OpenPrice) * op.Quantity;
                        }
                        else // Short
                        {
                            op.GrossProfit = (op.OpenPrice - currentPrice) * op.Quantity;
                        }

                        // Рассчитываем NetProfit
                        op.NetProfit = op.GrossProfit + op.TotalFee;
                        op.NetProfitPercent = op.OpenPrice > 0 ? (op.NetProfit / (op.OpenPrice * op.Quantity)) * 100 : 0;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка обновления P&L для {op.Ticker}: {ex.Message}");
                    }
                }

                // Обновляем представление
                _filteredProcessedOpsView?.Refresh();
                UpdateTotalFromOperations();

                Debug.WriteLine($"Обновлена P&L для {openPositions.Count} открытых позиций");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обновления P&L открытых позиций: {ex.Message}");
            }
        }


        partial void OnShowOnlyOpenProcessedOpsChanged(bool value)
        {
            _filteredProcessedOpsView?.Refresh();
        }


        // Метод диагностики:
        private async Task DiagnoseAsync()
        {
            try
            {
                StatusMessage = "Запуск диагностики...";

                var service = new OperationProcessingService();

                // Диагностика конкретного тикера
                await service.DiagnoseTickerAsync("SBER");
                await service.DiagnoseTickerAsync("GAZP");
                await service.DiagnoseTickerAsync("VTBR");
                await service.DiagnoseTickerAsync("TRNFP");

                // Или диагностика всех
                // await service.DiagnoseAllOpenPositionsAsync();

                StatusMessage = "Диагностика завершена. Проверьте вывод в Debug Output.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Ошибка диагностики: {ex.Message}";
                Debug.WriteLine($"Ошибка диагностики: {ex.Message}");
            }
        }


        /// <summary>
        /// Закрытие сделки из таблицы операций
        /// </summary>
        private async Task CloseOperationAsync(ProcessedOperation op)
        {
            if (op == null)
            {
                if (SelectedProcessedOperation == null)
                {
                    MessageBox.Show("Сделка не выбрана. Пожалуйста, выберите сделку в таблице.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                op = SelectedProcessedOperation;
            }

            if (!op.Status.Contains("Open"))
            {
                MessageBox.Show($"Сделка {op.Ticker} уже закрыта.",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Находим соответствующую сделку в Deals
            var deal = Deals.FirstOrDefault(d => d.InstrumentUid == op.InstrumentUid &&
                                                 d.Status == DealStatus.Open &&
                                                 d.Direction == (op.Direction == "Long" ? "Buy" : "Sell"));

            if (deal == null)
            {
                MessageBox.Show($"Не найдена открытая сделка для {op.Ticker}.",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await CloseDealAsync(deal);
        }

        /// <summary>
        /// Переворот сделки из таблицы операций
        /// </summary>
        private async Task FlipOperationAsync(ProcessedOperation op)
        {
            if (op == null)
            {
                if (SelectedProcessedOperation == null)
                {
                    MessageBox.Show("Сделка не выбрана. Пожалуйста, выберите сделку в таблице.",
                                  "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                op = SelectedProcessedOperation;
            }

            if (!op.Status.Contains("Open"))
            {
                MessageBox.Show($"Сделка {op.Ticker} уже закрыта.",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Находим соответствующую сделку в Deals
            var deal = Deals.FirstOrDefault(d => d.InstrumentUid == op.InstrumentUid &&
                                                 d.Status == DealStatus.Open &&
                                                 d.Direction == (op.Direction == "Long" ? "Buy" : "Sell"));

            if (deal == null)
            {
                MessageBox.Show($"Не найдена открытая сделка для {op.Ticker}.",
                              "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await FlipDealAsync(deal);
        }
        #endregion


    }

    public class DataSource
    {
        public string Name { get; set; }
        public bool IsEnabled { get; set; }
    }

    public class TimeFrame
    {
        public string DisplayName { get; }
        public string Value { get; }

        public TimeFrame(string displayName, string value)
        {
            DisplayName = displayName;
            Value = value;
        }

        public override string ToString() => DisplayName;
    }

    public class TradingStrategy
    {
        public string Name { get; }
        public string Type { get; }

        public TradingStrategy(string name, string type)
        {
            Name = name;
            Type = type;
        }

        public override string ToString() => Name;
    }
}