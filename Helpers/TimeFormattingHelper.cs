using System;
using Gelatinarm.Constants;

namespace Gelatinarm.Helpers
{
    public static class TimeFormattingHelper
    {
        /// <summary>
        ///     Formats a TimeSpan for display as position/duration (e.g., "1:23:45" or "45:23")
        /// </summary>
        public static string FormatTime(TimeSpan time)
        {
            if (time.TotalHours >= 1)
            {
                return time.ToString(MediaPlayerConstants.TIME_FORMAT_HOURS);
            }

            return time.ToString(MediaPlayerConstants.TIME_FORMAT_MINUTES);
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
                    ? string.Format(MediaPlayerConstants.TIME_FORMAT_HOURS_DISPLAY, hours, minutes)
                    : string.Format(MediaPlayerConstants.TIME_FORMAT_HOURS_ONLY, hours);
            }

            return string.Format(MediaPlayerConstants.TIME_FORMAT_MINUTES_ONLY, (int)duration.TotalMinutes);
        }

    }
}
