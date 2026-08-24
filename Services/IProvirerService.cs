using MoneyGenerator_v5.Models;
using System;
using System.Collections.Generic;
using System.Text;
using Tinkoff.InvestApi.V1;
using static Tinkoff.InvestApi.V1.OrderStateStreamResponse.Types;

namespace MoneyGenerator_v5.Services
{
    public interface IProvirerService
    {
        Task<bool> ConnectAsync(bool isSandbox);
        Task DisconnectAsync();
        Task<List<Models.Account>> GetAccountsAsync();
        Task<List<Models.Instrument>> GetInstrumentsAsync();
        Task SubscribeToMarketDataAsync(string instrumentId, Action<MarketData> onDataReceived);
        Task UnsubscribeFromMarketDataAsync(string instrumentId);

        Task<List<Models.Candle>> GetHistoricalCandles(string instrumentUid, CandleInterval interval, DateTime from, DateTime to);
        Task<List<Models.Candle>> GetHistoricalDataAsync(string tiker, string instrumentUid, string timeframe, DateTime startTime, DateTime endTime);

        Task SubscribeToCandlesAsync(string instrumentId, string candleInterval, Action<CandleUpdate> onCandleUpdate);
        Task UnsubscribeFromCandlesAsync(string instrumentId, string candleInterval);




        Task<Result> PlaceOrderAsync(Models.Order order);
        Task<OrderStatus> GetOrderStatusAsync(string orderId);
        Task<bool> CancelOrderAsync(string orderId, string? accountId = null);
        Task<decimal> GetAccountBalanceAsync();
        Task<List<Position>> GetPositionsAsync();



        Task<decimal> RefreshPositionsAsync(string accountId = null);

        Task<decimal> LoadCurrentPositionsAsync(string accountId);






        // Новые методы для стратегий
        Task<decimal> GetCurrentPriceAsync(string instrumentUid);
        Task<decimal> CalculateATRAsync(string instrumentUid, int period = 14, string interval = "1day");
        Task<decimal> GetPositionAsync(string accountId, string instrumentUid);
        Task<Models.Position> GetPositionObjectAsync(string accountId, string instrumentUid);

        // Дополнительные методы
        Task<bool> ReconnectAsync();
        Task<bool> CheckConnectionAsync();



        Task<bool> CancelAllOrdersAsync(string accountId, string instrumentUid = null);
        Task<List<Models.Order>> GetActiveOrdersAsync(string accountId, string instrumentUid = null);

        Task UpdateMarketStatusesAsync();

        Task<List<Models.Operation>> GetOperationsHistoryAsync(string accountId, DateTime from, DateTime to);
        Task<Position> GetPositionQuantity(string accountId, string instrumentUid, string ticker = null);


        string ProviderName { get; } // Добавляем это свойство




        bool IsConnected { get; }
        bool IsSandboxMode { get; }
    }



    public class Result
    {
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; }
        public string OrderId { get; internal set; }
        public string Message { get; internal set; }
    }


}
