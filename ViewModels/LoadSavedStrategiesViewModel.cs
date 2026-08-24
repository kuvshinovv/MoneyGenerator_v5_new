using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Views;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Tinkoff.InvestApi.V1;

namespace MoneyGenerator_v5.ViewModels
{
    public partial class LoadSavedStrategiesViewModel : ObservableObject
    {
        private readonly Func<string, IProvirerService> _providerFactory;
        private readonly TokenManager _tokenManager;
        private readonly ConnectionManager _connectionManager;
        private readonly Microsoft.Extensions.Logging.ILogger<MainViewModel> _logger;
        private readonly MainViewModel _mainViewModel;
        private bool _isLoadingStrategies = false;
        private readonly HashSet<string> _loadedStrategies = new();
        private bool _isUpdatingSelection = false;
        private bool _suppressPropertyChanged = false;
        private bool newValue ;

        [ObservableProperty]
        private ObservableCollection<SavedStrategyInfo> _strategies = new();

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _loadingStatus = "";

        [ObservableProperty]
        private int _selectedCount;

        [ObservableProperty]
        private bool _selectAll;

        public ICommand LoadSelectedCommand { get; }
        public ICommand LoadAllCommand { get; }
        public ICommand DeleteSelectedCommand { get; }
        public ICommand DeleteAllCommand { get; }
        public ICommand ToggleSelectAllCommand { get; }
        public ICommand CloseCommand { get; }
        public event Action<SavedStrategyInfo> StrategySelected;
        public event Func<SavedStrategyInfo, Task<bool>> StrategyLoadRequested;

        // Для отслеживания выбранных стратегий
        private readonly ObservableCollection<SavedStrategyInfo> _selectedStrategies = new();

        public LoadSavedStrategiesViewModel(
            Func<string, IProvirerService> providerFactory,
            TokenManager tokenManager,
            ConnectionManager connectionManager,
            Microsoft.Extensions.Logging.ILogger<MainViewModel> logger,
            MainViewModel mainViewModel)
        {
            _providerFactory = providerFactory;
            _tokenManager = tokenManager;
            _connectionManager = connectionManager;
            _logger = logger;
            _mainViewModel = mainViewModel;

            LoadSelectedCommand = new RelayCommand(async () => await LoadSelectedStrategiesAsync(), () => SelectedCount > 0);
            LoadAllCommand = new RelayCommand(async () => await LoadAllStrategiesAsync(), () => Strategies.Any());
            DeleteSelectedCommand = new RelayCommand(async () => await DeleteSelectedStrategiesAsync(), () => SelectedCount > 0);
            DeleteAllCommand = new RelayCommand(async () => await DeleteAllStrategiesAsync(), () => Strategies.Any());
            ToggleSelectAllCommand = new RelayCommand(ToggleSelectAll);
            CloseCommand = new RelayCommand(Close);

            _ = LoadStrategiesAsync();
        }

        private void Close()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Application.Current.Windows.OfType<LoadSavedStrategiesWindow>().FirstOrDefault() is var window)
                {
                    window?.Close();
                }
            });
        }

        /*private async Task LoadStrategiesAsync()
        {
            IsLoading = true;
            LoadingStatus = "Загрузка списка стратегий...";

            try
            {
                var strategies = await SavedStrategiesService.GetAllStrategiesAsync();
                Strategies.Clear();

                foreach (var strategy in strategies)
                {
                    // ✅ Подписываемся на событие изменения
                    strategy.PropertyChanged += Strategy_PropertyChanged;
                    Strategies.Add(strategy);
                }

                // ✅ Используем защищенную установку SelectAll
                _isUpdatingSelection = true;

                try
                {
                    if (SelectAll)
                    {
                        foreach (var strategy in Strategies)
                        {
                            if (!_selectedStrategies.Contains(strategy))
                            {
                                strategy.IsSelected = true;
                                _selectedStrategies.Add(strategy);
                            }
                        }
                    }

                    SelectedCount = _selectedStrategies.Count;
                    LoadingStatus = $"Найдено стратегий: {Strategies.Count}";
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            }
            catch (Exception ex)
            {
                LoadingStatus = $"Ошибка: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }*/
        private async Task LoadStrategiesAsync()
        {
            IsLoading = true;
            LoadingStatus = "Загрузка списка стратегий...";

            try
            {
                var strategies = await SavedStrategiesService.GetAllStrategiesAsync();
                Strategies.Clear();
                _selectedStrategies.Clear();

                // ✅ Отключаем обработку событий на время загрузки
                _suppressPropertyChanged = true;

                foreach (var strategy in strategies)
                {
                    strategy.PropertyChanged += Strategy_PropertyChanged;
                    Strategies.Add(strategy);
                }

                // Устанавливаем все стратегии выбранными
                foreach (var strategy in Strategies)
                {
                    strategy.IsSelected = true;
                    _selectedStrategies.Add(strategy);
                }

                SelectedCount = _selectedStrategies.Count;
                _selectAll = true;

                // ✅ Включаем обработку событий обратно
                _suppressPropertyChanged = false;

                OnPropertyChanged(nameof(SelectAll));
                OnPropertyChanged(nameof(SelectedCount));

                LoadingStatus = $"Найдено стратегий: {Strategies.Count}";
                (LoadSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (DeleteSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
            catch (Exception ex)
            {
                LoadingStatus = $"Ошибка: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
        // ✅ Обработчик изменения выделения
        private void Strategy_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // ✅ Если обновление подавлено, игнорируем
            if (_suppressPropertyChanged) return;
            if (_isUpdatingSelection) return;

            if (e.PropertyName == nameof(SavedStrategyInfo.IsSelected))
            {
                _isUpdatingSelection = true;

                try
                {
                    var strategy = sender as SavedStrategyInfo;
                    if (strategy.IsSelected)
                    {
                        if (!_selectedStrategies.Contains(strategy))
                            _selectedStrategies.Add(strategy);
                    }
                    else
                    {
                        _selectedStrategies.Remove(strategy);
                    }

                    SelectedCount = _selectedStrategies.Count;

                    // ✅ Вычисляем состояние SelectAll
                    // Если выбраны все стратегии И есть хотя бы одна стратегия
                    bool shouldSelectAll = _selectedStrategies.Count == Strategies.Count && Strategies.Count > 0;

                    if (_selectAll != shouldSelectAll)
                    {
                        _selectAll = shouldSelectAll;
                        OnPropertyChanged(nameof(SelectAll));
                    }
                }
                finally
                {
                    _isUpdatingSelection = false;
                }

                (LoadSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (DeleteSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }

        /*partial void OnSelectAllChanged(bool value)
        {
            if (value)
            {
                foreach (var strategy in Strategies)
                {
                    if (!_selectedStrategies.Contains(strategy))
                        _selectedStrategies.Add(strategy);
                }
            }
            else
            {
                _selectedStrategies.Clear();
            }
            SelectedCount = _selectedStrategies.Count;

            (LoadSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DeleteSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }*/

        /* partial void OnSelectAllChanged(bool value)
         {
             // ✅ Защита от рекурсии
             if (_isUpdatingSelection) return;

             _isUpdatingSelection = true;

             try
             {
                 Debug.WriteLine($"OnSelectAllChanged: setting all strategies to {value}");

                 foreach (var strategy in Strategies)
                 {
                     // Временно отписываемся от события
                     strategy.PropertyChanged -= Strategy_PropertyChanged;
                     strategy.IsSelected = value;
                     strategy.PropertyChanged += Strategy_PropertyChanged;
                 }

                 // Обновляем коллекцию выбранных
                 _selectedStrategies.Clear();
                 if (value)
                 {
                     foreach (var strategy in Strategies)
                     {
                         _selectedStrategies.Add(strategy);
                     }
                 }

                 SelectedCount = _selectedStrategies.Count;
             }
             finally
             {
                 _isUpdatingSelection = false;
             }

             // ✅ Обновляем состояние кнопок
             (LoadSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
             (DeleteSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
         }*/

        private void ToggleSelectAll()
        {
            if (_isUpdatingSelection) return;

            _isUpdatingSelection = true;

            try
            {
                // ✅ Вычисляем новое значение на основе текущего состояния
                // Если сейчас выбраны не все, то выбираем все, иначе снимаем все
                bool newValue = _selectedStrategies.Count != Strategies.Count;

                // Если все уже выбраны, то newValue будет false (снимаем все)
                // Если не все выбраны, то newValue будет true (выбираем все)

                Debug.WriteLine($"ToggleSelectAll: current SelectedCount={SelectedCount}, Strategies.Count={Strategies.Count}, newValue={newValue}");

                // ✅ Подавляем события при массовом обновлении
                _suppressPropertyChanged = true;

                foreach (var strategy in Strategies)
                {
                    strategy.IsSelected = newValue;
                }

                // Обновляем коллекцию выбранных
                _selectedStrategies.Clear();
                if (newValue)
                {
                    foreach (var strategy in Strategies)
                    {
                        _selectedStrategies.Add(strategy);
                    }
                }

                SelectedCount = _selectedStrategies.Count;
                _selectAll = newValue;

                // ✅ Включаем события обратно
                _suppressPropertyChanged = false;

                // ✅ Уведомляем UI об изменениях
                OnPropertyChanged(nameof(SelectAll));
                OnPropertyChanged(nameof(SelectedCount));

                Debug.WriteLine($"ToggleSelectAll: after update - SelectAll={SelectAll}, SelectedCount={SelectedCount}");
            }
            finally
            {
                _isUpdatingSelection = false;
            }

            (LoadSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DeleteSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        /*public void ToggleStrategySelection(SavedStrategyInfo strategy, bool isSelected)
        {
            if (isSelected)
            {
                if (!_selectedStrategies.Contains(strategy))
                    _selectedStrategies.Add(strategy);
            }
            else
            {
                _selectedStrategies.Remove(strategy);
                SelectAll = false;
            }
            SelectedCount = _selectedStrategies.Count;

            (LoadSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DeleteSelectedCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }*/

        private async Task LoadSelectedStrategiesAsync()
        {
            if (SelectedCount == 0) return;

            if (_isLoadingStrategies)
            {
                Debug.WriteLine("Загрузка стратегий уже выполняется, пропускаем");
                return;
            }

            if (!_mainViewModel.IsConnected)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show("Нет подключения к бирже. Сначала подключитесь.",
                                   "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
                return;
            }

            _isLoadingStrategies = true;
            IsLoading = true;
            LoadingStatus = $"Загрузка {SelectedCount} стратегий...";

            var successCount = 0;
            var failCount = 0;
            var autoStartCount = 0;

            int index = 0;
            foreach (var strategy in _selectedStrategies.ToList())
            {
                index++;

                var strategyKey = $"{strategy.StrategyType}_{strategy.InstrumentUid}_{strategy.Timeframe}";
                if (_loadedStrategies.Contains(strategyKey))
                {
                    Debug.WriteLine($"Стратегия {strategy.DisplayName} уже загружена, пропускаем");
                    LoadingStatus = $"Пропущена {strategy.DisplayName} (уже загружена)";
                    await Task.Delay(200);
                    continue;
                }

                LoadingStatus = $"Загрузка {index}/{SelectedCount}: {strategy.DisplayName}";

                try
                {
                    if (StrategyLoadRequested != null)
                    {
                        // ✅ ВЫВОДИМ ПАРАМЕТРЫ ИЗ БД ПЕРЕД ЗАГРУЗКОЙ
                        Debug.WriteLine($"=== Загрузка стратегии: {strategy.DisplayName} ===");
                        Debug.WriteLine($"  ParametersJson: {strategy.ParametersJson}");

                        // ✅ Загружаем параметры стратегии
                        var success = await StrategyLoadRequested.Invoke(strategy);
                        if (success)
                        {
                            Debug.WriteLine($"  Стратегия {strategy.DisplayName} успешно загружена");

                            successCount++;
                            _loadedStrategies.Add(strategyKey);

                            // ✅ Если стратегия помечена для автозапуска
                            if (strategy.IsAutoStart)
                            {
                                autoStartCount++;
                                Debug.WriteLine($"Стратегия {strategy.DisplayName} будет автоматически запущена");
                            }

                            // Показываем уведомление о загрузке параметров
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                /*var result = MessageBox.Show(
                                    $"Стратегия {strategy.DisplayName} загружена.\n\n" +
                                    $"Параметры:\n" +
                                    $"- Капитал: {strategy.Capital:N0}\n" +
                                    $"- Глобальный стоп-лосс: {(strategy.UseGlobalStopLoss ? $"Включен ({strategy.GlobalStopLossPercent}%)" : "Выключен")}\n" +
                                    $"- Глобальный тейк-профит: {(strategy.UseGlobalTakeProfit ? $"Включен ({strategy.GlobalTakeProfitPercent}%)" : "Выключен")}\n\n" +
                                    $"Запустить стратегию?",
                                    "Стратегия загружена",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);

                                if (result == MessageBoxResult.Yes)
                                {
                                    _mainViewModel.StartLoadedStrategy(strategy);
                                }*/


                                _mainViewModel.StartLoadedStrategy(strategy);
                            });
                        }
                        else
                            failCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Ошибка загрузки стратегии {strategy.DisplayName}");
                    failCount++;
                }

                if (index < SelectedCount)
                {
                    await Task.Delay(500);
                }
            }

            LoadingStatus = $"Загрузка завершена. Успешно: {successCount}, Ошибок: {failCount}, Автозапуск: {autoStartCount}";

            await Task.Delay(2000);
            await LoadStrategiesAsync();

            IsLoading = false;
            _isLoadingStrategies = false;
        }

        private async Task LoadAllStrategiesAsync()
        {
            SelectAll = true;
            await LoadSelectedStrategiesAsync();
        }

        private async Task DeleteSelectedStrategiesAsync()
        {
            if (SelectedCount == 0) return;

            var result = MessageBox.Show(
                $"Удалить {SelectedCount} сохраненных стратегий?\n\nЭто действие необратимо!",
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            LoadingStatus = $"Удаление {SelectedCount} стратегий...";

            var successCount = 0;
            foreach (var strategy in _selectedStrategies.ToList())
            {
                if (await SavedStrategiesService.DeleteStrategyAsync(strategy.Id))
                {
                    successCount++;
                    Strategies.Remove(strategy);
                }
            }

            _selectedStrategies.Clear();
            SelectedCount = 0;
            LoadingStatus = $"Удалено стратегий: {successCount}";

            await Task.Delay(1500);
            await LoadStrategiesAsync();

            IsLoading = false;
        }

        private async Task DeleteAllStrategiesAsync()
        {
            var result = MessageBox.Show(
                $"Удалить ВСЕ сохраненные стратегии?\n\nЭто действие необратимо!",
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            LoadingStatus = "Удаление всех стратегий...";

            var count = await SavedStrategiesService.DeleteAllStrategiesAsync();
            Strategies.Clear();
            _selectedStrategies.Clear();
            SelectedCount = 0;

            LoadingStatus = $"Удалено стратегий: {count}";

            await Task.Delay(1500);
            await LoadStrategiesAsync();

            IsLoading = false;
        }
    }
}