using System;
using Binance.Net.Enums;

namespace BinanceBot.Models;

public class Trade
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public bool Maker { get; set; }
    public OrderSide Side { get; set; }
    public DateTime Timestamp { get; set; }
    public decimal Fee { get; set; }
    public string FeeAsset { get; set; } = string.Empty;
    public long OrderId;
    public decimal RealizedPnl { get; set; }
    public long Id { get; set; }
}