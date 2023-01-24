using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot.Socket;
using BinanceBot.Models;
using Microsoft.Extensions.Logging;

namespace BinanceBot.Services;

public class CandlesManager
{
    private readonly string _candlesDataFolder = "Candles";
    private readonly ILogger<CandlesManager> _logger;
    private readonly Dictionary<string, SortedDictionary<int, List<Candle>>> _symbolTimeFrameCandles = new();
    private readonly Dictionary<string, SortedDictionary<int, Candle>> _symbolTimeFrameInProgressCandles = new();
    private readonly Dictionary<string, SortedDictionary<int, string>> _symbolTimeFrameFileNames = new();
    private readonly Dictionary<string, SortedDictionary<int, DateTime>> _symbolTimeFrameLastProcessedCandleTimes = new();
    private readonly ConcurrentQueue<BinanceStreamAggregatedTrade> _aggregatedTradesQueue;
    private readonly Dictionary<string, DateTime> _symbolLastProcessedTickTime = new();
    private readonly Dictionary<string, DateTime> _symbolLastEnqueuedTickTime = new();

    public event Action<Candle> NewCandle;
    public event Action<string> ProcessedNewCandleTrade;

    public CandlesManager(ILogger<CandlesManager> logger, ConcurrentQueue<BinanceStreamAggregatedTrade> aggregatedTradesQueue)
    {
        _aggregatedTradesQueue = aggregatedTradesQueue;
        _logger = logger;

        if (!Directory.Exists(_candlesDataFolder))
        {
            _logger.LogError($"Candle directory not exists: {_candlesDataFolder}");
        }
    }

    public DateTime GetLastEnqueuedTradeTime(string symbol)
    {
        return !_symbolLastEnqueuedTickTime.ContainsKey(symbol) ? DateTime.MinValue : _symbolLastEnqueuedTickTime[symbol];
    }
    public IEnumerable<Candle> GetLastNCandles(int N, string symbol, int timeframe)
    {
        var candles = _symbolTimeFrameCandles[symbol][timeframe];
        if (N >= candles.Count)
            return candles;
        return candles.GetRange(candles.Count - N - 1, N);
    }
    public async Task StartAsync(BinanceManager binanceManager)
    {
        foreach (var directory in Directory.GetDirectories(_candlesDataFolder))
        {
            var symbol = Path.GetFileName(directory).ToUpper();
            var timeframeCandles = new SortedDictionary<int, List<Candle>>();
            _symbolTimeFrameCandles.Add(symbol, timeframeCandles);
            var timeframeInProgressCandles = new SortedDictionary<int, Candle>();
            _symbolTimeFrameInProgressCandles.Add(symbol, timeframeInProgressCandles);
            var timeframeFileNames = new SortedDictionary<int, string>();
            _symbolTimeFrameFileNames.Add(symbol, timeframeFileNames);
            var timeFrameLastProcessedCandleTimes = new SortedDictionary<int, DateTime>();
            _symbolTimeFrameLastProcessedCandleTimes.Add(symbol, timeFrameLastProcessedCandleTimes);
            _symbolLastProcessedTickTime.Add(symbol, DateTime.MinValue);
            _symbolLastEnqueuedTickTime.Add(symbol, DateTime.MinValue);
            
            foreach (var file in Directory.GetFiles(directory, @"*mins.txt"))
            {
                var fileName = Path.GetFileName(file);
                var timeFrame = 0;
                try
                {
                    timeFrame = int.Parse(fileName.Substring(0, fileName.Length - 8));
                }
                catch
                {
                    _logger.LogError($"Unable to process candle file: {Path.Combine(directory, fileName)}");
                    continue;
                }
                timeframeCandles.Add(timeFrame, File.ReadAllLines(file).Select(line =>
                {
                    var parts = line.Split(';');
                    try
                    {
                        return new Candle
                        {
                            Time = DateTime.ParseExact(parts[0] + parts[1], "dd/MM/yyHHmmss",
                                CultureInfo.InvariantCulture),
                            ClosePriceDiff = decimal.Parse(parts[2].Replace(',','.'), NumberStyles.Float),
                            ClosePrice = decimal.Parse(parts[3].Replace(',','.'), NumberStyles.Float),
                            Volume = decimal.Parse(parts[4].Replace(',','.'), NumberStyles.Float),
                            Symbol = symbol,
                            TimeFrame = timeFrame
                        };
                    }
                    catch (Exception exp)
                    {
                        _logger.LogError(exp, $"Unable to parse candles {line} in file {file}");
                        throw;
                    }
                }).ToList());
                timeframeInProgressCandles.Add(timeFrame, null);
                timeframeFileNames.Add(timeFrame, file);
                var lastProcessedTime = timeframeCandles[timeFrame].Last().Time;
                timeFrameLastProcessedCandleTimes.Add(timeFrame, lastProcessedTime);
                var collectFromAPIStartTime = lastProcessedTime + TimeSpan.FromMinutes(timeFrame);
                _logger.LogDebug($"{symbol}: Last Processed Candle Time for {timeFrame} mins : {lastProcessedTime}. Collecting from API starting from {collectFromAPIStartTime}");
                var candlesFromAPI =
                    (await binanceManager.GetCandles(symbol, timeFrame, collectFromAPIStartTime.Subtract(TimeSpan.FromMinutes(4*timeFrame)), DateTime.UtcNow)).Where(c=>c.Time>=collectFromAPIStartTime).ToList();
                candlesFromAPI.RemoveAt(candlesFromAPI.Count - 1);
                candlesFromAPI.ForEach(ProcessCandle);
            }
        }

        foreach (var symbolTimeFrameLastProcessedCandleTime in _symbolTimeFrameLastProcessedCandleTimes)
        {
            var symbol = symbolTimeFrameLastProcessedCandleTime.Key;
            foreach (var binanceAggregatedTrade in await binanceManager.GetAggregatedTradesAsync(symbol, symbolTimeFrameLastProcessedCandleTime.Value.Min(p => p.Value), DateTime.UtcNow))
            {
                ProcessTrade(binanceAggregatedTrade, symbol);
            }
        }
        var tickersThreadRead = new Thread(ReadTickersQueue){IsBackground = true};
        tickersThreadRead.Start();
    }

    public void EnqueueAggTrade(BinanceStreamAggregatedTrade trade)
    {
        if (_symbolTimeFrameCandles.ContainsKey(trade.Symbol))
        {
            _aggregatedTradesQueue.Enqueue(trade);
            _symbolLastEnqueuedTickTime[trade.Symbol] = trade.TradeTime;
        }
    }
    private void ReadTickersQueue()
    {
        var i = 0;
        while (true)
        {
            BinanceStreamAggregatedTrade trade;
            if (_aggregatedTradesQueue.TryDequeue(out trade))
            {
                i = 0;
                ProcessTrade(trade, trade.Symbol);
            }
            else
            {
                Thread.Sleep(i);
                i++;
            }
        }
    }
    public void ProcessCandle(Candle c)
    {
        if (c.Time <= _symbolTimeFrameLastProcessedCandleTimes[c.Symbol][c.TimeFrame])
            return;
        _symbolTimeFrameCandles[c.Symbol][c.TimeFrame].Add(c);
        NewCandle?.Invoke(c);
        var line = $"{c.Time.ToString("dd/MM/yy;HHmmss", CultureInfo.InvariantCulture)};{c.ClosePriceDiff.ToString()};{c.ClosePrice.ToString()};{c.Volume.ToString()}{Environment.NewLine}";
        File.AppendAllText(_symbolTimeFrameFileNames[c.Symbol][c.TimeFrame], line, Encoding.ASCII);
        _symbolTimeFrameLastProcessedCandleTimes[c.Symbol][c.TimeFrame] = c.Time;
    }
    public void ProcessTrade(IBinanceAggregatedTrade trade, string symbol)
    {
        if (!_symbolLastProcessedTickTime.ContainsKey(symbol))
        {
            _logger.LogError($"Unexpected symbol trade: {symbol}");
            return;
        }
        var lastProcessedTickTime = _symbolLastProcessedTickTime[symbol];
        if (lastProcessedTickTime > trade.TradeTime)
            return;

        var isNewCandle = false;
        lock (_symbolTimeFrameCandles)
        {
            _symbolLastProcessedTickTime[symbol] = trade.TradeTime;
            foreach (var timeframe in _symbolTimeFrameLastProcessedCandleTimes[symbol].Keys.ToList())
            {
                var lastCandleTime = _symbolTimeFrameLastProcessedCandleTimes[symbol][timeframe];
                if (trade.TradeTime < lastCandleTime + TimeSpan.FromMinutes(timeframe))
                    continue;
                var candle = _symbolTimeFrameInProgressCandles[symbol][timeframe];
                if (candle != null)
                {
                    var time = candle.Time + TimeSpan.FromMinutes(timeframe);
                    if (trade.TradeTime >= time)
                    {
                        candle.ClosePriceDiff = candle.ClosePrice -
                                                _symbolTimeFrameCandles[symbol][timeframe].Last().ClosePrice;
                        ProcessCandle(candle);
                        candle = new Candle
                        {
                            Time = trade.TradeTime - TimeSpan.FromSeconds(trade.TradeTime.Second) -
                                   TimeSpan.FromMilliseconds(trade.TradeTime.Millisecond),
                            Symbol = symbol, TimeFrame = timeframe
                        };
                        if (60 % timeframe == 0)
                            candle.Time = candle.Time.AddMinutes(candle.Time.Minute % timeframe * -1);
                        _symbolTimeFrameInProgressCandles[symbol][timeframe] = candle;
                        isNewCandle = true;
                    }
                }
                else
                {
                    candle = new Candle
                    {
                        Time = trade.TradeTime - TimeSpan.FromSeconds(trade.TradeTime.Second) -
                               TimeSpan.FromMilliseconds(trade.TradeTime.Millisecond),
                        Symbol = symbol, TimeFrame = timeframe
                    };
                    if (60 % timeframe == 0)
                        candle.Time = candle.Time.AddMinutes(candle.Time.Minute % timeframe * -1);
                    _symbolTimeFrameInProgressCandles[symbol][timeframe] = candle;
                }

                candle.ClosePrice = trade.Price;
                candle.Volume += trade.Quantity;
            }
            if (isNewCandle && trade.TradeTime > DateTime.UtcNow - TimeSpan.FromMinutes(20))
                ProcessedNewCandleTrade?.Invoke(symbol);
        }
    }

    public void StartStrategies(StrategiesManager strategiesManager)
    {
        lock (_symbolTimeFrameCandles)
        {
            NewCandle += candle =>
            {
                if (strategiesManager.Strategies[candle.Symbol].ContainsKey(candle.TimeFrame))
                    strategiesManager.Strategies[candle.Symbol][candle.TimeFrame].ProcessCandle(candle);
            };
            ProcessedNewCandleTrade += async symbol => await strategiesManager.CheckStrategiesAsync(symbol);
            foreach (var (symbol, strategies) in strategiesManager.Strategies)
            {
                foreach (var (timeframe, strategy) in strategies)
                {
                    var candlesToProcess = _symbolTimeFrameCandles[symbol][timeframe]
                        .Where(c => c.Time > strategy.LastTimeCandle).ToList();
                    if (candlesToProcess.Count > 0)
                    {
                        var processed = false;
                        foreach (var candle in candlesToProcess)
                        {
                            if (strategy.ProcessCandle(candle, false))
                                processed = true;
                        }
                        if (processed)
                            strategy.SaveParams();
                    }
                }
            }
        }
    }
}