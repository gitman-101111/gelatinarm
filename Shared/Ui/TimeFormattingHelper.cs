using System;
using System.Globalization;

namespace Gelatinarm.Shared.Ui
{
    public static class TimeFormattingHelper
    {
        /// <summary>
        ///     Formats a TimeSpan for display as position/duration (e.g., "1:23:45" or "45:23")
        /// </summary>
        public static string FormatTime(TimeSpan time)
        {
            var format = time.TotalHours >= 1
                ? UiConstants.TimeFormatHours
                : UiConstants.TimeFormatMinutes;

            return time.ToString(format, CultureInfo.InvariantCulture);
        }

        /// <summary>
        ///     Formats a TimeSpan for display as duration text (e.g., "1h 30m" or "45m")
        /// </summary>
        public static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                var hours = (int)duration.TotalHours;
                var minutes = duration.Minutes;
                return minutes > 0
                    ? string.Format(UiConstants.TimeFormatHoursDisplay, hours, minutes)
                    : string.Format(UiConstants.TimeFormatHoursOnly, hours);
            }

            return string.Format(UiConstants.TimeFormatMinutesOnly, (int)duration.TotalMinutes);
        }
    }
}
