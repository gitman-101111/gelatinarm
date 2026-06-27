using System;
using System.Globalization;
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
            var format = time.TotalHours >= 1
                ? MediaPlayerConstants.TimeFormatHours
                : MediaPlayerConstants.TimeFormatMinutes;

            try
            {
                return time.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return time.ToString("c", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "0:00";
            }
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
                    ? string.Format(MediaPlayerConstants.TimeFormatHoursDisplay, hours, minutes)
                    : string.Format(MediaPlayerConstants.TimeFormatHoursOnly, hours);
            }

            return string.Format(MediaPlayerConstants.TimeFormatMinutesOnly, (int)duration.TotalMinutes);
        }
    }
}
