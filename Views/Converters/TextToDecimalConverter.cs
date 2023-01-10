using System;
using System.Globalization;
using System.Windows.Data;

namespace BinanceBot.Views.Converters;

public class TextToDecimalConverter : IValueConverter
{
    private readonly NumberFormatInfo _commaNFI = new() { NumberDecimalSeparator = "." };
    #region IValueConverter Members

    public object Convert(object value, Type targetType, object parameter,
        CultureInfo culture)
    {
        return ((decimal)value).ToString(_commaNFI);
    }

    public object ConvertBack(object value, Type targetType, object parameter,
        CultureInfo culture)
    {
        try
        {
            return decimal.Parse((string) value, _commaNFI);
        }
        catch (Exception)
        {
        }
        return 0;
    }

    #endregion
}