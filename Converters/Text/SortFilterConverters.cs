using System;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Text
{
    /// <summary>
    ///     Consolidated converter for sort and filter UI elements
    ///     Use ConverterParameter: "Text", "Icon", or "FilterCount"
    /// </summary>
    public class SortFilterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (parameter is string converterType)
            {
                switch (converterType)
                {
                    case "Text":
                        // SortOrderToTextConverter logic
                        if (value is bool isAscendingText)
                        {
                            return isAscendingText ? "Ascending" : "Descending";
                        }

                        break;

                    case "Icon":
                        // SortOrderToIconConverter logic
                        if (value is bool isAscendingIcon)
                        {
                            return isAscendingIcon ? "\uE70E" : "\uE70D"; // Up/Down arrow glyphs
                        }

                        break;

                    case "FilterCount":
                        // FilterCountToTextConverter logic
                        if (value is int count && count > 0)
                        {
                            return $"({count})";
                        }

                        break;
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
