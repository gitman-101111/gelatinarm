using System;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Shared.Ui
{
    /// <summary>
    ///     Base for converters used only source-to-target: no binding writes their value back
    /// </summary>
    public abstract class OneWayConverter : IValueConverter
    {
        public abstract object Convert(object value, Type targetType, object parameter, string language);

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
