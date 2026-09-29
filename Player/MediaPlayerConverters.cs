using System;
using Windows.UI.Xaml;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Player
{
    /// <summary>
    ///     One Segoe MDL2 glyph for true, another for false or no value
    /// </summary>
    public abstract class BoolGlyphConverter : OneWayConverter
    {
        private readonly string _whenFalse;
        private readonly string _whenTrue;

        protected BoolGlyphConverter(string whenTrue, string whenFalse)
        {
            _whenTrue = whenTrue;
            _whenFalse = whenFalse;
        }

        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is true ? _whenTrue : _whenFalse;
        }
    }

    /// <summary>
    ///     Pause while playing, play otherwise
    /// </summary>
    public class PlayPauseIconConverter : BoolGlyphConverter
    {
        public PlayPauseIconConverter() : base("\uE769", "\uE768")
        {
        }
    }

    public class EpisodeTypeToVisibilityConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is BaseItemDto_Type.Episode ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    ///     Filled heart for a favorite, empty heart otherwise
    /// </summary>
    public class FavoriteIconConverter : BoolGlyphConverter
    {
        public FavoriteIconConverter() : base("\uE735", "\uE734")
        {
        }
    }

    /// <summary>
    ///     The series name for an episode, the item's own name otherwise
    /// </summary>
    public class MediaTitleConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not BaseItemDto item)
            {
                return string.Empty;
            }

            return (item.Type == BaseItemDto_Type.Episode ? item.SeriesName : item.Name) ?? string.Empty;
        }
    }

    public class EpisodeInfoConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not BaseItemDto item)
            {
                return string.Empty;
            }

            if (item.Type == BaseItemDto_Type.Episode)
            {
                return $" S{item.ParentIndexNumber ?? 0}:E{item.IndexNumber ?? 0} \"{item.Name}\"";
            }

            return item.ProductionYear.HasValue ? $" ({item.ProductionYear.Value})" : string.Empty;
        }
    }

    public class PercentageToStarGridLengthConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double percentage)
            {
                percentage = Math.Max(0, Math.Min(100, percentage));
                return new GridLength(percentage, GridUnitType.Star);
            }

            return new GridLength(0, GridUnitType.Star);
        }
    }

    public class PercentageToComplementaryStarGridLengthConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double percentage)
            {
                percentage = Math.Max(0, Math.Min(100, percentage));
                return new GridLength(100 - percentage, GridUnitType.Star);
            }

            return new GridLength(100, GridUnitType.Star);
        }
    }
}
