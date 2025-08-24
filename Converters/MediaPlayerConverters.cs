using System;
using Jellyfin.Sdk.Generated.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters
{
    /// <summary>
    ///     Converts boolean IsPlaying to appropriate play/pause icon
    /// </summary>
    public class PlayPauseIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isPlaying)
            {
                return isPlaying ? "\uE769" : "\uE768"; // Pause : Play
            }

            return "\uE768"; // Default to Play
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    ///     Converts Episode type to visibility
    /// </summary>
    public class EpisodeTypeToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is BaseItemDto_Type type)
            {
                return type == BaseItemDto_Type.Episode
                    ? Windows.UI.Xaml.Visibility.Visible
                    : Windows.UI.Xaml.Visibility.Collapsed;
            }

            return Windows.UI.Xaml.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    ///     Converts favorite status to appropriate icon
    /// </summary>
    public class FavoriteIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isFavorite)
            {
                return isFavorite ? "\uE735" : "\uE734"; // Filled heart : Empty heart
            }

            return "\uE734"; // Default to empty heart
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }


    /// <summary>
    ///     Converts TimeSpan position to margin for seek thumb
    /// </summary>
    public class PositionToMarginConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // This would calculate the left margin based on position
            return new Thickness(0, 0, 0, 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    ///     Converts BaseItemDto to media title (series name for episodes, movie name for movies)
    /// </summary>
    public class MediaTitleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is BaseItemDto item)
            {
                if (item.Type == BaseItemDto_Type.Episode)
                {
                    return item.SeriesName ?? string.Empty;
                }

                return item.Name ?? string.Empty;
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    ///     Converts BaseItemDto to episode info string
    /// </summary>
    public class EpisodeInfoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is BaseItemDto item && item.Type == BaseItemDto_Type.Episode)
            {
                var seasonNumber = item.ParentIndexNumber ?? 0;
                var episodeNumber = item.IndexNumber ?? 0;
                return $" S{seasonNumber}:E{episodeNumber} \"{item.Name}\"";
            }

            if (value is BaseItemDto movie)
            {
                // For movies, include the year if available
                if (movie.ProductionYear.HasValue)
                {
                    return $" ({movie.ProductionYear.Value})";
                }

                return string.Empty;
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }


    /// <summary>
    ///     Converts percentage to GridLength for dynamic column sizing
    /// </summary>
    public class PercentageToStarGridLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double percentage)
            {
                // Ensure percentage is between 0 and 100
                percentage = Math.Max(0, Math.Min(100, percentage));

                // For the complementary column pattern, we need to return the percentage value
                // The second column with Width="*" will automatically take (100-percentage)
                return new GridLength(percentage, GridUnitType.Star);
            }

            return new GridLength(0, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    ///     Converts percentage to complementary GridLength for the remaining space
    /// </summary>
    public class PercentageToComplementaryStarGridLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double percentage)
            {
                // Ensure percentage is between 0 and 100
                percentage = Math.Max(0, Math.Min(100, percentage)); return new GridLength(100 - percentage, GridUnitType.Star);
            }

            return new GridLength(100, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
