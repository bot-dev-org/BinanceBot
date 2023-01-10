using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures.Socket;
using BinanceBot.Models;
using BinanceBot.Services;
using BinanceBot.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Balance = BinanceBot.Models.Balance;
using Order = BinanceBot.Models.Order;
using Trade = BinanceBot.Models.Trade;

namespace BinanceBot.ViewModels;

public class ControlViewModel : NotifyBase, ITabViewModel
{
    public string Header { get; set; } = "Control";
    private readonly BinanceManager _binanceManager;
    public ObservableCollection<Order> Orders { get; set; }
    public ObservableCollection<Trade> Trades { get; set; } = new();
    public ObservableCollection<Balance> Balances { get; set; } = new();
    public ObservableCollection<BinancePosition> Positions { get; set; } = new();
    private IEnumerable<string> _tradedSymbols;
    private readonly ILogger<ControlViewModel> _logger;
    private decimal _bnbTargetBalance;
    private decimal _usdBalanceThreshold;
    private DateTime _lastReportBalanceTime = DateTime.Now;
    private TimeSpan _balanceReportFrequency;
    private List<Trade> _tradesForTakerChecking = new();

    public ControlViewModel(BinanceManager binanceManager, ILogger<ControlViewModel> logger, IConfiguration config,
        ObservableCollection<Order> orders)
    {
        _logger = logger;
        _binanceManager = binanceManager;
        _bnbTargetBalance = decimal.Parse(config["BNBAssetTarget"]);
        _usdBalanceThreshold = decimal.Parse(config["USDBalanceThreshold"]);
        _balanceReportFrequency = TimeSpan.FromHours(int.Parse(config["BalanceReportFrequencyHours"]));
        Orders = orders;
    }
    public async Task Initialize(IEnumerable<string> tradedSymbols)
    {
        _tradedSymbols = tradedSymbols;
        await LoadPositions();
        await LoadTrades();
        foreach (var trade in Trades)
        {
            _tradesForTakerChecking.Add(trade);
        }
        new Thread(async() =>
        {
            var prevTradesCount = Trades.Count;
            while (true)
            {
                try
                {
                    await LoadBalances();

                    foreach (var balance in Balances.Where(b => !b.Asset.Contains("USD")))
                    {
                        var asset_price = await _binanceManager.GetTickerPrice(balance.Asset + "BUSD");
                        _dispatcher.Invoke(() =>
                        {
                            balance.UsdBalance = (balance.WalletBalance + balance.CrossUnrealizedPnl) * asset_price;
                            RaisePropertyChanged("Balances");
                        });
                    }

                    _dispatcher.Invoke(() =>
                    {
                        var usdBalance = Balances.Sum(b => b.UsdBalance);
                        Balances.Add(new Balance
                        {
                            Asset = "Total", UsdBalance = usdBalance
                        });
                        if (usdBalance > _usdBalanceThreshold)
                            _logger.LogWarning($"USD balance is over threshold: {usdBalance}");
                    });
                    var now = DateTime.Now; 
                    if ( now > _lastReportBalanceTime + _balanceReportFrequency)
                    {
                        var message = string.Join("; ", Balances.Select(b => b.ToString()));
                        _logger.LogWarning($"Current balances: {message}");
                        _lastReportBalanceTime = now - TimeSpan.FromMinutes(now.Minute);
                    }

                    if (prevTradesCount != Trades.Count)
                    {
                        await LoadTrades();
                        prevTradesCount = Trades.Count;
                        CheckTakers(Trades.ToList());
                    }
                    await Task.Delay(TimeSpan.FromMinutes(10));
                }
                catch (Exception exp)
                {
                    _logger.LogError(exp, $"Exception on updating trading data: {exp.Message}");
                    await Task.Delay(TimeSpan.FromMinutes(10));
                }
            }
        }){IsBackground = true}.Start();
    }

    private void CheckTakers(List<Trade> newTrades)
    {
        var tradesToRemove = new List<Trade>();
        _tradesForTakerChecking.ForEach(t =>
        {
            if (newTrades.All(nt => nt.Id != t.Id))
                tradesToRemove.Add(t);
        });
        tradesToRemove.ForEach(t=>_tradesForTakerChecking.Remove(t));
        newTrades.ForEach(nt =>
        {
            if (_tradesForTakerChecking.Any(t => t.Id == nt.Id)) return;
            _tradesForTakerChecking.Add(nt);
            if (nt.Maker == false)
                _logger.LogWarning($"Taker trade: {nt.Symbol} {nt.Side} {nt.Quantity} {nt.Timestamp}");
        });
    }

    private async Task LoadBalances()
    {
        var balances = await _binanceManager.GetBalances();
        _dispatcher.Invoke(()=>{
            Balances.Clear();
            foreach (var binanceFuturesAccountBalance in balances)
            {
                if (binanceFuturesAccountBalance.WalletBalance != 0m)
                    Balances.Add(new Balance
                    {
                        Asset = binanceFuturesAccountBalance.Asset,
                        WalletBalance = binanceFuturesAccountBalance.WalletBalance,
                        AvailableBalance = binanceFuturesAccountBalance.AvailableBalance,
                        CrossUnrealizedPnl = binanceFuturesAccountBalance.CrossUnrealizedPnl,
                        MaxWithdrawQuantity = binanceFuturesAccountBalance.MaxWithdrawQuantity,
                        UsdBalance = binanceFuturesAccountBalance.WalletBalance + binanceFuturesAccountBalance.CrossUnrealizedPnl
                    });
            }

            var bnbBalance = Balances.FirstOrDefault(b => b.Asset == "BNB");
            if (bnbBalance == default)
                _logger.LogWarning($"No BNB in balance");
            else if (bnbBalance.AvailableBalance < _bnbTargetBalance)
                _logger.LogWarning($"Insufficient amount of BNB: {bnbBalance.AvailableBalance}");
            RaisePropertyChanged("Balances");
        });
    }
    private async Task LoadPositions()
    {
        var positions = await _binanceManager.GetPositions();
        _dispatcher.Invoke(()=>{
            lock (Positions)
            {
                Positions.Clear();
                foreach (var position in positions)
                {
                    if (position.Quantity != 0m)
                        Positions.Add(new BinancePosition
                        {
                            Quantity = position.Quantity,
                            Symbol = position.Symbol,
                            EntryPrice = position.EntryPrice ?? 0m,
                            UnrealizedPnl = position.UnrealizedPnl ?? 0m
                        });
                }
            }

            RaisePropertyChanged("Positions");
        });
    }
    
    private async Task LoadTrades()
    {
        var trades = await _binanceManager.GetTrades(_tradedSymbols, DateTime.UtcNow - TimeSpan.FromDays(2));
        _dispatcher.Invoke(()=>{
            Trades.Clear();
            foreach (var trade in trades)
            {
                if (trade.Quantity != 0m)
                    Trades.Add(new Trade
                    {
                        Quantity = trade.Quantity,
                        Symbol = trade.Symbol,
                        Fee = trade.Fee,
                        Maker = trade.Maker,
                        Price = trade.Price,
                        Side = trade.Side,
                        Timestamp = trade.Timestamp,
                        FeeAsset = trade.FeeAsset,
                        OrderId = trade.OrderId, 
                        RealizedPnl = trade.RealizedPnl,
                        Id = trade.Id
                    });
            }
            RaisePropertyChanged("Trades");
        });
    }

    public void UpdateOrder(BinanceFuturesStreamOrderUpdateData orderData)
    {
        _dispatcher.Invoke(() =>
        {
            Order? order;
            switch (orderData.ExecutionType)
            {
                case ExecutionType.New:
                    Orders.Add(new Order
                    {
                        Id = orderData.OrderId,
                        Price = orderData.Price,
                        Quantity = orderData.Quantity,
                        Side = orderData.Side,
                        Status = orderData.Status,
                        Symbol = orderData.Symbol,
                        Created = orderData.UpdateTime,
                        UpdateTime = orderData.UpdateTime,
                        FilledVolume = orderData.AccumulatedQuantityOfFilledTrades
                    });
                    break;
                case ExecutionType.Amendment:
                case ExecutionType.Canceled:
                    order = Orders.FirstOrDefault(o => o.Id == orderData.OrderId);
                    if (order != default)
                    {
                        order.Status = orderData.Status;
                        order.FilledVolume = orderData.AccumulatedQuantityOfFilledTrades;
                        order.UpdateTime = orderData.UpdateTime;
                    }
                    break;
                case ExecutionType.Trade:
                    Trades.Add(new Trade
                    {
                        Fee = orderData.Fee,
                        Price = orderData.PriceLastFilledTrade,
                        Quantity = orderData.QuantityOfLastFilledTrade,
                        Side = orderData.Side,
                        Symbol = orderData.Symbol,
                        Timestamp = orderData.UpdateTime,
                        FeeAsset = orderData.FeeAsset,
                        Maker = orderData.BuyerIsMaker ? orderData.Side == OrderSide.Buy : orderData.Side == OrderSide.Sell,
                        OrderId = orderData.OrderId,
                        RealizedPnl = orderData.RealizedProfit,
                        Id = orderData.TradeId
                    });
                    order = Orders.FirstOrDefault(o => o.Id == orderData.OrderId);
                    if (order != default)
                    {
                        order.Status = orderData.Status;
                        order.FilledVolume = orderData.AccumulatedQuantityOfFilledTrades;
                        order.UpdateTime = orderData.UpdateTime;
                    }
                    break;
                case ExecutionType.Expired:
                    _logger.LogError($"Order has been expired: {orderData.Symbol} {orderData.Side} {orderData.UpdateTime} {orderData.OrderId}");
                    break;
                case ExecutionType.Rejected:
                    _logger.LogError($"Order has been rejected: {orderData.Symbol} {orderData.Side} {orderData.UpdateTime} {orderData.OrderId}");
                    break;
                case ExecutionType.Replaced:
                    _logger.LogError($"Order has been replaced: {orderData.Symbol} {orderData.Side} {orderData.UpdateTime} {orderData.OrderId}");
                    break;
            }
        });
    }

    public void UpdateAccountInfo(BinanceFuturesStreamAccountUpdateData accountData)
    {
        foreach (var binanceFuturesStreamPosition in accountData.Positions)
        {
            _dispatcher.Invoke(() =>
            {
                lock (Positions)
                {
                    var position = Positions.FirstOrDefault(p => p.Symbol == binanceFuturesStreamPosition.Symbol);
                    if (position == default)
                    {
                        Positions.Add(new BinancePosition
                        {
                            Symbol = binanceFuturesStreamPosition.Symbol,
                            Quantity = binanceFuturesStreamPosition.Quantity,
                            EntryPrice = binanceFuturesStreamPosition.EntryPrice,
                            UnrealizedPnl = binanceFuturesStreamPosition.UnrealizedPnl
                        });
                    }
                    else
                    {
                        position.Quantity = binanceFuturesStreamPosition.Quantity;
                        position.EntryPrice = binanceFuturesStreamPosition.EntryPrice;
                        position.UnrealizedPnl = binanceFuturesStreamPosition.UnrealizedPnl;
                    }

                    var positionsToRemove = Positions.Where(p => p.Quantity == 0m).ToList();
                    positionsToRemove.ForEach(p=>Positions.Remove(p));
                }
            });
        }
    }
    [DllImport("w32time.dll")]
    public static extern uint W32TimeSyncNow([MarshalAs(UnmanagedType.LPWStr)]String computername, bool wait, uint flag);
}