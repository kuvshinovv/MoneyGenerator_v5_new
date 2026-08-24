using Google.Protobuf.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.ViewModels;
using MoneyGenerator_v5.Views;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Tinkoff.InvestApi.V1;

namespace MoneyGenerator_v5.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
      


        public MainWindow()
        {
            InitializeComponent();

            // Способ 1: Через ServiceProvider (если используете DI)
            if (App.ServiceProvider != null)
            {
                DataContext = App.ServiceProvider.GetService<MainViewModel>();
            }
            else
            {
                // Способ 2: Создаем вручную (для теста)
                DataContext = new MainViewModel();
            }
        }



        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string dbPath = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var createDealsTable = connection.CreateCommand();
                createDealsTable.CommandText = @"
            CREATE TABLE IF NOT EXISTS DealsJournal (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL,
                InstrumentUid TEXT NOT NULL,
                Strategy TEXT NOT NULL,
                EntryTime DATETIME NOT NULL,
                EntryPrice DECIMAL(18,8) NOT NULL,
                EntryQuantity INTEGER NOT NULL,
                EntryOrderId TEXT NOT NULL,
                Direction TEXT NOT NULL,
                ExitTime DATETIME,
                ExitPrice DECIMAL(18,8),
                ExitOrderId TEXT,
                Status TEXT NOT NULL,
                ClosedPnL DECIMAL(18,2),
                ClosedPnLPercent DECIMAL(18,2),
                Comment TEXT,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                );
            
                    CREATE INDEX IF NOT EXISTS idx_DealsJournal_Ticker ON DealsJournal(Ticker);
                    CREATE INDEX IF NOT EXISTS idx_DealsJournal_Status ON DealsJournal(Status);
                    CREATE INDEX IF NOT EXISTS idx_DealsJournal_EntryTime ON DealsJournal(EntryTime DESC);
                ";
                await createDealsTable.ExecuteNonQueryAsync();


                var createStrategiesTable = connection.CreateCommand();
                createStrategiesTable.CommandText = @"
            CREATE TABLE IF NOT EXISTS SavedStrategies (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StrategyType TEXT NOT NULL,                     -- MA, RSI, Manual, Rating
                InstrumentUid TEXT NOT NULL,
                InstrumentTicker TEXT NOT NULL,
                InstrumentName TEXT,
                Timeframe TEXT NOT NULL,
                ParametersJson TEXT NOT NULL,                   --Сериализованные параметры стратегии
                IsAutoStart BOOLEAN DEFAULT 0,                  --Автозапуск при загрузке
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                LastUsed DATETIME DEFAULT CURRENT_TIMESTAMP
            );

                    CREATE INDEX IF NOT EXISTS idx_SavedStrategies_Type ON SavedStrategies(StrategyType);
                    CREATE INDEX IF NOT EXISTS idx_SavedStrategies_Ticker ON SavedStrategies(InstrumentTicker);
                ";
                await createStrategiesTable.ExecuteNonQueryAsync();









                // Создаем таблицу EquityJournal через EquityService
                await EquityService.EnsureTableExistsAsync();

                Debug.WriteLine("Таблицы созданы/проверены");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка инициализации БД: {ex.Message}");
            }

            Debug.WriteLine("DEBUG: MainWindow loaded");
        }


        /*private async void LoadSavedStrategies_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Создаем ViewModel для загрузки
                var loadViewModel = new LoadSavedStrategiesViewModel(
                    (App.ServiceProvider?.GetService<Func<string, IProvirerService>>()) ??
                        (name => new TinkoffApiService(name, App.ServiceProvider?.GetService<TokenManager>(), App.ServiceProvider?.GetService<ILogger<TinkoffApiService>>())),
                    App.ServiceProvider?.GetService<TokenManager>(),
                    App.ServiceProvider?.GetService<ConnectionManager>(),
                    App.ServiceProvider?.GetService<ILogger<MainViewModel>>()
                );

                // Подписываемся на событие загрузки стратегии
                loadViewModel.StrategyLoadRequested += async (strategyInfo) =>
                {
                    return await LoadStrategyFromInfo(strategyInfo);
                };

                var loadWindow = new LoadSavedStrategiesWindow(loadViewModel);
                loadWindow.Owner = this;
                loadWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка открытия окна загрузки: {ex.Message}",
                               "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private async Task<bool> LoadStrategyFromInfo(SavedStrategyInfo strategyInfo)
        {
            try
            {
                // Проверяем подключение
                if (!(DataContext as MainViewModel).IsConnected)
                {
                    MessageBox.Show("Нет подключения к бирже. Сначала подключитесь.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Находим инструмент в коллекции
                var instrument = (DataContext as MainViewModel).Instruments
                    .FirstOrDefault(i => i.Uid == strategyInfo.InstrumentUid);

                if (instrument == null)
                {
                    MessageBox.Show($"Инструмент {strategyInfo.InstrumentTicker} не найден в списке доступных.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Находим стратегию
                var strategy = (DataContext as MainViewModel).Strategies
                    .FirstOrDefault(s => s.Type == strategyInfo.StrategyType);

                if (strategy == null)
                {
                    MessageBox.Show($"Стратегия {strategyInfo.StrategyType} не найдена.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Находим таймфрейм
                var timeframe = (DataContext as MainViewModel).TimeFrames
                    .FirstOrDefault(t => t.Value == strategyInfo.Timeframe);

                if (timeframe == null)
                {
                    MessageBox.Show($"Таймфрейм {strategyInfo.Timeframe} не найден.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Создаем и открываем окно стратегии
                var strategyVM = new StrategyViewModel(
                    strategy,
                    instrument,
                    timeframe,
                    (DataContext as MainViewModel)._currentProvider,
                    (DataContext as MainViewModel)._connectionManager,
                    null);

                // Восстанавливаем параметры из JSON
                await RestoreStrategyParameters(strategyVM, strategyInfo);

                var strategyWindow = new StrategyWindow
                {
                    DataContext = strategyVM,
                    Owner = Application.Current.MainWindow
                };

                strategyWindow.Show();

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки стратегии {strategyInfo.DisplayName}: {ex.Message}");
                return false;
            }
        }

        private async Task RestoreStrategyParameters(StrategyViewModel strategyVM, SavedStrategyInfo strategyInfo)
        {
            try
            {
                if (string.IsNullOrEmpty(strategyInfo.ParametersJson)) return;

                var jsonDoc = System.Text.Json.JsonDocument.Parse(strategyInfo.ParametersJson);

                switch (strategyInfo.StrategyType)
                {
                    case "RSI":
                        if (strategyVM._rsiStrategy != null)
                        {
                            // Восстанавливаем параметры RSI
                            var root = jsonDoc.RootElement;

                            if (root.TryGetProperty("RsiPeriod", out var rsiPeriod))
                                strategyVM._rsiStrategy._parameters.RsiPeriod = rsiPeriod.GetInt32();
                            if (root.TryGetProperty("RsiOverbought", out var rsiOverbought))
                                strategyVM._rsiStrategy._parameters.RsiOverbought = rsiOverbought.GetDecimal();
                            if (root.TryGetProperty("RsiOversold", out var rsiOversold))
                                strategyVM._rsiStrategy._parameters.RsiOversold = rsiOversold.GetDecimal();
                            if (root.TryGetProperty("OrderSizePercent", out var orderSize))
                                strategyVM._rsiStrategy._parameters.OrderSizePercent = orderSize.GetDecimal();
                            // Добавьте остальные параметры

                            strategyVM._rsiStrategy._parameters.ApplyParameters();
                        }
                        break;

                    case "MA":
                        if (strategyVM._maStrategy != null)
                        {
                            var root = jsonDoc.RootElement;

                            if (root.TryGetProperty("SmaPeriods", out var smaPeriods))
                                strategyVM._maStrategy._parameters.SmaPeriods = smaPeriods.GetString();
                            if (root.TryGetProperty("EmaPeriods", out var emaPeriods))
                                strategyVM._maStrategy._parameters.EmaPeriods = emaPeriods.GetString();
                            if (root.TryGetProperty("PositionSizePercent", out var posPercent))
                                strategyVM._maStrategy._parameters.PositionSizePercent = posPercent.GetDecimal();

                            strategyVM._maStrategy._parameters.ApplyParameters();
                        }
                        break;

                    case "Rating":
                        if (strategyVM._ratingStrategy != null)
                        {
                            var root = jsonDoc.RootElement;

                            if (root.TryGetProperty("EntryThreshold", out var entryThreshold))
                                strategyVM._ratingStrategy._parameters.EntryThreshold = entryThreshold.GetInt32();
                            if (root.TryGetProperty("MatchTolerance", out var matchTolerance))
                                strategyVM._ratingStrategy._parameters.MatchTolerance = matchTolerance.GetDecimal();
                            // Добавьте остальные параметры

                            strategyVM._ratingStrategy._parameters.ApplyParameters();
                        }
                        break;
                }

                // Автоматически запускаем стратегию после восстановления параметров
                await Task.Delay(500);
                await strategyVM.StartStrategy();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка восстановления параметров стратегии: {ex.Message}");
            }
        }*/










    }
}