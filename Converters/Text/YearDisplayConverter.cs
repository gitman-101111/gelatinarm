using System;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Text
{
    /// <summary>
    ///     Converter to format year for display
    /// </summary>
    public class YearDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int year && year > 0)
            {
                return year.ToString();
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
