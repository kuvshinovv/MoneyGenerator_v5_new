using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using MoneyGenerator_v5.ViewModels;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Controls;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;
using static Google.Rpc.Context.AttributeContext.Types;






namespace MoneyGenerator_v5.Services
{
    public class TinkoffApiService : IProvirerService, IDisposable
    {
        #region Поля и свойства
        private InvestApiClient? _client;
        private readonly ILogger<TinkoffApiService> _logger;
        private readonly TokenManager _tokenManager;
        private CancellationTokenSource? _streamCts;
        private AsyncDuplexStreamingCall<MarketDataRequest, MarketDataResponse>? _marketDataStream;
        private readonly Dictionary<string, Action<MarketData>> _subscriptions = new();
        private readonly SemaphoreSlim _streamLock = new(1, 1);
        private Task? _streamProcessingTask;
        private GrpcChannel? _channel;
        private string? _token;
        private bool _isSandbox;
        private Dictionary<string, Models.Instrument> _instrumentsCache = new();
        private readonly Dictionary<string, string> _uidToFigiMap = new();
        private readonly ConnectionManager _connectionManager;

        // Константы для инструментов-индикаторов статуса рынков
        private const string STOCK_MARKET_INDICATOR = "BBG004730N88"; // SBER - для фондового рынка
        private const string DERIVATIVES_MARKET_INDICATOR = "FUTIMOEXF000"; // IMOEXF - для срочного рынка
        public decimal updPos;
        // Событие для обновления статусов
        public event Action<List<MarketStatus>> OnMarketStatusesUpdated;
        // Добавляем флаг для отслеживания состояния подписок
        private bool _marketStatusSubscribed = false;
        private object _marketStatusLock = new object();

        //  поле для хранения активных подписок
        private readonly Dictionary<string, (string instrumentId, Action<CandleUpdate> callback, string timeframe)> _activeCandleSubscriptions = new();

        // Событие обновления сделок
        public event Action OnDealsUpdated;
        // Событие обновления баланса
        public event Action<Models.Account> OnAccountBalanceUpdated;

        public string ProviderName => "Тинькофф";

        // Изменяем систему блокировок
        /*   private readonly Dictionary<string, bool> _entryLock = new(); // Блокировка входа
           private readonly Dictionary<string, bool> _exitLock = new();  // Блокировка выхода
           private readonly object _lockSync = new();*/


        // ОБНОВЛЕНИЕ СВЕЧЕЙ
        private readonly Dictionary<string, Action<CandleUpdate>> _candleSubscriptions = new();
        private readonly Dictionary<string, (DateTime, decimal, decimal, decimal, decimal, long)> _lastCandleUpdates = new();



        // поля и методы для управления подписками с подсчетом ссылок
        private readonly Dictionary<string, int> _candleSubscriptionRefCount = new();
        private readonly Dictionary<string, int> _marketDataSubscriptionRefCount = new();
        private readonly object _subscriptionLock = new object();

        // Внутренние словари для хранения колбэков
        private readonly Dictionary<string, Action<CandleUpdate>> _candleCallbacks = new();
        private readonly Dictionary<string, Action<MarketData>> _marketDataCallbacks = new();




        private readonly List<Models.Position> _positions = new();
        private readonly object _positionsLock = new();
        public IReadOnlyList<Models.Position> Positions
        {
            get
            {
                lock (_positionsLock)
                {
                    return _positions.ToList();
                }
            }
        }

        // Событие обновления позиций
        public event Action<List<Models.Position>> OnPositionsUpdated;

        private Dictionary<string, (decimal atr, DateTime lastUpdate)> _atrCache = new();
        private object _cacheLock = new object();
        private readonly TinkoffApiService _apiService;

        private readonly IServiceProvider _serviceProvider;

        public bool IsConnected => _client != null;
        public bool IsSandboxMode { get; private set; }

        #endregion


        public TinkoffApiService(ILogger<TinkoffApiService> logger, TokenManager tokenManager, ConnectionManager connectionManager)
        {
            _logger = logger;
            _tokenManager = tokenManager;
            _connectionManager = connectionManager;
            _instrumentsCache = new Dictionary<string, Models.Instrument>();

            // Инициализация пустой коллекции подписок
            _subscriptions = new Dictionary<string, Action<MarketData>>();


            // Подписываемся на события менеджера соединений
            _connectionManager.OnConnectionLost += OnConnectionLost;
            _connectionManager.OnReconnectCompleted += OnReconnectCompletedHandler;

        }


        #region Подключение и отключение
        public async Task<bool> ConnectAsync(bool isSandbox)
        {
            int maxAttempts = 1000;
            int attempt = 0;

            while (attempt < maxAttempts)
            {
                attempt++;
                try
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Попытка подключения #{attempt}/{maxAttempts} к Tinkoff API");

                    // Загружаем токены
                    var tokens = _tokenManager.LoadProviderTokens("Тинькофф");
                    var token = isSandbox ? tokens.SandboxToken : tokens.RealToken;

                    if (string.IsNullOrWhiteSpace(token))
                    {
                        throw new ArgumentException("Token cannot be empty");
                    }

                    _isSandbox = isSandbox;
                    _token = token;
                    IsSandboxMode = isSandbox;

                    // Создаем канал с таймаутами
                    var channelOptions = new GrpcChannelOptions
                    {
                        HttpHandler = new SocketsHttpHandler
                        {
                            ConnectTimeout = TimeSpan.FromSeconds(10),
                            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                            KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
                        }
                    };

                    // Создаем канал
                    var address = isSandbox ?
                        "https://sandbox-invest-public-api.tinkoff.ru:443" :
                        "https://invest-public-api.tinkoff.ru:443";

                    _channel = GrpcChannel.ForAddress(address, channelOptions);

                    // Создаем клиент
                    var callInvoker = _channel.CreateCallInvoker();
                    _client = new InvestApiClient(callInvoker);

                    // Тестируем подключение
                    var headers = CreateHeaders();
                    using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                    var accountsResponse = await _client.Users.GetAccountsAsync(
                        new GetAccountsRequest(),
                        headers,
                        cancellationToken: testCts.Token);





                    Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Подключение успешно. Счетов: {accountsResponse.Accounts.Count}");


                   


                    // Загружаем инструменты
                    try
                    {
                        await GetInstrumentsAsync();
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Кэш инструментов: {_instrumentsCache.Count}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Ошибка загрузки инструментов: {ex.Message}");
                    }

                    // Подписываемся на статусы рынков
                    if (!_marketStatusSubscribed)
                    {
                        await SubscribeToMarketStatusIndicators();
                        _marketStatusSubscribed = true;
                    }





                    // Получаем позиции сразу при подключении
                    try
                    {
                        var accounts = await GetAccountsAsync();
                        if (accounts.Any())
                        {
                            var accountId = accounts.First().Id;

                            

                            // Подписываемся на обновления позиций
                            await SubscribeToPositionsAsync(accountId);


                            // Загружаем текущие позиции
                            await LoadCurrentPositionsAsync(accountId);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Ошибка загрузки/подписки на позиции: {ex.Message}");
                    }





                    _connectionManager?.NotifyConnectionRestored();  // Уведомляем _connectionManager об успешном подключении

                    return true;
                }
                catch (Exception ex) when (ex is HttpRequestException ||
                                           ex is IOException ||
                                           ex is SocketException ||
                                           (ex is RpcException rpcEx &&
                                            (rpcEx.StatusCode == StatusCode.Unavailable ||
                                             rpcEx.StatusCode == StatusCode.DeadlineExceeded)))
                {
                    // Сетевая ошибка - пробуем еще
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Сетевая ошибка (попытка {attempt}/{maxAttempts}): {ex.Message}");

                    if (attempt < maxAttempts)
                    {
                        // Экспоненциальная задержка: 2, 4, 8, 16 секунд
                        //var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));  60sec
                        var delay = 60000;
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Ждем 60 секунд...");
                        await Task.Delay(delay);

                        if (attempt != 0)
                        {
                            _connectionManager?.NotifyConnectionRestored();
                        }
                        continue;
                    }

                    return false;
                }
                catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unauthenticated)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Ошибка аутентификации: {rpcEx.Status.Detail}");
                    return false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  ConnectAsync: Ошибка подключения: {ex.Message} \n{ex.StackTrace}");
                    return false;
                }
            }

            return false;
        }

        public async Task DisconnectAsync()
        {
            try
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Отключение от Tinkoff API...");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                await _streamLock.WaitAsync(cts.Token);

                try
                {
                    // Останавливаем поток данных с таймаутом
                    var stopTask = StopMarketDataStreamAsync();
                    if (await Task.WhenAny(stopTask, Task.Delay(5000, cts.Token)) != stopTask)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Timeout при остановке стрима");
                    }

                    // ✅ ОЧИЩАЕМ ВСЕ СЧЕТЧИКИ ПОДПИСОК ПРИ ОТКЛЮЧЕНИИ
                    await ClearAllSubscriptionsAsync();
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Все счетчики подписок очищены");

                    // СБРАСЫВАЕМ флаг подписки на статусы рынков при отключении
                    lock (_marketStatusLock)
                    {
                        _marketStatusSubscribed = false;
                    }
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Флаг подписки на статусы рынков сброшен");

                    // Очищаем коллекции
                    lock (_positionsLock)
                    {
                        _positions.Clear();
                    }

                    _subscriptions.Clear();
                    _candleSubscriptions.Clear();
                    _lastCandleUpdates.Clear();
                    // НЕ очищаем _activeCandleSubscriptions - они нужны для восстановления

                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Внутренние структуры очищены");
                }
                finally
                {
                    _streamLock.Release();
                }

                // Закрываем канал с таймаутом
                if (_channel != null)
                {
                    try
                    {
                        await _channel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Timeout при закрытии канала");
                    }
                    catch { }
                    _channel = null;
                }

                // Сбрасываем клиент
                _client = null;
                _token = null;
                IsSandboxMode = false;

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Успешно отключено от Tinkoff API");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка отключения: {ex.Message}");
                // Принудительная очистка даже при ошибке
                _client = null;
                _channel = null;
                _token = null;

                lock (_marketStatusLock)
                {
                    _marketStatusSubscribed = false;
                }

                // ✅ ПРИНУДИТЕЛЬНАЯ ОЧИСТКА СЧЕТЧИКОВ ДАЖЕ ПРИ ОШИБКЕ
                lock (_subscriptionLock)
                {
                    _candleSubscriptionRefCount.Clear();
                    _candleCallbacks.Clear();
                    _marketDataSubscriptionRefCount.Clear();
                    _marketDataCallbacks.Clear();
                }
            }
        }

        public async Task<bool> ReconnectAsync()
        {
            try
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService: ReconnectAsync: [{DateTime.Now:HH:mm:ss.fff}] Переподключение...");

                // 1. Сохраняем подписки
                var wasSandbox = _isSandbox;
                var activeSubscriptions = new Dictionary<string, (string instrumentId, Action<CandleUpdate> callback, string timeframe)>(_activeCandleSubscriptions);

                // 2. Отключаемся
                try
                {
                    await DisconnectAsync();
                }
                catch
                {
                    // Игнорируем ошибки отключения
                    _client = null;
                    _channel = null;
                    _token = null;
                }

                // 3. Подключаемся
                var connected = await ConnectAsync(wasSandbox);

                await Task.Delay(2000); // Небольшая задержка

                if (!connected)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService: ReconnectAsync: [{DateTime.Now:HH:mm:ss.fff}] Не удалось переподключиться");
                    return false;
                }


                // Восстанавливаем подписку на статусы рынков
                if (!_marketStatusSubscribed)
                {
                    await SubscribeToMarketStatusIndicators();
                    _marketStatusSubscribed = true;
                }





                // 4. Восстанавливаем подписки на свечи
                if (activeSubscriptions.Any())
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService: ReconnectAsync: [{DateTime.Now:HH:mm:ss.fff}] Восстанавливаем {activeSubscriptions.Count} подписок");

                    foreach (var subscription in activeSubscriptions.Values)
                    {
                        try
                        {
                            await SubscribeToCandlesAsync(
                                subscription.instrumentId,
                                subscription.timeframe,
                                subscription.callback);

                            Debug.WriteLine($"DEBUG: TinkoffApiService: ReconnectAsync: [{DateTime.Now:HH:mm:ss.fff}] Подписка восстановлена: {subscription.instrumentId}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService: ReconnectAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка восстановления: {ex.Message}");
                        }

                        await Task.Delay(200); // Небольшая задержка
                    }
                }


                // Восстанавливаем все подписки
                await RestoreAllSubscriptionsAsync();

                Debug.WriteLine($"DEBUG: TinkoffApiService: ReconnectAsync: [{DateTime.Now:HH:mm:ss.fff}] Переподключение успешно");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService: ReconnectAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка переподключения: {ex.Message}");
                return false;
            }
        }
        public Metadata CreateHeaders()
        {
            // Сначала проверяем, есть ли уже токен
            if (string.IsNullOrEmpty(_token))
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Token is empty in CreateHeaders(). IsSandbox: {_isSandbox}");

                // Загружаем токен из менеджера
                try
                {
                    var tokens = _tokenManager.LoadProviderTokens("Тинькофф");

                    
                    _token = _isSandbox ? tokens.SandboxToken : tokens.RealToken;

                    if (!string.IsNullOrEmpty(_token))
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Token loaded from TokenManager for Sandbox={_isSandbox}");
                    }
                    else
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Token is null/empty in TokenManager for Sandbox={_isSandbox}");
                        throw new InvalidOperationException($"Token is not set for Tinkoff API (Sandbox: {_isSandbox}). Check tokens.secret file.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  Error loading token: {ex.Message}");
                    throw;
                }
            }

            // Дополнительная проверка после загрузки
            if (string.IsNullOrEmpty(_token))
            {
                throw new InvalidOperationException($"Token is not set for Tinkoff API (Sandbox: {_isSandbox}).");
            }

            return new Metadata
    {
        { "Authorization", $"Bearer {_token}" },
        { "x-app-name", "MoneyGenerator_v5" }
    };
        }

        #endregion





        public async Task<List<Models.Account>> GetAccountsAsync()
        {
            if (_client == null)
                throw new InvalidOperationException("Клиент не подключен");
            
            try
            {
                var headers = CreateHeaders();
                var response = await _client.Users.GetAccountsAsync(new GetAccountsRequest(), headers);
                var accounts = new List<Models.Account>();

                foreach (var account in response.Accounts)
                {
                    
                    try
                    {
                        decimal balance = 0;

                        // Пытаемся получить портфель, но обрабатываем возможные ошибки
                        try
                        {
                            var portfolio = await _client.Operations.GetPortfolioAsync(
                                new PortfolioRequest { AccountId = account.Id },
                                headers);

                            // БЕЗОПАСНОЕ получение баланса
                            if (portfolio?.TotalAmountPortfolio != null)
                            {
                                balance = ConvertMoneyValueToDecimal(portfolio.TotalAmountPortfolio);


                               /* Debug.WriteLine($"\n----------------------------------------" +
                                    $"DEBUG: TinkoffApiService  GetAccountsAsync:  portfolio={portfolio}   " +
                                    //$"\n---Рассчитанная доходность портфеля за день в рублях ={Convert.ToString(portfolio.DailyYield.Units)},{Convert.ToString(portfolio.DailyYield.Nano)}" +
                                    //$"\n---Относительная доходность в день в % ={Convert.ToString(portfolio.DailyYieldRelative.Units)},{Convert.ToString(portfolio.DailyYieldRelative.Nano)}" +
                                    $"\n---Текущая относительная доходность портфеля в % ={Convert.ToString(portfolio.ExpectedYield.Units)},{Convert.ToString(portfolio.ExpectedYield.Nano)}" +
                                    $"\n---portfolio.TotalAmountPortfolio=Общая стоимость портфеля={portfolio.TotalAmountPortfolio}" +
                                    $"\n----------------------------------------");*/



                            }
                            else
                            {
                                // Альтернативный способ - используем GetPositionsAsync
                                var positionsResponse = await GetPositionsAsync(account.Id);
                                if (positionsResponse?.Money != null)
                                {
                                    var rubMoney = positionsResponse.Money.FirstOrDefault(m =>
                                        m.Currency == "rub");
                                    if (rubMoney != null)
                                    {
                                        balance = ConvertMoneyValueToDecimal(rubMoney);
                                    }
                                }
                            }
                        }
                        catch (RpcException ex) when (ex.StatusCode == StatusCode.Internal ||
                                                     ex.StatusCode == StatusCode.Unavailable ||
                                                     ex.StatusCode == StatusCode.Unknown)
                        {
                            // Логируем, но не падаем
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка получения портфеля для счета {account.Id}: {ex.Status.Detail}");
                            balance = 0;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Общая ошибка получения баланса для счета {account.Id}: {ex.Message}");
                            balance = 0;
                        }

               

                        var accountModel = new Models.Account
                        {
                            Id = account.Id,
                            Name = account.Name,
                            Currency = "RUB",
                            Balance = balance
                        };

                        accounts.Add(accountModel);

                        // ✅ УВЕДОМЛЯЕМ ОБ ИЗМЕНЕНИИ БАЛАНСА
                        OnAccountBalanceUpdated?.Invoke(accountModel);

                        //Debug.WriteLine($"DEBUG: TinkoffApiService:  Account {account.Name} - Balance: {balance}");
                        
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Critical error for account {account.Id}: {ex.Message}");
                        // Добавляем аккаунт с нулевым балансом в случае ошибки
                        accounts.Add(new Models.Account
                        {
                            Id = account.Id,
                            Name = account.Name,
                            Currency = "RUB",
                            Balance = 0
                        });
                    }
                }

                //Debug.WriteLine($"DEBUG: TinkoffApiService:  Total accounts loaded: {accounts.Count}");
                return accounts;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Fatal error in GetAccountsAsync: {ex.Message}");
                return new List<Models.Account>();
            }
        }

        /*private async Task<PositionsResponse> GetPositionsAsync(string accountId)
        {
            if (_client == null)
                throw new InvalidOperationException("Клиент не подключен");

            if (IsSandboxMode)
            {
                return await _client.Sandbox.GetSandboxPositionsAsync(new PositionsRequest
                {
                    AccountId = accountId
                });
            }
            else
            {
                return await _client.Operations.GetPositionsAsync(new PositionsRequest
                {
                    AccountId = accountId
                });
            }
        }*/

        // Метод для восстановления подписок после реконнекта
        public async Task RestoreSubscriptionsAsync()
        {
            await _streamLock.WaitAsync();
            try
            {
                _logger.LogInformation("Восстановление подписок после реконнекта...");

                // Останавливаем старый поток
                await StopMarketDataStreamAsync();

                // Ждем перед созданием нового потока
                await Task.Delay(5000);

                // Создаем новый поток
                await InitializeMarketDataStreamAsync();

                // Ждем инициализации потока
                await Task.Delay(3000);

                // Восстанавливаем подписки на статусы рынков
                if (_marketStatusSubscribed)
                {
                    try
                    {
                        await SubscribeToMarketStatusIndicators();
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Подписки на статусы рынков восстановлены");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка восстановления статусов рынков: {ex.Message}");
                    }
                }

                // Восстанавливаем подписки на свечи
                foreach (var subscription in _activeCandleSubscriptions.Values.ToList())
                {
                    try
                    {
                        // Добавляем задержку между восстановлением подписок
                        await Task.Delay(1000);

                        await SubscribeToCandlesAsync(
                            subscription.instrumentId,
                            subscription.timeframe,
                            subscription.callback);

                        _logger.LogDebug($"Подписка восстановлена: {subscription.instrumentId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Ошибка восстановления подписки для {subscription.instrumentId}");
                    }
                }

                _logger.LogInformation($"Восстановлено {_activeCandleSubscriptions.Count} подписок");
            }
            finally
            {
                _streamLock.Release();
            }
        }




        public async Task<List<Models.Instrument>> GetInstrumentsAsync()
        {
            if (_client == null) throw new InvalidOperationException("Not connected");

            try
            {
                Debug.WriteLine("DEBUG: TinkoffApiService:  Loading all available instruments...");

                var headers = CreateHeaders();
                var result = new List<Models.Instrument>();

                // Загружаем акции
                var stocksResponse = await _client.Instruments.SharesAsync(new InstrumentsRequest
                {
                    InstrumentStatus = InstrumentStatus.Base
                }, headers);

                var stocks = stocksResponse.Instruments
                    .Where(x => x.RealExchange == RealExchange.Moex || x.Exchange == "SPBXM")
                    .Select(x => new Models.Instrument
                    {
                        Uid = x.Uid,
                        Figi = x.Figi,
                        Ticker = x.Ticker,
                        Name = x.Name,
                        Currency = x.Currency,
                        ClassCode = x.ClassCode,
                        Exchange = x.Exchange,
                        MinStepPrice = x.MinPriceIncrement,
                        LotSize = (int)(x.Lot > 0 ? x.Lot : 1) // Добавляем размер лота
                    })
                    .ToList();

                result.AddRange(stocks);
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Loaded {stocks.Count} stocks");

                // Загружаем фьючерсы - ТОЛЬКО 4 указанных тикера
                var futuresResponse = await _client.Instruments.FuturesAsync(new InstrumentsRequest
                {
                    InstrumentStatus = InstrumentStatus.Base
                }, headers);

                // Список нужных фьючерсов
                var targetFutures = new[] { "IMOEXF", "GLDRUBF", "CNYRUBF", "USDRUBF" };

                var futures = futuresResponse.Instruments
                    .Where(x => targetFutures.Contains(x.Ticker))
                    .Select(x => new Models.Instrument
                    {
                        Uid = x.Uid,
                        Figi = x.Figi,
                        Ticker = x.Ticker,
                        Name = x.Name,
                        Currency = x.Currency,
                        ClassCode = x.ClassCode,
                        Exchange = x.Exchange,
                        LotSize = (int)(x.Lot > 0 ? x.Lot : 1), // Добавляем размер лота для фьючерсов
                        InitialMarginOnBuy = x.InitialMarginOnBuy, // гарантиное обеспечение при покупке
                        InitialMarginOnSell = x.InitialMarginOnSell, // гарантиное обеспечение при продаже
                        MinPriceIncrementAmount = x.MinPriceIncrementAmount, // Стоимость шага цены
                        MinPriceIncrement = x.MinPriceIncrement // Шаг цены
                    })
                    .ToList();

                result.AddRange(futures);
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Loaded {futures.Count} futures (filtered to: {string.Join(", ", futures.Select(f => f.Ticker))})");

                // Кэшируем инструменты
                foreach (var instrument in result)
                {
                    if (!string.IsNullOrEmpty(instrument.Uid))
                    {
                        _instrumentsCache[instrument.Uid] = instrument;

                        // Сохраняем соответствие UID -> FIGI
                        if (!string.IsNullOrEmpty(instrument.Figi))
                        {
                            _uidToFigiMap[instrument.Uid] = instrument.Figi;
                        }
                    }
                }

                Debug.WriteLine($"DEBUG: TinkoffApiService:  Total instruments loaded: {result.Count}");
                Debug.WriteLine($"DEBUG: TinkoffApiService:  UID->FIGI mappings: {_uidToFigiMap.Count}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Error loading instruments: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Подписка на рыночные данные с корректной ссылочной системой
        /// </summary>
        public async Task SubscribeToMarketDataAsync(string instrumentId, Action<MarketData> onDataReceived)
        {
            if (_client == null)
                throw new InvalidOperationException("Клиент не подключен");

            if (string.IsNullOrEmpty(instrumentId))
                throw new ArgumentException("InstrumentId cannot be empty", nameof(instrumentId));

            if (onDataReceived == null)
                throw new ArgumentNullException(nameof(onDataReceived));

            Debug.WriteLine($"[SubscribeToMarketDataAsync] Запрос подписки на {instrumentId}");

            lock (_subscriptionLock)
            {
                string key = instrumentId;

                // Инициализируем счетчик если его нет
                if (!_marketDataSubscriptionRefCount.ContainsKey(key))
                {
                    _marketDataSubscriptionRefCount[key] = 0;
                    // ✅ ИСПРАВЛЕНИЕ: Используем List для хранения всех колбэков
                    _marketDataCallbacks[key] = null;
                }

                // Увеличиваем счетчик
                _marketDataSubscriptionRefCount[key]++;

                // ✅ ИСПРАВЛЕНИЕ: Добавляем колбэк в список, а не перезаписываем цепочку
                var existingCallback = _marketDataCallbacks[key];
                _marketDataCallbacks[key] = (data) =>
                {
                    // Вызываем ВСЕ зарегистрированные колбэки
                    existingCallback?.Invoke(data);
                    onDataReceived?.Invoke(data);
                };

                Debug.WriteLine($"[SubscribeToMarketDataAsync] Подписка {key}: счетчик = {_marketDataSubscriptionRefCount[key]}");

                // Если это первая подписка, реально подписываемся
                if (_marketDataSubscriptionRefCount[key] == 1)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool lockTaken = await _streamLock.WaitAsync(TimeSpan.FromSeconds(10));
                            if (!lockTaken)
                            {
                                Debug.WriteLine($"[SubscribeToMarketDataAsync] ⏰ Timeout ожидания блокировки для {instrumentId}");
                                return;
                            }

                            try
                            {
                                if (_marketDataStream == null)
                                {
                                    await InitializeMarketDataStreamAsync();
                                }

                                // Проверяем, не подписаны ли уже  
                                if (!_subscriptions.ContainsKey(instrumentId))
                                {

                                    // Отправляем подписку на информацию (статус торгов)
                                    var subscribeRequest = new MarketDataRequest
                                    {
                                        SubscribeInfoRequest = new SubscribeInfoRequest
                                        {
                                            SubscriptionAction = SubscriptionAction.Subscribe,
                                            Instruments = { new InfoInstrument { InstrumentId = instrumentId } }
                                        }
                                    };

                                    Debug.WriteLine($"[SubscribeToMarketDataAsync] Отправка реальной подписки на {instrumentId}");
                                    await _marketDataStream!.RequestStream.WriteAsync(subscribeRequest);
                                    _subscriptions[instrumentId] = _marketDataCallbacks[key];

                                    Debug.WriteLine($"[SubscribeToMarketDataAsync] ✅ Реальная подписка на {instrumentId} выполнена");
                                    _logger.LogInformation("Подписка на инструмент {InstrumentId} выполнена успешно", instrumentId);




                                    // ✅ ДОБАВЛЯЕМ: Подписка на последние цены
                                    var subscribeLastPriceRequest = new MarketDataRequest
                                    {
                                        SubscribeLastPriceRequest = new SubscribeLastPriceRequest
                                        {
                                            SubscriptionAction = SubscriptionAction.Subscribe,
                                            Instruments = { new LastPriceInstrument { InstrumentId = instrumentId } }
                                        }
                                    };
                                    await _marketDataStream!.RequestStream.WriteAsync(subscribeLastPriceRequest);

                                    Debug.WriteLine($"[SubscribeToMarketDataAsync] ✅ Реальная подписка на {instrumentId} выполнена (Info + LastPrice)");





                                }
                                else
                                {
                                    Debug.WriteLine($"[SubscribeToMarketDataAsync] 📌 Подписка на {instrumentId} уже существует");
                                }
                            }
                            finally
                            {
                                _streamLock.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SubscribeToMarketDataAsync] ❌ Ошибка подписки на {instrumentId}: {ex.Message}");
                            _logger.LogError(ex, "Ошибка подписки на инструмент {InstrumentId}", instrumentId);

                            // При ошибке уменьшаем счетчик
                            lock (_subscriptionLock)
                            {
                                if (_marketDataSubscriptionRefCount.ContainsKey(key))
                                {
                                    _marketDataSubscriptionRefCount[key]--;
                                    if (_marketDataSubscriptionRefCount[key] == 0)
                                    {
                                        _marketDataSubscriptionRefCount.Remove(key);
                                        _marketDataCallbacks.Remove(key);
                                    }
                                }
                            }
                        }
                    });
                }
                else
                {
                    Debug.WriteLine($"[SubscribeToMarketDataAsync] 📌 Используем существующую подписку на {instrumentId} (всего подписчиков: {_marketDataSubscriptionRefCount[key]})");

                    // ✅ ИСПРАВЛЕНИЕ: Если подписка уже есть, просто обновляем колбэк в словаре
                    if (_subscriptions.ContainsKey(instrumentId))
                    {
                        _subscriptions[instrumentId] = _marketDataCallbacks[key];
                    }
                }
            }
        }

        // Метод подписки на индикаторы статусов рынков
        public async Task SubscribeToMarketStatusIndicators()
        {
            Debug.WriteLine($"---SubscribeToMarketStatusIndicators----->");

            try
            {

                // ПРОВЕРЯЕМ, не подписаны ли мы уже (уже есть)
                lock (_marketStatusLock)
                {
                    if (_marketStatusSubscribed && _marketDataStream != null)
                    {
                        Debug.WriteLine("Уже подписаны на статусы рынков и стрим существует");
                        return;
                    }
                }

                // Сначала инициализируем поток, если он еще не создан
                await _streamLock.WaitAsync();


                // ПРОВЕРЯЕМ ЕЩЕ РАЗ внутри блокировки
                if (_marketDataStream != null && _marketStatusSubscribed)
                {
                    Debug.WriteLine("Стрим уже существует, пропускаем создание нового");
                    _streamLock.Release();
                    return;
                }

                // Проверяем токен перед созданием стрима
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Before stream creation, token exists: {!string.IsNullOrEmpty(_token)}");
                if (string.IsNullOrEmpty(_token))
                {
                    Debug.WriteLine("DEBUG: TinkoffApiService:  Token is empty! Cannot create stream.");
                    _streamLock.Release();
                    return;
                }



                try
                {
                    // Вместо полной инициализации, просто подписываемся в существующий стрим
                    if (_marketDataStream == null)
                    {
                        await InitializeMarketDataStreamAsync();
                    }

                    // В песочнице используем правильные FIGI
                    // Для SBER в песочнице может быть другой FIGI
                    // Для песочницы попробуем найти инструменты по имени
                    string stockFigi = _isSandbox ? await GetSandboxInstrumentFigi("SBER") : STOCK_MARKET_INDICATOR;
                    string futuresFigi = _isSandbox ? await GetSandboxInstrumentFigi("IMOEX") : DERIVATIVES_MARKET_INDICATOR;

                    Debug.WriteLine($"Подписка на статусы рынков. Режим песочницы: {_isSandbox}");
                    Debug.WriteLine($"FIGI для фондового рынка: {stockFigi}");
                    Debug.WriteLine($"FIGI для срочного рынка: {futuresFigi}");

                    // ОТПИСЫВАЕМСЯ от старых подписок перед новой подпиской
                    if (_subscriptions.ContainsKey(stockFigi))
                    {
                        Debug.WriteLine($"Отписываемся от старой подписки на фондовый рынок");
                        _subscriptions.Remove(stockFigi);
                    }

                    if (_subscriptions.ContainsKey(futuresFigi))
                    {
                        Debug.WriteLine($"Отписываемся от старой подписки на срочный рынок");
                        _subscriptions.Remove(futuresFigi);
                    }

                    // Подписываемся на фондовый рынок
                    Debug.WriteLine($"Подписываемся на фондовый рынок (FIGI: {stockFigi})");
                    var subscribeStockRequest = new MarketDataRequest
                    {
                        SubscribeInfoRequest = new SubscribeInfoRequest
                        {
                            SubscriptionAction = SubscriptionAction.Subscribe,
                            Instruments = { new InfoInstrument {
                        InstrumentId = stockFigi,
                        Figi = stockFigi
                    } }
                        }
                    };

                    try
                    {
                        await _marketDataStream!.RequestStream.WriteAsync(subscribeStockRequest);
                        _subscriptions[stockFigi] = ProcessStockMarketStatus;
                        Debug.WriteLine($"Подписка на фондовый рынок успешно отправлена");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка подписки на фондовый рынок: {ex.Message}");
                        // Продолжаем попытку подписаться на другой рынок
                    }

                    // Подписываемся на срочный рынок
                    Debug.WriteLine($"Подписываемся на срочный рынок (FIGI: {futuresFigi})");
                    var subscribeFuturesRequest = new MarketDataRequest
                    {
                        SubscribeInfoRequest = new SubscribeInfoRequest
                        {
                            SubscriptionAction = SubscriptionAction.Subscribe,
                            Instruments = { new InfoInstrument {
                        InstrumentId = futuresFigi,
                        Figi = futuresFigi
                    } }
                        }
                    };

                    try
                    {
                        await _marketDataStream.RequestStream.WriteAsync(subscribeFuturesRequest);
                        _subscriptions[futuresFigi] = ProcessDerivativesMarketStatus;
                        Debug.WriteLine($"Подписка на срочный рынок успешно отправлена");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка подписки на срочный рынок: {ex.Message}");
                    }

                    Debug.WriteLine($"Всего активных подписок: {_subscriptions.Count}");
                    _logger.LogInformation("Подписка на статусы рынков выполнена успешно");

                    // Устанавливаем флаг ТОЛЬКО после успешной подписки
                    lock (_marketStatusLock)
                    {
                        _marketStatusSubscribed = true;
                    }
                    Debug.WriteLine("Подписка на статусы рынков установлена");
                }
                finally
                {
                    _streamLock.Release();
                }
            }
            catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unauthenticated)
            {
                Debug.WriteLine($"Ошибка аутентификации при подписке: {rpcEx.Status.Detail}");
                _logger.LogError(rpcEx, "Ошибка аутентификации при подписке на статусы рынков");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка подписки на статусы: {ex.Message}");
                _logger.LogError(ex, "Ошибка подписки на индикаторы статусов рынков");
            }
        }

        // Метод подписки на позиции
        public async Task SubscribeToPositionsAsync(string accountId)
        {
            if (_client == null)
                throw new InvalidOperationException("Клиент не подключен");

            try
            {
                Debug.WriteLine($"Подписка на позиции для счета {accountId}");

                var headers = CreateHeaders();

                // Проверьте правильное имя метода для стрима портфеля
                // Возможные варианты: PortfolioStream, GetPortfolioStream, PortfolioStreamAsync и т.д.
                var portfolioStream = _client.OperationsStream.PortfolioStream(
                    new PortfolioStreamRequest
                    {
                        Accounts = { accountId }
                    },
                    headers: headers);

                Debug.WriteLine($"Стрим портфеля создан для счета {accountId}");


                // Запускаем обработку стрима в фоне
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine($"Начинаем чтение стрима портфеля...");

                        await foreach (var response in portfolioStream.ResponseStream.ReadAllAsync())
                        {
                            ProcessPortfolioStreamResponse(accountId, response);
                        }

                        Debug.WriteLine("Стрим портфеля завершен");
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                    {
                        Debug.WriteLine("Стрим портфеля остановлен");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка в стриме портфеля: {ex.Message}");
                    }
                });

                Debug.WriteLine($"Стрим портфеля запущен для счета {accountId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка создания стрима портфеля: {ex.Message}");
                throw;
            }
        }

        // Обработчик ответов стрима портфеля
        private void ProcessPortfolioStreamResponse(string accountId, PortfolioStreamResponse response)
        {
            Debug.WriteLine($"DEBUG - ProcessPortfolioStreamResponse  ----    ");    // Игнорируем пинги
            try
            {
                switch (response.PayloadCase)  // Изменено с ResponseCase на PayloadCase
                {
                    case PortfolioStreamResponse.PayloadOneofCase.Subscriptions:  // Изменено с ResponseOneofCase на PayloadOneofCase
                        Debug.WriteLine($"DEBUG - Subscriptions  Подписка на портфель подтверждена  {response.Subscriptions.Accounts}");
                        break;

                    case PortfolioStreamResponse.PayloadOneofCase.Portfolio:  // Изменено с ResponseOneofCase на PayloadOneofCase
                        ProcessPortfolioUpdate(accountId, response.Portfolio);
                        Debug.WriteLine($"DEBUG - ProcessPortfolioStreamResponse  ----  Portfolio  {response.Portfolio} " +
                            $"TotalAmountPortfolio={response.Portfolio.TotalAmountPortfolio} - общая стоимость портфелля" +
                            $"Positions={response.Portfolio.Positions} - Список позиций портфля");
                        break;

                    case PortfolioStreamResponse.PayloadOneofCase.Ping:  // Изменено с ResponseOneofCase на PayloadOneofCase

                        Debug.WriteLine($"DEBUG - ProcessPortfolioStreamResponse  ----  Ping  {response.Ping}   ");    // Игнорируем пинги
                        break;

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обработки стрима портфеля: {ex.Message}");
            }


        }

        // Обработка обновлений портфеля
        private void ProcessPortfolioUpdate(string accountId, PortfolioResponse portfolio)
        {
            if (portfolio?.Positions == null) return;

            var updatedPositions = new List<Models.Position>();

            /*// ✅ ОБНОВЛЯЕМ БАЛАНС СЧЕТА
            if (portfolio.TotalAmountPortfolio != null)
            {
                var totalBalance = ConvertMoneyValueToDecimal(portfolio.TotalAmountPortfolio);

                // Находим или создаем аккаунт с обновленным балансом
                var updatedAccount = new Models.Account
                {
                    Id = accountId,
                    Balance = totalBalance,
                    Currency = "RUB"
                };

                // Уведомляем об изменении баланса
                OnAccountBalanceUpdated?.Invoke(updatedAccount);

                Debug.WriteLine($"DEBUG: Баланс счета {accountId} обновлен: {totalBalance:F2}");
            }*/

            // ✅ ОБНОВЛЯЕМ БАЛАНС СЧЕТА
            if (portfolio.TotalAmountPortfolio != null)
            {
                var totalBalance = ConvertMoneyValueToDecimal(portfolio.TotalAmountPortfolio);

                var updatedAccount = new Models.Account
                {
                    Id = accountId,
                    Balance = totalBalance,
                    Currency = "RUB"
                };
                OnAccountBalanceUpdated?.Invoke(updatedAccount);
                Debug.WriteLine($"DEBUG: ---+++=== ===+++---  Баланс счета {accountId} обновлен: {totalBalance:F2}     {portfolio.TotalAmountPortfolio:F2}  {portfolio.TotalAmountPortfolio:F}  {portfolio.TotalAmountPortfolio} ");
            }


            foreach (var pos in portfolio.Positions)
            {
                try
                {
                    // Получаем инструмент из кэша для определения LotSize
                    Models.Instrument instrument = null;
                    if (!string.IsNullOrEmpty(pos.InstrumentUid) &&
                        _instrumentsCache.TryGetValue(pos.InstrumentUid, out instrument))
                    {
                        // ✅ ИСПРАВЛЕНИЕ: Количество = QuantityLots / LotSize
                        int quantity = pos.QuantityLots != null ? (int)pos.QuantityLots : 0;
                        if (instrument.LotSize > 1 && quantity > 0)
                        {
                            quantity = (int)(pos.QuantityLots /*/ instrument.LotSize*/);
                        }

                        var position = new Models.Position
                        {
                            AccountId = accountId,
                            InstrumentUid = pos.InstrumentUid,
                            Figi = pos.Figi,
                            Quantity = quantity,
                            CurrentPrice = pos.CurrentPrice != null ? ConvertMoneyValueToDecimal(pos.CurrentPrice) : 0m,
                            AveragePrice = pos.AveragePositionPrice != null ? ConvertMoneyValueToDecimal(pos.AveragePositionPrice) : 0m,
                            ExpectedYield = pos.ExpectedYield != null ? ConvertQuotationToDecimal(pos.ExpectedYield) : 0m,
                            CurrentNkd = pos.CurrentNkd != null ? ConvertMoneyValueToDecimal(pos.CurrentNkd) : 0m,
                            InstrumentType = pos.InstrumentType,
                            LotSize = instrument.LotSize,
                            Ticker = instrument.Ticker,
                            Name = instrument.Name,
                            Currency = instrument.Currency,
                            LastUpdate = DateTime.Now
                        };
                        updatedPositions.Add(position);
                        Debug.WriteLine($"Позиция обновлена: {position.Ticker} - {position.Quantity} лотов (Original={pos.QuantityLots})");
                    }
                    else
                    {
                        // Если инструмент не найден, используем значение как есть
                        var position = new Models.Position
                        {
                            AccountId = accountId,
                            InstrumentUid = pos.InstrumentUid,
                            Figi = pos.Figi,
                            Quantity = pos.QuantityLots != null ? (int)pos.QuantityLots : 0,
                            CurrentPrice = pos.CurrentPrice != null ? ConvertMoneyValueToDecimal(pos.CurrentPrice) : 0m,
                            AveragePrice = pos.AveragePositionPrice != null ? ConvertMoneyValueToDecimal(pos.AveragePositionPrice) : 0m,
                            ExpectedYield = pos.ExpectedYield != null ? ConvertQuotationToDecimal(pos.ExpectedYield) : 0m,
                            CurrentNkd = pos.CurrentNkd != null ? ConvertMoneyValueToDecimal(pos.CurrentNkd) : 0m,
                            InstrumentType = pos.InstrumentType,
                            LotSize = 1,
                            LastUpdate = DateTime.Now
                        };
                        updatedPositions.Add(position);
                        Debug.WriteLine($"Позиция обновлена (без кэша): {pos.InstrumentUid} - {position.Quantity}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка обработки позиции: {ex.Message}");
                }
            }

            // Обновляем список позиций
            lock (_positionsLock)
            {
                _positions.Clear();
                _positions.AddRange(updatedPositions);
            }

            // Вызываем событие обновления
            OnPositionsUpdated?.Invoke(updatedPositions);

            Debug.WriteLine($"Обновлено {updatedPositions.Count} позиций");
        }
        private async Task<string> GetSandboxInstrumentFigi(string ticker)
        {
            try
            {
                if (_client == null) return null;

                var headers = CreateHeaders();

                // Для акций (SBER)
                if (ticker == "SBER")
                {
                    var sharesResponse = await _client.Instruments.SharesAsync(new InstrumentsRequest
                    {
                        InstrumentStatus = InstrumentStatus.Base
                    }, headers);

                    var sber = sharesResponse.Instruments.FirstOrDefault(i =>
                        i.Ticker == "SBER" && i.ClassCode == "TQBR");

                    if (sber != null)
                    {
                        Debug.WriteLine($"Найден SBER в песочнице: FIGI={sber.Figi}, UID={sber.Uid}");
                        return sber.Figi;
                    }
                }

                // Для фьючерсов (IMOEX)
                if (ticker == "IMOEX")
                {
                    var futuresResponse = await _client.Instruments.FuturesAsync(new InstrumentsRequest
                    {
                        InstrumentStatus = InstrumentStatus.Base
                    }, headers);

                    var imoex = futuresResponse.Instruments.FirstOrDefault(i =>
                        i.Ticker.Contains("IMOEX"));

                    if (imoex != null)
                    {
                        Debug.WriteLine($"Найден IMOEX в песочнице: FIGI={imoex.Figi}, UID={imoex.Uid}");
                        return imoex.Figi;
                    }
                }

                // Если не нашли, возвращаем дефолтные значения
                return ticker == "SBER" ? STOCK_MARKET_INDICATOR : DERIVATIVES_MARKET_INDICATOR;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка поиска инструмента {ticker} в песочнице: {ex.Message}");
                return ticker == "SBER" ? STOCK_MARKET_INDICATOR : DERIVATIVES_MARKET_INDICATOR;
            }
        }
        /*private async Task InternalSubscribeToInstrument(string figi, Action<MarketData> handler)
        {
            Debug.WriteLine($"---InternalSubscribeToInstrument----->");

            try
            {
                await _streamLock.WaitAsync();

                if (_subscriptions.ContainsKey(figi))
                {
                    Debug.WriteLine($"Уже подписан на инструмент {figi}");
                    return;
                }

                // Инициализируем поток, если он еще не создан
                if (_marketDataStream == null)
                {
                    await InitializeMarketDataStreamAsync();
                }

                _subscriptions[figi] = handler;

                var subscribeRequest = new MarketDataRequest
                {
                    SubscribeInfoRequest = new SubscribeInfoRequest
                    {
                        SubscriptionAction = SubscriptionAction.Subscribe,
                        Instruments = { new InfoInstrument { Figi = figi } }
                    }
                };

                Debug.WriteLine($"Подписываемся на статус рынка по маркерному инструменту {figi}");

                await _marketDataStream!.RequestStream.WriteAsync(subscribeRequest);

                _logger.LogInformation("Подписка на инструмент {Figi} выполнена успешно", figi);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка подписки на инструмент {Figi}", figi);
                throw;
            }
            finally
            {
                _streamLock.Release();
            }
        }*/
        // Обработчики статусов рынков
        private void ProcessStockMarketStatus(MarketData data)
        {
            // ИСПРАВЛЕНИЕ: Проверяем наличие данных перед обновлением
            if (string.IsNullOrEmpty(data.TradingStatus) && data.IsTrading == false)
            {
                //Debug.WriteLine($"Пропускаем ProcessStockMarketStatus - нет данных о торгах");
                return;
            }

            //Debug.WriteLine($"ProcessStockMarketStatus вызван. Статус: {data.TradingStatus}, Торги: {data.IsTrading}");
            UpdateMarketStatus("Фондовый рынок MOEX", data);
        }

        private void ProcessDerivativesMarketStatus(MarketData data)
        {
            // ИСПРАВЛЕНИЕ: Проверяем наличие данных перед обновлением
            if (string.IsNullOrEmpty(data.TradingStatus) && data.IsTrading == false)
            {
                //Debug.WriteLine($"Пропускаем ProcessDerivativesMarketStatus - нет данных о торгах");
                return;
            }

            Debug.WriteLine($"ProcessDerivativesMarketStatus вызван. Статус: {data.TradingStatus}, Торги: {data.IsTrading}");
            UpdateMarketStatus("Срочный рынок MOEX", data);
        }

        public void UpdateMarketStatus(string marketName, MarketData data)
        {
            try
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  UpdateMarketStatus: Обновление статуса для {marketName}: Статус='{data.TradingStatus}', IsTrading={data.IsTrading}");

                // ИСПРАВЛЕНИЕ: Проверяем, есть ли данные о торгах
                // Если статус пустой, не обновляем
                if (string.IsNullOrEmpty(data.TradingStatus) && data.IsTrading == false)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  UpdateMarketStatus:Пропускаем обновление статуса для {marketName} - нет данных о торгах");
                    return;
                }

                var statusText = data.IsTrading ? "Торги идут" : "Торги остановлены";

                // Если пришел конкретный статус от Tinkoff, используем его
                if (!string.IsNullOrEmpty(data.TradingStatus) && data.TradingStatus != "Unspecified")
                {
                    statusText = data.TradingStatus;
                }

                var statuses = new List<MarketStatus>
        {
            new MarketStatus
            {
                Name = marketName,
                Status = statusText,
                IsTrading = data.IsTrading,
                LastUpdate = DateTime.Now
            }
        };

                // Вызываем событие для обновления UI
                OnMarketStatusesUpdated?.Invoke(statuses);

                Debug.WriteLine($"DEBUG: TinkoffApiService:  UpdateMarketStatus:Статус рынка обновлен: {marketName} - {statusText} (Торги: {data.IsTrading})");
                _logger.LogDebug("DEBUG: TinkoffApiService:  UpdateMarketStatus:Статус рынка обновлен: {MarketName} - {Status}",
                    marketName, statusText);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  UpdateMarketStatus:Ошибка в UpdateMarketStatus: {ex.Message}");
                _logger.LogError(ex, "Ошибка обновления статуса рынка");
            }
        }

        public async Task UpdateMarketStatusesAsync()
        {
            try
            {
                Debug.WriteLine("DEBUG: TinkoffApiService:  UpdateMarketStatusesAsync: Принудительное обновление статусов рынков...");

                // НЕ сбрасываем флаг подписки - только проверяем текущее состояние
                bool needResubscribe;
                lock (_marketStatusLock)
                {
                    needResubscribe = !_marketStatusSubscribed;
                }

                // Подписываемся только если еще не подписаны
                if (needResubscribe)
                {
                    await SubscribeToMarketStatusIndicators();
                }
                else
                {
                    Debug.WriteLine("Уже подписаны на статусы рынков, пропускаем повторную подписку");
                }

                Debug.WriteLine("DEBUG: TinkoffApiService:  UpdateMarketStatusesAsync: Обновление статусов рынков завершено");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  UpdateMarketStatusesAsync: Ошибка при обновлении статусов рынков: {ex.Message}");
                throw;
            }
        }


        private async Task InitializeMarketDataStreamAsync()
        {
            try
            {
                Debug.WriteLine("Инициализация потока рыночных данных...");

                if (_client == null)
                    throw new InvalidOperationException("Клиент не инициализирован");

                // Используем метод с заголовками
                _marketDataStream = CreateMarketDataStreamWithHeaders();
                _streamCts = new CancellationTokenSource();
                _streamProcessingTask = ProcessMarketDataStreamAsync(_streamCts.Token);

                Debug.WriteLine("Поток рыночных данных успешно инициализирован");
                _logger.LogInformation("Поток рыночных данных инициализирован");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка инициализации потока: {ex.Message}");
                _logger.LogError(ex, "Ошибка инициализации потока рыночных данных");
                throw;
            }
        }

        /// <summary>
        /// Отписка от рыночных данных с корректной ссылочной системой
        /// </summary>
        public async Task UnsubscribeFromMarketDataAsync(string instrumentId)
        {
            if (string.IsNullOrEmpty(instrumentId))
                return;

            Debug.WriteLine($"[UnsubscribeFromMarketDataAsync] Запрос отписки от {instrumentId}");

            lock (_subscriptionLock)
            {
                string key = instrumentId;

                if (!_marketDataSubscriptionRefCount.ContainsKey(key))
                {
                    Debug.WriteLine($"[UnsubscribeFromMarketDataAsync] Подписка {key} не найдена");
                    return;
                }

                // Уменьшаем счетчик
                _marketDataSubscriptionRefCount[key]--;

                Debug.WriteLine($"[UnsubscribeFromMarketDataAsync] Подписка {key}: счетчик = {_marketDataSubscriptionRefCount[key]}");

                // Если счетчик стал 0, отписываемся реально
                if (_marketDataSubscriptionRefCount[key] == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool lockTaken = await _streamLock.WaitAsync(TimeSpan.FromSeconds(5));
                            if (!lockTaken)
                            {
                                Debug.WriteLine($"[UnsubscribeFromMarketDataAsync] ⏰ Timeout ожидания блокировки для {instrumentId}");
                                return;
                            }

                            try
                            {
                                if (_marketDataStream != null && _subscriptions.ContainsKey(instrumentId))
                                {
                                    var unsubscribeRequest = new MarketDataRequest
                                    {
                                        SubscribeInfoRequest = new SubscribeInfoRequest
                                        {
                                            SubscriptionAction = SubscriptionAction.Unsubscribe,
                                            Instruments = { new InfoInstrument { InstrumentId = instrumentId } }
                                        }
                                    };

                                    await _marketDataStream.RequestStream.WriteAsync(unsubscribeRequest);
                                    _subscriptions.Remove(instrumentId);

                                    Debug.WriteLine($"[UnsubscribeFromMarketDataAsync] ✅ Реальная отписка от {instrumentId} выполнена");
                                    _logger.LogInformation("Отписка от инструмента {InstrumentId}", instrumentId);
                                }
                            }
                            finally
                            {
                                _streamLock.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[UnsubscribeFromMarketDataAsync] ❌ Ошибка отписки от {instrumentId}: {ex.Message}");
                            _logger.LogError(ex, "Ошибка отписки от инструмента {InstrumentId}", instrumentId);
                        }
                    });

                    // Удаляем запись о подписке
                    _marketDataSubscriptionRefCount.Remove(key);
                    _marketDataCallbacks.Remove(key);
                }
                else
                {
                    Debug.WriteLine($"[UnsubscribeFromMarketDataAsync] 📌 Подписка {key} все еще используется ({_marketDataSubscriptionRefCount[key]} стратегий)");
                }
            }
        }

        /// <summary>
        /// Очищает все подписки (вызывается при полном отключении)
        /// </summary>
        private async Task ClearAllSubscriptionsAsync()
        {
            lock (_subscriptionLock)
            {
                // Очищаем счетчики
                _candleSubscriptionRefCount.Clear();
                _candleCallbacks.Clear();
                _marketDataSubscriptionRefCount.Clear();
                _marketDataCallbacks.Clear();

                Debug.WriteLine($"[ClearAllSubscriptionsAsync] Все счетчики подписок очищены");
            }

            // Даем время на завершение операций
            await Task.CompletedTask;
        }








        private async Task ProcessMarketDataStreamAsync(CancellationToken cancellationToken)
        {
            int reconnectAttempts = 0;
            const int maxReconnectAttempts = 10;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService: ProcessMarketDataStreamAsync: [{DateTime.Now:HH:mm:ss.fff}] Запуск потока данных");

                    // Пересоздаем стрим если нужно
                    if (_marketDataStream == null || _streamCts == null || _streamCts.IsCancellationRequested)
                    {
                        await _streamLock.WaitAsync(cancellationToken);
                        try
                        {
                            await StopMarketDataStreamAsync();
                            await InitializeMarketDataStreamAsync();
                        }
                        finally
                        {
                            _streamLock.Release();
                        }
                    }

                    if (_marketDataStream == null)
                    {
                        throw new InvalidOperationException("Не удалось создать стрим");
                    }

                    // Читаем данные
                    await foreach (var response in _marketDataStream!.ResponseStream.ReadAllAsync(cancellationToken))
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        ProcessMarketDataResponse(response);
                        reconnectAttempts = 0; // Сброс счетчика при успешном чтении
                    }
                }
                catch (Exception ex) when (ex is RpcException || ex is IOException || ex is HttpRequestException)
                {
                    /// Сетевая ошибка
                    reconnectAttempts++;

                    // ПРИ РАЗРЫВЕ СОЕДИНЕНИЯ ОБНОВЛЯЕМ СТАТУСЫ РЫНКОВ
                    try
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService: ProcessMarketDataStreamAsync: [{DateTime.Now:HH:mm:ss.fff}] Потеря соединения. Обновляем статусы рынков...");

                        // Обновляем статусы рынков на "Нет данных"
                        var statuses = new List<MarketStatus>
                        {
                            new MarketStatus
                            {
                                Name = "Фондовый рынок MOEX",
                                Status = "Нет данных",
                                IsTrading = false,
                                LastUpdate = DateTime.Now
                            },
                            new MarketStatus
                            {
                                Name = "Срочный рынок MOEX",
                                Status = "Нет данных",
                                IsTrading = false,
                                LastUpdate = DateTime.Now
                            }
                        };

                        OnMarketStatusesUpdated?.Invoke(statuses);

                        // Сбрасываем флаг подписки на статусы
                        /*lock (_marketStatusLock)
                        {
                            _marketStatusSubscribed = false;
                        }*/
                    }
                    catch (Exception statusEx)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService: ProcessMarketDataStreamAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка обновления статусов при разрыве: {statusEx.Message}");
                    }



                    await Task.Delay(3000, cancellationToken);

                    if (reconnectAttempts > maxReconnectAttempts)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService: ProcessMarketDataStreamAsync: [{DateTime.Now:HH:mm:ss.fff}] Достигнут лимит попыток: {maxReconnectAttempts}");
                        break;
                    }

                    Debug.WriteLine($"DEBUG: TinkoffApiService: ProcessMarketDataStreamAsync: [{DateTime.Now:HH:mm:ss.fff}] Потеря соединения. Попытка {reconnectAttempts}/{maxReconnectAttempts}");
                    _connectionManager?.NotifyConnectionLost();



                    // Ждем перед повторной попыткой
                    var delaySeconds = Math.Min(60, Math.Pow(10, reconnectAttempts));
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ждем {delaySeconds} секунд...");

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Пробуем переподключиться
                    try
                    {
                        var reconnected = await ReconnectAsync();
                        if (reconnected)
                        {
                            reconnectAttempts = 0;
                            Debug.WriteLine($"DEBUG: TinkoffApiService: ProcessMarketDataStreamAsync: [{DateTime.Now:HH:mm:ss.fff}] Переподключение успешно");



                        }
                    }
                    catch (Exception reconnectEx)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService: ProcessMarketDataStreamAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка переподключения: {reconnectEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService: ProcessMarketDataStreamAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка в потоке: {ex.Message}");

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        reconnectAttempts++;
                        await Task.Delay(5000, cancellationToken);
                    }
                }
            }

            
        }
        /*private async Task ReconnectMarketDataStreamAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || _client == null)
                return;

            await _streamLock.WaitAsync(cancellationToken);
            try
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Запуск переподключения...");

                // 1. Проверяем соединение клиента
                if (!await TestConnectionAsync())
                {
                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Клиент не подключен, требуется полное переподключение");

                    // Пробуем переподключить весь клиент
                    await ReconnectClientAsync();
                }

                // 2. Останавливаем старый поток
                await StopMarketDataStreamAsync();

                // 3. Создаем новый поток
                await InitializeMarketDataStreamAsync();

                // 4. Восстанавливаем подписки
                await RestoreAllSubscriptionsAsync();

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Переподключение завершено успешно");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка переподключения: {ex.Message}");
                throw;
            }
            finally
            {
                _streamLock.Release();
            }
        }*/
        /*private async Task ReconnectClientAsync()
        {
            try
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Переподключение клиента...");

                var wasSandbox = _isSandbox;
                var tokens = _tokenManager.LoadProviderTokens("Тинькофф");
                var token = wasSandbox ? tokens.SandboxToken : tokens.RealToken;

                if (!string.IsNullOrEmpty(token))
                {
                    _token = token;

                    // Полностью пересоздаем соединение
                    if (_channel != null)
                    {
                        try
                        {
                            await _channel.ShutdownAsync();
                        }
                        catch { }
                        _channel = null;
                    }

                    _client = null;

                    // Переподключаемся
                    var success = await ConnectAsync(wasSandbox);
                    if (!success)
                    {
                        throw new Exception("Не удалось переподключить клиент");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка переподключения клиента: {ex.Message}");
                throw;
            }
        }*/
        private async Task RestoreAllSubscriptionsAsync()
        {
            try
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Восстановление всех подписок");

                // СБРАСЫВАЕМ флаг подписки на статусы рынков перед восстановлением
                /*lock (_marketStatusLock)
                {
                    _marketStatusSubscribed = false;
                }*/

                Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Флаг подписки на статусы сброшен, начинаем восстановление");

                // Восстанавливаем подписки на статусы рынков
                Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Восстановление подписок на статусы рынков");
                await SubscribeToMarketStatusIndicators();

                // Проверяем, установился ли флаг после восстановления
                lock (_marketStatusLock)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Флаг _marketStatusSubscribed после восстановления: {_marketStatusSubscribed}");
                }

                // Восстанавливаем подписки на свечи
                /*if (_activeCandleSubscriptions.Any())
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Восстановление {_activeCandleSubscriptions.Count} подписок на свечи");

                    foreach (var subscription in _activeCandleSubscriptions.Values.ToList())
                    {
                        try
                        {
                            // Отписываемся от старых подписок
                            await UnsubscribeFromCandlesAsync(subscription.instrumentId, subscription.timeframe);

                            // Подписываемся заново
                            await SubscribeToCandlesAsync(
                                subscription.instrumentId,
                                subscription.timeframe,
                                subscription.callback);

                            Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Подписка восстановлена: {subscription.instrumentId}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка восстановления подписки: {ex.Message}");
                        }
                    }
                }*/

                Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Все подписки восстановлены");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  RestoreAllSubscriptionsAsync: [{DateTime.Now:HH:mm:ss.fff}] Ошибка восстановления подписок: {ex.Message}");
                throw;
            }
        }
        /*private async Task<bool> TestConnectionAsync()
        {
            try
            {
                if (_client == null) return false;

                var headers = CreateHeaders();
                var response = await _client.Users.GetAccountsAsync(new GetAccountsRequest(), headers);
                return response != null;
            }
            catch
            {
                return false;
            }
        }*/

        /*private async Task InitializeClientAsync()
        {
            try
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Инициализация клиента...");

                // Создаем канал с настройками для лучшей стабильности
                var channelOptions = new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                        KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                        KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                        EnableMultipleHttp2Connections = true,
                        ConnectTimeout = TimeSpan.FromSeconds(30)
                    },
                    DisposeHttpClient = true
                };

                var address = _isSandbox ?
                    "https://sandbox-invest-public-api.tinkoff.ru:443" :
                    "https://invest-public-api.tinkoff.ru:443";

                _channel = GrpcChannel.ForAddress(address, channelOptions);

                // Создаем клиент через CallInvoker
                var callInvoker = _channel.CreateCallInvoker();
                _client = new InvestApiClient(callInvoker);

                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Клиент успешно инициализирован");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка инициализации клиента: {ex.Message}");
                throw;
            }
        }*/

        public async Task<bool> CheckConnectionAsync()
        {
            try
            {
                if (_client == null) return false;

                var headers = CreateHeaders();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var response = await _client.Users.GetAccountsAsync(
                    new GetAccountsRequest(),
                    headers,
                    cancellationToken: cts.Token);

                return response != null;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("DEBUG: TinkoffApiService:  Проверка соединения отменена по таймауту");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка проверки соединения: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// полная реализация ProcessMarketDataResponse с обработкой всех типов данных из стрима
        /// </summary>
        /// <param name="response"></param>
        private void ProcessMarketDataResponse(MarketDataResponse response)
        {
            try
            {
                // Отладочная информация о полученном ответе
                //Debug.WriteLine($"DEBUG: TinkoffApiService:  Получен MarketDataResponse. Типы данных:");
                //if (response?.LastPrice != null) Debug.WriteLine($"  - LastPrice");
                //if (response?.Candle != null) Debug.WriteLine($"  - Candle");
                //if (response?.TradingStatus != null) Debug.WriteLine($"  - TradingStatus");
                //if (response?.Ping != null) Debug.WriteLine($"  - Ping");
                //if (response?.Orderbook != null) Debug.WriteLine($"  - Orderbook");
                //if (response?.SubscribeInfoResponse != null) Debug.WriteLine($"  - SubscribeInfoResponse");
                //if (response?.SubscribeCandlesResponse != null) Debug.WriteLine($"  - SubscribeCandlesResponse");
                //if (response?.SubscribeLastPriceResponse != null) Debug.WriteLine($"  - SubscribeLastPriceResponse");
                //if (response?.SubscribeOrderBookResponse != null) Debug.WriteLine($"  - SubscribeOrderBookResponse");
                //if (response?.SubscribeTradesResponse != null) Debug.WriteLine($"  - SubscribeTradesResponse");


                // 1. Обработка последних цен (LastPrice)
                if (response?.LastPrice != null)
                {
                    var lastPrice = response.LastPrice;
                    var instrumentId = lastPrice.InstrumentUid;
                    var figi = lastPrice.Figi;
                    var price = ConvertQuotationToDecimal(lastPrice.Price);
                    var time = lastPrice.Time.ToDateTime();

                    // Сохраняем соответствие UID-FIGI
                    if (!string.IsNullOrEmpty(instrumentId) && !string.IsNullOrEmpty(figi))
                    {
                        _uidToFigiMap[instrumentId] = figi;
                    }

                    // ✅ ИСПРАВЛЕНИЕ: Обработка для обычных подписок на рыночные данные
                    Action<MarketData> marketDataCallback = null;

                    // Сначала проверяем по instrumentId
                    if (_subscriptions.TryGetValue(instrumentId, out marketDataCallback))
                    {
                        var marketData = new MarketData
                        {
                            InstrumentId = instrumentId,
                            LastPrice = price,
                            Timestamp = time
                        };

                        try
                        {
                            marketDataCallback(marketData);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в MarketData callback для {instrumentId}: {ex.Message}");
                        }
                    }
                    // Затем проверяем по figi
                    else if (figi != null && _subscriptions.TryGetValue(figi, out marketDataCallback))
                    {
                        var marketData = new MarketData
                        {
                            InstrumentId = instrumentId,
                            LastPrice = price,
                            Timestamp = time
                        };

                        try
                        {
                            marketDataCallback(marketData);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в MarketData callback для {figi}: {ex.Message}");
                        }
                    }

                    // Обработка для подписок на свечи
                    Action<CandleUpdate> candleCallback = null;
                    if (_candleSubscriptions.TryGetValue(instrumentId, out candleCallback) ||
                        (figi != null && _candleSubscriptions.TryGetValue(figi, out candleCallback)))
                    {
                        var update = new CandleUpdate
                        {
                            InstrumentId = instrumentId,
                            LastPrice = price,
                            Time = time,
                            IsComplete = false
                        };

                        // Получаем последние данные свечи из кэша
                        if (_lastCandleUpdates.TryGetValue(instrumentId, out var lastCandle))
                        {
                            var (lastTime, lastOpen, lastHigh, lastLow, lastClose, volume) = lastCandle;

                            update.Open = lastOpen;
                            update.High = Math.Max(lastHigh, price);
                            update.Low = Math.Min(lastLow, price);
                            update.Close = price;
                            update.Volume = 0;
                        }
                        else
                        {
                            update.Open = price;
                            update.High = price;
                            update.Low = price;
                            update.Close = price;
                            update.Volume = 0;
                        }

                        try
                        {
                            candleCallback(update);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в LastPrice callback для {instrumentId}: {ex.Message}");
                        }
                    }
                }

                // 2. Обработка свечей (Candle)
                if (response?.Candle != null)
                {
                    var candle = response.Candle;
                    var instrumentId = candle.InstrumentUid;
                    var figi = candle.Figi;
                    var time = candle.Time.ToDateTime();
                    var lastTradeTime = candle.LastTradeTs?.ToDateTime() ?? time;

                    var open = SafeConvertQuotation(candle.Open);
                    var high = SafeConvertQuotation(candle.High);
                    var low = SafeConvertQuotation(candle.Low);
                    var close = SafeConvertQuotation(candle.Close);
                    var volume = (long)candle.Volume; // ОБЪЕМ ИЗ СВЕЧИ
                    var interval = candle.Interval.ToString();

                    //Debug.WriteLine($"DEBUG: TinkoffApiService:   ProcessMarketDataResponse  interval={candle.Interval} ");


                    // КОНВЕРТИРУЕМ ТАЙМФРЕЙМ TINKOFF В НАШ ФОРМАТ
                    var tinkoffInterval = candle.Interval.ToString();
                    var normalizedTimeframe = ConvertTinkoffIntervalToString(tinkoffInterval);

                    //Debug.WriteLine($"DEBUG: TinkoffApiService:   ProcessMarketDataResponse  normalizedTimeframe={normalizedTimeframe} ");





                    // Определяем, завершена ли свеча
                    // Свеча считается завершенной, если с ее начала времени прошло больше, чем длительность интервала
                    var intervalMinutes = GetIntervalMinutes(candle.Interval);
                    var timeSinceCandleStart = DateTime.UtcNow - time;
                    var isComplete = timeSinceCandleStart.TotalMinutes >= intervalMinutes;

                    //Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Свеча {interval} для {instrumentId}: " +
                    //      $"O={open}, H={high}, L={low}, C={close}, V={volume}, Time={time:HH:mm:ss}, " +
                     //     $"LastTrade={lastTradeTime:HH:mm:ss}, IsComplete={isComplete}");

                    // Сохраняем соответствие UID-FIGI
                    if (!string.IsNullOrEmpty(instrumentId) && !string.IsNullOrEmpty(figi))
                    {
                        _uidToFigiMap[instrumentId] = figi;
                    }

                    // Обновляем кэш последних свечей
                    _lastCandleUpdates[instrumentId] = (time, open, high, low, close, volume);

                    // Обработка для подписок на свечи
                    if (_candleSubscriptions.TryGetValue(instrumentId, out var candleCallback) ||
                        (figi != null && _candleSubscriptions.TryGetValue(figi, out candleCallback)))
                    {
                        // При создании CandleUpdate используем нормализованный таймфрейм
                        var update = new CandleUpdate
                        {
                            InstrumentId = instrumentId,
                            LastPrice = close,
                            Open = open,
                            High = high,
                            Low = low,
                            Close = close,
                            Volume = volume,
                            Time = time,
                            IsComplete = isComplete,
                            Timeframe = normalizedTimeframe, // ← ИСПОЛЬЗУЕМ НОРМАЛИЗОВАННЫЙ ТАЙМФРЕЙМ
                            LastTradeTime = lastTradeTime
                        };

                        try
                        {
                            candleCallback(update);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в Candle callback для {instrumentId}: {ex.Message}");
                        }
                    }

                    // Для завершенных свечей также отправляем в обычные подписки
                    if (isComplete)
                    {
                        if (_subscriptions.TryGetValue(instrumentId, out var marketDataCallback) ||
                            (figi != null && _subscriptions.TryGetValue(figi, out marketDataCallback)))
                        {
                            var marketData = new MarketData
                            {
                                InstrumentId = instrumentId,
                                LastPrice = close,
                                CandleOpen = open,
                                CandleHigh = high,
                                CandleLow = low,
                                CandleClose = close,
                                CandleVolume = volume,
                                CandleTime = time,
                                CandleIsComplete = isComplete,
                                Timestamp = DateTime.Now
                            };

                            try
                            {
                                marketDataCallback(marketData);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в завершенной свече callback для {instrumentId}: {ex.Message}");
                            }
                        }
                    }
                    
                }
                
                // 3. Обработка статусов торгов (TradingStatus)
                if (response?.TradingStatus != null)
                {
                    Debug.WriteLine($"------------------------------------- TradingStatus для {response.TradingStatus.TradingStatus_}: , Торги: ");

                    var tradingStatus = response.TradingStatus;
                    var instrumentId = tradingStatus.InstrumentUid;
                    var figi = tradingStatus.Figi;
                    var status = tradingStatus.TradingStatus_;

                    var isTrading = status == SecurityTradingStatus.NormalTrading ||
                                    status == SecurityTradingStatus.OpeningPeriod ||
                                    status == SecurityTradingStatus.ClosingPeriod ||
                                    status == SecurityTradingStatus.OpeningAuctionPeriod ||
                                    status == SecurityTradingStatus.SessionOpen ||
                                    status == SecurityTradingStatus.ClosingAuction;

                    // Дополнительная проверка: если статус не установлен, не обновляем
                    if (status == 0) // 0 = SecurityTradingStatus.Unspecified
                    {
                        Debug.WriteLine($"Пропускаем TradingStatus с пустым статусом для {instrumentId}");
                        return;
                    }

                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] TradingStatus для {instrumentId} (FIGI: {figi}): {status}, Торги: {isTrading}");

                    // Сохраняем соответствие UID-FIGI
                    if (!string.IsNullOrEmpty(instrumentId) && !string.IsNullOrEmpty(figi))
                    {
                        _uidToFigiMap[instrumentId] = figi;
                    }

                    // Проверяем, подписан ли кто-то на этот инструмент
                    Action<MarketData> callback = null;
                    string subscriptionKey = null;

                    // Сначала ищем прямой callback по instrumentId
                    if (_subscriptions.TryGetValue(instrumentId, out callback))
                    {
                        subscriptionKey = instrumentId;
                    }
                    // Затем ищем по figi
                    else if (figi != null && _subscriptions.TryGetValue(figi, out callback))
                    {
                        subscriptionKey = figi;
                    }
                    // Затем проверяем через маппинг UID->FIGI
                    else if (_uidToFigiMap.TryGetValue(instrumentId, out var mappedFigi) &&
                             _subscriptions.TryGetValue(mappedFigi, out callback))
                    {
                        subscriptionKey = mappedFigi;
                    }

                    if (callback != null)
                    {
                        var marketData = new MarketData
                        {
                            InstrumentId = instrumentId,
                            TradingStatus = status.ToString(),
                            IsTrading = isTrading,
                            Timestamp = DateTime.Now
                        };

                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Найдена подписка для {instrumentId} по ключу {subscriptionKey}");
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Вызываем callback для статуса торгов: {status}");

                        try
                        {
                            callback(marketData);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в TradingStatus callback для {instrumentId}: {ex.Message}");
                        }
                    }
                    else
                    {
                        // Проверяем, является ли это маркерным инструментом для статусов рынков
                        bool isStockMarketIndicator = false;
                        bool isDerivativesMarketIndicator = false;

                        // Проверяем по FIGI
                        if (figi == STOCK_MARKET_INDICATOR || instrumentId == STOCK_MARKET_INDICATOR)
                        {
                            isStockMarketIndicator = true;
                        }
                        else if (figi == DERIVATIVES_MARKET_INDICATOR || instrumentId == DERIVATIVES_MARKET_INDICATOR)
                        {
                            isDerivativesMarketIndicator = true;
                        }
                        // Проверяем по UID через маппинг
                        else if (_uidToFigiMap.TryGetValue(instrumentId, out var mappedFigi2))
                        {
                            if (mappedFigi2 == STOCK_MARKET_INDICATOR)
                            {
                                isStockMarketIndicator = true;
                            }
                            else if (mappedFigi2 == DERIVATIVES_MARKET_INDICATOR)
                            {
                                isDerivativesMarketIndicator = true;
                            }
                        }

                        if (isStockMarketIndicator || isDerivativesMarketIndicator)
                        {
                            var marketName = isStockMarketIndicator ? "Фондовый рынок MOEX" : "Срочный рынок MOEX";

                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Это маркерный инструмент для {marketName}, обновляем статус напрямую");

                            UpdateMarketStatus(marketName, new MarketData
                            {
                                InstrumentId = instrumentId,
                                TradingStatus = status.ToString(),
                                IsTrading = isTrading,
                                Timestamp = DateTime.Now
                            });
                        }
                        else
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Нет подписок для TradingStatus {instrumentId} (FIGI: {figi})");
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Все подписки: {string.Join(", ", _subscriptions.Keys)}");
                        }
                    }
                }

                // 4. Обработка стакана (Orderbook)
                if (response?.Orderbook != null)
                {
                    var orderbook = response.Orderbook;
                    var instrumentId = orderbook.InstrumentUid;
                    var figi = orderbook.Figi;
                    var depth = orderbook.Depth;
                    var bids = orderbook.Bids.Select(b => ConvertQuotationToDecimal(b.Price)).ToList();
                    var asks = orderbook.Asks.Select(a => ConvertQuotationToDecimal(a.Price)).ToList();
                    var time = orderbook.Time.ToDateTime();

                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Orderbook {depth} для {instrumentId}: " +
                                  $"Bids={bids.Count}, Asks={asks.Count}, Time={time:HH:mm:ss}");

                    // Для подписок на рыночные данные
                    if (_subscriptions.TryGetValue(instrumentId, out var callback) ||
                        (figi != null && _subscriptions.TryGetValue(figi, out callback)))
                    {
                        var marketData = new MarketData
                        {
                            InstrumentId = instrumentId,
                            OrderBookDepth = depth,
                            OrderBookTime = time,
                            OrderBookBids = bids,
                            OrderBookAsks = asks,
                            Timestamp = DateTime.Now
                        };

                        try
                        {
                            callback(marketData);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в Orderbook callback для {instrumentId}: {ex.Message}");
                        }
                    }
                }

                // 5. Обработка тиков (Trades)
                if (response?.Trade != null)
                {
                    var trade = response.Trade;
                    var instrumentId = trade.InstrumentUid;
                    var figi = trade.Figi;
                    var price = ConvertQuotationToDecimal(trade.Price);
                    var quantity = trade.Quantity; // ОБЪЕМ ИЗ ТИКА
                    var direction = trade.Direction.ToString();
                    var time = trade.Time.ToDateTime();

                    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Trade для {instrumentId}: " +
                            $"Price={price}, Qty={quantity}, Dir={direction}, Time={time:HH:mm:ss}");

                    // Для подписок на рыночные данные
                    if (_subscriptions.TryGetValue(instrumentId, out var callback) ||
                        (figi != null && _subscriptions.TryGetValue(figi, out callback)))
                    {
                        var marketData = new MarketData
                        {
                            InstrumentId = instrumentId,
                            LastPrice = price,
                            TradeQuantity = quantity,
                            TradeDirection = direction,
                            TradeTime = time,
                            Timestamp = DateTime.Now
                        };

                        try
                        {
                            callback(marketData);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в Trade callback для {instrumentId}: {ex.Message}");
                        }
                    }

                    // Для подписок на свечи (обновление объема)
                    if (_candleSubscriptions.TryGetValue(instrumentId, out var candleCallback) ||
                        (figi != null && _candleSubscriptions.TryGetValue(figi, out candleCallback)))
                    {
                        var update = new CandleUpdate
                        {
                            InstrumentId = instrumentId,
                            LastPrice = price,
                            Volume = quantity, // ПЕРЕДАЕМ ОБЪЕМ ИЗ ТИКА
                            Time = time,
                            IsComplete = false
                        };

                        try
                        {
                            candleCallback(update);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Ошибка в Trade->Candle callback для {instrumentId}: {ex.Message}");
                        }
                    }
                }

                // 6. Обработка ответов на подписки
                if (response?.SubscribeInfoResponse != null)
                {
                    var subResponse = response.SubscribeInfoResponse;
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  SubscribeInfoResponse: TrackingId={subResponse.TrackingId}");
                }

                if (response?.SubscribeCandlesResponse != null)
                {
                    var subResponse = response.SubscribeCandlesResponse;
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  SubscribeCandlesResponse: TrackingId={subResponse.TrackingId}");

                    foreach (var candleSubscription in subResponse.CandlesSubscriptions)
                    {
                        Debug.WriteLine($"  Инструмент: {candleSubscription.InstrumentUid}, " +
                                      $"FIGI: {candleSubscription.Figi}, " +
                                      $"Статус: {candleSubscription.SubscriptionStatus}");
                    }
                }

                if (response?.SubscribeLastPriceResponse != null)
                {
                    var subResponse = response.SubscribeLastPriceResponse;
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  SubscribeLastPriceResponse: TrackingId={subResponse.TrackingId}");

                    foreach (var lastPriceSubscription in subResponse.LastPriceSubscriptions)
                    {
                        Debug.WriteLine($"  Инструмент: {lastPriceSubscription.InstrumentUid}, " +
                                      $"FIGI: {lastPriceSubscription.Figi}, " +
                                      $"Статус: {lastPriceSubscription.SubscriptionStatus}");
                    }
                }

                // 7. Обработка пинга
                if (response?.Ping != null)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  Ping received: TrackingId={response.Ping.PingRequestTime}, " +
                                  $"Time={response.Ping.Time.ToDateTime():HH:mm:ss}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки ответа рыночных данных");
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Критическая ошибка в ProcessMarketDataResponse: {ex.Message}");
                Debug.WriteLine($"DEBUG: TinkoffApiService:  StackTrace: {ex.StackTrace}");
            }
        }

        // Улучшенный метод переподключения
        /*private async Task TryReconnectMarketDataStreamAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || _client == null)
                return;

            try
            {
                await _streamLock.WaitAsync(cancellationToken);

                // Сохраняем текущие подписки
                var currentSubscriptions = new Dictionary<string, Action<MarketData>>(_subscriptions);

                // Останавливаем старый поток
                await StopMarketDataStreamAsync();

                // Создаем новый поток
                await InitializeMarketDataStreamAsync();

                // Восстанавливаем подписки
                foreach (var subscription in currentSubscriptions)
                {
                    var subscribeRequest = new MarketDataRequest
                    {
                        SubscribeInfoRequest = new SubscribeInfoRequest
                        {
                            SubscriptionAction = SubscriptionAction.Subscribe,
                            Instruments = { new InfoInstrument { InstrumentId = subscription.Key } }
                        }
                    };
                    await _marketDataStream!.RequestStream.WriteAsync(subscribeRequest);
                }

                _logger.LogInformation("Поток рыночных данных успешно переподключен");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка переподключения потока рыночных данных");
            }
            finally
            {
                _streamLock.Release();
            }
        }*/

        private async Task StopMarketDataStreamAsync()
        {
            try
            {
                // Отменяем все операции через 3 секунды
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

                if (_streamCts != null)
                {
                    _streamCts.Cancel();
                    await Task.Delay(100, cts.Token); // Короткая задержка
                    if (_streamCts != null)
                    {
                        _streamCts.Dispose();
                    }
                        
                    _streamCts = null;
                }

                if (_marketDataStream != null)
                {
                    try
                    {
                        // Пытаемся корректно закрыть стрим
                        await _marketDataStream.RequestStream.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка завершения RequestStream: {ex.Message}");
                    }
                    finally
                    {
                        _marketDataStream.Dispose();
                        _marketDataStream = null;
                    }
                }

                if (_streamProcessingTask != null)
                {
                    try
                    {
                        // Ждем завершения с таймаутом
                        await _streamProcessingTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Timeout ожидания завершения потока");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка ожидания потока: {ex.Message}");
                    }
                    _streamProcessingTask = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка остановки стрима: {ex.Message}");
            }
        }





        private AsyncDuplexStreamingCall<MarketDataRequest, MarketDataResponse> CreateMarketDataStreamWithHeaders()
        {
            if (_client == null)
                throw new InvalidOperationException("Клиент не инициализирован");

            // Создаем заголовки с токеном
            var headers = CreateHeaders();

            // Создаем вызов с заголовками
            var callOptions = new CallOptions(headers: headers);

            // Создаем стрим с заголовками авторизации
            return _client.MarketDataStream.MarketDataStream(callOptions);
        }


        #region Загрузка исторических данных
        public async Task<List<Models.Candle>> GetHistoricalDataAsync(string tiker, string instrumentUid, string timeframe, DateTime startTime, DateTime endTime)
        {

            //Debug.WriteLine($"DEBUG5: ---------------------------------------------------- {instrumentUid}, timeframe: {timeframe}");



           /* if (string.IsNullOrEmpty(instrumentUid) || _tinkoffService == null)
            {
                Debug.WriteLine("DEBUG: TinkoffApiService:  InstrumentUid is null or empty OR TinkoffService is null");
                return new List<Models.Candle>();
            }*/

            try
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Loading historical data for {tiker}, timeframe: {timeframe}, from {startTime} to {endTime}");

                var interval = ConvertTimeframeToInterval(timeframe);
                var allCandles = new List<Models.Candle>();

                // Разбиваем диапазон на части по 1 дню
                var currentStart = startTime;

                while (currentStart < endTime)
                {
                    var currentEnd = currentStart.AddDays(1);
                    if (currentEnd > endTime)
                    {
                        currentEnd = endTime;
                    }

                    Debug.WriteLine($"DEBUG: TinkoffApiService:  Loading chunk for {tiker} from {currentStart} to {currentEnd}");

                    try
                    {
                        var chunkCandles = await GetHistoricalCandles(
                            instrumentUid,
                            interval,
                            currentStart,
                            currentEnd);

                        if (chunkCandles != null && chunkCandles.Any())
                        {
                            var convertedCandles = chunkCandles.Select(c => new Models.Candle
                            {
                                Time = c.Time,
                                Open = c.Open,
                                High = c.High,
                                Low = c.Low,
                                Close = c.Close,
                                Volume = c.Volume,
                                IsClosed = true
                            }).ToList();

                            allCandles.AddRange(convertedCandles);
                            Debug.WriteLine($"DEBUG: TinkoffApiService:  Loaded {chunkCandles.Count} candles for chunk");
                        }

                        // Небольшая задержка между запросами
                        await Task.Delay(100);
                    }
                    catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.InvalidArgument)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  RPC Error for chunk {currentStart}-{currentEnd}: {rpcEx.Status.Detail}");
                        // Пробуем еще меньший интервал
                        currentEnd = currentStart.AddHours(12);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Error loading chunk for {instrumentUid}: {ex.Message}");
                        // Продолжаем со следующим интервалом
                    }

                    currentStart = currentEnd;
                }

                Debug.WriteLine($"DEBUG: TinkoffApiService:  Successfully loaded {allCandles.Count} historical candles for {instrumentUid}");
                return allCandles;
            }
            catch (RpcException rpcEx)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  RPC Error loading historical data for {instrumentUid}: {rpcEx.Status.Detail}");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Error loading historical data for {instrumentUid}: {ex.Message}");
                throw;
            }
        }




        /// <summary>
        /// Получает исторические свечи для инструмента
        /// </summary>
        public async Task<List<Models.Candle>> GetHistoricalCandles(string instrumentUid, CandleInterval interval, DateTime from, DateTime to)
        {
            if (_client == null) throw new InvalidOperationException("Not connected");

            try
            {
                var headers = CreateHeaders();

                var request = new GetCandlesRequest
                {
                    InstrumentId = instrumentUid,
                    Interval = interval,
                    From = Timestamp.FromDateTime(from.ToUniversalTime()),
                    To = Timestamp.FromDateTime(to.ToUniversalTime())
                };

                var response = await _client.MarketData.GetCandlesAsync(request, headers);

                return response.Candles.Select(c => new Models.Candle
                {
                    Time = c.Time.ToDateTime(), // Время приходит в UTC
                    Open = SafeConvertQuotation(c.Open),
                    High = SafeConvertQuotation(c.High),
                    Low = SafeConvertQuotation(c.Low),
                    Close = SafeConvertQuotation(c.Close),
                    Volume = (long)c.Volume,
                    IsClosed = true
                }).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Error getting historical candles for {instrumentUid}: {ex.Message}");
                throw;
            }
        }



        #endregion

        #region Обновление последней свечи в БД
        //  метод подписки на свечи в TinkoffApiService:
        public async Task SubscribeToCandlesAsync(string instrumentId, string candleInterval, Action<CandleUpdate> onCandleUpdate)
        {
            if (_client == null)
                throw new InvalidOperationException("Клиент не подключен");

            if (string.IsNullOrEmpty(instrumentId))
                throw new ArgumentException("InstrumentId cannot be empty", nameof(instrumentId));

            if (string.IsNullOrEmpty(candleInterval))
                throw new ArgumentException("Timeframe cannot be empty", nameof(candleInterval));

            if (onCandleUpdate == null)
                throw new ArgumentNullException(nameof(onCandleUpdate));

            lock (_subscriptionLock)
            {
                string key = $"{instrumentId}_{candleInterval}";

                // Увеличиваем счетчик ссылок
                if (!_candleSubscriptionRefCount.ContainsKey(key))
                {
                    _candleSubscriptionRefCount[key] = 0;
                    _candleCallbacks[key] = onCandleUpdate;
                }

                _candleSubscriptionRefCount[key]++;

                Debug.WriteLine($"[SubscribeToCandlesAsync] Подписка {key}: счетчик = {_candleSubscriptionRefCount[key]}");

                // Если это первая подписка, реально подписываемся
                if (_candleSubscriptionRefCount[key] == 1)
                {
                    // Реальная подписка на свечи через стрим
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _streamLock.WaitAsync();

                            if (_marketDataStream == null)
                            {
                                await InitializeMarketDataStreamAsync();
                            }

                            // Добавляем в общий словарь подписок
                            if (!_candleSubscriptions.ContainsKey(instrumentId))
                            {
                                _candleSubscriptions[instrumentId] = onCandleUpdate;
                            }

                            var subscribeCandlesRequest = new MarketDataRequest
                            {
                                SubscribeCandlesRequest = new SubscribeCandlesRequest
                                {
                                    SubscriptionAction = SubscriptionAction.Subscribe,
                                    Instruments = { new CandleInstrument
                            {
                                InstrumentId = instrumentId,
                                Interval = ConvertStringToSubscriptionInterval(candleInterval)
                            } }
                                }
                            };

                            await _marketDataStream!.RequestStream.WriteAsync(subscribeCandlesRequest);

                            _activeCandleSubscriptions[instrumentId] = (instrumentId, onCandleUpdate, candleInterval);

                            Debug.WriteLine($"[SubscribeToCandlesAsync] ✅ Реальная подписка на {instrumentId} выполнена");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SubscribeToCandlesAsync] ❌ Ошибка подписки на {instrumentId}: {ex.Message}");
                        }
                        finally
                        {
                            _streamLock.Release();
                        }
                    });
                }
                else
                {
                    // Если подписка уже есть, просто добавляем колбэк в цепочку
                    var existingCallback = _candleCallbacks[key];
                    _candleCallbacks[key] = (update) =>
                    {
                        existingCallback?.Invoke(update);
                        onCandleUpdate?.Invoke(update);
                    };

                    Debug.WriteLine($"[SubscribeToCandlesAsync] 📌 Используем существующую подписку на {instrumentId}");
                }
            }
        }

        // Добавьте метод для отписки от свечей:
        public async Task UnsubscribeFromCandlesAsync(string instrumentId, string candleInterval)
        {
            if (string.IsNullOrEmpty(instrumentId))
                return;

            lock (_subscriptionLock)
            {
                string key = $"{instrumentId}_{candleInterval}";

                if (!_candleSubscriptionRefCount.ContainsKey(key))
                {
                    Debug.WriteLine($"[UnsubscribeFromCandlesAsync] Подписка {key} не найдена");
                    return;
                }

                // Уменьшаем счетчик
                _candleSubscriptionRefCount[key]--;

                Debug.WriteLine($"[UnsubscribeFromCandlesAsync] Подписка {key}: счетчик = {_candleSubscriptionRefCount[key]}");

                // Если счетчик стал 0, отписываемся реально
                if (_candleSubscriptionRefCount[key] == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool lockTaken = await _streamLock.WaitAsync(TimeSpan.FromSeconds(5));
                            if (!lockTaken)
                            {
                                Debug.WriteLine($"[UnsubscribeFromCandlesAsync] ⏰ Timeout ожидания блокировки для {instrumentId}");
                                return;
                            }

                            try
                            {
                                if (_marketDataStream != null && _candleSubscriptions.ContainsKey(instrumentId))
                                {
                                    var unsubscribeCandlesRequest = new MarketDataRequest
                                    {
                                        SubscribeCandlesRequest = new SubscribeCandlesRequest
                                        {
                                            SubscriptionAction = SubscriptionAction.Unsubscribe,
                                            Instruments = { new CandleInstrument
                                    {
                                        InstrumentId = instrumentId,
                                        Interval = ConvertStringToSubscriptionInterval(candleInterval)
                                    } }
                                        }
                                    };

                                    await _marketDataStream.RequestStream.WriteAsync(unsubscribeCandlesRequest);

                                    _candleSubscriptions.Remove(instrumentId);
                                    _lastCandleUpdates.Remove(instrumentId);
                                    _activeCandleSubscriptions.Remove(instrumentId);

                                    Debug.WriteLine($"[UnsubscribeFromCandlesAsync] ✅ Реальная отписка от {instrumentId} выполнена");
                                }
                            }
                            finally
                            {
                                _streamLock.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[UnsubscribeFromCandlesAsync] ❌ Ошибка отписки от {instrumentId}: {ex.Message}");
                        }
                    });

                    // Удаляем запись о подписке
                    _candleSubscriptionRefCount.Remove(key);
                    _candleCallbacks.Remove(key);
                }
                else
                {
                    Debug.WriteLine($"[UnsubscribeFromCandlesAsync] 📌 Подписка {key} все еще используется ({_candleSubscriptionRefCount[key]} стратегий)");
                }
            }
        }





        #endregion


        // Метод для получения позиции по инструменту (для использования в стратегиях)
        /* public Models.Position GetPosition(string accountId, string instrumentUid)
         {
             lock (_positionsLock)
             {
                 return _positions.FirstOrDefault(p =>
                     p.AccountId == accountId &&
                     (p.InstrumentUid == instrumentUid || p.Figi == instrumentUid));
             }
         }*/

        /*public decimal GetPositionQuantity(string accountId, string instrumentUid)
        {
            var position = GetPosition(accountId, instrumentUid);

            if (position != null)
            {
                Debug.WriteLine($"Текущая позиция по инструменту:  {position.Name}   {position.Quantity}   {position.LastUpdate}   ");
            }
            else
            {
                Debug.WriteLine($"Текущая позиция по инструменту:  0  ");
            }

            

            return position?.Quantity ?? 0;
        }*/



        /// <summary>
        /// Метод получения позиции по конкретно заданному инструменту из стратегии
        /// </summary>
        /// <param name="accountId"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>

        public async Task<Position> GetPositionQuantity(string accountId, string instrumentUid, string ticker = null)
        {
            Position position = null;

            if (_client == null)
                throw new InvalidOperationException("Клиент не подключен");

            try
            {

                Debug.WriteLine($"Загрузка текущей позиции для инструмента {instrumentUid}");


                var headers = CreateHeaders();

                PositionsResponse positionsResponse;

                if (IsSandboxMode)
                {
                    // Для песочницы
                    positionsResponse = await _client.Sandbox.GetSandboxPositionsAsync(
                        new PositionsRequest { AccountId = accountId },
                        headers);
                    Debug.WriteLine($"-----------positionsResponse = песочница -------------------");
                }
                else
                {
                    // Для реального счета
                    positionsResponse = await _client.Operations.GetPositionsAsync(
                        new PositionsRequest { AccountId = accountId },
                        headers);

                    Debug.WriteLine($"-----------positionsResponse = реальный счет-------------------");
                }

                if (positionsResponse != null)
                {
                    if (positionsResponse.Securities?.Count != 0)
                    {
                        //Debug.WriteLine($"Позиций загружено: {positionsResponse.Securities?.Count ?? 0}    {positionsResponse.Securities?.ElementAt(0).Ticker ?? "___"}     {positionsResponse.Securities?.ElementAt(0).Balance} ");
                    }
                    else if (positionsResponse.Futures?.Count != 0)
                    {

                        //Debug.WriteLine($"Позиций загружено: {positionsResponse.Futures?.Count ?? 0}    {positionsResponse.Futures?.ElementAt(0).Ticker ?? "___"}     {positionsResponse.Futures?.ElementAt(0).Balance} ");
                    }
                    else
                    {
                        //Debug.WriteLine($"Позиций загружено: {positionsResponse.Securities?.Count ?? 0}    ");
                        return position;
                    }


                    // Обрабатываем Акции
                    if (positionsResponse.Securities != null)
                    {
                        foreach (var security in positionsResponse.Securities)
                        {
                            if (security.Ticker == ticker || security.InstrumentUid == instrumentUid)
                            {
                                try
                                {
                                    // Получаем инструмент из кэша
                                    Models.Instrument instrument = null;
                                    if (!string.IsNullOrEmpty(security.InstrumentUid) &&
                                        _instrumentsCache.TryGetValue(security.InstrumentUid, out instrument))
                                    {
                                        // ✅ ИСПРАВЛЕНИЕ: Количество = Balance / LotSize (если LotSize > 1)
                                        int quantity = (int)security.Balance;
                                        if (instrument.LotSize > 1)
                                        {
                                            quantity = (int)(security.Balance / instrument.LotSize);
                                            Debug.WriteLine($"DEBUG: Корректировка количества для {instrument.Ticker}: " +
                                                           $"Balance={security.Balance}, LotSize={instrument.LotSize}, Quantity={quantity}");
                                        }

                                        position = new Models.Position
                                        {
                                            AccountId = accountId,
                                            InstrumentUid = security.InstrumentUid,
                                            Figi = security.Figi,
                                            Quantity = quantity,
                                            Ticker = instrument.Ticker,
                                            Name = instrument.Name,
                                            Currency = instrument.Currency,
                                            LotSize = instrument.LotSize,
                                            InstrumentType = security.InstrumentType,
                                            LastUpdate = DateTime.Now
                                        };
                                        
                                        Debug.WriteLine($"Акции: {position.Ticker} - {position.Quantity} (Balance={security.Balance}) - Позиция загружена");
                                        return position;
                                    }
                                    else
                                    {
                                        // Если инструмент не найден в кэше, используем значение как есть
                                         position = new Models.Position
                                        {
                                            AccountId = accountId,
                                            InstrumentUid = security.InstrumentUid,
                                            Figi = security.Figi,
                                            Quantity = (int)security.Balance,
                                            InstrumentType = security.InstrumentType,
                                            LastUpdate = DateTime.Now
                                        };
                                        
                                        Debug.WriteLine($"Акции (без кэша): {security.InstrumentUid} - {position.Quantity} - Позиция загружена");
                                        return position;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Ошибка обработки позиции: {ex.Message}");
                                }
                            }
                        }
                    }

                    // Обрабатываем Фьючерсы
                    if (positionsResponse.Futures != null)
                    {
                        foreach (var future in positionsResponse.Futures)
                        {
                            if (future.Ticker == ticker || future.InstrumentUid == instrumentUid)
                            {
                                try
                                {
                                    Models.Instrument instrument = null;
                                    if (!string.IsNullOrEmpty(future.InstrumentUid) &&
                                        _instrumentsCache.TryGetValue(future.InstrumentUid, out instrument))
                                    {
                                        // Для фьючерсов Balance также может быть в лотах
                                        int quantity = (int)future.Balance;
                                        if (instrument.LotSize > 1)
                                        {
                                            quantity = (int)(future.Balance / instrument.LotSize);
                                        }

                                        position = new Models.Position
                                        {
                                            AccountId = accountId,
                                            InstrumentUid = future.InstrumentUid,
                                            Figi = future.Figi,
                                            Quantity = quantity,
                                            Ticker = instrument.Ticker,
                                            Name = instrument.Name,
                                            Currency = instrument.Currency,
                                            LotSize = instrument.LotSize,
                                            //InstrumentType = future.InstrumentType,
                                            LastUpdate = DateTime.Now
                                        };

                                        Debug.WriteLine($"Фьючерсы: {position.Ticker} - {position.Quantity} (Balance={future.Balance}) - Позиция загружена");

                                        return position;
                                    }
                                    else
                                    {
                                         position = new Models.Position
                                        {
                                            AccountId = accountId,
                                            InstrumentUid = future.InstrumentUid,
                                            Figi = future.Figi,
                                            Quantity = (int)future.Balance,
                                            //InstrumentType = future.InstrumentType,
                                            LastUpdate = DateTime.Now
                                        };
                          
                                        Debug.WriteLine($"Фьючерсы (без кэша): {future.InstrumentUid} - {position.Quantity} - Позиция загружена");

                                        return position;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Ошибка обработки фьючерса: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.NotFound)
            {

                Debug.WriteLine($"Счет {accountId} не найден при загрузке позиций");
                return position;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки позиций: {ex.Message}");
                return position;
            }

            return position;
        }










        public async Task<decimal> LoadCurrentPositionsAsync(string accountId)
        {
            if (_client == null)
                throw new InvalidOperationException("Клиент не подключен");

            try
            {
                Debug.WriteLine($"Загрузка текущих позиций для счета {accountId}");

                var headers = CreateHeaders();

                PositionsResponse positionsResponse;

                if (IsSandboxMode)
                {
                    // Для песочницы
                    positionsResponse = await _client.Sandbox.GetSandboxPositionsAsync(
                        new PositionsRequest { AccountId = accountId },
                        headers);
                    Debug.WriteLine($"-----------positionsResponse = песочница -------------------");
                }
                else
                {
                    // Для реального счета
                    positionsResponse = await _client.Operations.GetPositionsAsync(
                        new PositionsRequest { AccountId = accountId },
                        headers);

                    Debug.WriteLine($"-----------positionsResponse = реальный счет-------------------");
                }
                
                if (positionsResponse != null)
                {
                    
                    if (positionsResponse.Securities?.Count != 0)
                    {
                        //Debug.WriteLine($"Позиций загружено: {positionsResponse.Securities?.Count ?? 0}    {positionsResponse.Securities?.ElementAt(0).Ticker ?? "___"}     {positionsResponse.Securities?.ElementAt(0).Balance} ");
                    }
                    else if (positionsResponse.Futures?.Count != 0)
                    {
                        
                        //Debug.WriteLine($"Позиций загружено: {positionsResponse.Futures?.Count ?? 0}    {positionsResponse.Futures?.ElementAt(0).Ticker ?? "___"}     {positionsResponse.Futures?.ElementAt(0).Balance} ");
                    }
                    else
                    {
                        Debug.WriteLine($"Позиций загружено: {positionsResponse.Securities?.Count ?? 0}    ");
                        return 0;
                    }
                    

                    var updatedPositions = new List<Models.Position>();

                    // Обрабатываем Акции
                    if (positionsResponse.Securities != null)
                    {
                        
                        foreach (var security in positionsResponse.Securities)
                        {
                            try
                            {
                                /*var position = new Models.Position
                                {
                                    AccountId = accountId,
                                    InstrumentUid = security.InstrumentUid,
                                    Figi = security.Figi,
                                    Quantity = Convert.ToInt32(security.Balance),
                                   
                                   


                                    InstrumentType = security.InstrumentType,
                                    
                                    LastUpdate = DateTime.Now
                                };*/

                                // Получаем инструмент из кэша
                                Models.Instrument instrument = null;
                                if (!string.IsNullOrEmpty(security.InstrumentUid) &&
                                    _instrumentsCache.TryGetValue(security.InstrumentUid, out instrument))
                                {
                                    // ✅ ИСПРАВЛЕНИЕ: Количество = Balance / LotSize (если LotSize > 1)
                                    int quantity = (int)security.Balance ;
                                    if (instrument.LotSize > 1)
                                    {
                                        quantity = (int)(security.Balance / instrument.LotSize);
                                        Debug.WriteLine($"DEBUG: Корректировка количества для {instrument.Ticker}: " +
                                                       $"Balance={security.Balance}, LotSize={instrument.LotSize}, Quantity={quantity}");
                                    }

                                    var position = new Models.Position
                                    {
                                        AccountId = accountId,
                                        InstrumentUid = security.InstrumentUid,
                                        Figi = security.Figi,
                                        Quantity = quantity,
                                        Ticker = instrument.Ticker,
                                        Name = instrument.Name,
                                        Currency = instrument.Currency,
                                        LotSize = instrument.LotSize,
                                        InstrumentType = security.InstrumentType,
                                        LastUpdate = DateTime.Now
                                    };
                                    updatedPositions.Add(position);
                                    Debug.WriteLine($"Акции: {position.Ticker} - {position.Quantity} (Balance={security.Balance}) - Позиция загружена");


                                   





                                }
                                else
                                {
                                    // Если инструмент не найден в кэше, используем значение как есть
                                    var position = new Models.Position
                                    {
                                        AccountId = accountId,
                                        InstrumentUid = security.InstrumentUid,
                                        Figi = security.Figi,
                                        Quantity = (int)security.Balance,
                                        InstrumentType = security.InstrumentType,
                                        LastUpdate = DateTime.Now
                                    };
                                    updatedPositions.Add(position);
                                    Debug.WriteLine($"Акции (без кэша): {security.InstrumentUid} - {position.Quantity} - Позиция загружена");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Ошибка обработки позиции: {ex.Message}");
                            }
                        }
                    }

                    // Обрабатываем Фьючерсы
                    if (positionsResponse.Futures != null)
                    {

                        foreach (var future in positionsResponse.Futures)
                        {
                            /*try
                            {
                                var position = new Models.Position
                                {
                                    AccountId = accountId,
                                    InstrumentUid = future.InstrumentUid,
                                    Figi = future.Figi,
                                    Quantity = Convert.ToInt32(future.Balance),

                                    LastUpdate = DateTime.Now
                                };

                                // Получаем тикер из кэша инструментов
                                if (!string.IsNullOrEmpty(position.InstrumentUid) &&
                                    _instrumentsCache.TryGetValue(position.InstrumentUid, out var instrument))
                                {
                                    position.Ticker = instrument.Ticker;
                                    position.Name = instrument.Name;
                                    position.Currency = instrument.Currency;
                                }
                                else if (!string.IsNullOrEmpty(position.Figi))
                                {
                                    // Ищем по FIGI
                                    var foundInstrument = _instrumentsCache.Values
                                        .FirstOrDefault(i => i.Figi == position.Figi);
                                    if (foundInstrument != null)
                                    {
                                        position.Ticker = foundInstrument.Ticker;
                                        position.Name = foundInstrument.Name;
                                        position.Currency = foundInstrument.Currency;
                                    }
                                }

                                //if (position.Quantity > 0) // Добавляем только позиции с ненулевым количеством
                                //{
                                updatedPositions.Add(position);
                                Debug.WriteLine($"Фьючерсы: {position.Ticker} - {position.Quantity} - Позиция загружена");
                                updPos = position.Quantity;

                                //}
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Ошибка обработки позиции: {ex.Message}");
                            }*/
                            try
                            {
                                Models.Instrument instrument = null;
                                if (!string.IsNullOrEmpty(future.InstrumentUid) &&
                                    _instrumentsCache.TryGetValue(future.InstrumentUid, out instrument))
                                {
                                    // Для фьючерсов Balance также может быть в лотах
                                    int quantity = (int)future.Balance ;
                                    if (instrument.LotSize > 1)
                                    {
                                        quantity = (int)(future.Balance / instrument.LotSize);
                                    }

                                    var position = new Models.Position
                                    {
                                        AccountId = accountId,
                                        InstrumentUid = future.InstrumentUid,
                                        Figi = future.Figi,
                                        Quantity = quantity,
                                        Ticker = instrument.Ticker,
                                        Name = instrument.Name,
                                        Currency = instrument.Currency,
                                        LotSize = instrument.LotSize,
                                        //InstrumentType = future.InstrumentType,
                                        LastUpdate = DateTime.Now
                                    };
                                    updatedPositions.Add(position);
                                    Debug.WriteLine($"Фьючерсы: {position.Ticker} - {position.Quantity} (Balance={future.Balance}) - Позиция загружена");
                                }
                                else
                                {
                                    var position = new Models.Position
                                    {
                                        AccountId = accountId,
                                        InstrumentUid = future.InstrumentUid,
                                        Figi = future.Figi,
                                        Quantity = (int)future.Balance,
                                        //InstrumentType = future.InstrumentType,
                                        LastUpdate = DateTime.Now
                                    };
                                    updatedPositions.Add(position);
                                    Debug.WriteLine($"Фьючерсы (без кэша): {future.InstrumentUid} - {position.Quantity} - Позиция загружена");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Ошибка обработки фьючерса: {ex.Message}");
                            }
                        }
                    }

                    // Обрабатываем денежные средства
                    if (positionsResponse.Money != null)
                    {
                        foreach (var money in positionsResponse.Money)
                        {
                            // Добавляем денежные позиции если нужно
                           
                            try
                            {
                                var position = new Models.Position
                                {
                                    AccountId = accountId,
                                    InstrumentUid = money.Currency,
                                    Currency = money.Currency,
                                    Figi = money.Currency,
                                    Quantity = Convert.ToInt32(money.Units),
                                    AveragePrice = ConvertMoneyValueToDecimal(money),
                                    Ticker = money.Currency,
                                    


                                    LastUpdate = DateTime.Now
                                };

                                // Получаем тикер из кэша инструментов
                                if (!string.IsNullOrEmpty(position.InstrumentUid) &&
                                    _instrumentsCache.TryGetValue(position.InstrumentUid, out var instrument))
                                {
                                    position.Ticker = money.Currency;
                                    position.Name = money.Currency;
                                    position.Currency = money.Currency;
                                }
                                else if (!string.IsNullOrEmpty(position.Figi))
                                {
                                    // Ищем по FIGI
                                    var foundInstrument = _instrumentsCache.Values
                                        .FirstOrDefault(i => i.Figi == position.Figi);
                                    if (foundInstrument != null)
                                    {
                                        position.Ticker = money.Currency;
                                        position.Name = money.Currency;
                                        position.Currency = money.Currency;
                                    }
                                }

                                //if (position.Quantity > 0) // Добавляем только позиции с ненулевым количеством
                                //{
                                updatedPositions.Add(position);
                                Debug.WriteLine($"Денежные средства: {money.Currency} - {ConvertMoneyValueToDecimal(money)} - Позиция загружена");
                                //Debug.WriteLine($"Позиция загружена: {position.Ticker} - {ConvertMoneyValueToDecimal(money)}");
                                updPos = position.Quantity;

                                //}
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Ошибка обработки позиции: {ex.Message}");
                            }

                        }
                    }

                    // Обрабатываем заблокированные денежные средства
                    if (positionsResponse.Blocked != null)
                    {
                        foreach (var moneyBlocked in positionsResponse.Blocked)
                        {
                            // Добавляем денежные позиции если нужно
                            
                            try
                            {
                                var position = new Models.Position
                                {
                                    AccountId = accountId,
                                    InstrumentUid = moneyBlocked.Currency,
                                    Currency = moneyBlocked.Currency,
                                    Figi = moneyBlocked.Currency,
                                    Quantity = Convert.ToInt32(moneyBlocked.Units),
                                    AveragePrice = ConvertMoneyValueToDecimal(moneyBlocked),
                                    Ticker = $"Blocked {moneyBlocked.Currency}",



                                    LastUpdate = DateTime.Now
                                };

                                // Получаем тикер из кэша инструментов
                                if (!string.IsNullOrEmpty(position.InstrumentUid) &&
                                    _instrumentsCache.TryGetValue(position.InstrumentUid, out var instrument))
                                {
                                    position.Ticker = moneyBlocked.Currency;
                                    position.Name = moneyBlocked.Currency;
                                    position.Currency = moneyBlocked.Currency;
                                }
                                else if (!string.IsNullOrEmpty(position.Figi))
                                {
                                    // Ищем по FIGI
                                    var foundInstrument = _instrumentsCache.Values
                                        .FirstOrDefault(i => i.Figi == position.Figi);
                                    if (foundInstrument != null)
                                    {
                                        position.Ticker = moneyBlocked.Currency;
                                        position.Name = moneyBlocked.Currency;
                                        position.Currency = moneyBlocked.Currency;
                                    }
                                }

                                //if (position.Quantity > 0) // Добавляем только позиции с ненулевым количеством
                                //{
                                updatedPositions.Add(position);
                                //Debug.WriteLine($"Позиция  загружена: {position.Ticker} - {ConvertMoneyValueToDecimal(moneyBlocked)}");
                                Debug.WriteLine($"Заблокированные денежные средства: {moneyBlocked.Currency} - {ConvertMoneyValueToDecimal(moneyBlocked)}  - Позиция загружена");
                                updPos = position.Quantity;

                                //}
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Ошибка обработки позиции: {ex.Message}");
                            }

                        }
                    }





                    // Обновляем список позиций
                    lock (_positionsLock)
                    {
                        _positions.Clear();
                        _positions.AddRange(updatedPositions);
                    }

                    // Вызываем событие обновления
                    OnPositionsUpdated?.Invoke(updatedPositions);

                    Debug.WriteLine($"Всего загружено {updatedPositions.Count} позиций");
                    
                }
                return updPos;
            }
            catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.NotFound)
            {
                
                Debug.WriteLine($"Счет {accountId} не найден при загрузке позиций");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки позиций: {ex.Message}");
                return 0;
            }
        }
        public async Task<decimal> RefreshPositionsAsync(string accountId = null)
        {
            decimal posTemp = 0;
            try
            {
                Debug.WriteLine($"Принудительное обновление позиций... posTemp:{posTemp}");

                var accounts = await GetAccountsAsync();

                if (string.IsNullOrEmpty(accountId))
                {
                    // Обновляем все счета
                    foreach (var account in accounts)
                    {
                         posTemp = await LoadCurrentPositionsAsync(account.Id);
                    }
                    Debug.WriteLine($"... posTemp:{posTemp}");

                    return posTemp;
                }
                else
                {
                    // Обновляем только указанный счет
                    posTemp = await LoadCurrentPositionsAsync(accountId);
                }
                return posTemp;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка принудительного обновления позиций: {ex.Message}");
                return 0;
            }
        }

        #region Вспомогательные методы
        //  вспомогательный метод для конвертации Quotation в decimal:
        private decimal ConvertQuotationToDecimal(Quotation quotation)
        {
            if (quotation == null) return 0;
            return quotation.Units + quotation.Nano / 1000000000m;
        }

        // Добавьте этот метод для конвертации MoneyValue в decimal
        private decimal ConvertMoneyValueToDecimal(MoneyValue moneyValue)
        {
            if (moneyValue == null)
            {
                Debug.WriteLine("DEBUG: TinkoffApiService:  MoneyValue is null, returning 0");
                return 0;
            }

            try
            {
                // MoneyValue состоит из Units(целая часть) и Nano(дробная часть, нано-единицы)
                // 1 Unit = 1,000,000,000 Nano
                var units = moneyValue.Units;
                var nano = moneyValue.Nano;

                // Создаем decimal из units и nano
                decimal result = units;

                if (nano != 0)
                {
                    result += nano / 1_000_000_000m;
                }

                //Debug.WriteLine($"DEBUG: TinkoffApiService:  Converted MoneyValue: Units={units}, Nano={nano}, Result={result}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Error converting MoneyValue to decimal: {ex.Message}");
                _logger.LogError(ex, "Ошибка конвертации MoneyValue в decimal");
                return 0;
            }






        }
        public decimal SafeConvertQuotation(Quotation? quotation)
        {
            if (quotation == null) return 0;
            try
            {
                return quotation.Units + quotation.Nano / 1000000000m;
            }
            catch
            {
                return 0;
            }
        }

        private SubscriptionInterval ConvertStringToSubscriptionInterval(string interval)
        {
            return interval?.ToLower() switch
            {
                "1min" => SubscriptionInterval.OneMinute,
                "5min" => SubscriptionInterval.FiveMinutes,
                "10min" => SubscriptionInterval._10Min,
                "15min" => SubscriptionInterval.FifteenMinutes,
                "30min" => SubscriptionInterval._30Min,
                "1hour" => SubscriptionInterval.OneHour,
                "2hour" => SubscriptionInterval._2Hour,
                "4hour" => SubscriptionInterval._4Hour,
                "1day" => SubscriptionInterval.OneDay,
                _ => SubscriptionInterval.OneMinute
            };
        }

        //  метод GetIntervalMinutes для поддержки строковых таймфреймов:
        private int GetIntervalMinutes(SubscriptionInterval interval)
        {
            return interval switch
            {
                SubscriptionInterval.OneMinute => 1,
                SubscriptionInterval.FiveMinutes => 5,
                SubscriptionInterval._10Min => 10,
                SubscriptionInterval.FifteenMinutes => 15,
                SubscriptionInterval._30Min => 30,
                SubscriptionInterval.OneHour => 60,
                SubscriptionInterval._2Hour => 120,
                SubscriptionInterval._4Hour => 240,
                SubscriptionInterval.OneDay => 1440,
                _ => 1
            };
        }

        private string ConvertTinkoffIntervalToString(string tinkoffInterval)
        {
            if (string.IsNullOrEmpty(tinkoffInterval))
                return "1min";

            var lower = tinkoffInterval.ToLower();

            return lower switch
            {
                "1_min" or "1min" or "_1min" or "oneminute" or "1" => "1min",
                "5_min" or "5min" or "_5min" or "fiveminutes" or "5" => "5min",
                "10_min" or "10min" or "_10min" or "tenminutes" or "10" => "10min",
                "15_min" or "15min" or "_15min" or "fifteenminutes" or "15" => "15min",
                "30_min" or "30min" or "_30min"  or "thirtyminutes" or "30" => "30min",
                "1_hour" or "1hour" or "_1Hour" or "onehour" or "Hour" or "hour" or "60" or "60_min" => "1hour",
                "2_hour" or "2hour" or "_2Hour" or "twohours" or "2" or "120" => "2hour",
                "4_hour" or "4hour" or "_4Hour" or "fourhours" or "4" or "240" => "4hour",
                "1_day" or "1day" or "Day" or "oneday" or "day" or "1440" => "1day",
                _ => "1min" // значение по умолчанию
            };
        }

        private CandleInterval ConvertTimeframeToInterval(string timeframe)
        {
            return timeframe?.ToLower() switch
            {
                "1min" => CandleInterval._1Min,
                "5min" => CandleInterval._5Min,
                "10min" => CandleInterval._10Min,
                "15min" => CandleInterval._15Min,
                "30min" => CandleInterval._30Min,
                "1hour" => CandleInterval.Hour,
                "2hour" => CandleInterval._2Hour,
                "4hour" => CandleInterval._4Hour,
                "1day" => CandleInterval.Day,
                _ => CandleInterval._1Min
            };
        }
        #endregion

        #region Trading Operations (перенесено из TinkoffTradingService)
        public async Task<Result> PlaceOrderAsync(Models.Order order)
        {
            string accountId = null;
            var lockKey = string.Empty;

            try
            {
                if (_client == null)
                    return new Result { IsSuccess = false, ErrorMessage = "Клиент не подключен" };

                // 1. Получаем accountId
                var accounts = await GetAccountsAsync();

                if (!string.IsNullOrEmpty(order.AccountId))
                {
                    accountId = order.AccountId;
                }
                else
                {
                    if (!accounts.Any())
                        return new Result { IsSuccess = false, ErrorMessage = "Нет доступных счетов" };
                    accountId = accounts.First().Id;
                }

                // Устанавливаем AccountId в order для дальнейшего использования
                order.AccountId = accountId;
                lockKey = $"{accountId}_{order.InstrumentUid}";

                // 2. Получаем текущую позицию
                var currentPosition = await GetPositionAsync(accountId, order.InstrumentUid);
                var directionStr = order.Direction?.ToLower()?.Trim() ?? "";

                // 3. Определяем тип операции: вход или выход
                // 3. ✅ ИСПРАВЛЕННАЯ ЛОГИКА определения входа/выхода
                bool isEntryOrder = false;
                bool isExitOrder = false;

                if (Math.Abs(currentPosition) == 0)
                {
                    // Если позиции нет - это вход
                    isEntryOrder = true;
                    isExitOrder = false;
                }
                else
                {
                    if (currentPosition > 0) // Long позиция
                    {
                        // Buy -> вход (увеличение), Sell -> выход (закрытие)
                        isEntryOrder = directionStr == "buy" || directionStr == "long";
                        isExitOrder = directionStr == "sell" || directionStr == "short";
                    }
                    else // Short позиция
                    {
                        // Sell -> вход (увеличение), Buy -> выход (закрытие)
                        isEntryOrder = directionStr == "sell" || directionStr == "short";
                        isExitOrder = directionStr == "buy" || directionStr == "long";
                    }
                }

                Debug.WriteLine($"DEBUG: TinkoffApiService:  Current position: {currentPosition}, Order direction: {directionStr}, IsEntry: {isEntryOrder}, IsExit: {isExitOrder}");

                // 4. ✅ ДОБАВЛЯЕМ проверку: если это выход, убеждаемся, что направление правильное
                if (isExitOrder)
                {
                    bool isValidExit = false;
                    if (currentPosition > 0 && (directionStr == "sell" || directionStr == "short"))
                        isValidExit = true;
                    else if (currentPosition < 0 && (directionStr == "buy" || directionStr == "long"))
                        isValidExit = true;

                    if (!isValidExit)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService: ОШИБКА - Неверное направление для выхода. Position={currentPosition}, Direction={directionStr}");
                        return new Result
                        {
                            IsSuccess = false,
                            ErrorMessage = $"Неверное направление для выхода. Позиция: {currentPosition}, Направление: {directionStr}"
                        };
                    }
                }

                Debug.WriteLine($"DEBUG: TinkoffApiService:  Current position: {currentPosition}, Order direction: {directionStr}, IsEntry: {isEntryOrder}, IsExit: {isExitOrder}");

                // 5. ✅ ПРОВЕРКА: Если это выход, не даем войти в противоположную позицию
                if (isExitOrder && Math.Abs(currentPosition) == 0)
                {
                    return new Result
                    {
                        IsSuccess = false,
                        ErrorMessage = "Нельзя выйти из позиции - позиция отсутствует"
                    };
                }


                // 6. Если это вход, проверяем что позиция равна 0
                if (isEntryOrder && Math.Abs(currentPosition) > 0)
                {
                    // Проверяем, не пытаемся ли мы увеличить позицию (если это разрешено)
                    bool isIncreasingPosition = false;
                    if (currentPosition > 0 && (directionStr == "buy" || directionStr == "long"))
                        isIncreasingPosition = true;
                    else if (currentPosition < 0 && (directionStr == "sell" || directionStr == "short"))
                        isIncreasingPosition = true;

                    if (!isIncreasingPosition)
                    {
                        return new Result
                        {
                            IsSuccess = false,
                            ErrorMessage = $"Нельзя войти в позицию - уже есть позиция {currentPosition} лотов. Для увеличения позиции используйте то же направление."
                        };
                    }
                }



                // 7. Быстрая проверка баланса (только для входов)
                if (isEntryOrder)
                {
                    try
                    {
                        var balance = await GetAccountBalanceAsync();
                        if (balance <= 0)
                        {
                            // Снимаем блокировку если была установлена
                            //ClearLocksOnError(lockKey, isEntryOrder, isExitOrder);
                            return new Result
                            {
                                IsSuccess = false,
                                ErrorMessage = $"Недостаточно средств. Баланс: {balance}"
                            };
                        }
                    }
                    catch (Exception balanceEx)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Balance check error: {balanceEx.Message}");
                        // Продолжаем, так как баланс может быть не критичен для некоторых типов счетов
                    }
                }

                var headers = CreateHeaders();

                // 7. Определяем тип инструмента
                string instrumentType = "";
                if (_instrumentsCache.TryGetValue(order.InstrumentUid, out var instrument))
                {
                    instrumentType = instrument.Type.ToString() ?? "";
                }

                // 8. Преобразуем направление ордера
                OrderDirection direction;

                if (string.IsNullOrEmpty(directionStr))
                {
                    // Снимаем блокировку
                    //ClearLocksOnError(lockKey, isEntryOrder, isExitOrder);
                    return new Result { IsSuccess = false, ErrorMessage = "Не указано направление ордера" };
                }

                // Для фьючерсов используем специальную логику
                if (instrumentType.Contains("Future", StringComparison.OrdinalIgnoreCase))
                {
                    direction = directionStr switch
                    {
                        "long" or "buy" => OrderDirection.Buy,
                        "short" or "sell" => OrderDirection.Sell,
                        _ => OrderDirection.Unspecified
                    };

                    // Для фьючерсов количество должно быть целым числом лотов
                    if (order.Quantity % 1 != 0)
                    {
                        // Снимаем блокировку
                        //ClearLocksOnError(lockKey, isEntryOrder, isExitOrder);
                        return new Result
                        {
                            IsSuccess = false,
                            ErrorMessage = "Для фьючерсов количество должно быть целым числом лотов"
                        };
                    }
                }
                else
                {
                    // Для акций и других инструментов
                    direction = directionStr switch
                    {
                        "long" or "buy" => OrderDirection.Buy,
                        "short" or "sell" => OrderDirection.Sell,
                        _ => OrderDirection.Unspecified
                    };
                }

                if (direction == OrderDirection.Unspecified)
                {
                    // Снимаем блокировку
                    //ClearLocksOnError(lockKey, isEntryOrder, isExitOrder);
                    return new Result
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Неверное направление ордера: '{order.Direction}'. Допустимые значения: 'buy', 'sell', 'long', 'short'"
                    };
                }

                // 9. Преобразуем тип ордера
                var orderTypeString = order.OrderType?.ToString()?.ToLower()?.Trim() ?? "market";
                var orderType = orderTypeString switch
                {
                    "market" => Tinkoff.InvestApi.V1.OrderType.Market,
                    "limit" => Tinkoff.InvestApi.V1.OrderType.Limit,
                    "stoplimit" or "bestprice" => Tinkoff.InvestApi.V1.OrderType.Bestprice,
                    _ => Tinkoff.InvestApi.V1.OrderType.Market
                };

                // 10. Создаем уникальный ID ордера если не указан
                if (string.IsNullOrEmpty(order.OrderId))
                {
                    order.OrderId = Guid.NewGuid().ToString();
                }

                // 11. Создаем запрос
                var request = new PostOrderRequest
                {
                    AccountId = accountId,
                    InstrumentId = order.InstrumentUid,
                    Quantity = Math.Abs(order.Quantity),
                    Direction = direction,
                    OrderType = orderType,
                    OrderId = order.OrderId,
                    Price = DecimalToQuotation(order.Price)
                };


                Debug.WriteLine($"DEBUG: TinkoffApiService:  PlaceOrderAsync - Tinkoff  ---- IsSandboxMode={IsSandboxMode}  request.InstrumentId={request.InstrumentId}   request.Direction={request.Direction}  request.Quantity={request.Quantity}   request.Price={request.Price}  order.Price={order.Price}  request.PriceType={request.PriceType}    request.OrderType={request.OrderType}");


                // 12. Добавляем цену для лимитного ордера
                /*if (orderType == Tinkoff.InvestApi.V1.OrderType.Limit)
                {
                    if (order.Price <= 0)
                    {
                        // Снимаем блокировку
                        //ClearLocksOnError(lockKey, isEntryOrder, isExitOrder);
                        return new Result
                        {
                            IsSuccess = false,
                            ErrorMessage = "Для лимитного ордера необходимо указать цену"
                        };
                    }
                    request.Price = *//*DecimalToQuotation(order.Price) ??*//* 0;
                }
*/
                // 13. Отправляем ордер
                PostOrderResponse response;
                try
                {
                    if (IsSandboxMode)
                    {
                        response = await _client.Sandbox.PostSandboxOrderAsync(request, headers: headers);
                    }
                    else
                    {
                        response = await _client.Orders.PostOrderAsync(request, headers: headers);
                    }
                }
                catch (Exception ex)
                {
                    // Снимаем блокировку при ошибке размещения ордера
                    //ClearLocksOnError(lockKey, isEntryOrder, isExitOrder);

                    Debug.WriteLine($"DEBUG: TinkoffApiService:  _client.Sandbox.PostSandboxOrderAsync или _client.Orders.PostOrderAsync - ERROR: \n{ex.Message}  {ex.StackTrace}");
                    throw;
                }

                // 14. Анализируем ответ
                var isSuccess = response.ExecutionReportStatus == OrderExecutionReportStatus.ExecutionReportStatusFill ||
                               response.ExecutionReportStatus == OrderExecutionReportStatus.ExecutionReportStatusNew ||
                               response.ExecutionReportStatus == OrderExecutionReportStatus.ExecutionReportStatusPartiallyfill;

                // 15. Логируем результат

                Debug.WriteLine($"DEBUG: TinkoffApiService:   - isSuccess={isSuccess}  response.ExecutionReportStatus={response.ExecutionReportStatus}");
               

                _logger.LogInformation("Ордер размещен: OrderId={OrderId}, Status={Status}, Message={Message}, Direction={Direction}, Quantity={Quantity}, Type={Type}",
                    response.OrderId, response.ExecutionReportStatus, response.Message, direction, order.Quantity,
                    isEntryOrder ? "Entry" : isExitOrder ? "Exit" : "Unknown");

                // 16. Обрабатываем результат
                if (isSuccess)
                {
                    // Если ордер исполнился сразу (рыночный)
                    if (response.ExecutionReportStatus == OrderExecutionReportStatus.ExecutionReportStatusFill)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService: 1  - ");

                        // Ждем обновления позиций
                        await Task.Delay(2000);
                        var updatedPosition = await GetPositionAsync(accountId, order.InstrumentUid);

                        Debug.WriteLine($"DEBUG: TinkoffApiService: 2  - ");




                        /* if (isEntryOrder)
                         {
                             if (Math.Abs(updatedPosition) > 0)
                             {
                                 // Позиция открыта успешно - блокировка входа остается, активируем блокировку выхода
                                 lock (_lockSync)
                                 {
                                     _exitLock[lockKey] = true;
                                 }
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Entry order filled, position opened: {updatedPosition} lots, exit lock activated");
                             }
                             else
                             {
                                 // Что-то пошло не так - снимаем блокировку входа
                                 lock (_lockSync)
                                 {
                                     _entryLock.Remove(lockKey);
                                 }
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Entry order filled but position not updated, entry lock removed");
                             }
                         }
                         else if (isExitOrder)
                         {
                             if (Math.Abs(updatedPosition) == 0)
                             {
                                 // Позиция закрыта успешно - снимаем все блокировки
                                 ClearAllLocks(lockKey);
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Exit order filled, position closed, all locks cleared");
                             }
                             else
                             {
                                 // Позиция закрыта не полностью - блокировка выхода остается
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Exit order partially filled, position: {updatedPosition} lots, exit lock remains");
                             }
                         }*/
                    }
                    else if (response.ExecutionReportStatus == OrderExecutionReportStatus.ExecutionReportStatusNew)
                    {
                        // Ордер принят биржей, но не исполнен - запускаем мониторинг
                        _ = Task.Run(async () =>
                        {
                            //await MonitorOrderStatus(response.OrderId, accountId, order.InstrumentUid, lockKey, isEntryOrder);
                        });
                    }

                    return new Result
                    {
                        IsSuccess = true,
                        OrderId = response.OrderId,
                        Message = $"Ордер {response.OrderId} успешно размещен. Статус: {response.ExecutionReportStatus}"
                    };
                }
                else
                {
                    // Ордер не прошел - снимаем соответствующие блокировки
                    //ClearLocksOnError(lockKey, isEntryOrder, isExitOrder);

                    return new Result
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Ордер отклонен. Статус: {response.ExecutionReportStatus}, Причина: {response.Message}",
                        OrderId = response.OrderId
                    };
                }
            }
            catch (RpcException rpcEx)
            {
                // Снимаем все блокировки при RPC ошибке
                if (!string.IsNullOrEmpty(lockKey))
                {
                   // ClearAllLocks(lockKey);
                }

                var errorCode = rpcEx.StatusCode;
                var errorDetail = rpcEx.Status.Detail ?? "";

                _logger.LogError(rpcEx, "RPC ошибка размещения ордера. Code: {Code}, Detail: {Detail}", errorCode, errorDetail);

                // Обрабатываем специфичные ошибки Tinkoff
                string userMessage;

                if (errorDetail.Contains("30034"))
                {
                    userMessage = "Ошибка 30034: Недостаточно средств для совершения сделки (ошибка песочницы).";
                }
                else if (errorDetail.Contains("30035"))
                {
                    userMessage = "Ошибка 30035: Недостаточно средств для открытия позиции.";
                }
                else if (errorDetail.Contains("30036"))
                {
                    userMessage = "Ошибка 30036: Входной параметр stop_price является обязательным.\r\nУкажите корректный параметр stop_price.";
                }
                else if (errorCode == StatusCode.InvalidArgument)
                {
                    userMessage = $"Некорректные параметры ордера: {errorDetail}";
                }
                else if (errorCode == StatusCode.Unauthenticated)
                {
                    userMessage = "Ошибка аутентификации. Проверьте токен доступа.";
                }
                else if (errorCode == StatusCode.PermissionDenied)
                {
                    userMessage = "Недостаточно прав для размещения ордера.";
                }
                else
                {
                    userMessage = $"Ошибка биржи: {errorDetail} (код: {errorCode})";
                }

                return new Result
                {
                    IsSuccess = false,
                    ErrorMessage = userMessage
                };
            }
            catch (Exception ex)
            {
                // Снимаем все блокировки при общей ошибке
                if (!string.IsNullOrEmpty(lockKey))
                {
                    //ClearAllLocks(lockKey);
                }

                _logger.LogError(ex, "Общая ошибка размещения ордера");
                return new Result
                {
                    IsSuccess = false,
                    ErrorMessage = $"Внутренняя ошибка: {ex.Message}"
                };
            }
        }

        // ДОБАВЛЯЕМ: Метод для определения, является ли ордер входом
        /// <summary>
        /// Определяет, является ли ордер входом в позицию
        /// </summary>
        private bool IsEntryOrder(decimal currentPosition, string direction)
        {
            if (Math.Abs(currentPosition) == 0)
            {
                // Если позиции нет, любой ордер - это вход
                return true;
            }

            var normalizedDirection = direction.ToLower();

            if (currentPosition > 0) // Длинная позиция (Long)
            {
                // Для длинной позиции:
                // - Buy или Long -> УВЕЛИЧЕНИЕ позиции (вход)
                // - Sell или Short -> УМЕНЬШЕНИЕ позиции (выход)
                return normalizedDirection == "buy" || normalizedDirection == "long";
            }
            else // Короткая позиция (Short)
            {
                // Для короткой позиции:
                // - Sell или Short -> УВЕЛИЧЕНИЕ позиции (вход)
                // - Buy или Long -> УМЕНЬШЕНИЕ позиции (выход)
                return normalizedDirection == "sell" || normalizedDirection == "short";
            }
        }

        /// <summary>
        /// Определяет, является ли ордер выходом из позиции
        /// </summary>
        private bool IsExitOrder(decimal currentPosition, string direction)
        {
            if (Math.Abs(currentPosition) == 0)
            {
                // Если позиции нет, выход невозможен
                return false;
            }

            var normalizedDirection = direction.ToLower();

            if (currentPosition > 0) // Длинная позиция (Long)
            {
                // Для длинной позиции Sell или Short - это выход
                return normalizedDirection == "sell" || normalizedDirection == "short";
            }
            else // Короткая позиция (Short)
            {
                // Для короткой позиции Buy или Long - это выход
                return normalizedDirection == "buy" || normalizedDirection == "long";
            }
        }

        // Метод для снятия блокировок при ошибке
        /*private void ClearLocksOnError(string lockKey, bool isEntryOrder, bool isExitOrder)
        {
            lock (_lockSync)
            {
                if (isEntryOrder)
                {
                    _entryLock.Remove(lockKey);
                }
                if (isExitOrder)
                {
                    _exitLock.Remove(lockKey);
                }
            }
            Debug.WriteLine($"DEBUG: TinkoffApiService:  Locks cleared on error for {lockKey}");
        }*/

        // Метод для полной очистки всех блокировок
        /*private void ClearAllLocks(string lockKey)
        {
            lock (_lockSync)
            {
                _entryLock.Remove(lockKey);
                _exitLock.Remove(lockKey);
            }
            Debug.WriteLine($"DEBUG: TinkoffApiService:  All locks cleared for {lockKey}");
        }*/






        // Обновленный метод для очистки устаревших блокировок
        /*public void CleanupStaleLocks()
        {
            try
            {
                lock (_lockSync)
                {
                    // В реальной реализации здесь можно добавить логику очистки
                    // блокировок, которые существуют слишком долго (например, более 1 часа)
                    // Пока просто очищаем все при каждом вызове этого метода

                    var entryCount = _entryLock.Count;
                    var exitCount = _exitLock.Count;

                    _entryLock.Clear();
                    _exitLock.Clear();

                    if (entryCount > 0 || exitCount > 0)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Cleaned up {entryCount} entry locks and {exitCount} exit locks");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка очистки устаревших блокировок");
            }
        }*/



        /*private async Task MonitorOrderStatus(string orderId, string accountId, string instrumentUid, string lockKey, bool isEntryOrder)
        {
            try
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Starting order monitoring for {orderId}");

                var maxAttempts = 120; // 2 минуты
                var attempt = 0;

                while (attempt < maxAttempts)
                {
                    await Task.Delay(1000);
                    attempt++;

                    try
                    {
                        // Проверяем позицию
                        var currentPosition = await GetPositionAsync(accountId, instrumentUid);

                        if (isEntryOrder)
                        {
                            if (Math.Abs(currentPosition) > 0)
                            {
                                // Позиция открыта - активируем блокировку выхода
                                lock (_lockSync)
                                {
                                    _exitLock[lockKey] = true;
                                }
                                Debug.WriteLine($"DEBUG: TinkoffApiService:  Entry order {orderId} executed, position: {currentPosition}, exit lock activated");
                                break;
                            }
                        }
                        else
                        {
                            if (Math.Abs(currentPosition) == 0)
                            {
                                // Позиция закрыта - снимаем все блокировки
                                ClearAllLocks(lockKey);
                                Debug.WriteLine($"DEBUG: TinkoffApiService:  Exit order {orderId} executed, position closed, all locks cleared");
                                break;
                            }
                        }

                        // Проверяем статус ордера (опционально)
                        if (attempt % 30 == 0) // Каждые 30 секунд
                        {
                            try
                            {
                                var status = await GetOrderStatusAsync(orderId);
                                if (status == OrderStatus.Cancelled || status == OrderStatus.Rejected)
                                {
                                    // Ордер отменен или отклонен - снимаем блокировки
                                    //ClearLocksOnError(lockKey, isEntryOrder, !isEntryOrder);
                                    Debug.WriteLine($"DEBUG: TinkoffApiService:  Order {orderId} {status}, locks cleared");
                                    break;
                                }
                            }
                            catch
                            {
                                // Игнорируем ошибки проверки статуса
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DEBUG: TinkoffApiService:  Order monitoring error: {ex.Message}");
                    }
                }

                // Если вышли по таймауту
                if (attempt >= maxAttempts)
                {
                    Debug.WriteLine($"DEBUG: TinkoffApiService:  Order monitoring timeout for {orderId}");

                    // Проверяем финальную позицию
                    var finalPosition = await GetPositionAsync(accountId, instrumentUid);

                    if (isEntryOrder)
                    {
                        if (Math.Abs(finalPosition) > 0)
                        {
                            // Позиция открыта - активируем блокировку выхода
                            lock (_lockSync)
                            {
                                _exitLock[lockKey] = true;
                            }
                        }
                        else
                        {
                            // Позиция не открыта - снимаем блокировку входа
                            lock (_lockSync)
                            {
                                _entryLock.Remove(lockKey);
                            }
                        }
                    }
                    else
                    {
                        if (Math.Abs(finalPosition) == 0)
                        {
                            // Позиция закрыта
                            ClearAllLocks(lockKey);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DEBUG: TinkoffApiService:  Critical error in order monitoring: {ex.Message}");
                //ClearAllLocks(lockKey);
            }
        }*/


        public async Task CancelOrderAsync(string orderId)
        {
            try
            {
                if (_client == null)
                    return;

                var headers = CreateHeaders();
                var accounts = await GetAccountsAsync();

                if (!accounts.Any())
                    return;

                // Пытаемся отменить ордер для каждого счета
                foreach (var account in accounts)
                {
                    try
                    {
                        if (IsSandboxMode)
                        {
                            var request = new CancelOrderRequest
                            {
                                AccountId = account.Id,
                                OrderId = orderId
                            };

                            // ИСПРАВЛЕНИЕ: Используем правильный метод для песочницы
                            await _client.Sandbox.CancelSandboxOrderAsync(request, headers: headers);
                        }
                        else
                        {
                            var request = new CancelOrderRequest
                            {
                                AccountId = account.Id,
                                OrderId = orderId
                            };
                            await _client.Orders.CancelOrderAsync(request, headers: headers);
                        }

                        _logger.LogInformation("Ордер {OrderId} отменен", orderId);
                        return;
                    }
                    catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.NotFound)
                    {
                        // Ордер не найден на этом счете, пробуем следующий
                        continue;
                    }
                    catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unimplemented)
                    {
                        // ИСПРАВЛЕНИЕ: Если метод не реализован
                        Debug.WriteLine($"Метод CancelSandboxOrderAsync недоступен: {rpcEx.Message}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка отмены ордера {OrderId} для счета {AccountId}", orderId, account.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка отмены ордера {OrderId}", orderId);
            }
        }

        public async Task<decimal> GetAccountBalanceAsync()
        {
            try
            {
                var accounts = await GetAccountsAsync();
                if (accounts == null || !accounts.Any())
                    return 0;
                //Debug.WriteLine($"-----------------------------{(decimal)accounts.Sum(a => a.Balance)}-----------------------------1");
                return (decimal)accounts.Sum(a => a.Balance);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения баланса счета");
                return 0;
            }
        }

     
        public async Task<PositionsResponse> GetPositionsAsync(string accountId)
        {
            if (_client == null)
                throw new InvalidOperationException("Клиент не подключен");

            var headers = CreateHeaders();

            if (IsSandboxMode)
            {
                return await _client.Sandbox.GetSandboxPositionsAsync(
                    new PositionsRequest { AccountId = accountId },
                    headers);
            }
            else
            {
                return await _client.Operations.GetPositionsAsync(
                    new PositionsRequest { AccountId = accountId },
                    headers);
            }
        }

        public async Task<List<Models.Position>> GetPositionsAsync()
        {
            try
            {
                lock (_positionsLock)
                {
                    // Возвращаем кэшированные позиции из стрима

                    //Debug.WriteLine($"DEBUG - GetPositionsAsync() - _positions={_positions.Count}");

                    return _positions.ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения позиций");
                return new List<Models.Position>();
            }
        }

       

        #region Дополнительные торговые методы (для стратегий)
        public async Task<decimal> GetCurrentPriceAsync(string instrumentUid)
        {
            try
            {
                if (_client == null)
                    return 0; // Возвращаем 0 вместо исключения

                var headers = CreateHeaders();
                var request = new GetLastPricesRequest
                {
                    InstrumentId = { instrumentUid }
                };

                var response = await _client.MarketData.GetLastPricesAsync(request, headers: headers);
                var lastPrice = response.LastPrices.FirstOrDefault();

                if (lastPrice != null && lastPrice.Price != null)
                {
                    return ConvertQuotationToDecimal(lastPrice.Price);
                }

                return 0; // Возвращаем 0 если нет данных
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения текущей цены для {InstrumentUid}", instrumentUid);
                return 0;
            }
        }

        public async Task<decimal> CalculateATRAsync(string instrumentUid, int period = 14, string interval = "1day")
        {
            try
            {
                lock (_cacheLock)
                {
                    // Проверяем кэш (обновляем не чаще 3 раз в секунду)
                    if (_atrCache.TryGetValue($"{instrumentUid}_{period}_{interval}", out var cached) &&
                        (DateTime.UtcNow - cached.lastUpdate).TotalMilliseconds < 333)
                    {
                        return cached.atr;
                    }
                }

                // Получаем исторические свечи
                var endTime = DateTime.Now;
                var startTime = endTime.AddDays(-30); // Берем данные за 30 дней
                var tinkoffInterval = ConvertTimeframeToInterval(interval);

                var candles = await GetHistoricalCandles(instrumentUid, tinkoffInterval, startTime, endTime);

                if (candles.Count < period + 1)
                {
                    _logger.LogWarning("Недостаточно свечей для расчета ATR: {Count}", candles.Count);
                    return 0;
                }

                decimal sumTR = 0;

                for (int i = 1; i < candles.Count; i++)
                {
                    var current = candles[i];
                    var previous = candles[i - 1];

                    var highLow = current.High - current.Low;
                    var highClose = Math.Abs(current.High - previous.Close);
                    var lowClose = Math.Abs(current.Low - previous.Close);

                    var trueRange = Math.Max(highLow, Math.Max(highClose, lowClose));
                    sumTR += trueRange;
                }

                var atr = sumTR / period;

                lock (_cacheLock)
                {
                    _atrCache[$"{instrumentUid}_{period}_{interval}"] = (atr, DateTime.UtcNow);
                }

                return atr;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка расчета ATR для {InstrumentUid}", instrumentUid);
                return 0;
            }
        }

        public async Task<decimal> GetPositionAsync(string accountId, string instrumentUid)
        {
            try
            {
                var positions = await GetPositionsAsync();
                var position = positions.FirstOrDefault(p =>
                    p.AccountId == accountId &&
                    (p.InstrumentUid == instrumentUid || p.Figi == instrumentUid));


                if (position?.Ticker != null || position?.Quantity != 0)
                {
                    //Debug.WriteLine($"DEBUG - GetPositionAsync  Ticker {position?.Ticker} - _positions={positions.Count} /// Quantity={position?.Quantity ?? 0}");
                }

                


                return position?.Quantity ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения позиции для {InstrumentUid}", instrumentUid);
                return 0;
            }
        }

        public async Task<Models.Position> GetPositionObjectAsync(string accountId, string instrumentUid)
        {
            var positions = await GetPositionsAsync();
            return positions.FirstOrDefault(p =>
                p.AccountId == accountId &&
                (p.InstrumentUid == instrumentUid || p.Figi == instrumentUid));
        }


        #endregion
        #endregion

        #region Вспомогательные методы для конвертации статусов
        private OrderStatus ConvertOrderStatus(OrderExecutionReportStatus status)
        {
            return status switch
            {
                OrderExecutionReportStatus.ExecutionReportStatusFill => OrderStatus.Filled,
                OrderExecutionReportStatus.ExecutionReportStatusNew => OrderStatus.New,
                OrderExecutionReportStatus.ExecutionReportStatusCancelled => OrderStatus.Cancelled,
                OrderExecutionReportStatus.ExecutionReportStatusPartiallyfill => OrderStatus.PartiallyFilled,
                OrderExecutionReportStatus.ExecutionReportStatusRejected => OrderStatus.Rejected,
                _ => OrderStatus.Unknown
            };
        }

        private Quotation DecimalToQuotation(decimal value)
        {
            var units = (long)value;
            var nano = (int)((value - units) * 1_000_000_000);
            return new Quotation { Units = units, Nano = nano};
        }
        #endregion





        #region История операций из АПИ

        public async Task<List<Models.Operation>> GetOperationsHistoryAsync(string accountId, DateTime from, DateTime to)
        {
            var operations = new List<Models.Operation>();
            try
            {
                var headers = CreateHeaders();
                string cursor = "";

                while (true)
                {
                    var request = new GetOperationsByCursorRequest
                    {
                        AccountId = accountId,
                        From = Timestamp.FromDateTime(from.ToUniversalTime()),
                        To = Timestamp.FromDateTime(to.ToUniversalTime()),
                        Cursor = cursor,
                        Limit = 100
                    };

                    var response = await _client.Operations.GetOperationsByCursorAsync(request, headers);

                    foreach (var tinkoffOp in response.Items)
                    {

                        


                        var operation = new Models.Operation
                        {
                            Id = tinkoffOp.Id ?? "",
                            
                            ParentOperationId = tinkoffOp.ParentOperationId ?? "",
                            Currency = "RUB",  // Currency не входит в OperationItem, ставим RUB
                            InstrumentUid = tinkoffOp.InstrumentUid ?? "",
                            InstrumentType = tinkoffOp.InstrumentType ?? "",
                            Figi = tinkoffOp.Figi ?? "",
                            InstrumentUidFrom = "",  // Не существует
                            InstrumentUidTo = "",    // Не существует
                            PositionUid = tinkoffOp.PositionUid ?? "",
                            Ticker = await GetTickerByInstrumentUid(tinkoffOp.InstrumentUid),
                            AssetUid = tinkoffOp.AssetUid ?? "",
                            AssetType = "",  // AssetType не входит
                            OperationType = tinkoffOp.Type.ToString(),  // Используем Type (это enum)
                            State = tinkoffOp.State.ToString(),
                            Quantity = tinkoffOp.Quantity,
                            QuantityRest = tinkoffOp.QuantityRest,
                            Price = tinkoffOp.Price != null ? ConvertMoneyValueToDecimal(tinkoffOp.Price) : 0,
                            Payment = tinkoffOp.Payment != null ? ConvertMoneyValueToDecimal(tinkoffOp.Payment) : 0,
                            Commission = tinkoffOp.Commission != null ? ConvertMoneyValueToDecimal(tinkoffOp.Commission) : 0,
                            Date = tinkoffOp.Date.ToDateTime(),
                            OperationTypeName = tinkoffOp.Name ?? tinkoffOp.Type.ToString(),  // Используем Name поле
                            Yield = tinkoffOp.Yield != null ? ConvertMoneyValueToDecimal(tinkoffOp.Yield) : 0,
                            YieldRelative = tinkoffOp.YieldRelative != null ? ConvertQuotationToDecimal(tinkoffOp.YieldRelative) : 0,
                            AveragePositionPrice = 0,  // Не существует
                            OperationId = tinkoffOp.Id ?? ""
                        };
                        operations.Add(operation);

                        // Отладка: выводим первые несколько операций
                        if (operations.Count <= 10)
                        {
                            //Debug.WriteLine($"  Operation: Type={operation.OperationType}, Name={operation.OperationTypeName}, Qty={operation.Quantity}, Price={operation.Price}");
                        }
                    }

                    if (string.IsNullOrEmpty(response.NextCursor))
                        break;

                    cursor = response.NextCursor;
                    await Task.Delay(100);
                }

                //Debug.WriteLine($"Загружено операций: {operations.Count}");

                // Выводим уникальные типы операций для отладки
                var uniqueTypes = operations.Select(o => o.OperationType).Distinct().ToList();
               // Debug.WriteLine($"Уникальные типы операций: {string.Join(", ", uniqueTypes)}");

                return operations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения истории операций");
                return operations;
            }
        }

        private async Task<string> GetTickerByInstrumentUid(string instrumentUid)
        {
            if (string.IsNullOrEmpty(instrumentUid))
                return "";

            if (_instrumentsCache.TryGetValue(instrumentUid, out var instrument))
                return instrument.Ticker;

            return "";
        }

       
        #endregion








        public async Task<OrderStatus> GetOrderStatusAsync(string orderId)
        {
            if (string.IsNullOrEmpty(orderId))
            {
                Debug.WriteLine("DEBUG: TinkoffApiService:  OrderId is null or empty");
                return OrderStatus.Unknown;
            }

            if (_client == null)
            {
                Debug.WriteLine("DEBUG: TinkoffApiService:  Client is null");
                return OrderStatus.Unknown;
            }


            return OrderStatus.Filled;



            /* try
             {
                 var headers = CreateHeaders();
                 var accounts = await GetAccountsAsync();

                 if (!accounts.Any())
                 {
                     Debug.WriteLine("DEBUG: TinkoffApiService:  No accounts found");
                     return OrderStatus.Unknown;
                 }

                 // Пробуем для каждого счета
                 foreach (var account in accounts)
                 {
                     try
                     {
                         Debug.WriteLine($"DEBUG: TinkoffApiService:  Trying to get order status for order {orderId} on account {account.Id}");

                         if (IsSandboxMode)
                         {
                             // ПОДХОД 1: Прямой запрос статуса ордера в песочнице
                             try
                             {
                                 *//*var request = new GetOrderStateRequest
                                 {
                                     AccountId = account.Id,
                                     OrderId = orderId
                                 };*//*
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  ----------------------------------------------------------ORDERS");
                                 var request = new GetOrdersRequest
                                 {
                                     AccountId = account.Id,

                                 };

                                 using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                                 var response = await _client.Sandbox.GetSandboxOrdersAsync(
                                     request,
                                     headers: headers,
                                     cancellationToken: cts.Token);


                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Got order status via GetSandboxOrderStateAsync: {response.Orders.Count}");
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  ----------------------------------------------------------ORDERS={response.Orders.Count}");



                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  ----------------------------------------------------------STOP-ORDERS");
                                 var requestStoordes = new GetStopOrdersRequest
                                 {
                                     AccountId = account.Id,

                                 };

                                 using var ctsst = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                                 var responseStoordes =  _client.Sandbox.GetSandboxOrders(
                                     request,
                                     headers: headers,
                                     cancellationToken: ctsst.Token);


                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Got order status via GetSandboxOrderStateAsync: {responseStoordes.Orders.Count}");
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  ----------------------------------------------------------STOP-ORDERS={responseStoordes.Orders.Count}");






                                 if (response != null || responseStoordes != null)
                                 {
                                     foreach (var item in response.Orders)
                                     {
                                         if (item.OrderId == orderId)
                                         {
                                             Debug.WriteLine($"DEBUG: TinkoffApiService:  Ордер активен: {item.OrderId}");
                                             return OrderStatus.Pending;
                                         }




                                     }







                                     //Debug.WriteLine($"DEBUG: TinkoffApiService:  Got order status via GetSandboxOrderStateAsync: {response.ExecutionReportStatus}");
                                     //return ConvertTinkoffStatusToOrderStatus(response.ExecutionReportStatus);
                                 }
                                 else
                                 {
                                     Debug.WriteLine($"DEBUG: TinkoffApiService:  Ордера не найдены, значит исполнены");

                                     return OrderStatus.Filled;
                                 }

                             }
                             catch (RpcException rpcEx1) when (rpcEx1.StatusCode == StatusCode.Unimplemented)
                             {
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  GetSandboxOrderStateAsync not implemented: {rpcEx1.Message}");
                                 // Пробуем другой подход
                             }
                             catch (RpcException rpcEx1) when (rpcEx1.StatusCode == StatusCode.NotFound)
                             {
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Order {orderId} not found on account {account.Id}");
                                 //continue; // Пробуем следующий счет

                             }
                         }
                         else
                         {
                             // Реальный режим
                             try
                             {
                                 var request = new GetOrderStateRequest
                                 {
                                     AccountId = account.Id,
                                     OrderId = orderId
                                 };

                                 using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                                 var response = await _client.Orders.GetOrderStateAsync(
                                     request,
                                     headers: headers,
                                     cancellationToken: cts.Token);

                                 if (response != null)
                                 {
                                     Debug.WriteLine($"DEBUG: TinkoffApiService:  Got order status via GetOrderStateAsync: {response.ExecutionReportStatus}");
                                     return ConvertTinkoffStatusToOrderStatus(response.ExecutionReportStatus);
                                 }
                             }
                             catch (RpcException rpcEx2) when (rpcEx2.StatusCode == StatusCode.NotFound)
                             {
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Order {orderId} not found on account {account.Id}");
                                 continue; // Пробуем следующий счет
                             }
                         }

                         // ПОДХОД 2: Получаем список всех активных ордеров и ищем нужный
                         *//*try
                         {
                             List<Tinkoff.InvestApi.V1.Order> orders = new List<Tinkoff.InvestApi.V1.Order>();

                             if (IsSandboxMode)
                             {
                                 var ordersResponse = await _client.Sandbox.GetSandboxOrdersAsync(
                                     new GetOrdersRequest { AccountId = account.Id },
                                     headers: headers);

                                 if (ordersResponse?.Orders != null)
                                 {
                                     orders.AddRange(ordersResponse.Orders);
                                 }
                             }
                             else
                             {
                                 var ordersResponse = await _client.Orders.GetOrdersAsync(
                                     new GetOrdersRequest { AccountId = account.Id },
                                     headers: headers);

                                 if (ordersResponse?.Orders != null)
                                 {
                                     orders.AddRange(ordersResponse.Orders);
                                 }
                             }

                             var order = orders.FirstOrDefault(o => o.OrderId == orderId);
                             if (order != null)
                             {
                                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Found order in orders list: {order.ExecutionReportStatus}");
                                 return ConvertTinkoffStatusToOrderStatus(order.ExecutionReportStatus);
                             }
                         }
                         catch (Exception ex)
                         {
                             Debug.WriteLine($"DEBUG: TinkoffApiService:  Error getting orders list: {ex.Message}");
                         }*//*

                         // ПОДХОД 3: Проверяем через операции (для исполненных ордеров)
                         try
                         {
                             var operationsRequest = new OperationsRequest
                             {
                                 AccountId = account.Id,
                                 From = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-1)), // За последние 24 часа
                                 To = Timestamp.FromDateTime(DateTime.UtcNow),
                                 State = OperationState.Executed
                             };

                             OperationsResponse operationsResponse;

                             if (IsSandboxMode)
                             {
                                 operationsResponse = await _client.Sandbox.GetSandboxOperationsAsync(
                                     operationsRequest,
                                     headers: headers);
                             }
                             else
                             {
                                 operationsResponse = await _client.Operations.GetOperationsAsync(
                                     operationsRequest,
                                     headers: headers);
                             }

                             if (operationsResponse?.Operations != null)
                             {
                                 // Ищем операцию связанную с нашим ордером
                                 foreach (var operation in operationsResponse.Operations)
                                 {
                                     // Проверяем разные возможные связи
                                     if (operation.ParentOperationId == orderId ||
                                         operation.Id == orderId ||
                                         (operation.Trades != null && operation.Trades.Any(t => t.TradeId == orderId)))
                                     {
                                         Debug.WriteLine($"DEBUG: TinkoffApiService:  Found operation for order {orderId}: State={operation.State}");

                                         // Если есть операция со статусом Executed, значит ордер исполнен
                                         if (operation.State == OperationState.Executed)
                                         {
                                             return OrderStatus.Filled;
                                         }
                                     }
                                 }
                             }
                         }
                         catch (Exception ex)
                         {
                             Debug.WriteLine($"DEBUG: TinkoffApiService:  Error checking operations: {ex.Message}");
                         }

                         // ПОДХОД 4: Проверяем через стоп-ордеры (если применимо)
                         *//*try
                         {
                             if (!IsSandboxMode)
                             {
                                 // Для реального счета проверяем стоп-ордеры
                                 var stopOrdersRequest = new GetStopOrdersRequest
                                 {
                                     AccountId = account.Id
                                 };

                                 var stopOrdersResponse = await _client.StopOrders.GetStopOrdersAsync(
                                     stopOrdersRequest,
                                     headers: headers);

                                 if (stopOrdersResponse?.StopOrders != null)
                                 {
                                     var stopOrder = stopOrdersResponse.StopOrders
                                         .FirstOrDefault(so => so.StopOrderId == orderId);

                                     if (stopOrder != null)
                                     {
                                         Debug.WriteLine($"DEBUG: TinkoffApiService:  Found stop order: {stopOrder.Status}");
                                         return ConvertTinkoffStatusToOrderStatus(stopOrder.Status);
                                     }
                                 }
                             }
                         }
                         catch (Exception ex)
                         {
                             Debug.WriteLine($"DEBUG: TinkoffApiService:  Error checking stop orders: {ex.Message}");
                         }*//*
                     }
                     catch (Exception accountEx)
                     {
                         Debug.WriteLine($"DEBUG: TinkoffApiService:  Error for account {account.Id}: {accountEx.Message}");
                         // Продолжаем с другим счетом
                     }
                 }

                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Order {orderId} not found on any account");
                 return OrderStatus.NotFound;
             }
             catch (RpcException rpcEx)
             {
                 Debug.WriteLine($"DEBUG: TinkoffApiService:  RPC error getting order status: {rpcEx.StatusCode} - {rpcEx.Status.Detail}");

                 // Обрабатываем специфичные ошибки
                 if (rpcEx.StatusCode == StatusCode.Unauthenticated)
                 {
                     _logger.LogError(rpcEx, "Authentication error getting order status");
                 }
                 else if (rpcEx.StatusCode == StatusCode.PermissionDenied)
                 {
                     _logger.LogError(rpcEx, "Permission denied getting order status");
                 }

                 return OrderStatus.Unknown;
             }
             catch (OperationCanceledException)
             {
                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Timeout getting order status for {orderId}");
                 return OrderStatus.Timeout;
             }
             catch (Exception ex)
             {
                 Debug.WriteLine($"DEBUG: TinkoffApiService:  Fatal error getting order status: {ex.GetType().Name}: {ex.Message}");
                 _logger.LogError(ex, "Critical error getting order status {OrderId}", orderId);
                 return OrderStatus.Unknown;
             }*/
        }




        /// <summary>
        /// Конвертирует статус Tinkoff в наш OrderStatus
        /// </summary>
        private OrderStatus ConvertTinkoffStatusToOrderStatus(OrderExecutionReportStatus tinkoffStatus)
        {
            return tinkoffStatus switch
            {
                OrderExecutionReportStatus.ExecutionReportStatusUnspecified => OrderStatus.Unknown,
                OrderExecutionReportStatus.ExecutionReportStatusFill => OrderStatus.Filled,
                OrderExecutionReportStatus.ExecutionReportStatusNew => OrderStatus.New,
                OrderExecutionReportStatus.ExecutionReportStatusCancelled => OrderStatus.Cancelled,
                OrderExecutionReportStatus.ExecutionReportStatusPartiallyfill => OrderStatus.PartiallyFilled,
                OrderExecutionReportStatus.ExecutionReportStatusRejected => OrderStatus.Rejected,
                _ => OrderStatus.Unknown
            };
        }

     









        public async Task<List<Models.Order>> GetActiveOrdersAsync(string accountId, string instrumentUid = null)
        {
            try
            {
                var headers = CreateHeaders(); // Создаем заголовки

                var request = new GetOrdersRequest
                {
                    AccountId = accountId
                };

                // Передаем заголовки в вызов
                var response = await _client.Orders.GetOrdersAsync(
                    request,
                    headers: headers); // ← ИСПРАВЛЕНИЕ: передаем headers!

                var activeOrders = response.Orders
                    .Where(o => o.ExecutionReportStatus == OrderExecutionReportStatus.ExecutionReportStatusNew ||
                               o.ExecutionReportStatus == OrderExecutionReportStatus.ExecutionReportStatusPartiallyfill)
                    .ToList();

                if (!string.IsNullOrEmpty(instrumentUid))
                {
                    activeOrders = activeOrders
                        .Where(o => o.InstrumentUid == instrumentUid || o.Figi == instrumentUid)
                        .ToList();
                }

                return activeOrders.Select(o => new Models.Order
                {
                    OrderId = o.OrderId,
                    Type = o.OrderType.ToString(),
                    Direction = o.Direction.ToString(),
                    Quantity = (int)o.LotsRequested,
                    ExecutedQuantity = (int)o.LotsExecuted,
                    Price = (o.InitialOrderPrice),
                    Time = o.OrderDate?.ToDateTime() ?? DateTime.UtcNow,
                    Status = o.ExecutionReportStatus.ToString()
                }).ToList();
            }
            catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unauthenticated)
            {
                _logger.LogError(rpcEx, "Authentication error getting active orders for account {AccountId}. Check your token.", accountId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active orders for account {AccountId}", accountId);
                throw;
            }
        }

        public async Task<bool> CancelOrderAsync(string accountId, string orderId)
        {
            try
            {
                var request = new CancelOrderRequest
                {
                    AccountId = accountId,
                    OrderId = orderId
                };

                var response = await _client.Orders.CancelOrderAsync(request);

                _logger.LogInformation("Order {OrderId} cancelled at {Time}", orderId, response.Time);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling order {OrderId}", orderId);
                return false;
            }
        }


        // Добавьте этот метод в класс TinkoffApiService
        public async Task<bool> CancelAllOrdersAsync(string accountId, string instrumentUid = null)
        {
            try
            {
                var activeOrders = await GetActiveOrdersAsync(accountId, instrumentUid);

                if (!activeOrders.Any())
                {
                    return true;
                }

                var results = new List<bool>();

                foreach (var order in activeOrders)
                {
                    try
                    {
                        var success = await CancelOrderAsync(order.OrderId, accountId);
                        results.Add(success);
                        await Task.Delay(100); // Небольшая задержка между отменами
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error cancelling order {OrderId}", order.OrderId);
                        results.Add(false);
                    }
                }

                return results.All(r => r) && results.Any();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling all orders for account {AccountId}", accountId);
                return false;
            }
        }

        private void OnConnectionLost()
        {
            try
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Соединение потеряно. Обновляем статусы рынков...");

                // Обновляем статусы рынков на "Нет данных"
                var statuses = new List<MarketStatus>
        {
            new MarketStatus
            {
                Name = "Фондовый рынок MOEX",
                Status = "Соединение потеряно",
                IsTrading = false,
                LastUpdate = DateTime.Now
            },
            new MarketStatus
            {
                Name = "Срочный рынок MOEX",
                Status = "Соединение потеряно",
                IsTrading = false,
                LastUpdate = DateTime.Now
            }
        };

                OnMarketStatusesUpdated?.Invoke(statuses);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Ошибка обновления статусов при потере соединения: {ex.Message}");
            }
        }

        private async void OnReconnectCompletedHandler()
        {
            try
            {
                Debug.WriteLine($"TinkoffApiService: OnReconnectCompletedHandler: [{DateTime.Now:HH:mm:ss.fff}] Реконнект завершен. Восстанавливаем подписки на статусы...");

                // Даем время на стабилизацию соединения
                await Task.Delay(3000);


                // Проверяем подключение
                if (await CheckConnectionAsync())
                {
                    Debug.WriteLine("TinkoffApiService: OnReconnectCompletedHandler: Соединение стабильно, обновляем статусы...");

                    // Не сбрасываем флаг, а проверяем текущее состояние
                    bool needResubscribe;

                    lock (_marketStatusLock)
                    {
                        needResubscribe = !_marketStatusSubscribed;
                    }

                    if (needResubscribe)
                    {
                        await SubscribeToMarketStatusIndicators();
                        Debug.WriteLine($"TinkoffApiService: OnReconnectCompletedHandler: [{DateTime.Now:HH:mm:ss.fff}] Подписки на статусы восстановлены после реконнекта");
                    }

                }

               
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TinkoffApiService: OnReconnectCompletedHandler: [{DateTime.Now:HH:mm:ss.fff}] Ошибка восстановления подписок после реконнекта: {ex.Message}");
            }
        }






        public async ValueTask DisposeAsync()
        {

            // ✅ ОЧИЩАЕМ ВСЕ СЧЕТЧИКИ ПРИ УНИЧТОЖЕНИИ
            await ClearAllSubscriptionsAsync();

            /* try
             {
                 await StopMarketDataStreamAsync();
                 _subscriptions.Clear();

                 if (_client != null)
                 {
                     await _client.DisposeAsync();
                 }

                 _streamLock?.Dispose();
             }
             catch (Exception ex)
             {
                 _logger.LogError(ex, "Ошибка при освобождении ресурсов");
             }*/
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }
    }

    
}