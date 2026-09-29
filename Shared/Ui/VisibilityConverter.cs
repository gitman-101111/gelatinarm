using System;

namespace Gelatinarm.Shared.Ui
{
    /// <summary>
    ///     Set ConversionType (or pass ConverterParameter) to "Boolean", "Inverse", "Nullable" (visible
    ///     for a value that is not null or an empty string) or "ProgressBar".
    /// </summary>
    public class VisibilityConverter : OneWayConverter
    {
        // Windows.UI.Xaml.Visibility is spelled out below: this namespace is itself named Visibility
        public string ConversionType { get; set; } = "Boolean";

        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            switch (parameter as string ?? ConversionType)
            {
                case "Boolean":
                    return value is bool b && b
                        ? Windows.UI.Xaml.Visibility.Visible
                        : Windows.UI.Xaml.Visibility.Collapsed;

                case "Inverse":
                    return value is bool inverse && !inverse
                        ? Windows.UI.Xaml.Visibility.Visible
                        : Windows.UI.Xaml.Visibility.Collapsed;

                case "Nullable":
                    if (value == null || (value is string str && string.IsNullOrEmpty(str)))
                    {
                        return Windows.UI.Xaml.Visibility.Collapsed;
                    }

                    return Windows.UI.Xaml.Visibility.Visible;

                case "ProgressBar":
                    // Partly watched only: nothing at 0% or 100%
                    if (value is double playedPercentageValue)
                    {
                        return playedPercentageValue > 0 && playedPercentageValue < 100
                            ? Windows.UI.Xaml.Visibility.Visible
                            : Windows.UI.Xaml.Visibility.Collapsed;
                    }

                    return Windows.UI.Xaml.Visibility.Collapsed;

                default:
                    return Windows.UI.Xaml.Visibility.Collapsed;
            }
        }
    }
}
