using System;
using Gelatinarm.Helpers;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Text
{
    /// <summary>
    ///     Converter to format runtime ticks to duration string
    /// </summary>
    public class RuntimeTicksConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is long ticks && ticks > 0)
            {
                var time = TimeSpan.FromTicks(ticks);

                // Check if we should show full time format (for music tracks)
                var showFullTime = parameter?.ToString() == "full";

                if (showFullTime)
                {
                    // For music tracks, use TimeFormattingHelper
                    return TimeFormattingHelper.FormatTime(time);
                }

                // For movies/shows, use TimeFormattingHelper
                return TimeFormattingHelper.FormatDuration(time);
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
