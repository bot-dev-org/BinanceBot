using BinanceBot.Utils;

namespace BinanceBot.Models;

public class BinancePosition : NotifyBase
{
    public string Symbol { get; set; } = string.Empty;
    private decimal _quantity;
    public decimal Quantity
    {
        get => _quantity;
        set
        {
            _quantity = value;
            RaisePropertyChanged("Quantity");
        }
    }
    private decimal _entryPrice;
    public decimal EntryPrice
    {
        get => _entryPrice;
        set
        {
            _entryPrice = value;
            RaisePropertyChanged("EntryPrice");
        }
    }
    private decimal _unrealizedPnl;
    public decimal UnrealizedPnl
    {
        get => _unrealizedPnl;
        set
        {
            _unrealizedPnl = value;
            RaisePropertyChanged("UnrealizedPnl");
        }
    }
}