using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Binance.Net.Objects.Models.Spot.Socket;
using BinanceBot.Models;
using BinanceBot.Services;
using BinanceBot.ViewModels;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using TelegramSink;

namespace BinanceBot
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            var cci = new CultureInfo(Thread.CurrentThread.CurrentCulture.Name)
                {NumberFormat = {NumberDecimalSeparator = "."}};
            Thread.CurrentThread.CurrentCulture = cci;
            var thisProc = Process.GetCurrentProcess();
            thisProc.PriorityClass = ProcessPriorityClass.RealTime;
            
            base.OnStartup(e);
            LiveCharts.Configure(config => config.AddSkiaSharp().AddDefaultMappers().AddDarkTheme());

            var host = new HostBuilder().ConfigureAppConfiguration((ctx, config) =>
                {
                    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<ConcurrentQueue<BinanceStreamAggregatedTrade>>();
                    services.AddSingleton(context.Configuration);
                    services.AddSingleton<ObservableCollection<Order>>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<ControlViewModel>();
                    services.AddSingleton<PythonClient>();
                    services.AddSingleton<BinanceManager>();
                    services.AddSingleton<CandlesManager>();
                    services.AddSingleton<StrategiesManager>();
                    services.AddSingleton<MainWindow>();
                    services.AddSingleton(Current.Dispatcher);
                }).ConfigureLogging((ctx, log) =>
                {
                    Log.Logger = new LoggerConfiguration().Enrich.FromLogContext()
                        .WriteTo.TeleSink(ctx.Configuration["TelegramAPIKey"], ctx.Configuration["TelegramChatId"], minimumLevel: LogEventLevel.Information)
                        .WriteTo.File("logs\\BinanceBot.log", LogEventLevel.Debug, rollingInterval: RollingInterval.Day, outputTemplate:"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level} {SourceContext}.{Method} {Message}{NewLine}{Exception}")
                        .CreateLogger();
                    log.AddSerilog(Log.Logger);
                }).Build();
            var logger = Log.Logger;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) => {logger.Error((Exception)args.ExceptionObject, "CurrentDomainOnUnhandledException");};
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                foreach (var exception in e.Exception.Flatten().InnerExceptions)
                {
                    logger.Error(exception, "TaskSchedulerUnobservedTaskException");
                }
                e.SetObserved();
            };
            host.Services.GetService<MainWindow>().Show();
        }
    }
}
