using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using BinanceBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;

namespace BinanceBot.Services;

public class StrategiesManager
{
    private readonly ILogger<StrategiesManager> _logger;
    private readonly Dictionary<string, BinanceFuturesUsdtSymbol> _symbols = new();
    private readonly PythonClient _pythonClient;
    public readonly Dictionary<string, Dictionary<int, Strategy>> Strategies = new();
    private readonly ILogger<Strategy> _strategyLogger;
    private readonly BinanceManager _binanceManager;
    private readonly ConcurrentDictionary<string, decimal> _lastPrices = new();
    private Dictionary<string, int> _cancelOrderCounter = new();
    private ObservableCollection<Order> _orders;
    private readonly int _notifyOnCancelCount;
    private readonly Dictionary<string, DateTime> _lastPositionProcessedTimes = new();

    public StrategiesManager(ILogger<StrategiesManager> logger, PythonClient pythonClient, IConfiguration config,
        ILogger<Strategy> strategyLogger, BinanceManager binanceManager, ObservableCollection<Order> orders)
    {
        _logger = logger;
        _pythonClient = pythonClient;
        _strategyLogger = strategyLogger;
        _binanceManager = binanceManager;
        _orders = orders;
        _notifyOnCancelCount = int.Parse(config["NotifyOnCancelCount"]);
    }

    public void Initialize()
    {
        _logger.LogDebug("Gathering metadata from python server");
        var metadata = _pythonClient.GetMetadata();
        foreach (var symbolMetadata in metadata)
        {
            var symbol = symbolMetadata.Key.ToUpper();
            _symbols.Add(symbol, null);
            if (!Strategies.ContainsKey(symbol))
            {
                _lastPositionProcessedTimes.Add(symbol, DateTime.UtcNow - TimeSpan.FromMinutes(5));
                Strategies.Add(symbol, new Dictionary<int, Strategy>());
            }

            if (!_cancelOrderCounter.ContainsKey(symbol))
                _cancelOrderCounter.Add(symbol, 0);
            foreach (var timeframeMetadata in symbolMetadata.Value)
            {
                Strategies[symbol].Add(timeframeMetadata.Key, new Strategy(symbol, timeframeMetadata.Key, timeframeMetadata.Value, _pythonClient, _strategyLogger));
            }
            _logger.LogDebug($"Recieved symbol {symbol} from python. Initialized {Strategies[symbol].Count} strategies");
        }
    }

    public IEnumerable<string> GetTradedSymbols => _symbols.Keys;
    public void SynchronizeSymbols(IEnumerable<BinanceFuturesUsdtSymbol> symbols)
    {
        foreach (var binanceFuturesSymbol in symbols)
        {
            if (_symbols.ContainsKey(binanceFuturesSymbol.Pair))
            {
                _symbols[binanceFuturesSymbol.Pair] = binanceFuturesSymbol;
                foreach (var strategy in Strategies[binanceFuturesSymbol.Pair].Values)
                {
                    strategy.Initialize(binanceFuturesSymbol);
                }
            }
        }
    }

    public async Task CheckStrategiesAsync(string symbol)
    {
        try
        {
            var symbolStrategies = Strategies[symbol].Select(s => s.Value);
            if (symbolStrategies.Any(s => s.DirectionChanged))
            {
                if (_lastPositionProcessedTimes[symbol] > DateTime.UtcNow - TimeSpan.FromSeconds(30))
                {
                    _logger.LogInformation($"Too often position processing for {symbol}. Wait");
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
                var deltaSteps = symbolStrategies.Max(s => s.DeltaSteps);
                var targetPosition = 0m;
                var sendMakeDealsWarning = false;
                foreach (var timeframeStrategy in Strategies[symbol])
                {
                    if (timeframeStrategy.Value.MakeDeals)
                        targetPosition += timeframeStrategy.Value.Volume * timeframeStrategy.Value.Direction;
                    else
                        sendMakeDealsWarning = true;
                }
                if (sendMakeDealsWarning)
                    _logger.LogWarning($"Skip trading as make deals == false for {symbol}");

                var hasActiveOrder = _orders.Any(o => o.Status is OrderStatus.New or OrderStatus.PartiallyFilled);
                await _binanceManager.CancelOrdersAsync(symbol);
                var currentPosition = await _binanceManager.GetBalanceAsync(symbol);
                var toTrade = targetPosition - currentPosition;
                var lastPrice = _lastPrices[symbol];
                if (Math.Abs(toTrade*lastPrice) > 5m)
                {
                    var side = toTrade > 0m ? OrderSide.Buy : OrderSide.Sell;
                    var quantity = Math.Abs(toTrade);
                    var price = lastPrice + (side == OrderSide.Buy ? -1m : 1m) * deltaSteps *
                        _symbols[symbol].PriceFilter.TickSize;
                    await _binanceManager.PlaceOrderAsync(symbol, quantity, price, side);
                    Log.Information(
                        $"{symbol}: Current position: {currentPosition}. Target position: {targetPosition}. Place Order: {side.ToString()} {quantity}@{price}. Last price: {lastPrice}");
                }
                else
                    foreach (var symbolStrategy in symbolStrategies)
                        symbolStrategy.DirectionChanged = false;
                foreach (var symbolStrategy in symbolStrategies)
                {
                    if (symbolStrategy.DeltaSteps != deltaSteps)
                        symbolStrategy.DeltaSteps = deltaSteps;
                }
                if (hasActiveOrder)
                {
                    _cancelOrderCounter[symbol] += 1;
                    if (_cancelOrderCounter[symbol] >= _notifyOnCancelCount)
                        _logger.LogWarning($"Too many cancels for {symbol}");
                }
                else
                    _cancelOrderCounter[symbol] = 0;
            }
            _lastPositionProcessedTimes[symbol] = DateTime.UtcNow;
        }
        catch (Exception exp)
        {
            _logger.LogError(exp, $"Unable to check positions for {symbol}");
        }
    }

    public void UpdateLastPrice(string symbol, decimal lastPrice)
    {
        if (!_lastPrices.ContainsKey(symbol))
            _lastPrices.TryAdd(symbol, lastPrice);
        else
            _lastPrices[symbol] = lastPrice;
    }
}