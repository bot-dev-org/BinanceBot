using System.IO;
using System.Text.Json;

namespace BinanceBot.Models
{
    public class StrategyParameters
    {
        private int _deltaSteps;
        public int DeltaSteps
        {
            get { return _deltaSteps;}
            set { _deltaSteps = value; Serialize();}
        }

        private bool _makeDeals;
        public bool MakeDeals 
        {             
            get { return _makeDeals;}
            set { _makeDeals = value; Serialize();} 
        }
        private string _path;
        public string Path 
        {             
            get { return _path;}
            set { _path = value; Serialize();} 
        }

        public StrategyParameters(string path, int deltaSteps, bool makeDeals)
        {
            _path = path;
            _deltaSteps = deltaSteps;
            _makeDeals = makeDeals;
            Serialize();
        }

        private void Serialize()
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(this));
        }
    }
}
