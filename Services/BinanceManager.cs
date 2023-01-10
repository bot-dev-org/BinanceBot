using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients.UsdFuturesApi;
using Binance.Net.Objects;
using Binance.Net.Objects.Models;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Futures.Socket;
using Binance.Net.Objects.Models.Spot;
using Binance.Net.Objects.Models.Spot.Socket;
using BinanceBot.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.CommonObjects;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using TelegramSink;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace BinanceBot.Services;

public class BinanceManager
{
    private readonly BinanceSocketClient _wsClient;
    private readonly IBinanceClientUsdFuturesApi _futuresClient;
    private readonly ILogger<BinanceManager> _logger;

    public BinanceManager(ILogger<BinanceManager> logger, IConfiguration config)
    {
        var apiKey = config["BinanceAPIKey"];
        var apiSecret = config["BinanceSecretKey"];
        var creds = new ApiCredentials(apiKey, apiSecret);
        var binanceLogger = new SerilogLoggerFactory(new LoggerConfiguration()
            .WriteTo.TeleSink(config["TelegramAPIKey"], config["TelegramChatId"], minimumLevel: LogEventLevel.Warning)
            .WriteTo.File("logs\\Binance.log", LogEventLevel.Debug, rollingInterval: RollingInterval.Day).CreateLogger()).CreateLogger("BinanceClient");
        _logger = logger;
        _futuresClient = new BinanceClient(new BinanceClientOptions
        {
            LogWriters = new List<ILogger> {binanceLogger}, 
            ApiCredentials = creds, 
            ReceiveWindow = TimeSpan.FromSeconds(30), 
            UsdFuturesApiOptions = new BinanceApiClientOptions
            {
                AutoTimestamp = true, 
                ApiCredentials = new ApiCredentials(apiKey, apiSecret),
                TimestampRecalculationInterval = TimeSpan.FromMinutes(30),
                RateLimitingBehaviour = RateLimitingBehaviour.Wait,
            }
        }).UsdFuturesApi;

        _wsClient = new BinanceSocketClient(new BinanceSocketClientOptions
        {
            AutoReconnect = true,
            ReconnectInterval = TimeSpan.FromSeconds(5),
            SocketNoDataTimeout = TimeSpan.FromSeconds(90),
            ApiCredentials = creds,
            LogLevel = LogLevel.Debug,
            LogWriters = new List<ILogger> {binanceLogger}
        });
    }

    public async Task<IEnumerable<BinanceFuturesUsdtSymbol>> InitializeAsync()
    {
        var exchangeInfo = await _futuresClient.ExchangeData.GetExchangeInfoAsync();
        if (!exchangeInfo.Success)
            throw new Exception($"Unable to get exchange data: {exchangeInfo.Error.Message}");
        foreach (var rateLimit in exchangeInfo.Data.RateLimits)
        {
            _logger.LogInformation($"Rate limit {rateLimit.Type.ToString()}: {rateLimit.Limit} per {rateLimit.IntervalNumber} {rateLimit.Interval.ToString()}");
        }

        return exchangeInfo.Data.Symbols;
    }
    
    public async Task StartWebSocketsAsync(IEnumerable<string> symbols, Action<DataEvent<BinanceStreamAggregatedTrade>> aggTradesCallback,
        Action<DataEvent<BinanceFuturesStreamOrderUpdate>> orderCallback, Action<DataEvent<BinanceFuturesStreamAccountUpdate>> accountCallback)
    {
        var listenKey = "";
        while (true)
        {
            try
            {
                listenKey = await Subscribe(symbols, aggTradesCallback, orderCallback, accountCallback);
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to subscribe to listen key");
                await Task.Delay(15000);
            }
        }

        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(TimeSpan.FromMinutes(5));
                    var response = _futuresClient.Account.KeepAliveUserStreamAsync(listenKey).Result;
                    if (!response.Success)
                    {
                        _logger.LogError($"Unable to keep alive: {response.Error.Message}");
                        if (response.Error.Code == -1125)
                        {
                            _logger.LogError($"Try to resubscribe");
                            listenKey = Subscribe(symbols, aggTradesCallback, orderCallback, accountCallback).Result;
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Unable to subscribe");
                }
            }
        }){IsBackground = true}.Start();
    }

    private async Task<string> Subscribe(IEnumerable<string> symbols, Action<DataEvent<BinanceStreamAggregatedTrade>> aggTradesCallback,
        Action<DataEvent<BinanceFuturesStreamOrderUpdate>> orderCallback, Action<DataEvent<BinanceFuturesStreamAccountUpdate>> accountCallback)
    {
        // subscribe to agg trades
        var response = await _wsClient.UsdFuturesStreams.SubscribeToAggregatedTradeUpdatesAsync(symbols, aggTradesCallback);
        if (!response.Success)
            throw new Exception($"Unable to subscribe to agg trades: {response.Error.Message}");
        // subscribe to user data updates
        var listenKeyResponse = await _futuresClient.Account.StartUserStreamAsync();
        if (!listenKeyResponse.Success)
            throw new Exception($"Unable to get listen key: {listenKeyResponse.Error.Message}");
        response = await _wsClient.UsdFuturesStreams.SubscribeToUserDataUpdatesAsync(listenKeyResponse.Data,
            OnLeverageUpdate, OnMarginUpdate, accountCallback, orderCallback, OnListenKeyExpired);
        if (!response.Success)
            throw new Exception($"Unable to subscribe to user data: {response.Error.Message}");
        return listenKeyResponse.Data;
    }

    private void OnListenKeyExpired(DataEvent<BinanceStreamEvent> obj)
    {
        _logger.LogError($"Listen key is about to expire: {JsonSerializer.Serialize(obj)}");
    }

    private void OnMarginUpdate(DataEvent<BinanceFuturesStreamMarginUpdate> obj)
    {
        _logger.LogWarning($"Margin Update: {JsonSerializer.Serialize(obj)}");
    }

    private void OnLeverageUpdate(DataEvent<BinanceFuturesStreamConfigUpdate> obj)
    {
        _logger.LogWarning($"Leverage Update: {JsonSerializer.Serialize(obj)}");
    }

    public async Task<IEnumerable<BinanceFuturesAccountBalance>> GetBalances()
    {
        var response = await _futuresClient.Account.GetBalancesAsync();
        if (!response.Success)
            throw new Exception($"Unable to get balances: {response.Error.Message}");
        return response.Data;
    }
    public async Task<decimal> GetTickerPrice(string symbol)
    {
        var response = await _futuresClient.ExchangeData.GetPriceAsync(symbol);
        if (!response.Success)
            throw new Exception($"Unable to get price of {symbol}: {response.Error.Message}");
        return response.Data.Price;
    }
    public async Task<IEnumerable<Position>> GetPositions()
    {
        var response = await _futuresClient.CommonFuturesClient.GetPositionsAsync();
        if (!response.Success)
            throw new Exception($"Unable to get positions: {response.Error.Message}");
        return response.Data;
    }
    public async Task<IEnumerable<BinanceFuturesUsdtTrade>> GetTrades(IEnumerable<string> symbols, DateTime startTime)
    {
        var result = new List<BinanceFuturesUsdtTrade>();
        foreach (var symbol in symbols)
        {
            var response = await _futuresClient.Trading.GetUserTradesAsync(symbol, startTime, limit: 1000);
            if (!response.Success)
                throw new Exception($"Unable to get trades for {symbol} from {startTime}: {response.Error.Message}");
            result.AddRange(response.Data);
        }

        return result;
    }
    public async Task<IEnumerable<BinanceAggregatedTrade>> GetAggregatedTradesAsync(string symbol, DateTime startTime, DateTime endTime)
    {
        var result = new List<BinanceAggregatedTrade>();
        var limit = 1000;
        var startTimeToLoad = startTime;
        while(true)
        {
            var tradesResult = await _futuresClient.ExchangeData.GetAggregatedTradeHistoryAsync(symbol, startTime: startTimeToLoad, endTime: endTime,
                limit: limit);
            if (!tradesResult.Success)
                throw new Exception($"Unable to get trades: {tradesResult.Error.Message}");
            var trades = tradesResult.Data;
            result.AddRange(trades);
            if (trades.Count() < limit)
                return result;
            startTimeToLoad = trades.Max(t => t.TradeTime).AddMilliseconds(1);
        }
    }
    public async Task<decimal> GetBalanceAsync(string symbol)
    {
        var positions = await _futuresClient.Account.GetPositionInformationAsync(symbol); 
        if (!positions.Success)
        {
            throw new Exception($"Unable to get balance: {positions.Error.Message}");
        }

        return positions.Data.Sum(position => position.Quantity);
    }
    public async Task PlaceOrderAsync(string symbol, decimal quantity, decimal price, OrderSide side)
    {
        var response = await _futuresClient.Trading.PlaceOrderAsync(symbol, side, FuturesOrderType.Limit, quantity, price, timeInForce:TimeInForce.GoodTillCanceled);
        if (!response.Success)
        {
            throw new Exception($"Unable to place order: {response.Error.Message}");
        }
    }
    public async Task CancelOrdersAsync(string symbol)
    {
        var response = await _futuresClient.Trading.CancelAllOrdersAsync(symbol);
        if (!response.Success)
        {
            throw new Exception($"Unable to cancel orders: {response.Error.Message}");
        }
    }
    public async Task<IEnumerable<Candle>> GetCandles(string symbol, int timeframe, DateTime startTime, DateTime endTime)
    {
        var interval = KlineInterval.OneMinute;
        switch (timeframe)
        {
            case 3:
                interval = KlineInterval.ThreeMinutes;
                break;
            case 10:
            case 5:
                interval = KlineInterval.FiveMinutes;
                break;
        }

        var limit = 499;
        var klines = new List<IBinanceKline>();
        var requestStartTime = startTime;
        while (true)
        {
            var nextKLinesResult = await _futuresClient.ExchangeData.GetKlinesAsync(symbol, interval, requestStartTime, endTime, limit);
            if (!nextKLinesResult.Success)
            {
                _logger.LogError($"Unable to get klines: {nextKLinesResult.Error.Message}");
                continue;
            }
            

            var nextKLines = nextKLinesResult.Data;
            klines.AddRange(nextKLines);
            if (nextKLines.Count() < limit)
                break;
            requestStartTime = klines.Max(k => k.OpenTime) + TimeSpan.FromMinutes(timeframe);
        }

        var candles = new List<Candle>();
        if (klines.Any())
        {
            var firstKLine = klines.First();
            var candle = new Candle()
            {
                Symbol = symbol, Time = firstKLine.OpenTime,
                ClosePrice = firstKLine.ClosePrice, TimeFrame = timeframe
            };
            candles.Add(candle);
            var prevClose = firstKLine.ClosePrice;
            foreach (var kline in klines)
            {
                if (kline.OpenTime - candle.Time >= TimeSpan.FromMinutes(timeframe))
                {
                    candle.ClosePriceDiff = candle.ClosePrice - prevClose;
                    prevClose = candle.ClosePrice;
                    candle = new Candle
                    {
                        Symbol = symbol, Time = kline.OpenTime, Volume = kline.Volume,
                        ClosePrice = kline.ClosePrice, TimeFrame = timeframe
                    };
                    if (60 % timeframe == 0)
                        candle.Time = candle.Time.AddMinutes(candle.Time.Minute % timeframe * -1);
                    candles.Add(candle);
                }
                else
                {
                    candle.ClosePrice = kline.ClosePrice;
                    candle.Volume += kline.Volume;
                }
            }
        }

        return candles;
    }
}