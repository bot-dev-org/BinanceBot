using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BinanceBot.Services
{
    public class PythonClient
    {
        private readonly byte[] _getLastProcessedTimeCommand = BitConverter.GetBytes(0);
        private readonly byte[] _processCandleCommand = BitConverter.GetBytes(1);
        private readonly byte[] _getDealsCommand = BitConverter.GetBytes(2);
        private readonly byte[] _saveCommand = BitConverter.GetBytes(3);
        private readonly byte[] _getSkipValueCommand = BitConverter.GetBytes(4);
        private readonly byte[] _getVolumeCommand = BitConverter.GetBytes(5);
        private readonly byte[] _setVolumeCommand = BitConverter.GetBytes(6);
        private readonly byte[] _getLastValueCommand = BitConverter.GetBytes(7);
        private readonly NamedPipeClientStream _clientStream;
        private readonly object _syncObj = new ();
        private const string PIPE_NAME = "BinanceBot";
        private ILogger<PythonClient> _logger;
        private readonly Timer timer;

        public int LastValue { get; private set; }

        public PythonClient(ILogger<PythonClient> logger)
        {
            _logger = logger;
            _logger.LogInformation("Connecting to Pipe");
            _clientStream = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut, PipeOptions.WriteThrough);
            _clientStream.Connect();
            _clientStream.ReadMode = PipeTransmissionMode.Byte;
            timer = new Timer(LogLongProcessTime);
        }

        ~PythonClient()
        {
            _clientStream.Close();
        }

        public int Predict(decimal value, decimal closePrice, DateTime time, decimal volume, string ticker, int timeframe, decimal skipCoeff, bool saveParams = true)
        {
            lock (_syncObj)
            {
                var valueBytes = BitConverter.GetBytes(Convert.ToSingle(value));
                var closeBytes = BitConverter.GetBytes(Convert.ToSingle(closePrice));
                var timeBytes = Encoding.ASCII.GetBytes(time.ToString("yyyyMMddHHmmss"));
                var volumeBytes = BitConverter.GetBytes(Convert.ToSingle(volume));
                var timeFrameBytes = BitConverter.GetBytes(timeframe);
                var skipCoeffBytes = BitConverter.GetBytes((float)skipCoeff);
                var tickerBytes = Encoding.ASCII.GetBytes(ticker.ToLower());
                var saveParamsBytes = BitConverter.GetBytes(saveParams?1:0);
                var messageBytes = new byte[_processCandleCommand.Length + valueBytes.Length + closeBytes.Length + timeBytes.Length + volumeBytes.Length +
                    timeFrameBytes.Length + skipCoeffBytes.Length + tickerBytes.Length + saveParamsBytes.Length];
                var startIndex = 0;
                Array.Copy(_processCandleCommand, 0, messageBytes, startIndex, _processCandleCommand.Length);
                startIndex += _processCandleCommand.Length;
                Array.Copy(valueBytes, 0, messageBytes, startIndex, valueBytes.Length);
                startIndex += valueBytes.Length;
                Array.Copy(closeBytes, 0, messageBytes, startIndex, closeBytes.Length);
                startIndex += closeBytes.Length;
                Array.Copy(timeBytes, 0, messageBytes, startIndex, timeBytes.Length);
                startIndex += timeBytes.Length;
                Array.Copy(volumeBytes, 0, messageBytes, startIndex, volumeBytes.Length);
                startIndex += volumeBytes.Length;
                Array.Copy(timeFrameBytes, 0, messageBytes, startIndex, timeFrameBytes.Length);
                startIndex += timeFrameBytes.Length;
                Array.Copy(skipCoeffBytes, 0, messageBytes, startIndex, skipCoeffBytes.Length);
                startIndex += skipCoeffBytes.Length;
                Array.Copy(saveParamsBytes, 0, messageBytes, startIndex, saveParamsBytes.Length);
                startIndex += saveParamsBytes.Length;
                Array.Copy(tickerBytes, 0, messageBytes, startIndex, tickerBytes.Length);
                timer.Change(4000, 4000);
                _clientStream.Write(messageBytes, 0, messageBytes.Length);
                _clientStream.Read(valueBytes, 0, valueBytes.Length);
                timer.Change(Timeout.Infinite, Timeout.Infinite);
                LastValue = BitConverter.ToInt32(valueBytes, 0);
                return LastValue;
            }
        }

        public void Save(string ticker, int timeframe, decimal skipCoeff)
        {
            lock (_syncObj)
            {
                var timeFrameBytes = BitConverter.GetBytes(timeframe);
                var skipCoeffBytes = BitConverter.GetBytes((float)skipCoeff);
                var tickerBytes = Encoding.ASCII.GetBytes(ticker.ToLower());
                var messageBytes = new byte[_getLastProcessedTimeCommand.Length + timeFrameBytes.Length + skipCoeffBytes.Length + tickerBytes.Length];
                var startIndex = 0;
                Array.Copy(_saveCommand, 0, messageBytes, startIndex, _saveCommand.Length);
                startIndex += _saveCommand.Length;
                Array.Copy(timeFrameBytes, 0, messageBytes, startIndex, timeFrameBytes.Length);
                startIndex += timeFrameBytes.Length;
                Array.Copy(skipCoeffBytes, 0, messageBytes, startIndex, skipCoeffBytes.Length);
                startIndex += skipCoeffBytes.Length;
                Array.Copy(tickerBytes, 0, messageBytes, startIndex, tickerBytes.Length);

                _clientStream.Write(messageBytes, 0, messageBytes.Length);
                var resArray = new byte[4];
                _clientStream.Read(resArray, 0, resArray.Length);
                BitConverter.ToInt32(resArray, 0);
            }
        }

        public Dictionary<DateTime, int> GetDeals(string ticker, int timeframe, decimal skipCoeff)
        {
            lock (_syncObj)
            {
                var timeFrameBytes = BitConverter.GetBytes(timeframe);
                var skipCoeffBytes = BitConverter.GetBytes((float)skipCoeff);
                var tickerBytes = Encoding.ASCII.GetBytes(ticker.ToLower());
                var messageBytes = new byte[_getDealsCommand.Length + timeFrameBytes.Length + skipCoeffBytes.Length + tickerBytes.Length];
                var startIndex = 0;
                Array.Copy(_getDealsCommand, 0, messageBytes, startIndex, _getDealsCommand.Length);
                startIndex += _getDealsCommand.Length;
                Array.Copy(timeFrameBytes, 0, messageBytes, startIndex, timeFrameBytes.Length);
                startIndex += timeFrameBytes.Length;
                Array.Copy(skipCoeffBytes, 0, messageBytes, startIndex, skipCoeffBytes.Length);
                startIndex += skipCoeffBytes.Length;
                Array.Copy(tickerBytes, 0, messageBytes, startIndex, tickerBytes.Length);

                _clientStream.Write(messageBytes, 0, messageBytes.Length);
                var resArray = new byte[262144];
                var resLength = _clientStream.Read(resArray, 0, resArray.Length);
                var res = new Dictionary<DateTime, int>();
                for (var i = 0; i < resLength; i += 15)
                {
                    var time = DateTime.ParseExact(Encoding.UTF8.GetString(resArray, i, 14), "dd/MM/yyHHmmss",
                        CultureInfo.InvariantCulture);
                    int direction = (sbyte)resArray[i + 14];
                    if (!res.ContainsKey(time))
                        res.Add(time, direction);
                }
                return res;
            }
        }

        public DateTime GetLastProcessedTime(string ticker, int timeframe, decimal skipCoeff)
        {
            lock (_syncObj)
            {
                var timeFrameBytes = BitConverter.GetBytes(timeframe);
                var skipCoeffBytes = BitConverter.GetBytes((float)skipCoeff);
                var tickerBytes = Encoding.ASCII.GetBytes(ticker.ToLower());
                var messageBytes = new byte[_getLastProcessedTimeCommand.Length + timeFrameBytes.Length + skipCoeffBytes.Length + tickerBytes.Length];
                var startIndex = 0;
                Array.Copy(_getLastProcessedTimeCommand, 0, messageBytes, startIndex, _getLastProcessedTimeCommand.Length);
                startIndex += _getLastProcessedTimeCommand.Length;
                Array.Copy(timeFrameBytes, 0, messageBytes, startIndex, timeFrameBytes.Length);
                startIndex += timeFrameBytes.Length;
                Array.Copy(skipCoeffBytes, 0, messageBytes, startIndex, skipCoeffBytes.Length);
                startIndex += skipCoeffBytes.Length;
                Array.Copy(tickerBytes, 0, messageBytes, startIndex, tickerBytes.Length);

                _clientStream.Write(messageBytes, 0, messageBytes.Length);
                var resArray = new byte[14];
                var resLength = _clientStream.Read(resArray, 0, resArray.Length);
                return DateTime.ParseExact(Encoding.UTF8.GetString(resArray, 0, resLength), "dd/MM/yyHHmmss",
                    CultureInfo.InvariantCulture);
            }
        }
        public decimal GetVolume(string ticker, int timeframe, decimal skipCoeff)
        {
            lock (_syncObj)
            {
                var timeFrameBytes = BitConverter.GetBytes(timeframe);
                var skipCoeffBytes = BitConverter.GetBytes((float)skipCoeff);
                var tickerBytes = Encoding.ASCII.GetBytes(ticker.ToLower());
                var messageBytes = new byte[_getVolumeCommand.Length + timeFrameBytes.Length + skipCoeffBytes.Length + tickerBytes.Length];
                var startIndex = 0;
                Array.Copy(_getVolumeCommand, 0, messageBytes, startIndex, _getVolumeCommand.Length);
                startIndex += _getVolumeCommand.Length;
                Array.Copy(timeFrameBytes, 0, messageBytes, startIndex, timeFrameBytes.Length);
                startIndex += timeFrameBytes.Length;
                Array.Copy(skipCoeffBytes, 0, messageBytes, startIndex, skipCoeffBytes.Length);
                startIndex += skipCoeffBytes.Length;
                Array.Copy(tickerBytes, 0, messageBytes, startIndex, tickerBytes.Length);

                _clientStream.Write(messageBytes, 0, messageBytes.Length);
                var resArray = new byte[8];
                _clientStream.Read(resArray, 0, resArray.Length);
                return Decimal.Round(Convert.ToDecimal(BitConverter.ToDouble(resArray, 0)), 10);
            }
        }

        public int GetLastValue(string ticker, int timeframe, decimal skipCoeff)
        {
            lock (_syncObj)
            {
                var timeFrameBytes = BitConverter.GetBytes(timeframe);
                var skipCoeffBytes = BitConverter.GetBytes((float)skipCoeff);
                var tickerBytes = Encoding.ASCII.GetBytes(ticker.ToLower());
                var messageBytes = new byte[_getVolumeCommand.Length + timeFrameBytes.Length + skipCoeffBytes.Length + tickerBytes.Length];
                var startIndex = 0;
                Array.Copy(_getLastValueCommand, 0, messageBytes, startIndex, _getLastValueCommand.Length);
                startIndex += _getVolumeCommand.Length;
                Array.Copy(timeFrameBytes, 0, messageBytes, startIndex, timeFrameBytes.Length);
                startIndex += timeFrameBytes.Length;
                Array.Copy(skipCoeffBytes, 0, messageBytes, startIndex, skipCoeffBytes.Length);
                startIndex += skipCoeffBytes.Length;
                Array.Copy(tickerBytes, 0, messageBytes, startIndex, tickerBytes.Length);

                _clientStream.Write(messageBytes, 0, messageBytes.Length);
                _clientStream.Read(timeFrameBytes, 0, timeFrameBytes.Length);
                LastValue = BitConverter.ToInt32(timeFrameBytes, 0);
                return LastValue;
            }
        }

        public void SetVolume(decimal volume, string ticker, int timeframe, decimal skipCoeff)
        {
            lock (_syncObj)
            {
                var volumeBytes = BitConverter.GetBytes(Convert.ToDouble(volume));
                var timeFrameBytes = BitConverter.GetBytes(timeframe);
                var skipCoeffBytes = BitConverter.GetBytes((float)skipCoeff);
                var tickerBytes = Encoding.ASCII.GetBytes(ticker.ToLower());
                var messageBytes = new byte[_setVolumeCommand.Length + volumeBytes.Length + timeFrameBytes.Length + skipCoeffBytes.Length + tickerBytes.Length];
                var startIndex = 0;
                Array.Copy(_setVolumeCommand, 0, messageBytes, startIndex, _setVolumeCommand.Length);
                startIndex += _setVolumeCommand.Length;
                Array.Copy(volumeBytes, 0, messageBytes, startIndex, volumeBytes.Length);
                startIndex += volumeBytes.Length;
                Array.Copy(timeFrameBytes, 0, messageBytes, startIndex, timeFrameBytes.Length);
                startIndex += timeFrameBytes.Length;
                Array.Copy(skipCoeffBytes, 0, messageBytes, startIndex, skipCoeffBytes.Length);
                startIndex += skipCoeffBytes.Length;
                Array.Copy(tickerBytes, 0, messageBytes, startIndex, tickerBytes.Length);

                _clientStream.Write(messageBytes, 0, messageBytes.Length);
                var resArray = new byte[4];
                _clientStream.Read(resArray, 0, resArray.Length);
                BitConverter.ToInt32(resArray, 0);
            }
        }

        public IDictionary<string, IDictionary<int, decimal>> GetMetadata()
        {
            lock (_syncObj)
            {
                _clientStream.Write(_getSkipValueCommand, 0, _getSkipValueCommand.Length);
                var resArray = new byte[4096];
                var resLength = _clientStream.Read(resArray, 0, resArray.Length);
                var resString = Encoding.UTF8.GetString(resArray, 0, resLength);
                var metadata = new Dictionary<string, IDictionary<int, decimal>>();
                var o = JObject.Parse(resString);
                var r = o.SelectToken("$");
                foreach (var t in r)
                {
                    var property = (JProperty)t;
                    if (!metadata.ContainsKey(property.Name))
                    {
                        metadata.Add(property.Name, new Dictionary<int, decimal>());
                    }
                    var tfDict = metadata[property.Name];
                    var tf = property.Value.SelectToken("$");
                    foreach (var tt in tf)
                    {
                        property = (JProperty)tt;
                        var timeframe = int.Parse(property.Name);
                        if (!tfDict.ContainsKey(timeframe))
                        {
                            var sc = property.Value.SelectToken("$");
                            foreach (var ttt in sc)
                            {
                                property = (JProperty)ttt;
                                var skipCoeff = decimal.Parse(property.Name, NumberStyles.Float);
                                tfDict.Add(timeframe, skipCoeff);
                            }
                        }
                    }
                }
                return metadata;
            }
        }
        private void LogLongProcessTime(object e) 
        {
            _logger.LogError("Long Process Time: " + e.ToString());
        }
    }
}
