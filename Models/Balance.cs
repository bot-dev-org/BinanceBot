using BinanceBot.Utils;

namespace BinanceBot.Models;

public class Balance : NotifyBase
{
    public string Asset { get; set; } = string.Empty;
    public decimal WalletBalance { get; set; }
    public decimal CrossUnrealizedPnl { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal MaxWithdrawQuantity { get; set; }

    private decimal _usdBalance;
    public decimal UsdBalance
    {
        get => _usdBalance;
        set
        {
            _usdBalance = value;
            RaisePropertyChanged("UsdBalance");
        }
    }

    public override string ToString()
    {
        return $"{Asset} balance: {WalletBalance}, unrealized pnl {CrossUnrealizedPnl}, usd balance: {_usdBalance}";
    }
}