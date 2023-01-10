using System;
using Binance.Net.Enums;
using BinanceBot.Utils;

namespace BinanceBot.Models;

public class Order : NotifyBase
{
    public long Id;
    public string Symbol { get; set; }
    public OrderSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    private OrderStatus _status;
    public OrderStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            RaisePropertyChanged("Status");
        }
    }

    private decimal _filledVolume;
    public decimal FilledVolume
    {
        get => _filledVolume;
        set
        {
            _filledVolume = value;
            RaisePropertyChanged("FilledVolume");
        }
    }

    public DateTime Created { get; set; }
    private DateTime _updateTime;

    public DateTime UpdateTime
    {
        get => _updateTime;
        set
        {
            _updateTime = value;
            RaisePropertyChanged("UpdateTime");
        }
    }
}