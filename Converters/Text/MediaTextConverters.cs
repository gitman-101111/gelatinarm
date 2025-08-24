using System;
using Jellyfin.Sdk.Generated.Models;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Text
{
    /// <summary>
    ///     Consolidated converter for media text display (subtitles, continue watching, etc.)
    ///     Use ConverterParameter to specify the type: "Media", "ContinueWatching", "RecentlyAdded"
    /// </summary>
    public class MediaTextConverter : IValueConverter
    {
        public string ConversionType { get; set; } = "Media";

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is BaseItemDto item)
            {
                // Use parameter if provided, otherwise use the ConversionType property
                var converterType = parameter as string ?? ConversionType;

                switch (converterType)
                {
                    case "Media":
                        // MediaSubtitleConverter logic
                        if (item.Type == BaseItemDto_Type.Episode)
                        {
                            if (!string.IsNullOrEmpty(item.SeriesName))
                            {
                                return item.SeriesName;
                            }
                        }
                        else if (item.Type == BaseItemDto_Type.Movie && item.ProductionYear.HasValue)
                        {
                            return item.ProductionYear.Value.ToString();
                        }
                        else if (item.Type == BaseItemDto_Type.Series)
                        {
                            // Show year range for TV series
                            if (item.ProductionYear.HasValue)
                            {
                                var startYear = item.ProductionYear.Value;
                                var currentYear = DateTime.Now.Year;

                                // If EndDate is available and show has ended
                                if (item.EndDate.HasValue)
                                {
                                    var endYear = item.EndDate.Value.Year;

                                    // If start and end years are the same, just show one year
                                    if (startYear == endYear)
                                    {
                                        return startYear.ToString();
                                    }
                                    // If end year is current year or later, show "Present"

                                    if (endYear >= currentYear)
                                    {
                                        return $"{startYear}-Present";
                                    }

                                    return $"{startYear}-{endYear}";
                                }
                                // If PremiereDate indicates show is still running (Status might not be reliable)

                                if ((item.Status != null && item.Status == "Continuing") ||
                                    (item.PremiereDate.HasValue && !item.EndDate.HasValue))
                                {
                                    // If the show started this year, just show the year
                                    if (startYear == currentYear)
                                    {
                                        return startYear.ToString();
                                    }

                                    return $"{startYear}-Present";
                                }
                                return startYear.ToString();
                            }
                            // Fallback to season count if no year info

                            if (item.ChildCount.HasValue && item.ChildCount.Value > 0)
                            {
                                return item.ChildCount.Value == 1 ? "1 Season" : $"{item.ChildCount.Value} Seasons";
                            }
                        }

                        break;

                    case "ContinueWatching":
                        // ContinueWatchingSubtitleConverter logic
                        // Commented out to reduce log spam
#if DEBUG
#endif

                        if (item.Type == BaseItemDto_Type.Movie)
                        {
#if DEBUG
#endif
                            if (item.ProductionYear.HasValue)
                            {
                                var year = item.ProductionYear.Value.ToString();
#if DEBUG
#endif
                                return year;
                            }
#if DEBUG
#endif
                            return "Movie"; // Return "Movie" instead of empty string
                        }

                        if (item.Type == BaseItemDto_Type.Episode && !string.IsNullOrEmpty(item.SeriesName))
                        {
#if DEBUG
#endif
                            return item.SeriesName;
                        }

                        break;

                    case "RecentlyAdded":
                        // RecentlyAddedSubtitleConverter logic

                        // Check if this is a grouped episode item
                        if (item.Type == BaseItemDto_Type.Series &&
                            !string.IsNullOrEmpty(item.Overview) &&
                            item.Overview.StartsWith("__RecentlyAddedGrouped__"))
                        {
                            // Extract episode count from the overview
                            var parts = item.Overview.Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1 && int.TryParse(parts[1], out var episodeCount))
                            {
                                return episodeCount == 1 ? "1 New Episode" : $"{episodeCount} New Episodes";
                            }
                        }

                        if (item.Type == BaseItemDto_Type.Episode)
                        {
                            // For episodes, show the episode name as subtitle
                            return item.Name;
                        }

                        if (item.Type == BaseItemDto_Type.Season)
                        {
                            // For seasons, show "Season X" as subtitle
                            return item.Name; // This typically contains "Season X"
                        }

                        if (item.Type == BaseItemDto_Type.Movie && item.ProductionYear.HasValue)
                        {
                            return item.ProductionYear.Value.ToString();
                        }

                        if (item.Type == BaseItemDto_Type.Series)
                        {
                            // For regular series (not grouped episodes), show number of seasons
                            if (item.ChildCount.HasValue && item.ChildCount.Value > 0)
                            {
                                return item.ChildCount.Value == 1 ? "1 Season" : $"{item.ChildCount.Value} Seasons";
                            }
                        }

                        break;

                    case "RecentlyAddedTitle":
                        // RecentlyAddedTitleConverter logic - shows proper title for each type
                        if (item.Type == BaseItemDto_Type.Season)
                        {
                            // For seasons, show the series name
                            return item.SeriesName ?? item.Name;
                        }

                        if (item.Type == BaseItemDto_Type.Episode)
                        {
                            // For episodes, show the series name
                            return item.SeriesName ?? item.Name;
                        }

                        // For movies and series, show regular name
                        return item.Name;

                    case "Rating":
                        // RatingToStringConverter logic - formats rating to one decimal place
                        if (value is float floatRating)
                        {
                            return floatRating.ToString("F1");
                        }

                        if (value is double doubleRating)
                        {
                            return doubleRating.ToString("F1");
                        }

                        break;
                }
            }

            // Handle direct float/double values for Rating converter
            if (parameter as string == "Rating")
            {
                if (value is float floatRating)
                {
                    return floatRating.ToString("F1");
                }

                if (value is double doubleRating)
                {
                    return doubleRating.ToString("F1");
                }
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
