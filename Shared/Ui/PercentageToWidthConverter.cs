using System;

namespace Gelatinarm.Shared.Ui
{
    /// <summary>
    ///     A percentage as a width, for progress bars; the parameter is the full width
    /// </summary>
    public class PercentageToWidthConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double percentage && parameter != null && double.TryParse(parameter.ToString(), out var maxWidth))
            {
                return percentage / 100.0 * maxWidth;
            }

            return 0.0;
        }
    }
}
