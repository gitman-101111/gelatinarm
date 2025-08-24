using System;
using Jellyfin.Sdk.Generated.Models;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Visibility
{
    /// <summary>
    ///     Consolidated visibility converter supporting multiple conversion types
    ///     Use ConverterParameter to specify type: "Inverse", "Nullable", "PositiveInt", "ProgressBar", "UnwatchedIndicator",
    ///     etc.
    /// </summary>
    public class VisibilityConverter : IValueConverter
    {
        public string ConversionType { get; set; } = "Boolean";

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var converterType = parameter as string ?? ConversionType;

            switch (converterType)
            {
                case "Boolean":
                    // Standard BooleanToVisibilityConverter
                    return value is bool b && b
                        ? Windows.UI.Xaml.Visibility.Visible
                        : Windows.UI.Xaml.Visibility.Collapsed;

                case "Inverse":
                    // InverseBooleanToVisibilityConverter
                    return value is bool inverse && !inverse
                        ? Windows.UI.Xaml.Visibility.Visible
                        : Windows.UI.Xaml.Visibility.Collapsed;

                case "Nullable":
                    // NullableToVisibilityConverter
                    if (value == null)
                    {
                        return Windows.UI.Xaml.Visibility.Collapsed;
                    }

                    if (value is string str && string.IsNullOrEmpty(str))
                    {
                        return Windows.UI.Xaml.Visibility.Collapsed;
                    }

                    return Windows.UI.Xaml.Visibility.Visible;

                case "PositiveInt":
                    // PositiveIntToVisibilityConverter
                    if (value is int intValue)
                    {
                        return intValue > 0 ? Windows.UI.Xaml.Visibility.Visible : Windows.UI.Xaml.Visibility.Collapsed;
                    }

                    if (value is long longValue)
                    {
                        return longValue > 0
                            ? Windows.UI.Xaml.Visibility.Visible
                            : Windows.UI.Xaml.Visibility.Collapsed;
                    }

                    return Windows.UI.Xaml.Visibility.Collapsed;

                case "ProgressBar":
                    // ProgressBarVisibilityConverter - only show for partially watched (not 0% or 100%)
                    if (value is double playedPercentageValue)
                    {
                        return playedPercentageValue > 0 && playedPercentageValue < 100
                            ? Windows.UI.Xaml.Visibility.Visible
                            : Windows.UI.Xaml.Visibility.Collapsed;
                    }

                    return Windows.UI.Xaml.Visibility.Collapsed;

                case "UnwatchedIndicator":
                    // UnwatchedIndicatorVisibilityConverter
                    if (value is int unplayedCountValue)
                    {
                        return unplayedCountValue > 0
                            ? Windows.UI.Xaml.Visibility.Visible
                            : Windows.UI.Xaml.Visibility.Collapsed;
                    }

                    return Windows.UI.Xaml.Visibility.Collapsed;

                case "FirstItem":
                    // FirstItemToVisibilityConverter - for showing dividers between items
                    if (value is int index)
                    {
                        return index == 0 ? Windows.UI.Xaml.Visibility.Collapsed : Windows.UI.Xaml.Visibility.Visible;
                    }

                    return Windows.UI.Xaml.Visibility.Collapsed;

                case "UnwatchedVideo":
                    // UnwatchedVideoToVisibilityConverter
                    if (value is BaseItemDto item &&
                        (item.Type == BaseItemDto_Type.Movie || item.Type == BaseItemDto_Type.Episode) &&
                        item.UserData != null && item.UserData.Played != true)
                    {
                        return Windows.UI.Xaml.Visibility.Visible;
                    }

                    return Windows.UI.Xaml.Visibility.Collapsed;

                case "EmptyString":
                    // EmptyStringToVisibilityConverter - shows element if string is not empty
                    if (value is string stringValue && !string.IsNullOrEmpty(stringValue))
                    {
                        return Windows.UI.Xaml.Visibility.Visible;
                    }

                    return Windows.UI.Xaml.Visibility.Collapsed;

                default:
                    return Windows.UI.Xaml.Visibility.Collapsed;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            var converterType = parameter as string ?? ConversionType;

            if (converterType == "Boolean" || converterType == "Inverse")
            {
                // Safely check if value is Visibility type
                if (value is Windows.UI.Xaml.Visibility visibility)
                {
                    var isVisible = visibility == Windows.UI.Xaml.Visibility.Visible;
                    return converterType == "Inverse" ? !isVisible : isVisible;
                }

                // Return default value if not a Visibility type
                return converterType == "Inverse" ? true : false;
            }

            throw new NotImplementedException($"ConvertBack not implemented for type: {converterType}");
        }
    }
}
