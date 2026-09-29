using System;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Shared.Ui
{
    /// <summary>
    ///     Tile text for an item, by ConversionType (or ConverterParameter): "Media", "ContinueWatching",
    ///     "RecentlyAdded" or "RecentlyAddedTitle". "Rating" formats a community score instead, and
    ///     "CriticRating" a critic score, which Jellyfin keeps as a percentage.
    /// </summary>
    public class MediaTextConverter : OneWayConverter
    {
        public string ConversionType { get; set; } = "Media";

        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is BaseItemDto item)
            {
                switch (parameter as string ?? ConversionType)
                {
                    case "Media":
                        if (item.Type == BaseItemDto_Type.Episode && !string.IsNullOrEmpty(item.SeriesName))
                        {
                            return item.SeriesName;
                        }

                        if (item.Type == BaseItemDto_Type.Movie && item.ProductionYear.HasValue)
                        {
                            return item.ProductionYear.Value.ToString();
                        }

                        if (item.Type == BaseItemDto_Type.Series)
                        {
                            if (item.ProductionYear.HasValue)
                            {
                                return SeriesYears(item.ProductionYear.Value, item);
                            }

                            if (item.ChildCount > 0)
                            {
                                return item.ChildCount == 1 ? "1 Season" : $"{item.ChildCount} Seasons";
                            }
                        }

                        break;

                    case "ContinueWatching":
                        if (item.Type == BaseItemDto_Type.Movie)
                        {
                            return item.ProductionYear?.ToString() ?? "Movie";
                        }

                        if (item.Type == BaseItemDto_Type.Episode && !string.IsNullOrEmpty(item.SeriesName))
                        {
                            return item.SeriesName;
                        }

                        break;

                    case "RecentlyAdded":
                        // A series here is the server grouping its new episodes: ChildCount is how many
                        if (item.Type == BaseItemDto_Type.Series && item.ChildCount > 0)
                        {
                            return item.ChildCount == 1 ? "1 New Episode" : $"{item.ChildCount} New Episodes";
                        }

                        if (item.Type == BaseItemDto_Type.Episode || item.Type == BaseItemDto_Type.Season)
                        {
                            return item.Name;
                        }

                        if (item.Type == BaseItemDto_Type.Movie && item.ProductionYear.HasValue)
                        {
                            return item.ProductionYear.Value.ToString();
                        }

                        break;

                    case "RecentlyAddedTitle":
                        if (item.Type == BaseItemDto_Type.Season || item.Type == BaseItemDto_Type.Episode)
                        {
                            return item.SeriesName ?? item.Name;
                        }

                        return item.Name;
                }
            }

            if (value is float rating)
            {
                switch (parameter as string)
                {
                    case "Rating":
                        return rating.ToString("F1");
                    case "CriticRating":
                        return $"{rating:0}%";
                }
            }

            return string.Empty;
        }

        // As Jellyfin shows it: the server's status says whether a series still runs, its end date
        // when it stopped
        private static string SeriesYears(int startYear, BaseItemDto series)
        {
            if (series.Status == "Continuing")
            {
                return $"{startYear}-Present";
            }

            var endYear = series.EndDate?.Year;
            return endYear.HasValue && endYear.Value != startYear
                ? $"{startYear}-{endYear.Value}"
                : startYear.ToString();
        }
    }
}
