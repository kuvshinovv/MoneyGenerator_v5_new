using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using static MoneyGenerator_v5.Services.TinkoffApiService;

namespace MoneyGenerator_v5.Services
{
    public class AlorApiService : IProvirerService, IDisposable
    {
        private readonly ILogger<AlorApiService>? _logger;
        private readonly TokenManager _tokenManager;
        public string ProviderName => "Алор";
        public bool IsConnected => throw new NotImplementedException();

        public bool IsSandboxMode => throw new NotImplementedException();
        private ProgressCallback _progressCallback;


        public AlorApiService(ILogger<AlorApiService> logger, TokenManager tokenManager)
        {
            _logger = (ILogger<AlorApiService>?)logger;
            _tokenManager = tokenManager;
        }





        public Task<bool> ConnectAsync(bool isSandbox)
        {
            Debug.WriteLine($"Успешно подключено к Алор API");
            _logger.LogInformation("Успешно подключено к Алор API");
            return Task.FromResult( true );
        }

        public Task DisconnectAsync()
        {
            Debug.WriteLine($"Успешно отключено от Алор API");
            _logger.LogInformation("Успешно отключено от Алор API");
            return Task.FromResult(true);
        }

       

        public Task<List<Account>> GetAccountsAsync()
        {
            throw new NotImplementedException();
        }

        public Task<List<Instrument>> GetInstrumentsAsync()
        {
            throw new NotImplementedException();
        }

        public Task SubscribeToMarketDataAsync(string instrumentId, Action<MarketData> onDataReceived)
        {
            throw new NotImplementedException();
        }

        public Task UnsubscribeFromMarketDataAsync(string instrumentId)
        {
            throw new NotImplementedException();
        }




        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public Task<List<Candle>> GetHistoricalCandles(string instrumentUid, Tinkoff.InvestApi.V1.CandleInterval interval, DateTime from, DateTime to)
        {
            throw new NotImplementedException();
        }

        public Task<List<Candle>> GetHistoricalDataAsync(string tiker, string instrumentUid, string timeframe, DateTime startTime, DateTime endTime)
        {
            throw new NotImplementedException();
        }

        public Task SubscribeToCandlesAsync(string instrumentId, string candleInterval, Action<CandleUpdate> onCandleUpdate)
        {
            throw new NotImplementedException();
        }

        public Task UnsubscribeFromCandlesAsync(string instrumentId, string candleInterval)
        {
            throw new NotImplementedException();
        }

        public Task<Result> PlaceOrderAsync(Order order)
        {
            throw new NotImplementedException();
        }

        public Task<OrderStatus> GetOrderStatusAsync(string orderId)
        {
            throw new NotImplementedException();
        }

        public Task CancelOrderAsync(string orderId)
        {
            throw new NotImplementedException();
        }

        public Task<decimal> GetAccountBalanceAsync()
        {
            throw new NotImplementedException();
        }

        public Task<List<Position>> GetPositionsAsync()
        {
            throw new NotImplementedException();
        }

        public Task<bool> CancelOrderAsync(string orderId, string? accountId = null)
        {
            throw new NotImplementedException();
        }

        public Task<decimal> GetCurrentPriceAsync(string instrumentUid)
        {
            throw new NotImplementedException();
        }

        public Task<decimal> CalculateATRAsync(string instrumentUid, int period = 14, string interval = "1day")
        {
            throw new NotImplementedException();
        }

        public Task<decimal> GetPositionAsync(string accountId, string instrumentUid)
        {
            throw new NotImplementedException();
        }

        public Task<Position> GetPositionObjectAsync(string accountId, string instrumentUid)
        {
            throw new NotImplementedException();
        }

        public Task<bool> ReconnectAsync()
        {
            throw new NotImplementedException();
        }

        public Task<bool> CheckConnectionAsync()
        {
            throw new NotImplementedException();
        }

        public Task<bool> CancelAllOrdersAsync(string accountId, string instrumentUid = null)
        {
            throw new NotImplementedException();
        }

        public Task<List<Order>> GetActiveOrdersAsync(string accountId, string instrumentUid = null)
        {
            throw new NotImplementedException();
        }

        public Task RefreshPositionsAsync(string accountId = null)
        {
            throw new NotImplementedException();
        }

        Task<decimal> IProvirerService.RefreshPositionsAsync(string accountId)
        {
            throw new NotImplementedException();
        }

        public Task<decimal> LoadCurrentPositionsAsync(string accountId)
        {
            throw new NotImplementedException();
        }

        public Task UpdateMarketStatusesAsync()
        {
            throw new NotImplementedException();
        }

        public Task<List<Operation>> GetOperationsHistoryAsync(string accountId, DateTime from, DateTime to)
        {
            throw new NotImplementedException();
        }

        public Task<Position> GetPositionQuantity(string accountId, string instrumentUid, string ticker = null)
        {
            throw new NotImplementedException();
        }

        // метод для установки callback  для прогрессбара в оптимизации
        public void SetProgressCallback(ProgressCallback callback)
        {
            _progressCallback = callback;
        }
    }
}
