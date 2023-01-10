using System;

namespace BinanceBot.Models;

public class Candle
{
    public DateTime Time;
    public decimal ClosePriceDiff;
    public decimal ClosePrice;
    public decimal Volume;
    public string Symbol;
    public int TimeFrame;
}