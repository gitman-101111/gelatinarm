using System;

namespace Gelatinarm.Shared.Ui
{
    /// <summary>
    ///     Dims watched content: the value is an item's UserData.Played
    /// </summary>
    public class PlayedToOpacityConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is true ? 0.6 : 1.0;
        }
    }
}
