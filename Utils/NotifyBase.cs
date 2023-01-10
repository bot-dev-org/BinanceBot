using System.ComponentModel;
using System.Windows.Threading;

namespace BinanceBot.Utils
{
    public abstract class NotifyBase : INotifyPropertyChanged
    {
        #region INotifyPropertyChanged Members

        public event PropertyChangedEventHandler PropertyChanged;
        protected Dispatcher _dispatcher;

        public NotifyBase()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        #endregion

        protected void RaisePropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
                _dispatcher.BeginInvoke(new PropertyChangedEventHandler(PropertyChanged), this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
