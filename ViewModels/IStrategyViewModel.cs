using BinanceBot.Models;

namespace BinanceBot.ViewModels;

public interface IStrategyViewModel : ITabViewModel
{
    string Symbol { get; }
    int TimeFrame { get; }

    void AddCandle(Candle candle);
}