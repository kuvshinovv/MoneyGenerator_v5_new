using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MoneyGenerator_v5.Services
{
    internal class FinamApiService : IProvirerService, IDisposable
    {
        private readonly ILogger<FinamApiService>? _logger;
        private readonly TokenManager _tokenManager;

        public string ProviderName => "Финам";
        public bool IsConnected => throw new NotImplementedException();

        public bool IsSandboxMode => throw new NotImplementedException();



        public FinamApiService(ILogger<FinamApiService> logger, TokenManager tokenManager)
        {
            _logger = (ILogger<FinamApiService>?)logger;
            _tokenManager = tokenManager;
        }





        public Task<bool> ConnectAsync(bool isSandbox)
        {
            Debug.WriteLine($"Успешно подключено к Финам API");
            _logger.LogInformation("Успешно подключено к Финам API");
            return Task.FromResult(true);
        }

        public Task DisconnectAsync()
        {
            Debug.WriteLine($"Успешно отключено от Финам API");
            _logger.LogInformation("Успешно отключено от Финам API");
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
    }
}
