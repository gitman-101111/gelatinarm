using System;
using Jellyfin.Sdk.Generated.Models;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Data
{
    /// <summary>
    ///     Converts played status to opacity value for dimming watched content
    ///     Accepts either BaseItemDto or bool values
    /// </summary>
    public class PlayedToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var isPlayed = false;

            // Handle BaseItemDto input
            if (value is BaseItemDto item)
            {
                isPlayed = item.UserData?.Played == true;
            }
            // Handle direct boolean input
            else if (value is bool played)
            {
                isPlayed = played;
            }
            return isPlayed ? 0.6 : 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
