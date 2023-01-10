using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using BinanceBot.Models;
using BinanceBot.Utils;
using LiveChartsCore;
using LiveChartsCore.Drawing;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Candle = BinanceBot.Models.Candle;

namespace BinanceBot.ViewModels;

public class StrategyViewModel : NotifyBase, IStrategyViewModel
{
    public string Header { get; set; }
    private readonly Strategy _strategy;
    public Strategy Strategy => _strategy;
    public int TimeFrame => _strategy.Timeframe;
    private readonly Dispatcher _dispatcher;
    private const int MAX_VALUES_TO_DISPLAY = 200;
    public ObservableCollection<ISeries> Series { get; set; } = new()
    {
        new ColumnSeries<Candle>
        {
            Values = new ObservableCollection<Candle>(), 
            Name = "Positive diff", 
            Fill = new SolidColorPaint(SKColors.Green), 
            GroupPadding = 0, 
            MaxBarWidth = double.MaxValue, 
            TooltipLabelFormatter = point=> $"{point.Model.ClosePriceDiff}@{point.Model.Time.ToString("dd.MM HH:mm")}", 
            DataPadding = new LvcPoint(0, 0),
            Mapping = (candle, point) =>
            {
                point.PrimaryValue = Convert.ToDouble(candle.ClosePriceDiff);
                point.SecondaryValue = candle.Time.Ticks;
            }
        },
        new ColumnSeries<Candle>
        {
            Values = new ObservableCollection<Candle>(), 
            Name = "Negative diff", 
            Fill = new SolidColorPaint(SKColors.Red), 
            GroupPadding = 0, 
            MaxBarWidth = double.MaxValue, 
            TooltipLabelFormatter = point=> $"{point.Model.ClosePriceDiff}@{point.Model.Time.ToString("dd.MM HH:mm")}", 
            DataPadding = new LvcPoint(0, 0),
            Mapping = (candle, point) =>
            {
                point.PrimaryValue = Convert.ToDouble(candle.ClosePriceDiff);
                point.SecondaryValue = candle.Time.Ticks;
            }
        }
    };
    public ObservableCollection<ISeries> Volumes { get; set; } = new()
    {
        new ColumnSeries<Candle>
        {
            Values = new ObservableCollection<Candle>(), 
            Name = "Positive volume", 
            Fill = new SolidColorPaint(SKColors.Green), 
            GroupPadding = 0, 
            MaxBarWidth = double.MaxValue, 
            TooltipLabelFormatter = point=> $"{point.Model.Volume}@{point.Model.Time.ToString("dd.MM HH:mm")}", 
            DataPadding = new LvcPoint(0, 0),
            Mapping = (candle, point) =>
            {
                point.PrimaryValue = Convert.ToDouble(candle.Volume);
                point.SecondaryValue = candle.Time.Ticks;
            }
        },
        new ColumnSeries<Candle>
        {
            Values = new ObservableCollection<Candle>(), 
            Name = "Negative volume", 
            Fill = new SolidColorPaint(SKColors.Red), 
            GroupPadding = 0, 
            MaxBarWidth = double.MaxValue, 
            TooltipLabelFormatter = point=> $"{point.Model.Volume}@{point.Model.Time.ToString("dd.MM HH:mm")}", 
            DataPadding = new LvcPoint(0, 0),
            Mapping = (candle, point) =>
            {
                point.PrimaryValue = Convert.ToDouble(candle.Volume);
                point.SecondaryValue = candle.Time.Ticks;
            }
        }
    };
    public Axis[] XAxes { get; set; }
    

    public void AddCandle(Candle candle)
    {
        _dispatcher.Invoke(() =>
        {
            var buySeries = (ObservableCollection<Candle>) Series[0].Values;
            var sellSeries = (ObservableCollection<Candle>) Series[1].Values;
            if (candle.ClosePriceDiff > 0)
            {
                buySeries.Add(candle);
                ((ObservableCollection<Candle>) Volumes[0].Values).Add(candle);
            }
            else
            {
                sellSeries.Add(candle);
                ((ObservableCollection<Candle>) Volumes[1].Values).Add(candle);
            }

            while (buySeries.Count + sellSeries.Count > MAX_VALUES_TO_DISPLAY)
            {
                var time = buySeries[0].Time;
                if (time > sellSeries[0].Time)
                    sellSeries.RemoveAt(0);
                else
                    buySeries.RemoveAt(0);
            }
        });
    }

    public string Symbol => _strategy.Symbol;

    public StrategyViewModel(Strategy strategy, IEnumerable<Candle> candles)
    {
        _strategy = strategy;
        _dispatcher = Dispatcher.CurrentDispatcher;
        XAxes = new Axis[] {
            new()
            {
                UnitWidth = TimeSpan.FromMinutes(_strategy.Timeframe).Ticks,
                IsVisible = false
            }
        };
        RaisePropertyChanged("XAxes");
        foreach (var candle in candles)
        {
            AddCandle(candle);
        }
    }
}