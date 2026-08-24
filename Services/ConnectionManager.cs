// ConnectionManager.cs - УПРОЩЕННАЯ ВЕРСИЯ
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.ViewModels;
using System.Diagnostics;

public class ConnectionManager
{
    private readonly List<IProvirerService> _providers = new();
    //private readonly List<StrategyViewModel> _strategies = new();

    private List<StrategyViewModel> _strategies = new List<StrategyViewModel>();
    private readonly object _strategiesLock = new object();

    public event Action OnConnectionLost;
    public event Action OnReconnectCompleted;




    // ТОЛЬКО одно событие для оповещения о потере/восстановлении соединения
    public event Action<bool> OnConnectionStateChanged; // true = подключен, false = отключен

    public void RegisterProvider(IProvirerService provider)
    {
        if (!_providers.Contains(provider))
        {
            _providers.Add(provider);
            Debug.WriteLine($"DEBUG: ConnectionManager: [{DateTime.Now:HH:mm:ss.fff}] Провайдер зарегистрирован");
        }
    }

    public void RegisterStrategy(StrategyViewModel strategy)
    {
        lock (_strategiesLock)
        {
            if (!_strategies.Contains(strategy))
            {
                _strategies.Add(strategy);
                Debug.WriteLine($"DEBUG: ConnectionManager: [{DateTime.Now:HH:mm:ss.fff}] Стратегия зарегистрирована");
            }
        }
           
    }

    public void UnregisterStrategy(StrategyViewModel strategy)
    {
        lock (_strategiesLock)
        {
            _strategies.Remove(strategy);
        }
    }

    // Метод для ручного вызова восстановления подписок после реконнекта
    public async Task RestoreSubscriptionsAsync()
    {
        Debug.WriteLine($"DEBUG: ConnectionManager: RestoreSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Восстановление подписок...");

        foreach (var provider in _providers.ToList())
        {
            try
            {
                if (provider is TinkoffApiService tinkoffService)
                {
                    await tinkoffService.RestoreSubscriptionsAsync();
                    Debug.WriteLine($"DEBUG: ConnectionManager: RestoreSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Подписки провайдера восстановлены");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: ConnectionManager: RestoreSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка восстановления: {ex.Message}");
            }
        }

        /*foreach (var strategy in _strategies.ToList())
        {
            try
            {
                await strategy.RestoreSubscriptionsAsync();
                Debug.WriteLine($"DEBUG: ConnectionManager: RestoreSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Стратегия восстановлена");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: ConnectionManager: RestoreSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка восстановления стратегии: {ex.Message}");
            }
        }*/
    }

    /*public async Task RestoreAllStrategiesAsync()
    {
        List<StrategyViewModel> strategiesCopy;
        lock (_strategiesLock)
        {
            strategiesCopy = new List<StrategyViewModel>(_strategies);
        }

        foreach (var strategy in strategiesCopy)
        {
            try
            {
                await strategy.RestoreSubscriptionsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка восстановления стратегии: {ex.Message}");
            }
        }
    }*/


    // Метод Только для оповещения о потере соединения
    public void NotifyConnectionLost()
    {
        Debug.WriteLine($"DEBUG: ConnectionManager: NotifyConnectionLost: [{DateTime.Now:HH:mm:ss.fff}] Уведомление о потере соединения");
        OnConnectionStateChanged?.Invoke(false);
    }

    // Метод только для оповещения о восстановлении соединения
    public void NotifyConnectionRestored()
    {
        Debug.WriteLine($"DEBUG: ConnectionManager: NotifyConnectionRestored: [{DateTime.Now:HH:mm:ss.fff}] Уведомление о восстановлении соединения");
        OnConnectionStateChanged?.Invoke(true);
    }

    public void NotifyReconnectCompleted()
    {
        OnReconnectCompleted?.Invoke();
    }

    public void Dispose()
    {
        _providers.Clear();
        _strategies.Clear();
    }
}