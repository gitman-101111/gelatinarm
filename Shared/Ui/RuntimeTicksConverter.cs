using System;

namespace Gelatinarm.Shared.Ui
{
    /// <summary>
    ///     A track's length from its runtime ticks ("3:45")
    /// </summary>
    public class RuntimeTicksConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is long ticks && ticks > 0
                ? TimeFormattingHelper.FormatTime(TimeSpan.FromTicks(ticks))
                : string.Empty;
        }
    }
}
