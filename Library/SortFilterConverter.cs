using System;
using Gelatinarm.Shared.Ui;

namespace Gelatinarm.Library
{
    /// <summary>
    ///     The sort-order button's label or glyph: ConverterParameter "Text" or "Icon"
    /// </summary>
    public class SortFilterConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            if (parameter is string converterType)
            {
                switch (converterType)
                {
                    case "Text":
                        if (value is bool isAscendingText)
                        {
                            return isAscendingText ? "Ascending" : "Descending";
                        }

                        break;

                    case "Icon":
                        if (value is bool isAscendingIcon)
                        {
                            return isAscendingIcon ? "\uE70E" : "\uE70D"; // Up/Down arrow glyphs
                        }

                        break;
                }
            }

            return string.Empty;
        }
    }
}
