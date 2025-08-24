using System;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Data
{
    public class PercentageToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // Convert percentage to width (for progress bars)
            // The parameter should be the max width

            if (value is double percentage && parameter != null)
            {
                if (double.TryParse(parameter.ToString(), out var maxWidth))
                {
                    var width = percentage / 100.0 * maxWidth;
                    return width;
                }
            }

            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
