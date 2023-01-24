using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Objects.Models.Spot.Socket;
using BinanceBot.Models;
using BinanceBot.Services;
using BinanceBot.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BinanceBot.ViewModels
{
    public class MainViewModel : NotifyBase
    {
        private readonly BinanceManager _binanceManager;
        private readonly StrategiesManager _strategiesManager;
        private readonly CandlesManager _candlesManager;
        public ObservableCollection<ITabViewModel> TabViewModels { get; private set; }
        public Dictionary<string, Dictionary<int, IStrategyViewModel>> SymbolTimeFrameStrategyViewModels = new();
        private readonly ConcurrentQueue<Candle> _newProcessedCandlesQueue = new();
        private readonly ControlViewModel _controlViewModel;
        private ILogger<MainViewModel> _logger; 
        public string Title { get; private set; }

        public MainViewModel(BinanceManager binanceManager, StrategiesManager strategiesManager, IConfiguration config,
            CandlesManager candlesManager, ILogger<MainViewModel> logger, ControlViewModel controlViewModel)
        {
            Title = $"Binance Bot - {config["PipeName"]}";
            _binanceManager = binanceManager;
            _strategiesManager = strategiesManager;
            _candlesManager = candlesManager;
            _controlViewModel = controlViewModel;
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            // initialize strategies from python server
            _strategiesManager.Initialize();
            
            // initialize strategies from Binance API
            var binanceSymbols = await _binanceManager.InitializeAsync();
            
            // synchronize symbols with strategies
            _strategiesManager.SynchronizeSymbols(binanceSymbols);

            // initialize control view model
            await _controlViewModel.Initialize(_strategiesManager.GetTradedSymbols);
            
            // start trades WebSocket
            await _binanceManager.StartWebSocketsAsync(_strategiesManager.GetTradedSymbols, 
                tradeEvent =>
                {
                    var symbol = tradeEvent.Data.Symbol;
                    var tradeTime = tradeEvent.Data.TradeTime;
                    _strategiesManager.UpdateLastPrice(symbol, tradeEvent.Data.Price);
                    lock (_candlesManager)
                    {
                        var lastProcessedTime = _candlesManager.GetLastEnqueuedTradeTime(symbol);
                        if (lastProcessedTime != DateTime.MinValue &&
                            tradeTime - lastProcessedTime > TimeSpan.FromSeconds(30))
                        {
                            var trades =
                                _binanceManager.GetAggregatedTradesAsync(symbol,
                                    lastProcessedTime.AddMilliseconds(1), tradeTime.AddMilliseconds(-1)).Result.ToList();
                            if (trades.Any())
                            {
                                foreach (var binanceAggregatedTrade in trades)
                                {
                                    _candlesManager.EnqueueAggTrade(new BinanceStreamAggregatedTrade
                                    {
                                        Symbol = symbol,
                                        Quantity = binanceAggregatedTrade.Quantity,
                                        TradeTime = binanceAggregatedTrade.TradeTime,
                                        Price = binanceAggregatedTrade.Price
                                    });
                                }

                                _logger.LogInformation(
                                    $"Recollected trades via API: {trades.Count()} from {trades.First().TradeTime} till {trades.Last().TradeTime} for {symbol}");
                            }
                        }

                        _candlesManager.EnqueueAggTrade(tradeEvent.Data);
                    }
                },
                orderEvent => _controlViewModel.UpdateOrder(orderEvent.Data.UpdateData),
                accountEvent => _controlViewModel.UpdateAccountInfo(accountEvent.Data.UpdateData));
            
            // start processing candles
            await _candlesManager.StartAsync(_binanceManager);
            
            // start strategies
            _candlesManager.StartStrategies(_strategiesManager);

            // create view models
            CreateViewModels();
            _logger.LogInformation("Bot started");
        }

        private void CreateViewModels()
        {
            TabViewModels = new ObservableCollection<ITabViewModel> {_controlViewModel};
            foreach (var symbolTimeframeStrategyPair in _strategiesManager.Strategies)
            {
                var symbol = symbolTimeframeStrategyPair.Key;
                SymbolTimeFrameStrategyViewModels.Add(symbol, new Dictionary<int, IStrategyViewModel>());
                foreach (var timeframeStrategyPair in symbolTimeframeStrategyPair.Value)
                {
                    var strategyViewModel = new StrategyViewModel(timeframeStrategyPair.Value,
                        _candlesManager.GetLastNCandles(100, symbol, timeframeStrategyPair.Key))
                    {
                        Header = $"{symbol[..3]} {timeframeStrategyPair.Key}"
                    };
                    SymbolTimeFrameStrategyViewModels[symbol].Add(timeframeStrategyPair.Key, strategyViewModel);
                    TabViewModels.Add(strategyViewModel);
                }
            }

            _candlesManager.NewCandle += _newProcessedCandlesQueue.Enqueue;
            var processNewCandleThread = new Thread(() =>
            {
                while (true)
                {
                    var i = 0;
                    while (true)
                    {
                        Candle candle;
                        if (_newProcessedCandlesQueue.TryDequeue(out candle))
                        {
                            i = 0;
                            if (SymbolTimeFrameStrategyViewModels[candle.Symbol].ContainsKey(candle.TimeFrame))
                                SymbolTimeFrameStrategyViewModels[candle.Symbol][candle.TimeFrame].AddCandle(candle);
                        }
                        else
                        {
                            Thread.Sleep(i);
                            i++;
                        }
                    }

                }
            }) {IsBackground = true};
            processNewCandleThread.Start();
        }
    }
}
