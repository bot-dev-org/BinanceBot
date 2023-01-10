using System;
using System.IO;
using System.Text.Json;
using Binance.Net.Objects.Models.Futures;
using BinanceBot.Services;
using BinanceBot.Utils;
using Microsoft.Extensions.Logging;

namespace BinanceBot.Models
{
    public class Strategy : NotifyBase
    {
        private readonly PythonClient _pythonClient;
        public readonly string Symbol;
        private BinanceFuturesUsdtSymbol _binanceSymbol;
        private readonly decimal _skipCoeff;
        public readonly int Timeframe;
        public DateTime LastTimeCandle;
        private decimal _volume;
        private decimal _accumClosePriceDiff;
        private decimal _accumVolume;
        private int _direction;
        public string Name;
        private readonly ILogger<Strategy> _logger;
        private StrategyParameters _parameters;
        private bool _directionChanged = false;
        
        public Strategy(string symbol, int timeframe, decimal skipCoeff, PythonClient pythonClient, ILogger<Strategy> logger)
        {
            Symbol = symbol;
            _skipCoeff = skipCoeff;
            Timeframe = timeframe;
            _pythonClient = pythonClient;
            _logger = logger;
        }

        public void Initialize(BinanceFuturesUsdtSymbol symbol)
        {
            _binanceSymbol = symbol;
            LastTimeCandle = _pythonClient.GetLastProcessedTime(Symbol, Timeframe, _skipCoeff);
            Volume = _pythonClient.GetVolume(Symbol, Timeframe, _skipCoeff);
            if (Volume <= 0.000000001m)
            {
                Volume = _binanceSymbol.LotSizeFilter.MinQuantity;
                _pythonClient.SetVolume(Volume, Symbol, Timeframe, _skipCoeff);
            }
            Direction = _pythonClient.GetLastValue(Symbol, Timeframe, _skipCoeff);
            var settingsFile = $"{_binanceSymbol.Pair} {Timeframe}.json";
            if (!File.Exists(settingsFile))
            {
                _logger.LogInformation($"Creating parameters for strategy: {_binanceSymbol.Pair} {Timeframe} {_skipCoeff}");
                _parameters = new StrategyParameters(settingsFile, 10, false);
            }
            else
                _parameters = JsonSerializer.Deserialize<StrategyParameters>(File.ReadAllText(settingsFile));
        }

        public bool ProcessCandle(Candle candle, bool saveParams = true)
        {
            _accumClosePriceDiff += candle.ClosePriceDiff;
            _accumVolume += candle.Volume;
            Candle newAggregatedCandle = null;
 
            if (Math.Abs(_accumClosePriceDiff) >= candle.ClosePrice * _skipCoeff)
            {
                newAggregatedCandle = new Candle
                {
                    ClosePrice = candle.ClosePrice,
                    Volume = _accumVolume,
                    ClosePriceDiff = _accumClosePriceDiff,
                    Time = candle.Time
                };
                _accumClosePriceDiff = 0m;
                _accumVolume = 0m;
            }
            else
            {
                _logger.LogDebug($"{Name} skips candle: {candle.Time} {_accumClosePriceDiff} {candle.ClosePrice} {_accumVolume} {_skipCoeff}");
                return false;
            }

            if (newAggregatedCandle.Time < LastTimeCandle)
                return false;
            LastTimeCandle = candle.Time;
            var prevState = Direction;
            Direction = _pythonClient.Predict(candle.ClosePriceDiff, candle.ClosePrice, candle.Time, candle.Volume, Symbol, Timeframe, _skipCoeff, saveParams);
            if (prevState != Direction)
                DirectionChanged = true;
            return true;
        }

        public void SaveParams()
        {
            _pythonClient.Save(Symbol, Timeframe, _skipCoeff);
        }
        public int DeltaSteps
        {
            get => _parameters.DeltaSteps;
            set
            {
                _parameters.DeltaSteps = value;
                RaisePropertyChanged("DeltaSteps");
            }
        }

        public bool MakeDeals
        {
            get => _parameters.MakeDeals;
            set
            {
                _parameters.MakeDeals = value;
                RaisePropertyChanged("MakeDeals");
            }
        }
        public int Direction
        {
            get => _direction;
            set
            {
                _direction = value;
                RaisePropertyChanged("Direction");
            }
        }
        public decimal Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                _pythonClient.SetVolume(_volume, Symbol, Timeframe, _skipCoeff);
                RaisePropertyChanged("Volume");
            }
        }
        
        public bool DirectionChanged
        {
            get => _directionChanged;
            set
            {
                _directionChanged = value;
                RaisePropertyChanged("DirectionChanged");
            }
        }
        

        public decimal PriceTick => _binanceSymbol.PriceFilter.TickSize;
        public decimal VolumeTick => _binanceSymbol.LotSizeFilter.StepSize;
    }
}
